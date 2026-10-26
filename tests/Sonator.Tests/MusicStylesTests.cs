using System.Threading;
using Sonator.Services;
using Sonator.Services.Sounds;
using Sonator.ViewModels;
using Xunit;

namespace Sonator.Tests;

public sealed class MusicStylesTests
{
    private static SoundEntry Preset(string name, string category, string plugin, string genres = "", string tags = "", string subcategory = "") =>
        new(name, category, subcategory, plugin, $"{plugin}.vst3", $@"C:\Presets\{plugin}\{name}", "Factory", "", tags, SoundKind.Preset, genres);

    private static SoundEntry Groove(string name, string library, string style) =>
        new(name, SoundCategories.DrumGrooves, "", "EZdrummer 3", "ezd.vst3", $@"C:\Grooves\{library}\{style}\{name}.mid",
            style, library, $"{library} {style} {name}", SoundKind.Groove);

    [Fact]
    public void EveryStyle_HasATempo_HitsAndPartsWithSoundsToChooseFrom()
    {
        Assert.True(MusicStyles.All.Count >= 15);
        Assert.Equal(MusicStyles.All.Count, MusicStyles.All.Select(style => style.Name).Distinct().Count());
        foreach (var style in MusicStyles.All)
        {
            Assert.InRange(style.Tempo, 60, 180);
            Assert.NotEmpty(style.Hits);
            Assert.Contains(style.TagGenre, SongTags.CommonGenres);
            Assert.InRange(style.Roles.Count, 4, 8);
            Assert.Equal(style.Roles.Count, style.Roles.Select(role => role.Name).Distinct().Count()); // each a track of its own name
            Assert.All(style.Roles, role => Assert.All(role.Categories, category => Assert.Contains(category, SoundCategories.All)));
        }
    }

    [Theory]
    [InlineData("ezd pop/rock verse", "pop", true)]
    [InlineData("popcorn percussion", "pop", false)]
    [InlineData("real reggae one drop 01", "one drop", true)]
    [InlineData("tr 808 kick", "808", true)]
    [InlineData("rom1b e.piano 3", "e.piano", true)]
    public void HasWord_FindsWholeWordsOnly(string text, string word, bool found) =>
        Assert.Equal(found, SoundMatcher.HasWord(text, word));

    [Fact]
    public void GenresOf_AreTheMakersGenres_SpacedOrNot()
    {
        var pad = Preset("Silk", SoundCategories.Pad, "Jup-8 V4", genres: "Soul / R&B|80s");

        Assert.Contains(SoundMatcher.GenreKey("Soul/R&B"), SoundMatcher.GenresOf(pad));
        Assert.Contains("80s", SoundMatcher.GenresOf(pad));
    }

    [Fact]
    public void ArturiaSounds_KeepTheirGenres()
    {
        var installed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Jup-8 V4"] = "j8.vst3" };

        var pad = ArturiaSoundSource.ToEntry("Silk", "Pad", "", "C:/ProgramData/Arturia/Presets/Jup-8 V4/Factory/Pads/Silk", "", "Warm", installed, "Pop|80s");

        Assert.Equal("Pop|80s", pad!.Genres);
    }

    [Fact]
    public void ASoundFiledUnderTheStyle_IsSuggestedBeforeOneOfAnother()
    {
        var pop = MusicStyles.Named("Pop")!;
        var pads = pop.Roles.Single(role => role.Name == "Pad");
        var sounds = new[]
        {
            Preset("Warehouse Drone", SoundCategories.Pad, "Pigments", genres: "Hard Techno|Industrial"),
            Preset("Radio Pad", SoundCategories.Pad, "Jup-8 V4", genres: "Pop|Modern", tags: "Bright Warm"),
            Preset("Radio Bass", SoundCategories.Bass, "Jup-8 V4", genres: "Pop"),
        };

        var suggested = SoundMatcher.Suggest(sounds, pop, pads);

        Assert.Equal(new[] { "Radio Pad", "Warehouse Drone" }, suggested.Select(sound => sound.Name)); // pads only, Pop's first
    }

    [Fact]
    public void APlayedStyle_GetsGroovesForItsDrums_AProgrammedOne_DrumMachineKits()
    {
        var sounds = new[]
        {
            Groove("Verse 01", "Real Reggae", "One Drop"),
            Preset("909 Territory Kit", SoundCategories.Drums, "Emulator II V", genres: "House|Techno|Reggae"),
        };
        var reggae = MusicStyles.Named("Reggae & Dub")!;
        var techno = MusicStyles.Named("Techno")!;

        Assert.Equal("Verse 01", SoundMatcher.Suggest(sounds, reggae, reggae.Roles.Single(role => role.Name == "Drums"))[0].Name);
        Assert.Equal("909 Territory Kit", SoundMatcher.Suggest(sounds, techno, techno.Roles.Single(role => role.Name == "Drums"))[0].Name);
    }

    [Fact]
    public void DrumParts_LeaveOutSoundsThatAreNotDrums_AndPutKitsBeforeSingleHits()
    {
        var techno = MusicStyles.Named("Techno")!;
        var drums = techno.Roles.Single(role => role.Name == "Drums");
        var sounds = new[]
        {
            Preset("Funky Clav", SoundCategories.Drums, "CS-80 V4", genres: "Techno", tags: "Drums"), // filed among the drums
            Preset("Dark Kick", SoundCategories.Drums, "ARP 2600 V3", genres: "Techno"),
            Preset("Warehouse Kit", SoundCategories.Drums, "Emulator II V", genres: "Techno"),
        };

        var suggested = SoundMatcher.Suggest(sounds, techno, drums).Select(sound => sound.Name).ToList();

        Assert.Equal(new[] { "Warehouse Kit", "Dark Kick" }, suggested);
    }

