using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using PatchDiff.Models;
using PatchDiff.Services;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Clipboard = System.Windows.Clipboard;
using DataFormats = System.Windows.DataFormats;
using DragEventArgs = System.Windows.DragEventArgs;
using MessageBox = System.Windows.MessageBox;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxResult = System.Windows.MessageBoxResult;

namespace PatchDiff;

public partial class DiffWindow : Window
{
    readonly AssemblyDiffResult _diff;
    readonly List<DiffRowVm> _rows = new();

    public DiffWindow(AssemblyDiffResult diff)
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
        BuildIntelPanel(diff.Intelligence);

        if (diff.TypeDiffs.Count > 0)
            TypesList.SelectedIndex = 0;

        Loaded += (_, _) =>
        {
            if (Owner is not null && ActualHeight > Owner.ActualHeight)
                Height = Owner.ActualHeight - 40;
        };
    }

    void BuildIntelPanel(PatchSummary? intel)
    {
        if (intel is null)
        {
            IntelPanel.Children.Add(new TextBlock
            {
                Text = "No intelligence data for this comparison.",
                Foreground = System.Windows.Media.Brushes.Gray,
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        void AddCard(string title, string body)
        {
            IntelPanel.Children.Add(new TextBlock
            {
                Text = title,
                Foreground = System.Windows.Media.Brushes.DimGray,
                Margin = new Thickness(2, 12, 0, 4),
            });
            IntelPanel.Children.Add(new System.Windows.Controls.Border
            {
                Background = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#232327")),
                BorderBrush = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#38383f")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8),
                Child = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap, LineHeight = 20 },
            });
        }

        if (intel.PlainTerms.Count > 0)
            AddCard("IN PLAIN TERMS", string.Join("\n", intel.PlainTerms.Select(t => "• " + t)));

        if (intel.Numbers.Count > 0)
        {
            var body = string.Join("\n", intel.Numbers.Take(12).Select(n =>
                $"{n.Member}\n    {n.OldText} → {n.NewText}  [{n.Direction} {Math.Abs(n.PercentDelta):0.#}%]"));
            AddCard($"NUMBER CHANGES ({intel.Numbers.Count})", body);
        }
        if (intel.Heat.Count > 0)
        {
            var body = string.Join("\n", intel.Heat.Take(6).Select(h => $"{h.Namespace} — {h.Count} ({h.Share:0}%)"));
            AddCard("MOST TOUCHED", body);
        }
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
