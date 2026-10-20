using Sonator.Services;
using Sonator.ViewModels;
using Xunit;

namespace Sonator.Tests;

public sealed class MidiImportTrackBuilderTests
{
    [Fact]
    public void GetInstrumentName_DrumChannel_ReturnsDrumKit()
    {
        Assert.Equal("Drum Kit", MidiImportTrackBuilder.GetInstrumentName(10, 0));
    }

    [Fact]
    public void GetInstrumentName_ProgramZero_ReturnsAcousticGrandPiano()
    {
        // General MIDI program numbers are zero-based in the file format.
        Assert.Equal("Acoustic Grand Piano", MidiImportTrackBuilder.GetInstrumentName(1, 0));
    }

    [Fact]
    public void GetInstrumentName_UnknownProgram_ReturnsNumberedFallback()
    {
        Assert.Equal("GM Program 201", MidiImportTrackBuilder.GetInstrumentName(1, 200));
    }

    [Theory]
    [InlineData("Piano V2", "Piano")]
    [InlineData("Piano", "Piano")]
    [InlineData("Bass Vintage", "Bass Vintage")]
    [InlineData("Lead V", "Lead V")]
    public void RemoveVoiceSuffix_StripsOnlyNumberedVoiceSuffix(string input, string expected)
    {
        Assert.Equal(expected, MidiImportTrackBuilder.RemoveVoiceSuffix(input));
    }

    [Fact]
    public void BuildImportedSongLanes_MergesSameTrackAcrossBarsWithStepOffset()
    {
        var bars = new List<MidiImportBar>
        {
            new(1, new List<MidiImportLane> { Lane("Piano V2", new MidiImportNote(0, 1, 60, 100)) }),
            new(2, new List<MidiImportLane> { Lane("Piano V2", new MidiImportNote(3, 1, 62, 90)) })
        };
        var document = new MidiImportDocument("Song", 120, bars, 1);

        var lanes = MidiImportTrackBuilder.BuildImportedSongLanes(document).ToList();

        var lane = Assert.Single(lanes);
        Assert.Equal("Piano", lane.Name);
        Assert.Equal(new[] { 0, 19 }, lane.Notes.Select(note => note.StepIndex).ToArray());
    }

    [Fact]
    public void CreateImportedTrack_PlacesNotesOnTheirSteps()
    {
        var lane = Lane("Piano", new MidiImportNote(2, 3, 64, 110));

        var track = MidiImportTrackBuilder.CreateImportedTrack(lane, stepCount: 32);

        Assert.Equal(32, track.Steps.Count);
        Assert.True(track.Steps[2].IsActive);
        Assert.Equal(64, track.Steps[2].MidiNoteNumber);
        Assert.Equal(110, track.Steps[2].Velocity);
        Assert.Equal(3, track.Steps[2].DurationSteps);
        Assert.False(track.Steps[3].IsActive);
        Assert.Equal(64, track.NoteNumber);
        Assert.Single(track.MidiNoteEvents);
    }

    [Fact]
    public void CreateImportedTrack_WithoutNotes_FallsBackToMiddleC()
    {
        var lane = Lane("Empty");

        var track = MidiImportTrackBuilder.CreateImportedTrack(lane, stepCount: 16);

        Assert.Equal(60, track.NoteNumber);
        Assert.Empty(track.MidiNoteEvents);
    }

    private static MidiImportLane Lane(string name, params MidiImportNote[] notes)
    {
        return new MidiImportLane(0, name, Channel: 1, ProgramNumber: 0, Notes: notes);
    }
}
