using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Win32;
using PatchDiff.Models;
using PatchDiff.Services;
using PatchDiff.Views;
using System.Windows.Input;
using Application = Avalonia.Application;
using HttpClient = System.Net.Http.HttpClient;

namespace PatchDiff;

public partial class MainWindow : Window
{
    const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValueName = "PatchDiff";

    readonly WatchManager _mgr = WatchManager.Instance;
    readonly ObservableCollection<string> _logLines = new();
    readonly Dictionary<string, AssemblyDiffResult> _lastDiffs = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, AssemblySnapshot> _baselineCache = new(StringComparer.OrdinalIgnoreCase);
    CancellationTokenSource? _searchCts;

    bool _reallyExiting;

    StoredSettings _settings = new();

    public MainWindow()
    {
        InitializeComponent();
        WatchedList.ItemsSource = _mgr.Watched;
        LogList.ItemsSource = _logLines;
        TimelineList.ItemsSource = Timeline;
        WatchManagerHookup();
        LoadSettings();
        ReportUpdateOutcome();
        // InitTray() is handled via Avalonia TrayIcon in App.axaml.cs or platform services
        OpenOverviewTab();
        // SystemEvents.SessionEnding is Windows-specific; handle using Avalonia lifecycle events or P/Invoke if strictly needed.
        AutoStartItem.IsChecked = IsAutoStartEnabled();
        UpdateFooter();
    }

    public ObservableCollection<TimelineEvent> Timeline { get; } = new();

    void ReportUpdateOutcome()
    {
        try
        {
            var current = UpdaterService.CurrentVersion();
            var lastSeenFile = Path.Combine(SnapshotStore.DataRoot, "last-version.txt");
            var previous = File.Exists(lastSeenFile) ? File.ReadAllText(lastSeenFile).Trim() : "";

            var statusFile = UpdaterService.ApplyStatusFile;
            if (File.Exists(statusFile))
            {
                var status = File.ReadAllText(statusFile).Trim();
                File.Delete(statusFile);
                var ok = int.TryParse(status, out var rc) && rc >= 0 && rc < 8;
                if (ok)
                    AddLog($"Update applied — now running {current}. All files in the install folder were replaced (robocopy code {rc}).");
                else if (status == "pending")
                    AddLog("An update was started but its file swap could not be confirmed.");
                else
                    AddLog($"The last update FAILED to replace the install files (robocopy code {status}) — the old build is still running. Close any program locking the PatchDiff folder and try Help ▸ Check for updates again.");
            }
            else if (previous.Length > 0 && previous != current)
            {
                AddLog($"Updated to {current} (was {previous}).");
            }

            File.WriteAllText(lastSeenFile, current);
        }
        catch { /* version bookkeeping must never block startup */ }
    }

    void WatchManagerHookup()
    {
        _mgr.Logged += message => Dispatcher.UIThread.Post(() => AddLog(message));

        _mgr.DiffProduced += (w, diff) => Dispatcher.UIThread.Post(() =>
        {
            _lastDiffs[w.Path] = diff;
            _baselineCache.Remove(w.Path);
            AddEvent("patched", "GAME PATCHED",
                $"{w.Name} rewritten on disk → auto-diff completed ({diff.Added + diff.Removed + diff.Modified} type changes) → report saved");
            OpenDiffTab(w, diff);
            UpdateFooter();
            // TODO: Avalonia Notification (requires INotificationService or TrayIcon Logic)
        });

        _mgr.BaselineCreated += (w, _) => Dispatcher.UIThread.Post(() =>
            AddEvent("baseline", "BASELINE",
                $"{w.Name} added to watch — baseline captured ({w.Meta1})"));

        _mgr.Watched.CollectionChanged += (_, _) => { UpdateFooter(); SaveAll(); };
    }

    void UpdateFooter()
    {
        FooterText.Text =
            $"{_mgr.Watched.Count} watched · {Timeline.Count} timeline events · snapshots & reports: {DisplayPath(SnapshotStore.DataRoot)}";
    }

