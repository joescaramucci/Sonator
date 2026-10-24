using Sonator.Services;
using System.Runtime.InteropServices;
using System.Text;

namespace Sonator.Services.Vst3;

/// <summary>
/// Default <see cref="IVst3PluginHost"/> implementation backed by the native
/// VstHostBridge.dll. See native/VstHostBridge and docs/VST3_HOSTING.md for
/// the current (scaffold-only) status and the roadmap towards real VST3
/// hosting. This class is written defensively so Sonator keeps working
/// normally when the native DLL is missing (e.g. it hasn't been built yet):
/// every native call is guarded and turned into a friendly error message.
/// </summary>
public sealed class Vst3PluginHost : IVst3PluginHost
{
    private bool? _nativeBridgeAvailable;

    public bool IsNativeBridgeAvailable => _nativeBridgeAvailable ??= ProbeNativeBridge();

    public Vst3ModuleHandle? TryLoadModule(string filePath, out string? error)
    {
        if (!IsNativeBridgeAvailable)
        {
            error = "The native VstHostBridge.dll is missing or incompatible. " +
                    "Consulta docs/VST3_HOSTING.md per compilarlo e collegare l'SDK VST3 di Steinberg.";
            return null;
        }

        try
        {
            var nativeHandle = Vst3HostInterop.VstHostBridge_LoadModule(filePath);
            if (nativeHandle == IntPtr.Zero)
            {
                error = ReadLastError();
                return null;
            }

            error = null;
            return new Vst3ModuleHandle(nativeHandle, filePath);
        }
        catch (Exception ex) when (IsBridgeUnavailableException(ex))
        {
            error = $"The native VST3 bridge cannot be used: {ex.Message}";
            return null;
        }
    }

    public void UnloadModule(Vst3ModuleHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);

