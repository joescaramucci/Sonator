#pragma once

#include <stdint.h>

// VstHostBridge.h
//
// C ABI exported by the native VstHostBridge.dll, consumed from managed code
// via P/Invoke (see Services/Vst3/Vst3HostInterop.cs in the Sonator project).
//
// STATUS: "step 2" of docs/VST3_HOSTING.md.
//   - Module loading: resolves the platform binary inside a .vst3 bundle
//     (Contents/x86_64-win/<name>.vst3), loads it with LoadLibraryW, calls
//     its exported "GetPluginFactory", enumerates the audio effect class and
//     instantiates IComponent + IEditController using the real (MIT
//     licensed) Steinberg VST3 "pluginterfaces" headers vendored under
//     native/VstHostBridge/thirdparty/pluginterfaces. Both are initialized
//     with a minimal internal IHostApplication implementation.
//   - Real plugin parameters (id, name, unit, default value) can now be read
//     via VstHostBridge_GetParameterCount/GetParameterInfo.
//   - Still NOT implemented: GUI hosting (IPlugView) and audio processing
//     (IAudioProcessor). Those need a bigger native effort (HwndHost + a
//     realtime audio engine) — see docs/VST3_HOSTING.md for the roadmap.
//
// See docs/VST3_HOSTING.md at the repository root for the full integration plan.

#ifdef VSTHOSTBRIDGE_EXPORTS
#define VSTHOSTBRIDGE_API extern "C" __declspec(dllexport)
#else
#define VSTHOSTBRIDGE_API extern "C" __declspec(dllimport)
#endif

// Returns the bridge API version.
//   0 = pure scaffold
//   1 = real module loading only (LoadLibrary + export check)
//   2 = real IComponent/IEditController instantiation + parameter reading
VSTHOSTBRIDGE_API int VstHostBridge_GetVersion();

// Audio load since the previous call, over every running plug-in: the share of each block's
// duration spent rendering it (1.0 = the whole block). outPeakLoad is the heaviest block,
// outAverageLoad the average. outDropouts counts the blocks that did not reach the device in
// time since the last reset. Returns 1 if any audio ran since the previous call, 0 otherwise.
VSTHOSTBRIDGE_API int VstHostBridge_GetDspLoad(double* outPeakLoad, double* outAverageLoad, int64_t* outDropouts);

// Sets the dropout count back to zero.
VSTHOSTBRIDGE_API void VstHostBridge_ResetDropouts(void);

// Attempts to load a VST3 module (a .vst3 file or bundle) at the given path,
// instantiate its first audio effect component and its edit controller.
// Returns an opaque module handle on success, or nullptr on failure
// (call VstHostBridge_GetLastError() for details).
VSTHOSTBRIDGE_API void* VstHostBridge_LoadModule(const wchar_t* filePath);

// Releases a module previously returned by VstHostBridge_LoadModule
// (terminates/releases the VST3 objects, then frees the library).
VSTHOSTBRIDGE_API void VstHostBridge_UnloadModule(void* moduleHandle);

// Returns a pointer to a static, null-terminated ANSI string describing the
// last error. The pointer is valid until the next bridge call on the same thread.
VSTHOSTBRIDGE_API const char* VstHostBridge_GetLastError();

// Copies the plugin's class name (as reported by the VST3 factory, e.g.
// "TAL-NoiseMaker") into outName (UTF-16, capacity in wchar_t units).
// Returns 1 on success, 0 on failure.
VSTHOSTBRIDGE_API int VstHostBridge_GetPluginDisplayName(void* moduleHandle, wchar_t* outName, int32_t nameCapacity);

// Returns the number of parameters exposed by the plugin's edit controller,
// or -1 if the module has no usable edit controller.
VSTHOSTBRIDGE_API int32_t VstHostBridge_GetParameterCount(void* moduleHandle);

// Fills information about the parameter at the given index (0-based).
// outTitle/outUnits are UTF-16 buffers with capacity in wchar_t units.
// Returns 1 on success, 0 on failure.
VSTHOSTBRIDGE_API int VstHostBridge_GetParameterInfo(
    void* moduleHandle,
    int32_t index,
    uint32_t* outId,
    wchar_t* outTitle,
    int32_t titleCapacity,
    wchar_t* outUnits,
    int32_t unitsCapacity,
    int32_t* outStepCount,
    double* outDefaultNormalized);

