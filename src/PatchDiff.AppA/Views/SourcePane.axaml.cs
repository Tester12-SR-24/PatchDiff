using Avalonia.Controls;
using System.IO;

namespace PatchDiff.Views;

public partial class SourcePane : UserControl
{
    public SourcePane(string typeName, string source, string assemblyName)
    {
        Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
        TypeNameText.Text = typeName;
        MetaText.Text = assemblyName;
        SourceText.Text = source;
    }
}
