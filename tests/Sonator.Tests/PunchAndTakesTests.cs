using Sonator.Services;
using Sonator.ViewModels;
using Xunit;

namespace Sonator.Tests;

public sealed class PunchAndTakesTests
{
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

    private static AudioClip Constant(float value, double seconds, int rate = 1000, int channels = 1) =>
        new(Enumerable.Repeat(value, (int)(seconds * rate) * channels).ToArray(), channels, rate);

    private static float LeftAt(AudioClip clip, double seconds) => clip.Samples[(int)(seconds * clip.SampleRate) * clip.Channels];

    [Fact]
    public void APunch_ReplacesTheClipBetweenTheLocators_CrossfadedAtEachSeam()
    {
        var clip = Constant(0.2f, 4);  // 0 to 4 s
        var take = Constant(0.8f, 3);  // recorded from 0.5 s
        var punched = AudioSplice.Punch(clip, 0, take, 0.5, punchIn: 1, punchOut: 2, start: 0);

        Assert.Equal(2, punched.Channels);
        Assert.Equal(4, punched.Duration.TotalSeconds, 2);
        Assert.Equal(0.2f, LeftAt(punched, 0.5), 3);   // before
        Assert.Equal(0.8f, LeftAt(punched, 1.5), 3);   // inside: the take
        Assert.Equal(0.2f, LeftAt(punched, 3), 3);     // after
        Assert.InRange(LeftAt(punched, 1.0), 0.3f, 0.7f); // the seam blends the two
    }

    [Fact]
    public void APunchWithoutAClip_IsTheTakeBetweenTheLocators()
    {
        var take = Constant(0.5f, 4);
        var punched = AudioSplice.Punch(null, 0, take, 0, punchIn: 1, punchOut: 2, start: 1);

        Assert.Equal(1, punched.Duration.TotalSeconds, 2);
        Assert.Equal(0.5f, LeftAt(punched, 0.5), 3);
    }

    [Fact]
    public void Takes_AreSavedWithTheTrack_AndOneChosenTakesThePlaceOfWhatItPlayed()
    {
        OnSta(() =>
        {
            using var viewModel = new MainViewModel();
            var track = viewModel.Tracks[0];
            var first = new[] { new MidiNoteEventViewModel(0, 1, 60, 100) };
            var second = new[] { new MidiNoteEventViewModel(4, 1, 64, 90) };
            track.MidiNoteEvents = second;
            track.Takes = new[] { new TrackTake("Before the take at 10:00", null, 0, first) };

            var restored = ProjectDocumentMapper.ApplyTrackDocument(ProjectDocumentMapper.BuildTrackDocument(track));
            Assert.Equal(first, Assert.Single(restored.Takes).Notes);

            viewModel.PlayTake(track, track.Takes[0]);

            Assert.Equal(first, track.MidiNoteEvents);
            var kept = Assert.Single(track.Takes);
            Assert.StartsWith("Replaced", kept.Name);
            Assert.Equal(second, kept.Notes);

            viewModel.UndoCommand.Execute(null);
            Assert.Equal(second, viewModel.Tracks[0].MidiNoteEvents);
        });
    }
}
