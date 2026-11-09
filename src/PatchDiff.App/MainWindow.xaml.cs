using System.Net.Http;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PatchDiff.Models;
using PatchDiff.Services;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Clipboard = System.Windows.Clipboard;
using DataFormats = System.Windows.DataFormats;
using DragEventArgs = System.Windows.DragEventArgs;
using MessageBox = System.Windows.MessageBox;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxResult = System.Windows.MessageBoxResult;
using DragDropEffects = System.Windows.DragDropEffects;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using Process = System.Diagnostics.Process;

namespace PatchDiff;

public partial class MainWindow : Window
{
    static readonly string SettingsFile = Path.Combine(SnapshotStore.DataRoot, "settings.json");

    static readonly string UiStateFile = Path.Combine(SnapshotStore.DataRoot, "ui-state.json");

    const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValueName = "PatchDiff";

    System.Windows.Forms.NotifyIcon? _trayIcon;
    System.Windows.Forms.ToolStripMenuItem? _trayAutoStartItem;
    bool _reallyExiting;
    bool _trayHintShown;

    readonly WatchManager _mgr = WatchManager.Instance;
    readonly ObservableCollection<string> _logLines = new();
    readonly Dictionary<string, AssemblyDiffResult> _lastDiffs = new(StringComparer.OrdinalIgnoreCase);

    public MainWindow()
    {
        InitializeComponent();
        CardsList.ItemsSource = _mgr.Watched;
        WatchManagerHookup();
        LogList.ItemsSource = _logLines;
        TimelineList.ItemsSource = Timeline;
        LoadWatchList();
        LoadUiState();

        // Save state when Windows shuts down / the user logs off too.
        SystemEvents.SessionEnding += (_, _) => SaveAll();
        AutoStartItem.IsChecked = IsAutoStartEnabled();
        InitTray();
        Loaded += (_, _) => ((App)Application.Current).MainWindow = this;
    }

    // ---------- system tray ----------

