using System.Diagnostics;
using System.Windows.Threading;
using Sonator.ViewModels;

namespace Sonator.Services;

internal sealed class SequencerEngine : IDisposable
{
    private const int DefaultStepsPerPattern = 16;
    private readonly object _sync = new();
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private WindowsMidiOutput? _midiOutput;
    private readonly List<ActiveNote> _activeNotes = new();
    private readonly List<PatternViewModel> _songPatterns = new();
    private PatternViewModel? _pattern;
    private int _currentStep;
    private int _currentPatternIndex;
    private int _loopStartStep;
    private int _loopEndStep = DefaultStepsPerPattern - 1;
    private double _tempoBpm = 116.5674;
    private CancellationTokenSource? _playbackCts;
    private Task? _playbackTask;
    private uint? _midiOutputDeviceId;

    public SequencerEngine()
    {
        TrySetMidiOutputDevice(null);
    }

    private bool IsTrackAudible(PatternTrackViewModel track)
    {
        if (track.IsMuted)
        {
            return false;
        }

        return _pattern?.Tracks.Any(candidate => candidate.IsSolo) != true || track.IsSolo;
    }

    public double TempoBpm
    {
        get => _tempoBpm;
        set => _tempoBpm = !double.IsFinite(value) || value <= 0 ? 1 : value;
    }

    public event EventHandler<int>? StepChanged;

    public uint? CurrentMidiOutputDeviceId => _midiOutputDeviceId;

    public void LoadPattern(PatternViewModel? pattern)
    {
        Stop();
        lock (_sync)
        {
            _pattern = pattern;
            _songPatterns.Clear();
            _currentPatternIndex = 0;
            _currentStep = _loopStartStep;
            _activeNotes.Clear();
        }

        ClearHighlights();
    }

    public void LoadSong(IReadOnlyList<PatternViewModel> patterns)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        if (patterns.Count == 0)
        {
            throw new ArgumentException("A song must contain at least one pattern.", nameof(patterns));
        }

        Stop();
        lock (_sync)
        {
            _songPatterns.Clear();
            _songPatterns.AddRange(patterns);
            _currentPatternIndex = 0;
            _pattern = _songPatterns[0];
            _currentStep = _loopStartStep;
            _activeNotes.Clear();
        }

