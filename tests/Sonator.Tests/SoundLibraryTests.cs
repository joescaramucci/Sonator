using Sonator.Services.Sounds;
using Xunit;

namespace Sonator.Tests;

public sealed class SoundLibraryTests
{
    [Theory]
    [InlineData("BARSAX1", "Organ", "", SoundCategories.Woodwinds)] // filed oddly by its maker, named for what it is
    [InlineData("Saxsoon", "Brass & Winds", "Brass Synth", SoundCategories.Woodwinds)]
    [InlineData("Big Section", "Brass & Winds", "Brass Acoustic", SoundCategories.Brass)]
    [InlineData("Solo Violin", "Keys", "", SoundCategories.Strings)]
    [InlineData("Lush", "Strings", "String Ensemble", SoundCategories.Strings)]
    [InlineData("Desert Piano", "Electric Piano", "Classic EP", SoundCategories.ElectricPiano)]
    [InlineData("Concert", "Piano", "Acoustic Grand", SoundCategories.Piano)]
    [InlineData("Wobble", "Bass", "Hard Bass", SoundCategories.Bass)]
    [InlineData("Low Reed", "Brass & Winds", "Winds Acoustic", SoundCategories.Woodwinds)]
    [InlineData("Night Air", "Pad", "Evolving Pad", SoundCategories.Pad)]
    [InlineData("Breaks", "Drums", "Drum Loop", SoundCategories.Drums)]
    [InlineData("Glass", "Keys", "Bells", SoundCategories.Bells)]
    [InlineData("Up And Down", "Sequence", "Arpeggio", SoundCategories.Sequence)]
    [InlineData("Angels", "Vocal", "Synth Choir", SoundCategories.Voices)]
    [InlineData("Wind Gust", "Sound Effects", "Nature Sounds", SoundCategories.SoundFx)]
    [InlineData("Plain", "None", "", SoundCategories.Other)]
    public void Sounds_FallInTheCategoryOfWhatTheyAre(string name, string type, string subtype, string category)
    {
        Assert.Equal(category, SoundCategories.Categorize(name, type, subtype));
    }

    [Fact]
    public void APreset_IsListed_OnlyWhenItsPlugInIsInstalled()
    {
        var installed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["CMI V"] = @"C:\Program Files\Common Files\VST3\Arturia\CMI V.vst3",
        };

        var sax = ArturiaSoundSource.ToEntry("SAX1", "Organ", "", "C:/ProgramData/Arturia/Presets/CMI V/Factory/REEDS2/SAX1", "Arturia", "Dry", installed);
        var older = ArturiaSoundSource.ToEntry("Ruins", "Bass", "", "C:/ProgramData/Arturia/Presets/CS-80 V3/Factory/Bass/Ruins", "", "", installed);
        var template = ArturiaSoundSource.ToEntry("Init", "-Templates-", "", "C:/ProgramData/Arturia/Presets/CMI V/Factory/Init/Init", "", "", installed);

        Assert.NotNull(sax);
        Assert.Equal("CMI V", sax!.PluginName);
        Assert.Equal(installed["CMI V"], sax.PluginPath);
        Assert.Equal("REEDS2", sax.Bank);
        Assert.Equal(SoundCategories.Woodwinds, sax.Category);
        Assert.Null(older);
        Assert.Null(template);
    }

    [Fact]
    public void Search_NeedsEveryWord_InAnyField_WithinTheCategory()
    {
        var sounds = new[]
        {
            new SoundEntry("SAX1", SoundCategories.Woodwinds, "", "CMI V", "cmi.vst3", "p1", "REEDS2", "Arturia", "Dry"),
            new SoundEntry("Smoky Sax", SoundCategories.Woodwinds, "Winds Acoustic", "Emulator II V", "e2.vst3", "p2", "Jazz", "Someone", "Warm"),
            new SoundEntry("Warm Pad", SoundCategories.Pad, "Evolving Pad", "Jup-8 V4", "j8.vst3", "p3", "Pads", "", "Warm"),
        };

        Assert.Equal(new[] { "SAX1", "Smoky Sax" }, SoundSearch.Filter(sounds, "sax", null).Select(s => s.Name));
        Assert.Equal(new[] { "Smoky Sax" }, SoundSearch.Filter(sounds, "sax warm", null).Select(s => s.Name));
        Assert.Equal(new[] { "Warm Pad" }, SoundSearch.Filter(sounds, "warm", SoundCategories.Pad).Select(s => s.Name));
        Assert.Equal(new[] { "SAX1" }, SoundSearch.Filter(sounds, "cmi", null).Select(s => s.Name));
        Assert.Equal(3, SoundSearch.Filter(sounds, "  ", null).Count());
    }
}
