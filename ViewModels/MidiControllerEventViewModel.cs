namespace Sonator.ViewModels;

// A controller recorded on a track: where it moved, in sixteenth-note steps from the pattern's start (with
// the fraction of a step), which controller (0-127 a control change, 64 the sustain pedal, 1 the mod wheel;
// 128 aftertouch, 129 pitch bend) and its value from 0 to 1.
public sealed record MidiControllerEventViewModel(double Step, int Controller, double Value);
