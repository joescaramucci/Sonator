using System.Windows;
using System.Globalization;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.ComponentModel;
using Sonator.Models;
using Sonator.Services;
using Sonator.Services.Vst3;
using Sonator.ViewModels;

namespace Sonator;

public partial class MainWindow : Window
{
    private const string ArrangePatternDragFormat = "Sonator.ArrangePatternRow";
    private readonly Vst3ThumbnailService _vst3ThumbnailService = new();
    private readonly HashSet<string> _thumbnailRequests = new(StringComparer.OrdinalIgnoreCase);
    private MainViewModel? _mainViewModel;
    private bool _isSynchronizingTrackScroll;
    private bool _isDraggingTimelineRuler;
    private double _timelineRulerDragStartX;
    private double _timelineRulerDragStartBarWidth;
    private Point? _arrangePatternDragStart;
    private SongArrangeRowViewModel? _draggedArrangePattern;

    public MainWindow()
    {
        InitializeComponent();
        _mainViewModel = new MainViewModel();
        _mainViewModel.PropertyChanged += MainViewModel_PropertyChanged;
        DataContext = _mainViewModel;
        Loaded += MainWindow_Loaded;
        Closed += OnClosed;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        viewModel.VstPlugins.CollectionChanged += (_, args) =>
        {
            if (args.NewItems is null)
            {
                return;
            }

            foreach (var plugin in args.NewItems.OfType<VstPluginViewModel>())
            {
                _ = GenerateVstThumbnailAsync(plugin);
            }
        };

        await Task.WhenAll(viewModel.VstPlugins.Select(GenerateVstThumbnailAsync));
    }

