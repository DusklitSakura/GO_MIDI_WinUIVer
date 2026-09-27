using GoMidi.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using System.Runtime.InteropServices;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;

namespace GoMidi.Controls;

/// <summary>
/// On-screen piano keyboard used by the keymap editor. Each key shows the
/// computer key currently bound to it; clicking a key then pressing any key
/// rebinds it, which is how the original's editor worked.
/// </summary>
public sealed class KeymapKeyboard : UserControl
{
    private const double BlackKeyWidthRatio = 0.62;
    private const double BlackKeyHeightRatio = 0.62;

    private static readonly bool[] IsBlackKey =
    [
        false, true, false, true, false, false, true, false, true, false, true, false,
    ];

    private readonly Canvas _canvas = new();
    private readonly List<(Rectangle Rect, int Pitch, bool IsBlack)> _hitAreas = new();

    private KeyManager? _keys;
    private int _lowPitch = 48;
    private int _highPitch = 84;
    private int _selectedPitch = -1;

    public KeymapKeyboard()
    {
        Content = _canvas;
        IsTabStop = true;
        Height = 190;
        Background = new SolidColorBrush(Colors.Transparent);

        SizeChanged += (_, _) => Redraw();
        ActualThemeChanged += (_, _) => Redraw();
        PointerPressed += OnPointerPressed;
        KeyDown += OnKeyDown;
        LostFocus += (_, _) => Redraw();
    }

    /// <summary>Raised when a key is clicked, with its MIDI pitch.</summary>
    public event EventHandler<int>? PitchSelected;

    /// <summary>Raised when the user presses a key while a note is selected.</summary>
    public event EventHandler<KeyBindingCapturedEventArgs>? BindingCaptured;

    /// <summary>Raised when the user clears a binding (right-click or Delete).</summary>
    public event EventHandler<int>? BindingCleared;

    public int SelectedPitch
    {
        get => _selectedPitch;
        set
        {
            _selectedPitch = value;
            Redraw();
        }
    }

    public void SetKeyManager(KeyManager keys)
    {
        _keys = keys;
        Redraw();
    }

    public void SetRange(int lowPitch, int highPitch)
    {
        _lowPitch = Math.Clamp(lowPitch, 0, 127);
        _highPitch = Math.Clamp(highPitch, 0, 127);
        if (_lowPitch >= _highPitch)
        {
            _highPitch = Math.Min(127, _lowPitch + 1);
        }

        Redraw();
    }

    public void Refresh() => Redraw();

