using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Sonator.Services.Voice;

namespace Sonator;

// Sondyne's sheet: the notes of a take as blobs on a grid of semitones, as wide as they last and as thick as they
// are loud, the pitch the voice sings drawn through them (where it was sung, faintly, when it has been changed).
// Click a note to select it (Ctrl adds), drag it up or down by semitones (Alt for cents); a double-click brings it
// to the middle of its semitone.
public sealed class SondyneView : FrameworkElement
{
    private const double KeysWidth = 46;
    private const double RowHeight = 16;

    private static readonly string[] Names = ["C", "C#", "D", "Eb", "E", "F", "F#", "G", "Ab", "A", "Bb", "B"];
    private static readonly Typeface Face = new("Segoe UI");
    private static readonly Brush Background = Frozen(new SolidColorBrush(Color.FromRgb(0x1E, 0x20, 0x23)));
    private static readonly Brush BlackRow = Frozen(new SolidColorBrush(Color.FromRgb(0x18, 0x1A, 0x1D)));
    private static readonly Brush KeyWhite = Frozen(new SolidColorBrush(Color.FromRgb(0xD8, 0xDC, 0xE0)));
    private static readonly Brush KeyBlack = Frozen(new SolidColorBrush(Color.FromRgb(0x2A, 0x2D, 0x31)));
    private static readonly Brush Blob = Frozen(new SolidColorBrush(Color.FromArgb(0xC8, 0x3C, 0xC8, 0xA6)));
    private static readonly Brush BlobEdited = Frozen(new SolidColorBrush(Color.FromArgb(0xD8, 0x7F, 0xB8, 0xFF)));
    private static readonly Brush BlobSelected = Frozen(new SolidColorBrush(Color.FromArgb(0xE8, 0xFF, 0x96, 0x3A)));
    private static readonly Pen RowPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x2C, 0x30, 0x35)), 1));
    private static readonly Pen OctavePen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x46, 0x4C, 0x53)), 1));
    private static readonly Pen SecondPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x2E, 0x33, 0x39)), 1));
    private static readonly Pen PitchPen = Frozen(new Pen(Brushes.White, 1.2));
    private static readonly Pen SungPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF)), 1) { DashStyle = DashStyles.Dot });
    private static readonly Pen PlayheadPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0xD2, 0x4C)), 1.5));

    private SondyneTake? _take;
    private double[] _edited = [];
    private int _lowest = 48;
    private int _highest = 72;
    private readonly HashSet<SondyneNote> _selected = new();

    // Raised when notes have been changed by dragging or double-clicking them, and when the selection changes.
    public event Action? Edited;
    public event Action? SelectionChanged;

    public double PixelsPerSecond { get; set; } = 160;

    // Where the preview plays, in seconds, or NaN when it does not.
    public double Playhead { get; set; } = double.NaN;

    public IReadOnlyCollection<SondyneNote> Selected => _selected;

    public SondyneTake? Take
    {
        get => _take;
        set
        {
            _take = value;
            _selected.Clear();
            _lowest = int.MaxValue;
            _highest = int.MinValue;
            Refresh();
        }
    }

    // After the notes have been changed from outside (the sliders): redraws, keeping the range.
    public void Refresh()
    {
        if (_take is not null)
        {
            _edited = Sondyne.EditedPitch(_take);
            var pitches = _take.Notes.SelectMany(note => new[] { note.SungPitch, note.EditedPitch }).ToList();
            // The range grows to take in the notes as they move, and never shrinks while the take is open.
            if (pitches.Count > 0)
            {
                _lowest = Math.Min(_lowest, (int)Math.Floor(pitches.Min()) - 5);
                _highest = Math.Max(_highest, (int)Math.Ceiling(pitches.Max()) + 5);
            }
        }

        if (_lowest > _highest)
        {
            (_lowest, _highest) = (48, 72);
        }

        InvalidateMeasure();
        InvalidateVisual();
    }

    public void SelectAll()
    {
        if (_take is null)
        {
            return;
        }

        _selected.Clear();
        _selected.UnionWith(_take.Notes);
        SelectionChanged?.Invoke();
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var seconds = _take is null ? 10 : _take.Clip.Duration.TotalSeconds;
        return new Size(KeysWidth + seconds * PixelsPerSecond + 40, (_highest - _lowest + 1) * RowHeight);
    }

    private double Y(double pitch) => (_highest - pitch + 0.5) * RowHeight;

    private double X(double seconds) => KeysWidth + seconds * PixelsPerSecond;

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        dc.DrawRectangle(Background, null, new Rect(0, 0, width, ActualHeight));
        if (_take is null)
        {
            return;
        }

        // The semitones, the black keys' rows darker, a line at each C; the keys and the note names on the left.
        for (var note = _lowest; note <= _highest; note++)
        {
            var top = Y(note) - RowHeight / 2;
            var black = (((note % 12) + 12) % 12) is 1 or 3 or 6 or 8 or 10;
            if (black)
            {
                dc.DrawRectangle(BlackRow, null, new Rect(KeysWidth, top, width - KeysWidth, RowHeight));
            }

            dc.DrawLine(note % 12 == 0 ? OctavePen : RowPen, new Point(KeysWidth, top + RowHeight), new Point(width, top + RowHeight));
            dc.DrawRectangle(black ? KeyBlack : KeyWhite, RowPen, new Rect(0, top, KeysWidth, RowHeight));
            if (note % 12 == 0 || !black)
            {
                var label = new FormattedText($"{Names[((note % 12) + 12) % 12]}{note / 12 - 1}", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    Face, note % 12 == 0 ? 10 : 8.5, black ? Brushes.White : Brushes.Black, 1.0);
                dc.DrawText(label, new Point(KeysWidth - label.Width - 4, top + (RowHeight - label.Height) / 2));
            }
        }

        // A line each second.
        for (var second = 0; second <= _take.Clip.Duration.TotalSeconds; second++)
        {
            dc.DrawLine(SecondPen, new Point(X(second), 0), new Point(X(second), ActualHeight));
        }

        // The notes: their loudness outline round their pitch, then the pitch sung through them.
        var loudest = Math.Max(1e-6, _take.Loudness.DefaultIfEmpty(0).Max());
        var frameSeconds = _take.FrameSeconds;
        foreach (var note in _take.Notes)
        {
            var centre = Y(note.EditedPitch);
            var outline = new StreamGeometry();
            using (var shape = outline.Open())
            {
                var points = new List<Point>();
                for (var frame = note.StartFrame; frame < note.EndFrame; frame++)
                {
                    var thickness = 2.5 + 6 * Math.Sqrt(_take.Loudness[Math.Min(frame, _take.Loudness.Length - 1)] / loudest);
                    points.Add(new Point(X(frame * frameSeconds), centre - thickness));
                }

                for (var frame = note.EndFrame - 1; frame >= note.StartFrame; frame--)
                {
                    var thickness = 2.5 + 6 * Math.Sqrt(_take.Loudness[Math.Min(frame, _take.Loudness.Length - 1)] / loudest);
                    points.Add(new Point(X(frame * frameSeconds), centre + thickness));
                }

                if (points.Count > 2)
                {
                    shape.BeginFigure(points[0], true, true);
                    shape.PolyLineTo(points.Skip(1).ToList(), true, true);
                }
            }

            outline.Freeze();
            var fill = _selected.Contains(note) ? BlobSelected : note.IsEdited ? BlobEdited : Blob;
            dc.DrawGeometry(fill, null, outline);
        }

        DrawCurve(dc, _take.Pitch, SungPen, onlyEdited: true);
        DrawCurve(dc, _edited, PitchPen, onlyEdited: false);

        if (!double.IsNaN(Playhead))
        {
            dc.DrawLine(PlayheadPen, new Point(X(Playhead), 0), new Point(X(Playhead), ActualHeight));
        }
    }

    // A pitch curve, broken where nothing is sung; only within the notes changed, for the pitch as it was sung.
    private void DrawCurve(DrawingContext dc, double[] pitch, Pen pen, bool onlyEdited)
    {
        var take = _take!;
        var geometry = new StreamGeometry();
        using (var line = geometry.Open())
        {
            foreach (var note in take.Notes.Where(note => !onlyEdited || note.IsEdited))
            {
                var started = false;
                for (var frame = note.StartFrame; frame < note.EndFrame && frame < pitch.Length; frame++)
                {
                    if (double.IsNaN(pitch[frame]))
                    {
                        started = false;
                        continue;
                    }

                    var point = new Point(X(frame * take.FrameSeconds), Y(pitch[frame]));
                    if (!started)
                    {
                        line.BeginFigure(point, false, false);
                        started = true;
                    }
                    else
                    {
                        line.LineTo(point, true, false);
                    }
                }
            }
        }

        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }

    // ---- Selecting and dragging ----

    private Point? _dragFrom;
    private Dictionary<SondyneNote, double> _shiftsAtDrag = new();
    private bool _dragged;

    private SondyneNote? NoteAt(Point point)
    {
        if (_take is null)
        {
            return null;
        }

        var seconds = (point.X - KeysWidth) / PixelsPerSecond;
        return _take.Notes
            .Where(note => seconds >= note.StartFrame * _take.FrameSeconds && seconds <= note.EndFrame * _take.FrameSeconds)
            .Where(note => Math.Abs(Y(note.EditedPitch) - point.Y) <= RowHeight)
            .OrderBy(note => Math.Abs(Y(note.EditedPitch) - point.Y))
            .FirstOrDefault();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        var point = e.GetPosition(this);
        var note = NoteAt(point);
        var adding = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (note is null)
        {
            if (!adding)
            {
                _selected.Clear();
                SelectionChanged?.Invoke();
                InvalidateVisual();
            }

            return;
        }

        if (e.ClickCount == 2)
        {
            note.Center = 1;
            Refresh();
            Edited?.Invoke();
            return;
        }

        if (adding)
        {
            if (!_selected.Remove(note))
            {
                _selected.Add(note);
            }
        }
        else if (!_selected.Contains(note))
        {
            _selected.Clear();
            _selected.Add(note);
        }

        SelectionChanged?.Invoke();
        _dragFrom = point;
        _dragged = false;
        _shiftsAtDrag = _selected.ToDictionary(item => item, item => item.Shift);
        CaptureMouse();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragFrom is not { } from || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var semitones = (from.Y - e.GetPosition(this).Y) / RowHeight;
        semitones = (Keyboard.Modifiers & ModifierKeys.Alt) != 0 ? Math.Round(semitones * 20) / 20 : Math.Round(semitones);
        foreach (var (note, shift) in _shiftsAtDrag)
        {
            note.Shift = shift + semitones;
        }

        _dragged |= Math.Abs(semitones) > 1e-9;
        _edited = Sondyne.EditedPitch(_take!);
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_dragFrom is null)
        {
            return;
        }

        _dragFrom = null;
        ReleaseMouseCapture();
        if (_dragged)
        {
            Refresh();
            Edited?.Invoke();
        }
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
