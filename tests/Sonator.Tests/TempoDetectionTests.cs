using Sonator.Services;
using Xunit;

namespace Sonator.Tests;

public sealed class TempoDetectionTests
{
    // A drum-like pattern: a kick on every beat, a quieter hi-hat on the off-beats, some noise, from the first
    // beat on.
    private static AudioClip Beats(double bpm, double firstBeat, double seconds = 30)
    {
        const int Rate = 44100;
        var samples = new float[(int)(Rate * seconds)];
        var random = new Random(7);
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (float)((random.NextDouble() - 0.5) * 0.02);
        }

        var beat = 60 / bpm;
        for (var t = firstBeat; t < seconds - 0.2; t += beat / 2)
        {
            var onBeat = Math.Abs((t - firstBeat) / beat - Math.Round((t - firstBeat) / beat)) < 0.01;
            var start = (int)(t * Rate);
            for (var i = 0; i < Rate / 10 && start + i < samples.Length; i++)
            {
                var decay = Math.Exp(-i / (Rate * (onBeat ? 0.04 : 0.01)));
                var tone = onBeat ? Math.Sin(2 * Math.PI * 60 * i / Rate) : random.NextDouble() - 0.5;
                samples[start + i] += (float)(tone * decay * (onBeat ? 0.8 : 0.25));
            }
        }

        return new AudioClip(samples, 1, Rate);
    }

    [Theory]
    [InlineData(128, 0.3)]
    [InlineData(92, 0.75)]
    [InlineData(140, 0.1)]
    public void FindsTheTempoAndTheFirstBeat(double bpm, double firstBeat)
    {
        var found = TempoDetection.Detect(Beats(bpm, firstBeat));

        Assert.NotNull(found);
        Assert.InRange(found!.Bpm, bpm - 0.3, bpm + 0.3);
        var beat = 60 / bpm;
        var phaseError = Math.Abs(found.FirstBeatSeconds - firstBeat) % beat;
        Assert.True(Math.Min(phaseError, beat - phaseError) < 0.02, $"first beat {found.FirstBeatSeconds:0.000} s");
    }

    [Fact]
    public void SyncingTheSong_SetsTheTempo_AndPutsTheClipsBeatsOnTheGrid()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var wav = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"sonator-beats-{Guid.NewGuid():N}.wav");
            var made = new List<string>();
            try
            {
                AudioClipLoader.SaveWav(Beats(128, 0.3), wav);
                using var viewModel = new ViewModels.MainViewModel();
                Assert.True(viewModel.ImportAudioFile(wav));
                var track = viewModel.SelectedTrack!;
                made.Add(track.AudioClipPath!);
                var found = viewModel.DetectClipTempo(track)!;

                viewModel.SyncSongToClip(track, found.Bpm, found.FirstBeatSeconds);
                made.Add(track.AudioClipPath!);

                Assert.Equal(found.Bpm, viewModel.Tempo, 2);
                // The lined-up clip's first beat now falls on a beat of the grid.
                var lined = viewModel.DetectClipTempo(track)!;
                var beat = 60 / viewModel.Tempo;
                var off = lined.FirstBeatSeconds % beat;
                Assert.True(Math.Min(off, beat - off) < 0.02, $"first beat {lined.FirstBeatSeconds:0.000} s");

                viewModel.UndoCommand.Execute(null); // one undo brings the tempo and the clip back
                Assert.Equal(120, viewModel.Tempo);
                Assert.Equal(made[0], viewModel.SelectedPattern!.Tracks.First(t => t.AudioClipPath is not null).AudioClipPath);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                System.IO.File.Delete(wav);
                foreach (var path in made.Distinct())
                {
                    System.IO.File.Delete(path);
                }
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
    public void AClipTooShort_HasNoTempo()
    {
        Assert.Null(TempoDetection.Detect(new AudioClip(new float[44100], 1, 44100)));
    }
}