    private void Redraw()
    {
        _canvas.Children.Clear();
        _hitAreas.Clear();

        double width = ActualWidth;
        double height = ActualHeight;
        if (width <= 1 || height <= 1 || _keys is null)
        {
            return;
        }

        int whiteCount = 0;
        for (int pitch = _lowPitch; pitch <= _highPitch; pitch++)
        {
            if (!IsBlackKey[pitch % 12])
            {
                whiteCount++;
            }
        }

        if (whiteCount == 0)
        {
            return;
        }

        bool dark = ActualTheme == ElementTheme.Dark;
        double whiteWidth = width / whiteCount;
        double blackWidth = whiteWidth * BlackKeyWidthRatio;

        var whiteFill = new SolidColorBrush(dark ? Color.FromArgb(255, 0xF2, 0xF2, 0xF2) : Colors.White);
        var blackFill = new SolidColorBrush(dark ? Color.FromArgb(255, 0x1C, 0x1C, 0x1C) : Color.FromArgb(255, 0x2B, 0x2B, 0x2B));
        var stroke = new SolidColorBrush(dark ? Color.FromArgb(255, 0x44, 0x44, 0x44) : Color.FromArgb(255, 0xC8, 0xC8, 0xC8));
        var accent = new SolidColorBrush(Color.FromArgb(255, 0x0F, 0x6C, 0xBD));
        var textDark = new SolidColorBrush(Color.FromArgb(255, 0x1A, 0x1A, 0x1A));
        var textLight = new SolidColorBrush(Color.FromArgb(255, 0xEC, 0xEC, 0xEC));
        var mutedLight = new SolidColorBrush(Color.FromArgb(255, 0x8A, 0x8A, 0x8A));

        // White keys first so black keys paint over them.
        int whiteIndex = 0;
        var whitePositions = new Dictionary<int, double>();
        for (int pitch = _lowPitch; pitch <= _highPitch; pitch++)
        {
            if (IsBlackKey[pitch % 12])
            {
                continue;
            }

            double x = whiteIndex * whiteWidth;
            whitePositions[pitch] = x;
            whiteIndex++;

            bool selected = pitch == _selectedPitch;
            var rect = new Rectangle
            {
                Width = Math.Max(1, whiteWidth - 1),
                Height = height,
                Fill = selected ? accent : whiteFill,
                Stroke = stroke,
                StrokeThickness = 1,
                RadiusX = 3,
                RadiusY = 3,
            };

            Canvas.SetLeft(rect, x);
            Canvas.SetTop(rect, 0);
            _canvas.Children.Add(rect);
            _hitAreas.Add((rect, pitch, false));

            KeyMapping mapping = _keys.GetMapping(pitch);
            bool bound = mapping.IsValid;

            var nameLabel = new TextBlock
            {
                Text = NoteNames.FromPitch(pitch),
                FontSize = 10,
                Foreground = selected ? textLight : mutedLight,
                IsHitTestVisible = false,
            };
            nameLabel.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(nameLabel, x + (whiteWidth - nameLabel.DesiredSize.Width) / 2);
            Canvas.SetTop(nameLabel, height - 18);
            _canvas.Children.Add(nameLabel);

            if (bound)
            {
                var bindingLabel = new TextBlock
                {
                    Text = FormatBinding(mapping),
                    FontSize = 13,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = selected ? textLight : textDark,
                    IsHitTestVisible = false,
                };
                bindingLabel.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(bindingLabel, x + (whiteWidth - bindingLabel.DesiredSize.Width) / 2);
                Canvas.SetTop(bindingLabel, height - 44);
                _canvas.Children.Add(bindingLabel);
            }
        }

        for (int pitch = _lowPitch; pitch <= _highPitch; pitch++)
        {
            if (!IsBlackKey[pitch % 12])
            {
                continue;
            }

            // A black key sits between the two white keys below it.
            int lowerWhite = pitch - 1;
            while (lowerWhite >= _lowPitch && IsBlackKey[lowerWhite % 12])
            {
                lowerWhite--;
            }

            if (!whitePositions.TryGetValue(lowerWhite, out double leftX))
            {
                continue;
            }

            double x = leftX + whiteWidth - blackWidth / 2;
            bool selected = pitch == _selectedPitch;

            var rect = new Rectangle
            {
                Width = Math.Max(1, blackWidth),
                Height = height * BlackKeyHeightRatio,
                Fill = selected ? accent : blackFill,
                RadiusX = 2,
                RadiusY = 2,
            };

            Canvas.SetLeft(rect, x);
            Canvas.SetTop(rect, 0);
            _canvas.Children.Add(rect);
            _hitAreas.Add((rect, pitch, true));

            KeyMapping mapping = _keys.GetMapping(pitch);
            if (mapping.IsValid)
            {
                var bindingLabel = new TextBlock
                {
                    Text = FormatBinding(mapping),
                    FontSize = 11,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = textLight,
                    IsHitTestVisible = false,
                };
                bindingLabel.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(bindingLabel, x + (blackWidth - bindingLabel.DesiredSize.Width) / 2);

                // Centre inside the black key so the label cannot be mistaken for
                // one belonging to the white key underneath.
                Canvas.SetTop(bindingLabel, (height * BlackKeyHeightRatio - bindingLabel.DesiredSize.Height) / 2);
                _canvas.Children.Add(bindingLabel);
            }
        }
    }