    internal static string DisplayPath(string path)
    {
        try
        {
            path = path.Replace("/", Path.DirectorySeparatorChar.ToString());
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

    static object IconContent(string iconKey, string text)
    {
        // TODO: Implement IconContent using Avalonia Geometry resources.
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    void AddTab(string header, object content, bool closable = true)
    {
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

    Control BuildOverviewPanel()
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
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 0, 0, 8),
        });
        panel.Children.Add(new TextBlock
        {
            Text = "PatchDiff snapshots a game's decompiled code and shows you exactly what a patch changed — types added, removed or modified, constants buffed or nerfed.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.LightGray,
            MaxWidth = 560,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 16),
        });

        var buttons = new WrapPanel { Orientation = Orientation.Horizontal };
        var watchReal = new Button
        {
            Content = "Watch a real DLL…",
            Padding = new Thickness(10, 5, 10, 5),
        };
        watchReal.Click += (_, _) => OnAddDll(watchReal, new RoutedEventArgs());
        buttons.Children.Add(watchReal);
        var watchFolder = new Button
        {
            Content = "Watch a whole folder…",
            Padding = new Thickness(10, 5, 10, 5),
        };
        watchFolder.Click += (_, _) => OnAddFolder(watchFolder, new RoutedEventArgs());
        buttons.Children.Add(watchFolder);
        var compareBtn = new Button
        {
            Content = "Compare two DLLs…",
            Padding = new Thickness(10, 5, 10, 5),
        };
        compareBtn.Click += (_, _) => OnCompareTwo(compareBtn, new RoutedEventArgs());
        buttons.Children.Add(compareBtn);
        panel.Children.Add(buttons);

