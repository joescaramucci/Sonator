namespace Sonator.ViewModels;

// A quantization grid for recording, in sixteenth-note steps. Null steps means no quantization.
public sealed record RecordGrid(double? Steps, string Label);
