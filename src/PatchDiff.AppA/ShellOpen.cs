using System.Diagnostics;
using System.IO;

namespace PatchDiff;

/// <summary>Opens a file/folder with the platform shell. Linux falls back through
/// xdg-open and (under WSL) Windows Explorer; failures become a friendly dialog
/// instead of the crash handler.</summary>
public static class ShellOpen
{
    public static void Open(Avalonia.Controls.Window? owner, string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                return;
            }
            if (OperatingSystem.IsMacOS())
            {
                if (Which("open") is not null)
                {
                    var p2 = new ProcessStartInfo("open") { UseShellExecute = false };
                    p2.ArgumentList.Add(path);
                    Process.Start(p2);
                    return;
                }
            }
            else if (Which("xdg-open") is { } xdg)
            {
                var p1 = new ProcessStartInfo(xdg) { UseShellExecute = false };
                p1.ArgumentList.Add(path);
                Process.Start(p1);
                return;
            }
            // Under WSL there is often no xdg-open, but Windows Explorer is reachable
            // through interop. Passing the UNC path as an argument gets mangled by the
            // WSL argument translation (opens the wrong folder) — the reliable pattern
            // is CWD = target + "." as the argument (interop translates the CWD).
            if (IsWsl() && Which("explorer.exe") is { } explorer)
            {
                var target = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
                if (target is not null && Directory.Exists(target))
                {
                    var p3 = new ProcessStartInfo(explorer)
                    {
                        UseShellExecute = false,
                        WorkingDirectory = target,
                    };
                    if (File.Exists(path))
                        p3.ArgumentList.Add("/select," + Path.GetFileName(path));
                    else
                        p3.ArgumentList.Add(".");
                    Process.Start(p3);
                    return;
                }
            }
            MessageBox.Show(owner,
                $"Nothing on this system can open files or folders (tried {(OperatingSystem.IsMacOS() ? "'open'" : "'xdg-open'")}).\n\nThe path is:\n{path}", "Open failed");
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, $"Could not open:\n{path}\n\n{ex.Message}", "Open failed");
        }
    }

    static bool IsWsl()
    {
        try
        {
            return File.ReadAllText("/proc/version").Contains("microsoft", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    static string? Which(string name)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), name);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch { /* skip malformed PATH entry */ }
        }
        return null;
    }
}
