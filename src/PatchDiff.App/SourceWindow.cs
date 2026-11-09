using System.Windows;
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
