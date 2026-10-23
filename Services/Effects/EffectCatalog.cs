namespace Sonator.Services.Effects;

// The effect types, numbered as in the native bridge (native/VstHostBridge/src/SonEffects.h).
public enum EffectKind
{
    Verb = 1,
    Delay = 2,
    Compress = 3,
    EQ = 4,
    Limiter = 5,
    Plugin = 100, // a VST3 effect plug-in
}

// One knob of an effect, in the units shown to the user. The bridge receives Value * NativeScale.
// A switch has a range of 0 to 1 and IsSwitch set.
public sealed record EffectParameterDefinition(
    string Name,
    double Minimum,
    double Maximum,
    double Default,
    string Unit,
    int Decimals = 0,
    double NativeScale = 1,
    bool IsSwitch = false,
    bool IsLogarithmic = false);

public sealed record EffectDefinition(EffectKind Kind, string Name, string Description, IReadOnlyList<EffectParameterDefinition> Parameters);

// Sonator's own effects, in the order the add menu lists them. Parameter order matches the bridge.
public static class EffectCatalog
{
    public static IReadOnlyList<EffectDefinition> All { get; } = new[]
    {
        new EffectDefinition(EffectKind.Verb, "SonVerb", "Room reverb", new[]
        {
            new EffectParameterDefinition("Size", 0, 100, 60, "%", NativeScale: 0.01),
            new EffectParameterDefinition("Damping", 0, 100, 40, "%", NativeScale: 0.01),
            new EffectParameterDefinition("Width", 0, 100, 100, "%", NativeScale: 0.01),
            new EffectParameterDefinition("Pre-delay", 0, 200, 10, "ms"),
            new EffectParameterDefinition("Mix", 0, 100, 25, "%"),
        }),
        new EffectDefinition(EffectKind.Delay, "SonDelay", "Stereo echo", new[]
        {
            new EffectParameterDefinition("Time", 1, 2000, 375, "ms", IsLogarithmic: true),
            new EffectParameterDefinition("Feedback", 0, 95, 35, "%"),
            new EffectParameterDefinition("Mix", 0, 100, 25, "%"),
            new EffectParameterDefinition("Ping-pong", 0, 1, 0, string.Empty, IsSwitch: true),
            new EffectParameterDefinition("High cut", 500, 20000, 8000, "Hz", IsLogarithmic: true),
        }),
        new EffectDefinition(EffectKind.Compress, "SonCompress", "Compressor", new[]
        {
            new EffectParameterDefinition("Threshold", -60, 0, -18, "dB", Decimals: 1),
            new EffectParameterDefinition("Ratio", 1, 20, 4, ":1", Decimals: 1),
            new EffectParameterDefinition("Attack", 0.1, 100, 10, "ms", Decimals: 1, IsLogarithmic: true),
            new EffectParameterDefinition("Release", 10, 1000, 120, "ms", IsLogarithmic: true),
            new EffectParameterDefinition("Makeup", 0, 24, 0, "dB", Decimals: 1),
            new EffectParameterDefinition("Knee", 0, 12, 6, "dB", Decimals: 1),
        }),
        new EffectDefinition(EffectKind.EQ, "SonEQ", "Three-band equalizer", new[]
        {
            new EffectParameterDefinition("Low gain", -15, 15, 0, "dB", Decimals: 1),
            new EffectParameterDefinition("Low freq", 20, 500, 120, "Hz", IsLogarithmic: true),
            new EffectParameterDefinition("Mid gain", -15, 15, 0, "dB", Decimals: 1),
            new EffectParameterDefinition("Mid freq", 200, 8000, 1000, "Hz", IsLogarithmic: true),
            new EffectParameterDefinition("Mid Q", 0.3, 6, 1, string.Empty, Decimals: 2, IsLogarithmic: true),
            new EffectParameterDefinition("High gain", -15, 15, 0, "dB", Decimals: 1),
            new EffectParameterDefinition("High freq", 2000, 18000, 8000, "Hz", IsLogarithmic: true),
        }),
        new EffectDefinition(EffectKind.Limiter, "SonLimiter", "Peak limiter", new[]
        {
            new EffectParameterDefinition("Ceiling", -12, 0, -0.3, "dB", Decimals: 1),
            new EffectParameterDefinition("Release", 1, 500, 50, "ms", IsLogarithmic: true),
            new EffectParameterDefinition("Drive", 0, 24, 0, "dB", Decimals: 1),
        }),
    };

    public static EffectDefinition Get(EffectKind kind) => All.First(definition => definition.Kind == kind);

    // A VST3 effect plug-in in a rack: it has no knobs of Sonator's, only its own window.
    public static EffectDefinition ForPlugin(string pluginName) =>
        new(EffectKind.Plugin, pluginName, "VST3 effect", Array.Empty<EffectParameterDefinition>());

    public static EffectDefinition? FindByName(string? name) =>
        All.FirstOrDefault(definition => string.Equals(definition.Name, name, StringComparison.OrdinalIgnoreCase));
}
