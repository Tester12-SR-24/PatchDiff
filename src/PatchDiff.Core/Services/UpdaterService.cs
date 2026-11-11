using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace PatchDiff.Services;

/// <summary>One GitHub release: tag, display name, page URL, download URL of the app asset.</summary>
public sealed record ReleaseInfo(string Tag, string Name, string PageUrl, string AssetUrl, DateTime PublishedUtc);

/// <summary>
/// Follows the repository's GitHub releases: check → download → stage → apply on exit.
///
/// Auth: the repo may be PRIVATE (unauthenticated API returns 404, which looks like
/// "no releases"). Resolution order: PATCHDIFF_GITHUB_TOKEN environment variable, then
/// the credential git push already uses (git credential fill), then anonymous.
///
/// Apply strategy (no admin rights, no data risk): the update zip is extracted to
/// DataRoot\update\staging, and a small cmd script waits for this process to exit,
/// copies the staged files over the install folder, and relaunches PatchDiff.
/// User data (snapshots, reports, revisions, settings) lives in DataRoot, never in
/// the install folder, so an update cannot touch it.
/// </summary>
public sealed class UpdaterService
{
    const string Repo = "Tester12-SR-24/PatchDiff";
    const string AppAssetName = "PatchDiff-app-win-x64.zip";

    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>Latest published release, or null when none is reachable (404/403/offline).</summary>
    public async Task<ReleaseInfo?> GetLatestReleaseAsync(CancellationToken ct = default)
    {
        try
        {
            using var req = NewRequest(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases/latest");
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var root = doc.RootElement;
            var tag = root.GetProperty("tag_name").GetString() ?? "";
            if (tag.Length == 0)
                return null;
            string? assetUrl = null;
            if (root.TryGetProperty("assets", out var assets))
            {
                foreach (var a in assets.EnumerateArray())
                {
                    var aname = a.GetProperty("name").GetString() ?? "";
                    if (string.Equals(aname, AppAssetName, StringComparison.OrdinalIgnoreCase))
                        assetUrl = a.GetProperty("browser_download_url").GetString();
                }
            }
            DateTime.TryParse(root.GetProperty("published_at").GetString(), out var published);
            return new ReleaseInfo(
                tag,
                root.GetProperty("name").GetString() ?? tag,
                root.GetProperty("html_url").GetString() ?? $"https://github.com/{Repo}/releases",
                assetUrl ?? "",
                published);
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        // strip a SemVer build suffix ("1.0.0+f391dc7") and any pre-release label
        var core = text.Split('+')[0].TrimStart('v', 'V', ' ');
        var dash = core.IndexOf('-');
        if (dash >= 0) core = core[..dash];
        return Version.TryParse(core, out var v) ? v : null;
    }

    /// <summary>
    /// True when the release tag is newer than the running build. When the running
    /// version cannot be determined, the result is "unknown": the caller must NOT
    /// treat this as up-to-date — offer the update (or a manual-download path) so a
    /// version-detection failure can never silently block all future updates.
    /// </summary>
    public static bool? IsNewerOrUnknown(ReleaseInfo release)
    {
        var local = ParseVersion(CurrentVersion());
        var remote = ParseVersion(release.Tag);
        if (local is null || remote is null) return null; // cannot prove up-to-date
        return remote > local;
    }

    /// <summary>True when the release tag is newer than the running build.
    /// Kept for callers that only act on a definite yes.</summary>
    public static bool IsNewer(ReleaseInfo release) => IsNewerOrUnknown(release) == true;

    public static string CurrentVersion()
    {
        try
        {
            var fv = FileVersionInfo.GetVersionInfo(Environment.ProcessPath ?? typeof(UpdaterService).Assembly.Location);
            return fv.ProductVersion is { Length: > 0 } pv ? pv : fv.FileVersion ?? "?";
        }
        catch { return "?"; }
    }

    /// <summary>
    /// Download the app asset into the staging folder and return the staged folder
    /// (or null on failure). Does not touch the running installation.
    /// </summary>
    public async Task<string?> DownloadAndStageAsync(ReleaseInfo release, Action<string>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(release.AssetUrl))
            return null;
        try
        {
            var staging = Path.Combine(SnapshotStore.DataRoot, "update", "staging");
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
            Directory.CreateDirectory(staging);

            progress?.Invoke("Downloading " + AppAssetName + "…");
            using var req = NewRequest(HttpMethod.Get, release.AssetUrl);
            req.Headers.Accept.ParseAdd("application/octet-stream");
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return null;

            var zipPath = Path.Combine(Path.GetTempPath(), "patchdiff-update.zip");
            await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var dst = File.Create(zipPath))
            {
                var buffer = new byte[1 << 16];
                long total = resp.Content.Headers.ContentLength ?? -1, done = 0;
                int read;
                while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    done += read;
                    if (total > 0)
                        progress?.Invoke($"Downloading… {done * 100 / total}%");
                }
            }

            progress?.Invoke("Extracting…");
            ZipFile.ExtractToDirectory(zipPath, staging, overwriteFiles: true);
            File.Delete(zipPath);

            // sanity: the staged folder must contain a REAL app entry point.
            // File.Exists alone is not enough — a truncated/HTML/partial download
            // produces a file that Windows then refuses to run ("Unsupported 16-Bit
            // Application"), or worse, overwrites a working install with garbage.
            var stagedExe = Path.Combine(staging, "PatchDiff.exe");
            if (!IsValidPeImage(stagedExe))
            {
                WriteUpdaterLog("stage validation",
                    new InvalidDataException($"Staged PatchDiff.exe is missing or not a valid executable: {stagedExe}"));
                try { Directory.Delete(staging, recursive: true); } catch { }
                return null;
            }
            return staging;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            WriteUpdaterLog("download/extract", ex);
            return null;
        }
    }

    /// <summary>Path of the file the apply script writes its robocopy exit code to.</summary>
    public static string ApplyStatusFile
    {
        get { var dir = Path.Combine(SnapshotStore.DataRoot, "update"); Directory.CreateDirectory(dir); return Path.Combine(dir, "last-apply.txt"); }
    }

    /// <summary>
    /// Write and launch the apply-on-exit script: wait for the current process to
    /// exit, copy the staged build over the install folder, relaunch PatchDiff.
    /// Call right before Application shutdown.
    /// </summary>
    public bool LaunchApplyAfterExit(string stagingFolder)
    {
        try
        {
            var installDir = AppContext.BaseDirectory;
            var installTrimmed = installDir.TrimEnd(Path.DirectorySeparatorChar);
            var stageTrimmed = stagingFolder.TrimEnd(Path.DirectorySeparatorChar);
            if (string.Equals(stageTrimmed, installTrimmed, StringComparison.OrdinalIgnoreCase))
                return false; // running from the staging folder itself — nothing to swap

            if (!IsValidPeImage(Path.Combine(stagingFolder, "PatchDiff.exe")))
            {
                WriteUpdaterLog("apply validation",
                    new InvalidDataException("Refusing to apply: staged PatchDiff.exe is not a valid executable."));
                return false;
            }

            var pid = Environment.ProcessId;
            var appExe = Path.Combine(installDir, "PatchDiff.exe");
            var script = Path.Combine(Path.GetTempPath(), "patchdiff-apply.cmd");
            var applyStatusFile = ApplyStatusFile;

            var cmd = new System.Text.StringBuilder();
            cmd.AppendLine("@echo off");
            cmd.AppendLine("rem PatchDiff update apply — waits for the running instance, swaps files, relaunches.");
            cmd.AppendLine(":wait");
            cmd.AppendLine($"tasklist /FI \"PID eq {pid}\" | find \"{pid}\" >nul && (timeout /t 1 /nobreak >nul & goto wait)");
            cmd.AppendLine("rem apply: robocopy mirrors the staged build over the install folder");
            cmd.AppendLine($"robocopy \"{stageTrimmed}\" \"{installTrimmed}\" /MIR >nul");
            cmd.AppendLine("rem robocopy exit codes 0-7 are success");
            cmd.AppendLine("set RC=%errorlevel%");
            cmd.AppendLine("if %RC% GTR 7 (");
            cmd.AppendLine("    rem transient lock (AV scan etc.) — wait a beat and try once more");
            cmd.AppendLine("    timeout /t 2 /nobreak >nul");
            cmd.AppendLine($"    robocopy \"{stageTrimmed}\" \"{installTrimmed}\" /MIR >nul");
            cmd.AppendLine("    set RC=%errorlevel%");
            cmd.AppendLine(")");
            cmd.AppendLine("rem record the apply result where the next launch will find it");
            cmd.AppendLine($"echo %RC% > \"{applyStatusFile}\"");
            cmd.AppendLine($"rmdir /s /q \"{stageTrimmed}\"");
            cmd.AppendLine($"start \"\" \"{appExe}\"");
            cmd.AppendLine("del \"%~f0\"");
            File.WriteAllText(script, cmd.ToString());
            try { File.WriteAllText(ApplyStatusFile, "pending"); } catch { }

            Process.Start(new ProcessStartInfo("cmd.exe", "/c \"" + script + "\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            });
            return true;
        }
        catch (Exception ex)
        {
            WriteUpdaterLog("apply launch", ex);
            return false;
        }
    }

    /// <summary>
    /// True only when the file exists, is plausibly a full build, and carries a
    /// Windows PE header. Guards the apply step from ever installing a corrupt file.
    /// </summary>
    public static bool IsValidPeImage(string? path)
    {
        try
        {
            if (string.IsNullOrEmpty(path))
                return false;
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length < 64 * 1024) // a real single-file build is tens of MB
                return false;

            using var fs = File.OpenRead(path);
            Span<byte> buf = stackalloc byte[4];
            if (fs.Read(buf[..2]) != 2 || buf[0] != (byte)'M' || buf[1] != (byte)'Z')
                return false;

            fs.Position = 0x3C; // e_lfanew
            if (fs.Read(buf) != 4)
                return false;
            var peOffset = BitConverter.ToInt32(buf);
            if (peOffset <= 0 || peOffset >= fi.Length - 4)
                return false;

            fs.Position = peOffset;
            if (fs.Read(buf) != 4)
                return false;
            return buf[0] == (byte)'P' && buf[1] == (byte)'E' && buf[2] == 0 && buf[3] == 0;
        }
        catch
        {
            return false;
        }
    }

    static HttpRequestMessage NewRequest(HttpMethod method, string url)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.UserAgent.ParseAdd("PatchDiff-Updater");
        var token = Environment.GetEnvironmentVariable("PATCHDIFF_GITHUB_TOKEN");
        if (string.IsNullOrEmpty(token))
            token = TryGetStoredCredential();
        if (!string.IsNullOrEmpty(token))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }

    /// <summary>Token from git's stored credential (the same one git push uses), if any.</summary>
    static string? TryGetStoredCredential()
    {
        try
        {
            var psi = new ProcessStartInfo("git", "credential fill")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var git = Process.Start(psi);
            if (git is null)
                return null;
            git.StandardInput.Write("protocol=https\nhost=github.com\n\n");
            git.StandardInput.Close();
            var output = git.StandardOutput.ReadToEnd();
            git.WaitForExit(5000);
            foreach (var line in output.Split('\n'))
                if (line.StartsWith("password="))
                    return line["password=".Length..].TrimEnd('\r');
        }
        catch { /* git missing or credential helper refused — fall back to anonymous */ }
        return null;
    }

    static void WriteUpdaterLog(string phase, Exception ex)
    {
        try
        {
            var dir = Path.Combine(SnapshotStore.DataRoot, "logs");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, $"updater-{DateTime.Now:yyyyMMdd}.log");
            File.AppendAllText(file,
                $"{DateTime.Now:HH:mm:ss} {phase}: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}");
        }
        catch { /* logging must never throw */ }
    }
}
