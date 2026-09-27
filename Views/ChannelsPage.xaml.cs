using GoMidi.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace GoMidi.Views;

/// <summary>
/// Eight channel routing cards: window, track, transpose and enablement.
/// The page is cached, so its view model (and its subscription to the
/// current-song changes) stays alive for the lifetime of the app.
/// </summary>
public sealed partial class ChannelsPage : Page
{
    public ChannelsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => ViewModel.RefreshWindows();
    }

    public ChannelsViewModel ViewModel { get; } = new();
}
