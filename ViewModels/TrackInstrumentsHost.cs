using Sonator.Services;
using Sonator.Services.Effects;
using Sonator.Services.Vst3;

namespace Sonator.ViewModels;

// Keeps one running plug-in per track, so every track plays through its own instrument and they all sound
// together. A track's plug-in starts the first time it is needed and runs through the track's effects; it
// stops when the track goes or takes another plug-in.
internal sealed class TrackInstrumentsHost : IDisposable
{
    private readonly IVst3PluginHost _host;
    private readonly Func<PatternTrackViewModel, NativeEffectChain?> _effectsFor;
    private readonly Action<PatternTrackViewModel>? _followMainOutput;
    private readonly Dictionary<PatternTrackViewModel, Vst3ModuleHandle> _instruments = new();
    private readonly object _sync = new();

    public TrackInstrumentsHost(
        IVst3PluginHost host,
        Func<PatternTrackViewModel, NativeEffectChain?> effectsFor,
        Action<PatternTrackViewModel>? followMainOutput = null)
    {
        _host = host;
        _effectsFor = effectsFor;
        _followMainOutput = followMainOutput;
    }

    // The track a track's plug-in runs for: its channel's (see MainViewModel.ChannelOf). Any thread.
    public Func<PatternTrackViewModel, PatternTrackViewModel>? ChannelOf { get; init; }

    private PatternTrackViewModel Channel(PatternTrackViewModel track) => ChannelOf?.Invoke(track) ?? track;

    // The track's plug-in when it is running, from any thread. Null when it has not started yet.
    public Vst3ModuleHandle? RunningFor(PatternTrackViewModel track)
    {
        track = Channel(track);
        lock (_sync)
        {
            return _instruments.TryGetValue(track, out var handle) && IsFor(track, handle) ? handle : null;
        }
    }

    // Starts the track's plug-in if it is not running yet. Null, with the reason, when it cannot start.
    public Vst3ModuleHandle? Ensure(PatternTrackViewModel track, AudioOutputSelection selection, out string? error)
    {
        error = null;
        track = Channel(track);
        if (string.IsNullOrEmpty(track.PluginFilePath))
        {
            return null;
        }

        if (RunningFor(track) is { } running)
        {
            return running;
        }

        Release(track); // a plug-in the track no longer uses

        var handle = _host.TryLoadModule(track.PluginFilePath, out var loadError);
        if (handle is null)
        {
            error = $"could not load the plug-in: {loadError}";
            return null;
        }

        // The sound saved with the project comes back before the plug-in plays.
        if (track.PluginState is { } state)
        {
            _host.TryLoadState(handle, 0, state);
        }
        if (track.PluginEditorState is { } editorState)
        {
            _host.TryLoadState(handle, 1, editorState);
        }

        _effectsFor(track)?.AttachTo(handle);
        AttachKitPieces(track, handle);
        string? audioError = null;
        if (!_host.HasAudioProcessor(handle) || !_host.TryStartAudio(handle, selection, out audioError))
        {
            _host.UnloadModule(handle);
            error = $"plug-in audio did not start: {audioError ?? "the plug-in has no audio processor"}";
            return null;
        }

        lock (_sync)
        {
            _instruments[track] = handle;
        }

        RememberSound(handle);
        return handle;
    }

    // Each of the plug-in's other outputs plays apart, through the track's kit piece for it, made for an output
    // that has none yet; the main output gets a piece too, first, for its own fader (it plays through the track's
    // chain). Pieces of outputs the plug-in does not have (it was another plug-in) go, all of them for a plug-in
    // with one output.
    private void AttachKitPieces(PatternTrackViewModel track, Vst3ModuleHandle handle)
    {
        var names = _host.OutputNames(handle);
        foreach (var gone in track.KitPieces.Where(piece => piece.KitOutput < 0 || piece.KitOutput >= names.Count || names.Count < 2).ToList())
        {
            track.KitPieces.Remove(gone);
        }

        for (var output = 0; output < names.Count && names.Count > 1; output++)
        {
            var piece = track.KitPieces.FirstOrDefault(item => item.KitOutput == output);
            if (piece is null)
            {
                piece = PatternTrackViewModel.CreateKitPiece(track, output, names[output]);
                track.KitPieces.Insert(output == 0 ? 0 : track.KitPieces.Count, piece);
            }

            piece.KitParent = track;
            if (output > 0)
            {
                _effectsFor(piece)?.AttachToOutput(handle, output);
            }
            else
            {
                _followMainOutput?.Invoke(piece);
            }
        }
    }

