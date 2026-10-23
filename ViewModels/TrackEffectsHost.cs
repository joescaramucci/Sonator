using Sonator.Services.Effects;
using Sonator.Services.Vst3;

namespace Sonator.ViewModels;

// Keeps one native effect chain per track in step with the track's effects: rebuilt when effects are
// added, removed or moved, and updated knob by knob as they turn, without interrupting the sound.
// A VST3 effect in a rack runs a plug-in module loaded here, one per effect instance (see
// TrackEffectViewModel.InstanceId); it stays loaded while an effect with its instance is on a track.
internal sealed class TrackEffectsHost : IDisposable
{
    private readonly Dictionary<PatternTrackViewModel, NativeEffectChain> _chains = new();
    private readonly IVst3PluginHost? _pluginHost;
    private readonly Dictionary<Guid, Vst3ModuleHandle> _pluginModules = new();
    private readonly Dictionary<Guid, PatternTrackViewModel> _pluginHolders = new(); // whose chain runs each one
    private readonly HashSet<Guid> _failedPlugins = new();
    private double _masterVolume = UnityVolume;

    public TrackEffectsHost(IVst3PluginHost? pluginHost = null)
    {
        _pluginHost = pluginHost;
    }

    // Raised before an effect's plug-in is unloaded, so a window showing it closes first.
    public event Action<Vst3ModuleHandle>? PluginUnloading;

    // The fader position that leaves the sound as it is: the default of the track and master volumes.
    internal const double UnityVolume = 0.75;

    // The gain the track's sound gets from its own volume and the master volume: unity at the defaults,
    // silent at zero, and a little over unity (about +5 dB each) with the faders all the way up.
    internal static double Gain(double trackVolume, double masterVolume) =>
        trackVolume / UnityVolume * (masterVolume / UnityVolume);

    public double MasterVolume
    {
        get => _masterVolume;
        set
        {
            _masterVolume = value;
            foreach (var (track, chain) in _chains)
            {
                ApplyMix(track, chain);
            }
        }
    }

    private PatternTrackViewModel? _master;

    // The master's effects run on the whole mix. Its gain stays at unity: the master volume is already in
    // every track's gain, ahead of the master's effects.
    public void UseAsMaster(PatternTrackViewModel master)
    {
        _master = master;
        if (ChainFor(master) is { } chain)
        {
            ApplyMix(master, chain);
            chain.UseAsMaster();
        }
    }

    private List<PatternTrackViewModel> _buses = new();

    // The buses, in their slots in the mix. Each runs through its own chain and joins the mix ahead of the
    // master's effects; every track's sends follow the slots.
    public void SetBuses(IReadOnlyList<PatternTrackViewModel> buses)
    {
        _buses = buses.Take(NativeEffectChain.MaxBuses).ToList();
        for (var slot = 0; slot < NativeEffectChain.MaxBuses; slot++)
        {
            if (slot < _buses.Count && ChainFor(_buses[slot]) is { } busChain)
            {
                busChain.UseAsBus(slot);
            }
            else
            {
                NativeEffectChain.ClearBus(slot);
            }
        }

        foreach (var (track, chain) in _chains)
        {
            ApplyMix(track, chain);
        }
    }

    private bool IsBus(PatternTrackViewModel track) => _buses.Contains(track);

    // A track's gain takes the master volume in; a bus's does not, since what reaches it already has it.
    // A bus has no sends of its own, nor does the master.
    private void ApplyMix(PatternTrackViewModel track, NativeEffectChain chain)
    {
        var isMaster = ReferenceEquals(track, _master);
        var isBus = IsBus(track);
        chain.SetGain(isMaster ? 1.0
            : isBus ? (track.IsMuted ? 0.0 : Gain(track.MixVolume, UnityVolume))
            : Gain(track.MixVolume, _masterVolume));
        chain.SetPan(isMaster ? 0.0 : track.MixPan);
        for (var slot = 0; slot < NativeEffectChain.MaxBuses; slot++)
        {
            var level = !isMaster && !isBus && slot < _buses.Count ? track.SendLevel(_buses[slot].BusId) : 0;
            chain.SetSend(slot, Gain(level, UnityVolume));
        }
    }

