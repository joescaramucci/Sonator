using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Sonator.Services;
using Sonator.Services.Voice;
using Sonator.ViewModels;

namespace Sonator;

// Sondyne: a sung take's notes to correct by hand, as with Melodyne. The take is heard as notes; drag them to other
// semitones, bring them to the middle of their semitone (CENTER) and steady their wobble (DRIFT), hear the result
// (PLAY) and put it on the track in place of the take (APPLY), which Undo takes back.
public partial class SondyneWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly PatternTrackViewModel _track;
    private readonly DispatcherTimer _playhead = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private SondyneTake? _take;
    private bool _updatingSliders;
    private bool _playing;
    private DateTime _playStarted;
    private double _playFrom;

    public SondyneWindow(MainViewModel viewModel, PatternTrackViewModel track)
    {
        InitializeComponent();
        WindowFrame.UseDarkFrame(this);
        _viewModel = viewModel;
        _track = track;
        TakeName.Text = track.Name;
        Title = $"Sondyne — {track.Name}";
        View.Edited += () => Status.Text = "Changed: PLAY to hear it, APPLY to put it on the track.";
        View.SelectionChanged += ShowSelection;
        _playhead.Tick += (_, _) => MovePlayhead();
        Loaded += async (_, _) => await OpenTakeAsync();
        Closed += (_, _) => StopPlaying();
    }

    public PatternTrackViewModel Track => _track;

    // Hears the track's take as notes.
    private async Task OpenTakeAsync()
    {
        if (_track.AudioClipPath is not { } path)
        {
            Status.Text = "This track has no take.";
            return;
        }

        Status.Text = "Hearing the notes...";
        try
        {
            _take = await Task.Run(() => Sondyne.Analyse(AudioClipLoader.Load(path)));
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException
                                       or System.Runtime.InteropServices.COMException)
        {
            Status.Text = $"The take could not be read: {ex.Message}";
            return;
        }

        View.Take = _take;
        ShowSelection();
        Status.Text = _take.Notes.Count == 0
            ? "No notes heard in this take: Sondyne works on a single voice or instrument, recorded on its own."
            : $"{_take.Notes.Count} notes. Click a note (Ctrl adds, Ctrl+A all), drag it up or down (Alt for cents); double-click centres it.";
    }

    // The notes CENTER and DRIFT act on: those selected, or all.
    private IReadOnlyCollection<SondyneNote> Targets =>
        View.Selected.Count > 0 ? View.Selected : (IReadOnlyCollection<SondyneNote>?)_take?.Notes.ToList() ?? Array.Empty<SondyneNote>();

    private void ShowSelection()
    {
        _updatingSliders = true;
        var first = Targets.FirstOrDefault();
        CenterSlider.Value = (first?.Center ?? 0) * 100;
        DriftSlider.Value = (first?.Drift ?? 0) * 100;
        CenterText.Text = $"{CenterSlider.Value:0}%";
        DriftText.Text = $"{DriftSlider.Value:0}%";
        _updatingSliders = false;
    }

    private void Correction_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (CenterText is null || DriftText is null)
        {
            return;
        }

        CenterText.Text = $"{CenterSlider.Value:0}%";
        DriftText.Text = $"{DriftSlider.Value:0}%";
        if (_updatingSliders || _take is null)
        {
            return;
        }

        foreach (var note in Targets)
        {
            note.Center = CenterSlider.Value / 100;
            note.Drift = DriftSlider.Value / 100;
        }

        View.Refresh();
        Status.Text = "Changed: PLAY to hear it, APPLY to put it on the track.";
    }

    // Every note to the middle of its semitone, its wobble halved: a natural correction.
    private void CorrectAll_Click(object sender, RoutedEventArgs e)
    {
        if (_take is null)
        {
            return;
        }

        foreach (var note in _take.Notes)
        {
            note.Center = 1;
            note.Drift = 0.5;
        }

        View.Refresh();
        ShowSelection();
        Status.Text = "Every note centred, its wobble halved: PLAY to hear it, APPLY to put it on the track.";
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (_take is null)
        {
            return;
        }

        foreach (var note in Targets)
        {
            note.Shift = 0;
            note.Center = 0;
            note.Drift = 0;
        }

        View.Refresh();
        ShowSelection();
    }

    private void Zoom_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (View is not null)
        {
            View.PixelsPerSecond = e.NewValue;
            View.Refresh();
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space)
        {
            Play_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control)
        {
            View.SelectAll();
            e.Handled = true;
        }
    }

    // Plays the take as changed, from a little before the first note selected (from its start with none).
    private async void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_playing)
        {
            StopPlaying();
            return;
        }

        if (_take is not { } take)
        {
            return;
        }

        Status.Text = "Singing it again...";
        var sung = await Task.Run(() => Sondyne.Render(take));
        _playFrom = View.Selected.Count > 0 ? Math.Max(0, View.Selected.Min(note => note.StartFrame) * take.FrameSeconds - 0.4) : 0;
        var startFrame = Math.Min(sung.FrameCount - 1, (int)(_playFrom * sung.SampleRate));
        var samples = sung.Samples.AsSpan(Math.Max(0, startFrame) * sung.Channels).ToArray();
        if (!_viewModel.StartPreview(samples, sung.Channels, sung.SampleRate))
        {
            Status.Text = "It could not be played: the audio output is not available.";
            return;
        }

        _playing = true;
        _playStarted = DateTime.UtcNow;
        PlayButton.Content = "STOP";
        _playhead.Start();
        Status.Text = "Playing (Space stops)";
    }

    private void MovePlayhead()
    {
        var seconds = _playFrom + (DateTime.UtcNow - _playStarted).TotalSeconds;
        if (_take is null || seconds > _take.Clip.Duration.TotalSeconds)
        {
            StopPlaying();
            return;
        }

        View.Playhead = seconds;
        View.InvalidateVisual();
    }

    private void StopPlaying()
    {
        _playhead.Stop();
        if (_playing)
        {
            _viewModel.StopPreview();
        }

        _playing = false;
        PlayButton.Content = "PLAY";
        View.Playhead = double.NaN;
        View.InvalidateVisual();
    }

    // Puts the take as changed on the track in place of the old one, which Undo brings back, and opens it here.
    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_take is not { } take || !take.Notes.Any(note => note.IsEdited))
        {
            Status.Text = "Nothing changed yet.";
            return;
        }

        StopPlaying();
        Status.Text = "Singing it again...";
        var sung = await Task.Run(() => Sondyne.Render(take));
        var path = AudioClipLoader.SaveToLibrary(sung);
        _viewModel.ReplaceAudioClip(_track, path);
        await OpenTakeAsync();
        Status.Text = "Applied: the track plays the corrected take. Undo in the main window brings the old one back.";
    }
}
