using System.Diagnostics;
using System.IO;
using Sonator.Services;
using Sonator.Services.Sounds;
using Sonator.Services.Vst3;

namespace Sonator.ViewModels;

// Plays a sound of the sound browser before it is loaded on a track. Each sound plays in a plug-in instance of
// its own, outside the tracks, that has already taken it, so it is heard at once: the sound being tried and
// the next few in the list are got ready ahead. At most MaxInstances instances are kept; one ready for a
// sound no longer wanted is given the next sound, and asleep, an instance waiting costs no audio time.
internal sealed class SoundPreviewer : IDisposable
{
    public const int MaxInstances = 4;

    // A preview instance: its plug-in, and the sound it has taken (null for one that plays grooves, which
    // need no sound of their own), with when it was last used.
    private sealed class Slot
    {
        public required Vst3ModuleHandle Instrument { get; init; }
        public required string PluginPath { get; init; }
        public SoundEntry? Sound { get; set; }
        public long LastUsed { get; set; }
        public long ReadyAt { get; set; } // Stopwatch time when its sound was given to it
    }

    private readonly IVst3PluginHost _host;
    private readonly List<Slot> _slots = new();
    private readonly Random _random = new();
    private readonly Dictionary<string, int> _lastPhrase = new();
    private HashSet<SoundEntry> _wanted = new();
    private CancellationTokenSource? _playing;
    private Task? _player;
    private Slot? _playingSlot;

    public SoundPreviewer(IVst3PluginHost host)
    {
        _host = host;
    }

    // The sounds to keep ready, in order: the one being tried and the next ones in the list. Ready instances
    // of other sounds may be given to these. Nothing is loaded here; see Prepare.
    public void Want(IEnumerable<SoundEntry> sounds) => _wanted = sounds.ToHashSet();

    // True when the sound can be heard at once.
    public bool IsReady(SoundEntry sound) => Find(sound) is not null;

    // Gets one sound ready in an instance, starting the plug-in if needed. Null when ready, else why not.
    public string? Prepare(SoundEntry sound) => SlotFor(sound, out var error) is null ? error : null;

