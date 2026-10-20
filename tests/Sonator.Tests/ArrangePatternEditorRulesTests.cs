using Sonator.ViewModels;
using Xunit;

namespace Sonator.Tests;

public sealed class ArrangePatternEditorRulesTests
{
    [Fact]
    public void TryParseInput_ValidInput_TrimsNameAndPadsSlot()
    {
        var ok = ArrangePatternEditorRules.TryParseInput("  Verse  ", " 7 ", 2, out var name, out var slot, out var error);

        Assert.True(ok);
        Assert.Equal("Verse", name);
        Assert.Equal("07", slot);
        Assert.Equal(string.Empty, error);
    }

    [Fact]
    public void TryParseInput_BlankName_Fails()
    {
        var ok = ArrangePatternEditorRules.TryParseInput("   ", "1", 0, out _, out _, out var error);

        Assert.False(ok);
        Assert.Equal("Pattern name cannot be empty.", error);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("100")]
    [InlineData("abc")]
    public void TryParseInput_AssignmentOutOfRange_Fails(string assignment)
    {
        var ok = ArrangePatternEditorRules.TryParseInput("Verse", assignment, 0, out _, out _, out var error);

        Assert.False(ok);
        Assert.Equal("Assignment number must be between 1 and 99.", error);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    public void TryParseInput_SubChannelOutOfRange_Fails(int subChannel)
    {
        var ok = ArrangePatternEditorRules.TryParseInput("Verse", "1", subChannel, out _, out _, out var error);

        Assert.False(ok);
        Assert.Equal("Sub channel must be a, b, c or d.", error);
    }

    [Fact]
    public void TryParseInput_ReportsNameBeforeOtherErrors()
    {
        // With several invalid fields, the first failing check is the one reported.
        ArrangePatternEditorRules.TryParseInput(" ", "0", 9, out _, out _, out var error);

        Assert.Equal("Pattern name cannot be empty.", error);
    }

    [Fact]
    public void IsSlotTaken_IgnoresCaseAndDetectsMatch()
    {
        Assert.True(ArrangePatternEditorRules.IsSlotTaken(new[] { "01", "12" }, "12"));
        Assert.False(ArrangePatternEditorRules.IsSlotTaken(new[] { "01", "12" }, "13"));
    }

    [Fact]
    public void IsSubChannelOccupied_OnlyChecksTheGivenRange()
    {
        var labels = new[] { "", "X", "", "" };

        Assert.True(ArrangePatternEditorRules.IsSubChannelOccupied(0, 2, row => labels[row]));
        Assert.False(ArrangePatternEditorRules.IsSubChannelOccupied(2, 2, row => labels[row]));
    }
}