    private static string FormatBinding(KeyMapping mapping)
    {
        string modifier = KeyManager.DescribeModifier(mapping.Modifier);
        string key = KeyManager.DescribeKey(mapping.VkCode);
        return modifier.Length == 0 ? key : $"{modifier}+{key}";
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(_canvas);
        double x = point.Position.X;
        double y = point.Position.Y;

        // Black keys are drawn last, so search them first.
        foreach ((Rectangle rect, int pitch, bool isBlack) in _hitAreas.Where(a => a.IsBlack))
        {
            if (Hit(rect, x, y))
            {
                HandleSelection(pitch, point.Properties.IsRightButtonPressed);
                e.Handled = true;
                return;
            }
        }

        foreach ((Rectangle rect, int pitch, bool isBlack) in _hitAreas.Where(a => !a.IsBlack))
        {
            if (!isBlack && Hit(rect, x, y))
            {
                HandleSelection(pitch, point.Properties.IsRightButtonPressed);
                e.Handled = true;
                return;
            }
        }
    }

    private static bool Hit(Rectangle rect, double x, double y)
    {
        double left = Canvas.GetLeft(rect);
        double top = Canvas.GetTop(rect);
        return x >= left && x <= left + rect.Width && y >= top && y <= top + rect.Height;
    }

    private void HandleSelection(int pitch, bool clear)
    {
        if (clear)
        {
            BindingCleared?.Invoke(this, pitch);
            return;
        }

        SelectedPitch = pitch;
        Focus(FocusState.Programmatic);
        PitchSelected?.Invoke(this, pitch);
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_selectedPitch < 0)
        {
            return;
        }

        e.Handled = true;

        if (e.Key == VirtualKey.Escape)
        {
            SelectedPitch = -1;
            return;
        }

        if (e.Key is VirtualKey.Delete or VirtualKey.Back)
        {
            BindingCleared?.Invoke(this, _selectedPitch);
            return;
        }

        // Modifiers on their own are not a binding; they become part of one.
        if (e.Key is VirtualKey.Shift or VirtualKey.Control or VirtualKey.Menu or
            VirtualKey.LeftShift or VirtualKey.RightShift or
            VirtualKey.LeftControl or VirtualKey.RightControl or
            VirtualKey.LeftMenu or VirtualKey.RightMenu or
            VirtualKey.LeftWindows or VirtualKey.RightWindows)
        {
            return;
        }

        int modifier = 0;
        if (IsDown(VirtualKey.Shift))
        {
            modifier |= KeyManager.ModShift;
        }

        if (IsDown(VirtualKey.Control))
        {
            modifier |= KeyManager.ModCtrl;
        }

        if (IsDown(VirtualKey.Menu))
        {
            modifier |= KeyManager.ModAlt;
        }

        // Left/right mouse buttons held while pressing a key become mouse modifiers.
        if (IsDown(VirtualKey.LeftButton))
        {
            modifier |= KeyManager.ModMouseLeft;
        }

        if (IsDown(VirtualKey.MiddleButton))
        {
            modifier |= KeyManager.ModMouseMiddle;
        }

        if (IsDown(VirtualKey.RightButton))
        {
            modifier |= KeyManager.ModMouseRight;
        }

        BindingCaptured?.Invoke(this, new KeyBindingCapturedEventArgs(_selectedPitch, (int)e.Key, modifier));
    }

    private static bool IsDown(VirtualKey key)
    {
        CoreVirtualKeyStates state = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key);
        return (state & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
    }
}

/// <summary>Payload for a captured key press in the keymap editor.</summary>
public sealed class KeyBindingCapturedEventArgs : EventArgs
{
    public KeyBindingCapturedEventArgs(int pitch, int vkCode, int modifier)
    {
        Pitch = pitch;
        VkCode = vkCode;
        Modifier = modifier;
    }

    public int Pitch { get; }

    public int VkCode { get; }

    public int Modifier { get; }
}
