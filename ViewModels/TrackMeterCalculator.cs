namespace Sonator.ViewModels;

// Drives the per-track signal meters and the smoothed master meter during playback.
internal sealed class TrackMeterCalculator
{
    private const double HoldDecay = 0.55;
    private const double ReleaseDecay = 0.78;
    private const double SustainDecay = 0.93; // a held note fades slowly
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

            // Tracks with their own audio show the measured signal instead (see ShowAudioLevels).
            if (track.HasOwnAudio)
            {
                mixLevel += track.SignalLevel;
                activeTrackCount++;
                continue;
            }

            var hit = HitVelocity(track, stepIndex);
            if (hit > 0)
            {
                track.SignalLevel = Math.Max(track.SignalLevel * HoldDecay, hit / 127.0 * track.Volume);
            }
            else
            {
                track.SignalLevel = track.SignalLevel * (IsNoteSounding(track, stepIndex) ? SustainDecay : ReleaseDecay);
            }

            mixLevel += track.SignalLevel;
            activeTrackCount++;
        }

        var normalizedMix = activeTrackCount > 0 ? mixLevel / activeTrackCount : 0.0;
        var targetLevel = Math.Clamp(normalizedMix * masterVolume * MasterGain, 0.0, 1.0);
        _smoothedMasterLevel = (_smoothedMasterLevel * SmoothingKeep) + (targetLevel * SmoothingResponse);
        return _smoothedMasterLevel;
    }

    // The track meters' scale for measured audio: -48 dBFS at the bottom, 0 dBFS at the top.
    private const double AudioMeterFloorDb = -48;

    // How far a meter of measured audio may fall in one refresh, so it falls back smoothly after a peak.
    private const double AudioMeterFall = 0.03;

    // Shows the peak measured on a track's output since the last refresh: the meter jumps up at once
    // and falls back gently, and stays lit for as long as the sound lasts.
    public static void ShowAudioLevel(PatternTrackViewModel track, double peak)
    {
        var level = peak <= 0 ? 0 : Math.Clamp((20 * Math.Log10(peak) - AudioMeterFloorDb) / -AudioMeterFloorDb, 0, 1);
        track.SignalLevel = track.IsMuted ? 0 : Math.Max(level, track.SignalLevel - AudioMeterFall);
    }

    // Lets the meters of all tracks fall back when nothing drives them, such as after a key is played live.
    // Returns true while any meter is still above zero.
    public static bool Release(IEnumerable<PatternTrackViewModel> tracks)
    {
        var anyLit = false;
        foreach (var track in tracks)
        {
            if (track.HasOwnAudio)
            {
                continue; // measured, not estimated
            }

            if (track.SignalLevel <= 0.01)
            {
                track.SignalLevel = 0;
                continue;
            }

            track.SignalLevel *= ReleaseDecay;
            anyLit = true;
        }

        return anyLit;
    }

    // The loudest note that starts on this step: a step of the grid, or a recorded note. 0 when none starts.
    internal static int HitVelocity(PatternTrackViewModel track, int stepIndex)
    {
        var velocity = 0;
        var length = PatternLength(track);
        if (length == 0)
        {
            return 0;
        }

        var localStep = stepIndex % length;
        if (localStep < track.Steps.Count && track.Steps[localStep].IsActive)
        {
            velocity = track.Steps[localStep].Velocity;
        }

        foreach (var note in track.MidiNoteEvents)
        {
            if ((int)Math.Floor(note.StepIndex + note.StartOffset) == localStep)
            {
                velocity = Math.Max(velocity, note.Velocity);
            }
        }

        return velocity;
    }

    // True while a recorded note that started earlier is still held on this step.
    private static bool IsNoteSounding(PatternTrackViewModel track, int stepIndex)
    {
        var length = PatternLength(track);
        if (length == 0)
        {
            return false;
        }

        var localStep = stepIndex % length;
        return track.MidiNoteEvents.Any(note =>
        {
            var start = note.StepIndex + note.StartOffset;
            return start < localStep && start + note.DurationSteps + note.LengthFraction > localStep;
        });
    }

    // Recorded notes can run past the step grid, so the track loops over whichever is longer.
    private static int PatternLength(PatternTrackViewModel track)
    {
        var notesEnd = track.MidiNoteEvents.Count == 0
            ? 0
            : (int)Math.Ceiling(track.MidiNoteEvents.Max(note => note.StepIndex + note.StartOffset + note.DurationSteps + note.LengthFraction));
        return Math.Max(track.Steps.Count, notesEnd);
    }
}
