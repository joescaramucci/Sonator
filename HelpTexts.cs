namespace Sonator;

// A window's help: its name and a few short paragraphs on what it is for and how to use it.
public sealed record HelpText(string Title, IReadOnlyList<string> Paragraphs);

// The help shown by the "?" of each window (see HelpBadge), by the window's type.
public static class HelpTexts
{
    public static HelpText? For(Type window) => All.TryGetValue(window, out var help) ? help : null;

    // The help of a part of a window, by its topic (see HelpBadge.Topic).
    public static HelpText? ForTopic(string topic) => Topics.TryGetValue(topic, out var help) ? help : null;

    private static readonly Dictionary<string, HelpText> Topics = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Sonator"] = new("Sonator", new[]
        {
            "The song is built from patterns. PATTERN, on the left, lists them and how many bars each lasts; ARRANGE, in the middle, shows the selected pattern's tracks along the timeline, or the whole song with ARRANGE on.",
            "Each track plays MIDI notes, a VST instrument dropped on it from the plug-ins on the right, or an audio clip (File > Import audio, or drop a WAV or MP3 on an empty track). M mutes, S solos, R arms recording, FX opens its effects.",
            "Drag along the bars of the ruler to choose a part to loop, then turn LOOP on. Space plays and stops, the full stop goes back to the start, the arrows move by a bar.",
            "MIXER, WAVE, ARRANGE and SOUNDS at the top open the mixer, the waveform editor, the song view and the sound browser.",
        }),
        ["Pattern"] = new("Pattern", new[]
        {
            "The song's patterns, each a few bars of all the tracks. The list says which pattern is in each slot of the arrangement (a, b, c, d), bar by bar; click one to work on it, drag it to move it, or drop it on DROP HERE to put it at the end.",
            "Below, the selected pattern: BARS sets how long it lasts, TRANSPOSE moves all its notes up or down in semitones, START, END and LENGTH say where it sits in the song. PREV and NEXT step through the patterns, COPY makes a copy, DELETE removes it, CREATE NEW PATTERN adds one.",
        }),
        ["Arrange"] = new("Arrange", new[]
        {
            "The tracks of the selected pattern along the timeline, or the whole song with ARRANGE on. On the left of each track: M mutes, S solos, R arms recording, FX opens its effects, + adds an effect, then the volume and pan bars.",
            "The ruler: drag along the bars to choose a part to loop (drag its L or R edge to change it), click to go there, right-click to add a marker or a change of tempo. Drag a recorded block or an audio clip to move it; double-click a block for the piano roll, a clip for the waveform editor, right-click a clip to detect its tempo.",
            "Each block of notes keeps its bounds: a block pasted after another stays a block of its own. Click a block to select it, Ctrl+click to add another, or drag a rectangle on the lanes around the blocks you want. Ctrl+C copies them and Ctrl+V puts the copy right after the track's last block, again and again; quicker, hold the right button and left-click once for each copy. Right-click for Join (Ctrl+J), which makes the selected blocks of a track one. The middle button drags the timeline along.",
            "ZOOM, or the mouse wheel, widens the bars; FIT shows them all. QUANT sets the grid recorded notes snap to, SNAP the grid blocks and clips move on. Drop a plug-in from the right on a track to play it, or a WAV or MP3 on an empty track.",
            "A drum track whose plug-in plays each drum on its own output (see the mixer's KIT) has KIT by its name: it opens a narrower lane under the track for each drum that has played, in the track's colour, with its mute, solo and volume and the hits of its drum.",
        }),
        ["Track"] = new("Track", new[]
        {
            "The selected track: its name and type, the MIDI channel it plays on (ROUTING) and its base note. PLUGIN chooses the VST instrument it plays; EDIT changes its settings, PIANO ROLL opens its notes, PLUGIN WINDOW shows the instrument's own window.",
            "EFFECTS lists the track's effects: + ADD puts one more, ON switches one, the arrows move it, the cross removes it; click a name to open it in the rack. MASTER, below, is the volume of the whole mix.",
        }),
        ["Transport"] = new("Transport", new[]
        {
            "LEFT LOCATOR and RIGHT LOCATOR are the loop's start and end, in steps; dragging along the ruler sets them. Below, the position in the song.",
            "UNDO takes back the last change. RECORD turns recording on: with a track armed (R), START records the keys played on the MIDI keyboard into a MIDI track, or the sound card's input into an audio track. START plays, STOP stops; IMPORT MIDI brings in a MIDI file.",
            "The line below says what Sonator has just done.",
        }),
        ["VstPlugins"] = new("VST plug-ins", new[]
        {
            "SONATOR EFFECTS, at the top, are Sonator's own (SonVerb, SonDelay, SonArmo, SonCoro, SonMast...), each pictured by its screen: click one to put it on the selected track and open its rack, or drag it onto a track.",
            "Your VST instruments and effects, found in the usual folders and in the one chosen here (Browse... picks another). Drag one onto a track to play it there, or drag the pictures to put them in your order; the star marks your favourites, which come first.",
        }),
        ["Loop"] = new("Loop", new[]
        {
            "ON repeats the part of the song chosen on the ruler, or the whole pattern without one, until STOP.",
        }),
        ["ArrangeMode"] = new("Arrange", new[]
        {
            "ON plays the whole song, its patterns one after another as the arrangement says; OFF plays the selected pattern only.",
        }),
        ["Tempo"] = new("Tempo", new[]
        {
            "The song's speed, in beats per minute. Double-click to type a value (1 to 999), then Enter or click away; the mouse wheel moves it by 1 BPM, with Shift by 0.1. Track > Detect tempo finds it from an audio clip.",
        }),
        ["Metronome"] = new("Metronome", new[]
        {
            "ON plays the click while the song plays; CLICK starts or stops a click on its own, to try it. ALWAYS clicks all along under the song; pressed, it becomes COUNT-IN, which only counts you in before recording, then goes quiet. Below, whether it clicks on quarters or eighths, and how many bars of count-in come before a recording: press each to change it.",
        }),
        ["Signature"] = new("Time signature", new[]
        {
            "How many beats a bar has: click to choose 4/4, 3/4 or any other; the mouse wheel changes the beats per bar.",
        }),
        ["Clock"] = new("Position", new[]
        {
            "Where the song is: hours, minutes and seconds on the first display, bar and beat on the second.",
        }),
        ["Views"] = new("Windows", new[]
        {
            "MIXER opens the mixing desk, WAVE the waveform editor, SOUNDS the sound browser. ARRANGE switches between the whole song and the selected pattern, as the ARRANGE box does; it is lit while the whole song plays.",
        }),
        ["Health"] = new("Computer load", new[]
        {
            "How hard the computer works: CPU, the audio's load (DSP) with its drops, when the sound could not be made in time and may have clicked, and the memory in use.",
        }),
    };

    private static readonly Dictionary<Type, HelpText> All = new()
    {
        [typeof(MixerWindow)] = new("Mixer", new[]
        {
            "One strip per track, as on a desk, top to bottom as the sound goes: its inserts (click one to open it in its rack), FX to add effects, the sends to the buses, pan, mute and solo, and the fader with its meter.",
            "+ BUS adds a bus, a strip the tracks send part of their sound to, for a shared reverb or echo. The master strip, on the right, has the effects on the whole mix, its two faders (LINK moves them together, or drag the bar across them), the VU meters, the spectrum and the oscillator to line the meters up: turn CAL until the needles sit on 0.",
            "Drag the master strip's left edge to widen it. Space, the full stop and the arrows work here as in the main window. A click on the main window puts the mixer behind it; MIXER brings it back, or puts it in the dock when it is the window in use.",
            "A drum plug-in that plays each drum on its own output gives the track a kit: in EZdrummer 3, open its Mixer page and choose Mixer > Select All Channels, then Mixer > Route Outputs > Multichannel. Once the drums have played, KIT shows on the track's strip: it, or a click on the track's fader, opens a narrower strip for each output that played, with its own fader, pan, mute, solo, effects and sends (a piece's S plays it alone, or with the other pieces soloed); while the drums play, Sonator names each one after the drum it hears on it (Snare, Hi-hat, Toms, Room...); click a name to write your own. The track's own strip plays the main output (the kick) and its fader moves the whole kit; the open kit is shaded in the track's colour.",
        }),
        [typeof(EffectRackWindow)] = new("FX rack", new[]
        {
            "The effects of one track, from the top down, as the sound goes through them. + ADD puts Sonator's effects or VST effect plug-ins at the bottom; drag the grip at an effect's left to move it up or down.",
            "The power key switches an effect on or off. Turn the knobs by dragging up or down (Shift for fine steps), with the mouse wheel, or double-click for the default. The screen beside the knobs shows what the effect does; drag its orange handles too.",
            "Choose a preset from the list, change it, and SAVE it under a name of yours; RESET goes back to the effect's defaults.",
            "SonArmo sings harmonies with a voice: VOICE 1 and VOICE 2 each a third, fifth, octave... above or below, at their own LEVEL and spread by WIDTH; DRY is the voice itself. Set KEY (and MINOR) to the song's key and each harmony stays in its scale, a third major or minor as the note sung needs; with KEY on None the intervals are fixed. Its screen shows the note sung and the harmony notes.",
            "SonCoro makes a choir of one voice: SINGERS how many (up to 32), each a little off the others by up to DETUNE, a little late by up to TIMING, its pitch WAVERING slowly on its own, spread by WIDTH; LOW SECTION and HIGH SECTION add singers an octave below and above, as far as people sing (the low section stays above F2, the high one below C6). VOICES chooses whose choir it is: as sung, women, men or children; from a man's voice a women's or children's choir sings an octave up, from a woman's a men's choir an octave down, each with the timbre of its singers. The kind of choir (pop backing vocals, chamber, gospel, church, epic...) is chosen among the presets. MIX sets choir against voice. Its screen shows the singers across the field.",
            "SonTune corrects the pitch of a voice, as Auto-Tune does: each note sung is moved to the nearest note of KEY's scale (MINOR for the minor one; with KEY on Chromatic, to the nearest of all twelve), keeping the voice's timbre. SPEED is how long the move takes: 0 ms snaps at once, the robotic hard-tune sound; 10 to 30 ms corrects as naturally as a good singer. HUMANIZE keeps the vibrato and the slides into a note, correcting only its centre; AMOUNT is how much of the way it goes; MIX sets the tuned voice against the voice as sung (keep it at 100%: halfway, the two voices beat against each other). TUNING is the song's reference in cents off A440, for a song or a band tuned otherwise. Its screen shows the note heard, the note it goes to and how many cents off it is sung. The voice comes out 32 ms late and the mixer delays the other tracks as much, so all stay in time.",
            "SonMast masters the finished song: put it on the MASTER (its FX). LOW, PRESENCE and AIR shape the tone, GLUE compresses gently to hold the mix together, WIDTH widens or narrows it, DRIVE makes it louder into a limiter that never lets a peak over CEILING (keep it at -1 dB for MP3). Its screen shows the loudness of the last 3 s in LUFS: about -14 is what the streaming services play at. File > Export audio then makes the WAV or MP3, and says how loud it came out.",
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
            "Drag in the lane at the bottom to set how hard each note is played. QUANTIZE moves the notes to the grid chosen (straight, dotted or triplet), as far as the strength says, with swing delaying every second grid point (100% is a triplet shuffle); ENDS moves where the notes end too, otherwise each keeps its length. APPLY quantizes all the track's notes; Undo reverts it.",
            "DRUM MAP names the keys by the General MIDI drums (BD bass drum, SN snare, HH C / P / O closed, pedal and open hi-hat, CR crash, RD ride, LT MT HT FT toms...). It is on by itself for a drum track.",
            "Space plays and stops.",
        }),
        [typeof(SondyneWindow)] = new("Sondyne", new[]
        {
            "The notes of a sung (or played) take, each a blob on the semitones, as long as it lasts and as thick as it is loud, with the pitch sung drawn through it. Open it from a clip's right-click, or Track > Correct the pitch.",
            "Click a note to select it (Ctrl adds, Ctrl+A all) and drag it up or down a semitone at a time, with Alt by cents; a double-click brings it to the middle of its semitone. CENTER brings the selected notes (all, with none selected) to the middle of their semitones, DRIFT steadies their wobble; CORRECT ALL does both, gently, to every note, and RESET takes the changes back.",
            "PLAY (or Space) plays it as changed, from just before the notes selected. APPLY puts it on the track in place of the take, which Undo in the main window brings back; the take itself is never changed.",
        }),
        [typeof(CromatorWindow)] = new("Cromator", new[]
        {
            "The score of a track of the selected pattern (of the whole song with ARRANGE on): its notes on a staff, in the clef and key they need, with rests, ties, beams and the bar numbers. It follows the track as you record or edit it.",
            "Write the words under LYRICS: a syllable goes under each note, in order. Split words with a hyphen (a-mo-re); a lone _ leaves a note without a syllable. TITLE and AUTHOR head the first page, and are saved with the project, as the words are with the track.",
            "FROM VOICE writes a score from a sung take: choose the audio track it is on and the language, and WRITE FROM VOICE hears the melody as notes on a MIDI track (the first empty one, or a new one), where the take sits in the song. With WORDS on, the words sung are heard too (by Whisper, on this computer: its model is downloaded once) and set under the notes, ready to be corrected under LYRICS.",
            "PRINT prints it on A4; PDF saves it as a PDF file through Windows' PDF printer, which asks where. SCORE brings the window back, or puts it in the dock.",
        }),
        [typeof(SoundBrowserWindow)] = new("Sounds", new[]
        {
            "The sounds of your plug-ins and Sonator's grooves, by category. Click one to hear it, then put it on the selected track; the star keeps it among your favourites, and Last used lists the latest ones you put on a track, newest first.",
            "GENRE keeps only the sounds and grooves that suit a kind of music (Pop, House, Jazz...), those that suit it best first: from the genres and character their makers file them under, and from their names (a groove library's Motown or Nashville, an 808, a Rhodes).",
        }),
        [typeof(NewSongWindow)] = new("New song", new[]
        {
            "Choose the kind of music you want to make: each style lists the parts its hits are played with (drums, bass, piano, pads...) and, for each part, the sounds of your plug-ins and grooves that suit it best, from how their makers file them (genre, character) and from their names. Click a sound to hear it at the style's tempo and choose it; OTHERS shows more.",
            "CREATE SONG starts a new project at the tempo shown, with a track for each part ticked, named after it and playing the sound chosen; a groove's notes go into its track from the first bar. EMPTY PROJECT starts one with eight empty tracks, as before.",
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
