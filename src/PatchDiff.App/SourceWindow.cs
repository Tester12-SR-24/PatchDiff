using System.Windows;

namespace PatchDiff;

public partial class SourceWindow : Window
{
    public SourceWindow(string typeName, string assemblyName, string source)
    {
        InitializeComponent();
        Title = $"{typeName} — PatchDiff";
        TypeNameText.Text = typeName;
        AssemblyText.Text = assemblyName;
        SourceText.Text = source;
    }
}
