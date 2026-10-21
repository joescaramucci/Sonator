using System.ComponentModel;
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
    private int _stepCount;

    // The playback position, moved as the sequencer plays. The view pages along with it.
    private readonly Line _playhead = new()
    {
        Stroke = new SolidColorBrush(Color.FromRgb(0xFF, 0xD2, 0x4C)),
        StrokeThickness = 2,
        IsHitTestVisible = false,
    };

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
        var notesEnd = _notes.Count == 0
            ? 0
            : (int)Math.Ceiling(_notes.Max(note => note.StepIndex + note.StartOffset + note.DurationSteps + note.LengthFraction));
        _stepCount = Math.Max(StepsPerBar, Math.Max(track.Steps.Count, notesEnd));
        Title = $"Piano roll - {track.Name}";

        QuantizeGridBox.ItemsSource = NoteQuantizer.Grids;
        QuantizeGridBox.SelectedItem = NoteQuantizer.Grids.First(grid => grid.Label == "1/16");
        QuantizeStrengthBox.ItemsSource = NoteQuantizer.Strengths;
        QuantizeStrengthBox.SelectedIndex = 0;
        QuantizeSwingBox.ItemsSource = NoteQuantizer.Swings;
        QuantizeSwingBox.SelectedIndex = 0;

        Loaded += (_, _) => Redraw();
        PreviewKeyDown += OnPreviewKeyDown;
        viewModel.TrackNotesChanged += OnTrackNotesChanged;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Closed += (_, _) =>
        {
            viewModel.TrackNotesChanged -= OnTrackNotesChanged;
            viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        };
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.CurrentStepIndex))
        {
            UpdatePlayhead();
        }
    }

    // Moves the playhead to the current step. While playing, the view turns a page when the playhead nears its right
    // edge, and goes back when the playhead jumps behind it (the loop starting again).
    private void UpdatePlayhead()
    {
        if (_track.Steps.Count > _stepCount)
        {
            Redraw();
            return;
        }

        var step = _viewModel.CurrentStepIndex % _stepCount;
        var x = KeyboardWidth + step * StepWidth;
        _playhead.X1 = x;
        _playhead.X2 = x;

        if (!_viewModel.IsPlaying || PianoRollScroller.ViewportWidth <= 0)
        {
            return;
        }

        var left = PianoRollScroller.HorizontalOffset;
        var right = left + PianoRollScroller.ViewportWidth;
        if (x > right - 4 * StepWidth || x < left + KeyboardWidth)
        {
            PianoRollScroller.ScrollToHorizontalOffset(Math.Max(0, x - KeyboardWidth - 2 * StepWidth));
        }
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
        // The track can grow after the roll is opened (playback lengthens a short pattern), so the roll follows it.
        _stepCount = Math.Max(_stepCount, _track.Steps.Count);
        PianoRollCanvas.Children.Clear();
        var width = KeyboardWidth + _stepCount * StepWidth;
        PianoRollCanvas.Width = width;
        PianoRollCanvas.Height = _gridHeight + VelocityLaneHeight + 8;

        DrawRowsAndKeys(width);
        DrawGridLines();
        DrawVelocityLane(width);
        DrawNotes();

        _playhead.Y1 = 0;
        _playhead.Y2 = _gridHeight;
        PianoRollCanvas.Children.Add(_playhead);
        UpdatePlayhead();
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
        // One line per thirty-second note. Bars are the strongest, then quarter notes, sixteenths, and thirty-seconds faintest.
        for (var half = 0; half <= _stepCount * 2; half++)
        {
            var x = KeyboardWidth + half * StepWidth / 2;
            var barLine = half % (StepsPerBar * 2) == 0;
            var beatLine = half % 8 == 0;
            var sixteenthLine = half % 2 == 0;
            var (color, thickness) = barLine ? (Color.FromRgb(120, 150, 182), 1.5)
                : beatLine ? (Color.FromRgb(86, 110, 136), 1.1)
                : sixteenthLine ? (Color.FromRgb(64, 82, 104), 1.0)
                : (Color.FromRgb(48, 62, 79), 1.0);
            var line = new Line
            {
                X1 = x,
                X2 = x,
                Y1 = 0,
                Y2 = _gridHeight,
                Stroke = new SolidColorBrush(color),
                StrokeThickness = thickness,
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
            var x = KeyboardWidth + (note.StepIndex + note.StartOffset) * StepWidth + 1;
            var y = (HighestNote - note.NoteNumber) * RowHeight + 1;
            var noteWidth = Math.Max(3, (note.DurationSteps + note.LengthFraction) * StepWidth - 2);

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

    // Notes recorded or changed elsewhere appear in the open roll.
    private void OnTrackNotesChanged(PatternTrackViewModel changed)
    {
        if (!ReferenceEquals(changed, _track) || _dragOriginal is not null)
        {
            return;
        }

        _notes.Clear();
        _notes.AddRange(changed.MidiNoteEvents);
        _selectedIndex = -1;
        Redraw();
    }

    private void QuantizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_notes.Count == 0
            || QuantizeGridBox.SelectedItem is not QuantizeOption grid
            || QuantizeStrengthBox.SelectedItem is not QuantizeOption strength
            || QuantizeSwingBox.SelectedItem is not QuantizeOption swing)
        {
            return;
        }

        var quantized = NoteQuantizer.Quantize(_notes, grid.Value, strength.Value, swing.Value, QuantizeEndsToggle.IsChecked == true);
        _notes.Clear();
        _notes.AddRange(quantized);
        CommitNotes();
        Redraw();
    }

    private void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.PlayCommand.Execute(null);
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.StopCommand.Execute(null);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space)
        {
            _viewModel.TogglePlayback();
            e.Handled = true;
            return;
        }

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
