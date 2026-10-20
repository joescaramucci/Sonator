namespace Sonator.ViewModels;

// Drives the per-track signal meters and the smoothed master meter during playback.
internal sealed class TrackMeterCalculator
{
    private const double HoldDecay = 0.55;
    private const double ReleaseDecay = 0.78;
    private const double MasterGain = 3.2;
    private const double SmoothingKeep = 0.72;
    private const double SmoothingResponse = 0.28;

    private double _smoothedMasterLevel;

    public void Reset(IEnumerable<PatternTrackViewModel> tracks)
    {
        foreach (var track in tracks)
        {
            track.SignalLevel = 0;
        }

        _smoothedMasterLevel = 0;
    }

    // Updates each track meter for the given step and returns the smoothed master level in [0, 1].
    public double Update(IReadOnlyList<PatternTrackViewModel> tracks, int stepIndex, double masterVolume)
    {
        var mixLevel = 0.0;
        var activeTrackCount = 0;
        foreach (var track in tracks)
        {
            if (track.IsMuted)
            {
                track.SignalLevel = 0;
                continue;
            }

            var localStep = track.Steps.Count == 0 ? -1 : stepIndex % track.Steps.Count;
            if (localStep >= 0 && track.Steps[localStep].IsActive)
            {
                var velocity = track.Steps[localStep].Velocity / 127.0 * track.Volume;
                track.SignalLevel = Math.Max(track.SignalLevel * HoldDecay, velocity);
            }
            else
            {
                track.SignalLevel = track.SignalLevel * ReleaseDecay;
            }

            mixLevel += track.SignalLevel;
            activeTrackCount++;
        }

        var normalizedMix = activeTrackCount > 0 ? mixLevel / activeTrackCount : 0.0;
        var targetLevel = Math.Clamp(normalizedMix * masterVolume * MasterGain, 0.0, 1.0);
        _smoothedMasterLevel = (_smoothedMasterLevel * SmoothingKeep) + (targetLevel * SmoothingResponse);
        return _smoothedMasterLevel;
    }
}
