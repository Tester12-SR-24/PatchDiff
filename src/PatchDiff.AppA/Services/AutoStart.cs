using System;
using System.IO;

namespace PatchDiff.Services;

/// <summary>Cross-platform "start when I sign in":
/// Windows — HKCU Run key; Linux — XDG autostart desktop entry.
/// The entry always passes <see cref="LoginArgument"/> so the app can tell a
/// sign-in launch (start hidden in the tray) from a manual launch (show the window).
/// (macOS LaunchAgents intentionally not implemented yet.)</summary>
public static class AutoStart
{
    /// <summary>Passed by the autostart entry; the app starts hidden in the tray.</summary>
    public const string LoginArgument = "--autostart";

    const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValueName = "PatchDiff";
    static string DesktopFilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", "autostart", "patchdiff.desktop");

    public static bool IsSupported =>
        OperatingSystem.IsWindows() || OperatingSystem.IsLinux();

    public static bool IsEnabled()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath);
                return key?.GetValue(RunValueName) is not null;
            }
            if (OperatingSystem.IsLinux())
                return File.Exists(DesktopFilePath);
        }
        catch { /* read failures mean "not enabled" */ }
        return false;
    }


    /// <summary>The command a sign-in launch should run.</summary>
    static string CurrentCommand() => $"\"{Environment.ProcessPath}\" {LoginArgument}";

    /// <summary>
    /// Windows keeps an "enabled/disabled" record for every Run entry. Without one the
    /// entry can be hidden from Task Manager's Startup tab, so write the enabled value
    /// (12 bytes, first byte 0x02) when registering autostart.
    /// </summary>
    static void MarkApproved()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", writable: true);
            key.SetValue(RunValueName, new byte[] { 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
                Microsoft.Win32.RegistryValueKind.Binary);
        }
        catch { /* cosmetic: Windows enables the entry by default when no record exists */ }
    }

    /// <summary>The executable path from a Run value such as '"C:\\app.exe" --flag'.</summary>
    static string ExecutableFrom(string command)
    {
        var text = command.Trim();
        if (text.Length == 0)
            return "";
        if (text[0] == '"')
        {
            var close = text.IndexOf('"', 1);
            return close > 1 ? text[1..close] : text.Trim('"');
        }
        var space = text.IndexOf(' ');
        return space > 0 ? text[..space] : text;
    }

    /// <summary>
    /// Repairs a stale autostart registration. An entry written by an older build can
    /// point at a folder the app no longer lives in (a per-user install later replaced
    /// by a Program Files one), in which case sign-in starts nothing at all.
    /// </summary>
    public static void RepairIfStale()
    {
        try
        {
            if (!IsSupported || !IsEnabled())
                return;

            if (OperatingSystem.IsWindows())
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
                if (key is null)
                    return;
                var current = key.GetValue(RunValueName) as string;
                if (string.IsNullOrEmpty(current))
                    return;

                var target = ExecutableFrom(current);
                var targetMissing = target.Length > 0 && !File.Exists(target);
                var isThisCopy = string.Equals(target, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);
                // A healthy entry for a DIFFERENT copy belongs to that install — leave it.
                if (!targetMissing && !isThisCopy)
                    return;
                if (!string.Equals(current, CurrentCommand(), StringComparison.OrdinalIgnoreCase))
                    key.SetValue(RunValueName, CurrentCommand());
                MarkApproved();
                return;
            }

            if (OperatingSystem.IsLinux())
            {
                var exe = Environment.ProcessPath ?? "patchdiff";
                var expected = $"Exec=\"{exe}\" {LoginArgument}";
                var text = File.Exists(DesktopFilePath) ? File.ReadAllText(DesktopFilePath) : "";
                if (text.Contains(expected, StringComparison.Ordinal))
                    return;
                // Same rule as Windows: only touch an entry that points at a missing file
                // or at this very copy.
                var existingExec = "";
                foreach (var line in text.Split('\n'))
                {
                    if (line.StartsWith("Exec=", StringComparison.Ordinal))
                    {
                        existingExec = ExecutableFrom(line["Exec=".Length..]);
                        break;
                    }
                }
                if (existingExec.Length > 0 && File.Exists(existingExec) &&
                    !string.Equals(existingExec, exe, StringComparison.Ordinal))
                    return;
                {
                    var kept = new System.Collections.Generic.List<string>();
                    foreach (var line in text.Split('\n'))
                    {
                        if (line.StartsWith("Exec=", StringComparison.Ordinal))
                            continue;
                        if (line.Trim().Length == 0)
                            continue;
                        kept.Add(line.TrimEnd('\r'));
                    }
                    if (kept.Count == 0 || !kept.Contains("[Desktop Entry]"))
                    {
                        kept.InsertRange(0, new[]
                        {
                            "[Desktop Entry]",
                            "Type=Application",
                            "Name=PatchDiff",
                            "Terminal=false",
                        });
                    }
                    kept.Add(expected);
                    File.WriteAllText(DesktopFilePath, string.Join("\n", kept) + "\n");
                }
            }
        }
        catch { /* autostart repair must never block startup */ }
    }

    public static void SetEnabled(bool enabled)
    {
        if (OperatingSystem.IsWindows())
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (enabled)
            {
                key.SetValue(RunValueName, CurrentCommand());
                MarkApproved();
            }
            else
            {
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
            }
            return;
        }
        if (OperatingSystem.IsLinux())
        {
            if (!enabled)
            {
                if (File.Exists(DesktopFilePath))
                    File.Delete(DesktopFilePath);
                return;
            }
            var dir = Path.GetDirectoryName(DesktopFilePath)!;
            Directory.CreateDirectory(dir);
            var exe = Environment.ProcessPath ?? "patchdiff";
            File.WriteAllText(DesktopFilePath,
                "[Desktop Entry]\n" +
                "Type=Application\n" +
                "Name=PatchDiff\n" +
                "Comment=Watch .NET/Unity DLLs; decompile, diff, report\n" +
                $"Exec=\"{exe}\" {LoginArgument}\n" +
                "Terminal=false\n" +
                "Categories=Development;\n" +
                "X-GNOME-Autostart-enabled=true\n");
        }
    }
}
