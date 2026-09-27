using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace GoMidi.Controls;

/// <summary>
/// Seek bar with draggable A/B loop markers, reproducing the original's
/// right-click gesture: the first right click drops A, the second drops B and
/// starts looping, the third clears both. Right-dragging on a marker moves it.
/// </summary>
public sealed class AbLoopBar : UserControl
{
    /// <summary>How close, in pixels, a right click must be to grab a marker.</summary>
    private const double MarkerGrabDistance = 10.0;

    private const double TrackHeight = 6.0;
    private const double MarkerWidth = 3.0;
    private const double ThumbSize = 14.0;

    private readonly Canvas _canvas = new();
    private readonly Rectangle _track = new() { RadiusX = 3, RadiusY = 3 };
    private readonly Rectangle _fill = new() { RadiusX = 3, RadiusY = 3 };
    private readonly Rectangle _loopRegion = new();
    private readonly Rectangle _markerA = new() { Width = MarkerWidth };
    private readonly Rectangle _markerB = new() { Width = MarkerWidth };
    private readonly Ellipse _thumb = new();

    private double _duration;
    private double _position;
    private double _loopA = -1;
    private double _loopB = -1;

    private bool _draggingThumb;
    private bool _draggingMarkerA;
    private bool _draggingMarkerB;
    private bool _hover;

    public AbLoopBar()
    {
        // Every shape inside is IsHitTestVisible=false, and a Canvas with a null
        // Background does not hit-test either — without this the bar would never
        // receive a pointer event at all.
        _canvas.Background = new SolidColorBrush(Colors.Transparent);

        Content = _canvas;
        Height = 30;
        Background = new SolidColorBrush(Colors.Transparent);

        foreach (UIElement element in new UIElement[] { _track, _loopRegion, _fill, _markerA, _markerB, _thumb })
        {
            element.IsHitTestVisible = false;
            _canvas.Children.Add(element);
        }

        ApplyBrushes();

        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCaptureLost += (_, _) => EndDrag();
        PointerEntered += (_, _) => { _hover = true; Layout(); };
        PointerExited += (_, _) => { _hover = false; Layout(); };
        SizeChanged += (_, _) => Layout();
        ActualThemeChanged += (_, _) => ApplyBrushes();
    }

    /// <summary>Raised when the user finishes scrubbing, with the requested position in seconds.</summary>
    public event EventHandler<double>? SeekRequested;

    /// <summary>Raised when the A/B points change. Both are -1 when cleared.</summary>
    public event EventHandler? LoopPointsChanged;

    public double Duration
    {
        get => _duration;
        set
        {
            _duration = Math.Max(0, value);
            Layout();
        }
    }

    public double Position
    {
        get => _position;
        set
        {
            if (_draggingThumb || _draggingMarkerA || _draggingMarkerB)
            {
                return; // user input wins while a gesture is in flight
            }

            _position = value;
            Layout();
        }
    }

    public double LoopA => _loopA;

    public double LoopB => _loopB;

    public bool HasLoop => _loopA >= 0 && _loopB > _loopA;

    /// <summary>Clears both loop points without raising a request.</summary>
    public void ClearLoop()
    {
        _loopA = _loopB = -1;
        Layout();
    }

    private void ApplyBrushes()
    {
        bool dark = ActualTheme == ElementTheme.Dark;

        _track.Fill = new SolidColorBrush(dark ? Color.FromArgb(255, 0x3A, 0x3A, 0x3A) : Color.FromArgb(255, 0xE2, 0xE2, 0xE2));
        _fill.Fill = new SolidColorBrush(dark ? Color.FromArgb(255, 0x8A, 0x8A, 0x8A) : Color.FromArgb(255, 0x9A, 0x9A, 0x9A));
        _loopRegion.Fill = new SolidColorBrush(dark
            ? Color.FromArgb(70, 0x60, 0xA5, 0xFA)
            : Color.FromArgb(60, 0x00, 0x78, 0xD4));
        _markerA.Fill = new SolidColorBrush(Color.FromArgb(255, 0x4C, 0xC2, 0x7B));
        _markerB.Fill = new SolidColorBrush(Color.FromArgb(255, 0xE8, 0x7A, 0x6B));
        _thumb.Fill = new SolidColorBrush(dark ? Colors.White : Color.FromArgb(255, 0x33, 0x33, 0x33));
        Layout();
    }

    private double TimeToX(double seconds)
    {
        if (_duration <= 0)
        {
            return 0;
        }

        return Math.Clamp(seconds / _duration, 0, 1) * ActualWidth;
    }

    private double XToTime(double x)
    {
        if (ActualWidth <= 0 || _duration <= 0)
        {
            return 0;
        }

        return Math.Clamp(x / ActualWidth, 0, 1) * _duration;
    }

