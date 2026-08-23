namespace Sonator.ViewModels;

public sealed class MidiOutputDeviceViewModel
{
    public MidiOutputDeviceViewModel(uint? deviceId, string displayName)
    {
        DeviceId = deviceId;
        DisplayName = displayName;
    }

    public uint? DeviceId { get; }

    public string DisplayName { get; }

    public bool IsMapper => DeviceId is null;
}
