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
    private int _selectionStart = -1;
    private int _selectionEnd = -1;
    private WaveOutEvent? _output;

    public WaveformEditorWindow(string? path = null)
    {
        InitializeComponent();
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

    private void OpenFile(string path)
    {
        try
        {
            _clip = AudioClipLoader.Load(path);
            _path = path;
            _undo.Clear();
            ClearSelection();
            Title = $"Waveform editor - {Path.GetFileName(path)}";
            Redraw();
            Info($"{Path.GetFileName(path)}: {_clip.Duration.TotalSeconds:F2} s, {_clip.Channels} channel(s), {_clip.SampleRate} Hz.");
        }
        catch (Exception ex)
        {
            Info($"Could not open the file: {ex.Message}");
        }
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
        var (start, end) = RangeOrWhole();
        Apply(AudioEdits.Silence(_clip.Samples, _clip.Channels, start, end), "Delete (silence)");
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

    private void UndoButton_Click(object sender, RoutedEventArgs e)
    {
        if (_undo.Count == 0)
        {
            Info("Nothing to undo.");
            return;
        }
        _clip = _undo.Pop();
        ClearSelection();
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
        if (_clip is null)
        {
            return;
        }
        _selectionAnchor = FrameAt(e.GetPosition(WaveCanvas).X);
        _selectionStart = _selectionAnchor;
        _selectionEnd = _selectionAnchor;
        WaveCanvas.CaptureMouse();
        Redraw();
    }

    private void WaveCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (_clip is null || !WaveCanvas.IsMouseCaptured)
        {
            return;
        }
        var frame = FrameAt(e.GetPosition(WaveCanvas).X);
        _selectionStart = Math.Min(_selectionAnchor, frame);
        _selectionEnd = Math.Max(_selectionAnchor, frame);
        Redraw();
    }

    private void WaveCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!WaveCanvas.IsMouseCaptured)
        {
            return;
        }
        WaveCanvas.ReleaseMouseCapture();
        if (_selectionEnd - _selectionStart < 2)
        {
            ClearSelection();
        }
        Redraw();
    }

    private int FrameAt(double x)
    {
        var frames = _clip?.FrameCount ?? 0;
        var frame = (int)(Math.Clamp(x, 0, WaveCanvas.Width) / WaveCanvas.Width * frames);
        return Math.Clamp(frame, 0, frames);
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
        _clip = new AudioClip(samples, _clip!.Channels, _clip.SampleRate);
        Redraw();
        Info($"{operation}. Duration now {_clip.Duration.TotalSeconds:F2} s.");
    }

    private void Play(bool wholeClip)
    {
        if (_clip is null)
        {
            return;
        }

        var (start, end) = wholeClip ? (0, _clip.FrameCount) : RangeOrWhole();
        StopPlayback();
        _output = new WaveOutEvent();
        _output.Init(AudioClipLoader.ToPlayable(_clip, start, end).ToWaveProvider());
        _output.PlaybackStopped += (_, _) => Dispatcher.InvokeAsync(StopPlayback);
        _output.Play();
        Info(wholeClip ? "Playing the whole clip." : "Playing the selection.");
    }

    private void StopPlayback()
    {
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

        var width = (int)WaveCanvas.Width;
        var middle = CanvasHeight / 2;
        var framesPerColumn = Math.Max(1.0, _clip.FrameCount / (double)width);
        var channels = _clip.Channels;

        for (var column = 0; column < width; column++)
        {
            var from = (int)(column * framesPerColumn);
            var to = Math.Min(_clip.FrameCount, (int)((column + 1) * framesPerColumn));
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
            var left = _selectionStart / (double)_clip.FrameCount * width;
            var right = _selectionEnd / (double)_clip.FrameCount * width;
            var selection = new Rectangle
            {
                Width = right - left,
                Height = CanvasHeight,
                Fill = new SolidColorBrush(Color.FromArgb(70, 242, 193, 78)),
            };
            Canvas.SetLeft(selection, left);
            Canvas.SetTop(selection, 0);
            WaveCanvas.Children.Add(selection);
        }
    }

    private void Info(string text)
    {
        InfoText.Text = text;
    }
}