        if (handle.NativeHandle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            Vst3HostInterop.VstHostBridge_UnloadModule(handle.NativeHandle);
        }
        catch (Exception ex) when (IsBridgeUnavailableException(ex))
        {
            // Bridge unavailable: nothing left to release on the managed side.
        }
    }

    public string? GetPluginDisplayName(Vst3ModuleHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);

        if (handle.NativeHandle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var buffer = new StringBuilder(256);
            var ok = Vst3HostInterop.VstHostBridge_GetPluginDisplayName(handle.NativeHandle, buffer, buffer.Capacity);
            return ok != 0 ? buffer.ToString() : null;
        }
        catch (Exception ex) when (IsBridgeUnavailableException(ex))
        {
            return null;
        }
    }

    public IReadOnlyList<Vst3ParameterInfo> GetParameters(Vst3ModuleHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);

        var parameters = new List<Vst3ParameterInfo>();
        if (handle.NativeHandle == IntPtr.Zero)
        {
            return parameters;
        }

        try
        {
            var count = Vst3HostInterop.VstHostBridge_GetParameterCount(handle.NativeHandle);
            if (count <= 0)
            {
                return parameters;
            }

            var title = new StringBuilder(256);
            var units = new StringBuilder(256);
            for (var i = 0; i < count; i++)
            {
                var ok = Vst3HostInterop.VstHostBridge_GetParameterInfo(
                    handle.NativeHandle, i, out var id, title, title.Capacity, units, units.Capacity,
                    out var stepCount, out var defaultNormalized);
                if (ok == 0)
                {
                    continue;
                }

                var currentNormalized = Vst3HostInterop.VstHostBridge_GetParameterNormalized(handle.NativeHandle, id);
                parameters.Add(new Vst3ParameterInfo(id, title.ToString(), units.ToString(), stepCount, defaultNormalized, currentNormalized));
            }
        }
        catch (Exception ex) when (IsBridgeUnavailableException(ex))
        {
            // Bridge unavailable: report whatever was gathered so far (likely nothing).
        }

        return parameters;
    }

    public bool SetParameterValue(Vst3ModuleHandle handle, uint parameterId, double normalizedValue)
    {
        ArgumentNullException.ThrowIfNull(handle);

        if (handle.NativeHandle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            return Vst3HostInterop.VstHostBridge_SetParameterNormalized(handle.NativeHandle, parameterId, normalizedValue) != 0;
        }
        catch (Exception ex) when (IsBridgeUnavailableException(ex))
        {
            return false;
        }
    }

    public bool HasNativeEditor(Vst3ModuleHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);

        if (handle.NativeHandle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            return Vst3HostInterop.VstHostBridge_HasEditor(handle.NativeHandle) != 0;
        }
        catch (Exception ex) when (IsBridgeUnavailableException(ex))
        {
            return false;
        }
    }

    public bool TryOpenNativeEditor(Vst3ModuleHandle handle, IntPtr parentHwnd, out IntPtr childHwnd, out int width, out int height, out string? error)
    {
        ArgumentNullException.ThrowIfNull(handle);

        childHwnd = IntPtr.Zero;
        width = 0;
        height = 0;

        if (handle.NativeHandle == IntPtr.Zero)
        {
            error = "VST3 module not loaded.";
            return false;
        }

        try
        {
            var ok = Vst3HostInterop.VstHostBridge_OpenEditor(handle.NativeHandle, parentHwnd, out childHwnd, out width, out height);
            if (ok == 0)
            {
                error = ReadLastError();
                return false;
            }

            error = null;
            return true;
        }
        catch (Exception ex) when (IsBridgeUnavailableException(ex))
        {
            error = $"The native VST3 bridge cannot be used: {ex.Message}";
            return false;
        }
    }

    public void CloseNativeEditor(Vst3ModuleHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);

        if (handle.NativeHandle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            Vst3HostInterop.VstHostBridge_CloseEditor(handle.NativeHandle);
        }
        catch (Exception ex) when (IsBridgeUnavailableException(ex))
        {
            // Bridge unavailable: nothing left to release on the managed side.
        }
    }

    public bool HasAudioProcessor(Vst3ModuleHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);

        if (handle.NativeHandle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            return Vst3HostInterop.VstHostBridge_HasAudioProcessor(handle.NativeHandle) != 0;
        }
        catch (Exception ex) when (IsBridgeUnavailableException(ex))
        {
            return false;
        }
    }

    public IReadOnlyList<string> OutputNames(Vst3ModuleHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (handle.NativeHandle == IntPtr.Zero)
        {
            return Array.Empty<string>();
        }

        try
        {
            var count = Vst3HostInterop.VstHostBridge_GetOutputBusCount(handle.NativeHandle);
            var names = new List<string>();
            for (var bus = 0; bus < count; bus++)
            {
                var name = new System.Text.StringBuilder(256);
                names.Add(Vst3HostInterop.VstHostBridge_GetOutputBusName(handle.NativeHandle, bus, name, name.Capacity) != 0
                    ? name.ToString()
                    : $"Out {bus + 1}");
            }

            return names;
        }
        catch (Exception ex) when (IsBridgeUnavailableException(ex))
        {
            return Array.Empty<string>();
        }
    }

    public PluginKind GetPluginKind(Vst3ModuleHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);

        if (handle.NativeHandle == IntPtr.Zero)
        {
            return PluginKind.Unknown;
        }

        try
        {
            return (PluginKind)Vst3HostInterop.VstHostBridge_GetPluginKind(handle.NativeHandle);
        }
        catch (Exception ex) when (IsBridgeUnavailableException(ex))
        {
            return PluginKind.Unknown;
        }
    }

    public bool TryStartAudio(Vst3ModuleHandle handle, AudioOutputSelection selection, out string? error)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(selection);

        if (handle.NativeHandle == IntPtr.Zero)
        {
            error = "VST3 module not loaded.";
            return false;
        }

        if (selection.UseAsio && selection.AsioDriverClsid is null)
        {
            error = "No ASIO driver is selected.";
            return false;
        }

        try
        {
            // Every plug-in started joins the bridge's one audio output, so they all play together.
            if (selection.UseAsio && Vst3HostInterop.VstHostBridge_IsAsioSupported() == 0)
            {
                error = "This build of the VST3 bridge does not include ASIO support.";
                return false;
            }

            var ok = selection.UseAsio
                ? Vst3HostInterop.VstHostBridge_StartAudioAsioWithFormat(
                    handle.NativeHandle, selection.AsioDriverClsid!, selection.AsioBufferFrames, selection.AsioSampleRate)
                : Vst3HostInterop.VstHostBridge_StartAudioOnDevice(handle.NativeHandle, selection.WasapiDeviceId);
            if (ok == 0)
            {
                error = ReadLastError();
                return false;
            }

            if (selection.UseAsio)
            {
                AsioSessionRegistry.Set(handle);
            }

            error = null;
            return true;
        }
        catch (Exception ex) when (IsBridgeUnavailableException(ex))
        {
            error = $"The native VST3 bridge cannot be used: {ex.Message}";
            return false;
        }
    }

    public bool TrySendNote(Vst3ModuleHandle handle, int channel, int noteNumber, int velocity, bool isOn)
    {
        ArgumentNullException.ThrowIfNull(handle);

        if (handle.NativeHandle == IntPtr.Zero)
        {
            return false;
        }

        return Vst3HostInterop.VstHostBridge_SendNoteEvent(handle.NativeHandle, channel, noteNumber, velocity, isOn ? 1 : 0) != 0;
    }

    public bool TrySendController(Vst3ModuleHandle handle, int channel, int controller, double value)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (handle.NativeHandle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            return Vst3HostInterop.VstHostBridge_SendController(handle.NativeHandle, channel, controller, value) != 0;
        }
        catch (Exception ex) when (IsBridgeUnavailableException(ex))
        {
            return false;
        }
    }

    public bool TryStartMixCapture(double maxSeconds)
    {
        try
        {
            return IsNativeBridgeAvailable && Vst3HostInterop.VstHostBridge_StartMixCapture(maxSeconds) != 0;
        }
        catch (Exception ex) when (IsBridgeUnavailableException(ex))
        {
            return false;
        }
    }

    public float[]? StopMixCapture(out int sampleRate)
    {
        sampleRate = 0;
        try
        {
            var frames = Vst3HostInterop.VstHostBridge_StopMixCapture(out var rate);
            sampleRate = (int)Math.Round(rate);
            if (frames <= 0 || sampleRate <= 0)
            {
                return null;
            }

            var samples = new float[frames * 2];
            return Vst3HostInterop.VstHostBridge_ReadMixCapture(samples, frames) != 0 ? samples : null;
        }
        catch (Exception ex) when (IsBridgeUnavailableException(ex))
        {
            return null;
        }
    }

    public Vst3ModuleHandle? TryCreateClip(float[] interleaved, int channels, int sampleRate, double startSeconds)
    {
        ArgumentNullException.ThrowIfNull(interleaved);

        var frames = channels > 0 ? interleaved.Length / channels : 0;
        if (!IsNativeBridgeAvailable || frames == 0)
        {
            return null;
        }

        try
        {
            var nativeHandle = Vst3HostInterop.VstHostBridge_CreateClipModule(interleaved, frames, channels, sampleRate, startSeconds);
            return nativeHandle == IntPtr.Zero ? null : new Vst3ModuleHandle(nativeHandle, "audio clip");
        }
        catch (Exception ex) when (IsBridgeUnavailableException(ex))
        {
            return null;
        }
    }

    public void SetClipPlayback(Vst3ModuleHandle handle, bool loops, double gain)
    {
        ArgumentNullException.ThrowIfNull(handle);

        if (handle.NativeHandle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            Vst3HostInterop.VstHostBridge_SetClipPlayback(handle.NativeHandle, loops ? 1 : 0, (float)gain);
        }
        catch (Exception ex) when (IsBridgeUnavailableException(ex))
        {
            // Without the bridge no clip plays at all.
        }
    }

    public void SeekClip(Vst3ModuleHandle handle, double seconds)
    {
        ArgumentNullException.ThrowIfNull(handle);

        if (handle.NativeHandle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            Vst3HostInterop.VstHostBridge_SeekClip(handle.NativeHandle, seconds);
        }
        catch (Exception ex) when (IsBridgeUnavailableException(ex))
        {
            // Without the bridge no clip plays at all.
        }
    }

    public void SetClipMonitor(Vst3ModuleHandle handle, bool monitorOnly, double meterPeak)
    {
        ArgumentNullException.ThrowIfNull(handle);

        if (handle.NativeHandle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            Vst3HostInterop.VstHostBridge_SetClipMonitor(handle.NativeHandle, monitorOnly ? 1 : 0, (float)meterPeak);
        }
        catch (Exception ex) when (IsBridgeUnavailableException(ex))
        {
            // Without the bridge no clip plays at all.
        }
    }

    public ulong GetInputPosition() => CallBridge(() => Vst3HostInterop.VstHostBridge_GetInputPosition(), 0UL);

    public int ReadInput(float[] buffer, ref ulong position, out double sampleRate, out int lostFrames)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        sampleRate = 0;
        lostFrames = 0;
        if (!IsNativeBridgeAvailable)
        {
            return 0;
        }

        try
        {
            return Vst3HostInterop.VstHostBridge_ReadInput(buffer, buffer.Length, ref position, out sampleRate, out lostFrames);
        }
        catch (Exception ex) when (IsBridgeUnavailableException(ex))
        {
            return 0;
        }
    }

    public double TakeInputPeak() => CallBridge(() => (double)Vst3HostInterop.VstHostBridge_TakeInputPeak(), 0.0);

    public bool TryGetAsioInputState(out bool hasInput, out int inputLatencyFrames, out int outputLatencyFrames)
    {
        hasInput = false;
        inputLatencyFrames = 0;
        outputLatencyFrames = 0;
        if (!IsNativeBridgeAvailable)
        {
            return false;
        }

        try
        {
            if (Vst3HostInterop.VstHostBridge_GetAsioInputState(out var input, out inputLatencyFrames, out outputLatencyFrames) == 0)
            {
                return false;
            }

            hasInput = input != 0;
            return true;
        }
        catch (Exception ex) when (IsBridgeUnavailableException(ex))
        {
            return false;
        }
    }

    private T CallBridge<T>(Func<T> call, T fallback)
    {
        if (!IsNativeBridgeAvailable)
        {
            return fallback;
        }

        try
        {
            return call();
        }
        catch (Exception ex) when (IsBridgeUnavailableException(ex))
        {
            return fallback;
        }
    }

    public int ReadOutputScope(float[] buffer, out double sampleRate)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        sampleRate = 0;
        if (!IsNativeBridgeAvailable)
        {
            return 0;
        }

        try
        {
            return Vst3HostInterop.VstHostBridge_ReadOutputScope(buffer, buffer.Length, out sampleRate);
        }
        catch (Exception ex) when (IsBridgeUnavailableException(ex))
        {
            return 0;
        }
    }

    public byte[]? TrySaveState(Vst3ModuleHandle handle, int part)
    {
        ArgumentNullException.ThrowIfNull(handle);

        if (handle.NativeHandle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var size = Vst3HostInterop.VstHostBridge_SaveState(handle.NativeHandle, part);
            if (size < 0)
            {
                return null;
            }

            var state = new byte[size];
            return Vst3HostInterop.VstHostBridge_ReadSavedState(handle.NativeHandle, state, size) != 0 ? state : null;
        }
        catch (Exception ex) when (IsBridgeUnavailableException(ex))
        {
            return null;
        }
    }

    public bool TryLoadState(Vst3ModuleHandle handle, int part, byte[] state)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(state);

        if (handle.NativeHandle == IntPtr.Zero || state.Length == 0)
        {
            return false;
        }

        try
        {
            return Vst3HostInterop.VstHostBridge_LoadState(handle.NativeHandle, part, state, state.Length) != 0;
        }
        catch (Exception ex) when (IsBridgeUnavailableException(ex))
        {
            return false;
        }
    }

    public void ReleaseAllNotes(Vst3ModuleHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);

        if (handle.NativeHandle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            Vst3HostInterop.VstHostBridge_ReleaseAllNotes(handle.NativeHandle);
        }
        catch (Exception ex) when (IsBridgeUnavailableException(ex))
        {
            // Bridge unavailable: no plug-in is sounding.
        }
    }

    public void StopAudio(Vst3ModuleHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);

        if (handle.NativeHandle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            Vst3HostInterop.VstHostBridge_StopAudio(handle.NativeHandle);
            AsioSessionRegistry.Clear(handle);
        }
        catch (Exception ex) when (IsBridgeUnavailableException(ex))
        {
            // Bridge unavailable: nothing left to release on the managed side.
        }
    }

    public bool IsAudioRunning(Vst3ModuleHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);

        if (handle.NativeHandle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            return Vst3HostInterop.VstHostBridge_IsAudioRunning(handle.NativeHandle) != 0;
        }
        catch (Exception ex) when (IsBridgeUnavailableException(ex))
        {
            return false;
        }
    }

    public bool TryMeasureAsioLatency(Vst3ModuleHandle handle, out double milliseconds, out string? error)
    {
        ArgumentNullException.ThrowIfNull(handle);

        milliseconds = 0;
        if (handle.NativeHandle == IntPtr.Zero)
        {
            error = "VST3 module not loaded.";
            return false;
        }

        try
        {
            if (Vst3HostInterop.VstHostBridge_MeasureAsioLatency(handle.NativeHandle, out milliseconds) != 0)
            {
                error = null;
                return true;
            }

            error = ReadLastError();
            return false;
        }
        catch (Exception ex) when (IsBridgeUnavailableException(ex))
        {
            error = $"The native VST3 bridge cannot be used: {ex.Message}";
            return false;
        }
    }

    public bool TryGetAsioTiming(Vst3ModuleHandle handle, out int bufferFrames, out int outputLatencyFrames, out double sampleRate)
    {
        ArgumentNullException.ThrowIfNull(handle);

        bufferFrames = 0;
        outputLatencyFrames = 0;
        sampleRate = 0;
        if (handle.NativeHandle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            return Vst3HostInterop.VstHostBridge_GetAsioTiming(handle.NativeHandle, out bufferFrames, out outputLatencyFrames, out sampleRate) != 0;
        }
        catch (Exception ex) when (IsBridgeUnavailableException(ex))
        {
            return false;
        }
    }

    private static bool ProbeNativeBridge()
    {
        try
        {
            _ = Vst3HostInterop.VstHostBridge_GetVersion();
            return true;
        }
        catch (Exception ex) when (IsBridgeUnavailableException(ex))
        {
            return false;
        }
    }

    private static string ReadLastError()
    {
        try
        {
            var pointer = Vst3HostInterop.VstHostBridge_GetLastError();
            return pointer == IntPtr.Zero
                ? "Unknown VST3 bridge error."
                : Marshal.PtrToStringUTF8(pointer) ?? "Unknown VST3 bridge error.";
        }
        catch (Exception ex) when (IsBridgeUnavailableException(ex))
        {
            return "Native VST3 bridge unavailable.";
        }
    }

    private static bool IsBridgeUnavailableException(Exception ex) =>
        ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException;
}
