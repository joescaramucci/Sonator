namespace Sonator.ViewModels;

// Checks the values entered for a track before it is added or updated.
internal static class TrackDefinitionRules
{
    private const int MinChannel = 1;
    private const int MaxChannel = 16;
    private const int MaxNoteNumber = 127;

    public static bool Validate(string name, int channel, int noteNumber, out string errorMessage)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            errorMessage = "Track name is required.";
            return false;
        }

        if (channel < MinChannel || channel > MaxChannel)
        {
            errorMessage = $"MIDI channel must be between {MinChannel} and {MaxChannel}.";
            return false;
        }

        if (noteNumber < 0 || noteNumber > MaxNoteNumber)
        {
            errorMessage = $"Base note must be between 0 and {MaxNoteNumber}.";
            return false;
        }

        errorMessage = string.Empty;
        return true;
    }
}
