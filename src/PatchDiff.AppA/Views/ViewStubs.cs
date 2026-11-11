using Avalonia.Controls;
using PatchDiff.Models;
using PatchDiff.Services;

namespace PatchDiff.Views;

// Temporary stubs to satisfy references during the port.
// These will be fleshed out as the views are migrated.

public class DiffView : UserControl
{
    public DiffView(AssemblyDiffResult diff) { }
}

public class BrowseView : UserControl
{
    public BrowseView(AssemblySnapshot snap, WatchedDll w) { }
}

public class HistoryView : UserControl
{
    public event Action<AssemblyDiffResult>? DiffRequested;
    public event Action<WatchedDll>? MatrixRequested;
    public HistoryView(WatchedDll w) { }
}

public class MatrixView : UserControl
{
    public event Action<AssemblyDiffResult, string>? DiffRequested;
    public MatrixView(WatchedDll w, List<AssemblySnapshot> revs) { }
}

public class SourcePane : UserControl
{
    public SourcePane(string typeName, string source, string assemblyName) { }
}