        panel.Children.Add(new TextBlock
        {
            Text = @"Unity tip:  <game>_Data\Managed\Assembly-CSharp.dll",
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11.5,
            Foreground = Brushes.Gray,
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

    void OpenHistoryTab(WatchedDll w)
    {
        var view = new HistoryView(w);
        view.DiffRequested += diff => Dispatcher.UIThread.Post(() =>
            AddTab($"Diff — {w.Name} (history)", new DiffView(diff)));
        view.MatrixRequested += watch => Dispatcher.UIThread.Post(() => OpenMatrixTab(watch));
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
        view.DiffRequested += (diff, label) => Dispatcher.UIThread.Post(() =>
            AddTab($"Diff — {w.Name} ({label})", new DiffView(diff)));
        AddTab($"Matrix — {w.Name}", view);
    }

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

    async void OnExportSources(object sender, RoutedEventArgs e)
    {
        var w = SelectedWatch;
        if (w is null) { AddLog("Select an assembly in the rail first."); return; }
        var snap = SnapshotStore.Load(w.Path);
        if (snap is null) { AddLog($"No snapshot yet for {w.Name}."); return; }

        // Avalonia StorageProvider API for folder picker
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null) return;

        var folders = await storage.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
        {
            Title = $"Export decompiled sources for {w.Name}",
            AllowMultiple = false
        });

        if (folders.Count == 0)
            return;

        var dir = Path.Combine(folders[0].Path.LocalPath,
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
        AssemblyActions.IsEnabled = SelectedWatch is not null;
    }

    public AssemblyDiffResult? GetLastDiffFor(string path) =>
        _lastDiffs.TryGetValue(path, out var d) ? d : null;

    public static string ExportFileName(string fullName) =>
        string.Concat(fullName.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_')) + ".cs";

    void OnClearTimeline(object sender, RoutedEventArgs e)
    {
        Timeline.Clear();
        UpdateFooter();
        AddLog("Timeline cleared.");
    }

    async void OnAddDll(object sender, RoutedEventArgs e)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null) return;

        var files = await storage.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "Add DLL to watch",
            AllowMultiple = true,
            FileTypeFilter = new[] { new Avalonia.Platform.Storage.FilePickerFileType("Assemblies") { Patterns = new[] { "*.dll", "*.exe" } } }
        });

        if (files.Count == 0)
            return;
        foreach (var f in files)
            _mgr.Add(f.Path.LocalPath);
        SaveAll();
    }

    async void OnAddFolder(object sender, RoutedEventArgs e)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null) return;

        var folders = await storage.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
        {
            Title = "Pick the folder containing the DLLs",
            AllowMultiple = false
        });

        if (folders.Count == 0)
            return;
        var dir = folders[0].Path.LocalPath;
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

        // TODO: Avalonia MessageBox implementation
        AddLog($"Remove confirmation for {targets.Count} watch(es) requested.");
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

    void OnCompareTwo(object sender, RoutedEventArgs e) => _ = CompareTwoFlowAsync();

    async Task CompareTwoFlowAsync()
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null) return;

        var files = await storage.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "Select TWO versions of the DLL (first = old, second = new)",
            AllowMultiple = true,
            FileTypeFilter = new[] { new Avalonia.Platform.Storage.FilePickerFileType("Assemblies") { Patterns = new[] { "*.dll", "*.exe" } } }
        });

        if (files.Count < 2) return;

        var oldPath = files[0].Path.LocalPath;
        var newPath = files[1].Path.LocalPath;

        AddLog("Comparing…");
        var diff = await _mgr.CompareTwoAsync(oldPath, newPath);
        if (diff is null)
            return;
        if (diff.HasChanges)
            AddTab($"Diff — {Path.GetFileName(oldPath)} → {Path.GetFileName(newPath)}", new DiffView(diff));
        else
            AddLog("No type changes detected between the two assemblies.");
    }

    void OnOpenReports(object? sender, RoutedEventArgs? e)
    {
        Directory.CreateDirectory(SnapshotStore.ReportsRoot);
        Process.Start(new ProcessStartInfo(SnapshotStore.ReportsRoot) { UseShellExecute = true });
    }

    void OnExit(object sender, RoutedEventArgs e) => ExitApp();

    async void OnCheckForUpdates(object sender, RoutedEventArgs e)
    {
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
            return;
        }
        if (release is null)
        {
            AddLog("Could not reach the releases feed (offline, private repo without credentials, or no releases yet).");
            return;
        }

        var newer = UpdaterService.IsNewerOrUnknown(release);
        if (newer == false)
        {
            AddLog($"Up to date — running {UpdaterService.CurrentVersion()} (latest release: {release.Tag}).");
            return;
        }

        AddLog($"Update available: {release.Tag}.");
        string? staging;
        try
        {
            staging = await updater.DownloadAndStageAsync(release,
                msg => Dispatcher.UIThread.Post(() => AddLog(msg))).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AddLog($"Update download failed: {ex.Message}");
            return;
        }
        if (staging is null)
        {
            AddLog("Update download failed — details in the updater log.");
            return;
        }

        AddLog("Update staged — applying on exit…");
        updater.LaunchApplyAfterExit(staging);
        (App.Current.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }

    void OnAbout(object sender, RoutedEventArgs e)
        => AddLog($"PatchDiff {UpdaterService.CurrentVersion()} — Watch .NET/Unity DLLs; decompile with the ILSpy engine; diff, report, repeat.");

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
        public PixelPoint? Position { get; set; }
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
                    Position = new PixelPoint((int)Position.X, (int)Position.Y),
                    Width = Width,
                    Height = Height,
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
            if (state.Position is PixelPoint p && state.Width is double wd && state.Height is double ht)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Position = p;
                Width = wd;
                Height = ht;
            }
            if (state.Timeline.Count > 0)
                AddEvent("info", "RESUMED", "Session restored — watch list re-armed; re-checking for changes made while PatchDiff was closed.");
        }
        catch { /* non-fatal */ }
    }

    static bool IsAutoStartEnabled()
    {
        if (!OperatingSystem.IsWindows())
            return false;
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(RunValueName) is not null;
    }

    void OnToggleAutoStart(object? sender, RoutedEventArgs? e)
    {
        if (!OperatingSystem.IsWindows())
            return;
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

public partial class MainWindow
{
    void ExitApp()
    {
        _reallyExiting = true;
        Close();
        (App.Current.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }

    void InitializeComponent() => Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
}

public partial class MainWindow
{
    void ApplySettings()
    {
        _mgr.PollInterval = TimeSpan.FromSeconds(Math.Max(2, _settings.PollSeconds));
        _mgr.ArchiveKeep = _settings.ArchiveKeep;
        _mgr.UpdatePollInterval();
    }
}
