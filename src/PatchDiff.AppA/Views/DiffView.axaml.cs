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
    // Assigned only when the view is constructed via the parameterized constructor;
    // the parameterless constructor exists for runtime-XAML support only.
    readonly AssemblyDiffResult _diff = null!;
    readonly ObservableCollection<SectionVm> _sections = new();
    Action? _saveToHistory;

    /// <summary>Shows the "Save to History" button (manual compares, or watch diffs when
    /// auto-save history is off). One-shot: the button hides after saving.</summary>
    public void EnableSaveToHistory(Action save)
    {
        _saveToHistory = save;
        SaveHistoryButton.IsVisible = true;
    }

    void OnSaveToHistory(object? sender, RoutedEventArgs e)
    {
        _saveToHistory?.Invoke();
        SaveHistoryButton.IsVisible = false;
    }

    /// <summary>
    /// For runtime-XAML support only — normal usage goes through the parameterized
    /// constructor; the diff view is not usable without an <see cref="AssemblyDiffResult"/>.
    /// </summary>
    public DiffView()
    {
        InitializeComponent();
    }

    public DiffView(AssemblyDiffResult diff)
    {
        InitializeComponent();
        _diff = diff;
        TitleText.Text = diff.Title;

        // per-type line deltas for the badges (same math as the HTML report).
        // SectionVm does not raise change notifications, so IsExpanded must be set
        // at creation time — mutating it after the first Rebuild() is invisible to
        // the already-realized ItemsControl bindings.
        var sectionIndex = 0;
        // Line-level totals for the header chips. These must match the per-section
        // pills exactly (the header used to report TYPE counts instead, so a diff with
        // 17 added lines across 2 added types claimed "+2 added").
        int totalAdded = 0, totalRemoved = 0, totalModified = 0;
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
            // Per-side panes: each side owns its horizontal scrollbar, so a long
            // line on one side never stretches or misaligns the other side.
            totalAdded += added;
            totalRemoved += removed;
            totalModified += modified;
            _sections.Add(new SectionVm
            {
                FullName = td.FullName,
                Kind = td.DisplayChange,
                AddBadge = added > 0 ? $"+{added}" : "",
                DelBadge = removed > 0 ? $"−{removed}" : "",
                ModBadge = modified > 0 ? $"~{modified}" : "",
                OldRows = rows.Select(r => new LineVm { No = r.OldNo, Text = r.OldText, Kind = r.OldKind }).ToList(),
                NewRows = rows.Select(r => new LineVm { No = r.NewNo, Text = r.NewText, Kind = r.NewKind }).ToList(),
                // open the first changed type by default — matches the old auto-selected UX
                IsExpanded = sectionIndex == 0,
            });
            sectionIndex++;
        }

        BuildMeta(diff, totalAdded, totalRemoved, totalModified);
        Rebuild(filterQuery: "");
    }

    /// <summary>Header meta as colored line-delta chips + baseline→current hashes.
    /// Counts are LINE deltas summed from the sections, so they always agree with the
    /// per-type pills underneath.</summary>
    void BuildMeta(AssemblyDiffResult diff, int added, int removed, int modified)
    {
        MetaPanel.Children.Add(DeltaChip($"+{added} lines added", "#8fe0a0", "#1e3a2a"));
        MetaPanel.Children.Add(DeltaChip($"−{removed} lines removed", "#ffb0b0", "#3a1e1e"));
        MetaPanel.Children.Add(DeltaChip($"~{modified} lines modified", "#ffe0a0", "#3a2f1e"));
        HashText.Text = $"baseline {Short(diff.OldSha)} → current {Short(diff.NewSha)}";
    }

    static string Short(string sha) => string.IsNullOrEmpty(sha) ? "unknown" : sha[..Math.Min(12, sha.Length)];

    static Avalonia.Controls.Border DeltaChip(string text, string fg, string bg) => new()
    {
        CornerRadius = new Avalonia.CornerRadius(9),
        Padding = new Avalonia.Thickness(9, 3),
        Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(bg)),
        Child = new TextBlock
        {
            Text = text,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(fg)),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        },
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
    };

    void Rebuild(string filterQuery)
    {
        DiffList.ItemsSource = null;
        DiffList.ItemsSource = _sections.Where(s =>
            filterQuery.Length == 0 || s.FullName.Contains(filterQuery, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    // Expand/collapse only flips SectionVm.IsExpanded; the bindings update in place.
    // (Rebuilding the ItemsSource here used to reset the scroll position to the top.)
    void OnToggleSection(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.DataContext is SectionVm s)
            s.IsExpanded = !s.IsExpanded;
    }

    void OnExpandAll(object? sender, RoutedEventArgs e)
    {
        foreach (var s in _sections) s.IsExpanded = true;
    }

    void OnCollapseAll(object? sender, RoutedEventArgs e)
    {
        foreach (var s in _sections) s.IsExpanded = false;
    }

    void OnFilter(object? sender, RoutedEventArgs e) => Rebuild(FilterBox.Text?.Trim() ?? "");
}

/// <summary>A collapsible per-type section header, with badges and the per-side rows.
/// Implements INotifyPropertyChanged so expand/collapse updates the bound IsVisible
/// and chevron in place (no ItemsSource rebuild, so the scroll position is kept).</summary>
public sealed class SectionVm : System.ComponentModel.INotifyPropertyChanged
{
    public string FullName { get; init; } = "";
    public string Kind { get; init; } = "";

    /// <summary>"ADDED"/"REMOVED"/"MODIFIED" chip label; empty for unchanged types.</summary>
    public string KindChip => Kind is "Added" or "Removed" or "Modified" ? Kind.ToUpperInvariant() : "";
    public string AddBadge { get; init; } = "";
    public string DelBadge { get; init; } = "";
    public string ModBadge { get; init; } = "";
    public string ChevronText => IsExpanded ? "▾" : "▸";

    bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            PropertyChanged?.Invoke(this, new(nameof(IsExpanded)));
            PropertyChanged?.Invoke(this, new(nameof(ChevronText)));
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    /// <summary>PREV pane lines. Same count/heights as NewRows → the panes stay aligned.</summary>
    public List<LineVm> OldRows { get; init; } = new();
    /// <summary>NOW pane lines.</summary>
    public List<LineVm> NewRows { get; init; } = new();
}

/// <summary>Intermediate paired line from DiffService.SideBySide, split into per-side
/// LineVms when a section is built.</summary>
public sealed record DiffRowVm
{
    public string OldNo { get; init; } = "—";
    public string OldText { get; init; } = "";
    public string OldKind { get; init; } = "";
    public string NewNo { get; init; } = "—";
    public string NewText { get; init; } = "";
    public string NewKind { get; init; } = "";
}

/// <summary>One decompiled line for one side of a diff section.</summary>
public sealed class LineVm
{
    public string No { get; init; } = "—";
    public string Text { get; init; } = "";
    public string Kind { get; init; } = "Unchanged";
}
