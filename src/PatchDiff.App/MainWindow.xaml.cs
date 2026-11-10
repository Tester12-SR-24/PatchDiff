using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using PatchDiff.Models;
using Application = System.Windows.Application;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxButton = System.Windows.MessageBoxButton;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using HttpClient = System.Net.Http.HttpClient;
using PatchDiff.Services;
using PatchDiff.Views;

namespace PatchDiff;

public partial class MainWindow : Window
{
    public static readonly RoutedCommand ReloadAllCommand = new("ReloadAll", typeof(MainWindow));

    const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValueName = "PatchDiff";

    readonly WatchManager _mgr = WatchManager.Instance;
    readonly ObservableCollection<string> _logLines = new();
    readonly Dictionary<string, AssemblyDiffResult> _lastDiffs = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, AssemblySnapshot> _baselineCache = new(StringComparer.OrdinalIgnoreCase);
    CancellationTokenSource? _searchCts;

    System.Windows.Forms.NotifyIcon? _trayIcon;
    System.Windows.Forms.ToolStripMenuItem? _trayAutoStartItem;
    bool _reallyExiting;
    bool _trayHintShown;

    StoredSettings _settings = new();

    public MainWindow()
    {
        InitializeComponent();
        WatchedList.ItemsSource = _mgr.Watched;
        LogList.ItemsSource = _logLines;
        TimelineList.ItemsSource = Timeline;
        WatchManagerHookup();
        LoadSettings();
        InitTray();
        OpenOverviewTab();
        SystemEvents.SessionEnding += (_, _) => SaveAll();
        AutoStartItem.IsChecked = IsAutoStartEnabled();
        Loaded += (_, _) =>
        {
            ((App)Application.Current).MainWindow = this;
            DarkTitleBar.Enable(this);
        };
        UpdateFooter();
    }

    public ObservableCollection<TimelineEvent> Timeline { get; } = new();

    void WatchManagerHookup()
    {
        _mgr.Logged += message => Dispatcher.BeginInvoke(() => AddLog(message));

        _mgr.DiffProduced += (w, diff) => Dispatcher.BeginInvoke(() =>
        {
            _lastDiffs[w.Path] = diff;
            _baselineCache.Remove(w.Path);
            AddEvent("patched", "GAME PATCHED",
                $"{w.Name} rewritten on disk → auto-diff completed ({diff.Added + diff.Removed + diff.Modified} type changes) → report saved");
            OpenDiffTab(w, diff);
            UpdateFooter();
            if (_settings.BalloonOnPatch)
            {
                var title = "DLL updated";
                _trayIcon?.ShowBalloonTip(4000, title,
                    $"{w.Name}: +{diff.Added} added, −{diff.Removed} removed, ~{diff.Modified} modified",
                    System.Windows.Forms.ToolTipIcon.Info);
            }
        });

        _mgr.BaselineCreated += (w, _) => Dispatcher.BeginInvoke(() =>
            AddEvent("baseline", "BASELINE",
                $"{w.Name} added to watch — baseline captured ({w.Meta1})"));

        _mgr.Watched.CollectionChanged += (_, _) => { UpdateFooter(); SaveAll(); };
    }

    void UpdateFooter()
    {
        // Show the data location with the user profile shortened, so screenshots
        // and stream captures never leak the Windows account name.
        FooterText.Text =
            $"{_mgr.Watched.Count} watched · {Timeline.Count} timeline events · snapshots & reports: {DisplayPath(SnapshotStore.DataRoot)}";
    }

