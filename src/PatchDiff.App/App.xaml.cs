using System.Windows;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Clipboard = System.Windows.Clipboard;
using DataFormats = System.Windows.DataFormats;
using DragEventArgs = System.Windows.DragEventArgs;
using MessageBox = System.Windows.MessageBox;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxResult = System.Windows.MessageBoxResult;

namespace PatchDiff;

public partial class App : Application
{
    private Mutex? _instanceMutex;
    private EventWaitHandle? _restoreEvent;

    protected override void OnStartup(StartupEventArgs e)
    {
        _instanceMutex = new Mutex(true, @"Local\PatchDiff.SingleInstance", out var createdNew);
        _restoreEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\PatchDiff.Restore");

        if (!createdNew)
        {
            // Already running in the tray — poke the existing instance and quit.
            _restoreEvent.Set();
            Shutdown();
            return;
        }

        ThreadPool.RegisterWaitForSingleObject(_restoreEvent,
            (_, _) => Dispatcher.BeginInvoke(() => (MainWindow as MainWindow)?.ShowFromTray()),
            null, -1, executeOnlyOnce: true);

        base.OnStartup(e);
    }
}