    // The state each running plug-in had when it started or the project was last saved, to tell whether its
    // sound has been changed since. A plug-in left alone keeps the very same state.
    private readonly Dictionary<Vst3ModuleHandle, (byte[]? Processor, byte[]? Editor)> _savedSounds = new();

    private void RememberSound(Vst3ModuleHandle handle)
    {
        var sound = (_host.TrySaveState(handle, 0), _host.TrySaveState(handle, 1));
        lock (_sync)
        {
            _savedSounds[handle] = sound;
        }
    }

    // True when a running plug-in's sound has been changed in its window since it started or was saved.
    public bool AnySoundChanged()
    {
        List<KeyValuePair<Vst3ModuleHandle, (byte[]? Processor, byte[]? Editor)>> remembered;
        lock (_sync)
        {
            remembered = _savedSounds.Where(pair => _instruments.ContainsValue(pair.Key)).ToList();
        }

        return remembered.Any(pair =>
            !SameBytes(_host.TrySaveState(pair.Key, 0), pair.Value.Processor)
            || !SameBytes(_host.TrySaveState(pair.Key, 1), pair.Value.Editor));
    }

    private static bool SameBytes(byte[]? a, byte[]? b) =>
        a is null ? b is null : b is not null && a.AsSpan().SequenceEqual(b);

    // Stops and unloads the track's plug-in, if it runs one.
    public void Release(PatternTrackViewModel track)
    {
        track = Channel(track);
        Vst3ModuleHandle? handle;
        lock (_sync)
        {
            if (!_instruments.Remove(track, out handle))
            {
                return;
            }
        }

        Stop(handle);
    }

    // Stops the plug-ins of tracks that are gone or have changed plug-in.
    public void KeepOnly(IEnumerable<PatternTrackViewModel> tracks)
    {
        var kept = tracks.Select(Channel).ToHashSet();
        List<Vst3ModuleHandle> stale;
        lock (_sync)
        {
            var staleTracks = _instruments.Where(pair => !kept.Contains(pair.Key) || !IsFor(pair.Key, pair.Value))
                .Select(pair => pair.Key)
                .ToList();
            stale = staleTracks.Select(track => _instruments[track]).ToList();
            foreach (var track in staleTracks)
            {
                _instruments.Remove(track);
            }
        }

        foreach (var handle in stale)
        {
            Stop(handle);
        }
    }

    // Takes each running plug-in's state into its track, so saving the project keeps the sounds chosen.
    public void CaptureStates()
    {
        List<KeyValuePair<PatternTrackViewModel, Vst3ModuleHandle>> running;
        lock (_sync)
        {
            running = _instruments.Where(pair => IsFor(pair.Key, pair.Value)).ToList();
        }

        foreach (var (track, handle) in running)
        {
            var processor = _host.TrySaveState(handle, 0);
            var editor = _host.TrySaveState(handle, 1);
            track.PluginState = processor ?? track.PluginState;
            track.PluginEditorState = editor ?? track.PluginEditorState;
            lock (_sync)
            {
                _savedSounds[handle] = (processor, editor); // saved now: no longer a change
            }
        }
    }

    // Hands the running plug-ins to the tracks that took the place of theirs, as after undo or redo, so they
    // keep playing with the sound they have instead of starting again.
    public void HandOver(Func<PatternTrackViewModel, PatternTrackViewModel?> successorOf)
    {
        lock (_sync)
        {
            foreach (var (track, handle) in _instruments.ToList())
            {
                if (successorOf(track) is { } successor && !ReferenceEquals(successor, track)
                    && IsFor(successor, handle) && !_instruments.ContainsKey(successor))
                {
                    _instruments.Remove(track);
                    _instruments[successor] = handle;
                }
            }
        }
    }

    // Releases every note held by any track's plug-in.
    public void ReleaseAllNotes()
    {
        List<Vst3ModuleHandle> running;
        lock (_sync)
        {
            running = _instruments.Values.ToList();
        }

        foreach (var handle in running)
        {
            _host.ReleaseAllNotes(handle);
        }
    }

    public void Dispose() => KeepOnly(Array.Empty<PatternTrackViewModel>());

    private static bool IsFor(PatternTrackViewModel track, Vst3ModuleHandle handle) =>
        string.Equals(track.PluginFilePath, handle.FilePath, StringComparison.OrdinalIgnoreCase);

    // Raised before a track's plug-in stops and is unloaded, so a window showing it can close first.
    public event Action<Vst3ModuleHandle>? Stopping;

    private void Stop(Vst3ModuleHandle handle)
    {
        lock (_sync)
        {
            _savedSounds.Remove(handle);
        }
        Stopping?.Invoke(handle);
        LiveInstrumentRegistry.Clear(handle);
        _host.StopAudio(handle);
        _host.UnloadModule(handle);
    }
}
