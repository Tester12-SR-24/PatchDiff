using System.IO;
using System.Text.Json;

namespace PatchDiff.Services;

/// <summary>User-nameable labels for revisions, persisted per DataRoot (keyed by revision sha).</summary>
public sealed class LabelsStore
{
    static readonly object FileLock = new();
    static string FilePath => Path.Combine(SnapshotStore.DataRoot, "revision-labels.json");
    static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public string GetLabel(string dllPath, string sha)
    {
        lock (FileLock)
        {
            var map = Load(dllPath);
            return map.TryGetValue(sha, out var label) ? label : "";
        }
    }

    public void SetLabel(string dllPath, string sha, string label)
    {
        lock (FileLock)
        {
            var map = Load(dllPath);
            if (string.IsNullOrWhiteSpace(label))
                map.Remove(sha);
            else
                map[sha] = label.Trim();
            Save(dllPath, map);
        }
    }

    Dictionary<string, string> Load(string dllPath)
    {
        try
        {
            var file = FilePathFor(dllPath);
            if (!System.IO.File.Exists(file))
                return new Dictionary<string, string>(StringComparer.Ordinal);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(
                System.IO.File.ReadAllText(file), JsonOpts) ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    void Save(string dllPath, Dictionary<string, string> map)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePathFor(dllPath))!);
        System.IO.File.WriteAllText(FilePathFor(dllPath), JsonSerializer.Serialize(map, JsonOpts));
    }

    // Labels are scoped per DLL: key file by the same 16-hex key SnapshotStore uses.
    static string FilePathFor(string dllPath) =>
        Path.Combine(SnapshotStore.DataRoot, "Labels", SnapshotStore.KeyNameFor(dllPath) + ".json");
}
