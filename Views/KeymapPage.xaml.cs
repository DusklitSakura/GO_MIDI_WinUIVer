using GoMidi.Controls;
using GoMidi.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace GoMidi.Views;

/// <summary>Keymap editor: scheme management, note range and per-note rebinding.</summary>
public sealed partial class KeymapPage : Page
{
    private bool _wired;

    public KeymapPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    public KeymapViewModel ViewModel { get; } = new();

    private void OnLoaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (_wired)
        {
            SyncKeyboard();
            return;
        }

        _wired = true;

        ViewModel.KeymapChanged += (_, _) => SyncKeyboard();

        Keyboard.PitchSelected += (_, pitch) => ViewModel.SelectPitch(pitch);
        Keyboard.BindingCaptured += (_, args) => ViewModel.SetBinding(args.Pitch, args.VkCode, args.Modifier);
        Keyboard.BindingCleared += (_, pitch) => ViewModel.ClearBinding(pitch);

        SyncKeyboard();
    }

    private void SyncKeyboard()
    {
        Keyboard.SetKeyManager(ViewModel.Keys);
        Keyboard.SetRange((int)ViewModel.MinPitch, (int)ViewModel.MaxPitch);
        Keyboard.Refresh();
    }
}
