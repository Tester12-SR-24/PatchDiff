using System.Windows;
using System.Windows.Controls;
using UserControl = System.Windows.Controls.UserControl;

namespace PatchDiff;

public partial class SourcePane : UserControl
{
    public SourcePane(string typeName, string source, string assemblyName)
    {
        InitializeComponent();
        TypeNameText.Text = typeName;
        AssemblyText.Text = assemblyName;
        SourceText.Text = source;
    }
}
