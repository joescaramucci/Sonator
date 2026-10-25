using System.Diagnostics;
using Sonator.ViewModels;
using Xunit;

namespace Sonator.Tests;

public sealed class CaptureTests
{
    private static readonly long Second = Stopwatch.Frequency;

    [Fact]
    public void APartPlayedWithTheSongStopped_IsTheNotesSinceThePause_FromThePlayhead()
    {
        var buffer = new PerformanceBuffer();
        buffer.Note(0, null, -1, 40, 90, true); // an early try, then a long pause
        buffer.Note(Second / 2, null, -1, 40, 0, false);
        var start = 10 * Second;
        buffer.Note(start, null, -1, 60, 100, true);
        buffer.Note(start + Second / 4, null, -1, 60, 0, false);
        buffer.Note(start + Second / 2, null, -1, 64, 80, true);
        buffer.Note(start + Second, null, -1, 64, 0, false);

        // At 120 BPM a second is 8 steps.
        var part = buffer.Take(fromStep: 16, tempo: 120, grid: null)!;

        Assert.False(part.WithSong);
        Assert.Equal(new[] { 60, 64 }, part.Notes.Select(note => note.NoteNumber));
        Assert.Equal(16, part.Notes[0].StepIndex + part.Notes[0].StartOffset, 6);
        Assert.Equal(2, part.Notes[0].DurationSteps + part.Notes[0].LengthFraction, 6);
        Assert.Equal(20, part.Notes[1].StepIndex + part.Notes[1].StartOffset, 6);
        Assert.Equal(80, part.Notes[1].Velocity);

        // Taken: not taken twice.
        Assert.Null(buffer.Take(16, 120, null));
    }

    [Fact]
    public void APartPlayedWithTheSong_IsWhereItWasPlayed_TheLastTimeRoundTheLoop()
    {
        var buffer = new PerformanceBuffer();
        buffer.Note(0, 0.1, pass: 1, 60, 100, true);
        buffer.Note(Second / 8, 1.1, pass: 1, 60, 0, false);
        buffer.Note(4 * Second, 2.4, pass: 2, 67, 110, true); // the loop went round
        buffer.Note(4 * Second + Second / 8, 3.4, pass: 2, 67, 0, false);
        buffer.Controller(4 * Second, 2.0, pass: 2, 64, 1.0);

        var part = buffer.Take(fromStep: 99, tempo: 120, grid: 1.0)!;

        Assert.True(part.WithSong);
        var note = Assert.Single(part.Notes);
        Assert.Equal(67, note.NoteNumber);
        Assert.Equal(2, note.StepIndex + note.StartOffset, 6); // on the record grid
        Assert.Equal(64, Assert.Single(part.Controllers).Controller);
    }
}
