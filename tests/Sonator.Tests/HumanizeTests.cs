using Sonator.ViewModels;
using Xunit;

namespace Sonator.Tests;

public sealed class HumanizeTests
{
    private static readonly MidiNoteEventViewModel[] Eighths =
        Enumerable.Range(0, 8).Select(i => new MidiNoteEventViewModel(i * 2, 1, 60, 100)).ToArray();

    [Fact]
    public void Swing_MovesOnlyTheOffBeats_TowardTheTriplet()
    {
        var swung = NoteQuantizer.Swing(Eighths, grid: 2, swing: 1.0);

        for (var i = 0; i < swung.Count; i++)
        {
            var start = swung[i].StepIndex + swung[i].StartOffset;
            Assert.Equal(i % 2 == 0 ? i * 2 : i * 2 + 2.0 / 3.0, start, 6);
            Assert.Equal(1.0, swung[i].DurationSteps + swung[i].LengthFraction, 6);
        }

        Assert.Equal(Eighths, NoteQuantizer.Swing(Eighths, grid: 2, swing: 0));
    }

    [Fact]
    public void Humanize_MovesNotesALittle_WithinTheAmount_TheSameForTheSameSeed()
    {
        var amount = NoteQuantizer.Humanizings.Single(option => option.Label == "Medium");
        var played = NoteQuantizer.Humanize(Eighths, amount, seed: 7);

        Assert.Equal(played, NoteQuantizer.Humanize(Eighths, amount, seed: 7));
        Assert.NotEqual(Eighths, played);
        for (var i = 0; i < played.Count; i++)
        {
            var start = played[i].StepIndex + played[i].StartOffset;
            Assert.InRange(start, Math.Max(0, i * 2 - amount.TimingSteps), i * 2 + amount.TimingSteps);
            Assert.InRange(played[i].Velocity, 100 - amount.Velocity, 100 + amount.Velocity);
            Assert.Equal(1.0, played[i].DurationSteps + played[i].LengthFraction, 6);
        }
    }
}
