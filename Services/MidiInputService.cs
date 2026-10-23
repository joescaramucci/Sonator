using System.Runtime.InteropServices;

namespace Sonator.Services;

// A note-on or note-off received from a MIDI input device.
public sealed record MidiNoteMessage(int Channel, int NoteNumber, int Velocity, bool IsOn);

// A controller moved on a MIDI input device: a control change (0-127: 1 the mod wheel, 64 the sustain pedal),
// channel aftertouch (128) or pitch bend (129), with its value from 0 to 1 (pitch bend centred at 0.5).
public sealed record MidiControllerMessage(int Channel, int Controller, double Value)
{
    public const int ModWheel = 1;
    public const int Sustain = 64;
    public const int Aftertouch = 128;
    public const int PitchBend = 129;
}

// Reads note messages from a Windows MIDI input device, such as a keyboard controller.
// Messages arrive on a WinMM thread, so subscribers must marshal to their own thread.
public sealed class MidiInputService : IDisposable
{
    private const uint CallbackFunction = 0x00030000;
    private const uint MimData = 0x3C3;
    private const int StatusNoteOff = 0x80;
    private const int StatusNoteOn = 0x90;
    private const int StatusControlChange = 0xB0;
    private const int StatusChannelPressure = 0xD0;
    private const int StatusPitchBend = 0xE0;

    // Kept as a field so the callback is not collected while WinMM still holds it.
    private readonly MidiInProc _callback;
    private IntPtr _handle = IntPtr.Zero;
    private uint _startedAt; // timeGetTime when the device started; message stamps count from here

    public MidiInputService()
    {
        _callback = OnMidiMessage;
    }

    public event Action<MidiNoteMessage>? NoteReceived;

    public event Action<MidiControllerMessage>? ControllerReceived;

    // Raised for every short message from the device, notes or not. The argument is the packed message: status, data1, data2.
    public event Action<int>? MessageReceived;

    public bool IsOpen => _handle != IntPtr.Zero;

    // How long the last message waited between the device and this thread, in milliseconds.
    public int LastInputDelayMs { get; private set; }

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
        _startedAt = timeGetTime();
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
        // The device stamps each message (param2) with milliseconds since the device started.
        var packed = param1.ToInt64();
        LastInputDelayMs = unchecked((int)(timeGetTime() - _startedAt - (uint)param2.ToInt64()));
        MessageReceived?.Invoke((int)packed);

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
            default:
                if (ToController(status, data1, data2) is { } controller)
                {
                    ControllerReceived?.Invoke(controller);
                }
                break;
        }
    }

    // A control change, channel aftertouch or pitch bend as a controller with its value from 0 to 1. Null for
    // any other message.
    internal static MidiControllerMessage? ToController(int status, int data1, int data2)
    {
        var channel = status & 0x0F;
        return (status & 0xF0) switch
        {
            StatusControlChange => new MidiControllerMessage(channel, data1, data2 / 127.0),
            StatusChannelPressure => new MidiControllerMessage(channel, MidiControllerMessage.Aftertouch, data1 / 127.0),
            // 14 bits, least significant byte first: 8192 is the centre.
            StatusPitchBend => new MidiControllerMessage(channel, MidiControllerMessage.PitchBend, ((data2 << 7) | data1) / 16383.0),
            _ => null,
        };
    }

    private delegate void MidiInProc(IntPtr handle, uint message, IntPtr instance, IntPtr param1, IntPtr param2);

    [DllImport("winmm.dll")]
    private static extern uint timeGetTime();

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
