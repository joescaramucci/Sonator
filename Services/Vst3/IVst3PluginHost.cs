using Sonator.Services;
namespace Sonator.Services.Vst3;

/// <summary>
/// Opaque handle to a loaded VST3 module, returned by
/// <see cref="IVst3PluginHost.TryLoadModule"/>.
/// </summary>
public sealed class Vst3ModuleHandle
{
    internal Vst3ModuleHandle(IntPtr nativeHandle, string filePath)
    {
        NativeHandle = nativeHandle;
        FilePath = filePath;
    }

    internal IntPtr NativeHandle { get; }

    public string FilePath { get; }
}

/// <summary>
/// A single real parameter exposed by a loaded VST3 plugin's edit controller
/// (title/units/id come straight from the plugin, via
/// IEditController::getParameterInfo).
/// </summary>
public sealed class Vst3ParameterInfo
{
    public Vst3ParameterInfo(uint id, string title, string units, int stepCount, double defaultNormalized, double currentNormalized)
    {
        Id = id;
        Title = title;
        Units = units;
        StepCount = stepCount;
        DefaultNormalized = defaultNormalized;
        CurrentNormalized = currentNormalized;
    }

    public uint Id { get; }
    public string Title { get; }
    public string Units { get; }
    public int StepCount { get; }
    public double DefaultNormalized { get; }
    public double CurrentNormalized { get; }
}

/// <summary>
/// Managed-side abstraction over the native VST3 hosting bridge
/// (<see cref="Vst3PluginHost"/>). The bridge performs real module loading
/// and real IComponent/IEditController instantiation via the MIT-licensed
/// Steinberg "pluginterfaces" headers, and can read the plugin's actual
/// parameter list. GUI hosting (IPlugView) and audio processing
/// (IAudioProcessor) are not implemented yet — see docs/VST3_HOSTING.md.
/// </summary>
public interface IVst3PluginHost
{
    /// <summary>
    /// True if the native VstHostBridge.dll could be located and its exported
    /// functions are callable. Does not imply real VST3 hosting is available —
    /// only that the bridge module itself is present.
    /// </summary>
    bool IsNativeBridgeAvailable { get; }

    /// <summary>
    /// Attempts to load a VST3 module from disk. Returns null and sets
    /// <paramref name="error"/> with a user-facing explanation on failure
    /// (including the expected "not implemented yet" case for the scaffold).
    /// </summary>
    Vst3ModuleHandle? TryLoadModule(string filePath, out string? error);

    /// <summary>
    /// Releases a module previously returned by <see cref="TryLoadModule"/>.
    /// </summary>
    void UnloadModule(Vst3ModuleHandle handle);

    /// <summary>
    /// Returns the plugin's display name as reported by its VST3 factory
    /// (e.g. "TAL-NoiseMaker"), or null if it could not be read.
    /// </summary>
    string? GetPluginDisplayName(Vst3ModuleHandle handle);

    /// <summary>
    /// Returns the real parameter list exposed by the plugin's edit
    /// controller. Empty if the module has no usable edit controller.
    /// </summary>
    IReadOnlyList<Vst3ParameterInfo> GetParameters(Vst3ModuleHandle handle);

    /// <summary>
    /// Sets the normalized value ([0,1]) of a real plugin parameter.
    /// Returns true on success.
    /// </summary>
    bool SetParameterValue(Vst3ModuleHandle handle, uint parameterId, double normalizedValue);

    /// <summary>
    /// True if the plugin exposes a native editor view (IPlugView) that can
    /// be embedded as a Win32 HWND.
    /// </summary>
    bool HasNativeEditor(Vst3ModuleHandle handle);

    /// <summary>
    /// Creates (if needed) and attaches the plugin's native editor view as a
    /// child window of <paramref name="parentHwnd"/>. On success returns the
    /// HWND of the bridge-owned container window (to be reparented/embedded
    /// by the caller, e.g. via a WPF HwndHost) plus its current size in
    /// pixels. Call <see cref="CloseNativeEditor"/> to detach.
    /// </summary>
    bool TryOpenNativeEditor(Vst3ModuleHandle handle, IntPtr parentHwnd, out IntPtr childHwnd, out int width, out int height, out string? error);

    /// <summary>
    /// Detaches and destroys the editor view previously opened with
    /// <see cref="TryOpenNativeEditor"/>. Safe to call even if not open.
    /// </summary>
    void CloseNativeEditor(Vst3ModuleHandle handle);

    /// <summary>
    /// True if the plugin exposes a usable IAudioProcessor (required for
    /// real-time audio output).
    /// </summary>
    bool HasAudioProcessor(Vst3ModuleHandle handle);

