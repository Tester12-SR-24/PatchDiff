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
    readonly WatchManager _mgr = WatchManager.Instance;
    readonly ObservableCollection<LogEntry> _logLines = new();
    readonly Dictionary<string, AssemblyDiffResult> _lastDiffs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Most recently produced diff across all assemblies (deterministic; replaces
    /// dictionary-order peeking for the Overview hero card).</summary>
    AssemblyDiffResult? _latestDiff;
    readonly Dictionary<string, AssemblySnapshot> _baselineCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>False until the constructor finished restoring persisted state.
    /// Guards SaveAll against the startup CollectionChanged storm wiping ui-state.json.</summary>
    bool _ready;

    /// <summary>Set for a genuine exit — Closing uses it to distinguish exit from close-to-tray.</summary>
    bool _reallyExiting;
    TrayIcon? _tray;

    /// <summary>True when the settings say the app should start hidden in the system tray;
    /// App.axaml.cs keeps the window unshown for this session when set.</summary>
    public bool StartHiddenToTray { get; private set; }

    StoredSettings _settings = new();

    public MainWindow()
    {
        InitializeComponent();
        try
        {
            var assets = Avalonia.Platform.AssetLoader.Open(new Uri("avares://PatchDiff/Assets/icon-512.png"));
            Icon = new Avalonia.Controls.WindowIcon(new Avalonia.Media.Imaging.Bitmap(assets));
        }
        catch { /* icon is cosmetic — never block startup */ }
        RefreshWatchList();
        LogList.ItemsSource = _logLines;
        TimelineList.ItemsSource = Timeline;
        WatchManagerHookup();
        LoadSettings();
        ReportUpdateOutcome();
        // InitTray() is handled via Avalonia TrayIcon in App.axaml.cs or platform services
        OpenOverviewTab();
        // SystemEvents.SessionEnding is Windows-specific; handle using Avalonia lifecycle events or P/Invoke if strictly needed.
        StartHiddenToTray = _settings.StartMinimized;
        AutoStartItem.IsChecked = AutoStart.IsEnabled();
        AutoStartItem.IsVisible = AutoStart.IsSupported;
        CreateTrayIcon();
        // A tray icon is only needed when the window can be hidden (close-to-tray or
        // tray start). When this window is shown normally, OnOpened drops the icon.
        if (!StartHiddenToTray && !_settings.CloseToTray)
            Opened += (_, _) => DisposeTray();
        Closing += (_, e) =>
        {
            if (!_reallyExiting && _settings.CloseToTray)
            {
                e.Cancel = true;
                Hide();
                AddLog("Closed to system tray — watching continues. Use the tray icon to reopen or exit.");
                return;
            }
            DisposeTray();
        };
        UpdateFooter();
        Closed += (_, _) => SaveAll();
        // Only now may watch-list changes persist — everything above must not touch ui-state.json.
        _ready = true;
    }

    void CreateTrayIcon()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            return; // macOS tray left unimplemented
        try
        {
            var assets = Avalonia.Platform.AssetLoader.Open(new Uri("avares://PatchDiff/Assets/icon-256.png"));
            var menu = new NativeMenu();
            var open = new NativeMenuItem("Open PatchDiff");
            open.Click += (_, _) => ShowFromTray();
            var exit = new NativeMenuItem("Exit");
            exit.Click += (_, _) => ExitApp();
            menu.Add(open);
            menu.Add(new NativeMenuItemSeparator());
            menu.Add(exit);
            _tray = new TrayIcon
            {
                Icon = new WindowIcon(new Avalonia.Media.Imaging.Bitmap(assets)),
                ToolTipText = "PatchDiff — Diff Workbench",
                Menu = menu,
                IsVisible = true,
            };
        }
        catch { /* tray is optional; the window still works normally */ }
    }

    void DisposeTray()
    {
        if (_tray is not null)
        {
            _tray.IsVisible = false;
            _tray.Dispose();
            _tray = null;
        }
    }

    public void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        if (!_settings.CloseToTray)
        {
            // Shown for good — let closing the last window exit the app again.
            if (App.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
                lifetime.ShutdownMode = ShutdownMode.OnLastWindowClose;
        }
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
            _latestDiff = diff;
            _baselineCache.Remove(w.Path);
            AddEvent("patched", "GAME PATCHED",
                $"{w.Name} rewritten on disk → auto-diff completed ({diff.Added + diff.Removed + diff.Modified} type changes) → report saved");
            if (_mgr.AutoSaveHistory)
                AddLog($"{w.Name}: diff ready and saved to history ({diff.Added + diff.Removed + diff.Modified} type changes).");
            else
                AddLog($"{w.Name}: diff ready — use Save to History in the diff tab to keep this version.");
            OpenDiffTab(w, diff);
            UpdateFooter();
        });

        _mgr.BaselineCreated += (w, _) => Dispatcher.UIThread.Post(() =>
            AddEvent("baseline", "BASELINE",
                $"{w.Name} added to watch — baseline captured ({w.Meta1})"));

        _mgr.Watched.CollectionChanged += (_, _) => { RefreshWatchList(); UpdateFooter(); SaveAll(); };
    }

    void UpdateFooter()
    {
        FooterText.Text =
            $"{_mgr.Watched.Count} assemblies watching · {Timeline.Count} recent events · local data: {DisplayPath(SnapshotStore.DataRoot)}";
        BuildPathRows();
        UpdateInspector();
    }

    /// <summary>Rail PATH section: what lives where and what each folder is for.</summary>
    void BuildPathRows()
    {
        PathRows.Children.Clear();
        var root = SnapshotStore.DataRoot;
        AddPathRow("Snapshots", "decompiled baseline of each watched DLL", Path.Combine(root, "Snapshots"));
        AddPathRow("History", "every analyzed version, kept until deleted", Path.Combine(root, "Revisions"));
        AddPathRow("Archive", "copies of each watched DLL at analysis time", Path.Combine(root, "Archive"));
        AddPathRow("Reports", "HTML diff reports", Path.Combine(root, "Reports"));
        AddPathRow("Logs", "crash diagnostics", Path.Combine(root, "logs"));
        AddPathRow("Settings", "options and watch list", Path.Combine(SnapshotStore.DefaultDataRoot, "settings.json"));
    }

    void AddPathRow(string label, string purpose, string path)
    {
        var row = new StackPanel { Spacing = 1 };
        row.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse("#9fb0c4")),
        });
        // Colors swapped per request: purpose (description) takes the path's cyan,
        // path takes the purpose's muted grey.
        row.Children.Add(new TextBlock
        {
            Text = purpose,
            FontSize = 8.5,
            Foreground = new SolidColorBrush(Color.Parse("#49d7e8")),
            TextWrapping = TextWrapping.Wrap,
        });
        row.Children.Add(new TextBlock
        {
            Text = DisplayPath(path),
            FontSize = 8.5,
            FontFamily = new FontFamily("Consolas, DejaVu Sans Mono, monospace"),
            Foreground = new SolidColorBrush(Color.Parse("#748197")),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        PathRows.Children.Add(row);
    }

    void RefreshWatchList()
    {
        var query = SearchBox?.Text?.Trim() ?? "";
        WatchedList.ItemsSource = query.Length == 0
            ? _mgr.Watched
            : _mgr.Watched.Where(w => w.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                      w.Path.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    void UpdateInspector()
    {
        var watch = SelectedWatch;
        if (watch is null)
        {
            InspectorName.Text = "Select an assembly";
            InspectorVersion.Text = "Watch a DLL to capture its baseline";
            InspectorPath.Text = "";
            AssemblyActions.IsEnabled = false;
            return;
        }

        InspectorName.Text = watch.Name;
        InspectorVersion.Text = string.IsNullOrWhiteSpace(watch.Meta1) ? watch.Status : watch.Meta1;
        InspectorPath.Text = DisplayPath(watch.Path);
        AssemblyActions.IsEnabled = true;
    }

    void OnSettings(object? sender, RoutedEventArgs e)
    {
        var pollInput = new TextBox { Text = _settings.PollSeconds.ToString(), Width = 110 };
        var archiveInput = new TextBox { Text = _settings.ArchiveKeep.ToString(), Width = 110 };
        var autoSaveCheck = new CheckBox
        {
            IsChecked = _settings.AutoSaveHistory,
            Content = new TextBlock { Text = "Automatically save every analyzed version to history", FontSize = 12 },
        };
        var closeToTrayCheck = new CheckBox
        {
            IsChecked = _settings.CloseToTray,
            Content = new TextBlock { Text = "Closing the window keeps PatchDiff running in the system tray", FontSize = 12 },
        };
        var startInTrayCheck = new CheckBox
        {
            IsChecked = _settings.StartMinimized,
            Content = new TextBlock { Text = "Start PatchDiff hidden in the system tray", FontSize = 12 },
        };
        var signInCheck = new CheckBox
        {
            IsChecked = AutoStart.IsEnabled(),
            IsVisible = AutoStart.IsSupported,
            Content = new TextBlock
            {
                Text = OperatingSystem.IsWindows()
                    ? "Start PatchDiff when I sign in to Windows"
                    : "Start PatchDiff when I sign in (Linux autostart)",
                FontSize = 12,
            },
        };
        var dataFolderText = new TextBlock
        {
            Text = DisplayPath(_settings.DataFolder) + (string.IsNullOrWhiteSpace(_settings.DataFolder) ? " (default location)" : ""),
            FontSize = 11,
            FontFamily = new FontFamily("Consolas, DejaVu Sans Mono, monospace"),
            Foreground = new SolidColorBrush(Color.Parse("#49d7e8")),
            VerticalAlignment = VerticalAlignment.Center,
        };
        string? pendingDataFolder = null;
        var dataPath = new TextBlock
        {
            Text = DisplayPath(SnapshotStore.DataRoot),
            FontFamily = new FontFamily("Consolas, DejaVu Sans Mono, monospace"),
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse("#49d7e8")),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var dialog = new Window
        {
            Title = "PatchDiff settings",
            Width = 540,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Background = new SolidColorBrush(Color.Parse("#0e1219")),
            Padding = new Thickness(0),
        };

        static TextBlock Header(string text) => new()
        {
            Text = text,
            Classes = { "section-label" },
            Margin = new Thickness(0, 4, 0, 2),
        };

        static TextBlock Description(string text) => new()
        {
            Text = text,
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse("#8d99ac")),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 6),
        };

        var content = new StackPanel { Spacing = 10 };

        var titleBar = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#10161f")),
            BorderBrush = new SolidColorBrush(Color.Parse("#283142")),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(22, 14),
            Child = new TextBlock { Text = "Settings", FontSize = 18, FontWeight = FontWeight.SemiBold, Foreground = new SolidColorBrush(Color.Parse("#e8edf5")) },
        };
        content.Children.Add(titleBar);

        var body = new StackPanel { Spacing = 8, Margin = new Thickness(22, 14, 22, 6) };

        body.Children.Add(Header("WATCHING"));
        body.Children.Add(Description("Polling catches DLL replacements even if the filesystem watcher misses the event."));
        var pollRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        pollRow.Children.Add(new TextBlock { Text = "Poll interval", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Width = 130 });
        pollRow.Children.Add(pollInput);
        pollRow.Children.Add(new TextBlock { Text = "seconds (at least 2)", FontSize = 11, Foreground = new SolidColorBrush(Color.Parse("#8d99ac")), VerticalAlignment = VerticalAlignment.Center });
        body.Children.Add(pollRow);

        body.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.Parse("#283142")), Margin = new Thickness(0, 6, 0, 2) });
        body.Children.Add(Header("HISTORY"));
        body.Children.Add(Description("Each watched assembly keeps its own history of analyzed versions — diff them or restore them later. The oldest are pruned beyond the limit."));
        body.Children.Add(autoSaveCheck);
        body.Children.Add(Description("While a watched DLL is being analyzed, the analyzed version is added to its history automatically. Turn this off to keep only the latest baseline and save versions yourself with the Save to History button in the diff tab."));
        var keepRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        keepRow.Children.Add(new TextBlock { Text = "History kept until deletion", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Width = 170 });
        keepRow.Children.Add(archiveInput);
        keepRow.Children.Add(new TextBlock { Text = "versions per DLL (0 = keep all)", FontSize = 11, Foreground = new SolidColorBrush(Color.Parse("#8d99ac")), VerticalAlignment = VerticalAlignment.Center });
        body.Children.Add(keepRow);

        body.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.Parse("#283142")), Margin = new Thickness(0, 6, 0, 2) });
        body.Children.Add(Header("TRAY & STARTUP"));
        body.Children.Add(closeToTrayCheck);
        body.Children.Add(Description("When off, closing the window exits PatchDiff."));
        body.Children.Add(startInTrayCheck);
        body.Children.Add(Description("Applies on next start: PatchDiff runs from the tray and the main window stays hidden until you open it from the tray icon."));
        body.Children.Add(signInCheck);
        body.Children.Add(Description("Registers PatchDiff to run at sign-in (Windows registry or a Linux autostart entry)."));

        body.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.Parse("#283142")), Margin = new Thickness(0, 6, 0, 2) });
        body.Children.Add(Header("DATA LOCATION"));
        body.Children.Add(Description("Baselines, history and reports stay on this device. Nothing is uploaded."));
        var dataRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        dataRow.Children.Add(dataPath);
        var openData = new Button { Content = "Open folder" };
        openData.Classes.Add("toolbar");
        openData.Click += (_, _) => ShellOpen.Open(dialog, SnapshotStore.DataRoot);
        dataRow.Children.Add(openData);
        body.Children.Add(dataRow);
        var dataFolderRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        dataFolderRow.Children.Add(new TextBlock { Text = "Data folder", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Width = 130 });
        dataFolderRow.Children.Add(dataFolderText);
        var changeData = new Button { Content = "Change…" };
        changeData.Classes.Add("toolbar");
        changeData.Click += async (_, _) =>
        {
            var storage = TopLevel.GetTopLevel(dialog)?.StorageProvider;
            if (storage is null) return;
            var folders = await storage.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
            {
                Title = "Choose the folder for snapshots, history and reports",
                AllowMultiple = false,
            });
            if (folders.Count > 0)
            {
                pendingDataFolder = folders[0].Path.LocalPath;
                dataFolderText.Text = DisplayPath(pendingDataFolder);
            }
        };
        dataFolderRow.Children.Add(changeData);
        body.Children.Add(dataFolderRow);
        body.Children.Add(Description("Takes effect on next start: snapshots, history, archive and reports move there. Settings stay in the default location so the choice can be changed back."));

        content.Children.Add(body);

        var error = new TextBlock { Foreground = new SolidColorBrush(Color.Parse("#ff969e")), FontSize = 12, Margin = new Thickness(22, 0, 22, 0) };
        content.Children.Add(error);

        var buttons = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#10161f")),
            BorderBrush = new SolidColorBrush(Color.Parse("#283142")),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(22, 12),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 8,
            },
        };
        var cancel = new Button { Content = "Cancel" };
        cancel.Classes.Add("secondary");
        cancel.Click += (_, _) => dialog.Close();
        var save = new Button { Content = "Save settings" };
        save.Classes.Add("primary");
        save.Click += (_, _) =>
        {
            if (!int.TryParse(pollInput.Text, out var poll) || poll < 2 ||
                !int.TryParse(archiveInput.Text, out var keep) || keep < 0)
            {
                error.Text = "Enter a poll interval of at least 2 seconds and a non-negative revision limit.";
                return;
            }
            _settings.PollSeconds = poll;
            _settings.ArchiveKeep = keep;
            _settings.AutoSaveHistory = autoSaveCheck.IsChecked == true;
            _settings.CloseToTray = closeToTrayCheck.IsChecked == true;
            _settings.StartMinimized = startInTrayCheck.IsChecked == true;
            if (pendingDataFolder is not null && pendingDataFolder != _settings.DataFolder)
            {
                _settings.DataFolder = pendingDataFolder;
                AddLog("Data folder change saved — restart PatchDiff to move snapshots, history and reports there.");
            }
            ApplySettings();
            SettingsFile.Save(_settings);
            if (AutoStart.IsSupported && signInCheck.IsChecked == true != AutoStart.IsEnabled())
            {
                try
                {
                    AutoStart.SetEnabled(signInCheck.IsChecked == true);
                    AddLog(signInCheck.IsChecked == true
                        ? "PatchDiff will now start when you sign in."
                        : "Sign-in auto-start disabled.");
                }
                catch (Exception ex)
                {
                    AddLog($"Could not change sign-in auto-start: {ex.Message}");
                }
            }
            AddLog("Settings saved.");
            dialog.Close();
        };
        (buttons.Child as StackPanel)!.Children.Add(cancel);
        (buttons.Child as StackPanel)!.Children.Add(save);
        content.Children.Add(buttons);
        dialog.Content = content;
        _ = dialog.ShowDialog(this);
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
        // Persist immediately once initialized — a crash must not lose the timeline
        // (watch-list changes and window close also save; this covers the rest).
        if (_ready)
            SaveUiState();
    }

    void AddLog(string line)
    {
        _logLines.Add(LogEntry.From(line));
        while (_logLines.Count > 500)
            _logLines.RemoveAt(0);
        if (_logLines.Count > 0)
            LogList.ScrollIntoView(_logLines[^1]);
    }

    void OnClearLog(object sender, RoutedEventArgs e)
    {
        _logLines.Clear();
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
        // Vector icon + label row; falls back to text-only if the resource is missing.
        return BuildIconRow(iconKey, text);
    }

    static StackPanel BuildIconRow(string iconKey, string text, string? lightIconKey = null, string? whiteIconKey = null)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        void Add(string key, string cls)
        {
            if (App.Current is { } app && app.Resources.TryGetValue(key, out var drawing) && drawing is Avalonia.Media.IImage img)
                row.Children.Add(new Image { Source = img, Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center, Classes = { cls } });
        }
        Add(iconKey, "darkIcon");
        // Default light icon while the primary button is disabled.
        if (lightIconKey is not null) Add(lightIconKey, "lightIcon");
        // White icon while hovered (Fluent turns the label white on pointerover).
        if (whiteIconKey is not null) Add(whiteIconKey, "whiteIcon");
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
        if (ReferenceEquals(tab.Content, _activeBrowse))
        {
            _activeBrowse = null;
            ExportCsBtn.IsEnabled = false;
        }
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
        var panel = new StackPanel { Spacing = 14, Margin = new Thickness(20, 18, 20, 14) };
        var selected = SelectedWatch;
        var recentDiff = selected is not null && _lastDiffs.TryGetValue(selected.Path, out var selectedDiff)
            ? selectedDiff
            : _latestDiff;

        var hero = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#151e29")),
            BorderBrush = new SolidColorBrush(Color.Parse("#2b3b49")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(18),
        };
        var heroGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 14 };
        // Left-align everything: a Stretch-aligned child capped by MaxWidth gets CENTERED
        // by Avalonia, which made the hero text drift to the middle on wide windows.
        var heroText = new StackPanel { Spacing = 5, HorizontalAlignment = HorizontalAlignment.Left };
        heroText.Children.Add(new TextBlock { Text = "DIFF WORKBENCH", FontSize = 10, FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(Color.Parse("#72dcd0")), HorizontalAlignment = HorizontalAlignment.Left });
        heroText.Children.Add(new TextBlock { Text = recentDiff?.Title ?? "Your assembly workspace is ready", FontSize = 22, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Left });
        var heroDeltas = recentDiff is null ? (0, 0, 0) : DiffService.LineDeltas(recentDiff);
        heroText.Children.Add(new TextBlock
        {
            Text = recentDiff is null
                ? "Watch a Unity or .NET assembly to capture its baseline. PatchDiff will compare future updates automatically."
                : $"{heroDeltas.Item1} lines added · {heroDeltas.Item2} removed · {heroDeltas.Item3} modified · baseline {ShortHash(recentDiff.OldSha)} → current {ShortHash(recentDiff.NewSha)}",
            FontSize = 12, Foreground = new SolidColorBrush(Color.Parse("#9aa8bb")), TextWrapping = TextWrapping.Wrap, MaxWidth = 640,
            HorizontalAlignment = HorizontalAlignment.Left,
        });
        heroGrid.Children.Add(heroText);
        var heroButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(heroButtons, 1);
        var compareButton = new Button();
        compareButton.Classes.Add("primary");
        compareButton.Content = BuildIconRow("IconCompareDark", "Compare DLLs", "IconCompare", "IconCompareWhite");
        compareButton.Click += (_, _) => OnCompareTwo(compareButton, new RoutedEventArgs());
        var addButton = new Button();
        addButton.Classes.Add("secondary");
        addButton.Content = BuildIconRow("IconWatchAdd", "Add DLL");
        addButton.Click += (_, _) => OnAddDll(addButton, new RoutedEventArgs());
        heroButtons.Children.Add(compareButton);
        heroButtons.Children.Add(addButton);
        heroGrid.Children.Add(heroButtons);
        hero.Child = heroGrid;
        panel.Children.Add(hero);

        // Line-level counts from the same helper the diff view uses, so the Overview
        // cards always agree with the diff header and the per-type pills.
        var (linesAdded, linesRemoved, linesModified) = recentDiff is null
            ? (0, 0, 0)
            : DiffService.LineDeltas(recentDiff);
        var stats = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*,*"), ColumnSpacing = 8 };
        AddStat(stats, 0, "WATCHING", _mgr.Watched.Count.ToString(), "#66daca");
        AddStat(stats, 1, "LINES CHANGED", recentDiff is null ? "—" : (linesAdded + linesRemoved + linesModified).ToString(), "#e5b567");
        AddStat(stats, 2, "LINES ADDED", recentDiff is null ? "—" : $"+{linesAdded}", "#6dda9f");
        AddStat(stats, 3, "LINES REMOVED", recentDiff is null ? "—" : $"−{linesRemoved}", "#f08e97");
        panel.Children.Add(stats);

        var columns = new Grid { ColumnDefinitions = new ColumnDefinitions("1.15*,0.85*"), ColumnSpacing = 10 };
        var activity = CreateOverviewCard("Recent patch activity");
        if (Timeline.Count == 0)
        {
            activity.Children.Add(new TextBlock { Text = "No patch events yet. Add a DLL or folder to start watching.", FontSize = 12, Foreground = new SolidColorBrush(Color.Parse("#8996aa")), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5) });
        }
        else
        {
            foreach (var evt in Timeline.Take(5))
            {
                var row = new StackPanel { Spacing = 2, Margin = new Thickness(0, 4) };
                row.Children.Add(new TextBlock { Text = evt.Badge, FontSize = 10, FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(Color.Parse("#69d9cd")) });
                row.Children.Add(new TextBlock { Text = evt.Message, FontSize = 11, Foreground = new SolidColorBrush(Color.Parse("#c3ccda")), TextWrapping = TextWrapping.Wrap });
                row.Children.Add(new TextBlock { Text = evt.When, FontSize = 10, Foreground = new SolidColorBrush(Color.Parse("#748197")) });
                activity.Children.Add(row);
            }
        }
        columns.Children.Add(WrapOverviewCard(activity));

        var actions = CreateOverviewCard("Continue working");
        AddOverviewAction(actions, "Browse decompiled code", "Inspect namespaces, types, members and source", OnBrowse);
        AddOverviewAction(actions, "Review latest diff", "Open the selected assembly's most recent changes", OnOpenDiff);
        AddOverviewAction(actions, "Revision history", "Compare or restore archived baselines", OnHistory);
        AddOverviewAction(actions, "Open reports", "View generated HTML patch reports", OnOpenReports);
        var actionBorder = WrapOverviewCard(actions);
        Grid.SetColumn(actionBorder, 1);
        columns.Children.Add(actionBorder);
        panel.Children.Add(columns);

        panel.Children.Add(new TextBlock
        {
            Text = @"UNITY TIP     <game>_Data/Managed/Assembly-CSharp.dll",
            FontFamily = new FontFamily("Consolas, DejaVu Sans Mono, monospace"), FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse("#77859a")), Margin = new Thickness(2, 0),
        });
        return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
    }

    static string ShortHash(string value) => string.IsNullOrEmpty(value) ? "unknown" : value[..Math.Min(8, value.Length)];

    static void AddStat(Grid grid, int column, string label, string value, string color)
    {
        var card = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#141a24")), BorderBrush = new SolidColorBrush(Color.Parse("#283142")),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7), Padding = new Thickness(11, 8),
        };
        var stack = new StackPanel { Spacing = 2 };
        stack.Children.Add(new TextBlock { Text = label, FontSize = 10, Foreground = new SolidColorBrush(Color.Parse("#8794a8")) });
        stack.Children.Add(new TextBlock { Text = value, FontFamily = new FontFamily("Consolas, DejaVu Sans Mono, monospace"), FontSize = 20, FontWeight = FontWeight.SemiBold, Foreground = new SolidColorBrush(Color.Parse(color)) });
        card.Child = stack;
        Grid.SetColumn(card, column);
        grid.Children.Add(card);
    }

    static StackPanel CreateOverviewCard(string title)
    {
        var stack = new StackPanel { Spacing = 5 };
        stack.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        return stack;
    }

    static Border WrapOverviewCard(Control content)
    {
        var border = new Border { Background = new SolidColorBrush(Color.Parse("#141a24")), BorderBrush = new SolidColorBrush(Color.Parse("#283142")), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(12), Child = content };
        return border;
    }

    static void AddOverviewAction(Panel panel, string title, string description, EventHandler<RoutedEventArgs> handler)
    {
        var button = new Button { HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(9, 7) };
        button.Classes.Add("secondary");
        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(new TextBlock { Text = title, FontSize = 11, FontWeight = FontWeight.SemiBold });
        text.Children.Add(new TextBlock { Text = description, FontSize = 10, Foreground = new SolidColorBrush(Color.Parse("#8c99ad")), TextWrapping = TextWrapping.Wrap });
        button.Content = text;
        button.Click += handler;
        panel.Children.Add(button);
    }

    void OnShowOverview(object? sender, RoutedEventArgs e) => OpenOverviewTab();

    void OnSearchAssemblies(object? sender, Avalonia.Controls.TextChangedEventArgs e) => RefreshWatchList();

    void OpenDiffTab(WatchedDll w, AssemblyDiffResult diff)
    {
        var view = new DiffView(diff);
        if (!_mgr.AutoSaveHistory)
        {
            // Auto-save is off — analysis only keeps the baseline; this version needs an
            // explicit save to enter the revision history.
            view.EnableSaveToHistory(() =>
            {
                _mgr.SaveToHistory(w.Path);
                AddLog($"{w.Name}: diff saved to history.");
            });
        }
        AddTab($"Diff — {w.Name}", view);
    }

    BrowseView? _activeBrowse;

    void OpenBrowseTab(WatchedDll w)
    {
        var snap = SnapshotStore.Load(w.Path);
        if (snap is null)
        {
            AddLog($"No snapshot yet for {w.Name} — wait for the first analysis to finish.");
            return;
        }
        var view = new BrowseView(snap, w);
        _activeBrowse = view;
        view.TypeSelectionChanged += enabled => ExportCsBtn.IsEnabled = enabled;
        AddTab($"Browse — {w.Name}", view);
    }

    void OpenHistoryTab(WatchedDll w)
    {
        var view = new HistoryView(w);
        view.DiffRequested += diff => Dispatcher.UIThread.Post(() =>
        {
            AddTab($"Diff — {w.Name} (history)", new DiffView(diff));
            _latestDiff = diff;
            UpdateFooter();
        });
        AddTab($"History — {w.Name}", view);
    }


    WatchedDll? SelectedWatch => WatchedList.SelectedItem as WatchedDll;

    void OnOpenDiff(object? sender, RoutedEventArgs e)
    {
        var w = SelectedWatch;
        if (w is null) { AddLog("Select an assembly in the rail first."); return; }
        if (_lastDiffs.TryGetValue(w.Path, out var diff) && diff.HasChanges)
            OpenDiffTab(w, diff);
        else
            AddLog($"No recorded changes for {w.Name} yet — waiting for the next update.");
    }

    void OnBrowse(object? sender, RoutedEventArgs e)
    {
        var w = SelectedWatch;
        if (w is null) { AddLog("Select an assembly in the rail first."); return; }
        OpenBrowseTab(w);
    }

    void OnHistory(object? sender, RoutedEventArgs e)
    {
        var w = SelectedWatch;
        if (w is null) { AddLog("Select an assembly in the rail first."); return; }
        OpenHistoryTab(w);
    }

    async void OnExportCsFile(object sender, RoutedEventArgs e)
    {
        // Exports the C# source of the type currently highlighted in the browser.
        var node = _activeBrowse?.SelectedNode;
        if (node?.FullName is null || node.Source is null)
        {
            AddLog("Select a type in the Browse tab first — Export C# file saves the highlighted class's source.");
            return;
        }

        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null) return;
        var file = await storage.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = $"Export C# source for {node.FullName}",
            DefaultExtension = ".cs",
            SuggestedFileName = ExportFileName(node.FullName),
            FileTypeChoices = new[] { new Avalonia.Platform.Storage.FilePickerFileType("C# source") { Patterns = new[] { "*.cs" } } },
        });
        if (file is null) return;
        File.WriteAllText(file.Path.LocalPath, node.Source);
        AddLog($"Exported {node.FullName} → {file.Path.LocalPath}");
    }

    void OnOpenReport(object sender, RoutedEventArgs e)
    {
        var w = SelectedWatch;
        if (w is null) { AddLog("Select an assembly in the rail first."); return; }
        string? report = File.Exists(w.LastReportPath) ? w.LastReportPath : null;
        if (report is null && _lastDiffs.TryGetValue(w.Path, out var diff) && diff.HasChanges)
            report = SnapshotStore.WriteHtmlReport(diff);
        if (report is null) { AddLog($"No report yet for {w.Name}."); return; }
        ShellOpen.Open(this, report);
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
        _ = RemoveManyAsync(targets);
    }

    void OnRemoveAll(object sender, RoutedEventArgs e) => _ = RemoveManyAsync(_mgr.Watched.ToList());

    async Task RemoveManyAsync(List<WatchedDll> targets)
    {
        if (targets.Count == 0)
            return;
        var msg = targets.Count == 1
            ? $"Stop watching {targets[0].Name}?\n\nIts baseline snapshots and archived revisions will be deleted from this device."
            : $"Stop watching all {targets.Count} DLLs?\n\nTheir baseline snapshots and archived revisions will be deleted from this device.";
        if (!await MessageBox.Confirm(this, msg, "Remove watch"))
        {
            AddLog("Remove cancelled — nothing was deleted.");
            return;
        }
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

    async void OnExportDecodedDll(object sender, RoutedEventArgs e)
    {
        var w = SelectedWatch;
        if (w is null) { AddLog("Select an assembly in the rail first."); return; }
        var snap = SnapshotStore.Load(w.Path);
        if (snap is null) { AddLog($"No snapshot yet for {w.Name}."); return; }

        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null) return;
        var folders = await storage.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
        {
            Title = $"Choose a parent folder for the exported {w.Name} source",
            AllowMultiple = false,
        });
        if (folders.Count == 0) return;

        // Folder named <name>-<date>-<time>, e.g. "Assembly-CSharp-11-24-2004-9.32.23 AM".
        // Colons are invalid in Windows folder names, so they're written as dots.
        var now = DateTime.Now;
        var stamp = $"{now.Month}-{now.Day}-{now.Year}-{now.Hour}.{now.Minute}.{now.Second} {(now.Hour < 12 ? "AM" : "PM")}";
        var dir = Path.Combine(folders[0].Path.LocalPath,
            $"{Path.GetFileNameWithoutExtension(w.Name)}-{stamp}");
        Directory.CreateDirectory(dir);
        foreach (var t in snap.Types)
        {
            var safe = string.Concat(t.FullName.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_'));
            File.WriteAllText(Path.Combine(dir, safe + ".cs"), t.Source);
        }
        AddLog($"Exported decoded DLL for {w.Name} — {snap.Types.Count} C# classes → {dir}");
        ShellOpen.Open(this, dir);
    }

    void OnExportSources(object sender, RoutedEventArgs e) => OnExportCsFile(sender, e);

    void OnRecheck(object sender, RoutedEventArgs e)
    {
        var w = SelectedWatch;
        if (w is null) { AddLog("Select an assembly in the rail first."); return; }
        var baseline = SnapshotStore.Load(w.Path);
        var currentBytes = baseline is null ? null : TryReadFile(w.Path);
        if (baseline is not null && currentBytes is not null)
        {
            var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(currentBytes));
            if (string.Equals(sha, baseline.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                AddLog($"{w.Name}: baseline is up to date (v{baseline.AssemblyVersion}, {baseline.Types.Count} types) — nothing to update.");
                return;
            }
            AddLog($"{w.Name}: file differs from baseline — updating baseline and re-analyzing…");
        }
        _mgr.RequestCheck(w);
        AddLog($"Re-checking {w.Name}…");
    }

    static byte[]? TryReadFile(string path)
    {
        try { return File.ReadAllBytes(path); }
        catch { return null; }
    }

    void OnReloadAll(object? sender, RoutedEventArgs? e)
    {
        if (_mgr.Watched.Count == 0)
        {
            AddLog("Nothing to reload — no DLLs are being watched.");
            return;
        }
        var stale = 0;
        foreach (var w in _mgr.Watched)
        {
            if (IsBaselineUpToDate(w))
            {
                AddLog($"{w.Name}: baseline is up to date.");
                continue;
            }
            stale++;
            _mgr.RequestCheck(w);
        }
        AddLog(stale > 0
            ? $"Reloading {stale} of {_mgr.Watched.Count} watched DLLs — {stale} baseline(s) out of date, updating…"
            : $"All {_mgr.Watched.Count} watched DLLs are up to date.");
    }

    /// <summary>True when the file on disk still matches the recorded baseline hash.</summary>
    bool IsBaselineUpToDate(WatchedDll w)
    {
        try
        {
            if (string.IsNullOrEmpty(w.Path) || !File.Exists(w.Path)) return false;
            var baseline = SnapshotStore.Load(w.Path);
            if (baseline is null) return false;
            using var sha = System.Security.Cryptography.SHA256.Create();
            using var fs = File.OpenRead(w.Path);
            var hash = Convert.ToHexString(sha.ComputeHash(fs));
            return string.Equals(hash, baseline.Sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
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
        {
            var view = new DiffView(diff);
            // Manual compares never archive on their own — offer the explicit save.
            view.EnableSaveToHistory(() =>
            {
                _mgr.SaveToHistory(newPath);
                AddLog($"{Path.GetFileName(newPath)}: compare result saved to revision history.");
            });
            AddTab($"Diff — {Path.GetFileName(oldPath)} → {Path.GetFileName(newPath)}", view);
            _latestDiff = diff;
            UpdateFooter();
        }
        else
            AddLog("No type changes detected between the two assemblies.");
    }

    void OnOpenReports(object? sender, RoutedEventArgs? e)
    {
        Directory.CreateDirectory(SnapshotStore.ReportsRoot);
        ShellOpen.Open(this, SnapshotStore.ReportsRoot);
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
        if (!OperatingSystem.IsWindows())
        {
            AddLog("Automatic in-place updates are only available on Windows; opening the release page for a manual update.");
            try
            {
                Process.Start(new ProcessStartInfo(release.PageUrl) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                AddLog($"Could not open the release page: {ex.Message}");
            }
            return;
        }

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
        (App.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }

    void OnAbout(object sender, RoutedEventArgs e)
        => AddLog($"PatchDiff {UpdaterService.CurrentVersion()} — Watch .NET/Unity DLLs; decompile with the ILSpy engine; diff, report, repeat.");

    // ---------------- persistence ----------------

    void SaveAll()
    {
        if (!_ready)
            return; // startup restore in progress — never overwrite persisted state with a half-built window
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
            if (File.Exists(p))
                _mgr.Add(p);
            else
                AddLog($"Watch skipped (file not found): {p}");
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
            _latestDiff = state.LastDiffs.Values.LastOrDefault();
            if (state.Position is PixelPoint p && state.Width is double wd && state.Height is double ht)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Position = ClampToScreen(p, wd, ht);
                Width = wd;
                Height = ht;
            }
            if (state.Timeline.Count > 0)
                AddEvent("info", "RESUMED", "Session restored — watch list re-armed; re-checking for changes made while PatchDiff was closed.");
        }
        catch { /* non-fatal */ }
    }

    /// <summary>Keeps a restored window position on-screen: clamps into the primary
    /// screen's work area and caps size to it. Prevents off-screen/oversized restores
    /// (e.g. saved geometry from a monitor that is no longer attached).</summary>
    PixelPoint ClampToScreen(PixelPoint p, double w, double h)
    {
        try
        {
            var wa = Screens.Primary?.WorkingArea;
            if (wa is not PixelRect area)
                return p;
            var width = Math.Min(Math.Max(w, MinWidth), area.Width);
            var height = Math.Min(Math.Max(h, MinHeight), area.Height);
            var x = Math.Min(Math.Max(p.X, area.X), area.X + area.Width - (int)width);
            var y = Math.Min(Math.Max(p.Y, area.Y), area.Y + area.Height - (int)height);
            Width = width;
            Height = height;
            return new PixelPoint(x, y);
        }
        catch { return p; }
    }

    void OnToggleAutoStart(object? sender, RoutedEventArgs? e)
    {
        if (!AutoStart.IsSupported)
            return;
        try
        {
            var enable = AutoStartItem.IsChecked;
            AutoStart.SetEnabled(enable);
            AddLog(enable
                ? (OperatingSystem.IsWindows() ? "PatchDiff will now start when you sign in to Windows."
                                               : "PatchDiff will now start when you sign in (Linux autostart).")
                : "Sign-in auto-start disabled.");
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

public enum LogLevel { Info, Warning, Error }

/// <summary>One activity-log row: timestamp + severity dot + message.
/// Severity is inferred from wording; the log feed is plain strings.</summary>
public sealed class LogEntry
{
    static readonly IBrush InfoDot = new SolidColorBrush(Color.FromRgb(0x49, 0xd7, 0xe8));
    static readonly IBrush WarningDot = new SolidColorBrush(Color.FromRgb(0xe5, 0xb5, 0x67));
    static readonly IBrush ErrorDot = new SolidColorBrush(Color.FromRgb(0xf0, 0x8e, 0x97));
    static readonly IBrush InfoText = new SolidColorBrush(Color.FromRgb(0xc1, 0xca, 0xd8));
    static readonly IBrush WarningText = new SolidColorBrush(Color.FromRgb(0xe8, 0xc9, 0x93));
    static readonly IBrush ErrorText = new SolidColorBrush(Color.FromRgb(0xf3, 0xa5, 0xac));

    public string When { get; init; } = "";
    public string Message { get; init; } = "";
    public LogLevel Level { get; init; }
    public IBrush DotBrush => Level switch
    {
        LogLevel.Warning => WarningDot,
        LogLevel.Error => ErrorDot,
        _ => InfoDot,
    };
    public IBrush MessageBrush => Level switch
    {
        LogLevel.Warning => WarningText,
        LogLevel.Error => ErrorText,
        _ => InfoText,
    };

    public static LogEntry From(string line) => new()
    {
        When = DateTime.Now.ToString("HH:mm:ss"),
        Message = line,
        Level = InferLevel(line),
    };

    static LogLevel InferLevel(string line)
    {
        var l = line.ToLowerInvariant();
        if (l.Contains("fail") || l.Contains("error") || l.Contains("could not"))
            return LogLevel.Error;
        if (l.Contains("skipped") || l.Contains("not found") || l.Contains("no snapshot") ||
            l.Contains("nothing") || l.Contains("cancelled") || l.Contains("waiting") ||
            l.Contains("locked"))
            return LogLevel.Warning;
        return LogLevel.Info;
    }
}

public partial class MainWindow
{
    void ExitApp()
    {
        _reallyExiting = true;
        DisposeTray();
        Close();
        (App.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }
}

public partial class MainWindow
{
    void ApplySettings()
    {
        _mgr.PollInterval = TimeSpan.FromSeconds(Math.Max(2, _settings.PollSeconds));
        _mgr.ArchiveKeep = _settings.ArchiveKeep;
        _mgr.AutoSaveHistory = _settings.AutoSaveHistory;
        _mgr.UpdatePollInterval();
    }
}
