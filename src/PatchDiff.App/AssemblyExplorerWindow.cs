using System.Windows;
using System.Windows.Controls;
using PatchDiff.Models;
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

/// <summary>
/// ILSpy-style assembly browser: namespaces → types → members, with the
/// decompiled source of the selected type shown alongside.
/// </summary>
public partial class AssemblyExplorerWindow : Window
{
    readonly AssemblySnapshot _snapshot;

    public AssemblyExplorerWindow(AssemblySnapshot snapshot)
    {
        InitializeComponent();
        _snapshot = snapshot;

        var name = System.IO.Path.GetFileName(snapshot.AssemblyPath);
        Title = $"{name} — PatchDiff Explorer";
        AssemblyNameText.Text = $"{name}  ({snapshot.Sha256[..12]}…)";
        AssemblyMetaText.Text = $"v{snapshot.AssemblyVersion} · {snapshot.Types.Count} types · baseline captured {snapshot.CapturedUtc.ToLocalTime():yyyy-MM-dd HH:mm}";

        // Build the tree off the UI thread — big assemblies have thousands of nodes.
        Loaded += async (_, _) =>
        {
            var snap = _snapshot;
            var root = await System.Threading.Tasks.Task.Run(() => BuildTree(snap));
            TypeTree.ItemsSource = new[] { root };
        };
    }

    static string Short(string? fullName)
    {
        if (string.IsNullOrEmpty(fullName))
            return "?";
        var name = fullName;
        var dot = name.LastIndexOf('.');
        if (dot >= 0)
            name = name[(dot + 1)..];
        var tick = name.IndexOf('`');
        if (tick > 0)
            name = name[..tick];
        return name;
    }

    static ExplorerNode BuildTree(AssemblySnapshot snapshot)
    {
        var root = new ExplorerNode
        {
            Kind = "Assembly",
            Glyph = "\u2699",
            Header = $"{System.IO.Path.GetFileName(snapshot.AssemblyPath)} — {snapshot.Types.Count} types",
        };

        foreach (var nsGroup in snapshot.Types
                     .GroupBy(t =>
                     {
                         var dot = t.FullName.LastIndexOf('.');
                         return dot < 0 ? "(global)" : t.FullName[..dot];
                     })
                     .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var nsNode = new ExplorerNode
            {
                Kind = "Namespace",
                Glyph = "\u25a6",
                Header = nsGroup.Key,
            };

            foreach (var type in nsGroup.OrderBy(t => t.FullName, StringComparer.Ordinal))
            {
                var typeNode = new ExplorerNode
                {
                    Kind = "Type",
                    Glyph = "\u2666",
                    Header = Short(type.FullName),
                    FullName = type.FullName,
                    Source = type.Source,
                };

                if (type.Signatures is { Count: > 0 } sigs)
                {
                    List<string> fields = new(), props = new(), methods = new(), events = new();
                    foreach (var sig in sigs)
                    {
                        if (sig.Contains(" event "))
                            events.Add(sig);
                        else if (sig.Contains('{'))
                            props.Add(sig);
                        else if (sig.Contains('('))
                            methods.Add(sig);
                        else
                            fields.Add(sig);
                    }

                    void AddGroup(string title, List<string> items)
                    {
                        if (items.Count == 0)
                            return;
                        var group = new ExplorerNode { Kind = "Group", Header = $"{title}  ({items.Count})" };
                        foreach (var item in items.OrderBy(x => x, StringComparer.Ordinal))
                            group.Children.Add(new ExplorerNode
                            {
                                Kind = "Member",
                                Glyph = " ",
                                Header = item,
                                Source = type.Source,
                                Highlight = ExtractMemberName(item),
                            });
                        typeNode.Children.Add(group);
                    }

                    AddGroup("\u2012 Fields", fields);
                    AddGroup("\u25c8 Properties", props);
                    AddGroup("\u25b6 Methods", methods);
                    AddGroup("\u26a1 Events", events);
                }

                nsNode.Children.Add(typeNode);
            }

            root.Children.Add(nsNode);
        }

        return root;
    }

    static string ExtractMemberName(string signature)
    {
        // Last identifier before '(' / ';' / '{' depending on member kind.
        var trimmed = signature.Trim();
        var stop = trimmed.IndexOfAny(['(', ';', '{']);
        if (stop < 0)
            stop = trimmed.Length;
        var head = trimmed[..stop];
        var parts = head.TrimEnd().Split(' ');
        return parts.Length > 0 ? parts[^1] : trimmed;
    }

    void OnTreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not ExplorerNode node || node.Source is null)
            return;

        SourceHeader.Text = node.FullName ?? node.Header;
        SourceText.Text = node.Source;
        SourceText.ScrollToHome();

        if (node.Highlight is { Length: > 0 } member)
        {
            var idx = SourceText.Text.IndexOf(member, StringComparison.Ordinal);
            if (idx >= 0)
            {
                SourceText.Select(idx, member.Length);
                SourceText.CaretIndex = idx;
                SourceText.ScrollToLine(SourceText.GetLineIndexFromCharacterIndex(idx));
            }
        }
    }
}

public sealed class ExplorerNode
{
    public string Kind { get; init; } = "";
    public string Glyph { get; init; } = "";
    public string Header { get; init; } = "";
    public string? FullName { get; init; }
    public string? Source { get; init; }
    public string? Highlight { get; init; }
    public List<ExplorerNode> Children { get; } = new();
}
