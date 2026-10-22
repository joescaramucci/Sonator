using Sonator.Models;
using Sonator.Services;
using Sonator.ViewModels;
using Xunit;

namespace Sonator.Tests;

public sealed class MeterTests
{
    [Theory]
    [InlineData("4/4", 16, 4)]
    [InlineData("3/4", 12, 4)]
    [InlineData("5/4", 20, 4)]
    [InlineData("6/8", 12, 2)]
    [InlineData("7/8", 14, 2)]
    [InlineData("2/2", 16, 8)]
    [InlineData("12/8", 24, 2)]
    public void Bar_AndBeat_AreCountedInSixteenths(string text, int stepsPerBar, int stepsPerBeat)
    {
        Assert.True(Meter.TryParse(text, out var meter));
        Assert.Equal(stepsPerBar, meter.StepsPerBar);
        Assert.Equal(stepsPerBeat, meter.StepsPerBeat);
        Assert.Equal(text, meter.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("4")]
    [InlineData("4/3")]
    [InlineData("0/4")]
    [InlineData("17/4")]
    [InlineData("a/b")]
    public void InvalidText_IsRejected(string text)
    {
        Assert.False(Meter.TryParse(text, out var meter));
        Assert.Equal(Meter.Common, meter);
    }

    [Fact]
    public void EveryPreset_IsValid()
    {
        Assert.All(Meter.Presets, preset => Assert.True(preset.IsValid, preset.ToString()));
    }

    // In 3/4 a bar is 12 steps: step 12 starts the second bar and step 16 is its second beat.
    // In 6/8 a beat is an eighth, so the beats count to 6.
    [Theory]
    [InlineData(11, 12, 4, "001.3")]
    [InlineData(12, 12, 4, "002.1")]
    [InlineData(16, 12, 4, "002.2")]
    [InlineData(10, 12, 2, "001.6")]
    public void Position_CountsBarsAndBeatsOfTheTimeSignature(int step, int stepsPerBar, int stepsPerBeat, string expected)
    {
        Assert.Equal(expected, TimelineFormatting.FormatBarBeat(step, stepsPerBar, stepsPerBeat));
    }

    [Fact]
    public void Click_AccentsTheFirstBeatOfEachBar()
    {
        using var engine = new SequencerEngine();
        engine.StepsPerBar = 12;
        engine.StepsPerBeat = 4;

        Assert.Equal(0, engine.ClickLevelForStep(0));
        Assert.Equal(1, engine.ClickLevelForStep(4));
        Assert.Equal(1, engine.ClickLevelForStep(8));
        Assert.Equal(0, engine.ClickLevelForStep(12));
        Assert.Equal(2, engine.ClickLevelForStep(2));
    }
}
