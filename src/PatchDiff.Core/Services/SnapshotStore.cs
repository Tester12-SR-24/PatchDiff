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
    static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PatchDiff", "Snapshots");

    public static readonly string ReportsRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PatchDiff", "Reports");

    static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static string FolderFor(string dllPath)
    {
        var normalized = Path.GetFullPath(dllPath).ToLowerInvariant();
        var key = Convert.ToHexString(SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(normalized)))[..16];
        return Path.Combine(Root, key);
    }

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
h3{margin:18px 0 4px;font-family:Segoe UI,sans-serif}
table{border-collapse:collapse;width:100%;font-size:12px}
td{vertical-align:top;padding:1px 6px;white-space:pre-wrap;width:50%;border:1px solid #e1e4e8}
del{background:#ffdce0}ins{background:#d4f8d4}mod-o{background:#fff3cd}chips{}
td.o{background:#ffeef0;border-left:4px solid #f66a0a}
td.n{background:#e6ffed;border-left:4px solid #28a745}
td.same{background:#fff}
.summary{font-family:Segoe UI,sans-serif;color:#57606a}
""");
        sb.Append("</style></head><body>");
        sb.Append($"<h2>{Escape(diff.Title)}</h2>");
        sb.Append($"<p class=\"summary\">+{diff.Added} added &nbsp; −{diff.Removed} removed &nbsp; ~{diff.Modified} modified</p>");

        foreach (var td in diff.TypeDiffs.Where(d => d.Change != ChangeKind.None))
        {
            sb.Append($"<h3>{td.DisplayChange} — {Escape(td.FullName)}</h3>");
            sb.Append("<table><tr>");
            var rows = DiffService.SideBySide(td);
            foreach (var r in rows)
            {
                sb.Append("<tr>");
                sb.Append($"<td class=\"{CellClass(r.Old, true)}\">{Escape(r.Old?.Text)}</td>");
                sb.Append($"<td class=\"{CellClass(r.New, false)}\">{Escape(r.New?.Text)}</td>");
                sb.Append("</tr>");
            }
            sb.Append("</tr></table>");
        }
        sb.Append("</body></html>");
        File.WriteAllText(path, sb.ToString());

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
