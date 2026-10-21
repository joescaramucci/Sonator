using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;

namespace Sonator.ViewModels;

// Builds the default patterns, tracks and steps used for new projects and new tracks.
internal static class PatternFactory
{
    internal static ObservableCollection<PatternViewModel> BuildDefaultPatterns()
    {
        return new ObservableCollection<PatternViewModel>
        {
            CreatePattern("Pattern 1", "Default pattern (8 MIDI channels)", "01")
        };
    }

    // The colours offered in a track's menu, in menu order. New patterns give one to each track.
    internal static readonly string[] TrackPalette =
    {
        "#FFD94C4C", // red
        "#FFDE8A3B", // orange
        "#FFE6C45B", // yellow
        "#FF5FB87B", // green
        "#FF5CAFD6", // cyan
        "#FF5F8FDE", // blue
        "#FF9C72D8", // purple
        "#FF7D8B99", // gray
    };

    internal static PatternViewModel CreatePattern(string name, string summary, string slot)
    {
        var tracks = new ObservableCollection<PatternTrackViewModel>(
            Enumerable.Range(1, 8).Select(channel =>
            {
                var track = CreateTrack(
                    $"Track {channel}",
                    "MIDI Channel",
                    channel,
                    36,
                    Enumerable.Repeat(false, 16).ToArray());
                track.TrackColorBrush = CreateTrackColorBrush(TrackPalette[(channel - 1) % TrackPalette.Length]);
                return track;
            }));

        return new PatternViewModel(name, summary, slot, tracks);
    }

    internal static PatternTrackViewModel CreateTrack(string name, string instrument, int channel, int noteNumber, IReadOnlyList<bool> steps)
    {
        var stepItems = new ObservableCollection<StepViewModel>();
        for (var i = 0; i < steps.Count; i++)
        {
            stepItems.Add(new StepViewModel(i, steps[i])
            {
                MidiNoteNumber = steps[i] ? noteNumber : null
            });
        }

        return new PatternTrackViewModel(name, instrument, channel, noteNumber, stepItems);
    }

    internal static SolidColorBrush CreateTrackColorBrush(string? colorHex)
    {
        if (string.IsNullOrWhiteSpace(colorHex))
        {
            return new SolidColorBrush(Color.FromRgb(38, 53, 69));
        }

        var color = (Color)ColorConverter.ConvertFromString(colorHex)!;
        if (color.A == byte.MaxValue)
        {
            color.A = 0x88;
        }

        return new SolidColorBrush(color);
    }

    internal static ObservableCollection<StepViewModel> BuildDefaultTrackSteps()
    {
        var steps = new ObservableCollection<StepViewModel>();
        for (var i = 0; i < 16; i++)
        {
            steps.Add(new StepViewModel(i, false));
        }

        return steps;
    }
}
