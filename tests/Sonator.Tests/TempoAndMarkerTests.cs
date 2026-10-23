using System.IO;
using Melanchall.DryWetMidi.Core;
using Sonator.Services;
using Sonator.ViewModels;
using Xunit;

namespace Sonator.Tests;

public sealed class TempoAndMarkerTests
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
    public void TempoMap_TimesTheSongThroughItsChanges()
    {
        // 120 BPM for two bars (32 steps = 4 s), then 60 BPM (a step lasts 0.25 s).
        var map = new TempoMap(120, new[] { new TempoChange(32, 60), new TempoChange(0, 90), new TempoChange(-4, 200) });

        Assert.Single(map.Changes); // nothing at or before the start: that is the base tempo
        Assert.Equal(120, map.TempoAt(31));
        Assert.Equal(60, map.TempoAt(32));
        Assert.Equal(4.0, map.SecondsAt(32), 9);
        Assert.Equal(5.0, map.SecondsAt(36), 9);
        Assert.Equal(36, map.StepAt(5.0), 9);
        Assert.Equal(16, map.StepAt(2.0), 9);
    }

    [Fact]
    public void Sequencer_PlaysEachStepAtTheTempoInForceThere()
    {
        OnSta(() =>
        {
            var engine = new SequencerEngine();
            engine.UpdateTempo(120);
            Assert.Equal(120, engine.TempoAtStep(40));

            engine.UpdateTempoMap(new TempoMap(120, new[] { new TempoChange(32, 90) }));
            Assert.Equal(120, engine.TempoAtStep(31));
            Assert.Equal(90, engine.TempoAtStep(40));
        });
    }

    [Fact]
    public void TransportClock_CountsTheTimeWithTheChanges()
    {
        var map = new TempoMap(120, new[] { new TempoChange(32, 60) });

        Assert.Equal("00:00:08", TransportClock.FormatPosition(48, map)); // 4 s, then 16 steps at 0.25 s
    }

    [Fact]
    public void MidiExport_WritesTempoMarkersNotesAndControllers()
    {
        var pattern = PatternFactory.CreatePattern("Pattern 1", "Test", "01");
        var steps = pattern.Tracks[0];
        foreach (var step in steps.Steps)
        {
            step.IsActive = false;
        }
        steps.Steps[4].IsActive = true;
        steps.Steps[4].MidiNoteNumber = 62;
        steps.Steps[4].Velocity = 90;
        steps.Steps[4].DurationSteps = 2;

        var recorded = pattern.Tracks[1];
        recorded.MidiNoteEvents = new[] { new MidiNoteEventViewModel(8, 1, 64, 100, 0.5, 0.5) };
        recorded.MidiControllerEvents = new[] { new MidiControllerEventViewModel(8, MidiControllerMessage.Sustain, 1) };
        foreach (var track in pattern.Tracks.Skip(2))
        {
            foreach (var step in track.Steps)
            {
                step.IsActive = false;
            }
        }

        var file = MidiFileExport.Build(
            new[] { (pattern, 0, (int?)null) },
            new TempoMap(100, new[] { new TempoChange(16, 140) }),
            "3/4",
            new[] { new SongMarker(16, "Chorus") });

        var path = Path.Combine(Path.GetTempPath(), $"sonator-export-{Guid.NewGuid():N}.mid");
        try
        {
            file.Write(path, overwriteFile: true);
            var read = MidiFile.Read(path);
            var chunks = read.GetTrackChunks().ToList();
            Assert.Equal(3, chunks.Count); // the tempo track and the two tracks with notes

            var conductor = chunks[0].Events;
            Assert.Equal(new[] { 600_000L, 428_571L }, conductor.OfType<SetTempoEvent>().Select(tempo => tempo.MicrosecondsPerQuarterNote));
            Assert.Equal(3, conductor.OfType<TimeSignatureEvent>().Single().Numerator);
            Assert.Equal("Chorus", conductor.OfType<MarkerEvent>().Single().Text);

            // The step note: on at step 4 (480 ticks), off two steps later.
            var stepNote = AbsoluteTimes(chunks[1]).Where(item => item.Event is NoteOnEvent or NoteOffEvent).ToList();
            Assert.Equal((480L, 62), (stepNote[0].Tick, (int)((NoteEvent)stepNote[0].Event).NoteNumber));
            Assert.Equal(720L, stepNote[1].Tick);

            // The recorded note half a step into step 8, a step and a half long; the pedal at step 8.
            var recordedEvents = AbsoluteTimes(chunks[2]);
            Assert.Equal(1020L, recordedEvents.Single(item => item.Event is NoteOnEvent).Tick);
            Assert.Equal(1200L, recordedEvents.Single(item => item.Event is NoteOffEvent).Tick);
            var pedal = recordedEvents.Single(item => item.Event is ControlChangeEvent);
            Assert.Equal((960L, 64, 127), (pedal.Tick, (int)((ControlChangeEvent)pedal.Event).ControlNumber, (int)((ControlChangeEvent)pedal.Event).ControlValue));
            Assert.Equal(1, (int)((ChannelEvent)pedal.Event).Channel); // track 2 is on MIDI channel 2
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static List<(long Tick, MidiEvent Event)> AbsoluteTimes(TrackChunk chunk)
    {
        var time = 0L;
        return chunk.Events.Select(midiEvent => (time += midiEvent.DeltaTime, midiEvent)).ToList();
    }

    [Fact]
    public void TempoChangesAndMarkers_AreSavedAndUndone()
    {
        OnSta(() =>
        {
            using var viewModel = new MainViewModel();
            viewModel.SetTempoChange(32, 140);
            viewModel.SetMarker(16, " Verse ");
            Assert.Equal(new[] { new TempoChange(32, 140) }, viewModel.TempoChanges);
            Assert.Equal(new[] { new SongMarker(16, "Verse") }, viewModel.Markers);
            Assert.Equal(2, viewModel.RulerFlags.Count);
            Assert.Equal(16 * viewModel.TimelineSixteenthWidth, viewModel.RulerFlags.Single(flag => !flag.IsTempo).X);

            viewModel.UndoCommand.Execute(null);
            Assert.Empty(viewModel.Markers);
            Assert.Single(viewModel.TempoChanges);

            viewModel.RemoveTempoChange(32);
            Assert.Empty(viewModel.TempoChanges);
            viewModel.UndoCommand.Execute(null);
            Assert.Equal(140, viewModel.CurrentTempoMap.TempoAt(40));

            // A change at the very start is the project's tempo.
            viewModel.SetTempoChange(0, 97);
            Assert.Equal(97, viewModel.Tempo);
        });
    }
}
