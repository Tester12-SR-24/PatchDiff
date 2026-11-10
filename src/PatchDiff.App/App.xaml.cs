using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using PatchDiff.Services;

namespace PatchDiff;

public partial class App : Application
{
    private Mutex? _instanceMutex;
    private EventWaitHandle? _restoreEvent;
    private const string LogDirName = "logs";

    protected override void OnStartup(StartupEventArgs e)
    {
        // ---- single instance ----
        // PATCHDIFF_ALLOW_MULTI=1 lets an isolated test/demo instance run alongside the
        // user's main one (QA harnesses, screenshots). Normal runs keep single-instance.
        var allowMulti = Environment.GetEnvironmentVariable("PATCHDIFF_ALLOW_MULTI") == "1";
        bool createdNew;
        _instanceMutex = allowMulti
            ? new Mutex(true, @"Local\PatchDiff.SingleInstance." + Guid.NewGuid().ToString("N"), out createdNew)
            : new Mutex(true, @"Local\PatchDiff.SingleInstance", out createdNew);
        _restoreEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\PatchDiff.Restore");

        if (!createdNew)
        {
            _restoreEvent.Set();
            Shutdown();
            return;
        }

        RegisterGlobalHandlers();

        // ---- pre-WPF crash safety net ----
        // Everything below runs inside a net: any failure (theme file problems,
        // malformed XAML, merge errors) becomes a loud MessageBox + log, never a
        // silent exit. No WPF window exists yet at this point.
        try
        {
            RunXamlPrecheck();

            var dark = new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/Themes/Dark.xaml"),
            };
            Resources.MergedDictionaries.Add(dark);
        }
        catch (Exception ex)
        {
            WriteCrashLog("startup (resource merge)", ex);
            var result = PreWindowCrashDialog(ex, "PatchDiff can't start — a UI theme file is corrupted.",
                "Problem found while loading the UI theme:" +
                Environment.NewLine + ex.Message +
                Environment.NewLine + Environment.NewLine +
                "Your data is safe (snapshots, reports, settings in %LOCALAPPDATA%\\PatchDiff).");
            Shutdown();
            return;
        }

        ThreadPool.RegisterWaitForSingleObject(_restoreEvent,
            (_, _) => Dispatcher.BeginInvoke(() => (MainWindow as MainWindow)?.ShowFromTray()),
            null, -1, executeOnlyOnce: true);

        base.OnStartup(e);

