namespace Sonator.Services;

// A change of tempo at a step from the start of the song: from there on the song plays at Bpm.
public sealed record TempoChange(int Step, double Bpm);

// The song's tempo over time: the project's tempo from the start, then each change from its step on.
// Steps are sixteenth notes, four to the beat.
public sealed class TempoMap
{
    private const double StepsPerBeat = 4;

    public TempoMap(double baseBpm, IEnumerable<TempoChange>? changes = null)
    {
        BaseBpm = baseBpm;
        Changes = (changes ?? Enumerable.Empty<TempoChange>())
            .Where(change => change.Step > 0 && double.IsFinite(change.Bpm) && change.Bpm > 0)
            .GroupBy(change => change.Step)
            .Select(group => group.Last())
            .OrderBy(change => change.Step)
            .ToList();
    }

    public double BaseBpm { get; }

    // In order of step, one at most per step, none at step 0 (that is the base tempo).
    public IReadOnlyList<TempoChange> Changes { get; }

    public double TempoAt(double step)
    {
        var tempo = BaseBpm;
        foreach (var change in Changes)
        {
            if (change.Step > step)
            {
                break;
            }
            tempo = change.Bpm;
        }
        return tempo;
    }

    // How long the song takes to get from its start to the step, in seconds.
    public double SecondsAt(double step)
    {
        var seconds = 0.0;
        var from = 0.0;
        var tempo = BaseBpm;
        foreach (var change in Changes)
        {
            if (change.Step >= step)
            {
                break;
            }
            seconds += (change.Step - from) * 60.0 / tempo / StepsPerBeat;
            from = change.Step;
            tempo = change.Bpm;
        }
        return seconds + (Math.Max(0, step) - from) * 60.0 / tempo / StepsPerBeat;
    }

    // The step the song reaches after so many seconds, the other way round from SecondsAt.
    public double StepAt(double seconds)
    {
        var elapsed = 0.0;
        var from = 0.0;
        var tempo = BaseBpm;
        foreach (var change in Changes)
        {
            var span = (change.Step - from) * 60.0 / tempo / StepsPerBeat;
            if (elapsed + span > seconds)
            {
                break;
            }
            elapsed += span;
            from = change.Step;
            tempo = change.Bpm;
        }
        return from + (seconds - elapsed) * tempo / 60.0 * StepsPerBeat;
    }
}
