using System.Text.RegularExpressions;
using PatchDiff.Models;

namespace PatchDiff.Services;

/// <summary>
/// Deterministic analysis over an existing diff: numeric buff/nerf detection,
/// namespace heat map, and the plain-terms summary. No AI — pattern rules only.
/// </summary>
public static partial class DiffIntelligence
{
    // Matches member numeric assignments, e.g. "public float moveSpeed = 6.2f;" or "HpMax = 180;"
    [GeneratedRegex(
        @"^\s*(?:(?:public|private|protected|internal)\s+)?(?:static\s+)?(?:readonly\s+)?[\w\.<>\[\],\?]+\s+(\w+)\s*=\s*(-?\d+(?:\.\d+)?)f?\s*;",
        RegexOptions.Multiline | RegexOptions.Compiled)]
    private static partial Regex AssignmentRegex();

    public static PatchSummary Analyze(AssemblyDiffResult diff)
    {
        var summary = new PatchSummary
        {
            Added = diff.Added,
            Removed = diff.Removed,
            Modified = diff.Modified,
        };

        // --- namespace heat map ---
        var groups = diff.TypeDiffs
            .GroupBy(d =>
            {
                var dot = d.FullName.LastIndexOf('.');
                return dot < 0 ? "(global)" : d.FullName[..dot];
            })
            .Select(g => (Namespace: g.Key, Count: g.Count()))
            .OrderByDescending(g => g.Count)
            .ToList();
        var total = Math.Max(1, diff.TypeDiffs.Count);
        foreach (var g in groups)
            summary.Heat.Add((g.Namespace, g.Count, g.Count * 100.0 / total));

        // --- numeric changes across modified types ---
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var td in diff.TypeDiffs.Where(d => d.Change == ChangeKind.Modified))
        {
            var oldVals = ExtractNumbers(td.OldSource);
            var newVals = ExtractNumbers(td.NewSource);
            foreach (var (member, newVal) in newVals)
            {
                if (!oldVals.TryGetValue(member, out var oldVal) || oldVal == newVal)
                    continue;
                var key = td.FullName + "." + member;
                if (!seen.Add(key))
                    continue;
                summary.Numbers.Add(new NumberChange
                {
                    Type = td.FullName,
                    Member = member,
                    OldValue = oldVal,
                    NewValue = newVal,
                });
            }
        }
        summary.Numbers.Sort((a, b) => Math.Abs(b.PercentDelta).CompareTo(Math.Abs(a.PercentDelta)));

        // --- plain-terms summary from detected facts ---
        var terms = summary.PlainTerms;
        if (summary.Added > 0)
        {
            var names = diff.TypeDiffs.Where(d => d.Change == ChangeKind.Added)
                .Select(d => Short(d.FullName)).Take(4).ToList();
            terms.Add($"New systems — {string.Join(", ", names)}{(summary.Added > names.Count ? $" (+{summary.Added - names.Count} more)" : "")} added");
        }
        if (summary.Removed > 0)
        {
            var names = diff.TypeDiffs.Where(d => d.Change == ChangeKind.Removed)
                .Select(d => Short(d.FullName)).Take(4).ToList();
            terms.Add($"Removed — {string.Join(", ", names)}{(summary.Removed > names.Count ? $" (+{summary.Removed - names.Count} more)" : "")}");
        }
        if (summary.Numbers.Count > 0)
        {
            var families = summary.Numbers
                .Select(n => Family(n.Type))
                .GroupBy(f => f, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .Take(3)
                .Select(g => g.Key + " (" + g.Count() + ")")
                .ToList();
            terms.Add($"Numeric tuning — {summary.Numbers.Count} constants changed, mostly in {string.Join(", ", families)}");
        }
        if (summary.Heat.Count > 0)
            terms.Add("Most touched — " + string.Join(", ",
                summary.Heat.Take(3).Select(h => $"{Short(h.Namespace)} ({h.Share:0}% )")));

        return summary;
    }

    /// <summary>
    /// Member-level blame across a revision history (ordered oldest → newest).
    /// Reports when each member was added, removed, or last modified.
    /// </summary>
    public static List<BlameEntry> Blame(IReadOnlyList<AssemblySnapshot> history)
    {
        var entries = new List<BlameEntry>();
        if (history.Count < 2)
            return entries;

        Dictionary<string, string>? prev = null;
        AssemblySnapshot? prevSnap = null;

        for (int i = 0; i < history.Count; i++)
        {
            var cur = MembersOf(history[i]);
            if (prev is not null && prevSnap is not null)
            {
                var rev = history[i];
                foreach (var (key, sig) in cur)
                {
                    if (!prev.TryGetValue(key, out var oldSig))
                        entries.Add(Entry(rev, key, "added", sig));
                    else if (!string.Equals(oldSig, sig, StringComparison.Ordinal))
                        entries.Add(Entry(rev, key, "modified", sig));
                }
                foreach (var (key, sig) in prev)
                {
                    if (!cur.ContainsKey(key))
                        entries.Add(Entry(rev, key, "removed", sig));
                }
            }
            prev = cur;
            prevSnap = history[i];
        }
        entries.Sort((a, b) => b.WhenUtc.CompareTo(a.WhenUtc));
        return entries;
    }

    static BlameEntry Entry(AssemblySnapshot rev, string key, string change, string sig)
    {
        var split = key.IndexOf('|');
        return new BlameEntry
        {
            Type = key[..split],
            Member = key[(split + 1)..],
            Change = change,
            RevisionSha = rev.Sha256[..12],
            AssemblyVersion = rev.AssemblyVersion,
            WhenUtc = rev.CapturedUtc,
            Signature = sig,
        };
    }

    static Dictionary<string, string> MembersOf(AssemblySnapshot snap)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var t in snap.Types)
        {
            if (t.Signatures is null)
                continue; // legacy snapshots without member data are skipped
            foreach (var sig in t.Signatures)
                map[t.FullName + "|" + MemberName(sig)] = sig;
        }
        return map;
    }

    static string MemberName(string signature)
    {
        var trimmed = signature.Trim();
        var stop = trimmed.IndexOfAny(['(', ';', '{']);
        if (stop < 0)
            stop = trimmed.Length;
        var head = trimmed[..stop].TrimEnd();
        var parts = head.Split(' ');
        return parts.Length > 0 ? parts[^1] : trimmed;
    }

    static Dictionary<string, double> ExtractNumbers(string source)
    {
        var map = new Dictionary<string, double>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(source))
            return map;
        foreach (Match m in AssignmentRegex().Matches(source))
        {
            var name = m.Groups[1].Value;
            if (name.Length < 2 || name.StartsWith("_") || char.IsDigit(name[1]) && name[0] == '_')
                continue;
            if (double.TryParse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture, out var v))
                map[name] = v; // last assignment wins
        }
        return map;
    }

    static string Short(string fullName)
    {
        var name = fullName;
        var dot = name.LastIndexOf('.');
        if (dot >= 0)
            name = name[(dot + 1)..];
        var tick = name.IndexOf('`');
        if (tick > 0)
            name = name[..tick];
        return name;
    }

    static string Family(string fullName)
    {
        var name = Short(fullName);
        var underscore = name.IndexOf('_');
        return underscore > 0 ? name[..underscore] : name;
    }
}
