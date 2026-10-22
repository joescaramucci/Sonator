using System.Collections.ObjectModel;
using Sonator.ViewModels;
using Xunit;

namespace Sonator.Tests;

public sealed class TimelineAndTrackRulesTests
{
    [Theory]
    [InlineData(0, "001.1")]
    [InlineData(3, "001.1")]
    [InlineData(4, "001.2")]
    [InlineData(15, "001.4")]
    [InlineData(16, "002.1")]
    [InlineData(35, "003.1")]
    public void FormatBarBeat_CountsBarsAndBeatsFromOne(int step, string expected)
    {
        Assert.Equal(expected, TimelineFormatting.FormatBarBeat(step));
    }

    [Fact]
    public void FormatBarBeat_UsesTwoDigitsWhenABarHasMoreThanNineBeats()
    {
        // 12/8: 24 steps per bar, a beat every 2 steps.
        Assert.Equal("001.12", TimelineFormatting.FormatBarBeat(22, 24, 2));
    }

    [Theory]
    [InlineData(new[] { 16 }, 1)]
    [InlineData(new[] { 17 }, 2)]
    [InlineData(new[] { 8, 33 }, 3)]
    [InlineData(new int[0], 1)]
    public void PatternBarCount_UsesLongestTrackRoundedUpToBars(int[] trackStepCounts, int expectedBars)
    {
        var tracks = trackStepCounts
            .Select(count => new PatternTrackViewModel("T", "I", 1, 60, Steps(count)))
            .ToList();
        var pattern = new PatternViewModel("P", "", "01", new ObservableCollection<PatternTrackViewModel>(tracks));

        Assert.Equal(expectedBars, TimelineFormatting.PatternBarCount(pattern));
    }

    [Fact]
    public void Validate_ValidDefinition_Passes()
    {
        Assert.True(TrackDefinitionRules.Validate("Bass", 1, 36, out var error));
        Assert.Equal(string.Empty, error);
    }

    [Fact]
    public void Validate_BlankName_Fails()
    {
        Assert.False(TrackDefinitionRules.Validate("  ", 1, 36, out var error));
        Assert.Equal("Track name is required.", error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(17)]
    public void Validate_ChannelOutOfRange_Fails(int channel)
    {
        Assert.False(TrackDefinitionRules.Validate("Bass", channel, 36, out var error));
        Assert.Equal("MIDI channel must be between 1 and 16.", error);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(128)]
    public void Validate_NoteOutOfRange_Fails(int noteNumber)
    {
        Assert.False(TrackDefinitionRules.Validate("Bass", 1, noteNumber, out var error));
        Assert.Equal("Base note must be between 0 and 127.", error);
    }

    private static ObservableCollection<StepViewModel> Steps(int count)
    {
        var steps = new ObservableCollection<StepViewModel>();
        for (var i = 0; i < count; i++)
        {
            steps.Add(new StepViewModel(i));
        }

        return steps;
    }
}
