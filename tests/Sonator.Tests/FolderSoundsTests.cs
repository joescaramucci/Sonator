using System.IO;
using Sonator.Services.Sounds;
using Xunit;

namespace Sonator.Tests;

public sealed class FolderSoundsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"sonator-folders-{Guid.NewGuid():N}");

    private static readonly IReadOnlyDictionary<string, string> Installed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Kontakt 7 Portable"] = "kp.vst3",
        ["Kontakt 7"] = "k7.vst3",
    };

    private string File(string relative, int bytes = 16)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void KontaktInstruments_AreFiledByTheirLibrary_WeighingAllItsFiles_HeardFromTheirPreviews()
    {
        var strings = File(@"Libraries\Orchestra One\Instruments\Strings\Strings Long.nki");
        File(@"Libraries\Orchestra One\Instruments\Strings\.previews\Strings Long.nki.ogg"); // beside the instrument
        File(@"Libraries\Orchestra One\Samples\strings.nkx", bytes: 4000);
        File(@"Libraries\Orchestra One\Samples\Strings Long C3.wav"); // the library's own sample

        var sounds = FolderSoundSource.Read(new[] { _root }, Installed);

        var instrument = Assert.Single(sounds);
        Assert.Equal(SoundKind.KontaktInstrument, instrument.Kind);
        Assert.Equal("Strings Long", instrument.Name);
        Assert.Equal("Orchestra One", instrument.Designer);
        Assert.Equal("Strings", instrument.Bank);
        Assert.Equal(SoundCategories.Strings, instrument.Category);
        Assert.Equal("Kontakt 7", instrument.PluginName); // the plain Kontakt, of the latest version
        Assert.Equal(strings, instrument.PresetPath);
        Assert.True(instrument.Bytes >= 4000);
        Assert.EndsWith(@".previews\Strings Long.nki.ogg", FolderSoundSource.PreviewOf(instrument));
    }

    [Fact]
    public void ALibraryChosenItself_IsNamedAfterItsFolder_AndListedOnceWhenReachedTwice()
    {
        File(@"Albion\Instruments\Brass\Brass Low.nki");

        var sounds = FolderSoundSource.Read(new[] { Path.Combine(_root, "Albion"), _root }, Installed);

        var brass = Assert.Single(sounds);
        Assert.Equal("Albion", brass.Designer);
    }

    [Fact]
    public void Samples_AreFiledByNameAndFolder_LoopsWithTheTempoTheirNameSays()
    {
        File(@"Packs\Deep House\Drums\Kick 01.wav");
        File(@"Packs\Deep House\Loops\Bass Loop 122.wav");
        File(@"Packs\Deep House\.previews\Kit.mxgrp.ogg"); // a maker's own preview

        var sounds = FolderSoundSource.Read(new[] { Path.Combine(_root, "Packs") }, Installed).ToDictionary(sound => sound.Name);

        Assert.Equal(2, sounds.Count);
        Assert.Equal(SoundCategories.Drums, sounds["Kick 01"].Category);
        Assert.Equal("One-shot", sounds["Kick 01"].Subcategory);
        Assert.Equal("Deep House", sounds["Kick 01"].Designer);
        Assert.Equal(SoundCategories.Bass, sounds["Bass Loop 122"].Category);
        Assert.Equal("Loop · 122 BPM", sounds["Bass Loop 122"].Subcategory);
        Assert.Equal(SoundKind.Sample, sounds["Bass Loop 122"].Kind);
    }

    [Theory]
    [InlineData("Groove_120bpm", false, 120.0)]
    [InlineData("Drums 92 BPM Am", false, 92.0)]
    [InlineData("Funk Loop 98", true, 98.0)]
    [InlineData("Kick 01", false, null)]
    [InlineData("Snare 808 Tight", false, null)] // an 808 is not a tempo
    public void BpmOf_ReadsTheTempoInAName(string name, bool loop, double? bpm) =>
        Assert.Equal(bpm, FolderSoundSource.BpmOf(name, loop));

    [Fact]
    public void Weights_AreShownInGigabytesOrMegabytes()
    {
        Assert.Equal("51.7 GB", SoundEntry.WeightOf((long)(51.7 * (1L << 30))));
        Assert.Equal("820 MB", SoundEntry.WeightOf(820L << 20));
        Assert.True(new SoundEntry("", "", "", "", "", "", "", "", "", SoundKind.KontaktInstrument, Bytes: 3L << 30).IsHeavy);
    }

    [Fact]
    public void ANewSong_GetsLoopsButNotOneShots_AndOrchestralKontaktLibrariesForFilmMusic()
    {
        var cinematic = MusicStyles.Named("Cinematic")!;
        var strings = cinematic.Roles.Single(role => role.Name == "Strings");
        var lofi = MusicStyles.Named("Lo-fi & Chillhop")!;
        var drums = lofi.Roles.Single(role => role.Name == "Drums");
        var albion = new SoundEntry("Strings Long", SoundCategories.Strings, "Kontakt · 52.0 GB library", "Kontakt 7", "k7.vst3", "a.nki",
            "Albion One Orchestra", "Spitfire Audio Albion ONE", "kontakt spitfire audio albion one", SoundKind.KontaktInstrument, Bytes: 52L << 30);
        var synth = new SoundEntry("Synth Strings", SoundCategories.Strings, "", "Jup-8 V4", "j8.vst3", "s", "Factory", "", "", Genres: "80s");
        var oneShot = new SoundEntry("Kick 01", SoundCategories.Drums, "One-shot", "Audio file", "", "k.wav", "Drums", "Lofi Pack", "sample one-shot lofi");
        var loop = new SoundEntry("Dusty Beat 85", SoundCategories.Drums, "Loop · 85 BPM", "Audio file", "", "l.wav", "Loops", "Lofi Pack", "sample loop lofi dusty", SoundKind.Sample);

        Assert.Equal("Strings Long", SoundMatcher.Suggest(new[] { synth, albion }, cinematic, strings)[0].Name);
        Assert.Contains(SoundMatcher.GenreKey("Cinematic"), SoundMatcher.GenresOf(albion));
        Assert.Equal(new[] { "Dusty Beat 85" }, SoundMatcher.Suggest(new[] { oneShot with { Kind = SoundKind.Sample }, loop }, lofi, drums).Select(sound => sound.Name));
    }
}
