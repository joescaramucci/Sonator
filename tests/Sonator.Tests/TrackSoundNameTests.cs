using Sonator.Services.Sounds;
using Sonator.ViewModels;
using Xunit;

namespace Sonator.Tests;

public sealed class TrackSoundNameTests
{
    [Fact]
    public void ATrack_ShowsItsPlugIn_ThenTheSoundLoaded_AfterADash()
    {
        var track = PatternFactory.CreatePattern("Pattern 1", "Test", "01").Tracks[0];
        track.PluginFilePath = @"C:\VST3\Arturia\Emulator II V.vst3";
        Assert.Equal("Emulator II V", track.PluginAndSoundName);

        track.SoundName = "Grand Piano";
        Assert.Equal("Emulator II V - Grand Piano", track.PluginAndSoundName);

        var saved = ProjectDocumentMapper.ApplyTrackDocument(ProjectDocumentMapper.BuildTrackDocument(track));
        Assert.Equal("Emulator II V - Grand Piano", saved.PluginAndSoundName);
    }

    [Fact]
    public void AnotherPlugIn_TakesTheSoundNameAway()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var viewModel = new MainViewModel();
                var track = viewModel.Tracks[0];
                viewModel.AssignPlugin(track, @"C:\VST3\Arturia\DX7 V.vst3");
                track.SoundName = "Desert Piano";

                viewModel.AssignPlugin(track, @"C:\VST3\Arturia\CMI V.vst3");

                Assert.Null(track.SoundName);
                Assert.Equal("CMI V", track.PluginAndSoundName);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }

    [Fact]
    public void AnNksSound_LeavesItsFamilyOut()
    {
        var violins = new SoundEntry("Strings: Discover - Violins 1", SoundCategories.Strings, "", "BBC", "bbc.vst3", "p", "", "", "", SoundKind.NksPreset);
        var piano = new SoundEntry("Grand: Piano", SoundCategories.Piano, "", "Piano V", "p.vst3", "p", "", "", "");

        Assert.Equal("Discover - Violins 1", MainViewModel.SoundDisplayName(violins));
        Assert.Equal("Grand: Piano", MainViewModel.SoundDisplayName(piano));
    }
}