    /// <summary>
    /// Whether the plug-in is an instrument or an effect, from its categories or, without them, its buses.
    /// </summary>
    PluginKind GetPluginKind(Vst3ModuleHandle handle);

    /// <summary>
    /// Starts real-time audio processing and plays the plugin's output
    /// through the given Windows audio output device, so its own GUI (e.g. an
    /// on-screen preview keyboard) can actually be heard. A null
    /// <paramref name="audioOutputDeviceId"/> uses the default device. Returns
    /// true on success; on failure <paramref name="error"/> explains why.
    /// </summary>
    bool TryStartAudio(Vst3ModuleHandle handle, AudioOutputSelection selection, out string? error);

    /// <summary>
    /// Stops real-time audio processing previously started with
    /// <see cref="TryStartAudio"/>. Safe to call even if not running.
    /// </summary>
    void StopAudio(Vst3ModuleHandle handle);

    /// <summary>
    /// Queues a note for a module whose real-time audio is running, so the plug-in plays it.
    /// Channel is 0-based. Returns false if the module is not running.
    /// </summary>
    bool TrySendNote(Vst3ModuleHandle handle, int channel, int noteNumber, int velocity, bool isOn);

    /// <summary>
    /// Releases every note the module's plug-in is still holding, so nothing is left sounding.
    /// </summary>
    void ReleaseAllNotes(Vst3ModuleHandle handle);

    /// <summary>
    /// Sends a MIDI controller to a running module's plug-in: 0-127 a control change (64 sustain, 1 mod
    /// wheel), 128 aftertouch, 129 pitch bend; value from 0 to 1. False when the plug-in has no parameter for it.
    /// </summary>
    bool TrySendController(Vst3ModuleHandle handle, int channel, int controller, double value);

    /// <summary>
    /// A module that plays an audio track's clip in the plug-ins' mix, from startSeconds into it. It starts
    /// with <see cref="TryStartAudio"/> and goes with <see cref="StopAudio"/> and <see cref="UnloadModule"/>.
    /// Null when the bridge is not available.
    /// </summary>
    /// <summary>
    /// Starts recording the mix of every plug-in and clip as it goes out, for exporting the song, for at
    /// most maxSeconds. False when the bridge is not available.
    /// </summary>
    bool TryStartMixCapture(double maxSeconds);

    /// <summary>
    /// Stops recording the mix and returns it: interleaved stereo floats, at sampleRate. Null when nothing
    /// was recorded, as when no plug-in or clip was playing.
    /// </summary>
    float[]? StopMixCapture(out int sampleRate);

    Vst3ModuleHandle? TryCreateClip(float[] interleaved, int channels, int sampleRate, double startSeconds);

    /// <summary>
    /// Makes a clip play round and round (loops) or once, at gain (1 as recorded); either can change while it
    /// plays, as for the master's test tone.
    /// </summary>
    void SetClipPlayback(Vst3ModuleHandle handle, bool loops, double gain);

    /// <summary>
    /// Makes a clip only to be heard, as the master's test tone: it joins the output after the master's
    /// effects, is left out of the recorded mix, and the master's meters see it at meterPeak (linear) whatever
    /// gain it plays at.
    /// </summary>
    void SetClipMonitor(Vst3ModuleHandle handle, bool monitorOnly, double meterPeak);

    /// <summary>
    /// Fills buffer with the last samples that went out to the audio output, both sides averaged, the latest
    /// last, for the master's spectrum analyser. Returns how many it filled: 0 when no sound has gone out
    /// since the last call.
    /// </summary>
    int ReadOutputScope(float[] buffer, out double sampleRate);

    /// <summary>
    /// The plug-in's state (the sound and settings chosen in it), to save with the project: part 0 is the
    /// processor's, part 1 the editor's. Null when the plug-in has none to give.
    /// </summary>
    byte[]? TrySaveState(Vst3ModuleHandle handle, int part);

    /// <summary>
    /// Gives the plug-in back a state taken by <see cref="TrySaveState"/>. True if it took it.
    /// </summary>
    bool TryLoadState(Vst3ModuleHandle handle, int part, byte[] state);

    /// <summary>
    /// True if real-time audio is currently running for this module.
    /// </summary>
    bool IsAudioRunning(Vst3ModuleHandle handle);

    /// <summary>
    /// Measures the loopback round trip of a running ASIO session (output 1 to input 1).
    /// Returns the time in milliseconds, or false with <paramref name="error"/> explaining why.
    /// </summary>
    bool TryMeasureAsioLatency(Vst3ModuleHandle handle, out double milliseconds, out string? error);

    /// <summary>
    /// Reads the buffer size and declared output latency of a running ASIO session.
    /// </summary>
    bool TryGetAsioTiming(Vst3ModuleHandle handle, out int bufferFrames, out int outputLatencyFrames, out double sampleRate);
}
