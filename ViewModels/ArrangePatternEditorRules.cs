namespace Sonator.ViewModels;

// Validation for the arrange pattern editor. The checks run in the same order the editor reports them.
internal static class ArrangePatternEditorRules
{
    private const int MaxSubChannelIndex = 3;

    // Trims the name and turns the assignment number into a two-digit slot.
    public static bool TryParseInput(
        string patternName,
        string assignmentNumber,
        int subChannelIndex,
        out string targetPatternName,
        out string slot,
        out string errorMessage)
    {
        targetPatternName = patternName.Trim();
        slot = string.Empty;
        errorMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(targetPatternName))
        {
            errorMessage = "Pattern name cannot be empty.";
            return false;
        }

        if (!int.TryParse(assignmentNumber.Trim(), out var slotNumber) ||
            slotNumber < 1 ||
            slotNumber > PatternIdentifiers.MaxSlot)
        {
            errorMessage = $"Assignment number must be between 1 and {PatternIdentifiers.MaxSlot}.";
            return false;
        }

        if (subChannelIndex < 0 || subChannelIndex > MaxSubChannelIndex)
        {
            errorMessage = "Sub channel must be a, b, c or d.";
            return false;
        }

        slot = slotNumber.ToString("00");
        return true;
    }

    public static bool IsSlotTaken(IEnumerable<string> otherSlots, string slot)
    {
        return otherSlots.Any(otherSlot => string.Equals(otherSlot, slot, StringComparison.OrdinalIgnoreCase));
    }

    // True when any row in the range already has a label in the target sub channel.
    public static bool IsSubChannelOccupied(int startRow, int length, Func<int, string> labelAt)
    {
        return Enumerable.Range(startRow, length).Any(row => !string.IsNullOrWhiteSpace(labelAt(row)));
    }
}
