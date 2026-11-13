using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace PatchDiff.Services;

/// <summary>One GitHub release: tag, display name, page URL, download URL of the app asset,
/// download URL of the Windows installer, download URL of the Linux archive, download URL
/// of the Linux AppImage, and publish time.</summary>
public sealed record ReleaseInfo(string Tag, string Name, string PageUrl, string AssetUrl, string InstallerUrl, string LinuxUrl, string AppImageUrl, DateTime PublishedUtc);

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
    // Windows tools are fully qualified: a Git-for-Windows install puts Unix
    // builds of find/timeout earlier on PATH, and cmd would run those instead.
    const string WinDir = "\"%SystemRoot%\\System32\\";

    const string Repo = "Tester12-SR-24/PatchDiff";
    // Must match the app zip published by .github/workflows/release.yml
    // (dist/PatchDiff-app-win-x64.zip) and the README's artifact table. The
    // compare below is case-insensitive, so only the spelling has to line up
    // with the asset name on the release.
    const string AppAssetName = "PatchDiff-app-win-x64.zip";
    // The Windows installer published alongside it (dist/PatchDiff-setup-x.y.z.exe).
    // Preferred over the zip: it installs to Program Files, keeps the chosen folder
    // on upgrade, and elevates properly instead of writing into a protected path.
    const string InstallerAssetPrefix = "PatchDiff-setup-";
    // Linux ships a tar.gz with install.sh instead of an .exe installer.
    static string LinuxAssetName => RuntimeInformation.ProcessArchitecture == Architecture.Arm64
        ? "PatchDiff-linux-arm64.tar.gz"
        : "PatchDiff-linux-x64.tar.gz";
    // AppImage runs update by replacing the AppImage file itself.
    static string AppImageAssetName => RuntimeInformation.ProcessArchitecture == Architecture.Arm64
        ? "PatchDiff-linux-arm64.AppImage"
        : "PatchDiff-linux-x64.AppImage";

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
            string? installerUrl = null;
            string? linuxUrl = null;
            string? appImageUrl = null;
            if (root.TryGetProperty("assets", out var assets))
            {
                foreach (var a in assets.EnumerateArray())
                {
                    var aname = a.GetProperty("name").GetString() ?? "";
                    if (string.Equals(aname, AppAssetName, StringComparison.OrdinalIgnoreCase))
                        assetUrl = a.GetProperty("browser_download_url").GetString();
                    else if (aname.StartsWith(InstallerAssetPrefix, StringComparison.OrdinalIgnoreCase) &&
                             aname.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        installerUrl = a.GetProperty("browser_download_url").GetString();
                    else if (string.Equals(aname, LinuxAssetName, StringComparison.OrdinalIgnoreCase))
                        linuxUrl = a.GetProperty("browser_download_url").GetString();
                    else if (string.Equals(aname, AppImageAssetName, StringComparison.OrdinalIgnoreCase))
                        appImageUrl = a.GetProperty("browser_download_url").GetString();
                }
            }
            DateTime.TryParse(root.GetProperty("published_at").GetString(), out var published);
            return new ReleaseInfo(
                tag,
                root.GetProperty("name").GetString() ?? tag,
                root.GetProperty("html_url").GetString() ?? $"https://github.com/{Repo}/releases",
                assetUrl ?? "",
                installerUrl ?? "",
                linuxUrl ?? "",
                appImageUrl ?? "",
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
            // The assembly's informational version is the only reliable source on
            // Linux: a single-file publish is an ELF binary with no PE version
            // resource, so FileVersionInfo returns nothing there and the app would
            // think it is older than every release.
            var info = Assembly.GetEntryAssembly()?
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info))
                return info.Split('+')[0].Trim();

            // Single-file publishes report an empty Assembly.Location, so resolve the
            // real process path first and fall back to the app base directory.
            var path = Environment.ProcessPath;
            if (string.IsNullOrEmpty(path))
            {
                var name = Assembly.GetEntryAssembly()?.GetName().Name ?? "PatchDiff";
                path = Path.Combine(AppContext.BaseDirectory, name + ".dll");
            }
            var fv = FileVersionInfo.GetVersionInfo(path);
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
    /// Download the release's Windows installer into the update folder and return its
    /// path (or null on failure). The file is validated as a real PE image before it
    /// is ever handed to the shell.
    /// </summary>
    public async Task<string?> DownloadInstallerAsync(ReleaseInfo release, Action<string>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(release.InstallerUrl))
            return null;
        try
        {
            var dir = Path.Combine(SnapshotStore.DataRoot, "update");
            Directory.CreateDirectory(dir);
            var fileName = $"PatchDiff-setup-{release.Tag.TrimStart('v', 'V')}.exe";
            var target = Path.Combine(dir, fileName);
            if (File.Exists(target))
                File.Delete(target);

            progress?.Invoke($"Downloading {fileName}…");
            using var req = NewRequest(HttpMethod.Get, release.InstallerUrl);
            req.Headers.Accept.ParseAdd("application/octet-stream");
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return null;

            await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var dst = File.Create(target))
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

            if (!IsValidPeImage(target))
            {
                WriteUpdaterLog("installer validation",
                    new InvalidDataException($"Downloaded installer is not a valid executable: {target}"));
                try { File.Delete(target); } catch { }
                return null;
            }
            return target;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            WriteUpdaterLog("installer download", ex);
            return null;
        }
    }

    /// <summary>
    /// Download the Linux release archive (tar.gz with install.sh) and return its path,
    /// or null on failure.
    /// </summary>
    public async Task<string?> DownloadLinuxArchiveAsync(ReleaseInfo release, Action<string>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(release.LinuxUrl))
            return null;
        try
        {
            var dir = Path.Combine(SnapshotStore.DataRoot, "update");
            Directory.CreateDirectory(dir);
            var target = Path.Combine(dir, LinuxAssetName);
            if (File.Exists(target))
                File.Delete(target);

            progress?.Invoke($"Downloading {LinuxAssetName}…");
            using var req = NewRequest(HttpMethod.Get, release.LinuxUrl);
            req.Headers.Accept.ParseAdd("application/octet-stream");
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return null;

            await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var dst = File.Create(target))
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

            // A gzip stream starts with 1f 8b; anything else is a failed download.
            using (var fs = File.OpenRead(target))
            {
                var magic = new byte[2];
                if (fs.Read(magic, 0, 2) != 2 || magic[0] != 0x1f || magic[1] != 0x8b)
                {
                    WriteUpdaterLog("linux archive validation",
                        new InvalidDataException($"Downloaded archive is not a gzip file: {target}"));
                    try { File.Delete(target); } catch { }
                    return null;
                }
            }
            return target;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            WriteUpdaterLog("linux archive download", ex);
            return null;
        }
    }

    /// <summary>
    /// Download the release's AppImage and return its path, or null on failure.
    /// Used when PatchDiff is running from an AppImage: the file itself is replaced.
    /// </summary>
    public async Task<string?> DownloadAppImageAsync(ReleaseInfo release, Action<string>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(release.AppImageUrl))
            return null;
        try
        {
            var dir = Path.Combine(SnapshotStore.DataRoot, "update");
            Directory.CreateDirectory(dir);
            var target = Path.Combine(dir, AppImageAssetName);
            if (File.Exists(target))
                File.Delete(target);

            progress?.Invoke($"Downloading {AppImageAssetName}…");
            using var req = NewRequest(HttpMethod.Get, release.AppImageUrl);
            req.Headers.Accept.ParseAdd("application/octet-stream");
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return null;

            await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var dst = File.Create(target))
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

            // ELF magic: 7f 45 4c 46. Anything else is a failed or HTML download.
            using (var fs = File.OpenRead(target))
            {
                var magic = new byte[4];
                if (fs.Read(magic, 0, 4) != 4 || magic[0] != 0x7f || magic[1] != 0x45 ||
                    magic[2] != 0x4c || magic[3] != 0x46)
                {
                    WriteUpdaterLog("appimage validation",
                        new InvalidDataException($"Downloaded AppImage is not an ELF file: {target}"));
                    try { File.Delete(target); } catch { }
                    return null;
                }
            }
            return target;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            WriteUpdaterLog("appimage download", ex);
            return null;
        }
    }

    /// <summary>
    /// Replace the running AppImage with a downloaded one once this process exits,
    /// then relaunch it.
    /// </summary>
    public static bool LaunchAppImageUpdateAfterExit(string newAppImage, string currentAppImage)
    {
        try
        {
            var pid = Environment.ProcessId;
            var script = Path.Combine(Path.GetTempPath(), "patchdiff-appimage-update.sh");
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("#!/bin/sh");
            sb.AppendLine("# PatchDiff update — waits for the running instance, swaps the AppImage, relaunches.");
            sb.AppendLine("set -u");
            sb.AppendLine($"i=0; while kill -0 {pid} 2>/dev/null && [ $i -lt 30 ]; do sleep 1; i=$((i+1)); done");
            sb.AppendLine($"kill -9 {pid} 2>/dev/null || true");
            sb.AppendLine("sleep 1");
            sb.AppendLine($"if cp -f '{newAppImage}' '{currentAppImage}' 2>/dev/null; then");
            sb.AppendLine($"  chmod 755 '{currentAppImage}'");
            sb.AppendLine($"  rm -f '{newAppImage}'");
            sb.AppendLine("fi");
            // Always relaunch, even if the swap failed: leaving the user with no window
            // is worse than running the previous version.
            sb.AppendLine($"nohup '{currentAppImage}' --show >/dev/null 2>&1 &");
            sb.AppendLine("rm -f \"$0\"");
            File.WriteAllText(script, sb.ToString());
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(script,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            // ArgumentList, not a quoted Arguments string: .NET passes arguments
            // verbatim on Unix, so single quotes would become part of the path.
            var psi = new ProcessStartInfo("/bin/sh") { UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add(script);
            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            WriteUpdaterLog("appimage update launch", ex);
            return false;
        }
    }

    /// <summary>
    /// Install a downloaded Linux archive once this process exits: extract it, run its
    /// install.sh into the current install folder, then relaunch the app.
    /// </summary>
    public static bool LaunchLinuxInstallerAfterExit(string archivePath, string installDir)
    {
        try
        {
            var pid = Environment.ProcessId;
            var script = Path.Combine(Path.GetTempPath(), "patchdiff-linux-update.sh");
            var work = Path.Combine(Path.GetTempPath(), "patchdiff-linux-update");
            var appExe = Path.Combine(installDir, "PatchDiff");
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("#!/bin/sh");
            sb.AppendLine("# PatchDiff update — waits for the running instance, installs, relaunches.");
            sb.AppendLine("set -u");
            sb.AppendLine($"i=0; while kill -0 {pid} 2>/dev/null && [ $i -lt 30 ]; do sleep 1; i=$((i+1)); done");
            sb.AppendLine($"kill -9 {pid} 2>/dev/null || true");
            sb.AppendLine("sleep 1");
            sb.AppendLine($"rm -rf '{work}'");
            sb.AppendLine($"mkdir -p '{work}'");
            sb.AppendLine($"if tar -xzf '{archivePath}' -C '{work}' && sh '{work}/install.sh' --dir '{installDir}' --yes; then");
            sb.AppendLine($"  rm -rf '{work}' '{archivePath}'");
            sb.AppendLine("fi");
            // Always relaunch, even if the install failed: leaving the user with no
            // window is worse than running the previous version.
            sb.AppendLine($"nohup '{appExe}' --show >/dev/null 2>&1 &");
            sb.AppendLine("rm -f \"$0\"");
            File.WriteAllText(script, sb.ToString());
            // Unix-only API: guard so the Windows build stays warning-free.
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(script,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            // ArgumentList, not a quoted Arguments string: .NET passes arguments
            // verbatim on Unix, so single quotes would become part of the path.
            var psi = new ProcessStartInfo("/bin/sh") { UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add(script);
            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            WriteUpdaterLog("linux installer launch", ex);
            return false;
        }
    }

    /// <summary>
    /// True when this process can create files in its own folder. Program Files is not
    /// writable for a standard user, which is what decides whether the installer has to
    /// ask for elevation.
    /// </summary>
    public static bool CanWriteInstallFolder()
    {
        try
        {
            var probe = Path.Combine(AppContext.BaseDirectory, $".write-test-{Guid.NewGuid():N}.tmp");
            using (var fs = File.Create(probe, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Run the downloaded installer once this process has exited. The installer is
    /// started silently with /CLOSEAPPLICATIONS so an upgrade never stops on a
    /// "close the running app?" prompt, and it keeps the folder chosen previously.
    /// </summary>
    public static bool LaunchInstallerAfterExit(string installerPath)
    {
        try
        {
            if (!IsValidPeImage(installerPath))
            {
                WriteUpdaterLog("installer launch",
                    new InvalidDataException($"Refusing to run a non-executable installer: {installerPath}"));
                return false;
            }

            var pid = Environment.ProcessId;
            var installDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            var script = Path.Combine(Path.GetTempPath(), "patchdiff-install-update.cmd");
            var cmd = new System.Text.StringBuilder();
            cmd.AppendLine("@echo off");
            cmd.AppendLine("rem PatchDiff update — waits for the running instance, then runs the installer.");
            cmd.AppendLine("set tries=0");
            cmd.AppendLine(":wait");
            // Fully qualified: a Git-for-Windows install puts a Unix `find` on PATH,
            // which would break this test.
            cmd.AppendLine($"{WinDir}tasklist.exe\" /FI \"PID eq {pid}\" /NH | {WinDir}find.exe\" \"{pid}\" >nul");
            cmd.AppendLine("if errorlevel 1 goto proceed");
            cmd.AppendLine("set /a tries+=1");
            cmd.AppendLine("if %tries% GEQ 30 goto force");
            cmd.AppendLine($"{WinDir}timeout.exe\" /t 1 /nobreak >nul");
            cmd.AppendLine("goto wait");
            cmd.AppendLine(":force");
            cmd.AppendLine($"{WinDir}taskkill.exe\" /F /PID {pid} >nul 2>&1");
            cmd.AppendLine($"{WinDir}timeout.exe\" /t 1 /nobreak >nul");
            cmd.AppendLine(":proceed");
            // /DIR pins the upgrade to the folder the app is running from. Without it a
            // per-user install would be upgraded into Program Files (or vice versa) and
            // the old copy would keep launching.
            // A folder this user can write to (the old per-user install) needs no UAC, so
            // ask for a per-user install there; Program Files keeps the elevated default.
            var privileges = CanWriteInstallFolder() ? " /CURRENTUSER" : "";
            cmd.AppendLine($"start \"\" \"{installerPath}\" /SILENT /CLOSEAPPLICATIONS /NORESTART /SUPPRESSMSGBOXES /DIR=\"{installDir}\"{privileges}");
            cmd.AppendLine("del \"%~f0\"");
            File.WriteAllText(script, cmd.ToString());

            // Elevate the helper when the app sits in a protected folder (Program Files):
            // the UAC prompt then appears while the app is still on screen, instead of
            // being raised by a hidden process after it exits.
            var needsElevation = !CanWriteInstallFolder();
            var psi = new ProcessStartInfo("cmd.exe", "/c \"" + script + "\"")
            {
                CreateNoWindow = true,
                UseShellExecute = needsElevation,
            };
            if (needsElevation)
                psi.Verb = "runas";
            Process.Start(psi);
            return true;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // 1223 = the user dismissed the elevation prompt.
            WriteUpdaterLog("installer launch", ex);
            return false;
        }
        catch (Exception ex)
        {
            WriteUpdaterLog("installer launch", ex);
            return false;
        }
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
            cmd.AppendLine("set tries=0");
            cmd.AppendLine(":wait");
            // Fully qualified: a Git-for-Windows install puts a Unix `find` on PATH,
            // which would break this test.
            cmd.AppendLine($"{WinDir}tasklist.exe\" /FI \"PID eq {pid}\" /NH | {WinDir}find.exe\" \"{pid}\" >nul");
            cmd.AppendLine("if errorlevel 1 goto proceed");
            cmd.AppendLine("set /a tries+=1");
            cmd.AppendLine("if %tries% GEQ 30 goto force");
            cmd.AppendLine($"{WinDir}timeout.exe\" /t 1 /nobreak >nul");
            cmd.AppendLine("goto wait");
            cmd.AppendLine(":force");
            cmd.AppendLine($"{WinDir}taskkill.exe\" /F /PID {pid} >nul 2>&1");
            cmd.AppendLine($"{WinDir}timeout.exe\" /t 1 /nobreak >nul");
            cmd.AppendLine(":proceed");
            cmd.AppendLine("rem apply: robocopy mirrors the staged build over the install folder");
            cmd.AppendLine($"{WinDir}robocopy.exe\" \"{stageTrimmed}\" \"{installTrimmed}\" /MIR >nul");
            cmd.AppendLine("rem robocopy exit codes 0-7 are success");
            cmd.AppendLine("set RC=%errorlevel%");
            cmd.AppendLine("if %RC% GTR 7 (");
            cmd.AppendLine("    rem transient lock (AV scan etc.) — wait a beat and try once more");
            cmd.AppendLine($"    {WinDir}timeout.exe\" /t 2 /nobreak >nul");
            cmd.AppendLine($"    {WinDir}robocopy.exe\" \"{stageTrimmed}\" \"{installTrimmed}\" /MIR >nul");
            cmd.AppendLine("    set RC=%errorlevel%");
            cmd.AppendLine(")");
            cmd.AppendLine("rem record the apply result where the next launch will find it");
            cmd.AppendLine($"echo %RC% > \"{applyStatusFile}\"");
            cmd.AppendLine($"rmdir /s /q \"{stageTrimmed}\"");
            cmd.AppendLine($"start \"\" \"{appExe}\" --show");
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
