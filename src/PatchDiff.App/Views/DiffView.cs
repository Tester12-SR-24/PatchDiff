using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using DiffPlex.DiffBuilder.Model;
using PatchDiff.Models;
using PatchDiff.Services;
using UserControl = System.Windows.Controls.UserControl;
using Brush = System.Windows.Media.Brush;

namespace PatchDiff.Views;

public partial class DiffView : UserControl
{
    readonly AssemblyDiffResult _diff;
    readonly List<DiffRowVm> _rows = new();

    public DiffView(AssemblyDiffResult diff)
    {
        InitializeComponent();
        _diff = diff;
        TitleText.Text = diff.Title;
        AddedRun.Text = $"+{diff.Added} added";
        RemovedRun.Text = $"−{diff.Removed} removed";
        ModifiedRun.Text = $"~{diff.Modified} modified";
        ShaRun.Text = $"baseline {diff.OldSha}… → current {diff.NewSha}…";
        TypesList.ItemsSource = diff.TypeDiffs;
        FilterBox.TextChanged += (_, _) => ApplyFilter();
        if (diff.TypeDiffs.Count > 0)
            TypesList.SelectedIndex = 0;
    }

    void ApplyFilter()
    {
        if (TypesList.ItemsSource is null)
            return;
        var view = (ICollectionView)CollectionViewSource.GetDefaultView(TypesList.ItemsSource);
        var query = FilterBox.Text.Trim();
        view.Filter = o => query.Length == 0 ||
                           ((TypeDiff)o).FullName.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    void OnFilterChanged(object sender, TextChangedEventArgs e) { }

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
}

public sealed class DiffRowVm
{
    public string OldText { get; init; } = "";
    public string NewText { get; init; } = "";
    public string OldKind { get; init; } = "";
    public string NewKind { get; init; } = "";
}
