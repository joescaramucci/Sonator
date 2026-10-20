using System.Collections.ObjectModel;
using System.Windows.Media;
using Sonator.Models;
using Sonator.ViewModels;
using Xunit;

namespace Sonator.Tests;

public sealed class ProjectDocumentMapperTests
{
    [Fact]
    public void TrackRoundTrip_PreservesStepsNotesAndSettings()
    {
        var original = CreateTrackWithContent();

        var document = ProjectDocumentMapper.BuildTrackDocument(original);
        var restored = ProjectDocumentMapper.ApplyTrackDocument(document);

        Assert.Equal(original.Name, restored.Name);
        Assert.Equal(original.Instrument, restored.Instrument);
        Assert.Equal(original.Channel, restored.Channel);
        Assert.Equal(original.NoteNumber, restored.NoteNumber);
        Assert.Equal(original.ProgramNumber, restored.ProgramNumber);
        Assert.Equal(original.TrackType, restored.TrackType);
        Assert.Equal(original.Source, restored.Source);
        Assert.Equal(original.Volume, restored.Volume);
        Assert.Equal(original.IsRecordArmed, restored.IsRecordArmed);
        Assert.Equal(original.TrackColorBrush.Color, restored.TrackColorBrush.Color);

        Assert.Equal(original.Steps.Count, restored.Steps.Count);
        for (var i = 0; i < original.Steps.Count; i++)
        {
            Assert.Equal(original.Steps[i].IsActive, restored.Steps[i].IsActive);
            Assert.Equal(original.Steps[i].MidiNoteNumber, restored.Steps[i].MidiNoteNumber);
            Assert.Equal(original.Steps[i].Velocity, restored.Steps[i].Velocity);
            Assert.Equal(original.Steps[i].DurationSteps, restored.Steps[i].DurationSteps);
        }

        Assert.Equal(original.MidiNoteEvents.Count, restored.MidiNoteEvents.Count);
        for (var i = 0; i < original.MidiNoteEvents.Count; i++)
        {
            Assert.Equal(original.MidiNoteEvents[i].StepIndex, restored.MidiNoteEvents[i].StepIndex);
            Assert.Equal(original.MidiNoteEvents[i].NoteNumber, restored.MidiNoteEvents[i].NoteNumber);
            Assert.Equal(original.MidiNoteEvents[i].Velocity, restored.MidiNoteEvents[i].Velocity);
            Assert.Equal(original.MidiNoteEvents[i].DurationSteps, restored.MidiNoteEvents[i].DurationSteps);
        }
    }

    [Fact]
    public void PatternRoundTrip_PreservesNameSlotAndTracks()
    {
        var original = new PatternViewModel(
            "Verse",
            "Two tracks",
            "03",
            new ObservableCollection<PatternTrackViewModel> { CreateTrackWithContent(), PatternFactory.CreateTrack("Bass", "Synth", 2, 40, new[] { true, false }) });

        var restored = ProjectDocumentMapper.ApplyPatternDocument(ProjectDocumentMapper.BuildPatternDocument(original));

        Assert.Equal("Verse", restored.Name);
        Assert.Equal("Two tracks", restored.Summary);
        Assert.Equal("03", restored.Slot);
        Assert.Equal(2, restored.Tracks.Count);
        Assert.Equal("Bass", restored.Tracks[1].Name);
    }

    [Fact]
    public void ArrangeRowRoundTrip_PreservesLabelsAndAccentColor()
    {
        var cells = new ObservableCollection<ArrangeCellViewModel>
        {
            new(1, "A", "Intro", new SolidColorBrush(Color.FromArgb(0x88, 0x10, 0x20, 0x30)))
        };
        var original = new ArrangeRowViewModel("Drums", "Kit", cells);

        var restored = ProjectDocumentMapper.ApplyArrangeRowDocument(ProjectDocumentMapper.BuildArrangeRowDocument(original));

        Assert.Equal("Drums", restored.TrackName);
        Assert.Equal("Kit", restored.Instrument);
        Assert.Single(restored.Cells);
        Assert.Equal("A", restored.Cells[0].Label);
        Assert.Equal("Intro", restored.Cells[0].Detail);
        Assert.Equal(Color.FromArgb(0x88, 0x10, 0x20, 0x30), ((SolidColorBrush)restored.Cells[0].Accent).Color);
    }

    [Theory]
    [InlineData("1.0", true)]
    [InlineData("1.1", true)]
    [InlineData("1.4", false)]
    public void IsLegacyProject_IdentifiesOlderVersions(string version, bool expected)
    {
        Assert.Equal(expected, ProjectDocumentMapper.IsLegacyProject(version));
    }

    [Fact]
    public void CreateTrackColorBrush_NullOrBlank_ReturnsDefaultColor()
    {
        Assert.Equal(Color.FromRgb(38, 53, 69), PatternFactory.CreateTrackColorBrush(null).Color);
        Assert.Equal(Color.FromRgb(38, 53, 69), PatternFactory.CreateTrackColorBrush("   ").Color);
    }

    [Fact]
    public void CreateTrackColorBrush_OpaqueColor_IsMadeSemiTransparent()
    {
        var brush = PatternFactory.CreateTrackColorBrush("#FF112233");

        Assert.Equal(0x88, brush.Color.A);
        Assert.Equal(0x11, brush.Color.R);
    }

    [Fact]
    public void BuildDefaultPatterns_CreatesOnePatternWithEightSixteenStepTracks()
    {
        var patterns = PatternFactory.BuildDefaultPatterns();

        var pattern = Assert.Single(patterns);
        Assert.Equal(8, pattern.Tracks.Count);
        Assert.All(pattern.Tracks, track => Assert.Equal(16, track.Steps.Count));
    }

    private static PatternTrackViewModel CreateTrackWithContent()
    {
        var steps = new ObservableCollection<StepViewModel>();
        for (var i = 0; i < 16; i++)
        {
            steps.Add(new StepViewModel(i, isActive: i % 3 == 0)
            {
                MidiNoteNumber = i % 3 == 0 ? 60 + i : null,
                Velocity = 90 + (i % 5),
                DurationSteps = 1 + (i % 4)
            });
        }

        var notes = new List<MidiNoteEventViewModel>
        {
            new(0, 2, 64, 100),
            new(5, 1, 67, 80)
        };

        return new PatternTrackViewModel(
            name: "Lead",
            instrument: "Piano",
            channel: 4,
            noteNumber: 60,
            steps: steps,
            programNumber: 12,
            midiNoteEvents: notes,
            trackType: PatternTrackType.MidiChannel,
            source: "piano.mid",
            volume: 0.6,
            isRecordArmed: true,
            trackColorBrush: new SolidColorBrush(Color.FromArgb(0x88, 0x40, 0x50, 0x60)));
    }
}
