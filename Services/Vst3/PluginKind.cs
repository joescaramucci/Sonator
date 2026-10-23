namespace Sonator.Services.Vst3;

// What a plug-in is, numbered as the bridge's VstHostBridge_GetPluginKind answers.
public enum PluginKind
{
    Unknown = 0,
    Instrument = 1,
    Effect = 2,
}
