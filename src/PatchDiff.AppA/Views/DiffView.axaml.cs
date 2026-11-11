using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using PatchDiff.Models;
using PatchDiff.Services;
using System.Collections.ObjectModel;

namespace PatchDiff.Views;

public partial class DiffView : UserControl
{
    readonly AssemblyDiffResult _diff;
    readonly ObservableCollection<SectionVm> _sections = new();

    public DiffView(AssemblyDiffResult diff)
    {
        InitializeComponent();
        _diff = diff;
        TitleText.Text = diff.Title;
        MetaText.Text = $"+{diff.Added} added · −{diff.Removed} removed · ~{diff.Modified} modified · baseline {diff.OldSha}… → current {diff.NewSha}…";

        // per-type line deltas for the badges (same math as the HTML report)
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
                    OldNo = r.Old?.Position is int op ? op.ToString() : "—",
                    OldText = r.Old?.Text ?? " ",
                    OldKind = ot ?? "Unchanged",
                    NewNo = r.New?.Position is int np ? np.ToString() : "—",
                    NewText = r.New?.Text ?? " ",
                    NewKind = nt ?? "Unchanged",
                });
            }
            // monospace (≈7.2px/char at 11.5px) + gutter — gives rows a real horizontal
            // extent so each pane's horizontal scrollbar actually appears
            var maxLen = rows.Max(r => Math.Max(r.OldText.Length, r.NewText.Length));
            foreach (var r in rows) r.MinWidth = 44 + 7.2 * maxLen;
            _sections.Add(new SectionVm
            {
                FullName = td.FullName,
                Kind = td.DisplayChange,
                AddBadge = added > 0 ? $"+{added}" : "",
                DelBadge = removed > 0 ? $"−{removed}" : "",
                ModBadge = Math.Max(0, modified - added) > 0 ? $"~{Math.Max(0, modified - added)}" : "",
                Rows = rows,
            });
        }

        Rebuild(filterQuery: "");
        // open the first changed type by default — matches the old auto-selected UX
        if (_sections.Count > 0)
        {
            _sections[0].IsExpanded = true;
            Rebuild(filterQuery: "");
        }
    }

    void Rebuild(string filterQuery)
    {
        DiffList.ItemsSource = null;
        DiffList.ItemsSource = _sections.Where(s =>
            filterQuery.Length == 0 || s.FullName.Contains(filterQuery, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    void OnToggleSection(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.DataContext is SectionVm s)
        {
            s.IsExpanded = !s.IsExpanded;
            Rebuild(FilterBox.Text?.Trim() ?? "");
        }
    }

    void OnExpandAll(object? sender, RoutedEventArgs e)
    {
        foreach (var s in _sections) s.IsExpanded = true;
        Rebuild(FilterBox.Text?.Trim() ?? "");
    }

    void OnCollapseAll(object? sender, RoutedEventArgs e)
    {
        foreach (var s in _sections) s.IsExpanded = false;
        Rebuild(FilterBox.Text?.Trim() ?? "");
    }

    void OnFilter(object? sender, RoutedEventArgs e) => Rebuild(FilterBox.Text?.Trim() ?? "");
}

/// <summary>A collapsible per-type section header, with badges and the paired rows.</summary>
public sealed class SectionVm
{
    public string FullName { get; init; } = "";
    public string Kind { get; init; } = "";
    public string AddBadge { get; init; } = "";
    public string DelBadge { get; init; } = "";
    public string ModBadge { get; init; } = "";
    public string ChevronText => IsExpanded ? "▾" : "▸";
    public bool IsExpanded { get; set; }
    public List<DiffRowVm> Rows { get; init; } = new();
}

/// <summary>One paired line: previous column + new column, aligned in one row.</summary>
public sealed class DiffRowVm
{
    public string OldNo { get; init; } = "—";
    public string OldText { get; init; } = "";
    public string OldKind { get; init; } = "";
    public string NewNo { get; init; } = "—";
    public string NewText { get; init; } = "";
    public string NewKind { get; init; } = "";
    public double MinWidth { get; set; }
}
