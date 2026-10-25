using System.Diagnostics;

namespace Sonator.ViewModels;

// Writes automation from what is moved while the song plays: a fader, a pan knob, a knob of Sonator's effects, a
// knob in a plug-in's window. Each movement is a pass; once nothing has moved it for a moment (or the song stops)
// its points take the place of those the lane had between the pass's first and last step. While a pass is open
// its lane is not read, so the knob stays where the hand puts it.
public sealed class AutomationRecorder
{
    // How long a knob must rest for its pass to be written into the lane.
    public static readonly long RestTicks = Stopwatch.Frequency * 2 / 5;

    private readonly record struct Key(PatternTrackViewModel Track, AutomationTarget Target, AutomatedParameter? Parameter);

    private sealed class Pass
    {
        public required AutomatedParameter? Parameter { get; init; }
        public List<AutomationPoint> Points { get; } = new();
        public long LastTicks { get; set; }
    }

    private readonly Dictionary<Key, Pass> _passes = new();

    public bool IsRecording => _passes.Count > 0;

    // A value the hand set at a step of the song. A step before the pass's last (the song looped back) starts a
    // new pass, the old one written first.
    public void Record(PatternTrackViewModel track, AutomationTarget target, AutomatedParameter? parameter, double step, double value, long nowTicks)
    {
        var key = new Key(track, target, target == AutomationTarget.Parameter ? parameter : null);
        if (_passes.TryGetValue(key, out var pass) && pass.Points.Count > 0 && step < pass.Points[^1].Step - 0.01)
        {
            Write(key, pass);
            _passes.Remove(key);
            pass = null;
        }

        if (pass is null)
        {
            pass = new Pass { Parameter = parameter };
            _passes[key] = pass;
        }

        pass.Points.Add(new AutomationPoint(Math.Max(0, step), value));
        pass.LastTicks = nowTicks;
    }

    // True while the hand is moving this lane's value.
    public bool IsWriting(PatternTrackViewModel track, AutomationTarget target, AutomatedParameter? parameter = null) =>
        _passes.ContainsKey(new Key(track, target, target == AutomationTarget.Parameter ? parameter : null));

    // Writes the passes whose knob has rested (all of them when all is set) into their lanes. Calls beforeWrite
    // once before the first, so it can be undone. Returns how many were written.
    public int Commit(long nowTicks, bool all, Action? beforeWrite = null)
    {
        var done = _passes.Where(pair => all || nowTicks - pair.Value.LastTicks >= RestTicks).ToList();
        if (done.Count == 0)
        {
            return 0;
        }

        beforeWrite?.Invoke();
        foreach (var (key, pass) in done)
        {
            Write(key, pass);
            _passes.Remove(key);
        }

        return done.Count;
    }

    private static void Write(Key key, Pass pass)
    {
        var track = key.Track;
        switch (key.Target)
        {
            case AutomationTarget.Volume:
                track.VolumeAutomation = Merge(track.VolumeAutomation, pass.Points);
                break;
            case AutomationTarget.Pan:
                track.PanAutomation = Merge(track.PanAutomation, pass.Points);
                break;
            case AutomationTarget.Parameter when pass.Parameter is { } parameter:
                track.SetParameterPoints(parameter, Merge(track.ParameterPoints(parameter), pass.Points));
                break;
        }
    }

    // The lane's points outside the pass, with the pass's, fewer where the value hardly moved.
    internal static IReadOnlyList<AutomationPoint> Merge(IReadOnlyList<AutomationPoint> lane, IReadOnlyList<AutomationPoint> pass)
    {
        if (pass.Count == 0)
        {
            return lane;
        }

        var start = pass[0].Step;
        var end = pass[^1].Step;
        return lane
            .Where(point => point.Step < start - 1e-9 || point.Step > end + 1e-9)
            .Concat(Thin(pass))
            .OrderBy(point => point.Step)
            .ToList();
    }

    // Keeps the first and last points, and between them a point where the value has moved or a step has passed.
    internal static IReadOnlyList<AutomationPoint> Thin(IReadOnlyList<AutomationPoint> points)
    {
        var kept = new List<AutomationPoint>();
        for (var i = 0; i < points.Count; i++)
        {
            var point = points[i];
            if (kept.Count == 0 || i == points.Count - 1
                || Math.Abs(point.Value - kept[^1].Value) >= 0.004
                || point.Step - kept[^1].Step >= 1)
            {
                if (kept.Count > 0 && Math.Abs(point.Step - kept[^1].Step) < 1e-9)
                {
                    kept[^1] = point; // the same step: the later value
                }
                else
                {
                    kept.Add(point);
                }
            }
        }

        return kept;
    }
}
