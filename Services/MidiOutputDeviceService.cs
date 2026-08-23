using System.Runtime.InteropServices;

namespace Sonator.Services;

internal sealed class MidiOutputDeviceService
{
    public IReadOnlyList<(uint? DeviceId, string DisplayName)> GetOutputDevices()
    {
        var devices = new List<(uint? DeviceId, string DisplayName)>
        {
            (null, "MIDI Mapper")
        };

        var count = midiOutGetNumDevs();
        for (uint i = 0; i < count; i++)
        {
            if (midiOutGetDevCaps(i, out var caps, (uint)Marshal.SizeOf<MidiOutCaps>()) == 0)
            {
                devices.Add((i, caps.ProductName));
            }
        }

        return devices;
    }

    [DllImport("winmm.dll")]
    private static extern uint midiOutGetNumDevs();

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern int midiOutGetDevCaps(uint uDeviceID, out MidiOutCaps pmoc, uint cbmoc);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MidiOutCaps
    {
        public ushort ManufacturerId;
        public ushort ProductId;
        public uint DriverVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string ProductName;

        public ushort Technology;
        public ushort Voices;
        public ushort Notes;
        public ushort ChannelMask;
        public uint Support;
    }
}
