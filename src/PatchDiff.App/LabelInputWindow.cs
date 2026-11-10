using System.Windows;

namespace PatchDiff;

public partial class LabelInputWindow : Window
{
    public string Value { get; private set; } = "";

    public LabelInputWindow(string title, string labelText, string initialValue)
    {
        InitializeComponent();
        Title = title;
        LabelText.Text = labelText;
        ValueBox.Text = initialValue ?? "";
        ValueBox.SelectAll();
        Loaded += (_, _) => { ValueBox.Focus(); DarkTitleBar.Enable(this); };
    }

    void OnSave(object sender, RoutedEventArgs e)
    {
        Value = ValueBox.Text.Trim();
        DialogResult = true;
    }

    void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
