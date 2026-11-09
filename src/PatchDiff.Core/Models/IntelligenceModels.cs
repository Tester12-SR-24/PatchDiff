namespace PatchDiff.Models;

public sealed class RevisionInfo
{
    public string Sha256 { get; set; } = "";
    public string AssemblyVersion { get; set; } = "";
    public DateTime CapturedUtc { get; set; }
    public string DirName { get; set; } = "";
    public int TypeCount { get; set; }
}

public sealed class NumberChange
{
    public string Type { get; set; } = "";
    public string Member { get; set; } = "";
    public double OldValue { get; set; }
    public double NewValue { get; set; }

    public double PercentDelta =>
        OldValue == 0 ? (NewValue == 0 ? 0 : double.PositiveInfinity) : (NewValue - OldValue) / Math.Abs(OldValue) * 100;

    public string Direction => PercentDelta >= 0 ? "buff" : "nerf";
    public string OldText => Format(OldValue);
    public string NewText => Format(NewValue);

    static string Format(double v) =>
        v == Math.Floor(v) && Math.Abs(v) < 1e15 ? v.ToString("0") : v.ToString("0.######");
}

public sealed class PatchSummary
{
    public int Added { get; set; }
    public int Removed { get; set; }
    public int Modified { get; set; }

    /// <summary>Namespace → count of changed types, ordered by share.</summary>
    public List<(string Namespace, int Count, double Share)> Heat { get; set; } = new();

    public List<NumberChange> Numbers { get; set; } = new();

    /// <summary>Human-readable summary lines assembled from detected facts.</summary>
    public List<string> PlainTerms { get; set; } = new();
}

public sealed class BlameEntry
{
    public string Type { get; set; } = "";
    public string Member { get; set; } = "";
    /// <summary>added / removed / modified</summary>
    public string Change { get; set; } = "";
    public string RevisionSha { get; set; } = "";
    public string AssemblyVersion { get; set; } = "";
    public DateTime WhenUtc { get; set; }
    public string Signature { get; set; } = "";
}
