using GoMidi.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GoMidi.Views;

/// <summary>
/// Latency, chord handling, scheduled playback, hotkey, run privilege and storage.
/// The page is cached, so leaving it only suspends the clock rather than
/// tearing down the view model.
/// </summary>
public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => ViewModel.Resume();
        Unloaded += (_, _) => ViewModel.Suspend();
    }

    public SettingsViewModel ViewModel { get; } = new();

    public static bool Not(bool value) => !value;

    public static string ScheduleLabel(bool isScheduled) => isScheduled ? "取消定时" : "定时播放";

    public static Visibility VisibleIf(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
}
