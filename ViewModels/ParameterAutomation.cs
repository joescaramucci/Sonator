namespace Sonator.ViewModels;

// A parameter a track's automation can move: of its instrument plug-in (Effect null) or of one of its effects
// (by the effect's InstanceId); a VST3 parameter's id, or the index of a knob of one of Sonator's effects. Two are
// the same parameter whatever they are called.
public sealed record AutomatedParameter(Guid? Effect, uint Parameter, string Name)
{
    public bool Equals(AutomatedParameter? other) => other is not null && Effect == other.Effect && Parameter == other.Parameter;

    public override int GetHashCode() => HashCode.Combine(Effect, Parameter);
}

// A track an effect can take its sidechain from, as the rack's list shows it (Guid.Empty for none).
public sealed record SidechainChoice(Guid Id, string Name);

// A track's automation of one parameter: points from 0 to 1, the knob's travel or the plug-in's own value.
public sealed record ParameterLane(AutomatedParameter Target, IReadOnlyList<AutomationPoint> Points);