    private async Task GenerateVstThumbnailAsync(VstPluginViewModel plugin)
    {
        if (!_thumbnailRequests.Add(plugin.FilePath))
        {
            return;
        }

        try
        {
            plugin.Thumbnail = await _vst3ThumbnailService.GetOrCreateAsync(plugin);
        }
        finally
        {
            _thumbnailRequests.Remove(plugin.FilePath);
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_mainViewModel is not null)
        {
            _mainViewModel.PropertyChanged -= MainViewModel_PropertyChanged;
            _mainViewModel = null;
        }

        if (DataContext is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private void MainViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (sender is not MainViewModel viewModel)
            {
                return;
            }

            if (e.PropertyName == nameof(MainViewModel.CurrentPlayingArrangeRow))
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (viewModel.CurrentPlayingArrangeRow is not null
                        && SongArrangeRowsItemsControl.ItemContainerGenerator.ContainerFromItem(viewModel.CurrentPlayingArrangeRow) is FrameworkElement row)
                    {
                        row.BringIntoView();
                    }
                });
            }

            if (e.PropertyName is nameof(MainViewModel.CurrentSongBarNumber) or nameof(MainViewModel.TimelineBarWidth))
            {
                Dispatcher.BeginInvoke(() => ScrollTimelineToCurrentBar(viewModel));
            }
        }

    private void ScrollTimelineToCurrentBar(MainViewModel viewModel)
        {
            var barStart = MainViewModel.TimelineTrackHeaderWidth +
                           ((viewModel.CurrentSongBarNumber - 1) * viewModel.TimelineBarWidth);
            var barEnd = barStart + viewModel.TimelineBarWidth;
            var visibleStart = ArrangeTimelineScrollViewer.HorizontalOffset;
            var visibleEnd = visibleStart + ArrangeTimelineScrollViewer.ViewportWidth;
            if (barStart < visibleStart)
            {
                ArrangeTimelineScrollViewer.ScrollToHorizontalOffset(
                    Math.Max(0, barStart - MainViewModel.TimelineTrackHeaderWidth));
            }
            else if (barEnd > visibleEnd)
            {
                ArrangeTimelineScrollViewer.ScrollToHorizontalOffset(
                    Math.Max(0, barEnd - ArrangeTimelineScrollViewer.ViewportWidth));
            }
    }

    private void ArrangeTimelineScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
            if (DataContext is MainViewModel viewModel)
            {
                viewModel.TimelineViewportWidth = e.NewSize.Width;
            }
    }

    private void TimelineTrackList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
            if (_isSynchronizingTrackScroll || e.VerticalChange == 0 || e.OriginalSource is not ScrollViewer source)
            {
                return;
            }

            var targetList = ReferenceEquals(sender, FrozenTrackListBox)
                ? TimelineBodyListBox
                : FrozenTrackListBox;
            var target = FindVisualChild<ScrollViewer>(targetList);
            if (target is null)
            {
                return;
            }

            _isSynchronizingTrackScroll = true;
            try
            {
                target.ScrollToVerticalOffset(source.VerticalOffset);
            }
            finally
            {
                _isSynchronizingTrackScroll = false;
            }
    }

    private void TimelineRuler_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
            if (sender is not UIElement ruler)
            {
                return;
            }

            _isDraggingTimelineRuler = true;
            _timelineRulerDragStartX = e.GetPosition(this).X;
            _timelineRulerDragStartBarWidth = (DataContext as MainViewModel)?.TimelineBarWidth ?? 180;
            ruler.CaptureMouse();
            e.Handled = true;
    }

    private void TimelineRuler_PreviewMouseMove(object sender, MouseEventArgs e)
    {
            if (!_isDraggingTimelineRuler || e.LeftButton != MouseButtonState.Pressed)
            {
                return;
            }

            if (DataContext is not MainViewModel viewModel)
            {
                return;
            }

            var delta = e.GetPosition(this).X - _timelineRulerDragStartX;
            viewModel.TimelineBarWidth = _timelineRulerDragStartBarWidth + delta;
            e.Handled = true;
    }

    private void TimelineRuler_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
            if (!_isDraggingTimelineRuler)
            {
                return;
            }

            _isDraggingTimelineRuler = false;
            if (sender is UIElement ruler)
            {
                ruler.ReleaseMouseCapture();
            }

            e.Handled = true;
    }

    private void Timeline_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
            if (DataContext is not MainViewModel viewModel)
            {
                return;
            }

            var pointerX = e.GetPosition(ArrangeTimelineScrollViewer).X;
            var timelinePointerX = Math.Max(0, pointerX - MainViewModel.TimelineTrackHeaderWidth);
            var contentPosition = ArrangeTimelineScrollViewer.HorizontalOffset + timelinePointerX;
            var oldWidth = viewModel.TimelineBarWidth;
            var newWidth = Math.Clamp(oldWidth + (e.Delta > 0 ? 20 : -20), 80, 480);
            if (Math.Abs(newWidth - oldWidth) < double.Epsilon)
            {
                e.Handled = true;
                return;
            }

            viewModel.TimelineBarWidth = newWidth;
            var anchoredOffset = (contentPosition * (newWidth / oldWidth)) - timelinePointerX;
            Dispatcher.BeginInvoke(() =>
                ArrangeTimelineScrollViewer.ScrollToHorizontalOffset(Math.Max(0, anchoredOffset)));
            e.Handled = true;
    }

    private static T? FindVisualChild<T>(DependencyObject parent)
            where T : DependencyObject
    {
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            {
                var child = VisualTreeHelper.GetChild(parent, index);
                if (child is T match)
                {
                    return match;
                }

                var descendant = FindVisualChild<T>(child);
                if (descendant is not null)
                {
                    return descendant;
                }
            }

            return null;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && DataContext is MainViewModel transportViewModel)
        {
            transportViewModel.TogglePlayback();
            e.Handled = true;
            return;
        }

        if (Keyboard.FocusedElement is TextBox)
        {
            return;
        }

        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        if (e.Key == Key.S && (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            viewModel.SaveProjectAsCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.S && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            viewModel.SaveProjectCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.L && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            viewModel.LoadProjectCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.N && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            viewModel.NewProjectCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key is Key.OemPeriod or Key.Decimal)
        {
            viewModel.RestartPlayback();
            e.Handled = true;
            return;
        }

        if (e.Key is Key.Left or Key.Right)
        {
            viewModel.MovePlaybackByBars(e.Key == Key.Left ? -1 : 1);
            e.Handled = true;
        }
    }

    private void PianoRollCell_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element)
        {
            return;
        }

        if (DataContext is not MainViewModel viewModel || element.DataContext is not PianoRollCellViewModel cell)
        {
            return;
        }

        if (e.ChangedButton == MouseButton.Right)
        {
            viewModel.PaintPianoRollCell(cell, false);
        }
        else
        {
            viewModel.PaintPianoRollCell(cell, true);
        }

        element.CaptureMouse();
        e.Handled = true;
    }

    private void PianoRollCell_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement element)
        {
            return;
        }

        if (DataContext is not MainViewModel viewModel || element.DataContext is not PianoRollCellViewModel cell)
        {
            return;
        }

        if (Mouse.LeftButton == MouseButtonState.Pressed)
        {
            viewModel.PaintPianoRollCell(cell, true);
        }
        else if (Mouse.RightButton == MouseButtonState.Pressed)
        {
            viewModel.PaintPianoRollCell(cell, false);
        }
    }

    private void PianoRollCell_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is UIElement element)
        {
            element.ReleaseMouseCapture();
        }
    }

    private void ArrangeCell_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element)
        {
            return;
        }

        if (DataContext is not MainViewModel viewModel || element.DataContext is not ArrangeCellViewModel cell)
        {
            return;
        }

        viewModel.SelectArrangeCell(cell);
        if (e.ChangedButton == MouseButton.Left && e.ClickCount == 2)
        {
            ShowArrangePatternEditorDialog(viewModel);
        }
        e.Handled = true;
    }

    private void ArrangeBlock_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element)
        {
            return;
        }

        if (DataContext is not MainViewModel viewModel || element.DataContext is not SongArrangeBlockViewModel block)
        {
            return;
        }

        viewModel.SelectArrangeBlock(block);
        e.Handled = true;
    }

    private void ArrangeBar_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element)
        {
            return;
        }

        if (DataContext is not MainViewModel viewModel || element.DataContext is not SongArrangeRowViewModel row)
        {
            return;
        }

        viewModel.SelectSongArrangeRow(row);
        e.Handled = true;
    }

    private void ArrangePatternRow_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element)
        {
            return;
        }

        if (DataContext is not MainViewModel viewModel || element.DataContext is not SongArrangeRowViewModel row)
        {
            return;
        }

        viewModel.SelectSongArrangeRow(row);
        if (e.ChangedButton == MouseButton.Left && e.ClickCount == 1)
        {
            BeginArrangePatternDrag(element, row, false);
        }

        if (e.ChangedButton == MouseButton.Left && e.ClickCount == 2)
        {
            ShowArrangePatternEditorDialog(viewModel);
        }
        e.Handled = true;
    }

    private void ArrangePatternDragHandle_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element ||
            DataContext is not MainViewModel viewModel ||
            viewModel.SelectedArrangeCell is null)
        {
            return;
        }

        _draggedArrangePattern = viewModel.SongArrangeRows.FirstOrDefault(row =>
            row.Cells.Contains(viewModel.SelectedArrangeCell));
        if (_draggedArrangePattern is null)
        {
            return;
        }

        BeginArrangePatternDrag(element, _draggedArrangePattern, true);
        e.Handled = true;
    }

    private void ArrangePatternDragHandle_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        ContinueArrangePatternDrag(sender, e);
    }

    private void ArrangePatternListItem_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        ContinueArrangePatternDrag(sender, e);
    }

    private void BeginArrangePatternDrag(
        FrameworkElement element,
        SongArrangeRowViewModel row,
        bool highlightHeader)
    {
        ResetArrangePatternDrag();
        _arrangePatternDragStart = Mouse.GetPosition(this);
        _draggedArrangePattern = row;
        if (highlightHeader)
        {
            ArrangePatternDragHandle.Background = new SolidColorBrush(Color.FromRgb(47, 120, 196));
        }
        else
        {
            row.IsDragging = true;
        }

        element.CaptureMouse();
    }

    private void ContinueArrangePatternDrag(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement element ||
            _arrangePatternDragStart is not Point dragStart ||
            _draggedArrangePattern is null ||
            e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var position = e.GetPosition(this);
        if (Math.Abs(position.X - dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var data = new DataObject(ArrangePatternDragFormat, _draggedArrangePattern);
        element.ReleaseMouseCapture();
        try
        {
            DragDrop.DoDragDrop(element, data, DragDropEffects.Move);
        }
        finally
        {
            ResetArrangePatternDrag();
        }
    }

    private void ArrangePatternDragHandle_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        CompleteArrangePatternDrag(sender, e);
    }

    private void ArrangePatternListItem_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        CompleteArrangePatternDrag(sender, e);
    }

    private void CompleteArrangePatternDrag(object sender, MouseButtonEventArgs e)
    {
        if (sender is UIElement element)
        {
            element.ReleaseMouseCapture();
        }

        ResetArrangePatternDrag();
        e.Handled = true;
    }

    private void ResetArrangePatternDrag()
    {
        if (_draggedArrangePattern is not null)
        {
            _draggedArrangePattern.IsDragging = false;
        }

        _arrangePatternDragStart = null;
        _draggedArrangePattern = null;
        ArrangePatternDragHandle.Background = (Brush)FindResource("Paper");
    }

    private void ArrangePatternRow_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = sender is FrameworkElement { DataContext: SongArrangeRowViewModel targetRow } &&
                    e.Data.GetDataPresent(ArrangePatternDragFormat) &&
                    e.Data.GetData(ArrangePatternDragFormat) is SongArrangeRowViewModel sourceRow &&
                    !ReferenceEquals(sourceRow, targetRow)
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void ArrangePatternRow_Drop(object sender, DragEventArgs e)
    {
        if (DataContext is MainViewModel viewModel &&
            sender is FrameworkElement { DataContext: SongArrangeRowViewModel targetRow } element &&
            e.Data.GetData(ArrangePatternDragFormat) is SongArrangeRowViewModel sourceRow)
        {
            var insertAfter = e.GetPosition(element).Y >= element.ActualHeight / 2;
            viewModel.MoveArrangePattern(sourceRow, targetRow, insertAfter);
        }

        e.Handled = true;
    }

    private void ArrangePatternEndDrop_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(ArrangePatternDragFormat) &&
                    e.Data.GetData(ArrangePatternDragFormat) is SongArrangeRowViewModel
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void ArrangePatternEndDrop_Drop(object sender, DragEventArgs e)
    {
        if (DataContext is MainViewModel viewModel &&
            e.Data.GetData(ArrangePatternDragFormat) is SongArrangeRowViewModel sourceRow)
        {
            viewModel.MoveArrangePatternToEnd(sourceRow);
        }

        e.Handled = true;
    }

    private void ShowArrangePatternEditorDialog(MainViewModel viewModel)
    {
        if (!viewModel.TryGetSelectedArrangePatternEditorState(out var patternName, out var assignmentNumber, out var subChannelIndex))
        {
            return;
        }

        var dialog = new ArrangePatternEditDialog(patternName, assignmentNumber, subChannelIndex)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        if (!viewModel.TryApplySelectedArrangePatternEditorState(
                dialog.PatternName,
                dialog.AssignmentNumber,
                dialog.SubChannelIndex,
                out var errorMessage))
        {
            MessageBox.Show(errorMessage, "Arrange Pattern", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ArrangePatternRow_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SongArrangeRowViewModel row })
        {
            row.IsPointerOver = true;
        }
    }

    private void ArrangePatternRow_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SongArrangeRowViewModel row })
        {
            row.IsPointerOver = false;
        }
    }

    private void EditableTextBox_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not TextBox { IsReadOnly: false, IsEnabled: true } textBox)
        {
            return;
        }

        var increment = e.Delta > 0 ? 1 : -1;
        if (int.TryParse(textBox.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var integerValue))
        {
            textBox.Text = (integerValue + increment).ToString(CultureInfo.CurrentCulture);
        }
        else if (double.TryParse(textBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var decimalValue))
        {
            textBox.Text = (decimalValue + increment).ToString("0.###", CultureInfo.CurrentCulture);
        }
        else
        {
            return;
        }

        textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        e.Handled = true;
    }

    private void CloseProjectMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.NewProjectCommand.Execute(null);
        }
    }

    private void CloseMenuItem_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private async void MeasureLatencyMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var handle = AsioSessionRegistry.Current;
        if (handle is null)
        {
            MessageBox.Show(this, "Start a plug-in on the ASIO backend first. The measurement uses that running session.", "Measure latency", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var host = new Vst3PluginHost();
        var result = await Task.Run(() =>
        {
            var measured = host.TryMeasureAsioLatency(handle, out var milliseconds, out var error);
            var timing = host.TryGetAsioTiming(handle, out var bufferFrames, out var outputLatency, out _);
            return (measured, milliseconds, error, timing, bufferFrames, outputLatency);
        });

        if (!result.measured)
        {
            MessageBox.Show(this, $"Latency not measured: {result.error}", "Measure latency", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var text = $"Measured round trip: {result.milliseconds.ToString("F2", CultureInfo.InvariantCulture)} ms.";
        if (result.timing)
        {
            text += $"\nBuffer {result.bufferFrames} samples; the driver declares {result.outputLatency} samples of output latency.";
        }
        MessageBox.Show(this, text, "Measure latency", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void MixerMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        new MixerWindow(viewModel) { Owner = this }.Show();
    }

    private void DriverSettingsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var settings = new AppSettingsService().Load();
        if (string.IsNullOrWhiteSpace(settings.AsioDriverClsid))
        {
            MessageBox.Show(this, "Choose an ASIO driver first (Audio > ASIO driver).", "ASIO driver settings", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var driverName = new AsioDriverService().GetInstalledDrivers()
            .FirstOrDefault(driver => driver.Clsid == settings.AsioDriverClsid)?.Name ?? settings.AsioDriverClsid;
        new DriverSettingsDialog(driverName, settings.AsioDriverClsid) { Owner = this }.ShowDialog();
    }

    private void AddMidiTrackMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ShowAddTrackDialog(PatternTrackType.MidiChannel);
    }

    private void AddVstTrackMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ShowAddTrackDialog(PatternTrackType.VstInstrument);
    }

    private void AddAudioTrackMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ShowAddTrackDialog(PatternTrackType.AudioInput);
    }

    private void TrackList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        if (sender is ListBox { SelectedItem: ArrangeTimelineTrackViewModel timelineTrack })
        {
            viewModel.SelectedTrack = timelineTrack.SourceTrack;
        }

        if (viewModel.SelectedTrack is null)
        {
            return;
        }

        ShowEditTrackDialog(viewModel, viewModel.SelectedTrack);
    }

    private void VstPluginThumbnail_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not VstPluginViewModel plugin)
        {
            return;
        }

        Window window = string.Equals(plugin.Kind, "VST3", StringComparison.OrdinalIgnoreCase)
            ? new Vst3NativeEditorWindow(plugin.Name, plugin.FilePath)
            : new VstPluginEditorDialog(plugin.Name, plugin.FilePath, plugin.Kind);
        window.Owner = this;
        window.Show();
        e.Handled = true;
    }

    private void TrackColorMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem || DataContext is not MainViewModel viewModel)
        {
            return;
        }

        if (menuItem.Parent is not ContextMenu contextMenu)
        {
            return;
        }

        if (contextMenu.PlacementTarget is not FrameworkElement placementTarget ||
            placementTarget.DataContext is not ArrangeTimelineTrackViewModel timelineTrack)
        {
            return;
        }

        var colorHex = menuItem.Tag as string;
        viewModel.SetTrackColor(timelineTrack.SourceTrack, colorHex);
    }

    private void ShowAddTrackDialog(PatternTrackType trackType)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        if (!viewModel.TryGetNewTrackDefaults(trackType, out var name, out var instrument, out var source, out var channel, out var noteNumber))
        {
            return;
        }

        var dialog = new TrackEditDialog(name, instrument, trackType, source, channel, noteNumber, true)
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        if (!viewModel.TryAddTrack(
                dialog.TrackType,
                dialog.TrackName,
                dialog.Instrument,
                dialog.Source,
                dialog.Channel,
                dialog.NoteNumber,
                out var errorMessage))
        {
            MessageBox.Show(errorMessage, "New track", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void EditSelectedTrackButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel || viewModel.SelectedTrack is not { } track)
        {
            return;
        }

        ShowEditTrackDialog(viewModel, track);
    }

    private void ShowEditTrackDialog(MainViewModel viewModel, PatternTrackViewModel track)
    {
        var dialog = new TrackEditDialog(
            track.Name,
            track.Instrument,
            track.TrackType,
            track.Source,
            track.Channel,
            track.NoteNumber,
            false)
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        if (!viewModel.TryUpdateTrack(
                track,
                dialog.TrackType,
                dialog.TrackName,
                dialog.Instrument,
                dialog.Source,
                dialog.Channel,
                dialog.NoteNumber,
                out var errorMessage))
        {
            MessageBox.Show(errorMessage, "Edit track", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

}
