using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Sonator.ViewModels;

namespace Sonator.Services;

// A named place in the song, on the timeline's ruler.
public sealed record SongMarker(int Step, string Name);

// Writes the song as a standard MIDI file: a first track with the tempo, its changes, the time signature
// and the markers, then one track per track of the patterns, with its notes and controllers on its channel.
// Patterns played one after another (the arrangement) follow each other; a track keeps its place in each.
public static class MidiFileExport
{
    public const int TicksPerQuarter = 480;
    private const int TicksPerStep = TicksPerQuarter / 4;

    // patterns: each with the step it starts at. lengthSteps: where the song ends, to cut notes running past
    // the end of a pattern played in the arrangement; null keeps every note of a single pattern.
    public static MidiFile Build(
        IReadOnlyList<(PatternViewModel Pattern, int StartStep, int? LengthSteps)> patterns,
        TempoMap tempoMap,
        string timeSignature,
        IReadOnlyList<SongMarker> markers)
    {
        var chunks = new List<TrackChunk> { ConductorTrack(tempoMap, timeSignature, markers) };
        var trackCount = patterns.Select(item => item.Pattern.Tracks.Count).DefaultIfEmpty(0).Max();
        for (var index = 0; index < trackCount; index++)
        {
            var events = new List<(long Tick, int Order, MidiEvent Event)>();
            string? name = null;
            var channel = 0;
            var program = 0;
            foreach (var (pattern, startStep, length) in patterns)
            {
                if (index >= pattern.Tracks.Count)
                {
                    continue;
                }

                var track = pattern.Tracks[index];
                name ??= track.PluginAndSoundName is { } sound ? $"{track.Name} ({sound})" : track.Name;
                channel = Math.Clamp(track.Channel - 1, 0, 15);
                program = track.ProgramNumber;
                AddNotes(events, track, startStep, length, channel);
                AddControllers(events, track, startStep, length, channel);
            }

            if (events.Count == 0)
            {
                continue; // a track with nothing to play is left out
            }

            events.Add((0, 0, new SequenceTrackNameEvent(name ?? $"Track {index + 1}")));
            events.Add((0, 1, new ProgramChangeEvent((SevenBitNumber)Math.Clamp(program, 0, 127)) { Channel = (FourBitNumber)channel }));
            chunks.Add(ToChunk(events));
        }

        return new MidiFile(chunks) { TimeDivision = new TicksPerQuarterNoteTimeDivision(TicksPerQuarter) };
    }

    private static TrackChunk ConductorTrack(TempoMap tempoMap, string timeSignature, IReadOnlyList<SongMarker> markers)
    {
        var events = new List<(long Tick, int Order, MidiEvent Event)>
        {
            (0, 0, new SequenceTrackNameEvent("Sonator")),
            (0, 1, Tempo(tempoMap.BaseBpm)),
        };

        var parts = timeSignature.Split('/');
        if (parts.Length == 2 && int.TryParse(parts[0], out var numerator) && int.TryParse(parts[1], out var denominator) && numerator > 0 && denominator > 0)
        {
            events.Add((0, 1, new TimeSignatureEvent((byte)numerator, (byte)denominator)));
        }

        events.AddRange(tempoMap.Changes.Select(change => ((long)change.Step * TicksPerStep, 1, (MidiEvent)Tempo(change.Bpm))));
        events.AddRange(markers.Select(marker => ((long)marker.Step * TicksPerStep, 2, (MidiEvent)new MarkerEvent(marker.Name))));
        return ToChunk(events);
    }

    private static SetTempoEvent Tempo(double bpm) => new((long)Math.Round(60_000_000.0 / bpm));

    private static void AddNotes(List<(long, int, MidiEvent)> events, PatternTrackViewModel track, int startStep, int? length, int channel)
    {
        IEnumerable<(double Start, double Length, int Note, int Velocity)> notes = track.MidiNoteEvents.Count > 0
            ? track.MidiNoteEvents.Select(note => (note.StepIndex + note.StartOffset, note.DurationSteps + note.LengthFraction, note.NoteNumber, note.Velocity))
            : track.Steps.Where(step => step.IsActive)
                .Select(step => ((double)step.Index, (double)Math.Max(1, step.DurationSteps), step.MidiNoteNumber ?? track.NoteNumber, step.Velocity));

        foreach (var (start, noteLength, note, velocity) in notes)
        {
            if (length is { } end && start >= end)
            {
                continue;
            }

            var stop = length is { } limit ? Math.Min(start + Math.Max(noteLength, 0.05), limit) : start + Math.Max(noteLength, 0.05);
            var noteNumber = (SevenBitNumber)Math.Clamp(note, 0, 127);
            events.Add((Ticks(startStep + start), 3, new NoteOnEvent(noteNumber, (SevenBitNumber)Math.Clamp(velocity, 1, 127)) { Channel = (FourBitNumber)channel }));
            events.Add((Math.Max(Ticks(startStep + start) + 1, Ticks(startStep + stop)), 1, new NoteOffEvent(noteNumber, (SevenBitNumber)0) { Channel = (FourBitNumber)channel }));
        }
    }

    private static void AddControllers(List<(long, int, MidiEvent)> events, PatternTrackViewModel track, int startStep, int? length, int channel)
    {
        foreach (var controller in track.MidiControllerEvents)
        {
            if (length is { } end && controller.Step >= end)
            {
                continue;
            }

            MidiEvent midiEvent = controller.Controller switch
            {
                MidiControllerMessage.PitchBend => new PitchBendEvent((ushort)Math.Clamp(Math.Round(controller.Value * 16383), 0, 16383)),
                MidiControllerMessage.Aftertouch => new ChannelAftertouchEvent(Seven(controller.Value)),
                var number => new ControlChangeEvent((SevenBitNumber)Math.Clamp(number, 0, 127), Seven(controller.Value)),
            };
            ((ChannelEvent)midiEvent).Channel = (FourBitNumber)channel;
            events.Add((Ticks(startStep + controller.Step), 2, midiEvent));
        }
    }

    private static SevenBitNumber Seven(double value) => (SevenBitNumber)Math.Clamp((int)Math.Round(value * 127), 0, 127);

    private static long Ticks(double step) => (long)Math.Round(step * TicksPerStep);

    // Events in time order (at the same moment: names and tempos, note-offs, controllers, then note-ons), as
    // the file wants them, each timed from the one before.
    private static TrackChunk ToChunk(List<(long Tick, int Order, MidiEvent Event)> events)
    {
        var chunk = new TrackChunk();
        var previous = 0L;
        foreach (var (tick, _, midiEvent) in events.OrderBy(item => item.Tick).ThenBy(item => item.Order))
        {
            midiEvent.DeltaTime = tick - previous;
            previous = tick;
            chunk.Events.Add(midiEvent);
        }
        return chunk;
    }
}
