using System.Runtime.InteropServices;

namespace Sonator.Services;

internal sealed class WindowsMidiOutput : IDisposable
{
    private const uint MidiMapper = 0xFFFFFFFF;
    private IntPtr _handle = IntPtr.Zero;

    public WindowsMidiOutput(uint? deviceId = null)
    {
        var result = midiOutOpen(out _handle, deviceId ?? MidiMapper, 0, 0, 0);
        if (result != 0)
        {
            throw new InvalidOperationException($"Unable to open Windows MIDI output device. WinMM error {result}.");
        }
    }

    public void NoteOn(int channel, int noteNumber, int velocity)
    {
        SendShortMessage(0x90 | (channel & 0x0F), noteNumber, velocity);
    }

    public void NoteOff(int channel, int noteNumber, int velocity = 0)
    {
        SendShortMessage(0x80 | (channel & 0x0F), noteNumber, velocity);
    }

    public void AllNotesOff(int channel)
    {
        SendShortMessage(0xB0 | (channel & 0x0F), 123, 0);
    }

    public void ProgramChange(int channel, int programNumber)
    {
        SendShortMessage(0xC0 | (channel & 0x0F), programNumber, 0);
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            midiOutReset(_handle);
            midiOutClose(_handle);
            _handle = IntPtr.Zero;
        }
    }

    private void SendShortMessage(int status, int data1, int data2)
    {
        var message = (uint)((status & 0xFF) | ((data1 & 0xFF) << 8) | ((data2 & 0xFF) << 16));
        var result = midiOutShortMsg(_handle, message);
        if (result != 0)
        {
            throw new InvalidOperationException($"Unable to send MIDI message. WinMM error {result}.");
        }
    }

    [DllImport("winmm.dll")]
    private static extern int midiOutOpen(out IntPtr lphmo, uint uDeviceID, uint dwCallback, uint dwInstance, uint dwFlags);

    [DllImport("winmm.dll")]
    private static extern int midiOutShortMsg(IntPtr hmo, uint dwMsg);

    [DllImport("winmm.dll")]
    private static extern int midiOutReset(IntPtr hmo);

    [DllImport("winmm.dll")]
    private static extern int midiOutClose(IntPtr hmo);
}
