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

    // Metronome: a click on every beat (quarter note), accented on the first beat of each bar, on MIDI channel 10.
    private const int MetronomeChannel = 9;
    private const int MetronomeHighNote = 77;
    private const int MetronomeLowNote = 76;
    private int _metronomeNoteOn = -1;
    private CancellationTokenSource? _countInCts;
    private CancellationTokenSource? _clickLoopCts;

    public bool IsMetronomeOn { get; set; } = true;

    // The step the sequencer is on right now, read by recording. It does not wait for the UI thread.
    private int _liveStep;

    public int LiveStep => Volatile.Read(ref _liveStep);

    // Sounds a click of the given volume (0 to 1) through Sonator's own output, independent of any MIDI output.
    public Action<float>? ClickSound { get; set; }

    // Steps between two clicks: 4 for quarter notes, 1 for sixteenths. Must divide 16.
    public int ClickStepInterval { get; set; } = 4;

    // Offered each note of a track that has a plug-in. Returns true when the plug-in took the note, so the MIDI output is skipped.
    public Func<string?, int, int, int, bool, bool>? PluginNoteRouter { get; set; }
    private readonly List<PatternViewModel> _songPatterns = new();
    private PatternViewModel? _pattern;
    private int _currentStep;
    private int _loopStartStep;
    private int _loopEndStep = DefaultStepsPerPattern - 1;
    private bool _isPatternLoopEnabled;
    private double _tempoBpm = 120;
    private double _masterVolume = 0.75;
    private CancellationTokenSource? _playbackCts;
    private Task? _playbackTask;
    private uint? _midiOutputDeviceId;
    private PatternViewModel? _highlightedPattern;
    private int _highlightedStep = -1;
    private int _playbackGeneration;

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

    public double MasterVolume
    {
        get => _masterVolume;
        set => _masterVolume = !double.IsFinite(value) ? 0.75 : Math.Clamp(value, 0.0, 1.0);
    }

    public event EventHandler<int>? StepChanged;

    public event EventHandler<PatternViewModel>? PatternChanged;

    public event EventHandler? PlaybackCompleted;

    public event EventHandler<Exception>? PlaybackFaulted;

    public uint? CurrentMidiOutputDeviceId => _midiOutputDeviceId;

    public void LoadPattern(PatternViewModel? pattern, bool loopEnabled = false)
    {
        Stop();
        lock (_sync)
        {
            _pattern = pattern;
            _songPatterns.Clear();
            _isPatternLoopEnabled = loopEnabled;
            _currentStep = 0;
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
            _isPatternLoopEnabled = false;
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
            var lastStep = _songPatterns.Count > 0
                ? GetSongStepCount() - 1
                : GetPatternStepCount() - 1;
            position = Math.Clamp(stepIndex, 0, lastStep);
            _currentStep = position;
            pattern = ResolveSongPosition(position).Pattern ?? _pattern;
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
            var playbackGeneration = unchecked(++_playbackGeneration);
            _playbackTask = RunPlaybackLoopAsync(_playbackCts.Token, playbackGeneration);
            _ = _playbackTask.ContinueWith(
                task => _dispatcher.InvokeAsync(() =>
                    PlaybackFaulted?.Invoke(this, task.Exception!.GetBaseException())),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }
    }

    // Plays one click per beat for the given number of bars, then starts the sequence and calls onStarted.
    // Stop cancels the count-in.
    public async Task StartAfterCountInAsync(int bars, Action? onStarted)
    {
        var cts = new CancellationTokenSource();
        lock (_sync)
        {
            _countInCts?.Cancel();
            _countInCts = cts;
        }

        try
        {
            await RunClicksAsync(bars, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_countInCts, cts))
                {
                    _countInCts = null;
                }
            }
            cts.Dispose();
        }

        await _dispatcher.InvokeAsync(() =>
        {
            Start();
            onStarted?.Invoke();
        });
    }

    public void Stop()
    {
        StopClickLoop();
        CancellationTokenSource? cts;
        lock (_sync)
        {
            _countInCts?.Cancel();
            _countInCts = null;
            cts = _playbackCts;
            _playbackCts = null;
            unchecked
            {
                _playbackGeneration++;
            }
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

    public void UpdateMasterVolume(double masterVolume)
    {
        MasterVolume = masterVolume;
    }

    // Stops sending to any MIDI output.
    public void ClearMidiOutputDevice()
    {
        lock (_sync)
        {
            _activeNotes.Clear();
            _midiOutput?.Dispose();
            _midiOutput = null;
            _midiOutputDeviceId = null;
        }
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

    // Sounds a note the player plays on a MIDI keyboard, on the given channel of the MIDI output.
    public void PlayLiveNote(int channel, int noteNumber, int velocity, bool isOn)
    {
        lock (_sync)
        {
            if (isOn)
            {
                _midiOutput?.NoteOn(channel, noteNumber, velocity);
            }
            else
            {
                _midiOutput?.NoteOff(channel, noteNumber);
            }
        }
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

    private async Task RunPlaybackLoopAsync(CancellationToken token, int playbackGeneration)
    {
        using var timerResolution = TimerResolution.Request();
        try
        {
            var stepDuration = GetInterval(TempoBpm);
            var nextStepAt = Stopwatch.GetTimestamp();

            while (!token.IsCancellationRequested)
            {
                var currentStep = GetCurrentStep();
                ProcessStep(currentStep, playbackGeneration);

                nextStepAt += (long)(stepDuration.TotalSeconds * Stopwatch.Frequency);
                await WaitUntilAsync(nextStepAt, token).ConfigureAwait(false);

                lock (_sync)
                {
                    if (!AdvanceStep(_currentStep))
                    {
                        _dispatcher.InvokeAsync(
                            () =>
                            {
                                if (IsCurrentPlaybackGeneration(playbackGeneration))
                                {
                                    PlaybackCompleted?.Invoke(this, EventArgs.Empty);
                                }
                            },
                            DispatcherPriority.Background);
                        break;
                    }

                    stepDuration = GetInterval(TempoBpm);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Graceful exit on stop/cancellation.
        }
    }

    private void ProcessStep(int stepIndex, int playbackGeneration)
    {
        Volatile.Write(ref _liveStep, stepIndex);
        var playbackPosition = ResolvePlaybackPosition(stepIndex);
        var pattern = playbackPosition.Pattern;
        if (pattern is null)
        {
            return;
        }

        if (!ReferenceEquals(GetCurrentPattern(), pattern))
        {
            lock (_sync)
            {
                _pattern = pattern;
            }

            ApplyProgramChanges(pattern);
            _dispatcher.InvokeAsync(
                () =>
                {
                    if (IsCurrentPlaybackGeneration(playbackGeneration))
                    {
                        PatternChanged?.Invoke(this, pattern);
                    }
                },
                DispatcherPriority.Background);
        }

        TickActiveNotes();
        PlayStepNotes(pattern, playbackPosition.LocalStep);
        if (IsMetronomeOn && playbackPosition.LocalStep % ClickStepInterval == 0)
        {
            SendClick(ClickLevelForStep(playbackPosition.LocalStep));
        }

        _dispatcher.InvokeAsync(
            () =>
            {
                if (!IsCurrentPlaybackGeneration(playbackGeneration))
                {
                    return;
                }

                HighlightPlaybackStep(pattern, playbackPosition.LocalStep);
                StepChanged?.Invoke(this, stepIndex);
            },
            DispatcherPriority.Background);
    }

    private bool IsCurrentPlaybackGeneration(int playbackGeneration)
    {
        lock (_sync)
        {
            return _playbackGeneration == playbackGeneration && _playbackCts is not null;
        }
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

    private int GetSongStepCount()
    {
        return Math.Max(1, _songPatterns.Sum(GetPatternStepCount));
    }

    private static int GetPatternStepCount(PatternViewModel pattern)
    {
        return Math.Max(
            1,
            pattern.Tracks.Select(track => track.Steps.Count).DefaultIfEmpty(DefaultStepsPerPattern).Max());
    }

    private (PatternViewModel? Pattern, int LocalStep) ResolvePlaybackPosition(int absoluteStep)
    {
        lock (_sync)
        {
            if (_songPatterns.Count == 0)
            {
                return (_pattern, absoluteStep % GetPatternStepCount());
            }

            return ResolveSongPosition(absoluteStep);
        }
    }

    private (PatternViewModel? Pattern, int LocalStep) ResolveSongPosition(int absoluteStep)
    {
        var remaining = Math.Max(0, absoluteStep);
        foreach (var pattern in _songPatterns)
        {
            var patternLength = GetPatternStepCount(pattern);
            if (remaining < patternLength)
            {
                return (pattern, remaining);
            }

            remaining -= patternLength;
        }

        var lastPattern = _songPatterns.LastOrDefault();
        return (lastPattern, lastPattern is null ? 0 : GetPatternStepCount(lastPattern) - 1);
    }

    private bool AdvanceStep(int currentStep)
    {
        if (_songPatterns.Count > 0 && currentStep + 1 >= GetSongStepCount())
        {
            return false;
        }

        if (_songPatterns.Count == 0 && currentStep + 1 >= GetPatternStepCount())
        {
            if (!_isPatternLoopEnabled)
            {
                return false;
            }

            _currentStep = 0;
            return true;
        }

        _currentStep = currentStep + 1;
        return true;
    }

    private void HighlightPlaybackStep(PatternViewModel activePattern, int activeStep)
    {
        if (_highlightedPattern is not null && _highlightedStep >= 0)
        {
            foreach (var track in _highlightedPattern.Tracks)
            {
                if (_highlightedStep < track.Steps.Count)
                {
                    track.Steps[_highlightedStep].IsCurrent = false;
                }
            }
        }

        foreach (var track in activePattern.Tracks)
        {
            if (activeStep < track.Steps.Count)
            {
                track.Steps[activeStep].IsCurrent = true;
            }
        }

        _highlightedPattern = activePattern;
        _highlightedStep = activeStep;
    }

    public bool IsClickLoopRunning => _clickLoopCts is not null;

    // Starts one bar of clicks after another at the current tempo, until StopClickLoop or Stop.
    // The metronome does not have to be on.
    public void StartClickLoop()
    {
        lock (_sync)
        {
            if (_clickLoopCts is not null)
            {
                return;
            }

            _clickLoopCts = new CancellationTokenSource();
            _ = RunClicksAsync(null, _clickLoopCts.Token);
        }
    }

    public void StopClickLoop()
    {
        lock (_sync)
        {
            _clickLoopCts?.Cancel();
            _clickLoopCts = null;
        }
    }

    // Clicks for a number of bars, or forever when bars is null. Ends with the last click released.
    private async Task RunClicksAsync(int? bars, CancellationToken token)
    {
        using var timerResolution = TimerResolution.Request();
        var clicksPerBar = 16 / ClickStepInterval;
        var clickTicks = (long)(60.0 / TempoBpm / 4.0 * ClickStepInterval * Stopwatch.Frequency);
        var nextClickAt = Stopwatch.GetTimestamp();
        try
        {
            for (var click = 0; bars is null || click < bars.Value * clicksPerBar; click++)
            {
                SendClick(ClickLevelForStep(click * ClickStepInterval));
                nextClickAt += clickTicks;
                await WaitUntilAsync(nextClickAt, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The loop was stopped.
        }
        finally
        {
            lock (_sync)
            {
                if (_metronomeNoteOn >= 0)
                {
                    _midiOutput?.NoteOff(MetronomeChannel, _metronomeNoteOn);
                    _metronomeNoteOn = -1;
                }
            }
        }
    }

    // 0 on the first beat of a bar, 1 on the other quarter notes, 2 on the subdivisions between them.
    private static int ClickLevelForStep(int step)
    {
        return step % 16 == 0 ? 0 : step % 4 == 0 ? 1 : 2;
    }

    private void SendClick(int level)
    {
        ClickSound?.Invoke(level switch
        {
            0 => 0.9f,
            1 => 0.5f,
            _ => 0.3f,
        });

        var velocity = level switch
        {
            0 => 120,
            1 => 60,
            _ => 40,
        };
        lock (_sync)
        {
            if (_metronomeNoteOn >= 0)
            {
                _midiOutput?.NoteOff(MetronomeChannel, _metronomeNoteOn);
            }

            _metronomeNoteOn = level == 0 ? MetronomeHighNote : MetronomeLowNote;
            _midiOutput?.NoteOn(MetronomeChannel, _metronomeNoteOn, velocity);
        }
    }

    // A track with a plug-in plays through it when the router takes the note; otherwise the note goes to the MIDI output.
    private void SendNoteOn(PatternTrackViewModel track, int channel, int noteNumber, int velocity)
    {
        if (PluginNoteRouter?.Invoke(track.PluginFilePath, channel, noteNumber, velocity, true) == true)
        {
            return;
        }

        _midiOutput?.NoteOn(channel, noteNumber, velocity);
    }

    private void SendNoteOff(PatternTrackViewModel track, int channel, int noteNumber)
    {
        if (PluginNoteRouter?.Invoke(track.PluginFilePath, channel, noteNumber, 0, false) == true)
        {
            return;
        }

        _midiOutput?.NoteOff(channel, noteNumber);
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
                    SendNoteOff(activeNote.Track, activeNote.Channel, activeNote.NoteNumber);
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
                        SendNoteOn(
                            track,
                            track.Channel - 1,
                            noteEvent.NoteNumber,
                            ApplyTrackVolume(noteEvent.Velocity, track.Volume * MasterVolume));
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
                SendNoteOn(
                    track,
                    track.Channel - 1,
                    noteNumber,
                    ApplyTrackVolume(step.Velocity, track.Volume * MasterVolume));
                _activeNotes.Add(new ActiveNote(track, track.Channel - 1, noteNumber, step.DurationSteps));
            }

        }
    }

    private static int ApplyTrackVolume(int velocity, double volume)
    {
        return Math.Clamp((int)Math.Round(velocity * volume), 0, 127);
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
            foreach (var activeNote in _activeNotes)
            {
                SendNoteOff(activeNote.Track, activeNote.Channel, activeNote.NoteNumber);
            }

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
            _highlightedPattern = null;
            _highlightedStep = -1;
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
