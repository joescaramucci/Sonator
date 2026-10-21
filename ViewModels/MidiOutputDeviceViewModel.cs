namespace Sonator.ViewModels;

public sealed class MidiOutputDeviceViewModel
{
    public MidiOutputDeviceViewModel(uint? deviceId, string displayName, bool isOff = false)
    {
        DeviceId = deviceId;
        DisplayName = displayName;
        IsOff = isOff;
    }

    // "Off" sends nothing to a MIDI output, so no General MIDI synth is used.
    public bool IsOff { get; }

    public uint? DeviceId { get; }

    public string DisplayName { get; }

    public bool IsMapper => DeviceId is null;
}
