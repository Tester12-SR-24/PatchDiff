using System.IO;
using System.Windows;
using System.Windows.Controls;
using PatchDiff.Models;
using PatchDiff.Services;
using UserControl = System.Windows.Controls.UserControl;
using Brush = System.Windows.Media.Brush;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;

namespace PatchDiff.Views;

public partial class BrowseView : UserControl
{
    readonly AssemblySnapshot _snapshot;
    readonly WatchedDll _watch;
    Window _owner => Window.GetWindow(this)!;

    public event Action<AssemblyDiffResult>? DiffRequested;

    public BrowseView(AssemblySnapshot snapshot, WatchedDll watch)
    {
        InitializeComponent();
        _snapshot = snapshot;
        _watch = watch;

        var name = Path.GetFileName(snapshot.AssemblyPath);
        SourceMeta.Text = $"v{snapshot.AssemblyVersion} · {snapshot.Types.Count} types · baseline {snapshot.Sha256[..12]}…";

        Loaded += async (_, _) =>
        {
            var snap = _snapshot;
            var (root, firstType) = await System.Threading.Tasks.Task.Run(() => BuildTree(snap));
            TypeTree.ItemsSource = new[] { root };
            root.IsExpanded = true;
            if (firstType is not null)
            {
                TypeTree.UpdateLayout();
                TypeTree.SelectItem(firstType);
            }
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

    static (ExplorerNode root, ExplorerNode? firstType) BuildTree(AssemblySnapshot snapshot)
    {
        ExplorerNode? firstType = null;
        var root = new ExplorerNode
        {
            Kind = "Assembly",
            Glyph = "⚙",
            Header = $"{Path.GetFileName(snapshot.AssemblyPath)} — {snapshot.Types.Count} types",
            IsExpanded = true,
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
                Glyph = "▭",
                Header = nsGroup.Key,
                IsExpanded = true,
            };

            foreach (var type in nsGroup.OrderBy(t => t.FullName, StringComparer.Ordinal))
            {
                var typeNode = new ExplorerNode
                {
                    Kind = "Type",
                    Glyph = "◆",
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

                    AddGroup("─ Fields", fields);
                    AddGroup("◐ Properties", props);
                    AddGroup("▶ Methods", methods);
                    AddGroup("⚡ Events", events);
                }

                firstType ??= typeNode;
                nsNode.Children.Add(typeNode);
            }

            root.Children.Add(nsNode);
        }

        return (root, firstType);
    }

    static string ExtractMemberName(string signature)
    {
        var trimmed = signature.Trim();
        var stop = trimmed.IndexOfAny(['(', ';', '{']);
        if (stop < 0)
            stop = trimmed.Length;
        var head = trimmed[..stop].TrimEnd();
        var parts = head.Split(' ');
        return parts.Length > 0 ? parts[^1] : trimmed;
    }

    ExplorerNode? SelectedNode => TypeTree.SelectedItem as ExplorerNode;

    void OnTreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not ExplorerNode node || node.Source is null)
            return;

        SourceHeader.Text = node.FullName ?? node.Header;
        SourceMeta.Text = $"{_watch.Name} · decompiled baseline";
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

    void OnDiffPrev(object sender, RoutedEventArgs e) => RequestDiff(againstCurrent: false);
    void OnDiffCurrent(object sender, RoutedEventArgs e) => RequestDiff(againstCurrent: true);

    void RequestDiff(bool againstCurrent)
    {
        try
        {
            var store = new RevisionStore();
            var revs = store.List(_watch.Path).OrderBy(r => r.CapturedUtc).ToList();
            if (revs.Count < 1)
            {
                MessageBox.Show(_owner, "No revisions archived yet for this DLL.", "PatchDiff");
                return;
            }

            var newSnap = againstCurrent
                ? SnapshotStore.Load(_watch.Path) ?? _snapshot
                : _snapshot;
            var oldSnap = newSnap;
            if (againstCurrent)
            {
                var prev = revs.FirstOrDefault(r => r.Sha256 != newSnap.Sha256);
                oldSnap = prev is null ? newSnap : store.LoadRevision(_watch.Path, prev.Sha256) ?? newSnap;
            }
            else
            {
                var idx = revs.FindIndex(r => r.Sha256 == newSnap.Sha256);
                oldSnap = idx > 0 ? store.LoadRevision(_watch.Path, revs[idx - 1].Sha256) ?? newSnap : newSnap;
            }

            var diff = new DiffService().Compare(oldSnap, newSnap,
                title: $"{Path.GetFileName(_watch.Path)} ({oldSnap.AssemblyVersion} → {newSnap.AssemblyVersion})");
    
            if (diff.HasChanges)
                DiffRequested?.Invoke(diff);
            else
                MessageBox.Show(_owner, "No type changes between those revisions.", "PatchDiff");
        }
        catch (Exception ex)
        {
            MessageBox.Show(_owner, $"Compare failed: {ex.Message}", "PatchDiff");
        }
    }

    void OnExportSingle(object sender, RoutedEventArgs e)
    {
        if (SelectedNode is not { Kind: "Type" } node || node.FullName is null)
        {
            MessageBox.Show(_owner, "Select a type first.", "PatchDiff");
            return;
        }
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export decompiled type as…",
            InitialDirectory = Path.GetDirectoryName(_watch.Path),
            FileName = MainWindow.ExportFileName(node.FullName),
            Filter = "C# source (*.cs)|*.cs",
        };
        if (dlg.ShowDialog(_owner) != true)
            return;
        File.WriteAllText(dlg.FileName, node.Source);
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
    public bool IsExpanded { get; set; }
}

public static class TreeViewHelper
{
    public static void SelectItem(this System.Windows.Controls.TreeView tree, object item)
    {
        SelectItem(tree.ItemContainerGenerator, item);
    }

    static void SelectItem(System.Windows.Controls.ItemContainerGenerator generator, object item)
    {
        if (generator.ContainerFromItem(item) is System.Windows.Controls.TreeViewItem container)
        {
            container.IsSelected = true;
            return;
        }
        foreach (object obj in generator.Items)
        {
            if (generator.ContainerFromItem(obj) is System.Windows.Controls.TreeViewItem parent)
            {
                parent.IsExpanded = true;
                parent.UpdateLayout();
                SelectItem(parent.ItemContainerGenerator, item);
            }
        }
    }
}
