using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Sonator.Services;
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
        if (new AppSettingsService().Load().MixerMasterWidth is { } width)
        {
            MasterStrip.Width = Math.Clamp(width, MasterMinWidth, MasterMaxWidth);
        }
    }

    // The transport keys work here as in the main window: Space plays or stops, the full stop goes back to
    // the start, the arrows move by a bar; not while a box is being typed in.
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox || Keyboard.Modifiers != ModifierKeys.None)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Space:
                _viewModel.TogglePlayback();
                break;
            case Key.OemPeriod or Key.Decimal:
                _viewModel.RestartPlayback();
                break;
            case Key.Left or Key.Right:
                _viewModel.MovePlaybackByBars(e.Key == Key.Left ? -1 : 1);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    // ---- The master strip's width, dragged at its left edge and kept in the settings ----

    private const double MasterMinWidth = 120;
    private const double MasterMaxWidth = 420;

    private void MasterGrip_DragDelta(object sender, DragDeltaEventArgs e) =>
        MasterStrip.Width = Math.Clamp(MasterStrip.Width - e.HorizontalChange, MasterMinWidth, MasterMaxWidth);

    private void MasterGrip_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        var settingsService = new AppSettingsService();
        var settings = settingsService.Load();
        settings.MixerMasterWidth = MasterStrip.Width;
        settingsService.Save(settings);
    }

    // The VU meters keep their shape as the strip widens, each a little wider than it is tall.
    private void MasterStrip_SizeChanged(object sender, SizeChangedEventArgs e) =>
        VuRow.Height = Math.Clamp(Math.Round((e.NewSize.Width - 9) / 2 * 0.78), 48, 130);

    private void Fx_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: PatternTrackViewModel track } && Application.Current.MainWindow is MainWindow main)
        {
            main.OpenTrackFxRack(track, null);
        }
    }

    // A click on an effect in a strip opens the rack it is in, showing it.
    private void Insert_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TrackEffectViewModel effect } element
            && InsertListOf(element) is { Tag: PatternTrackViewModel track }
            && Application.Current.MainWindow is MainWindow main)
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

    // A click anywhere on a channel strip selects its track, as on a desk the channel in hand (a kit piece's,
    // its drum track); the knob, fader or button clicked still does its own work.
    private void Strip_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PatternTrackViewModel strip }
            && (strip.KitParent ?? strip) is var track && !ReferenceEquals(_viewModel.SelectedTrack, track))
        {
            _viewModel.SelectedTrack = track;
        }
    }

    private void TrackName_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PatternTrackViewModel { IsKitPiece: false } track })
        {
            _viewModel.SelectedTrack = track;
        }
    }

    // ---- A drum kit's pieces, opened beside its track ----

    private void Kit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PatternTrackViewModel track })
        {
            track.IsKitOpen = !track.IsKitOpen;
        }
    }

    // Enter writes a kit piece's name on its tape.
    private void KitPieceName_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Escape)
        {
            Keyboard.ClearFocus();
            e.Handled = true;
        }
    }

    // A click on a drum track's fader that does not move it opens or closes its kit's pieces; a double-click
    // still puts the fader back at 0 dB, so the click waits a double-click's time to be sure.
    private double _faderValueAtPress;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    private System.Windows.Threading.DispatcherTimer? _kitClick;

    private void Fader_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _kitClick?.Stop();
        if (sender is Slider slider)
        {
            _faderValueAtPress = slider.Value;
        }
    }

    private void Fader_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Slider { DataContext: PatternTrackViewModel { HasKitPieces: true } track } slider
            || Math.Abs(slider.Value - _faderValueAtPress) > 1e-9)
        {
            return;
        }

        _kitClick?.Stop();
        _kitClick = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(GetDoubleClickTime()) };
        _kitClick.Tick += (_, _) =>
        {
            _kitClick?.Stop();
            track.IsKitOpen = !track.IsKitOpen;
        };
        _kitClick.Start();
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
        _kitClick?.Stop();
        if (sender is FrameworkElement { DataContext: PatternTrackViewModel track })
        {
            track.Volume = TrackEffectsHost.UnityVolume;
            e.Handled = true;
        }
    }
}
