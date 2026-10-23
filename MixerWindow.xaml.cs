using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Sonator.ViewModels;

namespace Sonator;

// The mixing console: one channel strip per track of the pattern, as the sound goes through it (inserts,
// sends, pan, fader with its meter, mute and solo), then the buses, and the master strip at the right.
public partial class MixerWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MixerWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        WindowFrame.UseDarkFrame(this);
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    private void Fx_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: PatternTrackViewModel track } && Owner is MainWindow main)
        {
            main.OpenTrackFxRack(track, null);
        }
    }

    // A click on an effect in a strip opens the rack it is in, showing it.
    private void Insert_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TrackEffectViewModel effect } element
            && InsertListOf(element) is { Tag: PatternTrackViewModel track }
            && Owner is MainWindow main)
        {
            main.OpenTrackFxRack(track, effect);
        }
    }

    // The insert list an effect is shown in: its Tag is the track (or the master) the effects belong to.
    private static ItemsControl? InsertListOf(DependencyObject element)
    {
        for (var current = System.Windows.Media.VisualTreeHelper.GetParent(element); current is not null;
             current = System.Windows.Media.VisualTreeHelper.GetParent(current))
        {
            if (current is ItemsControl list)
            {
                return list;
            }
        }
        return null;
    }

    // A click anywhere on a channel strip selects its track, as on a desk the channel in hand; the knob,
    // fader or button clicked still does its own work.
    private void Strip_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PatternTrackViewModel track } && !ReferenceEquals(_viewModel.SelectedTrack, track))
        {
            _viewModel.SelectedTrack = track;
        }
    }

    private void TrackName_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PatternTrackViewModel track })
        {
            _viewModel.SelectedTrack = track;
        }
    }

    private void Mute_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: PatternTrackViewModel track })
        {
            _viewModel.ToggleTrackMuteCommand.Execute(track);
        }
    }

    private void Solo_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: PatternTrackViewModel track })
        {
            _viewModel.ToggleTrackSoloCommand.Execute(track);
        }
    }

    private void AddBus_Click(object sender, RoutedEventArgs e) => _viewModel.AddBus();

    private void RemoveBus_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: PatternTrackViewModel bus })
        {
            _viewModel.RemoveBus(bus);
        }
    }

    private void BusMute_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: PatternTrackViewModel bus })
        {
            bus.IsMuted = !bus.IsMuted;
        }
    }

    // A double-click puts the fader back at 0 dB.
    private void Fader_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PatternTrackViewModel track })
        {
            track.Volume = TrackEffectsHost.UnityVolume;
            e.Handled = true;
        }
    }
}