// Returns the current normalized value ([0,1]) of the parameter with the
// given id, or -1.0 if it could not be read.
VSTHOSTBRIDGE_API double VstHostBridge_GetParameterNormalized(void* moduleHandle, uint32_t paramId);

// Sets the normalized value ([0,1]) of the parameter with the given id.
// Returns 1 on success, 0 on failure (e.g. no usable edit controller).
VSTHOSTBRIDGE_API int VstHostBridge_SetParameterNormalized(void* moduleHandle, uint32_t paramId, double value);

// --- Real-time audio processing (IAudioProcessor + WASAPI output) --------

// Returns 1 if the plugin exposes a usable IAudioProcessor (VST3 requires
// this to live on the same object as IComponent) with at least one audio
// output bus, 0 otherwise.
VSTHOSTBRIDGE_API int VstHostBridge_HasAudioProcessor(void* moduleHandle);

// Starts real-time audio processing on a dedicated background thread and
// plays the plugin's rendered output through the Windows default audio
// output device (WASAPI, shared mode). This lets the plugin's own GUI (e.g.
// an on-screen preview keyboard) actually produce audible sound. Blocks
// briefly (a few seconds max) while the audio device/thread starts up.
// Returns 1 on success (call VstHostBridge_GetLastError() for details on
// failure). Safe to call again while already running (no-op, returns 1).
VSTHOSTBRIDGE_API int VstHostBridge_StartAudio(void* moduleHandle);

// Stops real-time audio processing and releases the audio device/thread.
// Safe to call even if audio isn't running. Also called automatically from
// VstHostBridge_UnloadModule if the caller forgot to stop it first.
VSTHOSTBRIDGE_API void VstHostBridge_StopAudio(void* moduleHandle);

// Queues a note for the plug-in of a running module. The plug-in hears it in the next block.
// isOn 0, or velocity 0, releases the note. Returns 1 if queued, 0 if audio is not running.
VSTHOSTBRIDGE_API int VstHostBridge_SendNoteEvent(void* moduleHandle, int32_t channel, int32_t pitch, int32_t velocity, int32_t isOn);

// Returns 1 if audio is currently running for this module, 0 otherwise.
VSTHOSTBRIDGE_API int VstHostBridge_IsAudioRunning(void* moduleHandle);

// Same as VstHostBridge_StartAudio, but opens the audio output device with
// the given endpoint ID (as returned by VstHostBridge_ListAudioOutputs). Pass
// nullptr or an empty string to use the Windows default output device.
// Returns 0 with an error message if no active device has that ID.
VSTHOSTBRIDGE_API int VstHostBridge_StartAudioOnDevice(void* moduleHandle, const wchar_t* deviceId);

// Lists the active audio render (output) devices. Each device is written as
// one line "<endpoint id>\t<friendly name>\n" in UTF-16. Call with buffer ==
// nullptr to get the required capacity in wchar_t (including the terminating
// null), then call again with a buffer of at least that size. Returns -1 on
// failure (see VstHostBridge_GetLastError).
VSTHOSTBRIDGE_API int VstHostBridge_ListAudioOutputs(wchar_t* buffer, int capacity);

// Returns 1 if this build includes the ASIO backend (compiled against the
// Steinberg ASIO SDK), 0 otherwise.
VSTHOSTBRIDGE_API int VstHostBridge_IsAsioSupported(void);

// Reports the timing of a running ASIO session: the driver's buffer size in
// frames, the output latency the driver declares in frames, and the sample
// rate. Returns 1 when the values are valid, 0 if this module is not playing
// through ASIO.
VSTHOSTBRIDGE_API int VstHostBridge_GetAsioTiming(void* moduleHandle, int32_t* outBufferFrames, int32_t* outOutputLatencyFrames, double* outSampleRate);

