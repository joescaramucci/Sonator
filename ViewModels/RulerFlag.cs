namespace Sonator.ViewModels;

// A flag on the timeline's ruler: a change of tempo (its new tempo as the label) or a marker (its name),
// at X pixels from the start of the timeline.
public sealed record RulerFlag(int Step, string Label, bool IsTempo, double X);
