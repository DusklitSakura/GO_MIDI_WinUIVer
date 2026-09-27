using System.Runtime.InteropServices;
using Windows.Media;

namespace GoMidi.Core;

/// <summary>
/// Bridges the app to the Windows media session, so the hardware media keys and
/// the system media popup can drive playback.
///
/// <para>
/// A desktop app cannot use <c>SystemMediaTransportControls.GetForCurrentView</c>
/// — that needs a CoreWindow. The supported route is the
/// <c>ISystemMediaTransportControlsInterop</c> activation-factory interface,
/// obtained through <c>RoGetActivationFactory</c> and called with our HWND.
/// </para>
/// </summary>
public sealed class MediaSession : IDisposable
{
    // From the Windows SDK: SystemMediaTransportControlsInterop.idl and the
    // ISystemMediaTransportControls IID in the Windows.Media projection.
    private static readonly Guid InteropIid = new("ddb0472d-c911-4a1f-86d9-dc3d71a95f5a");
    private static readonly Guid SmtcIid = new("99FA3FF4-1742-42A6-902E-087D41F965EC");

    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher;
    private SystemMediaTransportControls? _controls;
    private bool _disposed;

    public MediaSession(Microsoft.UI.Dispatching.DispatcherQueue dispatcher) => _dispatcher = dispatcher;

    public event EventHandler? PreviousRequested;

    public event EventHandler? NextRequested;

    public event EventHandler? PlayPauseRequested;

    public event EventHandler? StopRequested;

    /// <summary>True when the media session was created and is listening for buttons.</summary>
    public bool IsAvailable => _controls is not null;

    public string LastError { get; private set; } = string.Empty;

    /// <summary>Binds the session to the app window. Safe to call more than once.</summary>
    public bool TryInitialize(nint hwnd)
    {
        if (_controls is not null || hwnd == nint.Zero)
        {
            return _controls is not null;
        }

        try
        {
            _controls = CreateForWindow(hwnd);

            // IsEnabled must be set before the individual buttons: flipping it can
            // reset the per-button flags, which would silently drop every key
            // except the ones that happen to default to enabled.
            _controls.IsEnabled = true;
            _controls.IsPlayEnabled = true;
            _controls.IsPauseEnabled = true;
            _controls.IsStopEnabled = true;
            _controls.IsNextEnabled = true;
            _controls.IsPreviousEnabled = true;

            // Closed keeps the session dormant until a song is actually loaded,
            // so we do not hijack the media keys from other players at startup.
            _controls.PlaybackStatus = MediaPlaybackStatus.Closed;
            _controls.DisplayUpdater.Type = MediaPlaybackType.Music;

            _controls.ButtonPressed += OnButtonPressed;

            Log.Info(
                $"媒体会话已建立（enabled={_controls.IsEnabled} play={_controls.IsPlayEnabled} " +
                $"pause={_controls.IsPauseEnabled} stop={_controls.IsStopEnabled} " +
                $"next={_controls.IsNextEnabled} prev={_controls.IsPreviousEnabled}）");
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _controls = null;
            Log.Warn($"媒体会话不可用：{ex.Message}");
            return false;
        }
    }

    /// <summary>Updates the title shown in the system media popup.</summary>
    public void UpdateNowPlaying(string title, string subtitle)
    {
        if (_controls is null)
        {
            return;
        }

        try
        {
            _controls.DisplayUpdater.MusicProperties.Title = title;
            _controls.DisplayUpdater.MusicProperties.Artist = subtitle;
            _controls.DisplayUpdater.Update();
        }
        catch (Exception ex)
        {
            Log.Warn($"更新媒体信息失败：{ex.Message}");
        }
    }

