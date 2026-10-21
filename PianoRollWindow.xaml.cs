using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Sonator.ViewModels;

namespace Sonator;

// Piano roll for one track. Notes are shown as bars on a vertical keyboard; the lane at the bottom edits velocity.
// Edits work on a copy and are committed to the track once per gesture, so each gesture is one undo step.
public partial class PianoRollWindow : Window
{
    private const double StepWidth = 14;
    private const double RowHeight = 10;
    private const double KeyboardWidth = 56;
    private const double VelocityLaneHeight = 90;
    private const int HighestNote = 96;
    private const int LowestNote = 36;
    private const int DefaultVelocity = 100;
    private const int StepsPerBar = 16;

    private readonly MainViewModel _viewModel;
    private readonly PatternTrackViewModel _track;
    private readonly List<MidiNoteEventViewModel> _notes;
    private readonly double _gridHeight = (HighestNote - LowestNote + 1) * RowHeight;
    private readonly int _stepCount;

    private int _selectedIndex = -1;
    private int _dragIndex = -1;
    private MidiNoteEventViewModel? _dragOriginal;
    private (int Step, int Note)? _dragStart;
    private bool _velocityDrag;

    public PianoRollWindow(MainViewModel viewModel, PatternTrackViewModel track)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _track = track;
        _notes = track.MidiNoteEvents.Count > 0 ? track.MidiNoteEvents.ToList() : NotesFromSteps(track);
        _stepCount = Math.Max(StepsPerBar, track.Steps.Count);
        Title = $"Piano roll - {track.Name}";

        Loaded += (_, _) => Redraw();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    // Playback falls back to the step grid when a track has no recorded notes, so the roll starts from the same notes.
    private static List<MidiNoteEventViewModel> NotesFromSteps(PatternTrackViewModel track)
    {
        var notes = new List<MidiNoteEventViewModel>();
        for (var index = 0; index < track.Steps.Count; index++)
        {
            var step = track.Steps[index];
            if (step.IsActive)
            {
                notes.Add(new MidiNoteEventViewModel(index, Math.Max(1, step.DurationSteps), step.MidiNoteNumber ?? track.NoteNumber, step.Velocity));
            }
        }
        return notes;
    }

    private void Redraw()
    {
        PianoRollCanvas.Children.Clear();
        var width = KeyboardWidth + _stepCount * StepWidth;
        PianoRollCanvas.Width = width;
        PianoRollCanvas.Height = _gridHeight + VelocityLaneHeight + 8;

        DrawRowsAndKeys(width);
        DrawGridLines();
        DrawVelocityLane(width);
        DrawNotes();
    }

    private void DrawRowsAndKeys(double width)
    {
        for (var note = LowestNote; note <= HighestNote; note++)
        {
            var y = (HighestNote - note) * RowHeight;
            var black = IsBlackKey(note);

            AddRect(KeyboardWidth, y, width - KeyboardWidth, RowHeight, black ? "#1F2935" : "#243142", "#2C3A4B");
            AddRect(0, y, KeyboardWidth, RowHeight, black ? "#0F151C" : "#E8EEF4", "#5A6B7D");

            if (note % 12 == 0)
            {
                var label = new TextBlock
                {
                    Text = $"C{note / 12 - 1}",
                    FontSize = 9,
                    Foreground = Brushes.Black,
                };
                Canvas.SetLeft(label, 4);
                Canvas.SetTop(label, y - 1);
                PianoRollCanvas.Children.Add(label);
            }
        }
    }

    private void DrawGridLines()
    {
        for (var step = 0; step <= _stepCount; step++)
        {
            var x = KeyboardWidth + step * StepWidth;
            var barLine = step % StepsPerBar == 0;
            var line = new Line
            {
                X1 = x,
                X2 = x,
                Y1 = 0,
                Y2 = _gridHeight,
                Stroke = new SolidColorBrush(barLine ? Color.FromRgb(96, 122, 150) : Color.FromRgb(44, 58, 75)),
                StrokeThickness = barLine ? 1.2 : 0.6,
            };
            PianoRollCanvas.Children.Add(line);
        }
    }

    private void DrawVelocityLane(double width)
    {
        AddRect(KeyboardWidth, _gridHeight + 8, width - KeyboardWidth, VelocityLaneHeight, "#152029", "#2C3A4B");
    }

    private void DrawNotes()
    {
        for (var index = 0; index < _notes.Count; index++)
        {
            var note = _notes[index];
            var selected = index == _selectedIndex;
            var x = KeyboardWidth + note.StepIndex * StepWidth + 1;
            var y = (HighestNote - note.NoteNumber) * RowHeight + 1;
            var noteWidth = Math.Max(StepWidth - 2, note.DurationSteps * StepWidth - 2);

            AddRect(x, y, noteWidth, RowHeight - 2, selected ? "#F2C14E" : "#4FA3D9", "#0E1620");

            var velocityHeight = note.Velocity / 127.0 * (VelocityLaneHeight - 8);
            var laneTop = _gridHeight + 8;
            AddRect(x + 2, laneTop + VelocityLaneHeight - velocityHeight, StepWidth - 6, velocityHeight,
                selected ? "#F2C14E" : "#4FA3D9", "#0E1620");
        }
    }

    private void AddRect(double x, double y, double width, double height, string fill, string stroke)
    {
        var rect = new Rectangle
        {
            Width = Math.Max(0, width),
            Height = Math.Max(0, height),
            Fill = (SolidColorBrush)new BrushConverter().ConvertFromString(fill)!,
            Stroke = (SolidColorBrush)new BrushConverter().ConvertFromString(stroke)!,
            StrokeThickness = 0.5,
        };
        Canvas.SetLeft(rect, x);
        Canvas.SetTop(rect, y);
        PianoRollCanvas.Children.Add(rect);
    }