    private void Layout()
    {
        double width = ActualWidth;
        double height = ActualHeight;
        if (width <= 1 || height <= 1)
        {
            return;
        }

        double trackTop = (height - TrackHeight) / 2;

        _track.Width = width;
        _track.Height = TrackHeight;
        Canvas.SetLeft(_track, 0);
        Canvas.SetTop(_track, trackTop);

        double progressX = TimeToX(_position);
        _fill.Width = progressX;
        _fill.Height = TrackHeight;
        Canvas.SetLeft(_fill, 0);
        Canvas.SetTop(_fill, trackTop);

        double aX = TimeToX(_loopA);
        double bX = TimeToX(_loopB);
        if (HasLoop)
        {
            _loopRegion.Visibility = Visibility.Visible;
            _loopRegion.Width = Math.Max(1, bX - aX);
            _loopRegion.Height = TrackHeight;
            Canvas.SetLeft(_loopRegion, aX);
            Canvas.SetTop(_loopRegion, trackTop);

            PlaceMarker(_markerA, aX, height);
            PlaceMarker(_markerB, bX, height);
        }
        else
        {
            _loopRegion.Visibility = Visibility.Collapsed;
            _markerA.Visibility = Visibility.Collapsed;
            _markerB.Visibility = Visibility.Collapsed;

            if (_loopA >= 0)
            {
                PlaceMarker(_markerA, aX, height);
            }

            if (_loopB >= 0)
            {
                PlaceMarker(_markerB, bX, height);
            }
        }

        _thumb.Width = ThumbSize;
        _thumb.Height = ThumbSize;
        Canvas.SetLeft(_thumb, Math.Clamp(progressX - ThumbSize / 2, 0, Math.Max(0, width - ThumbSize)));
        Canvas.SetTop(_thumb, (height - ThumbSize) / 2);
        _thumb.Opacity = _hover || _draggingThumb ? 1.0 : 0.75;
    }

    private void PlaceMarker(Rectangle marker, double x, double height)
    {
        marker.Visibility = Visibility.Visible;
        marker.Height = height - 6;
        Canvas.SetLeft(marker, Math.Clamp(x - MarkerWidth / 2, 0, Math.Max(0, ActualWidth - MarkerWidth)));
        Canvas.SetTop(marker, 3);
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_duration <= 0)
        {
            return;
        }

        var point = e.GetCurrentPoint(this);
        CapturePointer(e.Pointer);
        e.Handled = true;

        if (point.Properties.IsRightButtonPressed)
        {
            // Grab an existing marker if the click is close enough to one.
            double aX = TimeToX(_loopA);
            double bX = TimeToX(_loopB);

            if (_loopA >= 0 && Math.Abs(point.Position.X - aX) <= MarkerGrabDistance)
            {
                _draggingMarkerA = true;
                return;
            }

            if (_loopB >= 0 && Math.Abs(point.Position.X - bX) <= MarkerGrabDistance)
            {
                _draggingMarkerB = true;
                return;
            }

            CycleLoopPoints(XToTime(point.Position.X));
            return;
        }

        _draggingThumb = true;
        _position = XToTime(point.Position.X);
        Layout();
    }

    /// <summary>0 → set A, 1 → set B and loop, 2 → clear.</summary>
    private void CycleLoopPoints(double time)
    {
        if (_loopA < 0)
        {
            _loopA = time;
            _loopB = -1;
        }
        else if (_loopB < 0)
        {
            // Keep A ordered before B regardless of which side was clicked.
            if (time < _loopA)
            {
                (_loopA, time) = (time, _loopA);
            }

            _loopB = time;
        }
        else
        {
            _loopA = _loopB = -1;
        }

        Layout();
        LoopPointsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_draggingThumb && !_draggingMarkerA && !_draggingMarkerB)
        {
            return;
        }

        double time = XToTime(e.GetCurrentPoint(this).Position.X);

        if (_draggingThumb)
        {
            _position = time;
        }
        else if (_draggingMarkerA)
        {
            // A must stay before B.
            _loopA = _loopB > 0 ? Math.Min(time, _loopB) : time;
        }
        else
        {
            _loopB = Math.Max(time, _loopA);
        }

        Layout();
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        ReleasePointerCapture(e.Pointer);

        if (_draggingThumb)
        {
            SeekRequested?.Invoke(this, _position);
        }
        else if (_draggingMarkerA || _draggingMarkerB)
        {
            LoopPointsChanged?.Invoke(this, EventArgs.Empty);
        }

        EndDrag();
    }

    private void EndDrag()
    {
        _draggingThumb = false;
        _draggingMarkerA = false;
        _draggingMarkerB = false;
        Layout();
    }
}