    // Starts playing the sound, a phrase of its kind chosen at random (or the start of a groove). Null when it
    // plays, else why it cannot.
    public string? Preview(SoundEntry sound, double tempoBpm)
    {
        Stop();
        _wanted.Add(sound);
        if (SlotFor(sound, out var error) is not { } slot)
        {
            return error;
        }

        IReadOnlyList<PreviewNote> notes;
        try
        {
            notes = sound.Kind == SoundKind.Groove
                ? GrooveReader.Read(sound.PresetPath).Where(note => note.Start < 32) // the first two bars
                    .Select(note => new PreviewNote(note.Start, note.Length, note.NoteNumber, note.Velocity)).ToList()
                : PreviewPhrases.Pick(sound.Category, _random, _lastPhrase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                       or Melanchall.DryWetMidi.Common.MidiException)
        {
            return $"{sound.Name} could not be read";
        }

        // A sampled instrument (CMI, Emulator, BBC...) reads a new sound's samples in the background: a sound
        // given to its instance just now waits a moment, so the first notes are not lost.
        var sinceReady = Stopwatch.GetElapsedTime(slot.ReadyAt).TotalSeconds;
        var settle = sound.Kind == SoundKind.Groove ? 0.0 : Math.Max(0, 0.8 - sinceReady);
        var playing = new CancellationTokenSource();
        _playing = playing;
        _playingSlot = slot;
        var secondsPerStep = 60.0 / Math.Max(20, tempoBpm) / 4.0;
        _player = Task.Run(() => Play(slot.Instrument, notes, secondsPerStep, settle, playing.Token));
        return null;
    }

    // Silences the preview playing, if any. Returns once no more notes can be sent, so its instance can go.
    public void Stop()
    {
        _playing?.Cancel();
        _playing = null;
        _player?.Wait(TimeSpan.FromSeconds(1));
        _player = null;
        if (_playingSlot is not null)
        {
            _host.ReleaseAllNotes(_playingSlot.Instrument);
            _playingSlot = null;
        }
    }

    public void Dispose()
    {
        Stop();
        foreach (var slot in _slots)
        {
            Unload(slot);
        }

        _slots.Clear();
    }

    private Slot? Find(SoundEntry sound) => sound.Kind == SoundKind.Groove
        ? _slots.FirstOrDefault(slot => string.Equals(slot.PluginPath, sound.PluginPath, StringComparison.OrdinalIgnoreCase))
        : _slots.FirstOrDefault(slot => Equals(slot.Sound, sound));

    // The instance ready with the sound: one already holding it; else one of its plug-in no longer wanted,
    // given the sound; else a new instance, room made by letting go of the one least recently used.
    private Slot? SlotFor(SoundEntry sound, out string? error)
    {
        error = null;
        var slot = Find(sound);
        if (slot is null)
        {
            slot = _slots
                .Where(candidate => string.Equals(candidate.PluginPath, sound.PluginPath, StringComparison.OrdinalIgnoreCase)
                                    && !IsWanted(candidate) && candidate != _playingSlot)
                .OrderBy(candidate => candidate.LastUsed)
                .FirstOrDefault();
            if (slot is null)
            {
                if (_slots.Count >= MaxInstances
                    && _slots.Where(candidate => !IsWanted(candidate) && candidate != _playingSlot)
                        .OrderBy(candidate => candidate.LastUsed).FirstOrDefault() is { } oldest)
                {
                    Unload(oldest);
                    _slots.Remove(oldest);
                }

                if (_slots.Count >= MaxInstances)
                {
                    error = "Too many sounds are being got ready";
                    return null;
                }

                if (Start(sound.PluginPath, out error) is not { } instrument)
                {
                    return null;
                }

                slot = new Slot { Instrument = instrument, PluginPath = sound.PluginPath };
                _slots.Add(slot);
            }

            if (sound.Kind != SoundKind.Groove && !Give(slot, sound, out error))
            {
                return null;
            }
        }

        slot.LastUsed = Stopwatch.GetTimestamp();
        return slot;
    }

    private bool IsWanted(Slot slot) => slot.Sound is { } held && _wanted.Contains(held);

    private bool Give(Slot slot, SoundEntry sound, out string? error)
    {
        error = null;
        try
        {
            var state = sound.Kind == SoundKind.NksPreset
                ? NksSoundSource.StateFor(sound.PresetPath, _host.TrySaveState(slot.Instrument, 0) ?? Array.Empty<byte>())
                : File.ReadAllBytes(sound.PresetPath);
            if (!_host.TryLoadState(slot.Instrument, 0, state))
            {
                error = $"{sound.PluginName} did not take the sound {sound.Name}";
                slot.Sound = null;
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            error = $"{sound.Name} could not be read";
            slot.Sound = null;
            return false;
        }

        slot.Sound = sound;
        slot.ReadyAt = Stopwatch.GetTimestamp();
        return true;
    }

    private Vst3ModuleHandle? Start(string pluginPath, out string? error)
    {
        error = null;
        var handle = _host.TryLoadModule(pluginPath, out var loadError);
        if (handle is null)
        {
            error = $"The plug-in could not be loaded: {loadError}";
            return null;
        }

        if (!_host.HasAudioProcessor(handle)
            || !_host.TryStartAudio(handle, AudioOutputSelection.FromSettings(new AppSettingsService().Load()), out _))
        {
            _host.UnloadModule(handle);
            error = "The plug-in's audio did not start";
            return null;
        }

        return handle;
    }

    private void Unload(Slot slot)
    {
        _host.StopAudio(slot.Instrument);
        _host.UnloadModule(slot.Instrument);
    }

    // Sends the notes on time from a worker thread; stopping releases what is held.
    private void Play(Vst3ModuleHandle instrument, IReadOnlyList<PreviewNote> notes, double secondsPerStep, double startSeconds,
        CancellationToken token)
    {
        var events = notes
            .SelectMany(note => new[]
            {
                (Time: startSeconds + note.Start * secondsPerStep, On: true, note.NoteNumber, note.Velocity),
                (Time: startSeconds + (note.Start + note.Length) * secondsPerStep, On: false, note.NoteNumber, Velocity: 0),
            })
            .OrderBy(e => e.Time)
            .ThenBy(e => e.On) // a note-off before a note-on at the same moment
            .ToList();

        var clock = Stopwatch.StartNew();
        foreach (var e in events)
        {
            while (!token.IsCancellationRequested && clock.Elapsed.TotalSeconds < e.Time)
            {
                Thread.Sleep(2);
            }

            if (token.IsCancellationRequested)
            {
                return;
            }

            _host.TrySendNote(instrument, 0, e.NoteNumber, e.Velocity, e.On);
        }
    }
}

// A note of a preview, in sixteenth-note steps.
public readonly record struct PreviewNote(double Start, double Length, int NoteNumber, int Velocity);

// The little pieces a sound is tried with, several for each kind of sound, one picked at random each time and
// never the same twice running: chords and progressions for keys, melodies for leads and winds, slow chords
// for pads and strings, riffs for basses, beats for drums.
public static class PreviewPhrases
{
    private const int C3 = 48;

    private static PreviewNote N(double start, double length, int note, int velocity = 96) => new(start, length, note, velocity);

    // Chords held one after another: each is its notes, from the given root, for the given steps.
    private static IEnumerable<PreviewNote> Chords(int root, double stepsEach, params int[][] chords) =>
        chords.SelectMany((chord, index) => chord.Select(interval => N(index * stepsEach, stepsEach, root + interval, 88)));

    // One note after another, each for the given steps.
    private static IEnumerable<PreviewNote> Line(int root, double stepsEach, params int[] intervals) =>
        intervals.Select((interval, index) => N(index * stepsEach, stepsEach * 0.9, root + interval, 92 + index % 3 * 6));

    private static readonly int[] Major = { 0, 4, 7 }, Minor = { 0, 3, 7 }, Major7 = { 0, 4, 7, 11 }, Minor7 = { 0, 3, 7, 10 }, Dominant7 = { 0, 4, 7, 10 }, Sus4 = { 0, 5, 7 }, Add9 = { 0, 4, 7, 14 };

    private static int[] On(int[] chord, int root) => chord.Select(interval => interval + root).ToArray();

    private static readonly IReadOnlyList<PreviewNote>[] Keys =
    {
        // C major arpeggio and chord
        Line(C3, 2, 0, 4, 7, 12).Concat(Chords(C3, 8, new[] { 0, 7, 12, 16 }).Select(n => n with { Start = n.Start + 8 })).ToList(),
        // I - V - vi - IV: C G Am F
        Chords(C3, 4, On(Major, 0), On(Major, 7), On(Minor, 9), On(Major, 5)).ToList(),
        // ii - V - I in jazz: Dm7 G7 Cmaj7
        Chords(C3, 5, On(Minor7, 2), On(Dominant7, 7 - 12), On(Major7, 0)).ToList(),
        // Am - F - C - G, broken chords
        new[] { On(Minor, 9), On(Major, 5), On(Major, 0), On(Major, 7) }
            .SelectMany((chord, bar) => chord.Select((note, i) => N(bar * 4 + i, 1.5, C3 + note, 90))).ToList(),
        // Cadd9 shimmer, then Fmaj7
        Chords(C3, 8, On(Add9, 0), On(Major7, 5)).ToList(),
    };

    private static readonly IReadOnlyList<PreviewNote>[] Melodies =
    {
        Line(C3 + 12, 2, 0, 2, 4, 7, 9, 7, 4, 2).ToList(),        // pentatonic up and back
        Line(C3 + 12, 1.5, 7, 4, 0, 2, 4, 7, 12, 11, 7).ToList(), // a falling and rising run
        Line(C3 + 12, 3, 0, 5, 4, 2, 0).ToList(),                 // a slow phrase
        Line(C3 + 9, 2, 0, 3, 5, 7, 10, 7, 5, 3).ToList(),        // A minor pentatonic
    };

    private static readonly IReadOnlyList<PreviewNote>[] Pads =
    {
        Chords(C3, 8, On(Major, 0), On(Major, 5)).ToList(),         // C - F
        Chords(C3, 8, On(Minor7, 9), On(Major7, 5)).ToList(),       // Am7 - Fmaj7
        Chords(C3, 8, On(Sus4, 0), On(Major, 0)).ToList(),          // Csus4 resolving to C
        Chords(C3, 5, On(Minor, 4), On(Major, 0), On(Major, 7)).ToList(), // Em - C - G
    };

    private static readonly IReadOnlyList<PreviewNote>[] Basses =
    {
        Line(C3 - 12, 2, 0, 0, 12, 0, 7, 0, 10, 12).ToList(),     // octave groove
        Line(C3 - 12, 4, 0, 7, 9, 5).ToList(),                    // root notes of C G Am F
        Line(C3 - 12, 1, 0, 0, 3, 0, 5, 0, 7, 5, 3, 0, 10, 12, 0, 0, 7, 0).ToList(), // a busy minor riff
        Line(C3 - 15, 2, 0, 3, 5, 6, 7, 10, 12, 10).ToList(),     // walking up in A
    };

    private static readonly IReadOnlyList<PreviewNote>[] Beats =
    {
        new[] { N(0, 1, 36, 110), N(2, 1, 42, 80), N(4, 1, 38, 105), N(6, 1, 42, 80), N(8, 1, 36, 110), N(10, 1, 36, 95), N(12, 1, 38, 105), N(14, 1, 46, 85) },
        Enumerable.Range(0, 16).Select(i => N(i, 0.5, 42, i % 2 == 0 ? 90 : 60))
            .Concat(new[] { N(0, 1, 36, 112), N(7, 1, 36, 95), N(8, 1, 36, 108), N(4, 1, 38, 108), N(12, 1, 38, 108) }).ToList(),
        new[] { N(0, 1, 36, 110), N(3, 1, 36, 90), N(6, 1, 36, 100), N(4, 1, 38, 100), N(12, 1, 38, 104), N(10, 1, 37, 80), N(14, 1, 39, 90) }
            .Concat(Enumerable.Range(0, 8).Select(i => N(i * 2, 1, 44, 70))).ToList(),
    };

    // The phrases for a kind of sound.
    public static IReadOnlyList<IReadOnlyList<PreviewNote>> For(string category) => category switch
    {
        SoundCategories.Drums => Beats,
        SoundCategories.Bass => Basses,
        SoundCategories.Lead or SoundCategories.Woodwinds or SoundCategories.Brass or SoundCategories.Voices => Melodies,
        SoundCategories.Pad or SoundCategories.Strings or SoundCategories.SoundFx => Pads,
        _ => Keys,
    };

    // One phrase for the kind of sound, at random, not the one played last for that kind.
    public static IReadOnlyList<PreviewNote> Pick(string category, Random random, IDictionary<string, int> lastPicked)
    {
        var phrases = For(category);
        var last = lastPicked.TryGetValue(category, out var previous) ? previous : -1;
        var index = random.Next(phrases.Count);
        if (phrases.Count > 1 && index == last)
        {
            index = (index + 1 + random.Next(phrases.Count - 1)) % phrases.Count;
        }

        lastPicked[category] = index;
        return phrases[index];
    }
}
