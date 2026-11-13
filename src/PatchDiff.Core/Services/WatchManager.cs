using System.Collections.ObjectModel;
using System.IO;
using PatchDiff.Models;

namespace PatchDiff.Services;

/// <summary>One watched DLL and its watcher plumbing.</summary>
public sealed class WatchedDll : System.ComponentModel.INotifyPropertyChanged
{
    public required string Path { get; init; }
    public string Name => System.IO.Path.GetFileName(Path);

    /// <summary>
    /// The game or mod this DLL belongs to, used to tell apart assemblies that share a
    /// file name (every Unity game ships an Assembly-CSharp.dll).
    ///
    /// Unity layout: &lt;game&gt;/&lt;game&gt;_Data/Managed/Assembly-CSharp.dll → "&lt;game&gt;".
    /// Anything else falls back to the folder two levels up, then the parent folder.
    /// </summary>
    public string Root
    {
        get
        {
            try
            {
                var dir = System.IO.Path.GetDirectoryName(Path);
                if (string.IsNullOrEmpty(dir))
                    return "";
                var managed = new System.IO.DirectoryInfo(dir);
                // .../<game>_Data/Managed → the game folder is the parent of <game>_Data
                if (managed.Name.Equals("Managed", StringComparison.OrdinalIgnoreCase) &&
                    managed.Parent is { } dataDir &&
                    dataDir.Name.EndsWith("_Data", StringComparison.OrdinalIgnoreCase))
                {
                    return dataDir.Parent?.Name ?? dataDir.Name;
                }

                // Otherwise walk up past build-output folders (bin/Debug/net8.0, libs,
                // plugins…) and take the first meaningful folder name.
                for (var d = managed; d is not null; d = d.Parent)
                {
                    if (d.Parent is null)
                        break; // drive root or filesystem root — nothing useful here
                    if (!IsGenericFolder(d.Name))
                        return d.Name;
                }
                return "";
            }
            catch { return ""; }
        }
    }

