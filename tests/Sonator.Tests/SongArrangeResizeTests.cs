using Sonator.ViewModels;
using Xunit;

namespace Sonator.Tests;

public sealed class SongArrangeResizeTests
{
    [Fact]
    public void ResizeRun_Grow_InsertsRowsWithSameLabelAfterRun()
    {
        var rows = Rows(
            new[] { "A", "", "" },
            new[] { "B", "", "" });

        SongArrangeLayout.ResizeRun(rows, startRow: 0, column: 0, currentLength: 1, requestedLength: 3);

        Assert.Equal(new[] { "A", "A", "A", "B" }, rows.Select(row => row[0]).ToArray());
        Assert.All(rows.Take(3), row => Assert.Equal(3, row.Length));
    }

    [Fact]
    public void ResizeRun_Shrink_RemovesRowsFromEndOfRun()
    {
        var rows = Rows(
            new[] { "A", "", "" },
            new[] { "A", "", "" },
            new[] { "A", "", "" },
            new[] { "B", "", "" });

        SongArrangeLayout.ResizeRun(rows, startRow: 0, column: 0, currentLength: 3, requestedLength: 1);

        Assert.Equal(new[] { "A", "B" }, rows.Select(row => row[0]).ToArray());
    }

    [Theory]
    [InlineData("8:0:0:0", 8)]
    [InlineData(" 4 : 0 : 0 : 0 ", 4)]
    [InlineData("0:0:0:0", null)]
    [InlineData("8:1:0:0", null)]
    [InlineData("8:0:2:0", null)]
    [InlineData("8:0:0:3", null)]
    [InlineData("8:0:0", null)]
    [InlineData("eight:0:0:0", null)]
    public void TryParseWholeBars_AcceptsOnlyWholeBarLengths(string input, int? expectedBars)
    {
        var parsed = SongArrangeLayout.TryParseWholeBars(input, out var bars);

        Assert.Equal(expectedBars.HasValue, parsed);
        if (expectedBars.HasValue)
        {
            Assert.Equal(expectedBars.Value, bars);
        }
    }

    private static List<string[]> Rows(params string[][] rows)
    {
        return rows.ToList();
    }
}
