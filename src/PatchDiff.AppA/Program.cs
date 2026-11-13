using System;
using System.IO;
using System.Linq;
using Avalonia;

namespace PatchDiff;

internal static class Program
{
    /// <summary>True when the process was started by the sign-in autostart entry.
    /// A sign-in launch starts hidden in the tray; a manual launch shows the window.</summary>
    public static bool LaunchedAtLogin { get; private set; }

    /// <summary>Passed by the installer and the updater: always show the window, even
    /// when the user normally starts PatchDiff hidden in the tray.</summary>
    public const string ShowArgument = "--show";
    public static bool ForceShow { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        LaunchedAtLogin = args.Any(a =>
            string.Equals(a, Services.AutoStart.LoginArgument, StringComparison.OrdinalIgnoreCase));
        ForceShow = args.Any(a =>
            string.Equals(a, ShowArgument, StringComparison.OrdinalIgnoreCase));

        // Apply the user-chosen data folder BEFORE any store touches the disk.
        // The env var (portable setups) always wins over the saved setting; settings
        // themselves stay in the default root so the override can be changed back.
        try
        {
            var settings = Services.SettingsFile.Load();
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PATCHDIFF_DATA_DIR")) &&
                !string.IsNullOrWhiteSpace(settings.DataFolder) &&
                Directory.Exists(settings.DataFolder))
            {
                Services.SnapshotStore.CustomRoot = settings.DataFolder;
            }
        }
        catch { /* a broken settings file must never block startup */ }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()

        .LogToTrace();
}