    internal static string DisplayPath(string path)
    {
        try
        {
            path = path.Replace("/", System.IO.Path.DirectorySeparatorChar.ToString());
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(profile) &&
                path.StartsWith(profile, StringComparison.OrdinalIgnoreCase))
                return "~" + path[profile.Length..].Replace('\\', '/');
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(local) &&
                path.StartsWith(local, StringComparison.OrdinalIgnoreCase))
                return "%LOCALAPPDATA%" + path[local.Length..];
        }
        catch { /* display only — fall back to the raw path */ }
        return path;
    }

    void AddEvent(string kind, string badge, string message)
    {
        Timeline.Insert(0, new TimelineEvent
        {
            When = DateTime.Now.ToString("ddd HH:mm"),
            Kind = kind,
            Badge = badge,
            Message = message,
        });
        while (Timeline.Count > 250)
            Timeline.RemoveAt(Timeline.Count - 1);
        UpdateFooter();
    }

    void AddLog(string line)
    {
        _logLines.Add(line);
        while (_logLines.Count > 500)
            _logLines.RemoveAt(0);
        if (_logLines.Count > 0)
            LogList.ScrollIntoView(_logLines[^1]);
    }

    // ---------------- tabs ----------------

    sealed class TabModel
    {
        public bool IsClosable { get; set; } = true;
        public string Header { get; set; } = "";
        public object Content { get; set; } = "";
        public ICommand CloseCommand { get; }
        public MainWindow Owner { get; init; } = null!;
        public TabModel(MainWindow owner)
        {
            Owner = owner;
            CloseCommand = new RelayCommand(_ => Owner.CloseTab(this));
        }
    }

    sealed class RelayCommand : ICommand
    {
        readonly Action<object?> _execute;
        public RelayCommand(Action<object?> execute) => _execute = execute;
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => _execute(parameter);
    }

    /// <summary>Icon + text content for dynamically created buttons (onboarding cards).</summary>
    static object IconContent(string iconKey, string text)
    {
        var app = Application.Current;
        var glyph = app?.TryFindResource(iconKey) as System.Windows.Media.Geometry;
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        if (glyph is not null)
        {
            var path = new System.Windows.Shapes.Path
            {
                Data = glyph,
                StrokeThickness = 1.8,
                StrokeStartLineCap = System.Windows.Media.PenLineCap.Round,
                StrokeEndLineCap = System.Windows.Media.PenLineCap.Round,
                StrokeLineJoin = System.Windows.Media.PenLineJoin.Round,
                Fill = null,
                Width = 15,
                Height = 15,
                Stretch = System.Windows.Media.Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
            };
            // inherit the button's Foreground (incl. accent/hover states)
            path.SetBinding(System.Windows.Shapes.Shape.StrokeProperty,
                new System.Windows.Data.Binding(nameof(Control.Foreground)) { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(Control), 1) });
            row.Children.Add(path);
        }
        row.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    void AddTab(string header, object content, bool closable = true)
    {
        // focus existing tab with the same header
        foreach (var item in WorkTabs.Items.OfType<TabModel>())
        {
            if (item.Header == header)
            {
                WorkTabs.SelectedItem = item;
                return;
            }
        }
        var model = new TabModel(this) { Header = header, Content = content, IsClosable = closable };
        WorkTabs.Items.Add(model);
        WorkTabs.SelectedItem = model;
    }

    void CloseTab(TabModel tab)
    {
        if (!ReferenceEquals(tab.Content, WorkTabs.SelectedItem))
            WorkTabs.SelectedItem = tab;
        WorkTabs.Items.Remove(tab);
        if (WorkTabs.Items.Count == 0)
            OpenOverviewTab();
    }

    void OpenOverviewTab()
    {
        // replace the existing Overview tab if present (keeps tab position)
        var existing = WorkTabs.Items.OfType<TabModel>().FirstOrDefault(t => t.Header == "Overview");
        var panel = BuildOverviewPanel();
        if (existing is not null)
        {
            existing.Content = panel;
            WorkTabs.SelectedItem = existing;
        }
        else
        {
            var model = new TabModel(this) { Header = "Overview", Content = panel, IsClosable = false };
            WorkTabs.Items.Insert(0, model);
            WorkTabs.SelectedItem = model;
        }
    }

    UIElement BuildOverviewPanel()
    {
        var panel = new StackPanel
        {
            Margin = new Thickness(28, 22, 28, 12),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        panel.Children.Add(new TextBlock
        {
            Text = "Welcome to PatchDiff",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8),
        });
        panel.Children.Add(new TextBlock
        {
            Text = "PatchDiff snapshots a game's decompiled code and shows you exactly what a patch changed — types added, removed or modified, constants buffed or nerfed.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = System.Windows.Media.Brushes.LightGray,
            MaxWidth = 560,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 16),
        });

        var buttons = new WrapPanel { Orientation = Orientation.Horizontal };
        var watchReal = new Button
        {
            Content = "Watch a real DLL…",
            Padding = new Thickness(10, 5, 10, 5),
            Style = (Style)FindResource("AccentButton"),
        };
        IconGlyph.SetGeometry(watchReal, (System.Windows.Media.Geometry?)FindResource("IconAdd"));
        watchReal.Click += (_, _) => OnAddDll(this, new RoutedEventArgs());
        buttons.Children.Add(watchReal);
        var watchFolder = new Button
        {
            Content = "Watch a whole folder…",
            Padding = new Thickness(10, 5, 10, 5),
        };
        IconGlyph.SetGeometry(watchFolder, (System.Windows.Media.Geometry?)FindResource("IconFolder"));
        watchFolder.Click += (_, _) => OnAddFolder(this, new RoutedEventArgs());
        buttons.Children.Add(watchFolder);
        var compareBtn = new Button
        {
            Content = "Compare two DLLs…",
            Padding = new Thickness(10, 5, 10, 5),
        };
        IconGlyph.SetGeometry(compareBtn, (System.Windows.Media.Geometry?)FindResource("IconCompare"));
        compareBtn.Click += (_, _) => OnCompareTwo(this, new RoutedEventArgs());
        buttons.Children.Add(compareBtn);
        panel.Children.Add(buttons);



        panel.Children.Add(new TextBlock
        {
            Text = @"Unity tip:  <game>_Data\Managed\Assembly-CSharp.dll",
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11.5,
            Foreground = System.Windows.Media.Brushes.Gray,
            Margin = new Thickness(0, 6, 0, 0),
        });
        return panel;
    }

    void OpenDiffTab(WatchedDll w, AssemblyDiffResult diff)
    {
        AddTab($"Diff — {w.Name}", new DiffView(diff));
    }

    void OpenBrowseTab(WatchedDll w)
    {
        var snap = SnapshotStore.Load(w.Path);
        if (snap is null)
        {
            AddLog($"No snapshot yet for {w.Name} — wait for the first analysis to finish.");
            return;
        }
        AddTab($"Browse — {w.Name}", new BrowseView(snap, w));
    }

    // ---------------- demo session (never persisted) ----------------




    void OpenHistoryTab(WatchedDll w)
    {
        var view = new HistoryView(w);
        view.DiffRequested += diff => Dispatcher.BeginInvoke(() =>
            AddTab($"Diff — {w.Name} (history)", new DiffView(diff)));
        view.MatrixRequested += watch => Dispatcher.BeginInvoke(() => OpenMatrixTab(watch));
        AddTab($"History — {w.Name}", view);
    }

    void OpenMatrixTab(WatchedDll w)
    {
        var store = new RevisionStore();
        var revs = new List<AssemblySnapshot>();
        foreach (var r in store.List(w.Path))
        {
            var snap = store.LoadRevision(w.Path, r.Sha256);
            if (snap is not null)
                revs.Add(snap);
        }
        if (revs.Count < 2)
        {
            AddLog($"Revision matrix needs at least two archived revisions — {w.Name} has {revs.Count}.");
            return;
        }
        var view = new MatrixView(w, revs);
        view.DiffRequested += (diff, label) => Dispatcher.BeginInvoke(() =>
            AddTab($"Diff — {w.Name} ({label})", new DiffView(diff)));
        AddTab($"Matrix — {w.Name}", view);
    }

    // ---------------- rail actions ----------------

    WatchedDll? SelectedWatch => WatchedList.SelectedItem as WatchedDll;

    void OnOpenDiff(object sender, RoutedEventArgs e)
    {
        var w = SelectedWatch;
        if (w is null) { AddLog("Select an assembly in the rail first."); return; }
        if (_lastDiffs.TryGetValue(w.Path, out var diff) && diff.HasChanges)
            OpenDiffTab(w, diff);
        else
            AddLog($"No recorded changes for {w.Name} yet — waiting for the next update.");
    }

    void OnBrowse(object sender, RoutedEventArgs e)
    {
        var w = SelectedWatch;
        if (w is null) { AddLog("Select an assembly in the rail first."); return; }
        OpenBrowseTab(w);
    }

    void OnHistory(object sender, RoutedEventArgs e)
    {
        var w = SelectedWatch;
        if (w is null) { AddLog("Select an assembly in the rail first."); return; }
        OpenHistoryTab(w);
    }


    void OnExportSources(object sender, RoutedEventArgs e)
    {
        var w = SelectedWatch;
        if (w is null) { AddLog("Select an assembly in the rail first."); return; }
        var snap = SnapshotStore.Load(w.Path);
        if (snap is null) { AddLog($"No snapshot yet for {w.Name}."); return; }
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = $"Export decompiled sources for {w.Name}" };
        if (dlg.ShowDialog(this) != true)
            return;
        var dir = Path.Combine(dlg.FolderName,
            $"{Path.GetFileNameWithoutExtension(w.Name)}_{snap.Sha256[..8]}_sources");
        Directory.CreateDirectory(dir);
        foreach (var t in snap.Types)
        {
            var safe = string.Concat(t.FullName.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_'));
            File.WriteAllText(Path.Combine(dir, safe + ".cs"), t.Source);
        }
        AddLog($"Exported {snap.Types.Count} sources for {w.Name} → {dir}");
        Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
    }

    void OnOpenReport(object sender, RoutedEventArgs e)
    {
        var w = SelectedWatch;
        if (w is null) { AddLog("Select an assembly in the rail first."); return; }
        string? report = File.Exists(w.LastReportPath) ? w.LastReportPath : null;
        if (report is null && _lastDiffs.TryGetValue(w.Path, out var diff) && diff.HasChanges)
            report = SnapshotStore.WriteHtmlReport(diff);
        if (report is null) { AddLog($"No report yet for {w.Name}."); return; }
        Process.Start(new ProcessStartInfo(report) { UseShellExecute = true });
    }

    void OnWatchSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Assembly actions are meaningless without a selection — keep them visibly disabled.
        AssemblyActions.IsEnabled = SelectedWatch is not null;
    }

    void OnWatchDoubleClicked(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is WatchedDll w)
            OpenBrowseTab(w);
    }

    public AssemblyDiffResult? GetLastDiffFor(string path) =>
        _lastDiffs.TryGetValue(path, out var d) ? d : null;

    public static string ExportFileName(string fullName) =>
        string.Concat(fullName.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_')) + ".cs";

    // ---------------- watch management ----------------

    void OnClearTimeline(object sender, RoutedEventArgs e)
    {
        Timeline.Clear();
        UpdateFooter();
        AddLog("Timeline cleared.");
    }

    void OnAddDll(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Add DLL to watch",
            Filter = "Assemblies (*.dll;*.exe)|*.dll;*.exe",
            Multiselect = true,
        };
        if (dlg.ShowDialog(this) != true)
            return;
        foreach (var f in dlg.FileNames)
            _mgr.Add(f);
        SaveAll();
    }

    void OnAddFolder(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Pick any DLL inside the folder (e.g. FooGame_Data\\Managed\\Assembly-CSharp.dll)",
            Filter = "Assemblies (*.dll;*.exe)|*.dll;*.exe",
            ValidateNames = false,
            CheckFileExists = false,
            FileName = "Select this folder",
        };
        if (dlg.ShowDialog(this) != true)
            return;
        var dir = Path.GetDirectoryName(dlg.FileName);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            return;
        int added = 0;
        foreach (var f in Directory.GetFiles(dir, "*.dll"))
        {
            _mgr.Add(f);
            added++;
        }
        AddLog($"Added {added} DLLs from {dir}.");
        SaveAll();
    }

    void OnRemoveWatch(object sender, RoutedEventArgs e)
    {
        var targets = new List<WatchedDll> { SelectedWatch! }.Where(t => t is not null).ToList();
        if (targets.Count == 0)
        {
            AddLog("Select an assembly in the rail first.");
            return;
        }
        RemoveMany(targets);
    }

    void OnRemoveAll(object sender, RoutedEventArgs e) => RemoveMany(_mgr.Watched.ToList());

    void RemoveMany(List<WatchedDll> targets)
    {
        var msg = targets.Count == 1
            ? $"Stop watching {targets[0].Name}?{Environment.NewLine}{Environment.NewLine}Its baseline snapshot will be deleted."
            : $"Stop watching all {targets.Count} DLLs?{Environment.NewLine}{Environment.NewLine}Their baseline snapshots will be deleted.";
        if (MessageBox.Show(this, msg, "PatchDiff", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        foreach (var t in targets)
        {
            _lastDiffs.Remove(t.Path);
            _baselineCache.Remove(t.Path);
            _mgr.Remove(t, purgeData: true);
        }
        SaveAll();
        UpdateFooter();
        AddLog($"Removed {targets.Count} watch{(targets.Count == 1 ? "" : "es")}.");
    }

    void OnRecheck(object sender, RoutedEventArgs e)
    {
        var w = SelectedWatch;
        if (w is null) { AddLog("Select an assembly in the rail first."); return; }
        _mgr.RequestCheck(w);
        AddLog($"Re-checking {w.Name}…");
    }

    void OnReloadAll(object? sender, RoutedEventArgs? e)
    {
        if (_mgr.Watched.Count == 0)
        {
            AddLog("Nothing to reload — no DLLs are being watched.");
            return;
        }
        foreach (var w in _mgr.Watched)
            _mgr.RequestCheck(w);
        AddLog($"Reloading all {_mgr.Watched.Count} watched DLLs…");
    }

    void OnReloadAllExecuted(object? sender, ExecutedRoutedEventArgs? e) => OnReloadAll(sender, e);

    void OnCompareTwo(object sender, RoutedEventArgs e) => _ = CompareTwoFlowAsync();

    async Task CompareTwoFlowAsync()
    {
        var dlg = new OpenFileDialog { Title = "OLD version of the DLL", Filter = "Assemblies (*.dll;*.exe)|*.dll;*.exe" };
        if (dlg.ShowDialog(this) != true)
            return;
        var oldPath = dlg.FileName;
        dlg.Title = "NEW version of the DLL";
        if (dlg.ShowDialog(this) != true)
            return;

        AddLog("Comparing…");
        var diff = await _mgr.CompareTwoAsync(oldPath, dlg.FileName);
        if (diff is null)
            return;
        if (diff.HasChanges)
            AddTab($"Diff — {Path.GetFileName(oldPath)} → {Path.GetFileName(dlg.FileName)}", new DiffView(diff));
        else
            MessageBox.Show(this, "No type changes detected between the two assemblies.", "PatchDiff");
    }

    void OnOpenReports(object? sender, RoutedEventArgs? e)
    {
        Directory.CreateDirectory(SnapshotStore.ReportsRoot);
        Process.Start(new ProcessStartInfo(SnapshotStore.ReportsRoot) { UseShellExecute = true });
    }

    void OnExit(object sender, RoutedEventArgs e) => ExitApp();

    void OnSettings(object sender, RoutedEventArgs e)
    {
        var dlg = new SettingsWindow(_settings, _mgr) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            _settings = dlg.Result;
            ApplySettings();
            SaveAll();
            AddLog("Settings applied.");
        }
    }

    void ApplySettings()
    {
        _mgr.PollInterval = TimeSpan.FromSeconds(Math.Max(2, _settings.PollSeconds));
        _mgr.ArchiveKeep = _settings.ArchiveKeep;
        _mgr.UpdatePollInterval();
    }

    async void OnCheckForUpdates(object sender, RoutedEventArgs e)
    {
        // NEVER block the dispatcher here: sync-over-async (GetAwaiter().GetResult())
        // deadlocks because the updater's continuations need the UI thread we froze.
        // This used to hang the whole app on the first click.
        AddLog("Checking for updates…");
        var updater = new UpdaterService();
        ReleaseInfo? release;
        try
        {
            release = await updater.GetLatestReleaseAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AddLog($"Update check failed: {ex.Message}");
            MessageBox.Show(this, "Could not check for updates." + Environment.NewLine + Environment.NewLine + ex.Message,
                "Check for updates", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (release is null)
        {
            AddLog("Could not reach the releases feed (offline, private repo without credentials, or no releases yet).");
            MessageBox.Show(this,
                "Could not check for updates.\n\nThe releases feed was unreachable (offline, private repository without credentials, or no releases published yet).",
                "Check for updates", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!UpdaterService.IsNewer(release))
        {
            MessageBox.Show(this,
                $"PatchDiff is up to date.\n\nLatest release: {release.Tag} ({release.Name})\n\nYou are running: {UpdaterService.CurrentVersion()}",
                "Check for updates", MessageBoxButton.OK, MessageBoxImage.Information);
            AddLog($"Up to date (latest release: {release.Tag}).");
            return;
        }

        var choice = MessageBox.Show(this,
            $"PatchDiff {release.Tag} is available ({release.Name}).\n\n" +
            $"You are running: {UpdaterService.CurrentVersion()}."+"\n\n" +
            "Download and install it now? PatchDiff will close, update itself, and restart."+"\n\n" +
            "(Snapshots, reports and settings are not touched by the update.)",
            "Update available", MessageBoxButton.YesNo, MessageBoxImage.Question);
        AddLog($"Update available: {release.Tag}.");
        if (choice != MessageBoxResult.Yes)
        {
            Process.Start(new ProcessStartInfo(release.PageUrl) { UseShellExecute = true });
            return;
        }

        string? staging;
        try
        {
            staging = await updater.DownloadAndStageAsync(release,
                msg => Dispatcher.BeginInvoke(() => AddLog(msg))).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AddLog($"Update download failed: {ex.Message}");
            MessageBox.Show(this, "The update could not be downloaded." + Environment.NewLine + Environment.NewLine + ex.Message,
                "Update failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (staging is null)
        {
            AddLog("Update download failed — details in the updater log.");
            MessageBox.Show(this,
                "The update could not be downloaded or extracted.\nDetails were written to the updater log under the data folder.",
                "Update failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        AddLog("Update staged — applying on exit…");
        updater.LaunchApplyAfterExit(staging);
        Application.Current.Shutdown();
    }
    void OnAbout(object sender, RoutedEventArgs e)
        => MessageBox.Show(this,
            $"PatchDiff {UpdaterService.CurrentVersion()}\n\nWatch .NET/Unity DLLs; decompile with the ILSpy engine; diff, report, repeat.\n\nLicensed under the WTFPL.",
            "About PatchDiff", MessageBoxButton.OK, MessageBoxImage.Information);

    // ---------------- drag & drop ----------------

    void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] items)
            return;
        int added = 0;
        foreach (var item in items)
        {
            if (Directory.Exists(item))
            {
                foreach (var f in Directory.GetFiles(item, "*.dll"))
                {
                    _mgr.Add(f);
                    added++;
                }
            }
            else if (File.Exists(item) &&
                     (item.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                      item.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
            {
                _mgr.Add(item);
                added++;
            }
        }
        if (added > 0)
        {
            SaveAll();
            AddLog($"Added {added} DLL{(added == 1 ? "" : "s")} via drag & drop.");
        }
    }

    // ---------------- search ----------------

    async void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        SearchPopup.IsOpen = false;
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;
        var query = SearchBox.Text.Trim();
        if (query.Length < 2)
            return;

        List<SearchResult> results;
        try
        {
            results = await Task.Run(async () =>
            {
                var list = new List<SearchResult>();
                foreach (var w in _mgr.Watched.ToList())
                {
                    ct.ThrowIfCancellationRequested();
                    var snap = await GetBaselineAsync(w, ct);
                    if (snap is null)
                        continue;
                    foreach (var t in snap.Types)
                    {
                        var csFile = ExportFileName(t.FullName);
                        if (t.FullName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                            csFile.Contains(query, StringComparison.OrdinalIgnoreCase))
                        {
                            list.Add(new SearchResult
                            {
                                TypeName = t.FullName,
                                CsFile = csFile,
                                AssemblyName = w.Name,
                                AssemblyPath = w.Path,
                                Source = t.Source,
                            });
                            if (list.Count >= 60)
                                return list;
                        }
                    }
                }
                return list;
            }, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (ct.IsCancellationRequested)
            return;
        // the query may have changed (or shrunk) while the search was running
        if (SearchBox.Text.Trim() != query || query.Length < 2)
            return;
        SearchResults.ItemsSource = results;
        if (results.Count > 0)
        {
            SearchResults.SelectedIndex = 0;
            SearchPopup.IsOpen = true;
        }
    }

    sealed class SearchResult
    {
        public required string TypeName { get; init; }
        public required string CsFile { get; init; }
        public required string AssemblyName { get; init; }
        public required string AssemblyPath { get; init; }
        public required string Source { get; init; }
    }

    async Task<AssemblySnapshot?> GetBaselineAsync(WatchedDll w, CancellationToken ct)
    {
        if (_baselineCache.TryGetValue(w.Path, out var cached))
            return cached;
        var snap = await Task.Run(() => SnapshotStore.Load(w.Path), ct);
        if (snap is not null)
            _baselineCache[w.Path] = snap;
        return snap;
    }

    void OnSearchResultActivated(object sender, MouseButtonEventArgs e) => OpenSelectedSearchResult();

    void OnSearchResultKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OpenSelectedSearchResult();
            e.Handled = true;
        }
    }

    void OpenSelectedSearchResult()
    {
        if (SearchResults.SelectedItem is not SearchResult hit)
            return;
        SearchPopup.IsOpen = false;
        var w = _mgr.Watched.FirstOrDefault(x => string.Equals(x.Path, hit.AssemblyPath, StringComparison.OrdinalIgnoreCase));
        if (w is null)
        {
            AddLog("That assembly is no longer watched.");
            return;
        }
        var snap = GetBaselineAsync(w, CancellationToken.None).GetAwaiter().GetResult();
        var source = snap?.Types.FirstOrDefault(t => t.FullName == hit.TypeName)?.Source ?? hit.Source;
        AddTab(hit.CsFile, new SourcePane(hit.TypeName, source, w.Name));
    }

    // ---------------- persistence ----------------

    void SaveAll()
    {
        _settings.Watched = _mgr.Watched.Select(w => w.Path).ToList();
        SettingsFile.Save(_settings);
        SaveUiState();
    }

    void LoadSettings()
    {
        _settings = SettingsFile.Load();
        ApplySettings();
        // Keep entries whose file is temporarily missing (unmounted drive etc.) so the
        // watch list is never silently truncated — the watch shows MISSING status.
        foreach (var p in _settings.Watched)
        {
            if (!File.Exists(p))
                AddLog($"Watch skipped (file not found): {p}");
            _mgr.Add(p);
        }
        LoadUiState();
    }

    sealed class UiState
    {
        public List<TimelineEvent> Timeline { get; set; } = new();
        public Dictionary<string, AssemblyDiffResult> LastDiffs { get; set; } = new();
        public double? Left { get; set; }
        public double? Top { get; set; }
        public double? Width { get; set; }
        public double? Height { get; set; }
        public string? WindowState { get; set; }
    }

    void SaveUiState()
    {
        try
        {
            File.WriteAllText(Path.Combine(SnapshotStore.DataRoot, "ui-state.json"),
                JsonSerializer.Serialize(new UiState
                {
                    Timeline = Timeline.ToList(),
                    LastDiffs = _lastDiffs,
                    Left = Left,
                    Top = Top,
                    Width = ActualWidth,
                    Height = ActualHeight,
                    WindowState = WindowState.ToString(),
                }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* non-fatal */ }
    }

    void LoadUiState()
    {
        try
        {
            var file = Path.Combine(SnapshotStore.DataRoot, "ui-state.json");
            if (!File.Exists(file))
                return;
            var state = JsonSerializer.Deserialize<UiState>(File.ReadAllText(file));
            if (state is null)
                return;
            foreach (var evt in state.Timeline.Take(250))
                Timeline.Add(evt);
            foreach (var (path, diff) in state.LastDiffs)
                _lastDiffs[path] = diff;
            if (state.Left is double l && state.Top is double t && state.Width is double wd && state.Height is double ht)
            {
                if (Enum.TryParse<WindowState>(state.WindowState, out var ws))
                    WindowState = ws == WindowState.Minimized ? WindowState.Normal : ws;
                if (l >= -100 && t >= -100)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual;
                    Left = l;
                    Top = t;
                }
                Width = wd;
                Height = ht;
            }
            if (state.Timeline.Count > 0)
                AddEvent("info", "RESUMED", "Session restored — watch list re-armed; re-checking for changes made while PatchDiff was closed.");
        }
        catch { /* non-fatal */ }
    }

    // ---------------- tray ----------------

    void InitTray()
    {
        try
        {
            var iconStream = Application.GetResourceStream(new Uri("pack://application:,,,/icon.ico")).Stream;
            var icon = new System.Drawing.Icon(iconStream);

            _trayAutoStartItem = new System.Windows.Forms.ToolStripMenuItem("Start with Windows")
            {
                CheckOnClick = true,
                Checked = AutoStartItem.IsChecked,
            };
            _trayAutoStartItem.CheckedChanged += (_, _) =>
            {
                if (AutoStartItem.IsChecked != _trayAutoStartItem.Checked)
                {
                    AutoStartItem.IsChecked = _trayAutoStartItem.Checked;
                    OnToggleAutoStart(null, null!);
                }
            };

            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("Open PatchDiff", null, (_, _) => ShowFromTray());
            menu.Items.Add("Check all now", null, (_, _) => Dispatcher.BeginInvoke(() => OnReloadAll(null, null!)));
            menu.Items.Add(_trayAutoStartItem);
            menu.Items.Add("Reports folder", null, (_, _) => Dispatcher.BeginInvoke(() => OnOpenReports(null, null!)));
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add("Exit", null, (_, _) => Dispatcher.BeginInvoke(ExitApp));

            _trayIcon = new System.Windows.Forms.NotifyIcon
            {
                Icon = icon,
                Text = "PatchDiff - watching your DLLs",
                Visible = true,
                ContextMenuStrip = menu,
            };
            _trayIcon.DoubleClick += (_, _) => ShowFromTray();
        }
        catch (Exception ex)
        {
            AddLog($"Tray icon unavailable: {ex.Message}");
        }
    }

    public void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Focus();
    }

    void ExitApp()
    {
        _reallyExiting = true;
        Close();
        Application.Current.Shutdown();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_reallyExiting)
        {
            e.Cancel = true;
            Hide();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                _trayIcon?.ShowBalloonTip(3500, "PatchDiff is still running",
                    "PatchDiff keeps watching your DLLs from the tray. Double-click the icon to reopen.",
                    System.Windows.Forms.ToolTipIcon.Info);
            }
            return;
        }
        SaveAll();
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }
        base.OnClosing(e);
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == WindowState.Minimized && !_reallyExiting)
            Hide();
    }

    static bool IsAutoStartEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(RunValueName) is not null;
    }

    void OnToggleAutoStart(object? sender, RoutedEventArgs? e)
    {
        // Sync the tray checkbox, then ALWAYS fall through to the registry write.
        // (An early return here made the File-menu path a no-op: the menu click sets
        // IsChecked, the tray sync ran, and the registry write never happened.)
        if (_trayAutoStartItem is not null && _trayAutoStartItem.Checked != AutoStartItem.IsChecked)
            _trayAutoStartItem.Checked = AutoStartItem.IsChecked;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (AutoStartItem.IsChecked)
                key.SetValue(RunValueName, $"\"{Environment.ProcessPath}\"");
            else
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
            AddLog(AutoStartItem.IsChecked
                ? "PatchDiff will now start automatically with Windows."
                : "Windows auto-start disabled.");
        }
        catch (Exception ex)
        {
            AddLog($"Could not change auto-start: {ex.Message}");
        }
    }
}

public sealed class TimelineEvent
{
    public string When { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Badge { get; init; } = "";
    public string Message { get; init; } = "";
}
