using System.IO;
using System.Text.Json;

namespace PatchDiff.Services;

public sealed class StoredSettings
{
    public List<string> Watched { get; set; } = new();
    public int PollSeconds { get; set; } = 10;
    public bool BalloonOnPatch { get; set; } = true;

    /// <summary>When set, the app starts hidden in the system tray (tray icon opens it).
    /// Default OFF: a first launch must show the window, otherwise the app looks like
    /// it never started.</summary>
    public bool StartMinimized { get; set; }

    /// <summary>Closing the main window hides it to the tray instead of exiting.</summary>
    public bool CloseToTray { get; set; }

    /// <summary>When true (default), every analyzed version of a watched assembly is archived
    /// to revision history automatically. When false, only the baseline is kept current and
    /// versions must be saved explicitly (Save to History).</summary>
    public bool AutoSaveHistory { get; set; } = true;

    /// <summary>Optional redirect for snapshots/revisions/reports. Settings themselves stay
    /// in the default data root so the override can be changed back.</summary>
    public string DataFolder { get; set; } = "";

    public int ArchiveKeep { get; set; } = 20; // history kept until deletion: oldest pruned beyond this; 0 = keep all
}

public static class SettingsFile
{
    // Settings always live in the DEFAULT data root — the custom DataFolder override
    // redirects the stores, not the settings file, so the override stays changeable.
    static string FilePath => System.IO.Path.Combine(SnapshotStore.DefaultDataRoot, "settings.json");
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
