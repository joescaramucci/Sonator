namespace Sonator.ViewModels;

public sealed class AudioOutputDeviceViewModel : ViewModelBase
{
    private bool _isSelected;

    public AudioOutputDeviceViewModel(string? deviceId, string displayName)
    {
        DeviceId = deviceId;
        DisplayName = displayName;
    }

    // Endpoint ID of the device. Null stands for the Windows default output device.
    public string? DeviceId { get; }

    public string DisplayName { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}
