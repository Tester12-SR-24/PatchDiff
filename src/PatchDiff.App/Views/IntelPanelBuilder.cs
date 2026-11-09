using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PatchDiff.Models;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace PatchDiff.Views;

public static class IntelPanelBuilder
{
    public static void Build(StackPanel panel, PatchSummary? intel)
    {
        panel.Children.Clear();
        if (intel is null)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "No patch intelligence yet — it appears here after the first detected update.",
                Foreground = Brushes.Gray,
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        void AddCard(string title, string body)
        {
            panel.Children.Add(new TextBlock
            {
                Text = title,
                Foreground = Brushes.DimGray,
                Margin = new Thickness(2, 12, 0, 4),
            });
            panel.Children.Add(new Border
            {
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#232327")),
                BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#38383f")),
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
}
