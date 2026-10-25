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

// A take of a track: an audio clip and where it starts, or the notes it had; named "Take 3, bar 5" and the like.
public sealed record TrackTake(string Name, string? AudioPath, int AudioStartStep, IReadOnlyList<MidiNoteEventViewModel>? Notes);

// A controller of a MIDI keyboard or control surface assigned to a control of Sonator's (MIDI Learn): its channel and
// controller (128 aftertouch, 129 pitch bend), and the track's fader, pan or knob it moves.
public sealed record MidiMapping(int Channel, int Controller, Guid Track, AutomationTarget Target, AutomatedParameter? Parameter, string Name)
{
    public string Describe() =>
        $"{(Controller switch { 128 => "Aftertouch", 129 => "Pitch bend", _ => $"CC {Controller}" })} (channel {Channel + 1}) → {Name}";
}
