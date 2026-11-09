using System.Windows;
using PatchDiff.Services;

namespace PatchDiff;

public partial class SettingsWindow : Window
{
    public StoredSettings Result { get; private set; }

    public SettingsWindow(StoredSettings current, WatchManager mgr)
    {
        InitializeComponent();
        Result = new StoredSettings
        {
            PollSeconds = current.PollSeconds,
            BalloonOnPatch = current.BalloonOnPatch,
            StartMinimized = current.StartMinimized,
            ArchiveKeep = current.ArchiveKeep,
        };
        BalloonOnPatch.IsChecked = Result.BalloonOnPatch;
        PollSeconds.Text = Result.PollSeconds.ToString();
        StartMinimized.IsChecked = Result.StartMinimized;
        ArchiveKeep.SelectedIndex = Result.ArchiveKeep switch
        {
            10 => 1,
            50 => 2,
            100 => 3,
            _ => 0,
        };
        VersionText.Text = $"PatchDiff {MainWindow.CurrentVersion()} · data: {SnapshotStore.DataRoot}";
    }

    void OnApply(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(PollSeconds.Text.Trim(), out var poll) || poll < 2)
            poll = 10;
        Result.PollSeconds = poll;
        Result.BalloonOnPatch = BalloonOnPatch.IsChecked == true;
        Result.StartMinimized = StartMinimized.IsChecked == true;
        Result.ArchiveKeep = ArchiveKeep.SelectedIndex switch
        {
            1 => 10,
            2 => 50,
            3 => 100,
            _ => 0,
        };
        DialogResult = true;
    }

    void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