    /// <summary>
    /// Mirrors the transport state. A song being loaded moves the session out of
    /// <see cref="MediaPlaybackStatus.Closed"/>, which is what makes it the
    /// active target for the media keys.
    /// </summary>
    public void UpdatePlaybackStatus(bool hasSong, bool playing, bool paused)
    {
        if (_controls is null)
        {
            return;
        }

        try
        {
            _controls.PlaybackStatus = !hasSong
                ? MediaPlaybackStatus.Closed
                : playing
                    ? MediaPlaybackStatus.Playing
                    : paused
                        ? MediaPlaybackStatus.Paused
                        : MediaPlaybackStatus.Stopped;
        }
        catch (Exception ex)
        {
            Log.Warn($"更新播放状态失败：{ex.Message}");
        }
    }

    private void OnButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        Log.Info($"收到媒体键：{args.Button}");

        // The event arrives on a system thread; every handler touches UI state.
        _dispatcher.TryEnqueue(() =>
        {
            switch (args.Button)
            {
                case SystemMediaTransportControlsButton.Play:
                case SystemMediaTransportControlsButton.Pause:
                    PlayPauseRequested?.Invoke(this, EventArgs.Empty);
                    break;

                case SystemMediaTransportControlsButton.Previous:
                    PreviousRequested?.Invoke(this, EventArgs.Empty);
                    break;

                case SystemMediaTransportControlsButton.Next:
                    NextRequested?.Invoke(this, EventArgs.Empty);
                    break;

                case SystemMediaTransportControlsButton.Stop:
                    StopRequested?.Invoke(this, EventArgs.Empty);
                    break;
            }
        });
    }

    // -- interop -------------------------------------------------------------

    private static SystemMediaTransportControls CreateForWindow(nint hwnd)
    {
        const string typeName = "Windows.Media.SystemMediaTransportControls";

        // The runtime-class id has to be an HSTRING. [MarshalAs(UnmanagedType.HString)]
        // is not supported on a classic DllImport, so the string is created with
        // the Windows string API and passed as a raw handle.
        int hr = WindowsCreateString(typeName, (uint)typeName.Length, out nint classId);
        if (hr < 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }

        nint factory = nint.Zero;
        try
        {
            Guid interopIid = InteropIid;
            hr = RoGetActivationFactory(classId, ref interopIid, out factory);
        }
        finally
        {
            WindowsDeleteString(classId);
        }

        if (hr < 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }

        try
        {
            var interop = (ISystemMediaTransportControlsInterop)Marshal.GetObjectForIUnknown(factory);

            Guid smtcIid = SmtcIid;
            hr = interop.GetForWindow(hwnd, ref smtcIid, out nint controls);
            if (hr < 0)
            {
                Marshal.ThrowExceptionForHR(hr);
            }

            // FromAbi takes its own reference, and the object lives for the whole
            // process, so the original pointer is deliberately not released here.
            return WinRT.MarshalInspectable<SystemMediaTransportControls>.FromAbi(controls);
        }
        finally
        {
            Marshal.Release(factory);
        }
    }

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(nint activatableClassId, ref Guid iid, out nint factory);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string sourceString,
        uint length,
        out nint hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(nint hstring);

    /// <summary>
    /// The IInspectable slots are spelled out rather than inherited: the native
    /// interface derives from IInspectable, so GetForWindow is the seventh entry
    /// in the vtable. Declaring them inline makes the layout explicit instead of
    /// depending on how the CLR chains a [ComImport] base interface.
    /// </summary>
    [ComImport]
    [Guid("ddb0472d-c911-4a1f-86d9-dc3d71a95f5a")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISystemMediaTransportControlsInterop
    {
        [PreserveSig]
        int GetIids(out uint iidCount, out nint iids);

        [PreserveSig]
        int GetRuntimeClassName(out nint className);

        [PreserveSig]
        int GetTrustLevel(out int trustLevel);

        [PreserveSig]
        int GetForWindow(nint appWindow, ref Guid riid, out nint mediaTransportControl);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_controls is not null)
        {
            try
            {
                _controls.ButtonPressed -= OnButtonPressed;
                _controls.IsEnabled = false;
                _controls.PlaybackStatus = MediaPlaybackStatus.Closed;
            }
            catch
            {
                // Shutting down anyway.
            }

            _controls = null;
        }
    }
}
