namespace Sonator.ViewModels;

public sealed class MidiInputDeviceViewModel : ViewModelBase
{
    private bool _isSelected;

    public MidiInputDeviceViewModel(uint? deviceId, string displayName)
    {
        DeviceId = deviceId;
        DisplayName = displayName;
    }

    public uint? DeviceId { get; }

    public string DisplayName { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}
