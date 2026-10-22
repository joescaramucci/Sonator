using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Sonator.ViewModels;

namespace Sonator;

// A track's FX rack: all its effects stacked in the order the sound goes through them, each with its
// own ON switch and knobs. While it is open the track's FX button is lit.
public partial class EffectRackWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly DispatcherTimer _meterTimer = new() { Interval = TimeSpan.FromMilliseconds(60) };

    public EffectRackWindow(MainViewModel viewModel, PatternTrackViewModel track)
    {
        _viewModel = viewModel;
        Track = track;
        InitializeComponent();
        WindowFrame.UseDarkFrame(this);
        DataContext = this;

        _meterTimer.Tick += (_, _) =>
        {
            foreach (var effect in Track.Effects.Where(effect => effect.HasMeter))
            {
                effect.MeterValue = _viewModel.EffectMeter(Track, effect);
            }
        };
        _meterTimer.Start();

        Track.IsFxRackOpen = true;
        Closed += (_, _) =>
        {
            _meterTimer.Stop();
            Track.IsFxRackOpen = false;
        };
    }

    public PatternTrackViewModel Track { get; }

    public string WindowTitle => $"FX rack — {Track.Name}";

    public string Subtitle => $"{Track.Name}: the sound goes through the effects from top to bottom";

    // The rack grows with its effects up to most of the screen, then scrolls.
    public double MaxRackHeight => SystemParameters.WorkArea.Height * 0.75;

    // Brings an effect's module into view, such as one just added or clicked in the track panel.
    public void ShowEffect(TrackEffectViewModel effect)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var index = Track.Effects.IndexOf(effect);
            if (index >= 0 && RackScroller.Content is ItemsControl items
                && items.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement module)
            {
                module.BringIntoView();
            }
        }, DispatcherPriority.Loaded);
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
        {
            return;
        }

        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        foreach (var definition in _viewModel.AvailableEffects)
        {
            var item = new MenuItem { Header = $"{definition.Name}  —  {definition.Description}" };
            item.Click += (_, _) =>
            {
                if (_viewModel.AddEffect(Track, definition) is { } effect)
                {
                    ShowEffect(effect);
                }
            };
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

    private void Move_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: TrackEffectViewModel effect, Tag: string direction })
        {
            _viewModel.MoveEffect(Track, effect, int.Parse(direction, System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: TrackEffectViewModel effect })
        {
            foreach (var parameter in effect.Parameters)
            {
                parameter.ResetToDefault();
            }
        }
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: TrackEffectViewModel effect })
        {
            _viewModel.RemoveEffect(Track, effect);
        }
    }
}
