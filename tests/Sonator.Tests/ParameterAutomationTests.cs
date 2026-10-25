using Sonator.Services.Effects;
using Sonator.ViewModels;
using Xunit;

namespace Sonator.Tests;

public sealed class ParameterAutomationTests
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

    private static PatternTrackViewModel Track() => PatternFactory.CreatePattern("Pattern 1", "Test", "01").Tracks[0];

    [Fact]
    public void APass_TakesThePlaceOfTheLanesPointsWhereItRan_AndIsThinned()
    {
        var track = Track();
        track.VolumeAutomation = new[] { new AutomationPoint(0, 0.2), new AutomationPoint(8, 0.3), new AutomationPoint(20, 0.9) };
        var recorder = new AutomationRecorder();

        for (var i = 0; i <= 40; i++)
        {
            // Steps 4 to 14, a quarter step apart, the value rising and then still.
            recorder.Record(track, AutomationTarget.Volume, null, 4 + i * 0.25, Math.Min(0.8, 0.5 + i * 0.01), nowTicks: 1000);
        }

        Assert.True(recorder.IsWriting(track, AutomationTarget.Volume));
        Assert.Equal(0, recorder.Commit(1000 + AutomationRecorder.RestTicks / 2, all: false));

        var undone = 0;
        Assert.Equal(1, recorder.Commit(1000 + AutomationRecorder.RestTicks, all: false, () => undone++));
        Assert.Equal(1, undone);
        Assert.False(recorder.IsWriting(track, AutomationTarget.Volume));

        var points = track.VolumeAutomation;
        Assert.Equal(0.2, points[0].Value); // before the pass
        Assert.Equal(0.9, points[^1].Value); // after it
        Assert.DoesNotContain(points, point => point.Step == 8 && point.Value == 0.3); // under it
        var pass = points.Where(point => point.Step >= 4 && point.Step <= 14).ToList();
        Assert.Equal(4, pass[0].Step);
        Assert.Equal(14, pass[^1].Step);
        Assert.Equal(0.8, pass[^1].Value, 6);
        Assert.True(pass.Count < 41, $"{pass.Count} points kept");
        Assert.Equal(points.OrderBy(point => point.Step), points);
    }

    [Fact]
    public void APassThatLoopsBack_IsWrittenAndANewOneStarts()
    {
        var track = Track();
        var knob = new AutomatedParameter(Guid.NewGuid(), 2, "SonVerb · Mix");
        var recorder = new AutomationRecorder();

        recorder.Record(track, AutomationTarget.Parameter, knob, 30, 0.1, 0);
        recorder.Record(track, AutomationTarget.Parameter, knob, 31, 0.2, 0);
        recorder.Record(track, AutomationTarget.Parameter, knob, 0, 0.7, 0); // the loop went back

        Assert.Equal(new[] { 30.0, 31.0 }, track.ParameterPoints(knob).Select(point => point.Step));
        recorder.Commit(0, all: true);
        Assert.Equal(new[] { 0.0, 30.0, 31.0 }, track.ParameterPoints(knob).Select(point => point.Step));
        Assert.True(track.HasAutomation);
    }

    [Fact]
    public void ParameterLanes_AreSavedWithTheTrack_AndFollowItsEffects()
    {
        var track = Track();
        var verb = Assert.Single(track.Effects);
        var mix = new AutomatedParameter(verb.InstanceId, 4, "SonVerb · Mix");
        var cutoff = new AutomatedParameter(null, 1234, "Synth · Cutoff");
        track.SetParameterPoints(mix, new[] { new AutomationPoint(0, 0), new AutomationPoint(16, 1) });
        track.SetParameterPoints(cutoff, new[] { new AutomationPoint(2, 0.5) });

        var restored = ProjectDocumentMapper.ApplyTrackDocument(ProjectDocumentMapper.BuildTrackDocument(track));

        Assert.Equal(verb.InstanceId, Assert.Single(restored.Effects).InstanceId);
        Assert.Equal(2, restored.ParameterAutomation.Count);
        Assert.Equal(new[] { 0.0, 1.0 }, restored.ParameterPoints(mix).Select(point => point.Value));
        Assert.Equal("Synth · Cutoff", restored.ParameterAutomation.Single(lane => lane.Target.Equals(cutoff)).Target.Name);

        // A lane with no points is no lane.
        restored.SetParameterPoints(cutoff, Array.Empty<AutomationPoint>());
        Assert.Single(restored.ParameterAutomation);
    }

    [Fact]
    public void AnEffectsKnob_FollowsItsLane_AsTheSongGoes()
    {
        OnSta(() =>
        {
            using var viewModel = new MainViewModel();
            var track = viewModel.Tracks[0];
            var verb = track.Effects[0];
            var mix = viewModel.AutomatableParameters(track, verb).Single(parameter => parameter.Name == "SonVerb · Mix");
            track.SetParameterPoints(mix, new[] { new AutomationPoint(0, 0), new AutomationPoint(16, 1) });

            viewModel.ApplyAutomationAt(8);
            Assert.Equal(0.5, verb.Parameters[(int)mix.Parameter].Position, 3);

            viewModel.ApplyAutomationAt(16);
            Assert.Equal(1, verb.Parameters[(int)mix.Parameter].Position, 3);

            viewModel.ShowParameterAutomation(track, mix);
            Assert.Equal(AutomationTarget.Parameter, track.ShownAutomation);
            Assert.Equal("SonVerb · Mix", track.ShownAutomationTitle);
            Assert.Equal(2, track.ShownAutomationPoints.Count);
            Assert.Equal(0, track.ShownAutomationMinimum);
        });
    }
}
