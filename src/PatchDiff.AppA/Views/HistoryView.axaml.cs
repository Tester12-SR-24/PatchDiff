using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using PatchDiff.Models;
using PatchDiff.Services;
using System.Diagnostics;
using System.IO;

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
        public string Title => $"{ShaShort} · {WhenLocal} · {VersionLabel}";
        public string Sub => TypeLabel;
    }

    readonly WatchedDll _watch;

    public event Action<AssemblyDiffResult>? DiffRequested;

    public HistoryView(WatchedDll watch)
    {
        Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
        _watch = watch;
        TitleText.Text = $"Revision history — {watch.Name}";
        var revs = new RevisionStore().List(watch.Path);
        MetaText.Text = revs.Count == 0
            ? "No revisions archived yet."
            : $"{revs.Count} revision{(revs.Count == 1 ? "" : "s")} archived (every analyzed version is kept).";
        RevisionList.ItemsSource = revs.OrderByDescending(r => r.CapturedUtc)
            .Select(r => new RevRow { Info = r, DllPath = watch.Path })
            .ToList();
    }

    async Task Compare(RevRow? row, bool againstCurrent)
    {
        if (row is null) return;
        try
        {
            var store = new RevisionStore();
            var newSnap = await Task.Run(() =>
                againstCurrent ? SnapshotStore.Load(_watch.Path) : store.LoadRevision(_watch.Path, row.Info.Sha256));
            if (newSnap is null)
            {
                MessageBox.Show("Could not load that revision.");
                return;
            }

            AssemblySnapshot oldSnap;
            var revs = store.List(_watch.Path).OrderBy(r => r.CapturedUtc).ToList();
            if (againstCurrent)
            {
                if (row.Info.Sha256 == newSnap.Sha256)
                {
                    MessageBox.Show("That revision is the current baseline — nothing to compare.");
                    return;
                }
                var clicked = store.LoadRevision(_watch.Path, row.Info.Sha256);
                if (clicked is null)
                {
                    MessageBox.Show("Could not load that revision.");
                    return;
                }
                oldSnap = clicked;
            }
            else
            {
                var idx = revs.FindIndex(r => r.Sha256 == row.Info.Sha256);
                if (idx <= 0)
                {
                    MessageBox.Show("This is the oldest revision — nothing before it.");
                    return;
                }
                oldSnap = store.LoadRevision(_watch.Path, revs[idx - 1].Sha256)!;
            }

            var diff = new DiffService().Compare(oldSnap, newSnap,
                title: $"{Path.GetFileName(_watch.Path)} ({oldSnap.AssemblyVersion} → {newSnap.AssemblyVersion})");
            if (diff.HasChanges)
                DiffRequested?.Invoke(diff);
            else
                MessageBox.Show("No type changes between those revisions.");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Compare failed: {ex.Message}");
        }
    }

    async void OnDiffPrev(object? sender, RoutedEventArgs e)
    {
        if (RevisionList.SelectedItem is not RevRow row) return;
        await Compare(row, againstCurrent: false);
    }

    async void OnDiffCurrent(object? sender, RoutedEventArgs e)
    {
        if (RevisionList.SelectedItem is not RevRow row) return;
        await Compare(row, againstCurrent: true);
    }

    async void OnRestore(object? sender, RoutedEventArgs e)
    {
        if (RevisionList.SelectedItem is not RevRow row) return;
        try
        {
            var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storage is null) return;
            var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Restore this revision as…",
                SuggestedFileName = Path.GetFileNameWithoutExtension(_watch.Name) + "_" + row.ShaShort + Path.GetExtension(_watch.Name),
                FileTypeChoices = new[] { new FilePickerFileType("Assemblies") { Patterns = new[] { "*.dll" } } },
            });
            if (file is null) return;
            var store = new RevisionStore();
            var restored = store.Restore(_watch.Path, row.Info.Sha256, file.Path.LocalPath);
            Process.Start(new ProcessStartInfo(Path.GetDirectoryName(restored)!) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Restore failed: {ex.Message}");
        }
    }
}
