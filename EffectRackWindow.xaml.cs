using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Sonator.Services.Effects;
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
        TrackIndex = viewModel.Tracks.IndexOf(track);
        InitializeComponent();
        WindowFrame.UseDarkFrame(this);
        DataContext = this;

        _meterTimer.Tick += (_, _) =>
        {
            foreach (var effect in Track.Effects)
            {
                if (effect.ReadsMeter)
                {
                    effect.MeterValue = _viewModel.EffectMeter(Track, effect);
                }

                if (effect.HasLevelMeters)
                {
                    var (input, output) = _viewModel.TakeEffectLevels(Track, effect);
                    effect.ShowLevels(input, output);
                }
            }
        };
        _meterTimer.Start();

        RefreshPresets();
        Track.Effects.CollectionChanged += OnEffectsChanged;

        Track.IsFxRackOpen = true;
        Closed += (_, _) =>
        {
            _meterTimer.Stop();
            Track.Effects.CollectionChanged -= OnEffectsChanged;
            Track.IsFxRackOpen = false;
        };
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

    private void OnEffectsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => RefreshPresets();

    // Gives each effect its preset list: Sonator's presets, then the user's.
    private void RefreshPresets()
    {
        foreach (var effect in Track.Effects.Where(effect => !effect.IsPlugin))
        {
            effect.Presets = _viewModel.EffectPresets.PresetsFor(effect.Definition);
        }
    }

    private void SavePreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: TrackEffectViewModel effect })
        {
            return;
        }

        // A modified factory preset suggests a new name; the user's own preset suggests itself.
        var suggested = effect.PresetName is { } name && !Services.Effects.EffectPresetStore.IsFactoryName(effect.Definition, name)
            ? name
            : effect.PresetName is { } factory ? $"{factory} (mine)" : $"My {effect.Name}";
        var dialog = new PresetNameDialog(effect.Definition, suggested, _viewModel.EffectPresets.UserPresets(effect.Definition)) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            _viewModel.SaveEffectPreset(effect, dialog.PresetName);
            RefreshPresets();
        }
    }

    public PatternTrackViewModel Track { get; }

    // Where the track was in the track list when the rack opened, to find its successor after undo.
    public int TrackIndex { get; }

    public string WindowTitle => $"FX rack — {Track.Name}";

    public string Subtitle => $"{Track.Name}: the sound goes through the effects from top to bottom";

    // The rack grows with its effects up to most of the screen, then scrolls.
    public double MaxRackHeight => SystemParameters.WorkArea.Height * 0.75;

    // A picture of an effect's module as it looks in a rack, from a rack never shown that holds only it: the plug-in
    // list pictures Sonator's effects with it.
    internal static ImageSource PictureOfModule(MainViewModel viewModel, TrackEffectViewModel effect)
    {
        var track = new PatternTrackViewModel(effect.Name, effect.Name, 0, 60, new System.Collections.ObjectModel.ObservableCollection<StepViewModel>());
        track.Effects.Add(effect);
        var rack = new EffectRackWindow(viewModel, track);
        rack._meterTimer.Stop();
        try
        {
            var content = (FrameworkElement)rack.Content;
            content.Measure(new Size(rack.Width, double.PositiveInfinity));
            content.Arrange(new Rect(content.DesiredSize));
            content.UpdateLayout();

            var module = FindModule(content, effect) ?? content;
            var size = new Size(Math.Ceiling(module.ActualWidth), Math.Ceiling(module.ActualHeight));
            var drawing = new DrawingVisual();
            using (var drawingContext = drawing.RenderOpen())
            {
                drawingContext.DrawRectangle(new VisualBrush(module) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(size));
            }

            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(drawing);
            bitmap.Freeze();
            return bitmap;
        }
        finally
        {
            rack.Close();
        }
    }

    private static FrameworkElement? FindModule(DependencyObject parent, TrackEffectViewModel effect)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ContentPresenter { Content: TrackEffectViewModel shown } presenter && shown == effect)
            {
                return presenter;
            }

            if (FindModule(child, effect) is { } found)
            {
                return found;
            }
        }

        return null;
    }

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

        menu.Items.Add(new Separator());
        menu.Items.Add(MainWindow.VstEffectsMenu(_viewModel, plugin =>
        {
            if (_viewModel.AddPluginEffect(Track, plugin) is { } effect)
            {
                ShowEffect(effect);
            }
        }));

        menu.IsOpen = true;
    }

    // EDIT opens a VST3 effect's own window.
    private void EditPlugin_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: TrackEffectViewModel effect } && Owner is MainWindow main)
        {
            main.OpenEffectPluginWindow(Track, effect);
        }
    }

    // ---- Dragging effects up and down the rack ----

    private static readonly Brush DropLineBrush = new SolidColorBrush(Color.FromRgb(0xE6, 0xC4, 0x5B));
    private static readonly Brush ModuleBorderBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x1F, 0x21));

    private Point? _dragStart;

    // The grip holds the mouse from the press, so a quick move off the small grip still starts the drag.
    private void Grip_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(this);
        if (sender is UIElement grip)
        {
            grip.CaptureMouse();
            grip.PreviewMouseLeftButtonUp -= Grip_PreviewMouseLeftButtonUp;
            grip.PreviewMouseLeftButtonUp += Grip_PreviewMouseLeftButtonUp;
        }
    }

    private void Grip_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _dragStart = null;
        if (sender is UIElement grip)
        {
            grip.ReleaseMouseCapture();
        }
    }

    // Past the system's drag distance the effect is picked up and can be dropped on another module.
    private void Grip_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not { } start || e.LeftButton != MouseButtonState.Pressed
            || sender is not FrameworkElement { DataContext: TrackEffectViewModel effect } grip)
        {
            return;
        }

        var moved = e.GetPosition(this) - start;
        if (Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance
            && Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance)
        {
            return;
        }

        _dragStart = null;
        grip.ReleaseMouseCapture();
        DragDrop.DoDragDrop(grip, new DataObject(typeof(TrackEffectViewModel), effect), DragDropEffects.Move);
    }

    // An effect dragged here from the plug-in list of the main window.
    private static EffectDefinition? DroppedDefinition(DragEventArgs e) => e.Data.GetData(MainWindow.SonEffectDragFormat) as EffectDefinition;

    // A gold line on the top or bottom edge of the module under the mouse shows where the effect will land, one of
    // the rack's moved or one from the plug-in list.
    private void Module_DragOver(object sender, DragEventArgs e)
    {
        var moving = e.Data.GetData(typeof(TrackEffectViewModel)) is TrackEffectViewModel;
        if (sender is not Border { DataContext: TrackEffectViewModel } module || (!moving && DroppedDefinition(e) is null))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var above = e.GetPosition(module).Y < module.ActualHeight / 2;
        module.BorderBrush = DropLineBrush;
        module.BorderThickness = above ? new Thickness(1, 3, 1, 1) : new Thickness(1, 1, 1, 3);
        e.Effects = moving ? DragDropEffects.Move : DragDropEffects.Copy;
        e.Handled = true;
    }

    private void Module_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is Border module)
        {
            ClearDropLine(module);
        }
    }

    private void Module_Drop(object sender, DragEventArgs e)
    {
        if (sender is not Border { DataContext: TrackEffectViewModel target } module)
        {
            return;
        }

        var dragged = e.Data.GetData(typeof(TrackEffectViewModel)) as TrackEffectViewModel;
        if (dragged is null && DroppedDefinition(e) is { } definition)
        {
            dragged = _viewModel.PutEffect(Track, definition);
        }

        if (dragged is null)
        {
            return;
        }

        ClearDropLine(module);
        var above = e.GetPosition(module).Y < module.ActualHeight / 2;
        var index = Track.Effects.IndexOf(target) + (above ? 0 : 1);
        if (Track.Effects.IndexOf(dragged) < index)
        {
            index--; // taking the effect out first moves the places after it up by one
        }

        _viewModel.MoveEffectTo(Track, dragged, index);
        ShowEffect(dragged);
        e.Handled = true;
    }

    // An effect from the plug-in list dropped on the rack but not on a module goes at the end of the chain.
    private void Rack_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DroppedDefinition(e) is not null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Rack_Drop(object sender, DragEventArgs e)
    {
        if (DroppedDefinition(e) is { } definition && _viewModel.PutEffect(Track, definition) is { } effect)
        {
            ShowEffect(effect);
            e.Handled = true;
        }
    }

    private static void ClearDropLine(Border module)
    {
        module.BorderBrush = ModuleBorderBrush;
        module.BorderThickness = new Thickness(1);
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