    void InitTray()
    {
        try
        {
            var iconStream = Application.GetResourceStream(
                new Uri("pack://application:,,,/icon.ico")).Stream;
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
            // X / minimize => keep watching from the tray (ILSpy-style).
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

    void SaveAll()
    {
        SaveWatchList();
        SaveUiState();
    }

    // ---------- UI state persistence (timeline + last diffs, ILSpy-style) ----------

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

    // ---------- search across watched assemblies (ILSpy search-pane port) ----------

    sealed class SearchResult
    {
        public required string TypeName { get; init; }
        public required string AssemblyName { get; init; }
        public required string AssemblyPath { get; init; }
        public required string Source { get; init; }
    }

    readonly Dictionary<string, AssemblySnapshot> _baselineCache = new(StringComparer.OrdinalIgnoreCase);
    CancellationTokenSource? _searchCts;

    async Task<AssemblySnapshot?> GetBaselineAsync(WatchedDll w, CancellationToken ct)
    {
        if (_baselineCache.TryGetValue(w.Path, out var cached))
            return cached;
        var snap = await Task.Run(() => SnapshotStore.Load(w.Path), ct);
        if (snap is not null)
            _baselineCache[w.Path] = snap;
        return snap;
    }

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
                        if (t.FullName.Contains(query, StringComparison.OrdinalIgnoreCase))
                        {
                            list.Add(new SearchResult
                            {
                                TypeName = t.FullName,
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
        SearchResults.ItemsSource = results;
        if (results.Count > 0)
        {
            SearchResults.SelectedIndex = 0;
            SearchPopup.IsOpen = true;
        }
    }

    void OnSearchResultActivated(object sender, System.Windows.Input.MouseButtonEventArgs e) => OpenSelectedSearchResult();

    void OnSearchResultKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
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
        new SourceWindow(hit.TypeName, hit.AssemblyName, hit.Source) { Owner = this }.Show();
    }

    // ---------- export decompiled sources (ILSpy "save project" port) ----------

    void OnExportSources(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not WatchedDll w)
            return;
        var snap = SnapshotStore.Load(w.Path);
        if (snap is null)
        {
            AddLog($"No snapshot yet for {w.Name}.");
            return;
        }
        var dlg = new OpenFolderDialog { Title = $"Export decompiled sources for {w.Name}" };
        if (dlg.ShowDialog(this) != true)
            return;

        var dir = Path.Combine(dlg.FolderName,
            $"{Path.GetFileNameWithoutExtension(w.Name)}_{snap.Sha256[..8]}_sources");
        Directory.CreateDirectory(dir);
        foreach (var t in snap.Types)
        {
            var safe = string.Concat(t.FullName.Select(c =>
                char.IsLetterOrDigit(c) || c == '_' ? c : '_'));
            File.WriteAllText(Path.Combine(dir, safe + ".cs"), t.Source);
        }
        AddLog($"Exported {snap.Types.Count} sources for {w.Name} -> {dir}");
        Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
    }

    // ---------- assembly explorer (ILSpy assembly-tree port) ----------

    void OnBrowse(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is WatchedDll w)
            Browse(w);
    }

    void OnCardDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is WatchedDll w)
            Browse(w);
    }

    void Browse(WatchedDll w)
    {
        var snap = SnapshotStore.Load(w.Path);
        if (snap is null)
        {
            AddLog($"No snapshot yet for {w.Name} — wait for the first analysis to finish.");
            return;
        }
        new AssemblyExplorerWindow(snap) { Owner = this }.Show();
    }

    // ---------- reload all + check for updates (ILSpy UpdateService port) ----------

    void OnReloadAll(object sender, RoutedEventArgs e)
    {
        if (_mgr.Watched.Count == 0)
        {
            AddLog("Nothing to reload - no DLLs are being watched.");
            return;
        }
        foreach (var w in _mgr.Watched)
            _mgr.RequestCheck(w);
        AddLog($"Reloading all {_mgr.Watched.Count} watched DLLs...");
    }

    static string CurrentVersion()
    {
        try
        {
            var fv = System.Diagnostics.FileVersionInfo.GetVersionInfo(Environment.ProcessPath!);
            var v = fv.ProductVersion;
            if (string.IsNullOrEmpty(v))
                v = fv.FileVersion;
            if (string.IsNullOrEmpty(v))
                v = typeof(MainWindow).Assembly.GetName().Version?.ToString(3);
            return v ?? "?";
        }
        catch
        {
            return typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "?";
        }
    }

    async void OnCheckForUpdates(object sender, RoutedEventArgs e)
    {
        AddLog("Checking for updates...");
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("PatchDiff");
            var json = await http.GetStringAsync("https://api.github.com/repos/Tester12-SR-24/PatchDiff/releases/latest");
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
            var url = doc.RootElement.GetProperty("html_url").GetString() ?? "";
            var local = Version.TryParse(CurrentVersion(), out var lv) ? lv : null;
            var remote = Version.TryParse(tag.TrimStart('v'), out var rv) ? rv : null;

            if (remote is not null && local is not null && remote > local)
            {
                if (MessageBox.Show(this,
                        $"PatchDiff {remote} is available (you have {local.ToString(3)}).\n\nOpen the releases page?",
                        "Update available", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                {
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                }
                AddLog($"Update available: {tag}.");
            }
            else
            {
                MessageBox.Show(this, "PatchDiff is up to date.", "Check for updates",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                AddLog("Up to date.");
            }
        }
        catch (Exception ex)
        {
            AddLog($"Update check failed: {ex.Message}");
        }
    }

    void OnAbout(object sender, RoutedEventArgs e)
    {
        var v = CurrentVersion();
        MessageBox.Show(this,
            $"PatchDiff {v}\n\nWatch .NET/Unity DLLs; decompile with the ILSpy engine; diff, report, repeat.\n\nBuilt with DeepAI Code.",
            "About PatchDiff", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    void SaveUiState()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(UiStateFile)!);
            File.WriteAllText(UiStateFile, JsonSerializer.Serialize(new UiState
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
            if (!File.Exists(UiStateFile))
                return;
            var state = JsonSerializer.Deserialize<UiState>(File.ReadAllText(UiStateFile));
            if (state is null)
                return;

            foreach (var evt in state.Timeline.Take(250))
                Timeline.Add(evt);
            foreach (var (path, diff) in state.LastDiffs)
                _lastDiffs[path] = diff;

            if (state.Left is double l && state.Top is double t &&
                state.Width is double wd && state.Height is double ht)
            {
                if (Enum.TryParse<System.Windows.WindowState>(state.WindowState, out var ws))
                    WindowState = ws == System.Windows.WindowState.Minimized ? WindowState.Normal : ws;
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
                AddEvent("info", "RESUMED", $"Session restored \u2014 watch list re-armed; re-checking for changes made while PatchDiff was closed.");
        }
        catch { /* non-fatal */ }
    }

    // ---------- Start with Windows ----------

    static bool IsAutoStartEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(RunValueName) is not null;
    }

    void OnToggleAutoStart(object sender, RoutedEventArgs e)
    {
        if (_trayAutoStartItem is not null && _trayAutoStartItem.Checked != AutoStartItem.IsChecked)
        {
            _trayAutoStartItem.Checked = AutoStartItem.IsChecked;
            return; // tray handler re-invokes OnToggleAutoStart once
        }
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

    void WatchManagerHookup()
    {
        _mgr.Logged += message => Dispatcher.BeginInvoke(() => AddLog(message));

        _mgr.DiffProduced += (w, diff) => Dispatcher.BeginInvoke(() =>
        {
            _lastDiffs[w.Path] = diff;
            _baselineCache.Remove(w.Path); // snapshot changed → reload on next search/browse
            AddEvent("patched", "GAME PATCHED",
                $"{w.Name} rewritten on disk → auto-diff completed ({diff.Added + diff.Removed + diff.Modified} type changes) → report saved");
            UpdateFooter();
            _trayIcon?.ShowBalloonTip(4000, "DLL updated",
                $"{w.Name}: +{diff.Added} added, minus {diff.Removed} removed, ~{diff.Modified} modified",
                System.Windows.Forms.ToolTipIcon.Info);
        });

        _mgr.BaselineCreated += (w, _) => Dispatcher.BeginInvoke(() =>
            AddEvent("baseline", "BASELINE",
                $"{w.Name} added to watch — baseline captured ({w.Meta1})"));

        _mgr.Watched.CollectionChanged += (_, _) => UpdateFooter();
        UpdateFooter();
    }

    void UpdateFooter() => FooterText.Text =
        $"{_mgr.Watched.Count} watched · {Timeline.Count} timeline events · snapshots & reports: %LOCALAPPDATA%\\PatchDiff";

    void OnClearTimeline(object sender, RoutedEventArgs e)
    {
        Timeline.Clear();
        UpdateFooter();
    }

    void OnRecheck(object sender, RoutedEventArgs e)
    {
        var targets = CardsList.SelectedItems.OfType<WatchedDll>().ToList();
        if (targets.Count == 0)
        {
            AddLog("Select a card to re-check.");
            return;
        }
        foreach (var t in targets)
            _mgr.RequestCheck(t);
        AddLog(targets.Count == 1 ? $"Re-checking {targets[0].Name}…" : $"Re-checking {targets.Count} DLLs…");
    }

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
            SaveWatchList();
            AddLog($"Added {added} DLL{(added == 1 ? "" : "s")} via drag & drop.");
        }
    }

    public ObservableCollection<TimelineEvent> Timeline { get; } = new();

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
    }

    void AddLog(string line)
    {
        _logLines.Add(line);
        while (_logLines.Count > 500)
            _logLines.RemoveAt(0);
        if (_logLines.Count > 0)
            LogList.ScrollIntoView(_logLines[^1]);
    }

    // ---------- menu / toolbar handlers ----------

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
        SaveWatchList();
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
        SaveWatchList();
    }

    void OnRemoveWatch(object sender, RoutedEventArgs e)
    {
        var targets = CardsList.SelectedItems.OfType<WatchedDll>().ToList();
        if (targets.Count == 0)
        {
            AddLog("Select a card to remove its watch (click a card first; Ctrl/Shift-click for several).");
            return;
        }
        RemoveMany(targets);
    }

    void OnRemoveAll(object sender, RoutedEventArgs e)
        => RemoveMany(_mgr.Watched.ToList());

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
        SaveWatchList();
        UpdateFooter();
        AddLog($"Removed {targets.Count} watch{(targets.Count == 1 ? "" : "es")}.");
    }

    async void OnCompareTwo(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "OLD version of the DLL",
            Filter = "Assemblies (*.dll;*.exe)|*.dll;*.exe",
        };
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
            new DiffWindow(diff).Show();
        else
            MessageBox.Show(this, "No type changes detected between the two assemblies.", "PatchDiff");
    }

    void OnOpenReports(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(SnapshotStore.ReportsRoot);
        Process.Start(new ProcessStartInfo(SnapshotStore.ReportsRoot) { UseShellExecute = true });
    }

    void OnExit(object sender, RoutedEventArgs e) => ExitApp();

    // ---------- card button handlers ----------

    void OnOpenDiff(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not WatchedDll w)
            return;
        if (!_lastDiffs.TryGetValue(w.Path, out var diff) || !diff.HasChanges)
        {
            AddLog($"No recorded changes for {w.Name} yet — waiting for the next update.");
            return;
        }
        new DiffWindow(diff) { Owner = this }.Show();
    }

    void OnOpenReport(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not WatchedDll w)
            return;
        string? report = null;

        if (File.Exists(w.LastReportPath))
            report = w.LastReportPath;
        else if (_lastDiffs.TryGetValue(w.Path, out var diff) && diff.HasChanges)
            report = SnapshotStore.WriteHtmlReport(diff);

        if (report is null)
        {
            AddLog($"No report yet for {w.Name}.");
            return;
        }
        Process.Start(new ProcessStartInfo(report) { UseShellExecute = true });
    }

    // ---------- settings persistence ----------

    void SaveWatchList()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
            File.WriteAllText(SettingsFile,
                JsonSerializer.Serialize(new { watched = _mgr.Watched.Select(w => w.Path).ToArray() },
                    new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* non-fatal */ }
    }

    void LoadWatchList()
    {
        try
        {
            if (!File.Exists(SettingsFile))
                return;
            var doc = JsonDocument.Parse(File.ReadAllText(SettingsFile));
            if (doc.RootElement.TryGetProperty("watched", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in arr.EnumerateArray())
                {
                    var path = p.GetString();
                    if (!string.IsNullOrEmpty(path) && File.Exists(path))
                        _mgr.Add(path);
                }
            }
        }
        catch { /* non-fatal */ }
    }
}

public sealed class TimelineEvent
{
    public string When { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Badge { get; init; } = "";
    public string Message { get; init; } = "";
}
