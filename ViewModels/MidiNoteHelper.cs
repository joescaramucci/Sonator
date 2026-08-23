namespace Sonator.ViewModels;

public static class MidiNoteHelper
{
    private static readonly string[] Names = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];

    public static string ToLabel(int midiNoteNumber)
    {
        var name = Names[midiNoteNumber % 12];
        var octave = midiNoteNumber / 12 - 1;
        return $"{name}{octave}";
    }
}
