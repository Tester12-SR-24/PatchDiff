using System.Windows;
using System.Windows.Controls;
using System.IO;
using System.Windows.Media;
using PatchDiff.Models;
using PatchDiff.Services;

namespace PatchDiff.Views;

/// <summary>Revision diff matrix: rows/columns = revisions, each cell lazily computed on click.</summary>
public partial class MatrixView : UserControl
{
    sealed class MatrixRevision
    {
        public required AssemblySnapshot Snapshot;
        public string Label => string.IsNullOrWhiteSpace(UserLabel) ? DefaultLabel : UserLabel!;
        public string DefaultLabel { get; set; } = "";
        public string? UserLabel { get; set; }
    }

    readonly List<MatrixRevision> _revisions = new();
    readonly LabelsStore _labels = new();
    readonly WatchedDll _watch;
    Window OwnerWindow => Window.GetWindow(this)!;

    public event Action<AssemblyDiffResult, string>? DiffRequested;

    // Soft cap: the matrix is an N x N grid of live buttons; without a bound,
    // a long history materializes thousands of cells at once. Show the most
    // recent window; the caption tells the user what was left out.
    const int MaxRevisions = 30;

    public MatrixView(WatchedDll watch, IReadOnlyList<AssemblySnapshot> revisions)
    {
        InitializeComponent();
        _watch = watch;
        var ordered = revisions.OrderBy(r => r.CapturedUtc).ToList();
        if (ordered.Count > MaxRevisions)
        {
            SubtitleText.Text += $"  Showing the most recent {MaxRevisions} of {ordered.Count} revisions.";
            ordered = ordered.Skip(ordered.Count - MaxRevisions).ToList();
        }
        foreach (var snap in ordered)
        {
            _revisions.Add(new MatrixRevision
            {
                Snapshot = snap,
                DefaultLabel = $"v{snap.AssemblyVersion} · {snap.CapturedUtc.ToLocalTime():MMM d}",
                UserLabel = _labels.GetLabel(watch.Path, snap.Sha256),
            });
        }
        BuildGrid();
    }