// Measures the loopback round trip on a running ASIO session: a click is
// written to output 1 and timed when it returns on input 1. Needs a cable
// from output 1 to input 1. Returns 1 and the round-trip time in milliseconds,
// or 0 with an error message (no signal, not an ASIO session, no input).
VSTHOSTBRIDGE_API int VstHostBridge_MeasureAsioLatency(void* moduleHandle, double* outMilliseconds);

// Reads what an ASIO driver supports: the buffer size range (min, max,
// preferred, granularity), the current sample rate, and the sample rates it
// accepts from a standard list (written to outRates, at most rateCapacity).
// The driver is opened briefly, so this fails while an ASIO session holds it.
VSTHOSTBRIDGE_API int VstHostBridge_GetAsioCapabilities(const wchar_t* driverClsid, int32_t* outMinBuffer, int32_t* outMaxBuffer,
    int32_t* outPreferredBuffer, int32_t* outGranularity, double* outCurrentRate, double* outRates,
    int32_t rateCapacity, int32_t* outRateCount);

// Opens the driver's own configuration panel (for example the SSL control panel).
VSTHOSTBRIDGE_API int VstHostBridge_OpenAsioControlPanel(const wchar_t* driverClsid);

// Like VstHostBridge_StartAudioAsio, with the buffer size in frames and the
// sample rate chosen by the user. 0 for either keeps the driver's own choice.
VSTHOSTBRIDGE_API int VstHostBridge_StartAudioAsioWithFormat(void* moduleHandle, const wchar_t* driverClsid, int32_t bufferFrames, double sampleRate);

// Reports the latencies the driver declares for a buffer size and sample rate,
// without starting audio: buffers are created briefly and released again. A
// buffer size of 0 uses the driver's preferred size; a rate of 0 keeps the current rate.
// Fails while an ASIO session holds the driver.
VSTHOSTBRIDGE_API int VstHostBridge_GetAsioLatency(const wchar_t* driverClsid, int32_t bufferFrames, double sampleRate, int32_t* outInputLatency, int32_t* outOutputLatency);

// Starts plug-in audio on the ASIO driver whose CLSID string is driverClsid
// (for example "{...}", as stored under HKLM\SOFTWARE\ASIO). Returns 0 with an
// error message if ASIO is not built in or the driver cannot be started.
VSTHOSTBRIDGE_API int VstHostBridge_StartAudioAsio(void* moduleHandle, const wchar_t* driverClsid);

// --- Native GUI hosting (IPlugView), Windows/HWND only -------------------

// Returns 1 if the plugin exposes a native "editor" view that supports HWND
// embedding (Windows), 0 otherwise (no editor, or unsupported platform type).
VSTHOSTBRIDGE_API int VstHostBridge_HasEditor(void* moduleHandle);

// Creates (if not already open) the plugin's native editor view and embeds
// it as a child window of parentHwnd. On success returns 1 and fills:
//   outChildHwnd  - HWND created/owned by the bridge; the caller should
//                   treat it as a foreign child window (e.g. return it from
//                   a WPF HwndHost.BuildWindowCore) and must NOT destroy it
//                   directly - use VstHostBridge_CloseEditor instead.
//   outWidth/outHeight - the view's current size in pixels.
// Calling this again while already open just returns the existing handle
// and current size (no-op).
VSTHOSTBRIDGE_API int VstHostBridge_OpenEditor(void* moduleHandle, void* parentHwnd, void** outChildHwnd, int32_t* outWidth, int32_t* outHeight);

// Detaches and destroys the editor view/window previously created by
// VstHostBridge_OpenEditor. Safe to call even if no editor is currently
// open. Also called automatically from VstHostBridge_UnloadModule if the
// caller forgot to close it first.
VSTHOSTBRIDGE_API void VstHostBridge_CloseEditor(void* moduleHandle);

// Returns the editor's current size in outWidth/outHeight (updated if the
// plugin requested a resize via IPlugFrame::resizeView). Returns 1 if an
// editor is currently open, 0 otherwise.
VSTHOSTBRIDGE_API int VstHostBridge_GetEditorSize(void* moduleHandle, int32_t* outWidth, int32_t* outHeight);
