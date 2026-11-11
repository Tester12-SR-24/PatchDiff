using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using PatchDiff.Models;
using PatchDiff.Services;
using UserControl = System.Windows.Controls.UserControl;
using Brush = System.Windows.Media.Brush;

namespace PatchDiff.Views;

public partial class DiffView : UserControl
{
    readonly AssemblyDiffResult _diff;
    readonly List<SectionVm> _sections = new();
    readonly ObservableCollection<object> _items = new();

    public DiffView(AssemblyDiffResult diff)
    {
        InitializeComponent();
        _diff = diff;
        TitleText.Text = diff.Title;
        AddedRun.Text = $"+{diff.Added} added";
        RemovedRun.Text = $"−{diff.Removed} removed";
        ModifiedRun.Text = $"~{diff.Modified} modified";
        ShaRun.Text = $"baseline {diff.OldSha}… → current {diff.NewSha}…";

        // pre-compute per-type line deltas for the badges (same math as the HTML report)
        foreach (var td in diff.TypeDiffs)
        {
            int added = 0, removed = 0, modified = 0;
            var rows = new List<DiffRowVm>();
            foreach (var r in DiffService.SideBySide(td))
            {
                var ot = r.Old?.Type.ToString();
                var nt = r.New?.Type.ToString();
                if (ot == "Deleted") removed++;
                if (nt == "Inserted") added++;
                if (ot == "Modified" || nt == "Modified") modified++;
                rows.Add(new DiffRowVm
                {
                    OldText = r.Old?.Text ?? " ",
                    NewText = r.New?.Text ?? " ",
                    OldKind = ot ?? "Unchanged",
                    NewKind = nt ?? "Unchanged",
                    OldLine = r.Old?.Position is int op ? op.ToString() : "—",
                    NewLine = r.New?.Position is int np ? np.ToString() : "—",
                });
            }
            var section = new SectionVm
            {
                FullName = td.FullName,
                Kind = td.DisplayChange,
                AddBadge = added > 0 ? $"+{added}" : "",
                RemBadge = removed > 0 ? $"−{removed}" : "",
                ModBadge = Math.Max(0, modified - added) > 0 ? $"~{Math.Max(0, modified - added)}" : "",
            };
            section.Rows = rows;
            section.OnToggle = ToggleSection;
            _sections.Add(section);
        }

        SectionList.ItemsSource = _items;
        Rebuild(filterQuery: "");

        // open the first changed type by default — matches the old auto-selected UX
        if (_sections.Count > 0)
        {
            _sections[0].IsExpanded = true;
            Rebuild(filterQuery: "");
        }
    }

    /// <summary>Rebuild the flat item list from the sections, honoring expanded flags and the filter.</summary>
    void Rebuild(string filterQuery)
    {
        _items.Clear();
        foreach (var s in _sections)
        {
            if (filterQuery.Length > 0 && !s.FullName.Contains(filterQuery, StringComparison.OrdinalIgnoreCase))
                continue;
            _items.Add(s);
            if (s.IsExpanded)
                foreach (var row in s.Rows)
                    _items.Add(row);
        }
    }

    void ToggleSection(SectionVm s)
    {
        s.IsExpanded = !s.IsExpanded;
        Rebuild(FilterBox.Text.Trim());
    }

    void OnSectionClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // Preview: ListBoxItem marks MouseLeftButtonDown as handled (selection),
        // so the section toggle must run on the tunneling event.
        if ((sender as FrameworkElement)?.DataContext is SectionVm s)
            ToggleSection(s);
    }

    void OnExpandAll(object sender, RoutedEventArgs e)
    {
        foreach (var s in _sections) s.IsExpanded = true;
        Rebuild(FilterBox.Text.Trim());
    }

    void OnCollapseAll(object sender, RoutedEventArgs e)
    {
        foreach (var s in _sections) s.IsExpanded = false;
        Rebuild(FilterBox.Text.Trim());
    }

    void OnFilterChanged(object sender, TextChangedEventArgs e)
    {
        if (SectionList.ItemsSource is null)
            return;
        Rebuild(FilterBox.Text.Trim());
    }
}

/// <summary>A collapsible per-type section header in the diff view.</summary>
public sealed class SectionVm
{
    public string FullName { get; init; } = "";
    public string Kind { get; init; } = "";
    public string AddBadge { get; init; } = "";
    public string RemBadge { get; init; } = "";
    public string ModBadge { get; init; } = "";
    public bool IsExpanded { get; set; }
    public List<DiffRowVm> Rows { get; set; } = new();
    public Action<SectionVm>? OnToggle { get; set; }
}

public sealed class DiffRowVm
{
    public string OldText { get; init; } = "";
    public string NewText { get; init; } = "";
    public string OldKind { get; init; } = "";
    public string NewKind { get; init; } = "";
    public string OldLine { get; init; } = "—";
    public string NewLine { get; init; } = "—";
}
