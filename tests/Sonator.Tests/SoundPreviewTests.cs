using Sonator.Services.Sounds;
using Sonator.ViewModels;
using Xunit;

namespace Sonator.Tests;

public sealed class SoundPreviewTests
{
    [Fact]
    public void EveryKindOfSound_HasSeveralPhrases_InTune()
    {
        foreach (var category in SoundCategories.All)
        {
            var phrases = PreviewPhrases.For(category);
            Assert.True(phrases.Count >= 3, category);
            Assert.All(phrases, phrase => Assert.All(phrase, note =>
            {
                Assert.InRange(note.NoteNumber, 24, 96);
                Assert.True(note.Length > 0);
            }));
        }
    }

    [Fact]
    public void APhrase_IsPickedAtRandom_NeverTheSameTwiceRunning()
    {
        var random = new Random(7);
        var last = new Dictionary<string, int>();
        var picked = Enumerable.Range(0, 40).Select(_ => PreviewPhrases.Pick(SoundCategories.Piano, random, last)).ToList();

        Assert.All(picked.Zip(picked.Skip(1)), pair => Assert.NotSame(pair.First, pair.Second));
        Assert.True(picked.Distinct().Count() >= 3); // the phrases all come up
    }

    [Fact]
    public void Basses_PlayLow_AndDrums_PlayKickSnareAndHats()
    {
        Assert.All(PreviewPhrases.For(SoundCategories.Bass), riff => Assert.True(riff.Max(note => note.NoteNumber) <= 48));
        Assert.All(PreviewPhrases.For(SoundCategories.Drums), beat =>
        {
            var notes = beat.Select(note => note.NoteNumber).ToHashSet();
            Assert.Contains(36, notes);
            Assert.True(notes.Contains(38) || notes.Contains(37));
        });
    }
}
