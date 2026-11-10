using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using PatchDiff.Models;
using PatchDiff.Services;
using UserControl = System.Windows.Controls.UserControl;
using Brush = System.Windows.Media.Brush;

namespace PatchDiff.Views;

public partial class HistoryView : UserControl
{
    sealed class RevRow
    {
        public required RevisionInfo Info;
        public required string DllPath;
        public string ShaShort => Info.Sha256[..12];
        public string WhenLocal => Info.CapturedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        public string VersionLabel => "v" + Info.AssemblyVersion;
        public string TypeLabel => $"{Info.TypeCount} types archived";
    }

    readonly WatchedDll _watch;
    Window OwnerWindow => Window.GetWindow(this)!;

    public event Action<AssemblyDiffResult>? DiffRequested;
    public event Action<WatchedDll>? MatrixRequested;

    public HistoryView(WatchedDll watch)
    {
        InitializeComponent();
        _watch = watch;
        TitleText.Text = $"Revision history — {watch.Name}";
        var revs = new RevisionStore().List(watch.Path);
        MetaText.Text = revs.Count == 0
            ? "No revisions archived yet."
            : $"{revs.Count} revision{(revs.Count == 1 ? "" : "s")} archived (every analyzed version is kept).";
        RevList.ItemsSource = revs.OrderByDescending(r => r.CapturedUtc)
            .Select(r => new RevRow { Info = r, DllPath = watch.Path })
            .ToList();
    }

    void OnDiffPrev(object sender, RoutedEventArgs e) => Compare((sender as FrameworkElement)?.DataContext as RevRow, againstCurrent: false);
    void OnDiffCurrent(object sender, RoutedEventArgs e) => Compare((sender as FrameworkElement)?.DataContext as RevRow, againstCurrent: true);

    async void Compare(RevRow? row, bool againstCurrent)
    {
        if (row is null)
            return;
        try
        {
            var store = new RevisionStore();
            var newSnap = await System.Threading.Tasks.Task.Run(() =>
                againstCurrent ? SnapshotStore.Load(_watch.Path) : store.LoadRevision(_watch.Path, row.Info.Sha256));
            if (newSnap is null)
            {
                MessageBox.Show(OwnerWindow, "Could not load that revision.", "PatchDiff");
                return;
            }

            AssemblySnapshot oldSnap;
            var revs = store.List(_watch.Path).OrderBy(r => r.CapturedUtc).ToList();
            if (againstCurrent)
            {
                var prev = revs.OrderByDescending(r => r.CapturedUtc)
                    .FirstOrDefault(r => r.Sha256 != newSnap.Sha256);
                if (prev is null)
                {
                    MessageBox.Show(OwnerWindow, "No earlier revision to compare with.", "PatchDiff");
                    return;
                }
                oldSnap = store.LoadRevision(_watch.Path, prev.Sha256)!;
            }
            else
            {
                var idx = revs.FindIndex(r => r.Sha256 == row.Info.Sha256);
                if (idx <= 0)
                {
                    MessageBox.Show(OwnerWindow, "This is the oldest revision — nothing before it.", "PatchDiff");
                    return;
                }
                oldSnap = store.LoadRevision(_watch.Path, revs[idx - 1].Sha256)!;
            }

            var diff = new DiffService().Compare(oldSnap, newSnap,
                title: $"{Path.GetFileName(_watch.Path)} ({oldSnap.AssemblyVersion} → {newSnap.AssemblyVersion})");
    
            if (diff.HasChanges)
                DiffRequested?.Invoke(diff);
            else
                MessageBox.Show(OwnerWindow, "No type changes between those revisions.", "PatchDiff");
        }
        catch (Exception ex)
        {
            MessageBox.Show(OwnerWindow, $"Compare failed: {ex.Message}", "PatchDiff");
        }
    }

    void OnMatrix(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is RevRow)
            MatrixRequested?.Invoke(_watch);
    }

    void OnRestore(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not RevRow row)
            return;
        try
        {
            var dir = Path.GetDirectoryName(_watch.Path);
            var dlg = new SaveFileDialog
            {
                Title = "Restore this revision as…",
                InitialDirectory = dir,
                FileName = Path.GetFileNameWithoutExtension(_watch.Name) + "_" + row.ShaShort + Path.GetExtension(_watch.Name),
                Filter = "Assemblies (*.dll)|*.dll",
            };
            if (dlg.ShowDialog(OwnerWindow) != true)
                return;

            var store = new RevisionStore();
            var restored = store.Restore(_watch.Path, row.Info.Sha256, dlg.FileName);
            Process.Start(new ProcessStartInfo(Path.GetDirectoryName(restored)!) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(OwnerWindow, $"Restore failed: {ex.Message}", "PatchDiff");
        }
    }
}
