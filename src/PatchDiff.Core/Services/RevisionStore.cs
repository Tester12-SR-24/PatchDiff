using System.IO;
using System.Text.Json;
using PatchDiff.Models;

namespace PatchDiff.Services;

/// <summary>
/// Keeps EVERY analyzed version of a watched DLL: the full decompiled snapshot
/// plus a copy of the DLL binary itself, so any revision can be diffed or restored.
/// Layout: DataRoot/Revisions/&lt;dllKey&gt;/index.json, /&lt;revDir&gt;/manifest.json, /Archive/&lt;dllKey&gt;/&lt;revDir&gt;.dll
/// </summary>
public sealed class RevisionStore
{
    static string KeyFor(string dllPath) => SnapshotStore.KeyNameFor(dllPath);
    static string RootFor(string dllPath) => Path.Combine(SnapshotStore.DataRoot, "Revisions", KeyFor(dllPath));
    static string ArchiveDirFor(string dllPath) => Path.Combine(SnapshotStore.DataRoot, "Archive", KeyFor(dllPath));

    static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    static string IndexFile(string dllPath) => Path.Combine(RootFor(dllPath), "index.json");
    static string LockFile(string dllPath) => Path.Combine(RootFor(dllPath), "index.lock");

    /// <summary>
    /// Cross-process lock around the index.json read-modify-write. The GUI and the CLI
    /// can watch the same assembly against one data folder; without this, a concurrent
    /// Append from one process can silently drop the revision recorded by the other.
    /// Uses a sibling .lock file opened with FileShare.None (portable, no named mutex).
    /// </summary>
    static IDisposable AcquireIndexLock(string dllPath, int timeoutMs = 5000)
    {
        var lockPath = LockFile(dllPath);
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        var deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                    bufferSize: 1, FileOptions.DeleteOnClose);
            }
            catch (IOException) when (Environment.TickCount64 < deadline)
            {
                Thread.Sleep(40); // another process holds the index — wait and retry
            }
            catch
            {
                // Locking is best-effort: never fail an archive because a stale lock exists.
                return Stream.Null;
            }
        }
    }

    public List<RevisionInfo> List(string dllPath)
    {
        try
        {
            var file = IndexFile(dllPath);
            if (!File.Exists(file))
                return new List<RevisionInfo>();
            // Retry reads that collide with a concurrent writer instead of returning empty
            // (an empty list would make the caller believe no history exists).
            for (var attempt = 0; attempt < 12; attempt++)
            {
                try
                {
                    return JsonSerializer.Deserialize<List<RevisionInfo>>(File.ReadAllText(file))
                        ?? new List<RevisionInfo>();
                }
                catch (IOException) { Thread.Sleep(30); }
            }
            return new List<RevisionInfo>();
        }
        catch
        {
            return new List<RevisionInfo>();
        }
    }

    /// <summary>Archives the snapshot manifest + the DLL bytes as a new revision.</summary>
    public void Append(AssemblySnapshot snapshot, byte[] dllBytes, int keepRevisions = 0)
    {
        var revDirName = $"{snapshot.Sha256[..8]}_{snapshot.CapturedUtc:yyyyMMdd_HHmmss}";
        var revDir = Path.Combine(RootFor(snapshot.AssemblyPath), revDirName);
        Directory.CreateDirectory(revDir);

        File.WriteAllText(Path.Combine(revDir, "manifest.json"),
            JsonSerializer.Serialize(snapshot, JsonOpts));

        var archiveDir = ArchiveDirFor(snapshot.AssemblyPath);
        Directory.CreateDirectory(archiveDir);
        var dllPath = Path.Combine(archiveDir, revDirName + ".dll");
        if (!File.Exists(dllPath))
            File.WriteAllBytes(dllPath, dllBytes);

        // Serialize the read-modify-write of index.json across processes (GUI + CLI).
        using var indexLock = AcquireIndexLock(snapshot.AssemblyPath);
        var list = List(snapshot.AssemblyPath);
        list.RemoveAll(r => r.Sha256 == snapshot.Sha256); // re-analysis of same file: refresh entry
        list.Add(new RevisionInfo
        {
            Sha256 = snapshot.Sha256,
            AssemblyVersion = snapshot.AssemblyVersion,
            CapturedUtc = snapshot.CapturedUtc,
            DirName = revDirName,
            TypeCount = snapshot.Types.Count,
        });
        list.Sort((a, b) => a.CapturedUtc.CompareTo(b.CapturedUtc));
        if (keepRevisions > 0 && list.Count > keepRevisions)
        {
            var drop = list.Take(list.Count - keepRevisions).ToList();
            list = list.Skip(list.Count - keepRevisions).ToList();
            foreach (var d in drop)
            {
                try
                {
                    var dd = Path.Combine(RootFor(snapshot.AssemblyPath), d.DirName);
                    if (Directory.Exists(dd)) Directory.Delete(dd, true);
                    var ad = Path.Combine(ArchiveDirFor(snapshot.AssemblyPath), d.DirName + ".dll");
                    if (File.Exists(ad)) File.Delete(ad);
                }
                catch { /* best effort */ }
            }
        }
        File.WriteAllText(IndexFile(snapshot.AssemblyPath), JsonSerializer.Serialize(list, JsonOpts));
    }

    public AssemblySnapshot? LoadRevision(string dllPath, string sha)
    {
        var info = List(dllPath).FirstOrDefault(r => r.Sha256.StartsWith(sha, StringComparison.OrdinalIgnoreCase));
        if (info is null)
            return null;
        var manifest = Path.Combine(RootFor(dllPath), info.DirName, "manifest.json");
        try
        {
            return File.Exists(manifest)
                ? JsonSerializer.Deserialize<AssemblySnapshot>(File.ReadAllText(manifest))
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Restores an archived DLL to an arbitrary destination path.</summary>
    public string Restore(string dllPath, string sha, string? destination = null)
    {
        var info = List(dllPath).FirstOrDefault(r => r.Sha256.StartsWith(sha, StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException("Revision not found: " + sha);
        var src = Path.Combine(ArchiveDirFor(dllPath), info.DirName + ".dll");
        if (!File.Exists(src))
            throw new FileNotFoundException("Archived DLL missing: " + src);
        destination ??= Path.Combine(
            Path.GetDirectoryName(dllPath)!,
            Path.GetFileNameWithoutExtension(dllPath) + "_" + info.DirName + Path.GetExtension(dllPath));
        File.Copy(src, destination, overwrite: true);
        return destination;
    }

    public string? ArchivedDllPath(string dllPath, string sha)
    {
        var info = List(dllPath).FirstOrDefault(r => r.Sha256.StartsWith(sha, StringComparison.OrdinalIgnoreCase));
        if (info is null)
            return null;
        var p = Path.Combine(ArchiveDirFor(dllPath), info.DirName + ".dll");
        return File.Exists(p) ? p : null;
    }
}