    private static bool IsBlackKey(int note)
    {
        return note % 12 is 1 or 3 or 6 or 8 or 10;
    }

    private (int Step, int Note)? CellAt(Point point)
    {
        if (point.X < KeyboardWidth || point.Y < 0 || point.Y >= _gridHeight)
        {
            return null;
        }

        var step = (int)((point.X - KeyboardWidth) / StepWidth);
        var note = HighestNote - (int)(point.Y / RowHeight);
        if (step < 0 || step >= _stepCount || note < LowestNote || note > HighestNote)
        {
            return null;
        }
        return (step, note);
    }

    // Index of the note whose bar covers this cell, or -1.
    private int NoteIndexAt(int step, int note)
    {
        for (var index = _notes.Count - 1; index >= 0; index--)
        {
            var candidate = _notes[index];
            if (candidate.NoteNumber == note && step >= candidate.StepIndex && step < candidate.StepIndex + candidate.DurationSteps)
            {
                return index;
            }
        }
        return -1;
    }

    private int NoteIndexInVelocityLane(int step)
    {
        for (var index = _notes.Count - 1; index >= 0; index--)
        {
            var candidate = _notes[index];
            if (step >= candidate.StepIndex && step < candidate.StepIndex + candidate.DurationSteps)
            {
                return index;
            }
        }
        return -1;
    }

    private void OnCanvasMouseDown(object sender, MouseButtonEventArgs e)
    {
        var point = e.GetPosition(PianoRollCanvas);
        Focus();

        if (point.X < KeyboardWidth)
        {
            return;
        }

        if (point.Y >= _gridHeight)
        {
            var step = (int)((point.X - KeyboardWidth) / StepWidth);
            var index = NoteIndexInVelocityLane(step);
            if (index < 0)
            {
                return;
            }

            _velocityDrag = true;
            _selectedIndex = index;
            _dragIndex = index;
            SetVelocityFromPoint(point.Y);
            PianoRollCanvas.CaptureMouse();
            Redraw();
            return;
        }

        var cell = CellAt(point);
        if (cell is null)
        {
            return;
        }

        var hit = NoteIndexAt(cell.Value.Step, cell.Value.Note);
        if (hit >= 0)
        {
            _selectedIndex = hit;
            _dragIndex = hit;
            _dragOriginal = _notes[hit];
            _dragStart = cell;
            PianoRollCanvas.CaptureMouse();
            Redraw();
            return;
        }

        _notes.Add(new MidiNoteEventViewModel(cell.Value.Step, 1, cell.Value.Note, DefaultVelocity));
        _selectedIndex = _notes.Count - 1;
        CommitNotes();
        Redraw();
    }

    private void OnCanvasMouseMove(object sender, MouseEventArgs e)
    {
        if (!PianoRollCanvas.IsMouseCaptured || _dragIndex < 0)
        {
            return;
        }

        var point = e.GetPosition(PianoRollCanvas);
        if (_velocityDrag)
        {
            SetVelocityFromPoint(point.Y);
            Redraw();
            return;
        }

        var cell = CellAt(point);
        if (cell is null || _dragStart is null || _dragOriginal is null)
        {
            return;
        }

        var deltaStep = cell.Value.Step - _dragStart.Value.Step;
        var deltaNote = cell.Value.Note - _dragStart.Value.Note;
        var newStep = Math.Clamp(_dragOriginal.StepIndex + deltaStep, 0, _stepCount - _dragOriginal.DurationSteps);
        var newNote = Math.Clamp(_dragOriginal.NoteNumber + deltaNote, LowestNote, HighestNote);
        _notes[_dragIndex] = _dragOriginal with { StepIndex = newStep, NoteNumber = newNote };
        Redraw();
    }

    private void OnCanvasMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!PianoRollCanvas.IsMouseCaptured)
        {
            return;
        }

        PianoRollCanvas.ReleaseMouseCapture();
        var moved = _dragIndex >= 0;
        _dragIndex = -1;
        _dragOriginal = null;
        _dragStart = null;
        _velocityDrag = false;
        if (moved)
        {
            CommitNotes();
        }
        Redraw();
    }

    private void OnCanvasRightMouseDown(object sender, MouseButtonEventArgs e)
    {
        var cell = CellAt(e.GetPosition(PianoRollCanvas));
        if (cell is null)
        {
            return;
        }

        var hit = NoteIndexAt(cell.Value.Step, cell.Value.Note);
        if (hit >= 0)
        {
            RemoveNote(hit);
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && _selectedIndex >= 0 && _selectedIndex < _notes.Count)
        {
            RemoveNote(_selectedIndex);
            e.Handled = true;
        }
    }

    private void RemoveNote(int index)
    {
        _notes.RemoveAt(index);
        _selectedIndex = -1;
        CommitNotes();
        Redraw();
    }

    private void SetVelocityFromPoint(double y)
    {
        var laneTop = _gridHeight + 8;
        var fraction = Math.Clamp(1.0 - (y - laneTop) / (VelocityLaneHeight - 8), 0.0, 1.0);
        var velocity = Math.Clamp((int)Math.Round(fraction * 127), 1, 127);
        _notes[_dragIndex] = _notes[_dragIndex] with { Velocity = velocity };
    }

    private void CommitNotes()
    {
        var ordered = _notes
            .OrderBy(note => note.StepIndex)
            .ThenBy(note => note.NoteNumber)
            .ToList();
        _viewModel.ReplaceTrackNotes(_track, ordered);
        _notes.Clear();
        _notes.AddRange(ordered);
        _selectedIndex = -1;
    }
}
