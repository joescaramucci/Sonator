using Sonator.Services.Vst3;

namespace Sonator.Services.Effects;

// One track's chain of Sonator effects inside the native bridge: the effects in order, then the
// track's gain. The bridge runs it on the track's plug-in output once attached to the plug-in, and
// Process runs audio the .NET side plays itself, such as audio clips, through it.
public sealed class NativeEffectChain : IDisposable
{
    private IntPtr _handle;

    private NativeEffectChain(IntPtr handle)
    {
        _handle = handle;
    }

    // Null when the native bridge is not available.
    public static NativeEffectChain? TryCreate()
    {
        try
        {
            var handle = Vst3HostInterop.VstHostBridge_CreateEffectChain();
            return handle == IntPtr.Zero ? null : new NativeEffectChain(handle);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return null;
        }
    }

    // Replaces the effects with new ones of these kinds, at their default settings.
    public void SetLayout(IReadOnlyList<EffectKind> kinds)
    {
        var types = kinds.Select(kind => (int)kind).ToArray();
        Vst3HostInterop.VstHostBridge_SetEffectChainLayout(_handle, types, types.Length);
    }

    // As SetLayout, where each Plugin slot runs its VST3 effect module. A module already in the chain keeps
    // running as it is, wherever it moves to.
    public void SetLayout(IReadOnlyList<EffectKind> kinds, IReadOnlyList<Vst3ModuleHandle?> modules)
    {
        var types = kinds.Select(kind => (int)kind).ToArray();
        var handles = modules.Select(module => module?.NativeHandle ?? IntPtr.Zero).ToArray();
        Vst3HostInterop.VstHostBridge_SetEffectChainLayoutWithPlugins(_handle, types, handles, types.Length);
    }

    // value is in the units shown to the user; the definition converts it for the bridge.
    public void SetParameter(int slot, EffectParameterDefinition definition, int parameterIndex, double value) =>
        Vst3HostInterop.VstHostBridge_SetEffectParameter(_handle, slot, parameterIndex, value * definition.NativeScale);

    public void SetEnabled(int slot, bool enabled) => Vst3HostInterop.VstHostBridge_SetEffectEnabled(_handle, slot, enabled ? 1 : 0);

    public void SetGain(double gain) => Vst3HostInterop.VstHostBridge_SetEffectChainGain(_handle, gain);

    // -1 hard left, 0 the centre, +1 hard right.
    public void SetPan(double pan) => Vst3HostInterop.VstHostBridge_SetEffectChainPan(_handle, pan);

    // The chain runs on the whole mix from now on, after every track is summed.
    public void UseAsMaster() => Vst3HostInterop.VstHostBridge_SetMasterEffectChain(_handle);

    public static void ClearMaster() => Vst3HostInterop.VstHostBridge_SetMasterEffectChain(IntPtr.Zero);

    // The most buses the mix has room for.
    public const int MaxBuses = 16;

    // How much of the track goes to a bus, after its effects, gain and pan: linear gain, 0 for none.
    public void SetSend(int bus, double level) => Vst3HostInterop.VstHostBridge_SetEffectChainSend(_handle, bus, level);

    // The chain is the bus in this slot from now on: the sends to it run through it and join the mix.
    public void UseAsBus(int bus) => Vst3HostInterop.VstHostBridge_SetBus(bus, _handle);

    // Takes the bus out of the mix. Nothing to do without the native bridge.
    public static void ClearBus(int bus)
    {
        try
        {
            Vst3HostInterop.VstHostBridge_SetBus(bus, IntPtr.Zero);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
        }
    }

    // The effect's own meter, such as a compressor's gain reduction in dB.
    public double Meter(int slot) => Vst3HostInterop.VstHostBridge_GetEffectMeter(_handle, slot);

    // The highest absolute output sample since the previous call.
    public double TakePeak() => Vst3HostInterop.VstHostBridge_TakeEffectChainPeak(_handle);

    // One effect's highest input and output samples since the previous call.
    public (double Input, double Output) TakeLevels(int slot)
    {
        Vst3HostInterop.VstHostBridge_TakeEffectLevels(_handle, slot, out var input, out var output);
        return (input, output);
    }

    // Processes interleaved samples in place.
    public void Process(float[] interleaved, int frames, int channels, double sampleRate) =>
        Vst3HostInterop.VstHostBridge_ProcessEffectChain(_handle, interleaved, frames, channels, sampleRate);

    // The chain runs on this plug-in's output from now on.
    public void AttachTo(Vst3ModuleHandle module) => Vst3HostInterop.VstHostBridge_AttachEffectChain(module.NativeHandle, _handle);

    public static void Detach(Vst3ModuleHandle module) => Vst3HostInterop.VstHostBridge_AttachEffectChain(module.NativeHandle, IntPtr.Zero);

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            Vst3HostInterop.VstHostBridge_DestroyEffectChain(_handle);
            _handle = IntPtr.Zero;
        }
    }
}
