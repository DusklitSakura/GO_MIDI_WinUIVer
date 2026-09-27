using GoMidi.Core;
using GoMidi.Views;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using System.Runtime.InteropServices;
using Windows.Graphics;

namespace GoMidi;

/// <summary>
/// Shell window: custom title bar, Mica backdrop, and a NavigationView that
/// switches between the player, channel, keymap and settings surfaces. The
/// title bar subtitle mirrors the live status line.
/// </summary>
public sealed partial class MainWindow : Microsoft.UI.Xaml.Window
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hWnd);

    private bool _initialized;

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        // Resolve against the app directory: the process working directory is not
        // guaranteed to be the install folder for an unpackaged app.
        string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(iconPath))
        {
            AppWindow.SetIcon(iconPath);
        }

        ResizeToDesignSize();

        // Subscribe before the first page is created: constructing the player
        // page can already raise a notification, and it must not be lost.
        App.NotificationRequested += OnNotificationRequested;

        AppServices.Current.StatusChanged += (_, text) => UpdateSubtitle(text);
        UpdateSubtitle(AppServices.Current.StatusText);

        Nav.SelectedItem = Nav.MenuItems[0];

        if (App.ConsumePendingNotification() is { } pending)
        {
            ShowNotification(pending);
        }

        Activated += (_, _) =>
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;
            AppServices.Current.AttachWindow(this);
        };

        Closed += (_, _) =>
        {
            App.NotificationRequested -= OnNotificationRequested;
            AppServices.Current.Shutdown();
        };
    }

    /// <summary>
    /// WinUI has no <c>SizeToContent</c>; without an explicit size the window
    /// opens at a generic default. The design size is restored from config and
    /// converted from DIPs to the physical pixels <c>AppWindow.Resize</c> wants.
    /// </summary>
    private void ResizeToDesignSize()
    {
        AppConfigSize saved = AppServices.Current.WindowSize;
        int widthDip = saved.Width > 0 ? saved.Width : 1320;
        int heightDip = saved.Height > 0 ? saved.Height : 880;

        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        double scale = GetDpiForWindow(hwnd) / 96.0;
        if (scale <= 0)
        {
            scale = 1.0;
        }

        AppWindow.Resize(new SizeInt32((int)(widthDip * scale), (int)(heightDip * scale)));
    }

    /// <summary>
    /// The privilege level is appended to every status line: whether keys reach
    /// the game depends on it, and it is easy to forget which mode you launched.
    /// </summary>
    private void UpdateSubtitle(string text) =>
        AppTitleBar.Subtitle = text + AppServices.Current.PrivilegeSuffix;

    private void OnNotificationRequested(object? sender, NotificationEventArgs e)
    {
        ShowNotification(e);
    }

    private void ShowNotification(NotificationEventArgs e)
    {
        Notification.Title = e.Title;
        Notification.Message = e.Message;
        Notification.Severity = e.Severity;
        Notification.IsOpen = true;
    }

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        Type page = args.IsSettingsSelected
            ? typeof(SettingsPage)
            : (args.SelectedItem as NavigationViewItem)?.Tag switch
            {
                "channels" => typeof(ChannelsPage),
                "keymap" => typeof(KeymapPage),
                _ => typeof(PlayerPage),
            };

        if (ContentFrame.CurrentSourcePageType != page)
        {
            ContentFrame.Navigate(page, null, new EntranceNavigationTransitionInfo());
        }
    }

    /// <summary>Current window size converted from physical pixels back to DIPs.</summary>
    internal AppConfigSize GetCurrentSizeDip()
    {
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        double scale = 1.0;
        uint dpi = GetDpiForWindow(hwnd);
        if (dpi > 0)
        {
            scale = dpi / 96.0;
        }

        return new AppConfigSize(
            (int)(AppWindow.Size.Width / scale),
            (int)(AppWindow.Size.Height / scale));
    }
}
