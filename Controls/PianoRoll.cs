using GoMidi.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace GoMidi.Controls;

/// <summary>
/// Waterfall piano-roll view. Notes scroll right-to-left past a playhead that
/// sits a quarter of the way in, so what is about to be played is always
/// visible. Rows are only created for the time window on screen, and the
/// rectangle instances are pooled rather than reallocated every frame.
/// </summary>
public sealed class PianoRoll : UserControl
{
    /// <summary>Seconds of music visible across the full width.</summary>
    private const double ViewSeconds = 9.0;

    /// <summary>Fraction of the width the playhead sits at.</summary>
    private const double PlayheadFraction = 0.26;

    private const double RowHeight = 7.0;
    private const double MinNoteWidth = 2.0;

    private static readonly Color[] TrackPalette =
    [
        Color.FromArgb(255, 0x4C, 0x8B, 0xF5),
        Color.FromArgb(255, 0xE0, 0x6C, 0x9F),
        Color.FromArgb(255, 0x4F, 0xC3, 0xA1),
        Color.FromArgb(255, 0xF2, 0xA6, 0x5B),
        Color.FromArgb(255, 0x9B, 0x8B, 0xF0),
        Color.FromArgb(255, 0x5B, 0xC8, 0xE8),
        Color.FromArgb(255, 0xE8, 0x7A, 0x6B),
        Color.FromArgb(255, 0x8F, 0xC4, 0x4A),
    ];

    private readonly Canvas _canvas = new();
    private readonly Rectangle _playhead = new() { Width = 1.5, IsHitTestVisible = false };
    private readonly List<Rectangle> _pool = new();

    private MidiFile? _song;
    private List<RawNote> _notes = new();
    private int _lowPitch = 48;
    private int _highPitch = 84;
    private double _position;

    public PianoRoll()
    {
        Content = _canvas;
        Background = new SolidColorBrush(Colors.Transparent);
        IsHitTestVisible = false;

        _playhead.Fill = new SolidColorBrush(Color.FromArgb(220, 0xFF, 0xFF, 0xFF));
        _canvas.Children.Add(_playhead);

        SizeChanged += (_, _) => Redraw();
    }

    /// <summary>Loads a song and recomputes the pitch range the view spans.</summary>
    public void SetSong(MidiFile? song)
    {
        _song = song;
        _notes = song is null ? new List<RawNote>() : song.Tracks.SelectMany(t => t.Notes).ToList();

        if (_notes.Count > 0)
        {
            int low = _notes.Min(n => n.Pitch);
            int high = _notes.Max(n => n.Pitch);

            // Pad by a couple of semitones so the extremes are not glued to the edges.
            _lowPitch = Math.Max(0, low - 2);
            _highPitch = Math.Min(127, high + 2);
        }
        else
        {
            _lowPitch = 48;
            _highPitch = 84;
        }

        Redraw();
    }

    /// <summary>Updates the playhead and scrolls the note window.</summary>
    public void SetPosition(double seconds)
    {
        _position = seconds;
        Redraw();
    }

    /// <summary>Total seconds of music, used to decide whether the view is scrolled.</summary>
    public double Duration => _song?.Length ?? 0;

    private void Redraw()
    {
        double width = ActualWidth;
        double height = ActualHeight;
        if (width <= 1 || height <= 1)
        {
            return;
        }

        // Fit the whole pitch span into the available height; never go below a
        // legible row height. Pitches are inclusive at both ends, so there is
        // one more row than the span.
        int pitchSpan = Math.Max(1, _highPitch - _lowPitch);
        int rowCount = pitchSpan + 1;
        double rowHeight = Math.Min(RowHeight, height / rowCount);
        rowHeight = Math.Max(rowHeight, 2.0);

        // The chart is anchored to the bottom so low notes sit lowest.
        double chartHeight = rowCount * rowHeight;
        double top = Math.Max(0, height - chartHeight);

        // Custom-drawn content must be clipped explicitly; a Canvas does not
        // clip its children and WinUI has no ClipToBounds.
        _canvas.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, width, height) };

        double secondsOnScreen = ViewSeconds;
        double pixelsPerSecond = width / secondsOnScreen;
        double viewStart = _position - secondsOnScreen * PlayheadFraction;
        double viewEnd = viewStart + secondsOnScreen;

        int used = 0;
        foreach (RawNote note in _notes)
        {
            if (note.EndSeconds < viewStart || note.StartSeconds > viewEnd)
            {
                continue;
            }

            double x = (note.StartSeconds - viewStart) * pixelsPerSecond;
            double w = Math.Max(MinNoteWidth, note.Duration * pixelsPerSecond);

            // Clip the left edge so long notes keep their rectangle but start on screen.
            if (x < 0)
            {
                w += x;
                x = 0;
            }

            // Clip the right edge too, so a long note cannot spill past the view.
            if (x + w > width)
            {
                w = width - x;
            }

            // Skip before renting a rectangle: renting first would leave the slot
            // visible at its previous position, drawing a stale note.
            if (w < MinNoteWidth / 2)
            {
                continue;
            }

            Rectangle rect = Rent(used++);
            Canvas.SetLeft(rect, x);
            Canvas.SetTop(rect, top + (_highPitch - note.Pitch) * rowHeight);
            rect.Width = w;
            rect.Height = Math.Max(1.5, rowHeight - 1);

            Color color = TrackPalette[Math.Abs(note.TrackIndex) % TrackPalette.Length];
            bool sounding = _position >= note.StartSeconds && _position <= note.EndSeconds;

            // Notes under the playhead light up, which makes dense passages readable.
            rect.Fill = new SolidColorBrush(sounding
                ? Color.FromArgb(255, 0xFF, 0xD5, 0x66)
                : color);

            rect.Opacity = sounding ? 1.0 : 0.85;
            rect.Visibility = Visibility.Visible;
        }

        // Hide whatever the pool did not need this frame.
        for (int i = used; i < _pool.Count; i++)
        {
            _pool[i].Visibility = Visibility.Collapsed;
        }

        _playhead.Height = height;
        Canvas.SetLeft(_playhead, width * PlayheadFraction);
        Canvas.SetTop(_playhead, 0);
        _playhead.Visibility = _song is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private Rectangle Rent(int index)
    {
        while (_pool.Count <= index)
        {
            var rect = new Rectangle { RadiusX = 1.5, RadiusY = 1.5, IsHitTestVisible = false };
            _pool.Add(rect);
            _canvas.Children.Add(rect);
        }

        return _pool[index];
    }
}
