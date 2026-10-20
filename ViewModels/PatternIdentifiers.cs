namespace Sonator.ViewModels;

// Naming and slot rules for patterns. Slots are two-digit identifiers from 01 to 99.
internal static class PatternIdentifiers
{
    public const int MaxSlot = 99;

    public static string? NextAvailableSlot(IEnumerable<string> usedSlots)
    {
        var used = new HashSet<string>(usedSlots, StringComparer.OrdinalIgnoreCase);
        for (var identifier = 1; identifier <= MaxSlot; identifier++)
        {
            var slot = identifier.ToString("00");
            if (!used.Contains(slot))
            {
                return slot;
            }
        }

        return null;
    }

    // Returns "<name> COPY", or "<name> COPY <n>" with the lowest n that is not taken.
    public static string CreateUniqueCopyName(IEnumerable<string> existingNames, string sourceName)
    {
        var used = new HashSet<string>(existingNames, StringComparer.OrdinalIgnoreCase);
        var copyNumber = 1;
        var copyName = $"{sourceName} COPY";
        while (used.Contains(copyName))
        {
            copyNumber++;
            copyName = $"{sourceName} COPY {copyNumber}";
        }

        return copyName;
    }
}
