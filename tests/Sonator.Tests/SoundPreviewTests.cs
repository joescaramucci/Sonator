using Sonator.Services.Sounds;
using Sonator.ViewModels;
using Xunit;

namespace Sonator.Tests;

public sealed class SoundPreviewTests
{
    [Fact]
    public void ASound_IsTriedWithAnArpeggioAndAChord_InC()
    {
        var phrase = SoundPreviewer.Phrase(SoundCategories.Pad);

        Assert.Equal(new[] { 48, 52, 55, 60 }, phrase.Take(4).Select(note => note.NoteNumber));
        Assert.Equal(4, phrase.Count(note => note.Start == 8)); // the chord, all together
    }

    [Fact]
    public void ABass_IsTriedAnOctaveDown_AndADrumSound_WithABeat()
    {
        Assert.Equal(36, SoundPreviewer.Phrase(SoundCategories.Bass)[0].NoteNumber);

        var beat = SoundPreviewer.Phrase(SoundCategories.Drums).Select(note => note.NoteNumber).ToList();
        Assert.Contains(36, beat); // kick
        Assert.Contains(38, beat); // snare
        Assert.Contains(42, beat); // hi-hat
    }
}
