namespace Sonator.ViewModels;

// Row arithmetic for the song arrangement. Each row holds one arranged pattern label per column.
internal static class SongArrangeLayout
{
    // Finds the run of consecutive rows that share the label at the given row.
    // Returns false when that label is empty.
    public static bool TryFindPatternRange(
        int rowCount,
        Func<int, string> labelAt,
        int row,
        out int startRow,
        out int length)
    {
        startRow = -1;
        length = 0;
        var label = labelAt(row);
        if (string.IsNullOrWhiteSpace(label))
        {
            return false;
        }

        startRow = row;
        while (startRow > 0 && IsSameLabel(labelAt(startRow - 1), label))
        {
            startRow--;
        }

        var endRow = row;
        while (endRow + 1 < rowCount && IsSameLabel(labelAt(endRow + 1), label))
        {
            endRow++;
        }

        length = endRow - startRow + 1;
        return true;
    }

    // Converts a requested insertion row, given before the block is removed, into the row
    // where the block ends up. The result is clamped to the rows that remain.
    public static int ResolveMoveRow(int sourceStart, int sourceLength, int requestedRow, int rowCount)
    {
        var row = requestedRow > sourceStart ? requestedRow - sourceLength : requestedRow;
        var remainingRowCount = rowCount - sourceLength;
        return Math.Clamp(row, 0, remainingRowCount);
    }

    public static void MoveRows(List<string[]> rows, int sourceStart, int sourceLength, int finalRow)
    {
        var movedRows = rows.GetRange(sourceStart, sourceLength);
        rows.RemoveRange(sourceStart, sourceLength);
        rows.InsertRange(finalRow, movedRows);
    }

    private static bool IsSameLabel(string candidate, string label)
    {
        return string.Equals(candidate, label, StringComparison.OrdinalIgnoreCase);
    }
}
