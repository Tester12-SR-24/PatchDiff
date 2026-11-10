using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PatchDiff.Models;

namespace PatchDiff.Services;

/// <summary>
/// Persists one baseline snapshot per watched DLL under
/// %LOCALAPPDATA%\PatchDiff\Snapshots\&lt;key&gt;\manifest.json
/// </summary>
public sealed class SnapshotStore
{
    /// <summary>Data root; PATCHDIFF_DATA_DIR overrides it (used for portable/demo setups).</summary>
    public static string DataRoot =>
        Environment.GetEnvironmentVariable("PATCHDIFF_DATA_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PatchDiff");

    static readonly string Root = Path.Combine(DataRoot, "Snapshots");

    public static readonly string ReportsRoot = Path.Combine(DataRoot, "Reports");

    static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static string KeyNameFor(string dllPath)
    {
        var normalized = Path.GetFullPath(dllPath).ToLowerInvariant();
        return Convert.ToHexString(SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(normalized)))[..16];
    }

    public static string FolderFor(string dllPath) => Path.Combine(Root, KeyNameFor(dllPath));

    public static AssemblySnapshot? Load(string dllPath)
    {
        var manifest = Path.Combine(FolderFor(dllPath), "manifest.json");
        if (!File.Exists(manifest))
            return null;
        try
        {
            return JsonSerializer.Deserialize<AssemblySnapshot>(File.ReadAllText(manifest), JsonOpts);
        }
        catch
        {
            return null;
        }
    }

    public static void Save(AssemblySnapshot snapshot)
    {
        var dir = FolderFor(snapshot.AssemblyPath);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "manifest.json"),
            JsonSerializer.Serialize(snapshot, JsonOpts));
    }

    public static void Delete(string dllPath)
    {
        var dir = FolderFor(dllPath);
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }

    public static string WriteHtmlReport(AssemblyDiffResult diff)
    {
        Directory.CreateDirectory(ReportsRoot);
        var safe = string.Concat(diff.Title
            .Replace('→', '-')
            .Where(c => !Path.GetInvalidFileNameChars().Contains(c)));
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var path = Path.Combine(ReportsRoot, $"{safe}_{stamp}.html");
        WriteHtmlReportTo(diff, path);
        return path;
    }

    public static void WriteHtmlReportTo(AssemblyDiffResult diff, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var sb = new StringBuilder();
        sb.Append("""
<!DOCTYPE html><html><head><meta charset="utf-8"><title>
""");
        sb.Append(Escape(diff.Title));
        sb.Append("</title><style>");
        sb.Append("""
body{font-family:'Cascadia Code',Consolas,monospace;margin:16px;background:#fafbfc;color:#24292f}
h2{font-family:Segoe UI,sans-serif}
.summary{font-family:Segoe UI,sans-serif;color:#57606a}
.toolbar{font-family:Segoe UI,sans-serif;margin:0 0 10px}
.toolbar button{font:inherit;padding:4px 10px;margin-right:6px;cursor:pointer}
details.type{border:1px solid #e1e4e8;border-radius:6px;margin:10px 0;background:#fff}
details.type>summary{cursor:pointer;list-style:none;padding:8px 12px;font-weight:600;
  font-family:Segoe UI,sans-serif;display:flex;align-items:center;gap:10px;flex-wrap:wrap}
details.type>summary::-webkit-details-marker{display:none}
details.type>summary:hover{background:#f6f8fa}
.arrow{display:inline-block;transition:transform .15s;font-size:11px;color:#57606a}
details[open]>summary .arrow{transform:rotate(90deg)}
.badge{font-size:11px;font-weight:500;padding:1px 8px;border-radius:9px;font-family:Segoe UI,sans-serif}
.b-add{background:#d4f8d4;color:#116329}.b-rem{background:#ffdce0;color:#cf222e}
.b-mod{background:#fff3cd;color:#7d5b00}.b-same{background:#eaeef2;color:#57606a}.mono{font-family:Consolas,monospace}
table{border-collapse:collapse;width:100%;font-size:12px;margin-top:2px}
td{vertical-align:top;padding:1px 6px;white-space:pre-wrap;width:50%;border:1px solid #e1e4e8}
td.o{background:#ffeef0;border-left:4px solid #f66a0a}
td.n{background:#e6ffed;border-left:4px solid #28a745}
td.same{background:#fff}
""");
        sb.Append("</style></head><body>");
        sb.Append($"<h2>{Escape(diff.Title)}</h2>");
        sb.Append($"<p class=\"summary\">+{diff.Added} added &nbsp; −{diff.Removed} removed &nbsp; ~{diff.Modified} modified</p>");
        sb.Append("<p class=\"toolbar\"><button onclick=\"document.querySelectorAll('details.type').forEach(d=>d.open=true)\">Expand all</button>"
                  + "<button onclick=\"document.querySelectorAll('details.type').forEach(d=>d.open=false)\">Collapse all</button></p>");

        foreach (var td in diff.TypeDiffs.Where(d => d.Change != ChangeKind.None))
        {
            var (addLines, remLines, modLines) = LineCounts(td);
            sb.Append("<details class=\"type\">");
            sb.Append($"<summary><span class=\"arrow\">▶</span>{Escape(td.FullName)}");
            if (addLines > 0) sb.Append($"<span class=\"badge b-add\">+{addLines}</span>");
            if (remLines > 0) sb.Append($"<span class=\"badge b-rem\">−{remLines}</span>");
            if (modLines > 0) sb.Append($"<span class=\"badge b-mod\">~{modLines}</span>");
            if (addLines + remLines + modLines == 0)
                sb.Append("<span class=\"badge b-same\">whitespace only</span>");
            sb.Append($"<span class=\"badge b-same\">{td.DisplayChange}</span>");
            sb.Append("</summary><table>");
            foreach (var r in DiffService.SideBySide(td))
            {
                sb.Append("<tr>");
                sb.Append($"<td class=\"{CellClass(r.Old, true)}\">{Escape(r.Old?.Text)}</td>");
                sb.Append($"<td class=\"{CellClass(r.New, false)}\">{Escape(r.New?.Text)}</td>");
                sb.Append("</tr>");
            }
            sb.Append("</table></details>");
        }
        sb.Append("</body></html>");
        File.WriteAllText(path, sb.ToString());

        static (int added, int removed, int modified) LineCounts(Models.TypeDiff td)
        {
            int added = 0, removed = 0, modified = 0;
            foreach (var r in DiffService.SideBySide(td))
            {
                var ot = r.Old?.Type.ToString();
                var nt = r.New?.Type.ToString();
                if (ot == "Deleted") removed++;
                if (nt == "Inserted") added++;
                if (ot == "Modified" || nt == "Modified") modified++;
            }
            return (added, removed, Math.Max(0, modified - added));
        }

        static string CellClass(DiffPlex.DiffBuilder.Model.DiffPiece? piece, bool isOld) =>
            piece?.Type switch
            {
                DiffPlex.DiffBuilder.Model.ChangeType.Deleted => "o",
                DiffPlex.DiffBuilder.Model.ChangeType.Inserted => "n",
                DiffPlex.DiffBuilder.Model.ChangeType.Modified => isOld ? "o" : "n",
                DiffPlex.DiffBuilder.Model.ChangeType.Unchanged => "same",
                _ => "same",
            };

        static string Escape(string? s) => s is null ? "" :
            s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    }
}