    [Fact]
    public void AGroovesFillOrEnding_ComesAfterItsBeats()
    {
        var rock = MusicStyles.Named("Rock")!;
        var drums = rock.Roles.Single(role => role.Name == "Drums");
        var sounds = new[] { Groove("Fill 03", "Rock Anthology", "Rock"), Groove("Verse 02", "Rock Anthology", "Rock") };

        Assert.Equal("Verse 02", SoundMatcher.Suggest(sounds, rock, drums)[0].Name);
    }

    [Fact]
    public void Suggestions_Vary_NoMoreThanTwoFromOneInstrumentOnAPage_AndOthersShowsMore()
    {
        var synthwave = MusicStyles.Named("Synthwave & 80s")!;
        var leads = synthwave.Roles.Single(role => role.Name == "Lead");
        var sounds = Enumerable.Range(1, 6).Select(index => Preset($"Jupiter {index}", SoundCategories.Lead, "Jup-8 V4", genres: "Synthwave|80s"))
            .Concat(Enumerable.Range(1, 6).Select(index => Preset($"Prophet {index}", SoundCategories.Lead, "Prophet-5 V", genres: "Synthwave")))
            .ToList();

        var first = SoundMatcher.Suggest(sounds, synthwave, leads, count: 4);
        var second = SoundMatcher.Suggest(sounds, synthwave, leads, count: 4, page: 1);

        Assert.Equal(4, first.Count);
        Assert.Equal(2, first.Count(sound => sound.PluginName == "Jup-8 V4"));
        Assert.Empty(first.Intersect(second));
    }

    [Fact]
    public void OfStyle_IsTheCatalogOfAStylesSounds()
    {
        var lofi = MusicStyles.Named("Lo-fi & Chillhop")!;
        var sounds = new[]
        {
            Preset("Dusty Keys", SoundCategories.ElectricPiano, "Stage-73 V", genres: "Lofi|Downtempo"),
            Preset("Rave Stab", SoundCategories.SynthKeys, "Jup-8 V4", genres: "Hard Techno"),
            Groove("Boom Bap 01", "Lofi Beats", "Hip Hop"),
        };

        Assert.Equal(new[] { "Dusty Keys", "Boom Bap 01" }, SoundMatcher.OfStyle(sounds, lofi).Select(sound => sound.Name));
    }

    [Fact]
    public void CreateSong_MakesATrackForEachPart_AtTheStylesTempo_WithItsGenre()
    {
        OnSta(() =>
        {
            using var viewModel = new MainViewModel();
            var house = MusicStyles.Named("House")!;
            var parts = new[]
            {
                ("Drums", Preset("909 Territory Kit", SoundCategories.Drums, "No Such Plugin")),
                ("Bass", Preset("Organ Bass", SoundCategories.Bass, "No Such Plugin")),
                ("Piano", Preset("M1 Piano", SoundCategories.Piano, "No Such Plugin")),
            };

            viewModel.CreateSong(house, 124, parts);

            Assert.Equal(124, viewModel.Tempo);
            Assert.Equal(new[] { "Drums", "Bass", "Piano" }, viewModel.SelectedPattern!.Tracks.Select(track => track.Name));
            Assert.Equal("Dance", viewModel.SongTags.Genre);
            Assert.Equal("House song: Drums, Bass, Piano", viewModel.SelectedPattern.Summary);
            Assert.Equal("New project — House song — SONATOR 1.0", viewModel.HeaderTitle); // the style always in sight
            var saved = viewModel.BuildProjectDocument();
            Assert.Equal("House", saved.Style); // kept with the song

            viewModel.NewProjectCommand.Execute(null);
            Assert.Null(viewModel.SongStyle);
            Assert.Equal("New project — SONATOR 1.0", viewModel.HeaderTitle);

            viewModel.LoadDocument(saved);
            Assert.Equal("House", viewModel.SongStyle); // and back when the song is opened
            Assert.False(viewModel.UndoCommand.CanExecute(null)); // the song starts here
        });
    }

    [Fact]
    public void TheArrows_GoThroughTheTimelinesTracks_StoppingAtTheEnds()
    {
        OnSta(() =>
        {
            using var viewModel = new MainViewModel();
            var tracks = viewModel.TimelineTracks.Select(lane => lane.SourceTrack).ToList();
            viewModel.SelectedTrack = tracks[0];

            Assert.True(viewModel.SelectAdjacentTrack(1));
            Assert.Same(tracks[1], viewModel.SelectedTrack);
            Assert.True(viewModel.SelectAdjacentTrack(-1));
            Assert.Same(tracks[0], viewModel.SelectedTrack);
            Assert.False(viewModel.SelectAdjacentTrack(-1)); // the first stays selected
            Assert.Same(tracks[0], viewModel.SelectedTrack);

            viewModel.SelectedTrack = tracks[^1];
            Assert.False(viewModel.SelectAdjacentTrack(1)); // and so does the last
        });
    }

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException(failure.ToString());
        }
    }
}