        ClearHighlights();
    }

    public void SetLoopRange(int startStep, int endStep)
    {
        lock (_sync)
        {
            var lastStep = GetPatternStepCount() - 1;
            var start = Math.Clamp(startStep, 0, lastStep);
            var end = Math.Clamp(endStep, 0, lastStep);
            if (end < start)
            {
                end = start;
            }

            _loopStartStep = start;
            _loopEndStep = end;
            _currentStep = Math.Clamp(_currentStep, _loopStartStep, _loopEndStep);
        }
    }

    public void SetPlaybackPosition(int stepIndex)
    {
        PatternViewModel? pattern;
        int position;
        lock (_sync)
        {
            position = Math.Clamp(stepIndex, 0, GetPatternStepCount() - 1);
            _currentStep = position;
            pattern = _pattern;
        }

        if (pattern is null)
        {
            return;
        }

        _dispatcher.InvokeAsync(() =>
        {
            HighlightStep(pattern, position);
            StepChanged?.Invoke(this, position);
        });
    }

    public void Start()
    {
        lock (_sync)
        {
            if (_pattern is null)
            {
                throw new InvalidOperationException("No pattern loaded.");
            }

            if (_playbackTask is not null && !_playbackTask.IsCompleted)
            {
                return;
            }

            ApplyProgramChanges(_pattern);
            _playbackCts = new CancellationTokenSource();
            _playbackTask = RunPlaybackLoopAsync(_playbackCts.Token);
        }
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        lock (_sync)
        {
            cts = _playbackCts;
            _playbackCts = null;
        }

        if (cts is null)
        {
            return;
        }

        cts.Cancel();
        AllNotesOff();
    }

    public void UpdateTempo(double tempoBpm)
    {
        TempoBpm = tempoBpm;
    }

    public bool TrySetMidiOutputDevice(uint? deviceId)
    {
        WindowsMidiOutput? newOutput = null;
        try
        {
            newOutput = new WindowsMidiOutput(deviceId);
        }
        catch
        {
            newOutput?.Dispose();
            return false;
        }

        lock (_sync)
        {
            _activeNotes.Clear();
            _midiOutput?.Dispose();
            _midiOutput = newOutput;
            _midiOutputDeviceId = deviceId;
        }

        return true;
    }

    public void Dispose()
    {
        Stop();
        lock (_sync)
        {
            _midiOutput?.Dispose();
            _midiOutput = null;
        }
    }

    private async Task RunPlaybackLoopAsync(CancellationToken token)
    {
        try
        {
            var stepDuration = GetInterval(TempoBpm);
            var nextStepAt = Stopwatch.GetTimestamp();

            while (!token.IsCancellationRequested)
            {
                var currentStep = GetCurrentStep();
                ProcessStep(currentStep);

                nextStepAt += (long)(stepDuration.TotalSeconds * Stopwatch.Frequency);
                await WaitUntilAsync(nextStepAt, token).ConfigureAwait(false);

                lock (_sync)
                {
                    _currentStep = AdvanceStep(_currentStep);
                    stepDuration = GetInterval(TempoBpm);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Graceful exit on stop/cancellation.
        }
    }

    private void ProcessStep(int stepIndex)
    {
        var pattern = GetCurrentPattern();
        if (pattern is null)
        {
            return;
        }

        TickActiveNotes();
        PlayStepNotes(pattern, stepIndex);

        _dispatcher.InvokeAsync(() =>
        {
            HighlightStep(pattern, stepIndex);
            StepChanged?.Invoke(this, stepIndex);
        });
    }

    private int GetCurrentStep()
    {
        lock (_sync)
        {
            return _currentStep;
        }
    }

    private PatternViewModel? GetCurrentPattern()
    {
        lock (_sync)
        {
            return _pattern;
        }
    }

    private int GetPatternStepCount()
    {
        return Math.Max(
            1,
            _pattern?.Tracks.Select(track => track.Steps.Count).DefaultIfEmpty(DefaultStepsPerPattern).Max()
            ?? DefaultStepsPerPattern);
    }

    private int AdvanceStep(int currentStep)
    {
        lock (_sync)
        {
            if (currentStep >= _loopEndStep)
            {
                if (_songPatterns.Count > 0)
                {
                    _currentPatternIndex = (_currentPatternIndex + 1) % _songPatterns.Count;
                    _pattern = _songPatterns[_currentPatternIndex];
                }

                return _loopStartStep;
            }

            return currentStep + 1;
        }
    }

    private void TickActiveNotes()
    {
        lock (_sync)
        {
            for (var i = _activeNotes.Count - 1; i >= 0; i--)
            {
                var activeNote = _activeNotes[i];
                activeNote.RemainingSteps -= 1;
                if (!IsTrackAudible(activeNote.Track) || activeNote.RemainingSteps <= 0)
                {
                    _midiOutput?.NoteOff(activeNote.Channel, activeNote.NoteNumber);
                    _activeNotes.RemoveAt(i);
                }
            }
        }
    }

    private void ApplyProgramChanges(PatternViewModel? pattern)
    {
        if (pattern is null)
        {
            return;
        }

        foreach (var track in pattern.Tracks)
        {
            if (!track.SupportsMidiPlayback)
            {
                continue;
            }

            _midiOutput?.ProgramChange(track.Channel - 1, track.ProgramNumber);
        }
    }

    private void PlayStepNotes(PatternViewModel pattern, int stepIndex)
    {
        lock (_sync)
        {
            foreach (var track in pattern.Tracks)
            {
                if (!track.SupportsMidiPlayback || !IsTrackAudible(track) || stepIndex >= track.Steps.Count)
                {
                    continue;
                }

                if (track.MidiNoteEvents.Count > 0)
                {
                    foreach (var noteEvent in track.MidiNoteEvents.Where(note => note.StepIndex == stepIndex))
                    {
                        _midiOutput?.NoteOn(track.Channel - 1, noteEvent.NoteNumber, noteEvent.Velocity);
                        _activeNotes.Add(new ActiveNote(
                            track,
                            track.Channel - 1,
                            noteEvent.NoteNumber,
                            noteEvent.DurationSteps));
                    }

                    continue;
                }

                var step = track.Steps[stepIndex];
                if (!step.IsActive)
                {
                    continue;
                }

                var noteNumber = step.MidiNoteNumber ?? track.NoteNumber;
                _midiOutput?.NoteOn(track.Channel - 1, noteNumber, step.Velocity);
                _activeNotes.Add(new ActiveNote(track, track.Channel - 1, noteNumber, step.DurationSteps));
            }
        }
    }

    private void HighlightStep(PatternViewModel pattern, int stepIndex)
    {
        foreach (var track in pattern.Tracks)
        {
            foreach (var step in track.Steps)
            {
                step.IsCurrent = step.Index == stepIndex;
            }
        }
    }

    private void ClearHighlights()
    {
        var patterns = _songPatterns.Count > 0
            ? _songPatterns
            : _pattern is null ? Enumerable.Empty<PatternViewModel>() : new[] { _pattern };
        foreach (var pattern in patterns)
        {
            foreach (var track in pattern.Tracks)
            {
                foreach (var step in track.Steps)
                {
                    step.IsCurrent = false;
                }
            }
        }
    }

    private void AllNotesOff()
    {
        lock (_sync)
        {
            var patterns = _songPatterns.Count > 0
                ? _songPatterns
                : _pattern is null ? Enumerable.Empty<PatternViewModel>() : new[] { _pattern };
            foreach (var pattern in patterns)
            {
                foreach (var track in pattern.Tracks)
                {
                    if (!track.SupportsMidiPlayback)
                    {
                        continue;
                    }

                    _midiOutput?.AllNotesOff(track.Channel - 1);
                }
            }

            _activeNotes.Clear();
        }
    }

    private static TimeSpan GetInterval(double tempoBpm)
    {
        var sixteenthNoteMs = 60000.0 / Math.Max(1.0, tempoBpm) / 4.0;
        return TimeSpan.FromMilliseconds(sixteenthNoteMs);
    }

    private static async Task WaitUntilAsync(long targetTimestamp, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();

            var remainingTicks = targetTimestamp - Stopwatch.GetTimestamp();
            if (remainingTicks <= 0)
            {
                return;
            }

            var remainingMs = remainingTicks * 1000.0 / Stopwatch.Frequency;
            if (remainingMs > 2)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(remainingMs - 1), token).ConfigureAwait(false);
            }
            else
            {
                Thread.SpinWait(100);
            }
        }
    }

    private sealed class ActiveNote
    {
        public ActiveNote(PatternTrackViewModel track, int channel, int noteNumber, int remainingSteps)
        {
            Track = track;
            Channel = channel;
            NoteNumber = noteNumber;
            RemainingSteps = Math.Max(1, remainingSteps);
        }

        public PatternTrackViewModel Track { get; }

        public int Channel { get; }

        public int NoteNumber { get; }

        public int RemainingSteps { get; set; }
    }
}
