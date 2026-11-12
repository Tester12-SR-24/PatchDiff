using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace PatchDiff;

/// <summary>Avalonia's tiny message box — enough for the dialogs the WPF app used.</summary>
public static class MessageBox
{
    public static void Show(Window? owner, string message, string title = "PatchDiff")
    {
        var box = new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false,
            Background = new SolidColorBrush(Avalonia.Media.Color.FromRgb(0x1e, 0x1e, 0x22)),
            Foreground = Avalonia.Media.Brushes.White,
        };
        var panel = new StackPanel { Margin = new Thickness(18) };
        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = Avalonia.Media.Brushes.White };
        var ok = new Button { Content = "OK", Margin = new Thickness(0, 14, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        ok.Click += (_, _) => box.Close();
        panel.Children.Add(text);
        panel.Children.Add(ok);
        box.Content = panel;

        if (owner is not null)
        {
            box.ShowDialog(owner);
            return;
        }
        var active = (Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)
            ?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
        if (active is not null)
            box.ShowDialog(active);
        else
            box.Show();
    }

    public static void Show(string message, string title = "PatchDiff")
    {
        Show(null, message, title);
    }

    /// <summary>Yes/No question. Resolves true only when the user clicks Yes
    /// (closing the window or clicking No resolves false).</summary>
    public static Task<bool> Confirm(Window? owner, string message, string title = "PatchDiff")
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var box = new Window
        {
            Title = title,
            Width = 440,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false,
            Background = new SolidColorBrush(Avalonia.Media.Color.FromRgb(0x1e, 0x1e, 0x22)),
            Foreground = Avalonia.Media.Brushes.White,
        };
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = Avalonia.Media.Brushes.White });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 14, 0, 0) };
        var no = new Button { Content = "No" };
        no.Classes.Add("secondary");
        var yes = new Button { Content = "Yes" };
        yes.Classes.Add("primary");
        no.Click += (_, _) => { tcs.TrySetResult(false); box.Close(); };
        yes.Click += (_, _) => { tcs.TrySetResult(true); box.Close(); };
        buttons.Children.Add(no);
        buttons.Children.Add(yes);
        panel.Children.Add(buttons);
        box.Content = panel;
        box.Closed += (_, _) => tcs.TrySetResult(false);

        if (owner is not null)
        {
            _ = box.ShowDialog(owner);
            return tcs.Task;
        }
        var active = (Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)
            ?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
        if (active is not null)
            _ = box.ShowDialog(active);
        else
            box.Show();
        return tcs.Task;
    }
}
