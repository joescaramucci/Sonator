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

    // Inserts count rows at insertRow. Each new row has the pattern slot in the given column.
    public static void InsertPatternRows(List<string[]> rows, int insertRow, int count, int column, string slot)
    {
        for (var offset = 0; offset < count; offset++)
        {
            var insertedRow = new string[4];
            Array.Fill(insertedRow, string.Empty);
            insertedRow[column] = slot;
            rows.Insert(insertRow + offset, insertedRow);
        }
    }

    // Grows or shrinks a run that starts at startRow. Growing copies the run's label into new rows
    // placed after the run. Shrinking removes rows from the end of the run.
    public static void ResizeRun(List<string[]> rows, int startRow, int column, int currentLength, int requestedLength)
    {
        if (requestedLength > currentLength)
        {
            var label = rows[startRow][column];
            for (var count = 0; count < requestedLength - currentLength; count++)
            {
                var insertedRow = new string[rows[startRow].Length];
                Array.Fill(insertedRow, string.Empty);
                insertedRow[column] = label;
                rows.Insert(startRow + currentLength + count, insertedRow);
            }
        }
        else
        {
            rows.RemoveRange(startRow + requestedLength, currentLength - requestedLength);
        }
    }

    // Accepts a length written as bars:quarters:eighths:sixteenths, where only whole bars are allowed.
    public static bool TryParseWholeBars(string value, out int bars)
    {
        bars = 0;
        var components = value.Split(':', StringSplitOptions.TrimEntries);
        if (components.Length != 4 ||
            !int.TryParse(components[0], out var parsedBars) ||
            !int.TryParse(components[1], out var quarters) ||
            !int.TryParse(components[2], out var eighths) ||
            !int.TryParse(components[3], out var sixteenths))
        {
            return false;
        }

        if (parsedBars < 1 || quarters != 0 || eighths != 0 || sixteenths != 0)
        {
            return false;
        }

        bars = parsedBars;
        return true;
    }

    private static bool IsSameLabel(string candidate, string label)
    {
        return string.Equals(candidate, label, StringComparison.OrdinalIgnoreCase);
    }
}
