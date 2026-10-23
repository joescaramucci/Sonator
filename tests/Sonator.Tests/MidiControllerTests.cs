using Sonator.Services;
using Sonator.ViewModels;
using Xunit;

namespace Sonator.Tests;

public sealed class MidiControllerTests
{
    [Fact]
    public void ControlChanges_PressureAndPitchBend_ReadAsControllers()
    {
        var sustain = MidiInputService.ToController(0xB0, 64, 127);
        Assert.Equal(new MidiControllerMessage(0, MidiControllerMessage.Sustain, 1.0), sustain);

        var wheel = MidiInputService.ToController(0xB2, 1, 0);
        Assert.Equal(2, wheel!.Channel);
        Assert.Equal(0.0, wheel.Value);

        // Pitch bend is 14 bits, least significant byte first: 0x2000 is the centre.
        var centre = MidiInputService.ToController(0xE0, 0x00, 0x40);
        Assert.Equal(MidiControllerMessage.PitchBend, centre!.Controller);
        Assert.Equal(0.5, centre.Value, 3);
        Assert.Equal(1.0, MidiInputService.ToController(0xE0, 0x7F, 0x7F)!.Value, 6);

        Assert.Equal(MidiControllerMessage.Aftertouch, MidiInputService.ToController(0xD0, 100, 0)!.Controller);
        Assert.Null(MidiInputService.ToController(0x90, 60, 100)); // a note is not a controller
    }

    [Fact]
    public void RecordedControllers_AreSavedWithTheProject()
    {
        var track = PatternFactory.CreatePattern("Pattern 1", "Test", "01").Tracks[0];
        track.MidiControllerEvents = new[]
        {
            new MidiControllerEventViewModel(0.25, MidiControllerMessage.Sustain, 1),
            new MidiControllerEventViewModel(7.5, MidiControllerMessage.Sustain, 0),
        };

        var saved = ProjectDocumentMapper.ApplyTrackDocument(ProjectDocumentMapper.BuildTrackDocument(track));

        Assert.Equal(track.MidiControllerEvents, saved.MidiControllerEvents);
    }
}
