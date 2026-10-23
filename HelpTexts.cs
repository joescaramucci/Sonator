namespace Sonator;

// A window's help: its name and a few short paragraphs on what it is for and how to use it.
public sealed record HelpText(string Title, IReadOnlyList<string> Paragraphs);

// The help shown by the "?" of each window (see HelpBadge), by the window's type.
public static class HelpTexts
{
    public static HelpText? For(Type window) => All.TryGetValue(window, out var help) ? help : null;

    private static readonly Dictionary<Type, HelpText> All = new()
    {
        [typeof(MainWindow)] = new("Sonator", new[]
        {
            "The song is built from patterns. PATTERN, on the left, lists them and how many bars each lasts; ARRANGE, in the middle, shows the selected pattern's tracks along the timeline, or the whole song with ARRANGE on.",
            "Each track plays MIDI notes, a VST instrument dropped on it from the plug-ins on the right, or an audio clip (File > Import audio, or drop a WAV or MP3 on an empty track). M mutes, S solos, R arms recording, FX opens its effects.",
            "Drag along the bars of the ruler to choose a part to loop, then turn LOOP on. Space plays and stops, the full stop goes back to the start, the arrows move by a bar.",
            "MIXER, WAVE, ARRANGE and SOUNDS at the top open the mixer, the waveform editor, the song view and the sound browser.",
        }),
        [typeof(MixerWindow)] = new("Mixer", new[]
        {
            "One strip per track, as on a desk, top to bottom as the sound goes: its inserts (click one to open it in its rack), FX to add effects, the sends to the buses, pan, mute and solo, and the fader with its meter.",
            "+ BUS adds a bus, a strip the tracks send part of their sound to, for a shared reverb or echo. The master strip, on the right, has the effects on the whole mix, its two faders (LINK moves them together, or drag the bar across them), the VU meters, the spectrum and the oscillator to line the meters up: turn CAL until the needles sit on 0.",
            "Drag the master strip's left edge to widen it. Space, the full stop and the arrows work here as in the main window.",
        }),
        [typeof(EffectRackWindow)] = new("FX rack", new[]
        {
            "The effects of one track, from the top down, as the sound goes through them. + ADD puts Sonator's effects or VST effect plug-ins at the bottom; drag the grip at an effect's left to move it up or down.",
            "The power key switches an effect on or off. Turn the knobs by dragging up or down (Shift for fine steps), with the mouse wheel, or double-click for the default. The screen beside the knobs shows what the effect does; drag its orange handles too.",
            "Choose a preset from the list, change it, and SAVE it under a name of yours; RESET goes back to the effect's defaults.",
        }),
        [typeof(WaveformEditorWindow)] = new("Waveform editor", new[]
        {
            "Edits an audio clip: double-click a clip on the timeline to open it here, or Open... a file. Drag across the waveform to select a part, then Cut, Copy, Paste, Delete, Normalize, or fade it in or out; Undo takes a step back.",
            "Space plays the selection, or the whole clip without one, and stops; the full stop plays it all from the start; Escape stops.",
            "Send to track puts the clip, or the part selected, back into the track it came from, or into the selected empty track. Stretch to song tempo fits it to the song's tempo from the Clip BPM you give.",
        }),
        [typeof(PianoRollWindow)] = new("Piano roll", new[]
        {
            "The notes of a track, pitch up the side, time along the bars. Click an empty cell to add a note, drag a note to move it, drag its end to change its length; right-click or Delete removes it.",
            "Drag in the lane at the bottom to set how hard each note is played. QUANTIZE moves the notes to the grid chosen, with the strength and swing beside it; APPLY makes it so.",
            "Space plays and stops.",
        }),
        [typeof(SoundBrowserWindow)] = new("Sounds", new[]
        {
            "The sounds of your plug-ins and Sonator's grooves, by category. Click one to hear it, then put it on the selected track; the star keeps it among your favourites.",
        }),
        [typeof(TrackEditDialog)] = new("Track", new[]
        {
            "The track's name, the instrument it is for, what kind of track it is (MIDI channel, VST instrument, or audio input to record from the sound card), and for MIDI the channel and the base note its steps play.",
        }),
        [typeof(ArrangePatternEditDialog)] = new("Arrange pattern", new[]
        {
            "The name of a pattern of the arrangement and the number it is assigned to, as shown in the song's cells.",
        }),
        [typeof(DriverSettingsDialog)] = new("ASIO driver settings", new[]
        {
            "The buffer size and the sample rate the ASIO driver runs at. A smaller buffer answers sooner when you play, a larger one leaves the computer more time and avoids clicks. The latency shown is the time the sound takes to come out.",
        }),
        [typeof(MidiImportDialog)] = new("Import MIDI", new[]
        {
            "Choose a standard MIDI file: its tracks become the song's tracks, with their notes, tempo and markers, in place of the song open now.",
        }),
        [typeof(NormalizeWindow)] = new("SonNormalize", new[]
        {
            "Turns the whole clip up or down so that its loudest moment reaches the level you choose, without changing its sound. The original file is kept, and Undo brings it back.",
        }),
        [typeof(TimeSignatureWindow)] = new("Time signature", new[]
        {
            "How many beats a bar has, and how long they are: 4/4 for most songs, 3/4 for a waltz, or any other. The grid, the metronome and the bars follow it.",
        }),
        [typeof(InstrumentIconPickerWindow)] = new("Instrument icon", new[]
        {
            "The picture shown on the track, to tell its instrument at a glance.",
        }),
        [typeof(PresetNameDialog)] = new("Save preset", new[]
        {
            "The name the effect's knobs are saved under, to choose them again in any project.",
        }),
    };
}
