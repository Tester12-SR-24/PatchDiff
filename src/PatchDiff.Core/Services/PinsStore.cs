using System.IO;
using System.Text.Json;

namespace PatchDiff.Services;

public sealed class PinEntry
{
    public string AssemblyPath { get; set; } = "";
    public string TypeName { get; set; } = "";
    public string Note { get; set; } = "";
    public DateTime PinnedUtc { get; set; }
}

/// <summary>Persisted per-type pins and notes (Workstation concept).</summary>
public sealed class PinsStore
{
    static string File => Path.Combine(SnapshotStore.DataRoot, "pins.json");
    static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public List<PinEntry> List()
    {
        try
        {
            if (!System.IO.File.Exists(File))
                return new List<PinEntry>();
            return JsonSerializer.Deserialize<List<PinEntry>>(System.IO.File.ReadAllText(File)) ?? new();
        }
        catch
        {
            return new List<PinEntry>();
        }
    }

    public void Save(List<PinEntry> pins)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(File)!);
        System.IO.File.WriteAllText(File, JsonSerializer.Serialize(pins, JsonOpts));
    }

    public void AddOrUpdate(string assemblyPath, string typeName, string note)
    {
        var pins = List();
        var existing = pins.FirstOrDefault(p =>
            string.Equals(p.TypeName, typeName, StringComparison.Ordinal) &&
            string.Equals(p.AssemblyPath, assemblyPath, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
            existing.Note = note;
        else
            pins.Add(new PinEntry { AssemblyPath = assemblyPath, TypeName = typeName, Note = note, PinnedUtc = DateTime.UtcNow });
        Save(pins);
    }

    public void Remove(string assemblyPath, string typeName)
    {
        var pins = List();
        pins.RemoveAll(p =>
            string.Equals(p.TypeName, typeName, StringComparison.Ordinal) &&
            string.Equals(p.AssemblyPath, assemblyPath, StringComparison.OrdinalIgnoreCase));
        Save(pins);
    }
}
