using System.Collections.ObjectModel;
using System.IO;
using PatchDiff.Models;

namespace PatchDiff.Services;

/// <summary>One watched DLL and its watcher plumbing.</summary>
public sealed class WatchedDll : System.ComponentModel.INotifyPropertyChanged
{
    public required string Path { get; init; }
    public string Name => System.IO.Path.GetFileName(Path);
    public FileSystemWatcher? Watcher { get; set; }

    string _status = "Initializing…";
    public string Status
    {
        get => _status;
        set { _status = value; PropertyChanged?.Invoke(this, new(nameof(Status))); }
    }

    string _version = "";
    public string AssemblyVersion
    {
        get => _version;
        set { _version = value; PropertyChanged?.Invoke(this, new(nameof(AssemblyVersion))); }
    }

    string _sha = "";
    public string ShaShort
    {
        get => _sha;
        set { _sha = value; PropertyChanged?.Invoke(this, new(nameof(ShaShort))); }
    }

    string _lastCheck = "—";
    public string LastCheck
    {
        get => _lastCheck;
        set { _lastCheck = value; PropertyChanged?.Invoke(this, new(nameof(LastCheck))); }
    }

    string _pillKind = "busy";
    public string PillKind
    {
        get => _pillKind;
        set { _pillKind = value; PropertyChanged?.Invoke(this, new(nameof(PillKind))); }
    }

    string _pillText = "…";
    public string PillText
    {
        get => _pillText;
        set { _pillText = value; PropertyChanged?.Invoke(this, new(nameof(PillText))); }
    }

    string _meta1 = "";
    public string Meta1
    {
        get => _meta1;
        set { _meta1 = value; PropertyChanged?.Invoke(this, new(nameof(Meta1))); }
    }

    string _changeSummary = "";
    public string ChangeSummary
    {
        get => _changeSummary;
        set { _changeSummary = value; PropertyChanged?.Invoke(this, new(nameof(ChangeSummary))); }
    }

    public string LastReportPath { get; set; } = "";

    /// <summary>Debounce timer keyed per DLL (reset on every file-system event).</summary>
    public System.Timers.Timer? Debounce { get; set; }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Watches registered DLLs. When a DLL is replaced/updated (e.g. a Unity game patch),
/// the new version is decompiled, diffed against the stored baseline snapshot, an HTML
/// report is written, and the result is pushed to the UI.
/// </summary>
public sealed class WatchManager : IDisposable
{
    public static readonly WatchManager Instance = new();

    public ObservableCollection<WatchedDll> Watched { get; } = new();

    readonly DecompilerService _decompiler = new();
    readonly DiffService _diff = new();
    readonly SemaphoreSlim _checkGate = new(1, 1);

    /// <summary>Poll fallback period: FileSystemWatcher can miss events (network drives, fast renames).</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(10);

    public event Action<string>? Logged;
    public event Action<WatchedDll, AssemblyDiffResult?>? BaselineCreated;
    public event Action<WatchedDll, AssemblyDiffResult>? DiffProduced;

    WatchManager() { }

    public void Add(string dllPath)
    {
        if (Watched.Any(w => string.Equals(w.Path, dllPath, StringComparison.OrdinalIgnoreCase)))
            return;

        var w = new WatchedDll { Path = System.IO.Path.GetFullPath(dllPath) };
        Watched.Add(w);

        StartWatcher(w);
        _ = Task.Run(() => CheckAsync(w));

        StartPollingIfNeeded();
        Log($"Watching {w.Path}");
    }

    public void Remove(WatchedDll w)
    {
        w.Watcher?.Dispose();
        w.Debounce?.Stop();
        Watched.Remove(w);
        Log($"Stopped watching {w.Path}");
    }

    /// <summary>Manual re-check (ignores debounce).</summary>
    public void RequestCheck(WatchedDll w) => _ = Task.Run(() => CheckAsync(w));

    /// <summary>Compares two arbitrary DLL files without touching the watch list or snapshots.</summary>
    public async Task<AssemblyDiffResult?> CompareTwoAsync(string oldPath, string newPath)
    {
        return await Task.Run(() =>
        {
            try
            {
                Log($"Manual compare: {oldPath} ↔ {newPath}");
                var newSnap = _decompiler.Build(newPath, Report);
                var oldSnap = _decompiler.Build(oldPath);
                var diff = _diff.Compare(oldSnap, newSnap, title: $"{Path.GetFileName(oldPath)} → {Path.GetFileName(newPath)}");
                if (diff.HasChanges)
                {
                    var report = SnapshotStore.WriteHtmlReport(diff);
                    Log($"{diff.Title}: +{diff.Added} −{diff.Removed} ~{diff.Modified} — report: {report}");
                }
                else
                    Log($"{diff.Title}: no type changes detected.");
                return diff;
            }
            catch (Exception ex)
            {
                Log($"Manual compare failed: {ex.Message}");
                return null;
            }
        });

        void Report(int done, int total) => Log($"Decompiling {done}/{total} types…");
    }

