using System.Runtime.InteropServices;

namespace Sonator.Services;

// A note-on or note-off received from a MIDI input device.
public sealed record MidiNoteMessage(int Channel, int NoteNumber, int Velocity, bool IsOn);

// Reads note messages from a Windows MIDI input device, such as a keyboard controller.
// Messages arrive on a WinMM thread, so subscribers must marshal to their own thread.
public sealed class MidiInputService : IDisposable
{
    private const uint CallbackFunction = 0x00030000;
    private const uint MimData = 0x3C3;
    private const int StatusNoteOff = 0x80;
    private const int StatusNoteOn = 0x90;

    // Kept as a field so the callback is not collected while WinMM still holds it.
    private readonly MidiInProc _callback;
    private IntPtr _handle = IntPtr.Zero;

    public MidiInputService()
    {
        _callback = OnMidiMessage;
    }

    public event Action<MidiNoteMessage>? NoteReceived;

    public bool IsOpen => _handle != IntPtr.Zero;

    public static IReadOnlyList<(uint DeviceId, string DisplayName)> GetInputDevices()
    {
        var devices = new List<(uint DeviceId, string DisplayName)>();
        var count = midiInGetNumDevs();
        for (uint i = 0; i < count; i++)
        {
            if (midiInGetDevCaps(i, out var caps, (uint)Marshal.SizeOf<MidiInCaps>()) == 0)
            {
                devices.Add((i, caps.ProductName));
            }
        }

        return devices;
    }

    public bool Open(uint deviceId)
    {
        Close();
        var callbackPointer = Marshal.GetFunctionPointerForDelegate(_callback);
        if (midiInOpen(out _handle, deviceId, callbackPointer, IntPtr.Zero, CallbackFunction) != 0)
        {
            _handle = IntPtr.Zero;
            return false;
        }

        midiInStart(_handle);
        return true;
    }

    public void Close()
    {
        if (_handle == IntPtr.Zero)
        {
            return;
        }

        midiInStop(_handle);
        midiInReset(_handle);
        midiInClose(_handle);
        _handle = IntPtr.Zero;
    }

    public void Dispose()
    {
        Close();
    }

    private void OnMidiMessage(IntPtr handle, uint message, IntPtr instance, IntPtr param1, IntPtr param2)
    {
        if (message != MimData)
        {
            return;
        }

        // Short messages are packed as status in the low byte, then the two data bytes.
        var packed = param1.ToInt64();
        var status = (int)(packed & 0xFF);
        var data1 = (int)((packed >> 8) & 0xFF);
        var data2 = (int)((packed >> 16) & 0xFF);
        var channel = status & 0x0F;

        switch (status & 0xF0)
        {
            case StatusNoteOn when data2 > 0:
                NoteReceived?.Invoke(new MidiNoteMessage(channel, data1, data2, IsOn: true));
                break;
            case StatusNoteOn:
            case StatusNoteOff:
                NoteReceived?.Invoke(new MidiNoteMessage(channel, data1, 0, IsOn: false));
                break;
        }
    }

    private delegate void MidiInProc(IntPtr handle, uint message, IntPtr instance, IntPtr param1, IntPtr param2);

    [DllImport("winmm.dll")]
    private static extern uint midiInGetNumDevs();

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern int midiInGetDevCaps(uint deviceId, out MidiInCaps caps, uint size);

    [DllImport("winmm.dll")]
    private static extern int midiInOpen(out IntPtr handle, uint deviceId, IntPtr callback, IntPtr instance, uint flags);

    [DllImport("winmm.dll")]
    private static extern int midiInStart(IntPtr handle);

    [DllImport("winmm.dll")]
    private static extern int midiInStop(IntPtr handle);

    [DllImport("winmm.dll")]
    private static extern int midiInReset(IntPtr handle);

    [DllImport("winmm.dll")]
    private static extern int midiInClose(IntPtr handle);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MidiInCaps
    {
        public ushort ManufacturerId;
        public ushort ProductId;
        public uint DriverVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string ProductName;

        public uint Support;
    }
}