    void BuildGrid()
    {
        MatrixGrid.Children.Clear();
        MatrixGrid.RowDefinitions.Clear();
        MatrixGrid.ColumnDefinitions.Clear();

        int n = _revisions.Count;
        MatrixGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        for (int c = 0; c < n; c++)
            MatrixGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 90 });
        MatrixGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        AddHeader(0, 0, "");
        for (int c = 0; c < n; c++)
            AddHeader(0, c + 1, HeaderText(_revisions[c]), editable: c);

        for (int r = 0; r < n; r++)
        {
            MatrixGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            AddHeader(r + 1, 0, HeaderText(_revisions[r]));

            for (int c = 0; c < n; c++)
            {
                if (r == c)
                {
                    var diagCell = MakeCell();
                    diagCell.Content = "—";
                    diagCell.Background = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x22));
                    diagCell.Foreground = new SolidColorBrush(Color.FromRgb(0x4a, 0x4d, 0x58));
                    diagCell.Cursor = System.Windows.Input.Cursors.Arrow;
                    Grid.SetRow(diagCell, r + 1);
                    Grid.SetColumn(diagCell, c + 1);
                    MatrixGrid.Children.Add(diagCell);
                }
                else if (c < r)
                {
                    var upper = FindCellButtonAt(r + 1, c + 1);
                    var lo = MakeCell();
                    lo.Content = "…";
                    lo.Click += (_, _) => _ = ComputeCellAsync(c, r);
                    Grid.SetRow(lo, r + 1);
                    Grid.SetColumn(lo, c + 1);
                    MatrixGrid.Children.Add(lo);
                }
                else
                {
                    var up = MakeCell();
                    up.Content = "… compute";
                    up.Click += (_, _) => _ = ComputeCellAsync(r, c);
                    Grid.SetRow(up, r + 1);
                    Grid.SetColumn(up, c + 1);
                    MatrixGrid.Children.Add(up);
                }
            }
        }
    }

    void AddHeader(int row, int col, string text, int editable = -1)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x9a, 0x9a, 0xa9)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(4),
        };
        var stack = new StackPanel { Margin = new Thickness(4) };
        stack.Children.Add(tb);
        if (editable >= 0)
        {
            var rename = new Button
            {
                Content = "✎",
                FontSize = 9,
                Padding = new Thickness(3, 0, 3, 0),
                Background = Brushes.Transparent,
                Foreground = Brushes.Gray,
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = "Rename this revision",
            };
            int idx = editable;
            rename.Click += (_, _) => RenameRevision(idx);
            stack.Children.Add(rename);
        }
        Grid.SetRow(stack, row);
        Grid.SetColumn(stack, col);
        MatrixGrid.Children.Add(stack);
    }

    static System.Windows.Controls.Button MakeCell()
    {
        return new System.Windows.Controls.Button
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            Padding = new Thickness(6, 5, 6, 5),
            Background = new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x2b)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xc9, 0xd1, 0xe0)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2b, 0x2b, 0x31)),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(2),
            Cursor = System.Windows.Input.Cursors.Hand,
        };
    }

    System.Windows.Controls.Button? FindCellButtonAt(int gridRow, int gridCol)
    {
        foreach (var child in MatrixGrid.Children.OfType<System.Windows.Controls.Button>())
        {
            if (Grid.GetRow(child) == gridRow && Grid.GetColumn(child) == gridCol)
                return child;
        }
        return null;
    }

    async System.Threading.Tasks.Task ComputeCellAsync(int row, int col)
    {
        var a = _revisions[row];
        var b = _revisions[col];
        var clicked = FindCellButtonAt(row + 1, col + 1);
        var mirror = FindCellButtonAt(col + 1, row + 1);
        if (clicked is null)
            return;

        // second click on an already-computed cell opens the diff tab
        if (clicked.Tag is AssemblyDiffResult cached)
        {
            DiffRequested?.Invoke(cached, a.Label + " \u2192 " + b.Label);
            return;
        }

        clicked.Content = "\u2026";
        if (mirror is not null)
            mirror.Content = "\u2026";
        clicked.IsEnabled = false;
        if (mirror is not null)
            mirror.IsEnabled = false;

        try
        {
            var diff = await System.Threading.Tasks.Task.Run(() =>
            {
                var d = new DiffService().Compare(a.Snapshot, b.Snapshot,
                    title: "{a} \u2192 {b}".Replace("{a}", Path.GetFileName(_watch.Path) + ": " + a.Label)
                                          .Replace("{b}", b.Label));
                return d;
            });

            var text = "+{a2} \u2212{r} ~{m}".Replace("{a2}", diff.Added.ToString())
                                             .Replace("{r}", diff.Removed.ToString())
                                             .Replace("{m}", diff.Modified.ToString());
            var brush = new SolidColorBrush(diff.HasChanges
                ? Color.FromRgb(0x35, 0x40, 0x30)
                : Color.FromRgb(0x2e, 0x2e, 0x35));
            clicked.Content = text;
            clicked.Background = brush;
            clicked.Tag = diff;
            if (mirror is not null)
            {
                mirror.Content = text;
                mirror.Background = brush;
                mirror.Tag = diff;
            }
        }
        catch (Exception ex)
        {
            clicked.Content = "error";
            MessageBox.Show(OwnerWindow, $"Compare failed: {ex.Message}", "PatchDiff");
        }
        finally
        {
            clicked.IsEnabled = true;
            if (mirror is not null)
                mirror.IsEnabled = true;
        }
    }

    string HeaderText(MatrixRevision rev) => rev.Label;

    void RenameRevision(int index)
    {
        var rev = _revisions[index];
        var dlg = new LabelInputWindow("Rename revision", "Label for this revision:", rev.UserLabel ?? rev.DefaultLabel);
        if (dlg.ShowDialog() != true)
            return;
        rev.UserLabel = dlg.Value;
        _labels.SetLabel(_watch.Path, rev.Snapshot.Sha256, dlg.Value);
        BuildGrid(); // rebuild so the header reflects the new label
    }
}