    void StartWatcher(WatchedDll w)
    {
        var dir = System.IO.Path.GetDirectoryName(w.Path)!;
        var file = System.IO.Path.GetFileName(w.Path);

        var fsw = new FileSystemWatcher(dir, file)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime | NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };
        fsw.Changed += (_, _) => QueueCheck(w);
        fsw.Created += (_, _) => QueueCheck(w);
        fsw.Renamed += (_, _) => QueueCheck(w);
        fsw.Error += (_, _) => { /* poll fallback covers buffer overflows */ };
        w.Watcher = fsw;

        w.Debounce = new System.Timers.Timer(900) { AutoReset = false };
        w.Debounce.Elapsed += (_, _) => _ = Task.Run(() => CheckAsync(w));
    }

    void QueueCheck(WatchedDll w)
    {
        var t = w.Debounce;
        if (t is null)
            return;
        t.Stop();
        t.Start();
    }

    System.Timers.Timer? _pollTimer;
    void StartPollingIfNeeded()
    {
        if (_pollTimer is not null)
            return;
        _pollTimer = new System.Timers.Timer(PollInterval.TotalMilliseconds) { AutoReset = true };
        _pollTimer.Elapsed += (_, _) =>
        {
            foreach (var w in Watched)
                _ = Task.Run(() => CheckAsync(w, quietWhenUnchanged: true));
        };
        _pollTimer.Start();
    }

    void CheckAsync(WatchedDll w, bool quietWhenUnchanged = false)
    {
        _checkGate.Wait();
        try
        {
            var data = TryReadAll(w.Path);
            if (data is null)
            {
                w.Status = "File locked / unreadable";
                return;
            }

            var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data));
            if (sha == w.ShaShort)
            {
                if (!quietWhenUnchanged)
                {
                    w.Status = "Up to date";
                    w.PillKind = "ok";
                    w.PillText = "UP TO DATE";
                }
                return;
            }

            w.Status = "Analyzing…";
            w.PillKind = "busy";
            w.PillText = "ANALYZING…";
            w.LastCheck = DateTime.Now.ToString("HH:mm:ss");

            var newSnap = _decompiler.Build(w.Path, (done, total) =>
                w.Status = $"Decompiling {done}/{total}…");
            w.AssemblyVersion = newSnap.AssemblyVersion;
            w.Meta1 = $"v{newSnap.AssemblyVersion} · {newSnap.Types.Count} types";
            w.ShaShort = sha;

            var oldSnap = SnapshotStore.Load(w.Path);
            if (oldSnap is null || oldSnap.Sha256 == newSnap.Sha256)
            {
                SnapshotStore.Save(newSnap);
                w.Status = $"Baseline saved (v{newSnap.AssemblyVersion})";
                w.PillKind = "baseline";
                w.PillText = "NEW BASELINE";
                w.ChangeSummary = "";
                BaselineCreated?.Invoke(w, null);
                Log($"Baseline snapshot saved for {w.Name} v{newSnap.AssemblyVersion} ({newSnap.Types.Count} types).");
                return;
            }

            var diff = _diff.Compare(oldSnap, newSnap);
            SnapshotStore.Save(newSnap);

            if (diff.HasChanges)
            {
                var report = SnapshotStore.WriteHtmlReport(diff);
                w.LastReportPath = report;
                Log($"{w.Name} UPDATED (v{oldSnap.AssemblyVersion} → v{newSnap.AssemblyVersion}): " +
                    $"+{diff.Added} added, −{diff.Removed} removed, ~{diff.Modified} modified. Report: {report}");
                w.Status = $"Changed! +{diff.Added} −{diff.Removed} ~{diff.Modified}";
                w.PillKind = "updated";
                w.PillText = "UPDATED";
                w.ChangeSummary = $"v{oldSnap.AssemblyVersion} → v{newSnap.AssemblyVersion} · +{diff.Added} added · −{diff.Removed} removed · ~{diff.Modified} modified";
                DiffProduced?.Invoke(w, diff);
            }
            else
            {
                Log($"{w.Name} changed on disk but decompiled API is identical.");
                w.Status = "Changed (identical decompile)";
                w.PillKind = "ok";
                w.PillText = "NO TYPE CHANGES";
                w.ChangeSummary = "";
            }
        }
        catch (Exception ex)
        {
            w.Status = "Error";
            w.PillKind = "error";
            w.PillText = "ERROR";
            Log($"Error checking {w.Name}: {ex.Message}");
        }
        finally
        {
            _checkGate.Release();
        }
    }

    static byte[]? TryReadAll(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var ms = new MemoryStream();
            fs.CopyTo(ms);
            return ms.ToArray();
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    void Log(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss}  {message}";
        Logged?.Invoke(line);
    }

    public void Dispose()
    {
        foreach (var w in Watched)
        {
            w.Watcher?.Dispose();
            w.Debounce?.Stop();
            w.Debounce?.Dispose();
        }
        _pollTimer?.Stop();
        _pollTimer?.Dispose();
        _checkGate.Dispose();
    }
}
