using System.IO;
using System.Text.Json;

namespace PatchDiff.Services;

public sealed class StoredSettings
{
    public List<string> Watched { get; set; } = new();
    public int PollSeconds { get; set; } = 10;
    public bool BalloonOnPatch { get; set; } = true;
    public bool StartMinimized { get; set; }
    public int ArchiveKeep { get; set; } // 0 = keep all
}

public static class SettingsFile
{
    static string FilePath => System.IO.Path.Combine(SnapshotStore.DataRoot, "settings.json");
    static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static StoredSettings Load()
    {
        try
        {
            if (!System.IO.File.Exists(FilePath))
                return new StoredSettings();

            var doc = JsonDocument.Parse(System.IO.File.ReadAllText(FilePath));
            var root = doc.RootElement;

            var settings = new StoredSettings();
            if (root.TryGetProperty("watched", out var watched) && watched.ValueKind == JsonValueKind.Array)
                settings.Watched = watched.EnumerateArray().Select(v => v.GetString() ?? "").Where(s => s.Length > 0).ToList();

            if (root.TryGetProperty("settings", out var s) && s.ValueKind == JsonValueKind.Object)
            {
                if (s.TryGetProperty("pollSeconds", out var poll)) settings.PollSeconds = poll.GetInt32();
                if (s.TryGetProperty("balloonOnPatch", out var bal)) settings.BalloonOnPatch = bal.GetBoolean();
                if (s.TryGetProperty("startMinimized", out var min)) settings.StartMinimized = min.GetBoolean();
                if (s.TryGetProperty("archiveKeep", out var keep)) settings.ArchiveKeep = keep.GetInt32();
            }
            return settings;
        }
        catch
        {
            return new StoredSettings();
        }
    }

    public static void Save(StoredSettings settings)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
            System.IO.File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, JsonOpts));
        }
        catch { /* non-fatal */ }
    }
}
