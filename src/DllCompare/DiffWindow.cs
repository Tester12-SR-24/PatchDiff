using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using DllCompare.Models;
using DllCompare.Services;

namespace DllCompare;

public partial class DiffWindow : Window
{
    readonly AssemblyDiffResult _diff;
    readonly List<DiffRowVm> _rows = new();

    public DiffWindow(AssemblyDiffResult diff)
    {
        InitializeComponent();
        _diff = diff;
        TitleText.Text = diff.Title;
        CountsText.Text = $"+{diff.Added} added · −{diff.Removed} removed · ~{diff.Modified} modified   " +
                          $"({diff.OldSha}… → {diff.NewSha}…)";
        TypesList.ItemsSource = diff.TypeDiffs;
        if (diff.TypeDiffs.Count > 0)
            TypesList.SelectedIndex = 0;
    }

    void OnTypeSelected(object sender, SelectionChangedEventArgs e)
    {
        if (TypesList.SelectedItem is not TypeDiff td)
            return;
        TypeHeader.Text = $"{td.FullName} — {td.DisplayChange}";

        _rows.Clear();
        foreach (var r in DiffService.SideBySide(td))
        {
            _rows.Add(new DiffRowVm
            {
                OldText = r.Old?.Text ?? " ",
                NewText = r.New?.Text ?? " ",
                OldKind = r.Old?.Type.ToString() ?? "Unchanged",
                NewKind = r.New?.Type.ToString() ?? "Unchanged",
            });
        }
        DiffList.ItemsSource = null;
        DiffList.ItemsSource = _rows;
    }

    void OnOpenReport(object sender, RoutedEventArgs e)
    {
        var report = SnapshotStore.WriteHtmlReport(_diff);
        Process.Start(new ProcessStartInfo(report) { UseShellExecute = true });
    }
}

public sealed class DiffRowVm
{
    public string OldText { get; init; } = "";
    public string NewText { get; init; } = "";
    public string OldKind { get; init; } = "";
    public string NewKind { get; init; } = "";
}