    static bool IsGenericFolder(string name) =>
        name.Equals("Managed", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Debug", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Release", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("x64", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("x86", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("AnyCPU", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("lib", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("libs", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("plugins", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("runtimes", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("net", StringComparison.OrdinalIgnoreCase) && name.Length > 3 && char.IsDigit(name[3]);

    /// <summary>"Assembly-CSharp.dll (ATLYSS)" — the name plus where it came from.</summary>
    public string DisplayName => string.IsNullOrEmpty(Root) ? Name : $"{Name} ({Root})";
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
    readonly RevisionStore _revisions = new();
    public int ArchiveKeep { get; set; } // 0 = keep all revisions

    /// <summary>When true (default), every analyzed version is archived to revision history
    /// automatically. When false, only the baseline snapshot is kept current — versions are
    /// saved explicitly via SaveToHistory (DiffView's Save to History button).</summary>
    public bool AutoSaveHistory { get; set; } = true;
    readonly SemaphoreSlim _checkGate = new(1, 1);
    bool _disposed;

    /// <summary>Poll fallback period: FileSystemWatcher can miss events (network drives, fast renames).</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(10);

    public event Action<string>? Logged;
    public event Action<WatchedDll, AssemblyDiffResult?>? BaselineCreated;
    public event Action<WatchedDll, AssemblyDiffResult>? DiffProduced;

    WatchManager() { }

    public void Add(string dllPath)
    {
        var fullPath = System.IO.Path.GetFullPath(dllPath);
        if (Watched.Any(w => string.Equals(w.Path, fullPath, StringComparison.OrdinalIgnoreCase)))
            return;

        var w = new WatchedDll { Path = fullPath };
        Watched.Add(w);

        try
        {
            StartWatcher(w);
        }
        catch (Exception ex)
        {
            // A stale watch can point into a removed directory. Do not let watcher
            // creation abort app startup; polling still detects the file if it returns.
            Log($"File watcher unavailable for {w.Path}; polling fallback remains active: {ex.Message}");
        }
        _ = Task.Run(() => CheckAsync(w));

        StartPollingIfNeeded();
        Log($"Watching {w.Path}");
    }

    public void Remove(WatchedDll w, bool purgeData = false)
    {
        w.Watcher?.Dispose();
        w.Debounce?.Stop();
        w.Debounce?.Dispose();
        Watched.Remove(w);
        if (purgeData)
        {
            try
            {
                SnapshotStore.Delete(w.Path);
                Log($"Deleted baseline for {w.DisplayName}.");
            }
            catch (Exception ex)
            {
                Log($"Could not delete baseline for {w.DisplayName}: {ex.Message}");
            }
        }
        Log($"Stopped watching {w.Path}");
    }

    /// <summary>Manual re-check (ignores debounce).</summary>
    public void RequestCheck(WatchedDll w) => _ = Task.Run(() => CheckAsync(w));

    /// <summary>Archives the current file as a revision in its history — used by the GUI's
    /// "Save to History" button for manual compares, or when auto-save history is off.</summary>
    public void SaveToHistory(string dllPath)
    {
        _ = Task.Run(() =>
        {
            try
            {
                if (!File.Exists(dllPath))
                {
                    Log($"Could not save to history — file not found: {dllPath}");
                    return;
                }
                var snap = _decompiler.Build(dllPath);
                var bytes = File.ReadAllBytes(dllPath);
                _revisions.Append(snap, bytes, ArchiveKeep);
                Log($"{Path.GetFileName(dllPath)} saved to revision history ({snap.Sha256[..8]}, v{snap.AssemblyVersion}, {snap.Types.Count} types).");
            }
            catch (Exception ex)
            {
                Log($"Could not save to history: {ex.Message}");
            }
        });
    }

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
        };
        var debounce = new System.Timers.Timer(900) { AutoReset = false };
        fsw.Changed += (_, _) => QueueCheck(w);
        fsw.Created += (_, _) => QueueCheck(w);
        fsw.Renamed += (_, _) => QueueCheck(w);
        fsw.Deleted += (_, _) => QueueCheck(w);
        fsw.Error += (_, _) => { /* poll fallback covers buffer overflows */ };
        debounce.Elapsed += (_, _) => _ = Task.Run(() => CheckAsync(w));

        try
        {
            fsw.EnableRaisingEvents = true;
            w.Watcher = fsw;
            w.Debounce = debounce;
        }
        catch
        {
            fsw.Dispose();
            debounce.Dispose();
            throw;
        }
    }

    void QueueCheck(WatchedDll w)
    {
        var t = w.Debounce;
        if (t is null)
            return;
        try
        {
            t.Stop();
            t.Start();
        }
        catch (ObjectDisposedException)
        {
            // Remove()/Dispose() raced an in-flight watcher event; the watch is gone,
            // so there is nothing left to debounce.
        }
    }

    System.Timers.Timer? _pollTimer;
    /// <summary>Re-arms the poll timer after PollInterval changes (settings dialog).</summary>
    public void UpdatePollInterval()
    {
        if (_pollTimer is not null)
            _pollTimer.Interval = PollInterval.TotalMilliseconds;
    }

    void StartPollingIfNeeded()
    {
        if (_pollTimer is not null)
            return;
        _pollTimer = new System.Timers.Timer(PollInterval.TotalMilliseconds) { AutoReset = true };
        _pollTimer.Elapsed += (_, _) =>
        {
            // Snapshot: this runs on a timer thread while the UI thread can Add/Remove,
            // and ObservableCollection does not tolerate concurrent mutation.
            foreach (var w in Watched.ToArray())
                _ = Task.Run(() => CheckAsync(w, quietWhenUnchanged: true));
        };
        _pollTimer.Start();
    }

    void CheckAsync(WatchedDll w, bool quietWhenUnchanged = false)
    {
        if (_disposed)
            return; // manager disposed while this check sat queued
        try
        {
            _checkGate.Wait();
        }
        catch (ObjectDisposedException)
        {
            return; // Dispose() ran between the flag check and the wait
        }
        try
        {
            if (!Watched.Contains(w))
                return; // removed while an analysis was queued/in flight

            var data = TryReadAll(w.Path);
            if (data is null)
            {
                if (!File.Exists(w.Path))
                {
                    w.Status = "File missing";
                    w.PillKind = "error";
                    w.PillText = "MISSING";
                    Log($"{w.DisplayName} no longer exists at {w.Path} — watch kept, awaiting the file to return.");
                }
                else
                {
                    w.Status = "File locked / unreadable";
                }
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

            if (!File.Exists(w.Path))
                return; // file vanished between the read and now — the next poll/report surfaces it

            // Fast path: file identical to the stored baseline (e.g. app restart,
            // PC reboot) — no decompilation needed.
            var oldSnap = SnapshotStore.Load(w.Path);
            if (oldSnap is not null && oldSnap.Sha256 == sha && oldSnap.SchemaVersion >= AssemblySnapshot.CurrentSchemaVersion)
            {
                w.ShaShort = sha;
                w.AssemblyVersion = oldSnap.AssemblyVersion;
                w.Meta1 = $"v{oldSnap.AssemblyVersion} · {oldSnap.Types.Count} types";
                w.Status = "Up to date";
                w.PillKind = "ok";
                w.PillText = "UP TO DATE";
                w.LastCheck = DateTime.Now.ToString("HH:mm:ss");
                return;
            }

            w.Status = "Analyzing…";
            w.PillKind = "busy";
            w.PillText = "ANALYZING…";
            w.LastCheck = DateTime.Now.ToString("HH:mm:ss");

            var (newSnap, newBytes) = _decompiler.BuildWithBytes(w.Path, (done, total) =>
                w.Status = $"Decompiling {done}/{total}…");
            w.AssemblyVersion = newSnap.AssemblyVersion;
            w.Meta1 = $"v{newSnap.AssemblyVersion} · {newSnap.Types.Count} types";
            w.ShaShort = sha;

            // Same file, but the baseline predates the current snapshot schema
            // (e.g. no member signatures) — silently upgrade the baseline.
            if (oldSnap is not null && oldSnap.Sha256 == newSnap.Sha256)
            {
                if (AutoSaveHistory)
                    _revisions.Append(newSnap, newBytes, ArchiveKeep);
                SnapshotStore.Save(newSnap);
                w.Status = "Baseline upgraded";
                w.PillKind = "ok";
                w.PillText = "UP TO DATE";
                Log($"Upgraded baseline snapshot for {w.DisplayName} to the current format (member view now available).");
                return;
            }

            if (oldSnap is null)
            {
                if (AutoSaveHistory)
                    _revisions.Append(newSnap, newBytes, ArchiveKeep);
                SnapshotStore.Save(newSnap);
                w.Status = $"Baseline saved (v{newSnap.AssemblyVersion})";
                w.PillKind = "baseline";
                w.PillText = "NEW BASELINE";
                w.ChangeSummary = "";
                BaselineCreated?.Invoke(w, null);
                Log($"Baseline snapshot saved for {w.DisplayName} v{newSnap.AssemblyVersion} ({newSnap.Types.Count} types).");
                return;
            }

            var diff = _diff.Compare(oldSnap, newSnap);
            if (AutoSaveHistory)
                _revisions.Append(newSnap, newBytes, ArchiveKeep);
            SnapshotStore.Save(newSnap);

            if (diff.HasChanges)
            {
                var report = SnapshotStore.WriteHtmlReport(diff);
                w.LastReportPath = report;
                Log($"{w.DisplayName} UPDATED (v{oldSnap.AssemblyVersion} → v{newSnap.AssemblyVersion}): " +
                    $"+{diff.Added} added, −{diff.Removed} removed, ~{diff.Modified} modified. Report: {report}");
                w.Status = $"Changed! +{diff.Added} −{diff.Removed} ~{diff.Modified}";
                w.PillKind = "updated";
                w.PillText = "UPDATED";
                w.ChangeSummary = oldSnap.AssemblyVersion == newSnap.AssemblyVersion
                    ? $"{diff.Added + diff.Removed + diff.Modified} type changes · +{diff.Added} added · −{diff.Removed} removed · ~{diff.Modified} modified"
                    : $"v{oldSnap.AssemblyVersion} → v{newSnap.AssemblyVersion} · +{diff.Added} added · −{diff.Removed} removed · ~{diff.Modified} modified";
                DiffProduced?.Invoke(w, diff);
            }
            else
            {
                Log($"{w.DisplayName} changed on disk but decompiled API is identical.");
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
            Log($"Error checking {w.DisplayName}: {ex.Message}");
        }
        finally
        {
            try
            {
                _checkGate.Release();
            }
            catch (ObjectDisposedException)
            {
                // Dispose() ran while this check was still executing.
            }
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
        _disposed = true;
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
