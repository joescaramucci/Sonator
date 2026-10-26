using System.Globalization;
using System.IO;
using Path = System.IO.Path;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Sonator.Services;
using Sonator.ViewModels;

namespace Sonator;

// Waveform editor for one audio clip. Edits act on the selection, or on the whole clip when nothing is selected.
// Each edit keeps the previous clip so Undo can restore it.
public partial class WaveformEditorWindow : Window
{
    private const double CanvasHeight = 360;

    private AudioClip? _clip;
    private string? _path;
    private readonly Stack<AudioClip> _undo = new();
    private float[]? _clipboard;
    private int _clipboardChannels;
    private int _selectionAnchor = -1;
    private double _pressX;

    // Where a click put the start: Space plays from there with nothing selected (from the clip's start, -1).
    private int _startFrame = -1;
    private int _selectionStart = -1;
    private int _selectionEnd = -1;

    // The frames in view: all of the clip, or the part zoomed in on with the wheel.
    private int _viewStart;
    private int _viewLength;

    // The playhead: where the clip being played has got to, by the time since it started.
    private readonly System.Diagnostics.Stopwatch _playClock = new();
    private int _playFrom;
    private int _playTo;
    private bool _playLoops;
    private readonly Line _playhead = new()
    {
        Stroke = new SolidColorBrush(Color.FromRgb(242, 242, 242)),
        StrokeThickness = 1.5,
        Y1 = 0,
        Y2 = CanvasHeight,
        IsHitTestVisible = false,
    };

    private WaveOutEvent? _output;

    // Ends a preview played through Sonator's own output when it has played through.
    private System.Windows.Threading.DispatcherTimer? _previewEnd;
    private readonly double _songTempo;
    private readonly MainViewModel? _viewModel;

    // The track whose clip is being edited, when it was opened from the timeline: SEND TO TRACK puts the
    // edit back into it.
    private PatternTrackViewModel? _sourceTrack;

    public WaveformEditorWindow(string? path = null, double songTempo = 120, MainViewModel? viewModel = null)
    {
        InitializeComponent();
        WindowFrame.UseDarkFrame(this);
        _songTempo = songTempo;
        _viewModel = viewModel;
        Closed += (_, _) => StopPlayback();
        if (path is not null)
        {
            OpenFile(path);
        }
        else
        {
            Info("Open an audio file to start.");
        }
    }

