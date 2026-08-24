using System.IO;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;

namespace Sonator.Services;

internal sealed class MidiFileImportService
{
    private const int StepsPerBar = 16;
    private const int BeatsPerBar = 4;

    public MidiImportDocument Import(string path)
    {
        var midiFile = MidiFile.Read(path);
        if (midiFile.TimeDivision is not TicksPerQuarterNoteTimeDivision timeDivision)
        {
            throw new InvalidDataException("SMPTE time division is not supported. Please use a standard MIDI file with ticks per quarter note.");
        }

        var ticksPerSixteenth = timeDivision.TicksPerQuarterNote / 4.0;
        if (ticksPerSixteenth <= 0)
        {
            throw new InvalidDataException("The MIDI file has an invalid time division.");
        }

        var tempo = midiFile.GetTempoMap().GetTempoAtTime((MidiTimeSpan)0).BeatsPerMinute;
        var sourceTracks = midiFile
            .GetTrackChunks()
            .Select((track, index) => new MidiSourceTrack(
                index + 1,
                track.Events
                    .OfType<SequenceTrackNameEvent>()
                    .Select(@event => @event.Text)
                    .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)),
                track.GetNotes().ToList(),
                track.Events
                    .OfType<ProgramChangeEvent>()
                    .GroupBy(@event => (int)@event.Channel)
                    .ToDictionary(group => group.Key, group => (int)group.First().ProgramNumber)))
            .Where(track => track.Notes.Count > 0)
            .ToList();

        if (sourceTracks.Count == 0)
        {
            throw new InvalidDataException("The MIDI file does not contain any notes.");
        }

        var lastNoteEnd = sourceTracks.Max(track => track.Notes.Max(note => note.Time + note.Length));
        var ticksPerBar = timeDivision.TicksPerQuarterNote * BeatsPerBar;
        var barCount = (int)Math.Ceiling(lastNoteEnd / (double)ticksPerBar);
        var bars = new List<MidiImportBar>(barCount);
        for (var barIndex = 0; barIndex < barCount; barIndex++)
        {
            var barStart = barIndex * ticksPerBar;
            var lanes = CreateBarLanes(sourceTracks, barStart, ticksPerSixteenth);
            bars.Add(new MidiImportBar(barIndex + 1, lanes));
        }

        return new MidiImportDocument(
            Path.GetFileNameWithoutExtension(path),
            tempo,
            bars,
            sourceTracks.Count);
    }

    private static List<MidiImportLane> CreateBarLanes(
        IReadOnlyList<MidiSourceTrack> sourceTracks,
        long barStart,
        double ticksPerSixteenth)
    {
        var lanes = new List<MidiImportLane>();

        foreach (var sourceTrack in sourceTracks)
        {
            var notesByChannel = sourceTrack.Notes
                .Where(note => note.Time >= barStart && note.Time < barStart + (long)(ticksPerSixteenth * StepsPerBar))
                .GroupBy(note => note.Channel)
                .OrderBy(group => group.Key);

            foreach (var channelNotes in notesByChannel)
            {
                var voices = new List<List<MidiImportNote>>();
                foreach (var note in channelNotes.OrderBy(note => note.Time).ThenByDescending(note => note.Velocity))
                {
                    var stepIndex = Math.Clamp(
                        (int)Math.Round((note.Time - barStart) / ticksPerSixteenth, MidpointRounding.AwayFromZero),
                        0,
                        StepsPerBar - 1);
                    var durationSteps = Math.Max(
                        1,
                        (int)Math.Round(note.Length / ticksPerSixteenth, MidpointRounding.AwayFromZero));
                    var importedNote = new MidiImportNote(
                        stepIndex,
                        durationSteps,
                        note.NoteNumber,
                        note.Velocity);

                    var voice = voices.FirstOrDefault(candidate => candidate.All(existing => existing.StepIndex != stepIndex));
                    if (voice is null)
                    {
                        voice = new List<MidiImportNote>();
                        voices.Add(voice);
                    }

                    voice.Add(importedNote);
                }

                for (var voiceIndex = 0; voiceIndex < voices.Count; voiceIndex++)
                {
                    lanes.Add(new MidiImportLane(
                        sourceTrack.Index,
                        BuildLaneName(sourceTrack, (int)channelNotes.Key + 1, voiceIndex, voices.Count),
                        channelNotes.Key + 1,
                        sourceTrack.ProgramNumbers.GetValueOrDefault((int)channelNotes.Key),
                        voices[voiceIndex]));
                }
            }
        }

        return lanes;
    }

    private static string BuildLaneName(MidiSourceTrack sourceTrack, int channel, int voiceIndex, int voiceCount)
    {
        var name = string.IsNullOrWhiteSpace(sourceTrack.Name)
            ? $"Track {sourceTrack.Index} CH {channel}"
            : sourceTrack.Name;
        return voiceCount > 1 ? $"{name} V{voiceIndex + 1}" : name;
    }

    private sealed record MidiSourceTrack(
        int Index,
        string? Name,
        List<Note> Notes,
        IReadOnlyDictionary<int, int> ProgramNumbers);
}

internal sealed record MidiImportDocument(
    string Name,
    double TempoBpm,
    IReadOnlyList<MidiImportBar> Bars,
    int SourceTrackCount);

internal sealed record MidiImportBar(int Number, IReadOnlyList<MidiImportLane> Lanes);

internal sealed record MidiImportLane(
    int SourceTrackIndex,
    string Name,
    int Channel,
    int ProgramNumber,
    IReadOnlyList<MidiImportNote> Notes);

internal sealed record MidiImportNote(int StepIndex, int DurationSteps, int NoteNumber, int Velocity);
