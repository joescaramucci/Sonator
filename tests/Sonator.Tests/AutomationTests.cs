using Sonator.ViewModels;
using Xunit;

namespace Sonator.Tests;

public sealed class AutomationTests
{
    private static readonly IReadOnlyList<AutomationPoint> Fade = new[]
    {
        new AutomationPoint(4, 0.8),
        new AutomationPoint(12, 0.0),
    };

    [Fact]
    public void Value_RunsInAStraightLineBetweenPoints_AndHoldsBeyondThem()
    {
        Assert.Null(Automation.ValueAt(Array.Empty<AutomationPoint>(), 3));
        Assert.Equal(0.8, Automation.ValueAt(Fade, 0));
        Assert.Equal(0.8, Automation.ValueAt(Fade, 4));
        Assert.Equal(0.4, Automation.ValueAt(Fade, 8)!.Value, 6);
        Assert.Equal(0.0, Automation.ValueAt(Fade, 12));
        Assert.Equal(0.0, Automation.ValueAt(Fade, 99));
    }

    [Fact]
    public void Points_StayInOrder_AsTheyAreAddedMovedAndRemoved()
    {
        var (points, index) = Automation.Add(Fade, new AutomationPoint(6, 1));
        Assert.Equal(1, index);
        Assert.Equal(new double[] { 4, 6, 12 }, points.Select(point => point.Step));

        // A point cannot be dragged past its neighbours.
        points = Automation.Move(points, 1, new AutomationPoint(20, 0.5));
        Assert.Equal(new double[] { 4, 12, 12 }, points.Select(point => point.Step));
        Assert.Equal(0.5, points[1].Value);

        points = Automation.Remove(points, 0);
        Assert.Equal(new double[] { 12, 12 }, points.Select(point => point.Step));
    }

    [Fact]
    public void Automation_IsSavedWithTheTrack()
    {
        var track = PatternFactory.CreatePattern("Pattern 1", "Test", "01").Tracks[0];
        track.VolumeAutomation = Fade;
        track.PanAutomation = new[] { new AutomationPoint(0, -1) };

        var restored = ProjectDocumentMapper.ApplyTrackDocument(ProjectDocumentMapper.BuildTrackDocument(track));

        Assert.Equal(Fade, restored.VolumeAutomation);
        Assert.Equal(track.PanAutomation, restored.PanAutomation);
        Assert.True(restored.HasAutomation);
    }

    [Fact]
    public void Lane_EditsTheTargetItShows()
    {
        var track = PatternFactory.CreatePattern("Pattern 1", "Test", "01").Tracks[0];
        Assert.Empty(track.ShownAutomationPoints);

        track.ShownAutomation = AutomationTarget.Pan;
        Assert.Equal(-1, track.ShownAutomationMinimum);
        track.ShownAutomationPoints = Fade;

        Assert.Equal(Fade, track.PanAutomation);
        Assert.Empty(track.VolumeAutomation);
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

    [Fact]
    public void Playing_MovesTheTrackToItsAutomation_AndLeavesTheFaderAsItWas()
    {
        OnSta(() =>
        {
            using var viewModel = new MainViewModel();
            var track = viewModel.Tracks[0];
            track.Volume = 0.6;
            track.VolumeAutomation = Fade;

            viewModel.ApplyAutomationAt(8);
            Assert.Equal(0.4, track.MixVolume, 6);
            Assert.Equal(0.6, track.Volume);
            Assert.Equal(0.0, track.MixPan); // no pan automation: the pan fader's own value

            // Moved by hand, the fader sets its own value; the automation still decides what is heard.
            track.MixVolume = 0.9;
            Assert.Equal(0.9, track.Volume);
            Assert.Equal(0.4, track.MixVolume, 6);

            // Without automation the fader is heard again.
            track.VolumeAutomation = Array.Empty<AutomationPoint>();
            viewModel.ApplyAutomationAt(8);
            Assert.Equal(0.9, track.MixVolume);
        });
    }

    [Fact]
    public void ClearingAutomation_CanBeUndone_AndTheLaneStaysOpen()
    {
        OnSta(() =>
        {
            using var viewModel = new MainViewModel();
            viewModel.Tracks[0].VolumeAutomation = Fade;
            viewModel.ShowAutomation(viewModel.Tracks[0], AutomationTarget.Volume);

            viewModel.ClearShownAutomation(viewModel.Tracks[0]);
            Assert.False(viewModel.Tracks[0].HasAutomation);

            viewModel.UndoCommand.Execute(null);
            Assert.Equal(Fade, viewModel.Tracks[0].VolumeAutomation);
            Assert.Equal(AutomationTarget.Volume, viewModel.Tracks[0].ShownAutomation);
        });
    }
}
