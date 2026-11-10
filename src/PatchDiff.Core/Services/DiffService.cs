using System.IO;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using PatchDiff.Models;

namespace PatchDiff.Services;

public sealed class DiffRow
{
    public DiffPiece? Old { get; init; }
    public DiffPiece? New { get; init; }
}

public sealed class DiffService
{
    static readonly SideBySideDiffBuilder Builder = SideBySideDiffBuilder.Instance;

    public AssemblyDiffResult Compare(AssemblySnapshot oldSnap, AssemblySnapshot newSnap, string? title = null)
    {
        var oldMap = oldSnap.Types
            .GroupBy(t => t.FullName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Source, StringComparer.Ordinal);
        var newMap = newSnap.Types
            .GroupBy(t => t.FullName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Source, StringComparer.Ordinal);

        var result = new AssemblyDiffResult
        {
            Title = title ?? Path.GetFileName(newSnap.AssemblyPath),
            OldSha = oldSnap.Sha256[..12],
            NewSha = newSnap.Sha256[..12],
        };

        foreach (var name in oldMap.Keys.Union(newMap.Keys).OrderBy(n => n, StringComparer.Ordinal))
        {
            var hasOld = oldMap.TryGetValue(name, out var oldSrc);
            var hasNew = newMap.TryGetValue(name, out var newSrc);

            var td = new TypeDiff { FullName = name, OldSource = oldSrc ?? "", NewSource = newSrc ?? "" };
            td.Change = (hasOld, hasNew) switch
            {
                (true, false) => ChangeKind.Removed,
                (false, true) => ChangeKind.Added,
                _ => oldSrc == newSrc ? ChangeKind.None : ChangeKind.Modified,
            };
            if (td.Change != ChangeKind.None)
                result.TypeDiffs.Add(td);
        }
        return result;
    }

    public static List<DiffRow> SideBySide(TypeDiff td)
    {
        var model = Builder.BuildDiffModel(
            td.Change == ChangeKind.Added ? "" : td.OldSource,
            td.Change == ChangeKind.Removed ? "" : td.NewSource);

        var oldLines = model.OldText.Lines;
        var newLines = model.NewText.Lines;
        var rows = new List<DiffRow>(Math.Max(oldLines.Count, newLines.Count));
        for (int i = 0; i < Math.Max(oldLines.Count, newLines.Count); i++)
            rows.Add(new DiffRow
            {
                Old = i < oldLines.Count ? oldLines[i] : null,
                New = i < newLines.Count ? newLines[i] : null,
            });
        return rows;
    }
}
