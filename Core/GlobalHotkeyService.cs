using System.Runtime.InteropServices;

namespace GoMidi.Core;

/// <summary>
/// Registers system-wide hotkeys through a message-only window, so the app can
/// toggle playback while another application (the game) holds focus. The
/// original used a low-level keyboard hook; <c>RegisterHotKey</c> is the
/// supported equivalent and does not require a hook procedure in the target
/// process.
/// </summary>
public sealed class GlobalHotkeyService : IDisposable
{
    private const int HotkeyId = 0xA17;

    private readonly NativeMethods.WndProc _wndProcDelegate;
    private readonly nint _hwnd;
    private readonly nint _classNamePtr;
    private readonly nint _windowNamePtr;
    private uint _registeredVk;

    private bool _disposed;

    /// <summary>Raised on the thread that pumps the message-only window.</summary>
    public event EventHandler? Pressed;

    public GlobalHotkeyService()
    {
        _wndProcDelegate = WndProcCallback;

        string className = $"GoMidiHotkey_{Guid.NewGuid():N}";
        _classNamePtr = Marshal.StringToHGlobalUni(className);
        _windowNamePtr = Marshal.StringToHGlobalUni("GoMidiHotkey");

        nint hInstance = NativeMethods.GetModuleHandle(null);

        var wndClass = new NativeMethods.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
            hInstance = hInstance,
            lpszClassName = _classNamePtr,
        };

        ushort atom = NativeMethods.RegisterClassEx(ref wndClass);
        if (atom == 0)
        {
            throw new InvalidOperationException("无法注册热键消息窗口类", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
        }

        _hwnd = NativeMethods.CreateWindowEx(
            0, _classNamePtr, _windowNamePtr, 0,
            0, 0, 0, 0,
            NativeMethods.HWND_MESSAGE, nint.Zero, hInstance, nint.Zero);

        if (_hwnd == nint.Zero)
        {
            throw new InvalidOperationException("无法创建热键消息窗口", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
        }
    }

    /// <summary>
    /// Binds a virtual key as a system-wide hotkey.
    /// </summary>
    /// <returns><c>true</c> when the key was claimed; <c>false</c> when another process owns it.</returns>
    public bool Register(uint virtualKey, bool noRepeat = true)
    {
        Unregister();

        uint modifiers = noRepeat ? NativeMethods.MOD_NOREPEAT : 0;
        if (!NativeMethods.RegisterHotKey(_hwnd, HotkeyId, modifiers, virtualKey))
        {
            return false;
        }

        _registeredVk = virtualKey;
        return true;
    }

    public void Unregister()
    {
        if (_registeredVk == 0)
        {
            return;
        }

        NativeMethods.UnregisterHotKey(_hwnd, HotkeyId);
        _registeredVk = 0;
    }

    private nint WndProcCallback(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam == HotkeyId)
        {
            Pressed?.Invoke(this, EventArgs.Empty);
            return nint.Zero;
        }

        return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Unregister();

        if (_hwnd != nint.Zero)
        {
            NativeMethods.DestroyWindow(_hwnd);
        }

        Marshal.FreeHGlobal(_classNamePtr);
        Marshal.FreeHGlobal(_windowNamePtr);
        GC.SuppressFinalize(this);
    }
}
