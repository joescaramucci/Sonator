using Sonator.Services.Effects;

namespace Sonator.ViewModels;

// Keeps one native effect chain per track in step with the track's effects: rebuilt when effects are
// added, removed or moved, and updated knob by knob as they turn, without interrupting the sound.
internal sealed class TrackEffectsHost : IDisposable
{
    private readonly Dictionary<PatternTrackViewModel, NativeEffectChain> _chains = new();
    private double _masterVolume = UnityVolume;

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

    private void ApplyMix(PatternTrackViewModel track, NativeEffectChain chain)
    {
        var isMaster = ReferenceEquals(track, _master);
        chain.SetGain(isMaster ? 1.0 : Gain(track.MixVolume, _masterVolume));
        chain.SetPan(isMaster ? 0.0 : track.MixPan);
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
            && sender is PatternTrackViewModel track && _chains.TryGetValue(track, out var chain))
        {
            ApplyMix(track, chain);
        }
    }

    // The track's chain if it has one already, without making it.
    public NativeEffectChain? ExistingChainFor(PatternTrackViewModel track) =>
        _chains.TryGetValue(track, out var chain) ? chain : null;

    public void Dispose()
    {
        if (_master is not null && _chains.ContainsKey(_master))
        {
            NativeEffectChain.ClearMaster();
        }

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

    private static void ApplyAll(PatternTrackViewModel track, NativeEffectChain chain)
    {
        chain.SetLayout(track.Effects.Select(effect => effect.Definition.Kind).ToList());
        for (var slot = 0; slot < track.Effects.Count; slot++)
        {
            var effect = track.Effects[slot];
            chain.SetEnabled(slot, effect.IsEnabled);
            foreach (var parameter in effect.Parameters)
            {
                chain.SetParameter(slot, parameter.Definition, parameter.Index, parameter.Value);
            }
        }
    }
}
