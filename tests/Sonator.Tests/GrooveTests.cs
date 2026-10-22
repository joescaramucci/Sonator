using System.IO;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using Sonator.Services.Sounds;
using Xunit;

namespace Sonator.Tests;

public sealed class GrooveTests
{
    [Theory]
    [InlineData("000011@SUPERIOR_DRUMMER_3", "Superior Drummer 3")]
    [InlineData("117-S164@CHORUS", "Chorus")]
    [InlineData("01@EZD_POP#ROCK", "Ezd Pop/Rock")]
    [InlineData("406@SWING_4#4", "Swing 4/4")]
    [InlineData("N@14._Hats_16th_Tight_Closed.mid", "14. Hats 16th Tight Closed")]
    [InlineData("Variation_01.mid", "Variation 01")]
    [InlineData("On_The_Rhodes", "On The Rhodes")]
    public void FolderAndFileNames_ReadAsWords(string part, string clean)
    {
        Assert.Equal(clean, ToontrackGrooveSource.Clean(part));
    }

    [Fact]
    public void AGroove_IsNamedByItsPart_AndFiledUnderItsStyleAndLibrary()
    {
        var entry = ToontrackGrooveSource.ToEntry(
            @"000011@SUPERIOR_DRUMMER_3\406@SWING_4#4\117-S164@CHORUS\Variation_01.mid",
            @"C:\grooves\Variation_01.mid",
            "EZdrummer 3",
            @"C:\VST3\EZdrummer 3.vst3",
            SoundCategories.DrumGrooves);

        Assert.NotNull(entry);
        Assert.Equal("Chorus · Variation 01", entry!.Name);
        Assert.Equal("Swing 4/4", entry.Bank);
        Assert.Equal("Superior Drummer 3", entry.Designer);
        Assert.Equal(SoundCategories.DrumGrooves, entry.Category);
        Assert.Equal(SoundKind.Groove, entry.Kind);
        Assert.Contains("swing", entry.SearchText);
    }

    [Fact]
    public void FoldersOfOneStyle_AreToldApartByNumber()
    {
        var names = ToontrackGrooveSource.StyleNames(new[]
        {
            @"01@EZD_POP#ROCK\05@MOTOWN\GROOVE 01\A@01._Hats.mid",
            @"01@EZD_POP#ROCK\11@MOTOWN\GROOVE 01\A@01._Hats.mid",
            @"01@EZD_POP#ROCK\06@BALLAD\GROOVE 01\A@01._Hats.mid",
        });

        Assert.Equal("Motown", names[("01@EZD_POP#ROCK", "05@MOTOWN")]);
        Assert.Equal("Motown 2", names[("01@EZD_POP#ROCK", "11@MOTOWN")]);
        Assert.Equal("Ballad", names[("01@EZD_POP#ROCK", "06@BALLAD")]);
    }

    [Fact]
    public void AGroovesNotes_KeepTheirPlace_WithinTheirStep()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sonator-groove-{Guid.NewGuid():N}.mid");
        try
        {
            // 480 ticks a quarter note: a sixteenth is 120 ticks. A kick on the beat, a snare pushed late.
            var chunk = new TrackChunk();
            using (var notes = chunk.ManageNotes())
            {
                notes.Objects.Add(new Note((SevenBitNumber)36, 120, 0) { Velocity = (SevenBitNumber)110 });
                notes.Objects.Add(new Note((SevenBitNumber)38, 60, 4 * 120 + 30) { Velocity = (SevenBitNumber)90 });
            }
            new MidiFile(chunk) { TimeDivision = new TicksPerQuarterNoteTimeDivision(480) }.Write(path);

            var read = GrooveReader.Read(path);

            Assert.Equal(2, read.Count);
            Assert.Equal((0.0, 1.0, 36, 110), read[0]);
            Assert.Equal(4.25, read[1].Start, 6);
            Assert.Equal(0.5, read[1].Length, 6);
            Assert.Equal(90, read[1].Velocity);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
