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
    internal static extern int VstHostBridge_IsAudioRunning(IntPtr moduleHandle);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    internal static extern int VstHostBridge_StartAudioOnDevice(IntPtr moduleHandle, string? deviceId);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    internal static extern int VstHostBridge_ListAudioOutputs(StringBuilder? buffer, int capacity);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int VstHostBridge_IsAsioSupported();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    internal static extern int VstHostBridge_StartAudioAsio(IntPtr moduleHandle, string driverClsid);
}
