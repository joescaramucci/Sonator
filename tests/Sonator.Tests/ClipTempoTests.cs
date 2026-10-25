using System.IO;
using Sonator.Services;
using Sonator.ViewModels;
using Xunit;

namespace Sonator.Tests;

public sealed class ClipTempoTests
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

    [Fact]
    public void AClipThatFollowsTheTempo_KeepsItsLengthInBeats_WhenTheTempoChanges()
    {
        OnSta(() =>
        {
            var path = Path.Combine(Path.GetTempPath(), $"sonator-cliptempo-{Guid.NewGuid():N}.wav");
            try
            {
                // Two seconds: four beats at 120 BPM.
                AudioClipLoader.SaveWav(new AudioClip(new float[2 * 44100 * 2], 2, 44100), path);
                using var viewModel = new MainViewModel();
                viewModel.Tempo = 120;
                var track = viewModel.Tracks[0];
                viewModel.AssignAudioClip(track, path);
                double Steps() => viewModel.TimelineTracks.First(lane => ReferenceEquals(lane.SourceTrack, track)).AudioLengthSteps;
                Assert.Equal(16, Steps(), 1);

                viewModel.Tempo = 60;
                Assert.Equal(8, Steps(), 1); // as it is: half as many beats

                viewModel.Tempo = 120;
                viewModel.SetClipFollowsTempo(track, true);
                Assert.Equal(120, track.AudioClipBpm);
                viewModel.Tempo = 60;
                Assert.Equal(16, Steps(), 1); // stretched: the same four beats
                Assert.Equal(4, viewModel.TimelineTracks.First(lane => ReferenceEquals(lane.SourceTrack, track)).AudioSeconds, 2);

                var restored = ProjectDocumentMapper.ApplyTrackDocument(ProjectDocumentMapper.BuildTrackDocument(track));
                Assert.Equal(120, restored.AudioClipBpm);
            }
            finally
            {
                File.Delete(path);
            }
        });
    }

    [Fact]
    public void TheStretcher_MakesAClipLonger_WithoutChangingItsPitch()
    {
        var rate = 44100;
        var tone = Enumerable.Range(0, rate).Select(i => (float)Math.Sin(2 * Math.PI * 440 * i / rate)).ToArray();
        var stretched = TimeStretcher.Stretch(tone, 1, 1.5);

        Assert.Equal(rate * 1.5, stretched.Length, 0);
        // Still 440 Hz: as many zero crossings per second.
        static int Crossings(float[] samples, int from, int to) =>
            Enumerable.Range(from + 1, to - from - 1).Count(i => samples[i - 1] < 0 && samples[i] >= 0);
        Assert.InRange(Crossings(stretched, rate / 4, rate / 4 + rate / 2), 210, 230);
    }
}
