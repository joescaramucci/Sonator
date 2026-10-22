using System.Collections.ObjectModel;
using Melanchall.DryWetMidi.Standards;
using Sonator.Services;

namespace Sonator.ViewModels;

// Turns parsed MIDI import data into pattern tracks: groups notes into lanes and names instruments.
internal static class MidiImportTrackBuilder
{
    internal static IEnumerable<MidiImportLane> BuildImportedSongLanes(MidiImportDocument importedSong)
    {
        var importedNotes = importedSong.Bars
            .SelectMany(bar => bar.Lanes.SelectMany(lane => lane.Notes.Select(note => new
            {
                Lane = lane,
                Note = note with { StepIndex = note.StepIndex + (bar.Number - 1) * 16 }
            })))
            .ToList();

        return importedNotes
            .GroupBy(item => (item.Lane.SourceTrackIndex, item.Lane.Channel))
            .OrderBy(group => group.Key.SourceTrackIndex)
            .ThenBy(group => group.Key.Channel)
            .Select(group =>
            {
                var firstLane = group.First().Lane;
                return new MidiImportLane(
                    firstLane.SourceTrackIndex,
                    RemoveVoiceSuffix(firstLane.Name),
                    group.Key.Channel,
                    firstLane.ProgramNumber,
                    group.Select(item => item.Note).ToList());
            });
    }

    internal static PatternTrackViewModel CreateImportedTrack(MidiImportLane lane, int stepCount)
    {
        var steps = new ObservableCollection<StepViewModel>(
            Enumerable.Range(0, stepCount).Select(index => new StepViewModel(index)));
        foreach (var note in lane.Notes)
        {
            var step = steps[note.StepIndex];
            step.IsActive = true;
            step.MidiNoteNumber = note.NoteNumber;
            step.Velocity = note.Velocity;
            step.DurationSteps = note.DurationSteps;
        }

        var firstNote = lane.Notes.FirstOrDefault();
        var midiNoteEvents = lane.Notes
            .Select(note => new MidiNoteEventViewModel(
                note.StepIndex,
                note.DurationSteps,
                note.NoteNumber,
                note.Velocity))
            .ToList();
        return PatternFactory.WithDefaultEffects(new PatternTrackViewModel(
            lane.Name,
            GetInstrumentName(lane.Channel, lane.ProgramNumber),
            lane.Channel,
            firstNote?.NoteNumber ?? 60,
            steps,
            lane.ProgramNumber,
            midiNoteEvents));
    }

    internal static string RemoveVoiceSuffix(string name)
    {
        var suffixIndex = name.LastIndexOf(" V", StringComparison.Ordinal);
        return suffixIndex >= 0 && int.TryParse(name[(suffixIndex + 2)..], out _)
            ? name[..suffixIndex]
            : name;
    }

    internal static string GetInstrumentName(int channel, int programNumber)
    {
        if (channel == 10)
        {
            return "Drum Kit";
        }

        var programName = Enum.GetName(typeof(GeneralMidiProgram), programNumber);
        if (programName is null)
        {
            return $"GM Program {programNumber + 1}";
        }

        return string.Concat(programName.Select((character, index) =>
            index > 0 && char.IsUpper(character) ? $" {character}" : character.ToString()));
    }
}
