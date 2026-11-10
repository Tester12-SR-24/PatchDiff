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
    static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public static StoredSettings Load()
    {
        try
        {
            if (!System.IO.File.Exists(FilePath))
                return new StoredSettings();

            var json = System.IO.File.ReadAllText(FilePath);

            // Current format: flat StoredSettings as written by Save().
            var settings = JsonSerializer.Deserialize<StoredSettings>(json, JsonOpts) ?? new StoredSettings();
            settings.Watched = (settings.Watched ?? new()).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();

            // Legacy format: { "watched": [...], "settings": { "pollSeconds": .. } }
            // Guarded so a non-object root or wrong-typed value can't throw away the flat settings above.
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("settings", out var s) && s.ValueKind == JsonValueKind.Object)
                {
                    if (s.TryGetProperty("pollSeconds", out var poll) && poll.TryGetInt32(out var pollVal)) settings.PollSeconds = pollVal;
                    if (s.TryGetProperty("balloonOnPatch", out var bal) && (bal.ValueKind == JsonValueKind.True || bal.ValueKind == JsonValueKind.False)) settings.BalloonOnPatch = bal.GetBoolean();
                    if (s.TryGetProperty("startMinimized", out var min) && (min.ValueKind == JsonValueKind.True || min.ValueKind == JsonValueKind.False)) settings.StartMinimized = min.GetBoolean();
                    if (s.TryGetProperty("archiveKeep", out var keep) && keep.TryGetInt32(out var keepVal)) settings.ArchiveKeep = keepVal;
                }
            }
            catch { /* legacy block is best-effort */ }

            if (settings.PollSeconds < 1) settings.PollSeconds = 10;
            if (settings.ArchiveKeep < 0) settings.ArchiveKeep = 0;
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
