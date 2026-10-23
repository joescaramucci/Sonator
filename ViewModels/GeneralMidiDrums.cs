namespace Sonator.ViewModels;

// The General MIDI drum map: which drum each note from 35 to 81 plays, in short (for a key of the piano roll)
// and in full.
public static class GeneralMidiDrums
{
    public const int FirstNote = 35;

    private static readonly (string Short, string Name)[] Drums =
    [
        ("BD2", "Acoustic bass drum"),
        ("BD", "Bass drum"),
        ("RIM", "Side stick"),
        ("SN", "Acoustic snare"),
        ("CLAP", "Hand clap"),
        ("SN2", "Electric snare"),
        ("FT L", "Low floor tom"),
        ("HH C", "Closed hi-hat"),
        ("FT H", "High floor tom"),
        ("HH P", "Pedal hi-hat"),
        ("LT", "Low tom"),
        ("HH O", "Open hi-hat"),
        ("MT L", "Low-mid tom"),
        ("MT H", "Hi-mid tom"),
        ("CR1", "Crash cymbal 1"),
        ("HT", "High tom"),
        ("RD1", "Ride cymbal 1"),
        ("CHN", "Chinese cymbal"),
        ("BELL", "Ride bell"),
        ("TAMB", "Tambourine"),
        ("SPL", "Splash cymbal"),
        ("COW", "Cowbell"),
        ("CR2", "Crash cymbal 2"),
        ("VIBR", "Vibraslap"),
        ("RD2", "Ride cymbal 2"),
        ("BNG H", "Hi bongo"),
        ("BNG L", "Low bongo"),
        ("CNG M", "Mute hi conga"),
        ("CNG H", "Open hi conga"),
        ("CNG L", "Low conga"),
        ("TMB H", "High timbale"),
        ("TMB L", "Low timbale"),
        ("AGO H", "High agogo"),
        ("AGO L", "Low agogo"),
        ("CABA", "Cabasa"),
        ("MARA", "Maracas"),
        ("WHS S", "Short whistle"),
        ("WHS L", "Long whistle"),
        ("GUI S", "Short guiro"),
        ("GUI L", "Long guiro"),
        ("CLAV", "Claves"),
        ("WB H", "Hi wood block"),
        ("WB L", "Low wood block"),
        ("CUI M", "Mute cuica"),
        ("CUI O", "Open cuica"),
        ("TRI M", "Mute triangle"),
        ("TRI O", "Open triangle"),
    ];

    public static int LastNote => FirstNote + Drums.Length - 1;

    // The short name of the drum on a note, null outside the map.
    public static string? ShortName(int note) => InMap(note) ? Drums[note - FirstNote].Short : null;

    public static string? Name(int note) => InMap(note) ? Drums[note - FirstNote].Name : null;

    // True when a track plays drums, so its piano roll shows the drum map from the start: MIDI channel 10, a drum
    // icon, or drums in its name, instrument or plug-in (EZdrummer, Superior Drummer...).
    public static bool LooksLikeDrums(PatternTrackViewModel track)
    {
        if (track.Channel == 10 || track.InstrumentIconKey is "drums" or "kick" or "hi-hat" or "percussion" or "conga")
        {
            return true;
        }

        return new[] { track.Name, track.Instrument, track.PluginName }
            .Any(text => text is not null && (text.Contains("drum", StringComparison.OrdinalIgnoreCase)
                || text.Contains("kit", StringComparison.OrdinalIgnoreCase)
                || text.Contains("perc", StringComparison.OrdinalIgnoreCase)));
    }

    private static bool InMap(int note) => note >= FirstNote && note <= LastNote;
}
