using Avalonia.Controls;
using System.IO;

namespace PatchDiff.Views;

public partial class SourcePane : UserControl
{
    /// <summary>
    /// For runtime-XAML support only — normal usage goes through the parameterized
    /// constructor; the source pane is not usable without source text.
    /// </summary>
    public SourcePane()
    {
        InitializeComponent();
    }

    public SourcePane(string typeName, string source, string assemblyName)
    {
        InitializeComponent();
        TypeNameText.Text = typeName;
        MetaText.Text = assemblyName;
        SourceText.Text = source;
    }
}
