using System.Runtime.InteropServices;
using System.Text;

namespace Sonator.Services.Vst3;

/// <summary>
/// Raw P/Invoke declarations for the native VstHostBridge.dll (see
/// native/VstHostBridge in the repository root). The bridge performs real
/// module loading (VST3 bundle resolution + LoadLibrary + "GetPluginFactory"
/// export check), instantiates IComponent/IEditController via the real
/// (MIT-licensed) Steinberg VST3 "pluginterfaces" headers, and exposes the
/// plugin's real parameter list (id, title, units, default/current value).
/// GUI hosting (IPlugView) and audio processing (IAudioProcessor) are not
/// implemented yet — see docs/VST3_HOSTING.md for the roadmap. If the DLL is
/// missing (not yet built/deployed), calls fail with DllNotFoundException,
/// which Vst3PluginHost catches and reports as a friendly error.
/// </summary>
internal static class Vst3HostInterop
{
    private const string LibraryName = "VstHostBridge.dll";

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int VstHostBridge_GetVersion();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int VstHostBridge_GetDspLoad(out double peakLoad, out double averageLoad, out long dropouts);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void VstHostBridge_ResetDropouts();

    // ---- Sonator's insert effects (native/VstHostBridge/src/SonEffects.h) ----

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr VstHostBridge_CreateEffectChain();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void VstHostBridge_DestroyEffectChain(IntPtr chain);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void VstHostBridge_SetEffectChainLayout(IntPtr chain, int[] types, int count);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void VstHostBridge_SetEffectParameter(IntPtr chain, int slot, int parameter, double value);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void VstHostBridge_SetEffectEnabled(IntPtr chain, int slot, int enabled);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void VstHostBridge_SetEffectChainGain(IntPtr chain, double gain);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void VstHostBridge_SetEffectChainPan(IntPtr chain, double pan);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void VstHostBridge_SetEffectChainLayoutWithPlugins(IntPtr chain, int[] types, IntPtr[] modules, int count);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int VstHostBridge_GetPluginKind(IntPtr module);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void VstHostBridge_SetMasterEffectChain(IntPtr chain);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void VstHostBridge_SetEffectChainSend(IntPtr chain, int bus, double level);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int VstHostBridge_TakeEffectChainStereoPeak(IntPtr chain, out double left, out double right);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void VstHostBridge_SetBus(int bus, IntPtr chain);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern double VstHostBridge_GetEffectMeter(IntPtr chain, int slot);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern double VstHostBridge_TakeEffectChainPeak(IntPtr chain);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int VstHostBridge_TakeEffectLevels(IntPtr chain, int slot, out double inputPeak, out double outputPeak);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void VstHostBridge_ProcessEffectChain(IntPtr chain, float[] interleaved, int frames, int channels, double sampleRate);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int VstHostBridge_AttachEffectChain(IntPtr module, IntPtr chain);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    internal static extern IntPtr VstHostBridge_LoadModule(string filePath);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void VstHostBridge_UnloadModule(IntPtr moduleHandle);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr VstHostBridge_GetLastError();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    internal static extern int VstHostBridge_GetPluginDisplayName(IntPtr moduleHandle, StringBuilder outName, int nameCapacity);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int VstHostBridge_GetParameterCount(IntPtr moduleHandle);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    internal static extern int VstHostBridge_GetParameterInfo(
        IntPtr moduleHandle,
        int index,
        out uint outId,
        StringBuilder outTitle,
        int titleCapacity,
        StringBuilder outUnits,
        int unitsCapacity,
        out int outStepCount,
        out double outDefaultNormalized);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern double VstHostBridge_GetParameterNormalized(IntPtr moduleHandle, uint paramId);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int VstHostBridge_SetParameterNormalized(IntPtr moduleHandle, uint paramId, double value);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int VstHostBridge_HasEditor(IntPtr moduleHandle);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int VstHostBridge_OpenEditor(IntPtr moduleHandle, IntPtr parentHwnd, out IntPtr outChildHwnd, out int outWidth, out int outHeight);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void VstHostBridge_CloseEditor(IntPtr moduleHandle);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int VstHostBridge_GetEditorSize(IntPtr moduleHandle, out int outWidth, out int outHeight);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int VstHostBridge_HasAudioProcessor(IntPtr moduleHandle);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int VstHostBridge_StartAudio(IntPtr moduleHandle);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void VstHostBridge_StopAudio(IntPtr moduleHandle);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int VstHostBridge_SendNoteEvent(IntPtr moduleHandle, int channel, int pitch, int velocity, int isOn);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int VstHostBridge_ReleaseAllNotes(IntPtr moduleHandle);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int VstHostBridge_SendController(IntPtr moduleHandle, int channel, int controller, double value);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int VstHostBridge_StartMixCapture(double maxSeconds);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern long VstHostBridge_StopMixCapture(out double sampleRate);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int VstHostBridge_ReadMixCapture(float[] interleaved, long frames);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr VstHostBridge_CreateClipModule(float[] interleaved, int frames, int channels, double sampleRate, double startSeconds);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int VstHostBridge_SaveState(IntPtr moduleHandle, int part);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int VstHostBridge_ReadSavedState(IntPtr moduleHandle, byte[] buffer, int capacity);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int VstHostBridge_LoadState(IntPtr moduleHandle, int part, byte[] data, int size);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int VstHostBridge_IsAudioRunning(IntPtr moduleHandle);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    internal static extern int VstHostBridge_StartAudioOnDevice(IntPtr moduleHandle, string? deviceId);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    internal static extern int VstHostBridge_ListAudioOutputs(StringBuilder? buffer, int capacity);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int VstHostBridge_IsAsioSupported();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int VstHostBridge_GetAsioTiming(IntPtr moduleHandle, out int outBufferFrames, out int outOutputLatencyFrames, out double outSampleRate);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int VstHostBridge_MeasureAsioLatency(IntPtr moduleHandle, out double outMilliseconds);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    internal static extern int VstHostBridge_GetAsioCapabilities(
        string driverClsid,
        out int outMinBuffer,
        out int outMaxBuffer,
        out int outPreferredBuffer,
        out int outGranularity,
        out double outCurrentRate,
        double[] outRates,
        int rateCapacity,
        out int outRateCount);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    internal static extern int VstHostBridge_OpenAsioControlPanel(string driverClsid);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    internal static extern int VstHostBridge_StartAudioAsioWithFormat(IntPtr moduleHandle, string driverClsid, int bufferFrames, double sampleRate);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    internal static extern int VstHostBridge_GetAsioLatency(string driverClsid, int bufferFrames, double sampleRate, out int outInputLatency, out int outOutputLatency);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    internal static extern int VstHostBridge_StartAudioAsio(IntPtr moduleHandle, string driverClsid);
}
