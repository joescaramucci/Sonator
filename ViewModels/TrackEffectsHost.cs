using Sonator.Services.Effects;

namespace Sonator.ViewModels;

// Keeps one native effect chain per track in step with the track's effects: rebuilt when effects are
// added, removed or moved, and updated knob by knob as they turn, without interrupting the sound.
internal sealed class TrackEffectsHost : IDisposable
{
    private readonly Dictionary<PatternTrackViewModel, NativeEffectChain> _chains = new();

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
        track.EffectsChanged += OnEffectsChanged;
        return chain;
    }

    // The track's chain if it has one already, without making it.
    public NativeEffectChain? ExistingChainFor(PatternTrackViewModel track) =>
        _chains.TryGetValue(track, out var chain) ? chain : null;

    public void Dispose()
    {
        foreach (var (track, chain) in _chains)
        {
            track.EffectsChanged -= OnEffectsChanged;
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
