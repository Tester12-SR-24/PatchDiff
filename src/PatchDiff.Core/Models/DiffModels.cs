namespace PatchDiff.Models;

public enum ChangeKind
{
    None,
    Modified,
    Added,
    Removed,
}

/// <summary>A decompiled source file for one type inside an assembly snapshot.</summary>
public sealed class TypeSource
{
    public string FullName { get; set; } = "";
    public string Source { get; set; } = "";
}

/// <summary>A point-in-time decompilation of a whole assembly (all top-level types).</summary>
public sealed class AssemblySnapshot
{
    public string AssemblyPath { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string AssemblyVersion { get; set; } = "";
    public DateTime CapturedUtc { get; set; }
    public List<TypeSource> Types { get; set; } = new();
}

/// <summary>The diff of one type between two snapshots.</summary>
public sealed class TypeDiff
{
    public string FullName { get; set; } = "";
    public ChangeKind Change { get; set; }
    public string OldSource { get; set; } = "";
    public string NewSource { get; set; } = "";

    public string DisplayChange =>
        Change switch
        {
            ChangeKind.Added => "Added",
            ChangeKind.Removed => "Removed",
            ChangeKind.Modified => "Modified",
            _ => "Unchanged",
        };
}

public sealed class AssemblyDiffResult
{
    public string Title { get; set; } = "";
    public string OldSha { get; set; } = "";
    public string NewSha { get; set; } = "";
    public List<TypeDiff> TypeDiffs { get; set; } = new();

    public int Added => TypeDiffs.Count(d => d.Change == ChangeKind.Added);
    public int Removed => TypeDiffs.Count(d => d.Change == ChangeKind.Removed);
    public int Modified => TypeDiffs.Count(d => d.Change == ChangeKind.Modified);
    public bool HasChanges => Added + Removed + Modified > 0;
}
