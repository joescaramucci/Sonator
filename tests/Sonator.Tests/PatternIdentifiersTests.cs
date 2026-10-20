using Sonator.ViewModels;
using Xunit;

namespace Sonator.Tests;

public sealed class PatternIdentifiersTests
{
    [Fact]
    public void NextAvailableSlot_NoPatterns_ReturnsFirstSlot()
    {
        Assert.Equal("01", PatternIdentifiers.NextAvailableSlot(Array.Empty<string>()));
    }

    [Fact]
    public void NextAvailableSlot_SkipsUsedSlots()
    {
        Assert.Equal("03", PatternIdentifiers.NextAvailableSlot(new[] { "01", "02" }));
    }

    [Fact]
    public void NextAvailableSlot_FillsGapsInOrder()
    {
        Assert.Equal("02", PatternIdentifiers.NextAvailableSlot(new[] { "01", "03" }));
    }

    [Fact]
    public void NextAvailableSlot_AllSlotsUsed_ReturnsNull()
    {
        var all = Enumerable.Range(1, 99).Select(number => number.ToString("00")).ToArray();

        Assert.Null(PatternIdentifiers.NextAvailableSlot(all));
    }

    [Fact]
    public void CreateUniqueCopyName_NoConflict_UsesCopySuffix()
    {
        Assert.Equal("Verse COPY", PatternIdentifiers.CreateUniqueCopyName(new[] { "Verse" }, "Verse"));
    }

    [Fact]
    public void CreateUniqueCopyName_ExistingCopies_NumbersTheNext()
    {
        var names = new[] { "Verse", "verse copy", "Verse COPY 2" };

        Assert.Equal("Verse COPY 3", PatternIdentifiers.CreateUniqueCopyName(names, "Verse"));
    }

    [Fact]
    public void InsertPatternRows_PlacesSlotInRequestedColumn()
    {
        var rows = new List<string[]>
        {
            new[] { "A", "", "", "" },
            new[] { "B", "", "", "" }
        };

        SongArrangeLayout.InsertPatternRows(rows, insertRow: 1, count: 2, column: 2, slot: "07");

        Assert.Equal(4, rows.Count);
        Assert.Equal(new[] { "A", "B" }, new[] { rows[0][0], rows[3][0] });
        Assert.Equal("07", rows[1][2]);
        Assert.Equal("07", rows[2][2]);
        Assert.Equal("", rows[1][0]);
    }
}
