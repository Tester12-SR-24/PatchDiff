using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;

namespace PatchDiff;

public class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // Crash resilience: published builds must never die silently. Route every
        // unhandled exception through one reporter that logs to the local data
        // directory and (for UI-thread faults) shows a dialog instead of vanishing.
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Report("ui-thread", e.Exception);
            e.Handled = true; // keep the window alive; the failure is logged and surfaced
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Report("app-domain", e.ExceptionObject as Exception ?? new Exception("Unknown fatal error"));
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Report("unobserved-task", e.Exception);
            e.SetObserved();
        };

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var main = new MainWindow();
            if (main.StartHiddenToTray)
            {
                // Tray start: keep the window hidden — the tray icon opens it. Explicit
                // shutdown so zero visible windows doesn't tear the app down.
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            }
            else
            {
                desktop.MainWindow = main;
            }

            // A second launch hands over through the instance pipe instead of starting a
            // rival process: the open window comes forward and says why nothing new opened.
            SingleInstance.StartServer(() => Dispatcher.UIThread.Post(main.ReportAlreadyRunning));
        }
        base.OnFrameworkInitializationCompleted();
    }

    static void Report(string phase, Exception exception)
    {
        try
        {
            var dir = Path.Combine(PatchDiff.Services.SnapshotStore.DataRoot, "logs");
            Directory.CreateDirectory(dir);
            // ToString() walks inner exceptions — TargetInvocationException and
            // aggregate wrappers are useless without the inner root cause.
            File.AppendAllText(
                Path.Combine(dir, $"crash-{DateTime.Now:yyyyMMdd}.log"),
                $"{DateTime.Now:HH:mm:ss} [{phase}] {exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { /* logging must never throw */ }

        try
        {
            Dispatcher.UIThread.Post(() =>
            {
                var dialog = new Window
                {
                    Title = "PatchDiff error",
                    Width = 460,
                    SizeToContent = SizeToContent.Height,
                    CanResize = false,
                    Background = new SolidColorBrush(Avalonia.Media.Color.Parse("#131923")),
                    Padding = new Thickness(20),
                };
                var panel = new StackPanel { Spacing = 10 };
                panel.Children.Add(new TextBlock { Text = "Something went wrong", FontSize = 16, FontWeight = FontWeight.SemiBold });
                panel.Children.Add(new TextBlock
                {
                    Text = $"{exception.GetType().Name}: {exception.Message}",
                    FontSize = 10,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Avalonia.Media.Color.Parse("#ffb0b6")),
                });
                panel.Children.Add(new TextBlock
                {
                    Text = "Details were written to the PatchDiff logs folder. The app keeps running; if this repeats, restart PatchDiff.",
                    FontSize = 9,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Avalonia.Media.Color.Parse("#96a2b4")),
                });
                var close = new Button { Content = "Close", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
                close.Classes.Add("primary");
                close.Click += (_, _) => dialog.Close();
                panel.Children.Add(close);
                dialog.Content = panel;
                var owner = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
                if (owner is not null)
                    _ = dialog.ShowDialog(owner);
                else
                    dialog.Show();
            });
        }
        catch { /* dialog is best-effort */ }
    }
}
