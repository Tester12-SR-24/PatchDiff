using System;
using System.IO;

namespace PatchDiff.Services;

/// <summary>Cross-platform "start when I sign in":
/// Windows — HKCU Run key; Linux — XDG autostart desktop entry.
/// (macOS LaunchAgents intentionally not implemented yet.)</summary>
public static class AutoStart
{
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

    public static void SetEnabled(bool enabled)
    {
        if (OperatingSystem.IsWindows())
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (enabled)
                key.SetValue(RunValueName, $"\"{Environment.ProcessPath}\"");
            else
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
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
                $"Exec=\"{exe}\"\n" +
                "Terminal=false\n" +
                "Categories=Development;\n" +
                "X-GNOME-Autostart-enabled=true\n");
        }
    }
}
