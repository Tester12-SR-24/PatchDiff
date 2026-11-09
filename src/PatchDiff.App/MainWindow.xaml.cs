using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using PatchDiff.Models;
using PatchDiff.Services;
using Microsoft.Win32;

namespace PatchDiff;

public partial class MainWindow : Window
{
    static readonly string SettingsFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PatchDiff", "settings.json");

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
    }

    void WatchManagerHookup()
    {
        _mgr.Logged += message => Dispatcher.BeginInvoke(() => AddLog(message));

        _mgr.DiffProduced += (w, diff) => Dispatcher.BeginInvoke(() =>
        {
            _lastDiffs[w.Path] = diff;
            AddEvent("patched", "GAME PATCHED",
                $"{w.Name} rewritten on disk → auto-diff completed ({diff.Added + diff.Removed + diff.Modified} type changes) → report saved");
            UpdateFooter();
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
        if (CardsList.SelectedItems.Count == 0)
        {
            AddLog("Select a card to remove its watch.");
            return;
        }
        foreach (var item in CardsList.SelectedItems.OfType<WatchedDll>().ToList())
            _mgr.Remove(item);
        SaveWatchList();
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

    void OnExit(object sender, RoutedEventArgs e) => Close();

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