    // The track's chain, made on first use. Null when the native bridge is not available.
    public NativeEffectChain? ChainFor(PatternTrackViewModel track)
    {
        if (_chains.TryGetValue(track, out var chain))
        {
            return chain;
        }

        chain = NativeEffectChain.TryCreate();
        if (chain is null)
        {
            return null;
        }

        _chains[track] = chain;
        ApplyAll(track, chain);
        ApplyMix(track, chain);
        track.EffectsChanged += OnEffectsChanged;
        track.PropertyChanged += OnTrackPropertyChanged;
        return chain;
    }

    private void OnTrackPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PatternTrackViewModel.MixVolume) or nameof(PatternTrackViewModel.MixPan)
                or nameof(PatternTrackViewModel.Sends) or nameof(PatternTrackViewModel.IsMuted)
            && sender is PatternTrackViewModel track && _chains.TryGetValue(track, out var chain))
        {
            ApplyMix(track, chain);
        }
    }

    // The track's chain if it has one already, without making it.
    public NativeEffectChain? ExistingChainFor(PatternTrackViewModel track) =>
        _chains.TryGetValue(track, out var chain) ? chain : null;

    // ---- VST3 effects ----

    // The loaded plug-in of a VST3 effect, for its window: loaded now if it is not yet. Null when the
    // plug-in cannot be loaded here (missing or broken) or the bridge is not available.
    public Vst3ModuleHandle? PluginModuleFor(TrackEffectViewModel effect)
    {
        if (!effect.IsPlugin || _pluginHost is null)
        {
            return null;
        }

        if (_pluginModules.TryGetValue(effect.InstanceId, out var module))
        {
            return module;
        }

        if (_failedPlugins.Contains(effect.InstanceId))
        {
            return null;
        }

        module = _pluginHost.TryLoadModule(effect.PluginPath!, out _);
        if (module is null)
        {
            _failedPlugins.Add(effect.InstanceId);
            effect.IsPluginMissing = true;
            return null;
        }

        if (effect.PluginState is { } state)
        {
            _pluginHost.TryLoadState(module, 0, state);
        }
        if (effect.PluginEditorState is { } editorState)
        {
            _pluginHost.TryLoadState(module, 1, editorState);
        }

        _pluginModules[effect.InstanceId] = module;
        return module;
    }

    // True when the effect's plug-in could not be loaded: the rack shows it as missing.
    public bool IsPluginMissing(TrackEffectViewModel effect) => _failedPlugins.Contains(effect.InstanceId);

    // Takes each loaded effect plug-in's state into its effects, so saving or copying keeps its settings.
    public void CaptureStates()
    {
        if (_pluginHost is null)
        {
            return;
        }

        foreach (var effect in LiveTracks().SelectMany(track => track.Effects).Where(effect => effect.IsPlugin))
        {
            if (_pluginModules.TryGetValue(effect.InstanceId, out var module))
            {
                effect.PluginState = _pluginHost.TrySaveState(module, 0) ?? effect.PluginState;
                effect.PluginEditorState = _pluginHost.TrySaveState(module, 1) ?? effect.PluginEditorState;
            }
        }
    }

    // Keeps the chains of these tracks (and the master's) and lets the others go, with the plug-ins no
    // track uses any more. Called when the tracks are replaced, as by undo or opening a project.
    public void KeepOnly(IEnumerable<PatternTrackViewModel> tracks)
    {
        var kept = tracks.Concat(_buses).ToHashSet();
        foreach (var track in _chains.Keys.Where(track => !kept.Contains(track) && !ReferenceEquals(track, _master)).ToList())
        {
            var chain = _chains[track];
            StripPlugins(track);
            track.EffectsChanged -= OnEffectsChanged;
            track.PropertyChanged -= OnTrackPropertyChanged;
            chain.Dispose();
            _chains.Remove(track);
        }

        ReleaseUnusedPlugins(kept);
    }

    // Unloads the plug-ins of effects no longer on any of these tracks or the master, as after one is removed.
    public void ReleaseUnusedPlugins(IEnumerable<PatternTrackViewModel> tracks)
    {
        var used = tracks.Concat(_buses).Append(_master).OfType<PatternTrackViewModel>()
            .SelectMany(track => track.Effects)
            .Where(effect => effect.IsPlugin)
            .Select(effect => effect.InstanceId)
            .ToHashSet();
        foreach (var id in _pluginModules.Keys.Where(id => !used.Contains(id)).ToList())
        {
            if (_pluginHolders.TryGetValue(id, out var holder))
            {
                StripPlugins(holder); // not normally needed: a chain drops a plug-in as soon as it leaves the track
            }

            UnloadPlugin(id);
        }

        _failedPlugins.IntersectWith(used);
    }

    // Unloads every effect plug-in, as before another project is opened.
    public void ReleaseAllPlugins()
    {
        foreach (var holder in _pluginHolders.Values.Distinct().ToList())
        {
            StripPlugins(holder);
        }

        foreach (var id in _pluginModules.Keys.ToList())
        {
            UnloadPlugin(id);
        }

        _failedPlugins.Clear();
    }

    private void UnloadPlugin(Guid id)
    {
        if (_pluginModules.Remove(id, out var module))
        {
            PluginUnloading?.Invoke(module);
            _pluginHost?.UnloadModule(module);
        }
        _pluginHolders.Remove(id);
    }

    // Rebuilds a track's chain with its plug-ins left out (their places pass the sound through), so they
    // can run in another chain or be unloaded.
    private void StripPlugins(PatternTrackViewModel track)
    {
        if (_chains.TryGetValue(track, out var chain))
        {
            ApplyAll(track, chain, withPlugins: false);
        }
    }

    // The tracks whose chains exist, the master's among them.
    private IEnumerable<PatternTrackViewModel> LiveTracks() => _chains.Keys;

    public void Dispose()
    {
        if (_master is not null && _chains.ContainsKey(_master))
        {
            NativeEffectChain.ClearMaster();
        }
        for (var slot = 0; slot < _buses.Count; slot++)
        {
            NativeEffectChain.ClearBus(slot);
        }

        ReleaseAllPlugins();
        foreach (var (track, chain) in _chains)
        {
            track.EffectsChanged -= OnEffectsChanged;
            track.PropertyChanged -= OnTrackPropertyChanged;
            chain.Dispose();
        }

        _chains.Clear();
    }

    private void OnEffectsChanged(object? sender, TrackEffectChange change)
    {
        if (sender is not PatternTrackViewModel track || !_chains.TryGetValue(track, out var chain))
        {
            return;
        }

        if (change.Effect is null)
        {
            ApplyAll(track, chain);
            return;
        }

        var slot = track.Effects.IndexOf(change.Effect);
        if (slot < 0)
        {
            return;
        }

        if (change.ParameterIndex < 0)
        {
            chain.SetEnabled(slot, change.Effect.IsEnabled);
        }
        else
        {
            var parameter = change.Effect.Parameters[change.ParameterIndex];
            chain.SetParameter(slot, parameter.Definition, parameter.Index, parameter.Value);
        }
    }

    private void ApplyAll(PatternTrackViewModel track, NativeEffectChain chain, bool withPlugins = true)
    {
        var modules = new List<Vst3ModuleHandle?>();
        foreach (var effect in track.Effects)
        {
            var module = withPlugins ? PluginModuleFor(effect) : null;

            // A plug-in runs in one chain at a time: after an undo the track that takes another's place takes
            // its plug-ins over, and the old track's chain lets them go first.
            if (module is not null && _pluginHolders.TryGetValue(effect.InstanceId, out var holder) && !ReferenceEquals(holder, track))
            {
                StripPlugins(holder);
            }

            modules.Add(module);
        }

        chain.SetLayout(track.Effects.Select(effect => effect.Definition.Kind).ToList(), modules);

        foreach (var id in _pluginHolders.Where(entry => ReferenceEquals(entry.Value, track)).Select(entry => entry.Key).ToList())
        {
            _pluginHolders.Remove(id);
        }
        for (var slot = 0; slot < track.Effects.Count; slot++)
        {
            var effect = track.Effects[slot];
            if (modules[slot] is not null)
            {
                _pluginHolders[effect.InstanceId] = track;
            }

            chain.SetEnabled(slot, effect.IsEnabled);
            foreach (var parameter in effect.Parameters)
            {
                chain.SetParameter(slot, parameter.Definition, parameter.Index, parameter.Value);
            }
        }
    }
}