    private void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open audio file",
            Filter = "Audio files|*.mp3;*.wav;*.m4a;*.wma;*.aiff|All files|*.*",
        };
        if (dialog.ShowDialog(this) == true)
        {
            OpenFile(dialog.FileName);
        }
    }

    // Opens a track's clip, to edit it and send it back into that track.
    public void OpenTrackClip(PatternTrackViewModel track, string path)
    {
        OpenFile(path);
        _sourceTrack = _clip is null ? null : track;
        if (_sourceTrack is not null)
        {
            Title = $"Waveform editor - {track.Name}";
            Info($"{track.Name}'s clip: {_clip!.Duration.TotalSeconds:F2} s, {_clip.Channels} channel(s), {_clip.SampleRate} Hz. Send to track puts the edit back into it.");
        }
    }

    public bool HasClip => _clip is not null;

    private void OpenFile(string path)
    {
        _sourceTrack = null;
        try
        {
            _clip = AudioClipLoader.Load(path);
            _path = path;
            _undo.Clear();
            _startFrame = -1;
            ClearSelection();
            ShowWholeClip();
            Title = $"Waveform editor - {Path.GetFileName(path)}";
            Redraw();
            Info($"{Path.GetFileName(path)}: {_clip.Duration.TotalSeconds:F2} s, {_clip.Channels} channel(s), {_clip.SampleRate} Hz.");
            TakeKeyboard();
        }
        catch (Exception ex)
        {
            Info($"Could not open the file: {ex.Message}");
        }
    }

    // The editor in front with the keys, once the click or menu that opened it is done: opened from the timeline's
    // menu, the main window took them back and Space played the song instead of the clip.
    private void TakeKeyboard()
    {
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, () =>
        {
            if (IsVisible)
            {
                Activate();
                WaveBorder.Focus();
            }
        });
    }

    private void PlayButton_Click(object sender, RoutedEventArgs e) => Play(wholeClip: true);

    private void PlaySelectionButton_Click(object sender, RoutedEventArgs e) => Play(wholeClip: false);

    private void StopButton_Click(object sender, RoutedEventArgs e) => StopPlayback();

    private void CutButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetRange(out var start, out var end))
        {
            Info("Select a part of the clip to cut.");
            return;
        }

        _clipboard = AudioEdits.Copy(_clip!.Samples, _clip.Channels, start, end);
        _clipboardChannels = _clip.Channels;
        Apply(AudioEdits.Cut(_clip.Samples, _clip.Channels, start, end), $"Cut {end - start} frames");
        ClearSelection();
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetRange(out var start, out var end))
        {
            Info("Select a part of the clip to copy.");
            return;
        }

        _clipboard = AudioEdits.Copy(_clip!.Samples, _clip.Channels, start, end);
        _clipboardChannels = _clip.Channels;
        Info($"Copied {end - start} frames.");
    }

    private void PasteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_clip is null || _clipboard is null)
        {
            Info("Nothing copied yet.");
            return;
        }
        if (_clipboardChannels != _clip.Channels)
        {
            Info("The copied audio has a different number of channels.");
            return;
        }

        var at = _selectionStart >= 0 ? _selectionStart : 0;
        Apply(AudioEdits.Paste(_clip.Samples, _clip.Channels, at, _clipboard), "Paste");
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_clip is null)
        {
            return;
        }
        if (!TryGetRange(out var start, out var end))
        {
            Info("Select a part of the clip to delete.");
            return;
        }

        // The rest closes up over the removed part, so the two sides join.
        Apply(AudioEdits.Cut(_clip.Samples, _clip.Channels, start, end), $"Deleted {end - start} frames; the rest is joined");
        ClearSelection();
    }

    private void NormalizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_clip is null)
        {
            return;
        }
        Apply(AudioEdits.Normalize(_clip.Samples), "Normalize");
    }

    private void FadeInButton_Click(object sender, RoutedEventArgs e)
    {
        if (_clip is null)
        {
            return;
        }
        var (start, end) = RangeOrWhole();
        Apply(AudioEdits.FadeIn(_clip.Samples, _clip.Channels, start, end), "Fade in");
    }

    private void FadeOutButton_Click(object sender, RoutedEventArgs e)
    {
        if (_clip is null)
        {
            return;
        }
        var (start, end) = RangeOrWhole();
        Apply(AudioEdits.FadeOut(_clip.Samples, _clip.Channels, start, end), "Fade out");
    }

    private void StretchButton_Click(object sender, RoutedEventArgs e)
    {
        if (_clip is null)
        {
            return;
        }
        if (!double.TryParse(ClipBpmBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var clipBpm) || clipBpm <= 0)
        {
            Info("Enter the tempo of the clip in BPM.");
            return;
        }

        // Output length over input length: a clip recorded at a faster tempo than the song gets longer.
        var ratio = clipBpm / _songTempo;
        Apply(TimeStretcher.Stretch(_clip.Samples, _clip.Channels, ratio), $"Stretched from {clipBpm:F1} to {_songTempo:F1} BPM");
    }

    private void SendToTrackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_clip is null)
        {
            return;
        }
        if (_viewModel is null || _viewModel.SelectedTrack is not { } track)
        {
            Info("Select an empty track in the main window first.");
            return;
        }
        var isSource = ReferenceEquals(track, _sourceTrack) && _viewModel.Tracks.Contains(track);
        if (!isSource && !MainViewModel.IsEmptyTrack(track))
        {
            Info($"{track.Name} is not empty. Choose an empty track.");
            return;
        }

        var (start, end) = RangeOrWhole();
        var clip = new AudioClip(AudioEdits.Copy(_clip.Samples, _clip.Channels, start, end), _clip.Channels, _clip.SampleRate);
        var path = AudioClipLoader.SaveToLibrary(clip);
        if (isSource)
        {
            _viewModel.ReplaceAudioClip(track, path); // the edit goes back where the clip came from
        }
        else
        {
            _viewModel.AssignAudioClip(track, path);
        }
        Info($"Sent {clip.Duration.TotalSeconds:F2} s to {track.Name}.");
    }

    private void UndoButton_Click(object sender, RoutedEventArgs e)
    {
        if (_undo.Count == 0)
        {
            Info("Nothing to undo.");
            return;
        }
        _clip = _undo.Pop();
        ClearSelection();
        ClampView();
        Redraw();
        Info("Undone.");
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_clip is null)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Save as WAV",
            Filter = "WAV file|*.wav",
            FileName = _path is null ? "edited.wav" : Path.GetFileNameWithoutExtension(_path) + " edited.wav",
        };
        if (dialog.ShowDialog(this) == true)
        {
            AudioClipLoader.SaveWav(_clip, dialog.FileName);
            Info($"Saved {Path.GetFileName(dialog.FileName)}.");
        }
    }

    private void WaveCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        WaveBorder.Focus(); // Space plays the clip, even after typing in the BPM box
        if (_clip is null)
        {
            return;
        }

        // A double-click plays the clip from there to its end.
        if (e.ClickCount == 2)
        {
            ClearSelection();
            _startFrame = FrameAt(e.GetPosition(WaveCanvas).X);
            Redraw();
            Play(_startFrame, _clip.FrameCount, $"Playing from {Seconds(_startFrame)} (bar {BarBeat(_startFrame)}).");
            e.Handled = true;
            return;
        }

        _pressX = e.GetPosition(WaveCanvas).X;
        _selectionAnchor = FrameAt(_pressX);
        _selectionStart = _selectionAnchor;
        _selectionEnd = _selectionAnchor;
        WaveBorder.CaptureMouse();
        Redraw();
    }

    // Moved less than this from where it was pressed, the mouse clicked rather than dragged.
    private const double ClickPixels = 3;

    private void WaveCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (_clip is null || !WaveBorder.IsMouseCaptured)
        {
            return;
        }

        // Dragged past an edge while zoomed in: the view follows.
        var x = e.GetPosition(WaveCanvas).X;
        var width = WaveCanvas.ActualWidth;
        if ((x < 0 || x > width) && _viewLength < _clip.FrameCount)
        {
            _viewStart += (int)(Math.Clamp((x < 0 ? x : x - width) / Math.Max(1, width), -0.2, 0.2) * _viewLength);
            ClampView();
        }

        if (Math.Abs(x - _pressX) < ClickPixels)
        {
            return; // a hand's tremble, not a selection: the click (or the double-click) stays one
        }

        WaveBorder.Cursor = Cursors.SizeWE;
        var frame = FrameAt(x);
        _selectionStart = Math.Min(_selectionAnchor, frame);
        _selectionEnd = Math.Max(_selectionAnchor, frame);
        Redraw();
    }

    private void WaveCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!WaveBorder.IsMouseCaptured)
        {
            return;
        }
        WaveBorder.ReleaseMouseCapture();
        WaveBorder.Cursor = Cursors.Arrow;
        if (_selectionEnd - _selectionStart < 2)
        {
            // A click: the place Space plays from, marked by an orange line.
            var at = _selectionAnchor;
            ClearSelection();
            if (_clip is not null && at >= 0)
            {
                _startFrame = at;
                Info($"Space plays from {Seconds(at)} (bar {BarBeat(at)}); a double-click plays from where it is.");
            }
        }
        else if (_clip is not null)
        {
            Info($"Selected {Seconds(_selectionStart)} to {Seconds(_selectionEnd)} (bar {BarBeat(_selectionStart)} to {BarBeat(_selectionEnd)}, {(_selectionEnd - _selectionStart) / (double)_clip.SampleRate:F3} s). Space plays it.");
        }
        Redraw();
    }

    // The wheel zooms in and out around the mouse; with Shift (or tilted) it moves along the clip.
    private void WaveCanvas_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        var width = WaveCanvas.ActualWidth;
        if (_clip is null || width <= 0)
        {
            return;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            _viewStart -= Math.Sign(e.Delta) * Math.Max(1, _viewLength / 8);
        }
        else
        {
            var place = Math.Clamp(e.GetPosition(WaveCanvas).X / width, 0, 1);
            var anchor = _viewStart + place * _viewLength;
            var length = e.Delta > 0 ? _viewLength / 1.5 : _viewLength * 1.5;
            _viewLength = (int)Math.Clamp(length, Math.Min(_clip.FrameCount, Math.Max(64, width)), _clip.FrameCount);
            _viewStart = (int)(anchor - place * _viewLength);
        }

        ClampView();
        Redraw();
    }

    private void ViewScrollBar_Scroll(object sender, System.Windows.Controls.Primitives.ScrollEventArgs e)
    {
        _viewStart = (int)ViewScrollBar.Value;
        ClampView();
        Redraw();
    }

    private void WaveCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => Redraw();

    private void ShowWholeClip()
    {
        _viewStart = 0;
        _viewLength = _clip?.FrameCount ?? 0;
    }

    // The view kept inside the clip (shorter now after an edit or an undo), and the scroll bar set to it.
    private void ClampView()
    {
        var frames = _clip?.FrameCount ?? 0;
        _viewLength = Math.Clamp(_viewLength, Math.Min(frames, 1), Math.Max(frames, 1));
        _viewStart = Math.Clamp(_viewStart, 0, Math.Max(0, frames - _viewLength));
        ViewScrollBar.Minimum = 0;
        ViewScrollBar.Maximum = Math.Max(0, frames - _viewLength);
        ViewScrollBar.ViewportSize = _viewLength;
        ViewScrollBar.LargeChange = Math.Max(1, _viewLength * 0.9);
        ViewScrollBar.SmallChange = Math.Max(1, _viewLength / 10.0);
        ViewScrollBar.Value = _viewStart;
        ViewScrollBar.IsEnabled = _viewLength < frames;
    }

    private string Seconds(int frame) => $"{frame / (double)(_clip?.SampleRate ?? 1):F3} s";

    private int FrameAt(double x)
    {
        var frames = _clip?.FrameCount ?? 0;
        var width = WaveCanvas.ActualWidth;
        if (width <= 0)
        {
            return 0;
        }

        // A few pixels from an edge are the edge itself, so a selection can start right at the clip's start.
        const double Snap = 4;
        var place = x <= Snap ? 0 : x >= width - Snap ? 1 : x / width;
        var frame = _viewStart + place * _viewLength;
        if (SnapCheckBox.IsChecked == true && place is > 0 and < 1)
        {
            var line = GridSteps(width) * FramesPerStep;
            frame = Math.Round(frame / line) * line;
        }

        return Math.Clamp((int)Math.Round(frame), 0, frames);
    }

    // ---- The song's grid, from the clip's start: bars, beats and, zoomed in enough, sixteenths ----

    private static readonly Brush BarLineBrush = Frozen(Color.FromArgb(120, 210, 211, 213));
    private static readonly Brush BeatLineBrush = Frozen(Color.FromArgb(55, 210, 211, 213));
    private static readonly Brush StepLineBrush = Frozen(Color.FromArgb(22, 210, 211, 213));
    private static readonly Brush BarNumberBrush = Frozen(Color.FromRgb(150, 160, 172));
    private static readonly Brush StartLineBrush = Frozen(Color.FromRgb(242, 162, 58));

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private double Tempo => _viewModel is { Tempo: > 0 } viewModel ? viewModel.Tempo : _songTempo;

    private Models.Meter Meter => _viewModel?.Meter ?? Models.Meter.Common;

    // A sixteenth of the song, in the clip's frames (the tempo counts quarter notes).
    private double FramesPerStep => (_clip?.SampleRate ?? 44100) * 60.0 / Math.Max(1, Tempo) / 4.0;

    // The grid's finest lines at this zoom, in sixteenths: each one, a beat, a bar or several, never closer than 6 pixels.
    private int GridSteps(double width)
    {
        var pixelsPerStep = FramesPerStep / Math.Max(1e-9, _viewLength / width);
        var meter = Meter;
        if (pixelsPerStep >= 6)
        {
            return 1;
        }

        if (pixelsPerStep * meter.StepsPerBeat >= 6)
        {
            return meter.StepsPerBeat;
        }

        var bars = 1;
        while (pixelsPerStep * meter.StepsPerBar * bars < 6)
        {
            bars *= 2;
        }

        return meter.StepsPerBar * bars;
    }

    private void DrawGrid(double width)
    {
        var meter = Meter;
        var framesPerStep = FramesPerStep;
        var every = GridSteps(width);
        var pixelsPerBar = framesPerStep * meter.StepsPerBar / (_viewLength / width);
        var numberEvery = 1; // bars between numbers, so they never run into each other
        while (pixelsPerBar * numberEvery < 40)
        {
            numberEvery *= 2;
        }

        var first = (long)Math.Ceiling(_viewStart / framesPerStep / every) * every;
        var last = (_viewStart + _viewLength) / framesPerStep;
        for (var step = first; step <= last; step += every)
        {
            var x = (step * framesPerStep - _viewStart) / _viewLength * width;
            var isBar = step % meter.StepsPerBar == 0;
            WaveCanvas.Children.Add(new Line
            {
                X1 = x,
                X2 = x,
                Y1 = 0,
                Y2 = CanvasHeight,
                Stroke = isBar ? BarLineBrush : step % meter.StepsPerBeat == 0 ? BeatLineBrush : StepLineBrush,
                StrokeThickness = 1,
                IsHitTestVisible = false,
            });

            var bar = step / meter.StepsPerBar;
            if (isBar && bar % numberEvery == 0)
            {
                var number = new TextBlock
                {
                    Text = (bar + 1).ToString(CultureInfo.InvariantCulture),
                    FontSize = 10,
                    Foreground = BarNumberBrush,
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(number, x + 3);
                Canvas.SetTop(number, 2);
                WaveCanvas.Children.Add(number);
            }
        }
    }

    // Where a frame is in the song's bars, as the transport shows it: bar.beat.sixteenth.
    private string BarBeat(int frame)
    {
        var meter = Meter;
        var step = (int)Math.Floor(frame / FramesPerStep);
        return $"{step / meter.StepsPerBar + 1}.{step % meter.StepsPerBar / meter.StepsPerBeat + 1}.{step % meter.StepsPerBeat + 1}";
    }

    private bool TryGetRange(out int start, out int end)
    {
        start = _selectionStart;
        end = _selectionEnd;
        return _clip is not null && _selectionStart >= 0 && _selectionEnd > _selectionStart;
    }

    private (int Start, int End) RangeOrWhole()
    {
        return TryGetRange(out var start, out var end) ? (start, end) : (0, _clip?.FrameCount ?? 0);
    }

    private void ClearSelection()
    {
        _selectionAnchor = -1;
        _selectionStart = -1;
        _selectionEnd = -1;
    }

    private void Apply(float[] samples, string operation)
    {
        _undo.Push(_clip!);
        var wasWhole = _viewStart == 0 && _viewLength >= _clip!.FrameCount;
        _clip = new AudioClip(samples, _clip!.Channels, _clip.SampleRate);
        if (wasWhole)
        {
            ShowWholeClip();
        }
        ClampView();
        Redraw();
        Info($"{operation}. Duration now {_clip.Duration.TotalSeconds:F2} s.");
    }

    // A preview playing through Sonator's output (stopped by _previewEnd, or by Space when it loops).
    private bool _isPreviewing;

    private bool IsPlaying => _isPreviewing || _output is not null;

    // The keys of the transport, for the clip: Space plays the selection, or the whole clip without one, and
    // stops it; the full stop plays the whole clip from its start; Escape stops. Not while a box is typed in.
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox || Keyboard.Modifiers != ModifierKeys.None)
        {
            return;
        }

        if (_clip is null)
        {
            if (e.Key == Key.Space)
            {
                Info("No clip to play: open an audio file, or a track's clip from the timeline (double-click it).");
                e.Handled = true;
            }
            return;
        }

        switch (e.Key)
        {
            case Key.Space when IsPlaying:
            case Key.Escape:
                StopPlayback();
                Info("Stopped.");
                break;
            case Key.Space when TryGetRange(out _, out _):
                Play(wholeClip: false);
                break;
            case Key.Space:
                var from = Math.Clamp(_startFrame, 0, _clip.FrameCount);
                Play(from, _clip.FrameCount, from > 0 ? $"Playing from {Seconds(from)} (bar {BarBeat(from)})." : "Playing the whole clip.");
                break;
            case Key.OemPeriod or Key.Decimal:
                Play(wholeClip: true);
                break;
            case Key.L:
                LoopCheckBox.IsChecked = LoopCheckBox.IsChecked != true;
                LoopCheckBox_Click(LoopCheckBox, e);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void Play(bool wholeClip)
    {
        if (_clip is null)
        {
            return;
        }

        var (start, end) = wholeClip ? (0, _clip.FrameCount) : RangeOrWhole();
        Play(start, end, wholeClip ? "Playing the whole clip." : "Playing the selection.");
    }

    private void Play(int start, int end, string message)
    {
        if (_clip is null || end <= start)
        {
            return;
        }

        StopPlayback();
        var loops = LoopCheckBox.IsChecked == true;
        if (loops)
        {
            message = message.TrimEnd('.') + ", in a loop: Space stops it.";
        }

        // Through Sonator's own output (ASIO too), as the song plays; Windows' default output only without it.
        if (_viewModel is not null
            && _viewModel.StartPreview(AudioEdits.Copy(_clip.Samples, _clip.Channels, start, end), _clip.Channels, _clip.SampleRate, loops))
        {
            _isPreviewing = true;
            if (!loops)
            {
                StopAfter((end - start) / (double)_clip.SampleRate + 0.2);
            }

            StartPlayhead(start, end, loops);
            Info(message);
            return;
        }

        _output = new WaveOutEvent();
        _output.Init(AudioClipLoader.ToPlayable(_clip, start, end, loops).ToWaveProvider());
        _output.PlaybackStopped += (_, _) => Dispatcher.InvokeAsync(StopPlayback);
        _output.Play();
        StartPlayhead(start, end, loops);
        Info(message);
    }

    private void StopAfter(double seconds)
    {
        _previewEnd?.Stop();
        _previewEnd = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Max(0.01, seconds)) };
        _previewEnd.Tick += (_, _) => StopPlayback();
        _previewEnd.Start();
    }

    // LOOP turned on while the clip plays: the part playing starts again, looped. Turned off: it stops at the
    // end of the time round it is in.
    private void LoopCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (!IsPlaying || _clip is null)
        {
            return;
        }

        if (LoopCheckBox.IsChecked == true)
        {
            Play(_playFrom, _playTo, "Playing");
        }
        else if (_playLoops)
        {
            _playLoops = false;
            StopAfter((_playTo - PlayheadFrame) / (double)_clip.SampleRate);
            Info("The loop stops at the end of this time round.");
        }
    }

    private void StartPlayhead(int from, int to, bool loops)
    {
        _playFrom = from;
        _playTo = to;
        _playLoops = loops;
        _playClock.Restart();
        CompositionTarget.Rendering -= MovePlayhead;
        CompositionTarget.Rendering += MovePlayhead;
    }

    private void StopPlayhead()
    {
        CompositionTarget.Rendering -= MovePlayhead;
        _playClock.Reset();
        WaveCanvas.Children.Remove(_playhead);
    }

    private int PlayheadFrame
    {
        get
        {
            var played = (long)(_playClock.Elapsed.TotalSeconds * (_clip?.SampleRate ?? 0));
            if (_playLoops && _playTo > _playFrom)
            {
                played %= _playTo - _playFrom; // round again from the start of the part
            }

            return (int)Math.Min(_playTo, _playFrom + played);
        }
    }

    // Each frame drawn: the playhead moved on; zoomed in, the view turns the page when it gets to the end.
    private void MovePlayhead(object? sender, EventArgs e)
    {
        if (_clip is null || !_playClock.IsRunning)
        {
            return;
        }

        var frame = PlayheadFrame;
        if (_viewLength < _clip.FrameCount && !WaveBorder.IsMouseCaptured
            && (frame < _viewStart || frame >= _viewStart + _viewLength))
        {
            _viewStart = frame - _viewLength / 20;
            ClampView();
            Redraw(); // which places the playhead
            return;
        }

        PlacePlayhead(frame);
    }

    private void PlacePlayhead(int frame)
    {
        var width = WaveCanvas.ActualWidth;
        var x = _viewLength > 0 ? (frame - _viewStart) / (double)_viewLength * width : -1;
        if (x < 0 || x > width)
        {
            WaveCanvas.Children.Remove(_playhead);
            return;
        }

        _playhead.X1 = x;
        _playhead.X2 = x;
        if (!WaveCanvas.Children.Contains(_playhead))
        {
            WaveCanvas.Children.Add(_playhead);
        }
    }

    private void StopPlayback()
    {
        StopPlayhead();
        _previewEnd?.Stop();
        _previewEnd = null;
        if (_isPreviewing)
        {
            _isPreviewing = false;
            _viewModel?.StopPreview();
        }

        if (_output is null)
        {
            return;
        }
        _output.Stop();
        _output.Dispose();
        _output = null;
    }

    private void Redraw()
    {
        WaveCanvas.Children.Clear();
        if (_clip is null)
        {
            return;
        }

        var width = (int)WaveCanvas.ActualWidth;
        if (width <= 0 || _viewLength <= 0)
        {
            return;
        }

        DrawGrid(width);
        var middle = CanvasHeight / 2;
        var framesPerColumn = _viewLength / (double)width;
        var channels = _clip.Channels;

        for (var column = 0; column < width; column++)
        {
            var from = _viewStart + (int)(column * framesPerColumn);
            var to = Math.Min(_clip.FrameCount, Math.Max(from + 1, _viewStart + (int)((column + 1) * framesPerColumn)));
            var min = 0f;
            var max = 0f;
            for (var frame = from; frame < to; frame++)
            {
                for (var channel = 0; channel < channels; channel++)
                {
                    var sample = _clip.Samples[frame * channels + channel];
                    min = Math.Min(min, sample);
                    max = Math.Max(max, sample);
                }
            }

            WaveCanvas.Children.Add(new Line
            {
                X1 = column + 0.5,
                X2 = column + 0.5,
                Y1 = middle - max * middle,
                Y2 = middle - min * middle,
                Stroke = new SolidColorBrush(Color.FromRgb(79, 163, 217)),
                StrokeThickness = 1,
            });
        }

        if (_selectionStart >= 0 && _selectionEnd > _selectionStart)
        {
            var left = Math.Clamp((_selectionStart - _viewStart) / (double)_viewLength * width, 0, width);
            var right = Math.Clamp((_selectionEnd - _viewStart) / (double)_viewLength * width, 0, width);
            var selection = new Rectangle
            {
                Width = Math.Max(0, right - left),
                Height = CanvasHeight,
                Fill = new SolidColorBrush(Color.FromArgb(70, 242, 193, 78)),
            };
            Canvas.SetLeft(selection, left);
            Canvas.SetTop(selection, 0);
            WaveCanvas.Children.Add(selection);
        }

        if (_startFrame >= 0 && _startFrame <= _clip.FrameCount)
        {
            var x = (_startFrame - _viewStart) / (double)_viewLength * width;
            if (x >= 0 && x <= width)
            {
                WaveCanvas.Children.Add(new Line
                {
                    X1 = x,
                    X2 = x,
                    Y1 = 0,
                    Y2 = CanvasHeight,
                    Stroke = StartLineBrush,
                    StrokeThickness = 1.5,
                    IsHitTestVisible = false,
                });
            }
        }

        if (_playClock.IsRunning)
        {
            PlacePlayhead(PlayheadFrame);
        }
    }

    private void Info(string text)
    {
        InfoText.Text = text;
    }
}
