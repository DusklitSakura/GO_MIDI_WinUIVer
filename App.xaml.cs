using GoMidi.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GoMidi;

/// <summary>Carries a transient message the shell should surface to the user.</summary>
public sealed class NotificationEventArgs : EventArgs
{
    public required string Title { get; init; }

    public required string Message { get; init; }

    public InfoBarSeverity Severity { get; init; } = InfoBarSeverity.Informational;
}

/// <summary>
/// Application entry point. Also exposes the handful of process-wide values
/// (window handle, XAML root, notification channel) that pages and view models
/// need but cannot reach through their own objects.
/// </summary>
public partial class App : Application
{
    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            Log.Error("未处理的异常", e.Exception);
        };
    }

    /// <summary>The main application window.</summary>
    public static Window Window { get; private set; } = null!;

    /// <summary>The UI thread dispatcher.</summary>
    public static Microsoft.UI.Dispatching.DispatcherQueue DispatcherQueue { get; private set; } = null!;

    /// <summary>XAML root of the active window; required to parent a <see cref="ContentDialog"/>.</summary>
    public static XamlRoot? CurrentXamlRoot => (Window?.Content as FrameworkElement)?.XamlRoot;

    /// <summary>Raised when a page wants a <see cref="InfoBar"/> shown in the shell.</summary>
    public static event EventHandler<NotificationEventArgs>? NotificationRequested;

    /// <summary>
    /// Holds a message raised before the shell started listening. A page created
    /// during startup (for example the player loading the previous session's
    /// song) would otherwise have its warning silently discarded.
    /// </summary>
    private static NotificationEventArgs? _pendingNotification;

    /// <summary>Queues a non-blocking message for the shell to display.</summary>
    public static void ShowInfoBar(string title, string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        var args = new NotificationEventArgs
        {
            Title = title,
            Message = message,
            Severity = severity,
        };

        if (NotificationRequested is { } handler)
        {
            handler(null, args);
        }
        else
        {
            _pendingNotification = args;
        }
    }

    /// <summary>Takes the message that was raised before the shell was listening, if any.</summary>
    internal static NotificationEventArgs? ConsumePendingNotification()
    {
        NotificationEventArgs? pending = _pendingNotification;
        _pendingNotification = null;
        return pending;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Window = new MainWindow();
        DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        Window.Activate();
    }
}
