using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using PatchDiff.Models;
using PatchDiff.Services;
using UserControl = System.Windows.Controls.UserControl;

namespace PatchDiff.Views;

public partial class DiffView : UserControl
{
    readonly AssemblyDiffResult _diff;
    readonly List<SectionVm> _sections = new();
    readonly ObservableCollection<object> _oldItems = new();
    readonly ObservableCollection<object> _newItems = new();

    public DiffView(AssemblyDiffResult diff)
    {
        InitializeComponent();
        _diff = diff;
        TitleText.Text = diff.Title;
        AddedRun.Text = $"+{diff.Added} added";
        RemovedRun.Text = $"−{diff.Removed} removed";
        ModifiedRun.Text = $"~{diff.Modified} modified";
        ShaRun.Text = $"baseline {diff.OldSha}… → current {diff.NewSha}…";

        // per-type line deltas for the badges (same math as the HTML report)
        foreach (var td in diff.TypeDiffs)
        {
            int added = 0, removed = 0, modified = 0;
            var oldRows = new List<SideRowVm>();
            var newRows = new List<SideRowVm>();
            foreach (var r in DiffService.SideBySide(td))
            {
                var ot = r.Old?.Type.ToString();
                var nt = r.New?.Type.ToString();
                if (ot == "Deleted") removed++;
                if (nt == "Inserted") added++;
                if (ot == "Modified" || nt == "Modified") modified++;

                // both panes always get a row per diff row (placeholder when the side
                // has no line) — identical vertical extents keep the shared vertical
                // scrollbar exact
                oldRows.Add(new SideRowVm
                {
                    Line = r.Old?.Position is int op ? op.ToString() : "—",
                    Text = r.Old?.Text ?? " ",
                    Kind = ot ?? "Unchanged",
                });
                newRows.Add(new SideRowVm
                {
                    Line = r.New?.Position is int np ? np.ToString() : "—",
                    Text = r.New?.Text ?? " ",
                    Kind = nt ?? "Unchanged",
                });
            }
            var maxLen = oldRows.Concat(newRows).Select(r => r.Text.Length).DefaultIfEmpty(0).Max();
            // monospace (Consolas 12px ≈ 7.2px/char) + gutter — gives rows a real horizontal
            // extent so each pane's horizontal scrollbar actually appears
            var minW = 44 + 7.2 * maxLen;
            foreach (var r in oldRows) ((SideRowVm)r).MinWidth = minW;
            foreach (var r in newRows) ((SideRowVm)r).MinWidth = minW;
            var section = new SectionVm
            {
                FullName = td.FullName,
                Kind = td.DisplayChange,
                AddBadge = added > 0 ? $"+{added}" : "",
                RemBadge = removed > 0 ? $"−{removed}" : "",
                ModBadge = Math.Max(0, modified - added) > 0 ? $"~{Math.Max(0, modified - added)}" : "",
            };
            section.OldRows = oldRows;
            section.NewRows = newRows;
            _sections.Add(section);
        }

        OldList.ItemsSource = _oldItems;
        NewList.ItemsSource = _newItems;
        Rebuild(filterQuery: "");

        // open the first changed type by default — matches the old auto-selected UX
        if (_sections.Count > 0)
        {
            _sections[0].IsExpanded = true;
            Rebuild(filterQuery: "");
        }
        Dispatcher.BeginInvoke(HookSharedVerticalScroll, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    ScrollViewer? _oldSv, _newSv;
    bool _syncing;

    void HookSharedVerticalScroll()
    {
        _oldSv = FindDescendant<ScrollViewer>(OldList);
        _newSv = FindDescendant<ScrollViewer>(NewList);
        if (_oldSv is null || _newSv is null) return;
        _oldSv.ScrollChanged += (_, e) => OnPaneScrolled(_oldSv, _newSv, e);
        _newSv.ScrollChanged += (_, e) => OnPaneScrolled(_newSv, _oldSv, e);
        VScroll.Maximum = _oldSv.ScrollableHeight;
        VScroll.ViewportSize = _oldSv.ViewportHeight;
    }

    void OnPaneScrolled(ScrollViewer source, ScrollViewer other, ScrollChangedEventArgs e)
    {
        if (_syncing) return;
        _syncing = true;
        try
        {
            if (e.VerticalChange != 0)
                other.ScrollToVerticalOffset(source.VerticalOffset);
            VScroll.Maximum = source.ScrollableHeight;
            VScroll.ViewportSize = source.ViewportHeight;
            VScroll.Value = source.VerticalOffset;
        }
        finally { _syncing = false; }
    }

    void OnVScroll(object sender, System.Windows.Controls.Primitives.ScrollEventArgs e)
    {
        if (_oldSv is null || _newSv is null || _syncing) return;
        _syncing = true;
        try
        {
            _oldSv.ScrollToVerticalOffset(VScroll.Value);
            _newSv.ScrollToVerticalOffset(VScroll.Value);
        }
        finally { _syncing = false; }
    }

    static T? FindDescendant<T>(DependencyObject d) where T : DependencyObject
    {
        if (d is T t) return t;
        int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(d);
        for (int i = 0; i < n; i++)
        {
            var r = FindDescendant<T>(System.Windows.Media.VisualTreeHelper.GetChild(d, i));
            if (r is not null) return r;
        }
        return null;
    }

    /// <summary>Rebuild both panes from the sections, honoring expanded flags and the filter.
    /// Sections are shared between the panes; rows are per-side. Each pane scrolls independently.</summary>
    void Rebuild(string filterQuery)
    {
        _oldItems.Clear();
        _newItems.Clear();
        foreach (var s in _sections)
        {
            if (filterQuery.Length > 0 && !s.FullName.Contains(filterQuery, StringComparison.OrdinalIgnoreCase))
                continue;
            _oldItems.Add(s);
            _newItems.Add(s);
            if (s.IsExpanded)
            {
                foreach (var row in s.OldRows) _oldItems.Add(row);
                foreach (var row in s.NewRows) _newItems.Add(row);
            }
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
        if (OldList.ItemsSource is null)
            return;
        Rebuild(FilterBox.Text.Trim());
    }
}

/// <summary>A collapsible per-type section header, shared by both diff panes.</summary>
public sealed class SectionVm
{
    public string FullName { get; init; } = "";
    public string Kind { get; init; } = "";
    public string AddBadge { get; init; } = "";
    public string RemBadge { get; init; } = "";
    public string ModBadge { get; init; } = "";
    public bool IsExpanded { get; set; }
    public List<SideRowVm> OldRows { get; set; } = new();
    public List<SideRowVm> NewRows { get; set; } = new();
}

/// <summary>One line of one side (previous or new) in the independent-scroll diff panes.</summary>
public sealed class SideRowVm
{
    public string Line { get; init; } = "—";
    public string Text { get; init; } = "";
    public string Kind { get; init; } = "";
    public double MinWidth { get; set; }
}
