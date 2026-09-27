using System.IO;

namespace GoMidi.Core;

/// <summary>A single synthesized key transition, mirroring the original <c>KeyInputEvent</c>.</summary>
public readonly record struct KeyInputEvent(bool IsNoteOn, int VkCode, int Modifier, nint Hwnd);

/// <summary>An enumerable top-level window that notes can be routed to.</summary>
public sealed record WindowInfo(nint Hwnd, string Title, string ProcessName, uint ProcessId)
{
    public string Display =>
        string.IsNullOrEmpty(ProcessName) ? Title : $"{Title}  ·  {ProcessName}";
}

/// <summary>
/// Replays note events as real keystrokes. Two delivery paths, exactly as the
/// original: a global <c>SendInput</c> batch when no target window is set, and
/// per-key <c>PostMessage</c> when a specific window is targeted.
/// </summary>
public sealed class KeyboardSimulator
{
    // Modifier bit layout — kept identical to Util::KeyManager in the C++ build.
    public const int ModNone = 0;
    public const int ModShift = 1;
    public const int ModCtrl = 2;
    public const int ModAlt = 4;
    public const int ModMouseLeft = 8;
    public const int ModMouseMiddle = 16;
    public const int ModMouseRight = 32;

    /// <summary>Physical modifier state per target window, so we never leave a modifier latched.</summary>
    private readonly Dictionary<nint, int> _modState = new();

    /// <summary>
    /// Scratch buffer for coalescing a batch into one SendInput call. An instance
    /// field rather than a static one: a static would be a cross-thread buffer
    /// shared with anything else that drives the simulator.
    /// </summary>
    private readonly NativeMethods.INPUT[] _inputBuffer = new NativeMethods.INPUT[512];

    /// <summary>
    /// Sends a batch of transitions. Windowless events collapse into a single
    /// <c>SendInput</c> call; window-targeted events keep per-key
    /// <c>PostMessage</c> semantics. Modifiers are wrapped instantaneously
    /// around each note so they are never held across notes.
    /// </summary>
    public void SendKeyEvents(IReadOnlyList<KeyInputEvent> events)
    {
        if (events.Count == 0)
        {
            return;
        }

        // ---- Path A: global SendInput, coalesced -------------------------
        int used = 0;
        foreach (KeyInputEvent e in events)
        {
            if (e.Hwnd != nint.Zero)
            {
                continue;
            }

            AppendKeyboardModifiers(e.Modifier, down: true, _inputBuffer, ref used);
            AppendKey(e.VkCode, down: e.IsNoteOn, _inputBuffer, ref used);
            AppendMouseModifiers(e.Modifier, down: true, _inputBuffer, ref used);
            AppendKeyboardModifiers(e.Modifier, down: false, _inputBuffer, ref used);
            AppendMouseModifiers(e.Modifier, down: false, _inputBuffer, ref used);

            if (used >= _inputBuffer.Length - 16)
            {
                Flush(used);
                used = 0;
            }
        }

        Flush(used);

        // ---- Path B: targeted PostMessage, per key ------------------------
        foreach (KeyInputEvent e in events)
        {
            if (e.Hwnd == nint.Zero)
            {
                continue;
            }

            PostModifiers(e.Modifier, down: true, e.Hwnd);
            PostKey(e.VkCode, down: e.IsNoteOn, e.Hwnd);
            PostMouseModifiers(e.Modifier, down: true, e.Hwnd);
            PostModifiers(e.Modifier, down: false, e.Hwnd);
            PostMouseModifiers(e.Modifier, down: false, e.Hwnd);
        }
    }

    /// <summary>Release keys grouped by target window — cheaper than one call per key.</summary>
    public void ReleaseKeys(IReadOnlyList<(int VkCode, nint Hwnd)> keys)
    {
        if (keys.Count == 0)
        {
            return;
        }

        var global = new List<KeyInputEvent>(keys.Count);
        foreach ((int vk, nint hwnd) in keys)
        {
            global.Add(new KeyInputEvent(false, vk, ModNone, hwnd));
        }

        SendKeyEvents(global);
    }

    /// <summary>Safety net for stop / seek / pause: physically lift every held modifier.</summary>
    public void ReleaseAllModifiers()
    {
        int used = 0;
        foreach (int bit in new[] { ModShift, ModCtrl, ModAlt })
        {
            AppendKeyboardModifiers(bit, down: false, _inputBuffer, ref used);
        }

        AppendMouseModifiers(ModMouseLeft | ModMouseMiddle | ModMouseRight, down: false, _inputBuffer, ref used);
        Flush(used);
        _modState.Clear();
    }

    private void Flush(int count)
    {
        if (count <= 0)
        {
            return;
        }

        NativeMethods.SendInput((uint)count, _inputBuffer, System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.INPUT>());
    }