        // ---- main window creation inside the same net ----
        try
        {
            var window = new MainWindow();
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            WriteCrashLog("startup (main window)", ex);
            PreWindowCrashDialog(ex, "PatchDiff hit a problem while building its interface.",
                ex.Message + Environment.NewLine + Environment.NewLine +
                "Your data is safe (snapshots, reports, settings in %LOCALAPPDATA%\\PatchDiff).");
            Shutdown();
        }
    }

    // ---- naive XAML precheck: catches the two crash classes we actually shipped ----
    // (duplicate implicit style keys; attribute appended after a self-closing tag)
    static void RunXamlPrecheck()
    {
        var dir = FindXamlSourceDir();
        if (dir is null)
            return; // published single-file build: no .xaml sources present, nothing to scan

        foreach (var file in Directory.GetFiles(dir, "*.xaml", SearchOption.AllDirectories))
        {
            if (file.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part =>
                    part.StartsWith("bin", StringComparison.OrdinalIgnoreCase) ||
                    part.StartsWith("obj", StringComparison.OrdinalIgnoreCase)))
                continue;
            var text = File.ReadAllText(file);
            var name = Path.GetFileName(file);

            // class 1: two DICTIONARY-LEVEL implicit styles for the same type in one file.
            // Regex cannot tell scoped inline styles (e.g. <Path.Style> inside a
            // ControlTemplate) from dictionary keys, so parse the XML and inspect
            // only <Style> elements that are DIRECT children of a
            // <ResourceDictionary> and carry no x:Key — the only set that can
            // actually collide as resource dictionary keys.
            try
            {
                var doc = System.Xml.Linq.XDocument.Parse(text);
                var xamlNs = "http://schemas.microsoft.com/winfx/2006/xaml";
                var implicitTargets = new List<string>();
                foreach (var dict in doc.Descendants("{http://schemas.microsoft.com/winfx/2006/xaml/presentation}ResourceDictionary"))
                {
                    foreach (var style in dict.Elements("{http://schemas.microsoft.com/winfx/2006/xaml/presentation}Style"))
                    {
                        if (style.Attribute(xamlNs + "Key") is not null)
                            continue; // keyed styles may share a TargetType legally
                        var target = style.Attribute("TargetType")?.Value;
                        if (target is not null)
                            implicitTargets.Add(target);
                    }
                }
                var dup = implicitTargets.GroupBy(t => t).Where(g => g.Count() > 1)
                    .Select(g => g.Key).ToList();
                if (dup.Count > 0)
                    throw new InvalidOperationException(
                        $"{name}: duplicate implicit style key(s): {string.Join(", ", dup)}");
            }
            catch (InvalidOperationException) { throw; }
            catch (System.Xml.XmlException)
            {
                // not well-formed XML (XAML with markup extensions is still XML, so
                // this should not happen for real files) — skip the parse-based check
                // rather than block startup on the scanner itself.
            }

            // class 2: attribute after a self-closing tag
            // Check a masked copy: attribute VALUES are replaced by a placeholder
            // (quotes kept) so a legal '/>' inside a value (e.g. Text="x/>y")
            // cannot trip the pattern, while a real '/>' followed by an attribute
            // still matches.
            var textMasked = Regex.Replace(text, "\"[^\"\r\n]*\"", m => "\"@\"");
            textMasked = Regex.Replace(textMasked, "'[^'\r\n]*'", m => "'@'");
            if (Regex.IsMatch(textMasked, @"/>\s*[\w.]+="""))
                throw new InvalidOperationException(
                    $"{name}: attribute found after a self-closing tag (/> Margin=... pattern)");
        }
    }

    static string? FindXamlSourceDir()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 6 && dir is not null; i++)
        {
            var probe = System.IO.Path.Combine(dir, "Themes", "Dark.xaml");
            if (File.Exists(probe))
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        return null;
    }

    // ---- global handlers + crash log ----

    void RegisterGlobalHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            WriteCrashLog("runtime (dispatcher)", args.Exception);
            var cont = RuntimeCrashDialog(args.Exception);
            args.Handled = cont == MessageBoxResult.Yes; // "Continue" keeps the session alive where safe
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            // INTENTIONAL, no dialog: this fires while the process is terminating;
            // a MessageBox here would race shutdown and often never display.
            // The log file is the durable record (header documents this).
            if (args.ExceptionObject is Exception ex)
                WriteCrashLog("fatal (app domain, process terminating - log only)", ex);
        };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            WriteCrashLog("unobserved task", args.Exception);
            args.SetObserved();
        };
    }

    static readonly object CrashLogGate = new();

    public static void WriteCrashLog(string phase, Exception ex)
    {
        try
        {
            var dir = Path.Combine(SnapshotStore.DataRoot, LogDirName);
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, $"crash-{DateTime.Now:yyyyMMdd_HHmmss}.log");
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"time:   {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"phase:  {phase}");
            sb.AppendLine($"version:{typeof(App).Assembly.GetName().Version}");
            sb.AppendLine($"type:   {ex.GetType().FullName}");
            sb.AppendLine($"message:{ex.Message}");
            sb.AppendLine(ex.StackTrace);
            var inner = ex.InnerException;
            int depth = 1;
            while (inner is not null)
            {
                sb.AppendLine($"--- inner[{depth}] {inner.GetType().FullName}: {inner.Message}");
                sb.AppendLine(inner.StackTrace);
                inner = inner.InnerException;
                depth++;
            }
            File.WriteAllText(file, sb.ToString());
        }
        catch { /* crash logging must never throw */ }
    }

    MessageBoxResult PreWindowCrashDialog(Exception ex, string title, string body)
    {
        try
        {
            return System.Windows.MessageBox.Show(
                $"{body}{Environment.NewLine}{Environment.NewLine}Technical: {ex.GetType().Name}: {ex.Message}",
                title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch
        {
            return MessageBoxResult.None;
        }
    }

    MessageBoxResult RuntimeCrashDialog(Exception ex)
    {
        try
        {
            return MessageBox.Show(
                "Something went wrong. The error was logged and the app recovered." + Environment.NewLine + Environment.NewLine +
                "YES = keep PatchDiff running (recommended)." + Environment.NewLine +
                "NO = close PatchDiff now." + Environment.NewLine + Environment.NewLine +
                $"Technical: {ex.GetType().Name}: {ex.Message}",
                "PatchDiff", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            // Yes = continue (ESC does not map to a destructive choice on YesNo)
        }
        catch
        {
            return MessageBoxResult.None;
        }
    }
}
