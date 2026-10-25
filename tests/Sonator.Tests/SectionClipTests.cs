using Sonator.Models;
using Sonator.Services;
using Sonator.ViewModels;
using Xunit;

namespace Sonator.Tests;

public sealed class SectionClipTests
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

    private static List<StepDocument> Bars(int bars) =>
        Enumerable.Range(0, bars * 16).Select(index => new StepDocument(index, false, null, 100, 1)).ToList();

    [Fact]
    public void InTheArrangement_EachSectionsClipIsAtItsSectionsPlace_ASectionPlayedTwiceTwice()
    {
        OnSta(() =>
        {
            using var viewModel = new MainViewModel();
            var document = viewModel.BuildProjectDocument();
            PatternDocument Section(string name, string slot, string? clip, int start) => document.Patterns[0] with
            {
                Name = name,
                Slot = slot,
                Tracks = document.Patterns[0].Tracks
                    .Select((track, index) => track with
                    {
                        Steps = Bars(2),
                        AudioClipPath = index == 0 ? clip : null,
                        AudioClipStartStep = index == 0 ? start : 0,
                    })
                    .ToList(),
            };

            // Verse (2 bars, its clip from step 4), Chorus (2 bars, its clip from 0), Verse again.
            var verse = Section("Verse", "01", @"C:\audio\verse.wav", 4);
            var chorus = Section("Chorus", "02", @"C:\audio\chorus.wav", 0);
            var grid = new List<string[]>();
            foreach (var slot in new[] { "01", "01", "02", "02", "01", "01" })
            {
                grid.Add(new[] { slot, string.Empty, string.Empty, string.Empty });
            }

            viewModel.LoadDocument(document with { Patterns = new List<PatternDocument> { verse, chorus }, SongGrid = grid });
            viewModel.IsArrangeMode = true;

            var placements = viewModel.ClipPlacements().Select(placement => (placement.Part.AudioClipPath, placement.StartStep)).ToList();
            Assert.Equal(new (string?, int)[]
            {
                (@"C:\audio\verse.wav", 4),
                (@"C:\audio\chorus.wav", 32),
                (@"C:\audio\verse.wav", 68),
            }, placements);

            // One channel: both sections' clips play through the first pattern's track.
            Assert.All(viewModel.ClipPlacements(), placement => Assert.Same(viewModel.Patterns[0].Tracks[0], viewModel.ChannelOf(placement.Part)));
        });
    }

    [Fact]
    public void ClipsLaidOut_AreEachAtTheirPlace_Faded()
    {
        var one = new AudioClip(Enumerable.Repeat(0.5f, 1000).ToArray(), 1, 1000);  // 1 s
        var two = new AudioClip(Enumerable.Repeat(-0.5f, 2000).ToArray(), 2, 1000); // 1 s stereo
        var laid = AudioSplice.Place(new[] { (one, 0.0, 0.0, 0.0), (two, 2.0, 0.1, 0.0) }, 1000);

        Assert.Equal(3, laid.Duration.TotalSeconds, 2);
        Assert.Equal(0.5f, laid.Samples[500 * 2], 3);
        Assert.Equal(0f, laid.Samples[1500 * 2], 3);       // the gap between them
        Assert.Equal(-0.5f, laid.Samples[2500 * 2], 3);
        Assert.InRange(laid.Samples[2050 * 2], -0.4f, -0.1f); // fading in
    }
}
