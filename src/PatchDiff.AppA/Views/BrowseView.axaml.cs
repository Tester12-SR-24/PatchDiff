using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using PatchDiff.Models;
using PatchDiff.Services;
using System.Diagnostics;
using System.IO;

namespace PatchDiff.Views;

public partial class BrowseView : UserControl
{
    readonly AssemblySnapshot _snapshot;
    readonly WatchedDll _watch;
    ExplorerNode? _root;

    public event Action<AssemblyDiffResult>? DiffRequested;

    public BrowseView(AssemblySnapshot snapshot, WatchedDll watch)
    {
        InitializeComponent();
        _snapshot = snapshot;
        _watch = watch;

        var name = Path.GetFileName(snapshot.AssemblyPath);
        TitleText.Text = name;
        MetaText.Text = $"v{snapshot.AssemblyVersion} · {snapshot.Types.Count} types · baseline {snapshot.Sha256[..12]}…";

        _ = Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var (root, firstType) = await Task.Run(() => BuildTree(snapshot));
            _root = root;
            TypeTree.ItemsSource = new[] { root };
            root.IsExpanded = true;
            if (firstType is not null)
            {
                TypeTree.SelectedItem = firstType;
            }
        });
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

                    AddGroup("Fields", fields);
                    AddGroup("Properties", props);
                    AddGroup("Methods", methods);
                    AddGroup("Events", events);
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

    void OnTreeSelection(object? sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0 || e.AddedItems[0] is not ExplorerNode node || node.Source is null)
            return;

        SourceText.Text = node.Source;
        if (node.Highlight is { Length: > 0 } member)
        {
            var idx = node.Source.IndexOf(member, StringComparison.Ordinal);
            if (idx >= 0)
            {
                SourceText.CaretIndex = idx;
                SourceText.SelectionStart = idx;
                SourceText.SelectionEnd = idx + member.Length;
            }
        }
    }

    void OnFilter(object? sender, RoutedEventArgs e)
    {
        if (_root is null) return;
        var q = TreeFilter.Text?.Trim() ?? "";
        if (q.Length == 0)
        {
            TypeTree.ItemsSource = new[] { _root };
            return;
        }
        var root = new ExplorerNode
        {
            Kind = "Assembly",
            Header = $"Filter: {q}",
            IsExpanded = true,
        };
        foreach (var ns in _root.Children)
        {
            var nsNode = new ExplorerNode { Kind = "Namespace", Glyph = "▭", Header = ns.Header, IsExpanded = true };
            foreach (var type in ns.Children.Where(t => t.Header?.Contains(q, StringComparison.OrdinalIgnoreCase) == true))
                nsNode.Children.Add(type);
            if (nsNode.Children.Count > 0)
                root.Children.Add(nsNode);
        }
        TypeTree.ItemsSource = new[] { root };
    }

    async void OnDiffCurrent(object? sender, RoutedEventArgs e)
    {
        var w = _watch;
        try
        {
            var store = new RevisionStore();
            var revs = store.List(w.Path).OrderBy(r => r.CapturedUtc).ToList();
            if (revs.Count < 1)
            {
                MessageBox.Show($"No revisions archived yet for this DLL.", "PatchDiff");
                return;
            }

            var newSnap = SnapshotStore.Load(w.Path) ?? _snapshot;
            var prev = revs.FirstOrDefault(r => r.Sha256 != newSnap.Sha256);
            var oldSnap = prev is null ? newSnap : store.LoadRevision(w.Path, prev.Sha256) ?? newSnap;

            var diff = new DiffService().Compare(oldSnap, newSnap,
                title: $"{Path.GetFileName(w.Path)} ({oldSnap.AssemblyVersion} → {newSnap.AssemblyVersion})");
            if (diff.HasChanges)
                DiffRequested?.Invoke(diff);
            else
                MessageBox.Show("No type changes between those revisions.");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Compare failed: {ex.Message}");
        }
    }

    async void OnDiffPrev(object? sender, RoutedEventArgs e)
    {
        var w = _watch;
        try
        {
            var store = new RevisionStore();
            var revs = store.List(w.Path).OrderBy(r => r.CapturedUtc).ToList();
            if (revs.Count < 1)
            {
                MessageBox.Show("No revisions archived yet for this DLL.");
                return;
            }

            var newSnap = _snapshot;
            var idx = revs.FindIndex(r => r.Sha256 == newSnap.Sha256);
            var oldSnap = idx > 0 ? store.LoadRevision(w.Path, revs[idx - 1].Sha256) ?? newSnap : newSnap;

            var diff = new DiffService().Compare(oldSnap, newSnap,
                title: $"{Path.GetFileName(w.Path)} ({oldSnap.AssemblyVersion} → {newSnap.AssemblyVersion})");
            if (diff.HasChanges)
                DiffRequested?.Invoke(diff);
            else
                MessageBox.Show("No type changes between those revisions.");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Compare failed: {ex.Message}");
        }
    }

    async void OnExportSingle(object? sender, RoutedEventArgs e)
    {
        if (TypeTree.SelectedItem is not ExplorerNode node || node.FullName is null)
        {
            MessageBox.Show("Select a type first.");
            return;
        }
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null) return;
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export decompiled type as…",
            DefaultExtension = ".cs",
            SuggestedFileName = MainWindow.ExportFileName(node.FullName),
            FileTypeChoices = new[] { new FilePickerFileType("C# source") { Patterns = new[] { "*.cs" } } },
        });
        if (file is null) return;
        await using var stream = await file.OpenWriteAsync();
        using var writer = new StreamWriter(stream);
        await writer.WriteAsync(node.Source ?? "");
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
    public bool IsExpanded { get; set; }
    public List<ExplorerNode> Children { get; } = new();
    public bool HasChildren => Children.Count > 0;
}
