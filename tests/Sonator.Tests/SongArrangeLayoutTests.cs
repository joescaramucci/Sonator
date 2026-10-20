using Sonator.ViewModels;
using Xunit;

namespace Sonator.Tests;

public sealed class SongArrangeLayoutTests
{
    [Fact]
    public void TryFindPatternRange_FindsRunOfMatchingLabels()
    {
        var labels = new[] { "A", "A", "B", "A", "" };

        var found = SongArrangeLayout.TryFindPatternRange(labels.Length, row => labels[row], 1, out var start, out var length);

        Assert.True(found);
        Assert.Equal(0, start);
        Assert.Equal(2, length);
    }

    [Fact]
    public void TryFindPatternRange_SingleRowRun()
    {
        var labels = new[] { "A", "A", "B", "A", "" };

        SongArrangeLayout.TryFindPatternRange(labels.Length, row => labels[row], 3, out var start, out var length);

        Assert.Equal(3, start);
        Assert.Equal(1, length);
    }

    [Fact]
    public void TryFindPatternRange_EmptyLabel_ReturnsFalse()
    {
        var labels = new[] { "A", "" };

        var found = SongArrangeLayout.TryFindPatternRange(labels.Length, row => labels[row], 1, out var start, out var length);

        Assert.False(found);
        Assert.Equal(-1, start);
        Assert.Equal(0, length);
    }

    [Fact]
    public void TryFindPatternRange_MatchesLabelsIgnoringCase()
    {
        var labels = new[] { "Verse", "verse", "VERSE" };

        SongArrangeLayout.TryFindPatternRange(labels.Length, row => labels[row], 0, out var start, out var length);

        Assert.Equal(0, start);
        Assert.Equal(3, length);
    }

    [Theory]
    [InlineData(2, 2, 5, 8, 3)]   // moving down: requested row is shifted back by the block length
    [InlineData(2, 2, 0, 8, 0)]   // moving up: requested row is used as is
    [InlineData(2, 2, 2, 8, 2)]   // same position: caller treats the result as a no-op
    [InlineData(2, 2, 100, 8, 6)] // past the end: clamped to the rows that remain
    public void ResolveMoveRow_AdjustsAndClampsTarget(int sourceStart, int sourceLength, int requested, int rowCount, int expected)
    {
        var row = SongArrangeLayout.ResolveMoveRow(sourceStart, sourceLength, requested, rowCount);

        Assert.Equal(expected, row);
    }

    [Fact]
    public void MoveRows_MovesBlockDown()
    {
        var rows = Rows("A", "A", "B", "C");

        SongArrangeLayout.MoveRows(rows, sourceStart: 0, sourceLength: 2, finalRow: 2);

        Assert.Equal(new[] { "B", "C", "A", "A" }, rows.Select(row => row[0]).ToArray());
    }

    [Fact]
    public void MoveRows_MovesBlockUp()
    {
        var rows = Rows("A", "B", "C", "C");

        SongArrangeLayout.MoveRows(rows, sourceStart: 2, sourceLength: 2, finalRow: 0);

        Assert.Equal(new[] { "C", "C", "A", "B" }, rows.Select(row => row[0]).ToArray());
    }

    [Fact]
    public void MoveRows_AfterTarget_UsesResolvedRow()
    {
        // Insert the A block after the C row (requested row 4). Resolve it first, then move.
        var rows = Rows("A", "A", "B", "C");
        var finalRow = SongArrangeLayout.ResolveMoveRow(sourceStart: 0, sourceLength: 2, requestedRow: 4, rowCount: rows.Count);

        SongArrangeLayout.MoveRows(rows, 0, 2, finalRow);

        Assert.Equal(new[] { "B", "C", "A", "A" }, rows.Select(row => row[0]).ToArray());
    }

    private static List<string[]> Rows(params string[] labels)
    {
        return labels.Select(label => new[] { label }).ToList();
    }
}
