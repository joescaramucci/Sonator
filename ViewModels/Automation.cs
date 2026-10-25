namespace Sonator.ViewModels;

// A point of a track's automation: at this step from the start, the value is this. Between two points
// the value moves in a straight line; before the first and after the last it stays at theirs.
public sealed record AutomationPoint(double Step, double Value);

// What a track's automation lane shows and edits.
public enum AutomationTarget
{
    None,
    Volume,
    Pan,
    Parameter, // a knob of a plug-in or of an effect (see AutomatedParameter)
}

public static class Automation
{
    // The value at a step, or null when there are no points. The points are in order of step.
    public static double? ValueAt(IReadOnlyList<AutomationPoint> points, double step)
    {
        if (points.Count == 0)
        {
            return null;
        }

        if (step <= points[0].Step)
        {
            return points[0].Value;
        }

        for (var i = 1; i < points.Count; i++)
        {
            var next = points[i];
            if (step < next.Step)
            {
                var previous = points[i - 1];
                var span = next.Step - previous.Step;
                return span <= 0 ? next.Value : previous.Value + (next.Value - previous.Value) * (step - previous.Step) / span;
            }
        }

        return points[^1].Value;
    }

    // The points with one more, kept in order of step. Returns the new list and where the point went.
    public static (IReadOnlyList<AutomationPoint> Points, int Index) Add(IReadOnlyList<AutomationPoint> points, AutomationPoint point)
    {
        var index = 0;
        while (index < points.Count && points[index].Step <= point.Step)
        {
            index++;
        }

        var list = points.ToList();
        list.Insert(index, point);
        return (list, index);
    }

    // The point at index moved, but not past its neighbours, so the points stay in order.
    public static IReadOnlyList<AutomationPoint> Move(IReadOnlyList<AutomationPoint> points, int index, AutomationPoint moved)
    {
        var low = index > 0 ? points[index - 1].Step : 0;
        var high = index < points.Count - 1 ? points[index + 1].Step : double.MaxValue;
        var list = points.ToList();
        list[index] = moved with { Step = Math.Clamp(moved.Step, low, high) };
        return list;
    }

    public static IReadOnlyList<AutomationPoint> Remove(IReadOnlyList<AutomationPoint> points, int index) =>
        points.Where((_, i) => i != index).ToList();

    // The lowest value of a target: a volume runs from 0 to 1, a pan from -1 (left) to 1 (right).
    public static double Minimum(AutomationTarget target) => target == AutomationTarget.Pan ? -1 : 0;
}
