using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PatchDiff.Models;
using PatchDiff.Services;
using UserControl = System.Windows.Controls.UserControl;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace PatchDiff.Views;

public sealed class PinRow
{
    public required PinEntry Entry;
    public string TypeName => Entry.TypeName;
    public string StatusText { get; init; } = "tracked";
    public Brush ChangedBrush { get; init; } = new SolidColorBrush(Color.FromRgb(0x27, 0x4a, 0x33));
    public bool IsPlaceholder { get; init; }
}

public partial class NotesView : UserControl
{
    readonly PinsStore _pins = new();
    readonly WatchedDll? _watch;
    List<PinRow> _rows = new();

    public NotesView(WatchedDll? watch)
    {
        InitializeComponent();
        _watch = watch;
        Reload();
    }

    public void Reload()
    {
        var lastDiff = _watch is null
            ? null
            : (App.Current.MainWindow as MainWindow)?.GetLastDiffFor(_watch.Path);

        _rows = _pins.List()
            .Where(p => _watch is null || string.Equals(p.AssemblyPath, _watch.Path, StringComparison.OrdinalIgnoreCase))
            .Select(p =>
            {
                var changed = lastDiff?.TypeDiffs.Any(d =>
                    string.Equals(d.FullName, p.TypeName, StringComparison.Ordinal)) == true;
                return new PinRow
                {
                    Entry = p,
                    StatusText = changed ? "changed this patch" : "tracked",
                    ChangedBrush = new SolidColorBrush(changed
                        ? Color.FromRgb(0x8a, 0x6d, 0x1f)
                        : Color.FromRgb(0x27, 0x4a, 0x33)),
                };
            })
            .ToList();

        PinList.ItemsSource = _rows;
        if (_rows.Count == 0)
        {
            PinList.ItemsSource = new[] { new PinRow { Entry = new PinEntry { TypeName = "(no pins yet — use ★ Pin in the Browse tab)" }, IsPlaceholder = true } };
            NoteBox.IsEnabled = false;
        }
        else
        {
            NoteBox.IsEnabled = true;
            PinList.SelectedIndex = 0;
        }
    }

    void OnPinSelected(object sender, SelectionChangedEventArgs e)
    {
        if (PinList.SelectedItem is PinRow row && row.Entry.PinnedUtc != default)
            NoteBox.Text = row.Entry.Note;
    }

    void OnSaveNote(object sender, RoutedEventArgs e)
    {
        if (PinList.SelectedItem is PinRow row && row.Entry.PinnedUtc != default)
        {
            _pins.AddOrUpdate(row.Entry.AssemblyPath, row.Entry.TypeName, NoteBox.Text);
            Reload();
        }
    }

    void OnUnpin(object sender, RoutedEventArgs e)
    {
        if (PinList.SelectedItem is PinRow row && row.Entry.PinnedUtc != default)
        {
            _pins.Remove(row.Entry.AssemblyPath, row.Entry.TypeName);
            Reload();
        }
    }
}
