using System.IO;
using Sonator.Models;
using Sonator.Services;
using Sonator.ViewModels;
using Xunit;

namespace Sonator.Tests;

public sealed class ImportAudioTests
{
    private static void OnStaThread(Action test)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                test();
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
    public void AnImportedWav_GoesIntoAnEmptyTrack_ThenIntoANewAudioTrack()
    {
        OnStaThread(() =>
        {
            var wav = Path.Combine(Path.GetTempPath(), $"sonator-import-{Guid.NewGuid():N}.wav");
            AudioClipLoader.SaveWav(new AudioClip(new float[4800 * 2], 2, 48000), wav);
            var imported = new List<string>();
            try
            {
                using var viewModel = new MainViewModel();
                var pattern = viewModel.SelectedPattern!;
                var empty = pattern.Tracks.First(MainViewModel.IsEmptyTrack);
                viewModel.SelectedTrack = empty;

                Assert.True(viewModel.ImportAudioFile(wav));
                Assert.Equal(PatternTrackType.AudioInput, empty.TrackType);
                imported.Add(empty.AudioClipPath!);

                // With every track full, the file gets a new audio track.
                foreach (var track in pattern.Tracks.Where(MainViewModel.IsEmptyTrack).ToList())
                {
                    track.AudioClipPath = "taken.wav";
                }

                var count = pattern.Tracks.Count;
                Assert.True(viewModel.ImportAudioFile(wav));
                Assert.Equal(count + 1, pattern.Tracks.Count);
                var added = pattern.Tracks.Last();
                Assert.Equal(PatternTrackType.AudioInput, added.TrackType);
                Assert.True(File.Exists(added.AudioClipPath));
                imported.Add(added.AudioClipPath!);

                // Dragged along, the clip starts later; dropped on an empty track, it moves there.
                Assert.True(viewModel.MoveAudioClip(added, 20));
                Assert.Equal(20, added.AudioClipStartStep);
                var free = pattern.Tracks.First(track => track.AudioClipPath == "taken.wav");
                free.AudioClipPath = null;
                Assert.True(viewModel.MoveAudioClip(added, 32, free));
                Assert.Null(added.AudioClipPath);
                Assert.Equal(32, free.AudioClipStartStep);
                Assert.Equal(PatternTrackType.AudioInput, free.TrackType);
                Assert.Equal(32, ProjectDocumentMapper.BuildTrackDocument(free).AudioClipStartStep);
            }
            finally
            {
                File.Delete(wav);
                foreach (var path in imported)
                {
                    File.Delete(path);
                }
            }
        });
    }
}
