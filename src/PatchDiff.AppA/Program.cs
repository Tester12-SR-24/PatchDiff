using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
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

    // Held for the lifetime of the process; the OS releases it when we exit.
    static FileStream? _instanceLock;
    static Mutex? _instanceMutex;

    [STAThread]
    public static void Main(string[] args)
    {
        LaunchedAtLogin = args.Any(a =>
            string.Equals(a, Services.AutoStart.LoginArgument, StringComparison.OrdinalIgnoreCase));
        ForceShow = args.Any(a =>
            string.Equals(a, ShowArgument, StringComparison.OrdinalIgnoreCase));

        // Only one PatchDiff may run: a second copy would watch the same files, write the
        // same snapshots and fight over the settings file.
        if (!TryAcquireSingleInstance())
        {
            // Hand over to the copy that is already running: it raises its window and
            // says so. This process exits immediately, so the duplicate never lingers.
            if (!SingleInstance.TrySignalExisting() && !LaunchedAtLogin)
                NotifyAlreadyRunning();
            return;
        }

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

    /// <summary>
    /// Takes the machine-wide instance lock. A file lock is used first because it behaves
    /// the same on Windows and Linux; the named mutex is a fallback for systems where the
    /// temp folder is not writable.
    /// </summary>
    static bool TryAcquireSingleInstance()
    {
        try
        {
            var lockPath = Path.Combine(Path.GetTempPath(), "PatchDiff.instance.lock");
            _instanceLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false; // another process holds it
        }
        catch
        {
            // Fall through to the mutex path.
        }

        try
        {
            _instanceMutex = new Mutex(initiallyOwned: true, @"Global\PatchDiff.SingleInstance", out var created);
            if (created)
                return true;
            _instanceMutex.Dispose();
            _instanceMutex = null;
            return false;
        }
        catch
        {
            return true; // never block startup because the guard itself failed
        }
    }

    /// <summary>Tells the user that PatchDiff is already running, then lets the caller exit.</summary>
    static void NotifyAlreadyRunning()
    {
        const string message = "PatchDiff is already running.\n\n" +
                               "Only one copy can watch your assemblies at a time — use the window " +
                               "that is already open (or the tray icon if it is hidden).";
        const string caption = "PatchDiff";

        try
        {
            if (OperatingSystem.IsWindows())
            {
                const uint MB_OK = 0x0, MB_ICONINFORMATION = 0x40, MB_TOPMOST = 0x40000, MB_SETFOREGROUND = 0x10000;
                MessageBoxW(IntPtr.Zero, message, caption,
                    MB_OK | MB_ICONINFORMATION | MB_TOPMOST | MB_SETFOREGROUND);
                return;
            }

            if (OperatingSystem.IsLinux())
            {
                foreach (var (file, arguments) in new[]
                         {
                             ("zenity", new[] { "--info", "--title=" + caption, "--text=" + message }),
                             ("kdialog", new[] { "--title", caption, "--msgbox", message }),
                             ("xmessage", new[] { "-center", message }),
                         })
                {
                    try
                    {
                        using var p = Process.Start(new ProcessStartInfo(file, arguments)
                        {
                            UseShellExecute = false,
                        });
                        if (p is not null)
                        {
                            p.WaitForExit(30000);
                            return;
                        }
                    }
                    catch { /* try the next dialog tool */ }
                }
            }
        }
        catch { /* a failed notice must not change the outcome */ }

        try { Console.Error.WriteLine(message); } catch { }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()

        .LogToTrace();
}
