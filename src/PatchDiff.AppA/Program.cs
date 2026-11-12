using System;
using System.IO;
using Avalonia;

namespace PatchDiff;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
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
