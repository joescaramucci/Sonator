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

    private double _masterBalance;

    // The master faders' balance, -1 to 1, as MainViewModel.MasterBalanceOf works it out: the master's chain
    // turns the lower side down by it.
    public double MasterBalance
    {
        get => _masterBalance;
        set
        {
            _masterBalance = Math.Clamp(value, -1, 1);
            if (_master is not null && ExistingChainFor(_master) is { } chain)
            {
                ApplyMix(_master, chain);
            }
        }
    }

    private PatternTrackViewModel? _master;

    // The master's effects run on the whole mix. Its gain stays at unity: the master volume is already in
    // every track's gain, ahead of the master's effects. Its balance sets the master faders' difference.
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

    private bool _isSoloActive;

    // True while some track of the pattern is soloed: then only the soloed tracks are heard.
    public bool IsSoloActive
    {
        get => _isSoloActive;
        set
        {
            if (_isSoloActive == value)
            {
                return;
            }

            _isSoloActive = value;
            foreach (var (track, chain) in _chains)
            {
                ApplyMix(track, chain);
            }
        }
    }

    // A track that makes its own sound (a plug-in or an audio clip) is silent while muted, or while another
    // track is soloed and it is not; its fader glides there and back, so muting never clicks.
    // A piece of a drum kit is silent with its track, or while other pieces of the kit are soloed and it is not;
    // the track's own output too, while pieces of its kit are soloed and it is not, or its own piece (the main
    // output's, once heard) is muted or not soloed.
    internal bool IsSilenced(PatternTrackViewModel track) => track.KitParent is { } parent
        ? IsSilenced(parent, ownOutput: false) || track.IsMuted || (parent.HasSoloedPiece && !track.IsSolo)
        : IsSilenced(track, ownOutput: true);

    private bool IsSilenced(PatternTrackViewModel track, bool ownOutput) =>
        track.IsMuted || (_isSoloActive && !track.IsSoloed)
        || (ownOutput && (HeardMainOutput(track) is { } main
            ? main.IsMuted || (track.HasSoloedPiece && !main.IsSolo && !track.IsSolo)
            : track.HasSoloedPiece && !track.IsSolo));

    private static PatternTrackViewModel? HeardMainOutput(PatternTrackViewModel track) =>
        track.KitMainOutput is { IsHeard: true } main ? main : null;

    // The piece of a drum kit's main output has no chain of its own: what is changed on it is mixed into its
    // track's.
    public void FollowMainOutput(PatternTrackViewModel piece)
    {
        piece.PropertyChanged -= OnTrackPropertyChanged;
        piece.PropertyChanged += OnTrackPropertyChanged;
    }

    // A track's gain takes the master volume in; a bus's does not, since what reaches it already has it.
    // A bus has no sends of its own, nor does the master.
    private void ApplyMix(PatternTrackViewModel track, NativeEffectChain chain)
    {
        // A kit piece plays at its own fader times its track's, silent with its track, with its own pan and sends.
        if (track.KitParent is { } parent)
        {
            chain.SetGain(IsSilenced(track)
                ? 0.0
                : Gain(track.MixVolume, _masterVolume) * parent.MixVolume / UnityVolume);
            chain.SetPan(track.MixPan);
            for (var slot = 0; slot < NativeEffectChain.MaxBuses; slot++)
            {
                chain.SetSend(slot, Gain(slot < _buses.Count ? track.SendLevel(_buses[slot].BusId) : 0, UnityVolume));
            }

            return;
        }

        // A drum kit's own output plays at its piece's fader times the track's, and with its piece's pan.
        var isMaster = ReferenceEquals(track, _master);
        var isBus = IsBus(track);
        var main = HeardMainOutput(track);
        chain.SetGain(isMaster ? 1.0
            : isBus ? (track.IsMuted ? 0.0 : Gain(track.MixVolume, UnityVolume))
            : IsSilenced(track) ? 0.0 : Gain(track.MixVolume, _masterVolume) * (main?.MixVolume ?? UnityVolume) / UnityVolume);
        chain.SetPan(isMaster ? _masterBalance : (main ?? track).MixPan);
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
                or nameof(PatternTrackViewModel.Sends) or nameof(PatternTrackViewModel.IsMuted) or nameof(PatternTrackViewModel.IsSolo)
                or nameof(PatternTrackViewModel.IsHeard)
            && sender is PatternTrackViewModel changed && (changed.KitParent ?? changed) is var track
            && _chains.TryGetValue(track, out var chain))
        {
            // A piece's solo changes what its whole kit plays.
            ApplyMix(track, chain);
            foreach (var piece in track.KitPieces)
            {
                if (_chains.TryGetValue(piece, out var pieceChain))
                {
                    ApplyMix(piece, pieceChain);
                }
            }
        }
    }

    // The whole mix's left and right peaks since the previous call, as they leave the master's effects.
    public (double Left, double Right) TakeMasterPeaks() =>
        _master is not null && ExistingChainFor(_master) is { } chain ? chain.TakeStereoPeak() : (0, 0);

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

    // Finds a track by its BusId, for the effects that take their sidechain from it.
    public Func<Guid, PatternTrackViewModel?>? FindTrack { get; set; }

    // Hands the effect in a slot the chain of its sidechain's track, or none.
    private void ApplySidechain(PatternTrackViewModel track, NativeEffectChain chain, int slot)
    {
        var effect = track.Effects[slot];
        var source = effect.SidechainSource is { } id ? FindTrack?.Invoke(id) : null;
        chain.SetSidechain(slot, source is not null && !ReferenceEquals(source, track) ? ChainFor(source) : null);
    }

    // The effect's plug-in when it is loaded, without loading it.
    public Vst3ModuleHandle? LoadedPluginModule(TrackEffectViewModel effect) =>
        effect.IsPlugin && _pluginModules.TryGetValue(effect.InstanceId, out var module) ? module : null;

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

        if (change.ParameterIndex == TrackEffectViewModel.SidechainChange)
        {
            ApplySidechain(track, chain, slot);
        }
        else if (change.ParameterIndex < 0)
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

            if (modules[slot] is { } module && _pluginHost is not null)
            {
                effect.CanSidechain = _pluginHost.HasSidechainInput(module);
            }

            ApplySidechain(track, chain, slot);
        }
    }
}