    private static void AppendKey(int vk, bool down, NativeMethods.INPUT[] buffer, ref int used)
    {
        if (vk <= 0)
        {
            return;
        }

        uint scan = NativeMethods.MapVirtualKey((uint)vk, NativeMethods.MAPVK_VK_TO_VSC);
        uint flags = NativeMethods.KEYEVENTF_SCANCODE;
        if (!down)
        {
            flags |= NativeMethods.KEYEVENTF_KEYUP;
        }

        if (NativeMethods.IsExtendedKey(vk))
        {
            flags |= NativeMethods.KEYEVENTF_EXTENDEDKEY;
        }

        buffer[used++] = new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_KEYBOARD,
            u = new NativeMethods.InputUnion
            {
                ki = new NativeMethods.KEYBDINPUT
                {
                    wVk = 0,
                    wScan = (ushort)scan,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = nint.Zero,
                },
            },
        };
    }

    private static void AppendKeyboardModifiers(int modifier, bool down, NativeMethods.INPUT[] buffer, ref int used)
    {
        if ((modifier & ModShift) != 0)
        {
            AppendKey(0x10, down, buffer, ref used); // VK_SHIFT
        }

        if ((modifier & ModCtrl) != 0)
        {
            AppendKey(0x11, down, buffer, ref used); // VK_CONTROL
        }

        if ((modifier & ModAlt) != 0)
        {
            AppendKey(0x12, down, buffer, ref used); // VK_MENU
        }
    }

    private static void AppendMouseModifiers(int modifier, bool down, NativeMethods.INPUT[] buffer, ref int used)
    {
        if ((modifier & ModMouseLeft) != 0)
        {
            AppendMouse(down ? NativeMethods.MOUSEEVENTF_LEFTDOWN : NativeMethods.MOUSEEVENTF_LEFTUP, buffer, ref used);
        }

        if ((modifier & ModMouseMiddle) != 0)
        {
            AppendMouse(down ? NativeMethods.MOUSEEVENTF_MIDDLEDOWN : NativeMethods.MOUSEEVENTF_MIDDLEUP, buffer, ref used);
        }

        if ((modifier & ModMouseRight) != 0)
        {
            AppendMouse(down ? NativeMethods.MOUSEEVENTF_RIGHTDOWN : NativeMethods.MOUSEEVENTF_RIGHTUP, buffer, ref used);
        }
    }

    private static void AppendMouse(uint flag, NativeMethods.INPUT[] buffer, ref int used)
    {
        buffer[used++] = new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_MOUSE,
            u = new NativeMethods.InputUnion
            {
                mi = new NativeMethods.MOUSEINPUT { dwFlags = flag },
            },
        };
    }

    private static void PostKey(int vk, bool down, nint hwnd)
    {
        if (vk <= 0)
        {
            return;
        }

        uint scan = NativeMethods.MapVirtualKey((uint)vk, NativeMethods.MAPVK_VK_TO_VSC);
        nint lParam = (nint)(1 | (scan << 16));
        if (NativeMethods.IsExtendedKey(vk))
        {
            lParam |= 1 << 24;
        }

        if (!down)
        {
            lParam |= 1 << 30; // previous key state
            lParam |= unchecked((nint)1L << 31); // transition state
        }

        uint msg = down ? NativeMethods.WM_KEYDOWN : NativeMethods.WM_KEYUP;
        NativeMethods.PostMessage(hwnd, msg, vk, lParam);
    }

    private static void PostModifiers(int modifier, bool down, nint hwnd)
    {
        if ((modifier & ModShift) != 0)
        {
            PostKey(0x10, down, hwnd);
        }

        if ((modifier & ModCtrl) != 0)
        {
            PostKey(0x11, down, hwnd);
        }

        if ((modifier & ModAlt) != 0)
        {
            PostKey(0x12, down, hwnd);
        }
    }

    private static void PostMouseModifiers(int modifier, bool down, nint hwnd)
    {
        if ((modifier & ModMouseLeft) != 0)
        {
            NativeMethods.PostMessage(hwnd, down ? NativeMethods.WM_LBUTTONDOWN : NativeMethods.WM_LBUTTONUP, 1, 0);
        }

        if ((modifier & ModMouseMiddle) != 0)
        {
            NativeMethods.PostMessage(hwnd, down ? NativeMethods.WM_MBUTTONDOWN : NativeMethods.WM_MBUTTONUP, 1, 0);
        }

        if ((modifier & ModMouseRight) != 0)
        {
            NativeMethods.PostMessage(hwnd, down ? NativeMethods.WM_RBUTTONDOWN : NativeMethods.WM_RBUTTONUP, 1, 0);
        }
    }

    /// <summary>
    /// Top-level, visible, non-tool, non-cloaked windows that own a title —
    /// the set offered by the per-channel window picker.
    /// </summary>
    public static List<WindowInfo> GetWindowList()
    {
        var result = new List<WindowInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd))
            {
                return true;
            }

            int len = NativeMethods.GetWindowTextLength(hwnd);
            if (len <= 0)
            {
                return true;
            }

            // Skip tool windows and cloaked (virtual-desktop-hidden) windows.
            int exStyle = NativeMethods.GetWindowExStyle(hwnd);
            if ((exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0)
            {
                return true;
            }

            if (NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
            {
                return true;
            }

            var buffer = new char[len + 2];
            int copied = NativeMethods.GetWindowText(hwnd, buffer, buffer.Length);
            if (copied <= 0)
            {
                return true;
            }

            string title = new string(buffer, 0, copied).Trim();
            if (title.Length == 0)
            {
                return true;
            }

            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            string process = GetProcessName(pid);

            // Two windows with an identical title+process are indistinguishable in the picker.
            if (!seen.Add($"{title}\u0000{process}"))
            {
                return true;
            }

            result.Add(new WindowInfo(hwnd, title, process, pid));
            return true;
        }, nint.Zero);

        return result;
    }

    private static string GetProcessName(uint pid)
    {
        nint handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == nint.Zero)
        {
            return string.Empty;
        }

        try
        {
            const int charCapacity = 1024;
            nint buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(charCapacity * sizeof(char));
            try
            {
                uint size = charCapacity;
                if (!NativeMethods.QueryFullProcessImageName(handle, 0, buffer, ref size))
                {
                    return string.Empty;
                }

                return Path.GetFileNameWithoutExtension(
                    System.Runtime.InteropServices.Marshal.PtrToStringUni(buffer, (int)size));
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }
}
