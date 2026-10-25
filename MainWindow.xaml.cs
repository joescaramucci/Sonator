using System.IO;
using System.Windows;
using System.Globalization;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.ComponentModel;
using Sonator.Models;
using Sonator.Services;
using Sonator.Services.Effects;
using Sonator.Services.Vst3;
using Sonator.ViewModels;

namespace Sonator;

public partial class MainWindow : Window
{
    private const string ArrangePatternDragFormat = "Sonator.ArrangePatternRow";
    private static readonly string[] AudioFileExtensions = { ".wav", ".mp3" };
    private const string VstPluginDragFormat = "Sonator.VstPluginPath";
    private readonly Dictionary<PatternTrackViewModel, Window> _trackPluginWindows = new();
    private Point? _vstPluginDragStart;
    private string? _vstPluginDragPath;
    private MixerWindow? _mixerWindow;
    private WaveformEditorWindow? _waveformEditorWindow;
    private readonly Vst3ThumbnailService _vst3ThumbnailService = new();
    private readonly HashSet<string> _thumbnailRequests = new(StringComparer.OrdinalIgnoreCase);
    private MainViewModel? _mainViewModel;
    private bool _isSynchronizingTrackScroll;
    private bool _isDraggingTimelineRuler;
    private bool _isPanningTimeline;
    private Border? _blockBorder;
    private TimelineBlockViewModel? _blockTimeline;
    private Point _blockPressPoint;
    private bool _blockResizing;
    private bool _blockResizingStart;
    private int _blockMoveSteps;
    private bool _timelinePanPending;
    private Point _timelinePanStart;
    private double _timelinePanStartOffset;
    private double _timelineRulerDragStartX;
    private Point? _arrangePatternDragStart;
    private SongArrangeRowViewModel? _draggedArrangePattern;

    public MainWindow()
    {
        InitializeComponent();
        WindowFrame.UseDarkFrame(this);
        _mainViewModel = new MainViewModel();
        _mainViewModel.PropertyChanged += MainViewModel_PropertyChanged;
        DataContext = _mainViewModel;
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        Closed += OnClosed;
    }

    // Exit, the window's X and Alt+F4 all ask whether to save the project first, when it has unsaved changes.
    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_mainViewModel is null || !_mainViewModel.HasUnsavedChanges)
        {
            return;
        }

        var choice = SonatorMessageDialog.AskToSave(this, "Exit Sonator", "Do you want to save the project before closing?");
        e.Cancel = choice switch
        {
            SaveChoice.Save => !_mainViewModel.SaveProject(),
            SaveChoice.DontSave => false,
            _ => true,
        };
    }

    // File > Open recent lists the projects last saved or opened, the latest first, numbered for the keyboard.
    private void FileMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource != sender || DataContext is not MainViewModel viewModel)
        {
            return;
        }

        OpenRecentMenuItem.Items.Clear();
        var recent = viewModel.RecentProjects;
        for (var i = 0; i < recent.Count; i++)
        {
            var path = recent[i];
            var item = new MenuItem
            {
                Header = $"_{(i + 1) % 10} {Path.GetFileName(path).Replace("_", "__")}",
                ToolTip = path,
            };
            item.Click += (_, _) => OpenRecentProject(viewModel, path);
            OpenRecentMenuItem.Items.Add(item);
        }

        OpenRecentMenuItem.IsEnabled = recent.Count > 0;

        AutosaveMenuItem.Items.Clear();
        foreach (var minutes in viewModel.AutosaveIntervalChoices)
        {
            var item = new MenuItem
            {
                Header = minutes == 0 ? "_Off" : $"Every _{minutes} min",
                IsCheckable = true,
                IsChecked = minutes == viewModel.AutosaveMinutes,
            };
            item.Click += (_, _) => viewModel.AutosaveMinutes = minutes;
            AutosaveMenuItem.Items.Add(item);
        }
    }

    private void OpenRecentProject(MainViewModel viewModel, string path)
    {
        if (MayLeaveProject(viewModel, "Open project", "Do you want to save the project before opening another?"))
        {
            viewModel.OpenProject(path);
        }
    }

    // Before the project open now is replaced (closed, or another opened or started), asks whether to save it
    // when it has changes, the sounds chosen in its plug-ins included. False when the player cancels, or the
    // save fails.
    private bool MayLeaveProject(MainViewModel viewModel, string title, string question)
    {
        if (!viewModel.HasUnsavedChanges)
        {
            return true;
        }

        return SonatorMessageDialog.AskToSave(this, title, question) switch
        {
            SaveChoice.Save => viewModel.SaveProject(),
            SaveChoice.DontSave => true,
            _ => false,
        };
    }

    private void NewProject(MainViewModel viewModel)
    {
        if (MayLeaveProject(viewModel, "New project", "Do you want to save the project before starting a new one?"))
        {
            viewModel.NewProjectCommand.Execute(null);
        }
    }

    private void OpenProject(MainViewModel viewModel)
    {
        if (MayLeaveProject(viewModel, "Open project", "Do you want to save the project before opening another?"))
        {
            viewModel.LoadProjectCommand.Execute(null);
        }
    }

    private void NewProjectMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
        {
            NewProject(viewModel);
        }
    }

    private void OpenProjectMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
        {
            OpenProject(viewModel);
        }
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        viewModel.Health.Start();
        viewModel.TracksReplaced += ReopenFxRacks;
        viewModel.TrackInstrumentStopping += ClosePluginWindowsOf;
        viewModel.EffectPluginUnloading += ClosePluginWindowsOf;
        viewModel.SavedInstrumentStarted += DockSavedInstrument;
        OfferToRecover(viewModel);
        FillSonEffectList(viewModel);
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

    // When the last session ended without saving its project (Sonator or Windows stopped), offers the work it
    // autosaved; then autosaving starts for this session.
    private void OfferToRecover(MainViewModel viewModel)
    {
        var autosave = new ProjectAutosave();
        if (autosave.Find() is { } info)
        {
            var project = string.IsNullOrWhiteSpace(info.ProjectPath) ? "a new project, never saved" : Path.GetFileName(info.ProjectPath);
            var recover = SonatorMessageDialog.Ask(this, "Recover autosaved work",
                $"Sonator stopped before saving {project}. Its work was autosaved at {info.SavedAt:HH:mm} on {info.SavedAt:d}. Recover it?",
                "RECOVER", "DISCARD");
            if (!recover || !viewModel.RecoverAutosave(autosave, info))
            {
                autosave.Discard(info);
            }
        }

        viewModel.UseAutosave(autosave);
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
        // The windows that belong to no window close with this one.
        _mixerWindow?.Close();
        _soundBrowser?.Close();
        _cromator?.Close();
        foreach (var sondyne in _sondynes.Values.ToList())
        {
            sondyne.Close();
        }

        if (_mainViewModel is not null)
        {
            _mainViewModel.Health.Stop();
            _mainViewModel.ClearAutosave();
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

    private static bool IsInsideNoteBlock(object? source)
    {
        return IsInsideTagged(source, "NoteBlock");
    }

    private static bool IsInsideTagged(object? source, string tag)
    {
        // A press on text starts on its run, which is not in the visual tree: its parents are the logical ones.
        for (var element = source as DependencyObject; element is not null;
             element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element))
        {
            if (element is FrameworkElement { Tag: string elementTag } && elementTag == tag)
            {
                return true;
            }
        }

        return false;
    }

    // A block moves when grabbed, and changes length when its right edge is grabbed. Both snap to the SNAP grid.
    private void NoteBlock_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border block || block.DataContext is not TimelineBlockViewModel timeline)
        {
            return;
        }

        // A double-click opens the block's notes in the piano roll.
        if (e.ClickCount == 2 && DataContext is MainViewModel model)
        {
            model.SelectBlock(timeline.Track, timeline.Block);
            OpenPianoRoll(model, timeline.Track);
            e.Handled = true;
            return;
        }

        // Ctrl+click adds the block to the blocks selected, or takes it from them.
        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control && DataContext is MainViewModel adding)
        {
            adding.SelectBlock(timeline.Track, timeline.Block, add: true);
            e.Handled = true;
            return;
        }

        _blockBorder = block;
        _blockTimeline = timeline;
        _blockPressPoint = e.GetPosition(this);
        _blockResizing = IsInsideTagged(e.OriginalSource, "NoteBlockEdge");
        _blockResizingStart = IsInsideTagged(e.OriginalSource, "NoteBlockStartEdge");
        _blockMoveSteps = 0;
        block.CaptureMouse();
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.SelectBlock(timeline.Track, timeline.Block);
        }

        e.Handled = true;
    }

    private void NoteBlock_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_blockBorder is null || _blockTimeline is null || DataContext is not MainViewModel viewModel
            || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var stepWidth = viewModel.TimelineSixteenthWidth;
        var steps = (int)Math.Round((e.GetPosition(this).X - _blockPressPoint.X) / stepWidth);
        if (!_blockResizing && !_blockResizingStart)
        {
            _blockMoveSteps = viewModel.SnapStep(_blockTimeline.Block.StartStep + steps) - _blockTimeline.Block.StartStep;
            _blockBorder.RenderTransform = new TranslateTransform(_blockMoveSteps * stepWidth, 0);
        }

        e.Handled = true;
    }

    private void NoteBlock_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_blockBorder is null || _blockTimeline is null || DataContext is not MainViewModel viewModel)
        {
            return;
        }

        var block = _blockBorder;
        var timeline = _blockTimeline;
        var stepWidth = viewModel.TimelineSixteenthWidth;
        var steps = (int)Math.Round((e.GetPosition(this).X - _blockPressPoint.X) / stepWidth);
        var resizing = _blockResizing;
        var resizingStart = _blockResizingStart;
        var moveSteps = _blockMoveSteps;

        _blockBorder = null;
        _blockTimeline = null;
        block.ReleaseMouseCapture();
        block.RenderTransform = Transform.Identity;

        var current = timeline.Block;
        if (resizingStart)
        {
            var newStart = viewModel.SnapStep(current.StartStep + steps);
            if (newStart != current.StartStep && newStart < current.EndStep)
            {
                viewModel.StretchBlock(timeline.Track, current, newStart, current.EndStep);
            }
        }
        else if (resizing)
        {
            var newEnd = viewModel.SnapStep(current.EndStep + steps);
            if (newEnd > current.StartStep && newEnd != current.EndStep)
            {
                viewModel.StretchBlock(timeline.Track, current, current.StartStep, newEnd);
            }
        }
        else
        {
            viewModel.MoveBlock(timeline.Track, current, moveSteps);
        }

        e.Handled = true;
    }

    // ---- Dragging an audio clip: along its track to move it in time, onto another empty track to move it there ----

    private Border? _clipBorder;
    private ArrangeTimelineTrackViewModel? _clipTimeline;
    private Point _clipPressPoint;
    private int _clipMoveSteps;

    private void AudioClip_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border clip || clip.DataContext is not ArrangeTimelineTrackViewModel timeline)
        {
            return;
        }

        // A double-click opens the clip in the waveform editor.
        if (e.ClickCount == 2)
        {
            OpenClipInWaveformEditor(timeline.SourceTrack);
            e.Handled = true;
            return;
        }

        // The arrow becomes the move cross only while the clip is held.
        clip.Cursor = Cursors.SizeAll;
        _clipBorder = clip;
        _clipTimeline = timeline;
        _clipPressPoint = e.GetPosition(this);
        _clipMoveSteps = 0;
        clip.CaptureMouse();
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.SelectedTrack = timeline.SourceTrack;
        }

        e.Handled = true;
    }

    private void AudioClip_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_clipBorder is null || _clipTimeline is null || DataContext is not MainViewModel viewModel
            || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var stepWidth = viewModel.TimelineSixteenthWidth;
        var moved = e.GetPosition(this) - _clipPressPoint;
        var start = _clipTimeline.AudioStartStep;
        _clipMoveSteps = Math.Max(0, viewModel.SnapStep(start + (int)Math.Round(moved.X / stepWidth))) - start;
        _clipBorder.RenderTransform = new TranslateTransform(_clipMoveSteps * stepWidth, 0);

        // The clip stays drawn in its own lane; the status line says where it will land.
        var newStart = start + _clipMoveSteps;
        var onto = TrackUnder(e.GetPosition(this), _clipBorder)?.SourceTrack;
        var where = onto is null || ReferenceEquals(onto, _clipTimeline.SourceTrack)
            ? _clipTimeline.SourceTrack.Name
            : MainViewModel.IsEmptyTrack(onto) ? onto.Name : $"{_clipTimeline.SourceTrack.Name} ({onto.Name} is not empty)";
        viewModel.StatusMessage = $"Audio clip: {where}, bar {newStart / 16 + 1}, step {newStart % 16 + 1}";
        e.Handled = true;
    }

    // The timeline lane under a point of the window, looking past the element being dragged.
    private ArrangeTimelineTrackViewModel? TrackUnder(Point point, Visual dragged)
    {
        ArrangeTimelineTrackViewModel? found = null;
        VisualTreeHelper.HitTest(this,
            candidate => ReferenceEquals(candidate, dragged) ? HitTestFilterBehavior.ContinueSkipSelfAndChildren : HitTestFilterBehavior.Continue,
            result =>
            {
                if (FindVisualAncestor<ListBoxItem>(result.VisualHit)?.DataContext is ArrangeTimelineTrackViewModel track)
                {
                    found = track;
                    return HitTestResultBehavior.Stop;
                }

                return HitTestResultBehavior.Continue;
            },
            new PointHitTestParameters(point));
        return found;
    }

    private void AudioClip_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_clipBorder is null || _clipTimeline is null || DataContext is not MainViewModel viewModel)
        {
            return;
        }

        var clip = _clipBorder;
        var timeline = _clipTimeline;
        var moveSteps = _clipMoveSteps;
        _clipBorder = null;
        _clipTimeline = null;
        clip.ReleaseMouseCapture();
        clip.RenderTransform = Transform.Identity;
        clip.Cursor = null;

        var target = TrackUnder(e.GetPosition(this), clip);
        var onto = target?.SourceTrack is { } other && !ReferenceEquals(other, timeline.SourceTrack) ? other : null;
        if (onto is not null && !MainViewModel.IsEmptyTrack(onto))
        {
            viewModel.StatusMessage = $"{onto.Name} is not empty: the clip stays on {timeline.SourceTrack.Name}";
            onto = null;
        }

        viewModel.MoveAudioClip(timeline.SourceTrack, timeline.AudioStartStep + moveSteps, onto);
        e.Handled = true;
    }

    // Dragging the timeline with the middle button, or with the left button after a few pixels of movement,
    // moves it left and right. A left click without movement still reaches the track under the mouse. On the
    // lanes the left button draws a rectangle to select blocks instead.
    private void ArrangeTimelineScrollViewer_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ScrollViewer viewer || e.ChangedButton is not (MouseButton.Left or MouseButton.Middle))
        {
            return;
        }

        // A press on a block belongs to the block: it moves or resizes it instead of scrolling. So does a press
        // on an automation lane, which adds or drags a point, one on an audio clip, which drags the clip, and one
        // on the ruler, which chooses the part to loop.
        if (e.ChangedButton == MouseButton.Left
            && (IsInsideNoteBlock(e.OriginalSource) || IsInsideTagged(e.OriginalSource, "AutomationLane") || IsInsideTagged(e.OriginalSource, "AudioClip")
                || IsInsideTagged(e.OriginalSource, "TimelineRuler") || IsOnLaneArea(e)))
        {
            return;
        }

        _timelinePanStart = e.GetPosition(viewer);
        _timelinePanStartOffset = viewer.HorizontalOffset;
        _timelinePanPending = true;
        if (e.ChangedButton == MouseButton.Middle)
        {
            BeginTimelinePan(viewer);
            e.Handled = true;
        }
    }

    private void ArrangeTimelineScrollViewer_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not ScrollViewer viewer || !_timelinePanPending)
        {
            return;
        }

        var dx = e.GetPosition(viewer).X - _timelinePanStart.X;
        if (!_isPanningTimeline)
        {
            if (Math.Abs(dx) < SystemParameters.MinimumHorizontalDragDistance)
            {
                return;
            }

            if (e.LeftButton != MouseButtonState.Pressed)
            {
                _timelinePanPending = false;
                return;
            }

            BeginTimelinePan(viewer);
        }

        viewer.ScrollToHorizontalOffset(Math.Max(0, _timelinePanStartOffset - dx));
        e.Handled = true;
    }

    private void ArrangeTimelineScrollViewer_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ScrollViewer viewer || e.ChangedButton is not (MouseButton.Middle or MouseButton.Left))
        {
            return;
        }

        _timelinePanPending = false;
        if (!_isPanningTimeline)
        {
            return;
        }

        _isPanningTimeline = false;
        viewer.ReleaseMouseCapture();
        viewer.Cursor = null;
        e.Handled = true;
    }

    private void BeginTimelinePan(ScrollViewer viewer)
    {
        _isPanningTimeline = true;
        viewer.CaptureMouse();
        viewer.Cursor = Cursors.SizeWE;
    }

    // ---- The ruler: drag along it to choose the part to loop, click to go there ----

    // What a drag on the ruler is doing: choosing a new range, or moving the range's left or right edge.
    private enum RulerDrag { NewRange, StartEdge, EndEdge }

    private RulerDrag _rulerDrag;
    private int _rulerAnchorStep;
    private bool _rulerMoved;

    private const double RulerEdgeGrip = 6;

    // The step under a point of the ruler, and how far the point is from the loop range's edges.
    private static int RulerStep(MainViewModel viewModel, double x) =>
        Math.Max(0, (int)Math.Floor(x / viewModel.TimelineSixteenthWidth));

    private RulerDrag? RulerEdgeAt(MainViewModel viewModel, double x)
    {
        if (!viewModel.HasLoopRange)
        {
            return null;
        }

        var width = viewModel.TimelineSixteenthWidth;
        if (Math.Abs(x - viewModel.LoopStartStep * width) <= RulerEdgeGrip)
        {
            return RulerDrag.StartEdge;
        }

        return Math.Abs(x - (viewModel.LoopEndStep + 1) * width) <= RulerEdgeGrip ? RulerDrag.EndEdge : null;
    }

    private void TimelineRuler_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement ruler || DataContext is not MainViewModel viewModel)
        {
            return;
        }

        var x = e.GetPosition(ruler).X;
        _rulerDrag = RulerEdgeAt(viewModel, x) ?? RulerDrag.NewRange;
        _rulerAnchorStep = _rulerDrag switch
        {
            RulerDrag.StartEdge => viewModel.LoopEndStep + 1, // the other edge stays where it is
            RulerDrag.EndEdge => viewModel.LoopStartStep,
            _ => viewModel.SnapStep(RulerStep(viewModel, x)),
        };
        _rulerMoved = false;
        _timelineRulerDragStartX = x;
        _isDraggingTimelineRuler = true;
        ruler.CaptureMouse();
        e.Handled = true;
    }

    private void TimelineRuler_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement ruler || DataContext is not MainViewModel viewModel)
        {
            return;
        }

        var x = e.GetPosition(ruler).X;
        if (!_isDraggingTimelineRuler || e.LeftButton != MouseButtonState.Pressed)
        {
            ruler.Cursor = RulerEdgeAt(viewModel, x) is null ? null : Cursors.SizeWE;
            return;
        }

        if (!_rulerMoved && Math.Abs(x - _timelineRulerDragStartX) < 4)
        {
            return;
        }

        // The edges snap to the SNAP grid; the range covers the steps between them.
        _rulerMoved = true;
        var edge = viewModel.SnapStep(RulerStep(viewModel, x + viewModel.TimelineSixteenthWidth / 2));
        if (edge != _rulerAnchorStep)
        {
            viewModel.SetLoopRange(Math.Min(edge, _rulerAnchorStep), Math.Max(edge, _rulerAnchorStep) - 1);
        }

        e.Handled = true;
    }

    private void TimelineRuler_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDraggingTimelineRuler)
        {
            return;
        }

        _isDraggingTimelineRuler = false;
        if (sender is FrameworkElement ruler)
        {
            ruler.ReleaseMouseCapture();
            if (!_rulerMoved && DataContext is MainViewModel viewModel)
            {
                viewModel.SeekToStep(RulerStep(viewModel, e.GetPosition(ruler).X)); // a click goes there
            }
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
            var newWidth = MainViewModel.ZoomedBarWidth(oldWidth, zoomIn: e.Delta > 0);
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

        if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            viewModel.CopySelectedNotes();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.X && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            viewModel.CutSelectedNotes();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            viewModel.PasteNotes();
            e.Handled = true;
            return;
        }

        // Ctrl+Z undoes; Ctrl+Y or Ctrl+Shift+Z redoes, as the Edit menu says.
        if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (viewModel.UndoCommand.CanExecute(null))
            {
                viewModel.UndoCommand.Execute(null);
            }
            e.Handled = true;
            return;
        }

        if ((e.Key == Key.Y && Keyboard.Modifiers == ModifierKeys.Control)
            || (e.Key == Key.Z && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift)))
        {
            if (viewModel.RedoCommand.CanExecute(null))
            {
                viewModel.RedoCommand.Execute(null);
            }
            e.Handled = true;
            return;
        }

        // Delete (Canc) removes the recorded block clicked on the timeline.
        if (e.Key == Key.J && Keyboard.Modifiers == ModifierKeys.Control)
        {
            viewModel.JoinSelectedBlocks();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Delete && Keyboard.Modifiers == ModifierKeys.None && viewModel.DeleteSelectedBlock())
        {
            e.Handled = true;
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
            OpenProject(viewModel);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.E && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            _ = ExportAudioAsync();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.B && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            ShowSoundBrowser();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.N && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            NewProject(viewModel);
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
        if (DataContext is MainViewModel viewModel
            && MayLeaveProject(viewModel, "Close project", "Do you want to save the project before closing it?"))
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

    // The tempo box is read-only until it is double-clicked, so it looks like a label.
    private void Tempo_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBox box)
        {
            return;
        }

        box.IsReadOnly = false;
        box.Focus();
        box.SelectAll();
        e.Handled = true;
    }

    // A click on the signature opens the window that chooses it.
    private void TimeSignature_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        var window = new TimeSignatureWindow(viewModel.Meter) { Owner = this };
        if (window.ShowDialog() == true)
        {
            viewModel.TimeSignature = window.Selected.ToString();
        }
    }

    // The mouse wheel over the signature adds or removes a beat per bar.
    private void TimeSignature_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        var meter = viewModel.Meter;
        var numerator = Math.Clamp(meter.Numerator + Math.Sign(e.Delta), Models.Meter.Numerators[0], Models.Meter.Numerators[^1]);
        viewModel.TimeSignature = new Models.Meter(numerator, meter.Denominator).ToString();
        e.Handled = true;
    }

    // The mouse wheel over the tempo steps it by 1 BPM, or 0.1 BPM with Shift.
    private void Tempo_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        var step = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift ? 0.1 : 1.0;
        viewModel.Tempo = Math.Round(viewModel.Tempo + (e.Delta > 0 ? step : -step), 1);
        e.Handled = true;
    }

    private void Tempo_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox box)
        {
            box.IsReadOnly = true;
        }
    }

    private void Tempo_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box || box.IsReadOnly)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            box.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            e.Handled = true;
        }
    }

    // Minimized, a piano roll goes to the dock with its picture, as the plug-ins do.
    private void OpenPianoRoll(MainViewModel viewModel, PatternTrackViewModel track)
    {
        var window = new PianoRollWindow(viewModel, track) { Owner = this };
        FollowPluginWindow(window);
        window.Show();
    }

    private void PianoRollMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel || viewModel.SelectedTrack is not { } track)
        {
            MessageBox.Show(this, "Select a track first.", "Piano roll", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        OpenPianoRoll(viewModel, track);
    }

    private void WaveformEditorMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ShowWaveformEditorWindow();
    }

    private void MixerMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ShowMixerWindow();
    }

    // File > Import audio: a WAV or MP3 file into a track, as dropping it on an empty track does.
    private void ImportAudioMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import audio",
            Filter = "Audio files (*.wav, *.mp3)|*.wav;*.mp3|WAV audio (*.wav)|*.wav|MP3 audio (*.mp3)|*.mp3",
        };
        if (dialog.ShowDialog(this) == true)
        {
            viewModel.ImportAudioFile(dialog.FileName);
        }
    }

    // File > Clean up the audio library: the clips kept in Sonator's library that nothing known plays go to the
    // Recycle Bin, once asked.
    private void CleanUpAudioLibraryMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        Mouse.OverrideCursor = Cursors.Wait;
        (IReadOnlyList<string> Files, long Bytes) unused;
        try
        {
            unused = viewModel.UnusedLibraryClips();
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }

        if (unused.Files.Count == 0)
        {
            SonatorMessageDialog.Show(this, "Clean up the audio library", "Every clip in the audio library is used by the song open now or a recent project: there is nothing to clean up.");
            return;
        }

        var size = unused.Bytes / (1024.0 * 1024.0);
        if (SonatorMessageDialog.Ask(this, "Clean up the audio library",
                $"{unused.Files.Count} audio clip(s), {size:0} MB, in Sonator's library are used neither by the song open now nor by the projects in File > Open recent. "
                + "Move them to the Recycle Bin? A project not in that list that still uses them would lose them, but they can be restored from the Recycle Bin.",
                "MOVE TO RECYCLE BIN", "CANCEL"))
        {
            viewModel.ThrowAwayLibraryClips(unused.Files);
        }
    }

    // File > Export audio: the song played through and written to a WAV file.
    private async void ExportAudioMenuItem_Click(object sender, RoutedEventArgs e) => await ExportAudioAsync();

    private async Task ExportAudioAsync()
    {
        if (DataContext is not MainViewModel viewModel || viewModel.IsExporting)
        {
            return;
        }

        var name = viewModel.CurrentProjectPath is { } project
            ? Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(project))
            : "Sonator song";
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export audio",
            Filter = "WAV audio, 24-bit (*.wav)|*.wav|MP3 audio, 320 kbps (*.mp3)|*.mp3",
            FileName = $"{name}.wav",
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        await viewModel.ExportAudioAsync(dialog.FileName);
    }

    // File > Export MIDI: the song's notes, controllers, tempo and markers as a standard MIDI file.
    private void ExportMidiMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        var name = viewModel.CurrentProjectPath is { } project
            ? Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(project))
            : "Sonator song";
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export MIDI",
            Filter = "MIDI file (*.mid)|*.mid",
            FileName = $"{name}.mid",
        };
        if (dialog.ShowDialog(this) == true)
        {
            viewModel.ExportMidi(dialog.FileName);
        }
    }

    // ---- Tempo changes and markers on the ruler ----

    // The step under the mouse on the ruler, snapped as the SNAP setting says.
    private int RulerStep(MouseEventArgs e, MainViewModel viewModel, FrameworkElement ruler) =>
        viewModel.SnapStep(Math.Max(0, (int)Math.Round(e.GetPosition(ruler).X / viewModel.TimelineSixteenthWidth)));

    // A right-click on the ruler adds a marker or a change of tempo where it was clicked.
    private void TimelineRuler_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel || sender is not FrameworkElement ruler)
        {
            return;
        }

        var step = RulerStep(e, viewModel, ruler);
        var menu = new ContextMenu { PlacementTarget = ruler };
        var marker = new MenuItem { Header = "Add marker here..." };
        marker.Click += (_, _) => AskMarkerName(viewModel, step, viewModel.NextMarkerName());
        var tempo = new MenuItem { Header = "Change tempo here..." };
        tempo.Click += (_, _) => AskTempo(viewModel, step, viewModel.CurrentTempoMap.TempoAt(step));
        menu.Items.Add(marker);
        menu.Items.Add(tempo);
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void AskMarkerName(MainViewModel viewModel, int step, string suggested)
    {
        var name = Microsoft.VisualBasic.Interaction.InputBox("Name of the marker:", "Marker", suggested);
        if (!string.IsNullOrWhiteSpace(name))
        {
            viewModel.SetMarker(step, name);
        }
    }

    private void AskTempo(MainViewModel viewModel, int step, double current)
    {
        var text = Microsoft.VisualBasic.Interaction.InputBox(
            "Tempo from here on, in BPM:", "Change of tempo", current.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture));
        if (double.TryParse(text.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var bpm))
        {
            viewModel.SetTempoChange(step, bpm);
        }
    }

    // A click on a flag moves the playhead there.
    private void RulerFlag_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainViewModel viewModel && sender is FrameworkElement { DataContext: RulerFlag flag })
        {
            viewModel.SeekToStep(flag.Step);
            e.Handled = true;
        }
    }

    // A right-click on a flag changes or removes it.
    private void RulerFlag_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel || sender is not FrameworkElement { DataContext: RulerFlag flag } element)
        {
            return;
        }

        var menu = new ContextMenu { PlacementTarget = element };
        var change = new MenuItem { Header = flag.IsTempo ? "Change the tempo..." : "Rename the marker..." };
        change.Click += (_, _) =>
        {
            if (flag.IsTempo)
            {
                AskTempo(viewModel, flag.Step, viewModel.CurrentTempoMap.TempoAt(flag.Step));
            }
            else
            {
                AskMarkerName(viewModel, flag.Step, flag.Label);
            }
        };
        var remove = new MenuItem { Header = flag.IsTempo ? "Remove the change of tempo" : "Remove the marker" };
        remove.Click += (_, _) =>
        {
            if (flag.IsTempo)
            {
                viewModel.RemoveTempoChange(flag.Step);
            }
            else
            {
                viewModel.RemoveMarker(flag.Step);
            }
        };
        menu.Items.Add(change);
        menu.Items.Add(remove);
        menu.IsOpen = true;
        e.Handled = true;
    }

    private SoundBrowserWindow? _soundBrowser;

    private void ShowSoundsButton_Click(object sender, RoutedEventArgs e) => ShowSoundBrowser();

    // One sound browser at a time; asked for again, it comes to the front, or goes to the dock when it is in front.
    private void ShowSoundBrowser()
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        if (_soundBrowser is null)
        {
            _soundBrowser = new SoundBrowserWindow(viewModel); // no owner: see ShowMixerWindow
            _soundBrowser.Closed += (_, _) => _soundBrowser = null;
            FollowPluginWindow(_soundBrowser); // minimized, it goes to the dock with its picture
            RememberDeactivation(_soundBrowser);
            _soundBrowser.Show();
        }
        else if (DockIfInFront(_soundBrowser))
        {
            return;
        }

        BringBack(_soundBrowser);
    }

    // MIXER and SOUNDS put their window away in the dock when it was the window in use, as the minimize button
    // does, and bring it back otherwise. The click on the button has just made this window the one in use, so the
    // other counts as in use if it was until a moment ago. True when it went to the dock.
    private bool DockIfInFront(Window window)
    {
        var wasInUse = window.IsActive
            || (_toolDeactivated.TryGetValue(window, out var when) && DateTime.UtcNow - when < TimeSpan.FromMilliseconds(600));
        if (!window.IsVisible || window.WindowState == WindowState.Minimized || !wasInUse)
        {
            return false;
        }

        DockPluginWindow(window);
        return true;
    }

    // When the mixer and the sound browser last stopped being the window in use.
    private readonly Dictionary<Window, DateTime> _toolDeactivated = new();

    private void RememberDeactivation(Window window)
    {
        window.Deactivated += (_, _) => _toolDeactivated[window] = DateTime.UtcNow;
        window.Closed += (_, _) => _toolDeactivated.Remove(window);
    }

    private CromatorWindow? _cromator;

    // Cromator, the score of the selected track: one window, which SCORE brings back or puts in the dock, as MIXER.
    private void ShowScoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        if (_cromator is null)
        {
            _cromator = new CromatorWindow(viewModel); // no owner: see ShowMixerWindow
            _cromator.Closed += (_, _) => _cromator = null;
            FollowPluginWindow(_cromator);
            RememberDeactivation(_cromator);
            _cromator.Show();
            return;
        }

        if (!DockIfInFront(_cromator))
        {
            _cromator.ShowTrack(viewModel.SelectedTrack);
            BringBack(_cromator);
        }
    }

    private void ShowMixerButton_Click(object sender, RoutedEventArgs e)
    {
        ShowMixerWindow();
    }

    private void ShowWaveformButton_Click(object sender, RoutedEventArgs e)
    {
        ShowWaveformEditorWindow();
    }

    private void ShowSongButton_Click(object sender, RoutedEventArgs e)
    {
        // On and off, as the ARRANGE box: the whole song, or the selected pattern.
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.IsArrangeMode = !viewModel.IsArrangeMode;
        }
        Activate();
    }

    // One mixer and one waveform editor at a time: a second click brings the open window forward (the mixer, when
    // it is in front already, goes to the dock).
    private void ShowMixerWindow()
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        if (_mixerWindow is null)
        {
            // No owner, so a click on the main window brings it in front of the mixer, to work on the timeline
            // with the mixer open; MIXER brings the mixer back. It closes with the main window.
            _mixerWindow = new MixerWindow(viewModel);
            _mixerWindow.Closed += (_, _) => _mixerWindow = null;
            FollowPluginWindow(_mixerWindow); // minimized, it goes to the dock with the plug-ins
            RememberDeactivation(_mixerWindow);
            _mixerWindow.Show();
            return;
        }

        if (!DockIfInFront(_mixerWindow))
        {
            BringBack(_mixerWindow);
        }
    }

    // Track > Detect tempo, or the clip's right-click: finds the clip's tempo (BPM detection) and, if asked,
    // sets the song's tempo to it with the clip lined up on the beats. For a clip's right-click, its track.
    private void DetectTempoMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        var track = (sender as FrameworkElement)?.DataContext is ArrangeTimelineTrackViewModel timeline
            ? timeline.SourceTrack
            : viewModel.SelectedTrack;
        if (track is null || string.IsNullOrEmpty(track.AudioClipPath))
        {
            SonatorMessageDialog.Show(this, "Detect tempo", "Select a track with an audio clip first: the tempo is found from its clip.");
            return;
        }

        viewModel.SelectedTrack = track;
        viewModel.StatusMessage = $"Finding the tempo of {track.Name}'s clip...";
        Mouse.OverrideCursor = Cursors.Wait;
        DetectedTempo? found;
        try
        {
            found = viewModel.DetectClipTempo(track);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }

        if (found is null)
        {
            viewModel.StatusMessage = "Detect tempo: no steady beat found";
            SonatorMessageDialog.Show(this, "Detect tempo", "No steady beat was found in the clip, so its tempo cannot be told.");
            return;
        }

        // Half or twice the tempo is often as good a reading: both are offered.
        var other = found.Bpm >= 120 ? found.Bpm / 2 : found.Bpm * 2;
        var sure = found.Confidence >= 0.5 ? "clearly" : "probably";
        var choice = SonatorMessageDialog.AskChoice(this, "Detect tempo",
            $"{track.Name}'s clip is {sure} at {found.Bpm:0.##} BPM, its first beat at {found.FirstBeatSeconds:0.00} s. "
            + $"Set the song to it? The metronome and the grid will follow the clip, lined up on its beats. "
            + $"If it counts in halves or doubles, choose {other:0.##} BPM.",
            $"{found.Bpm:0.##} BPM", $"{other:0.##} BPM");
        switch (choice)
        {
            case SaveChoice.Save:
                viewModel.SyncSongToClip(track, found.Bpm, found.FirstBeatSeconds);
                break;
            case SaveChoice.DontSave:
                viewModel.SyncSongToClip(track, other, found.FirstBeatSeconds);
                break;
            default:
                viewModel.StatusMessage = $"{track.Name}'s clip is at about {found.Bpm:0.##} BPM; the song's tempo is left as it was";
                break;
        }
    }

    // Sondyne, for the take of the clip right-clicked or of the selected track: one window a track.
    private readonly Dictionary<PatternTrackViewModel, SondyneWindow> _sondynes = new();

    private void SondyneMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        var track = (sender as FrameworkElement)?.DataContext is ArrangeTimelineTrackViewModel timeline
            ? timeline.SourceTrack
            : viewModel.SelectedTrack;
        if (track?.AudioClipPath is not { } path || !File.Exists(path))
        {
            SonatorMessageDialog.Show(this, "Sondyne", "Select an audio track with a take first: Sondyne corrects the pitch of a sung or played take.");
            return;
        }

        if (_sondynes.TryGetValue(track, out var open))
        {
            BringBack(open);
            return;
        }

        var window = new SondyneWindow(viewModel, track); // no owner: see ShowMixerWindow
        _sondynes[track] = window;
        window.Closed += (_, _) => _sondynes.Remove(track);
        FollowPluginWindow(window);
        window.Show();
    }

    private void EditClipMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ArrangeTimelineTrackViewModel timeline)
        {
            OpenClipInWaveformEditor(timeline.SourceTrack);
        }
    }

    // Opens a track's audio clip in the waveform editor; its edits can then go back into the same track.
    private void OpenClipInWaveformEditor(PatternTrackViewModel track)
    {
        if (track.AudioClipPath is not { } path || !File.Exists(path))
        {
            return;
        }

        ShowWaveformEditorWindow();
        _waveformEditorWindow?.OpenTrackClip(track, path);
    }

    private void ShowWaveformEditorWindow()
    {
        if (_waveformEditorWindow is null)
        {
            _waveformEditorWindow = new WaveformEditorWindow(null, DataContext is MainViewModel viewModel ? viewModel.Tempo : 120, DataContext as MainViewModel) { Owner = this };
            _waveformEditorWindow.Closed += (_, _) => _waveformEditorWindow = null;
            FollowPluginWindow(_waveformEditorWindow); // minimized, it goes to the dock with its picture
            _waveformEditorWindow.Show();
            return;
        }

        BringBack(_waveformEditorWindow);
    }

    // Audio files dropped on an empty track go straight into it.
    // A track takes a plug-in dropped on it, or an audio file dropped on it when the track is empty.
    private void TimelineBodyListBox_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = GetDroppedPluginTrack(e) is not null || GetDroppedSonEffectTrack(e) is not null
            || (GetDroppedAudioTrack(e) is { } track && MainViewModel.IsEmptyTrack(track))
                ? DragDropEffects.Copy
                : DragDropEffects.None;
        e.Handled = true;
    }

    private void TimelineBodyListBox_Drop(object sender, DragEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        if (GetDroppedSonEffectTrack(e) is { } effectTrack && e.Data.GetData(SonEffectDragFormat) is EffectDefinition dropped)
        {
            AddSonEffect(viewModel, effectTrack, dropped);
            e.Handled = true;
            return;
        }

        if (GetDroppedPluginTrack(e) is { } pluginTrack && e.Data.GetData(VstPluginDragFormat) is string pluginPath)
        {
            // The track dropped on takes the plug-in, and becomes the selected one, whichever was selected before.
            viewModel.SelectedTrack = pluginTrack;
            viewModel.AssignPlugin(pluginTrack, pluginPath);
            OpenPluginWindowFor(viewModel, pluginTrack, pluginPath);
            e.Handled = true;
            return;
        }

        if (GetDroppedAudioTrack(e) is { } track && GetDroppedAudioFile(e) is { } filePath)
        {
            viewModel.AssignAudioFile(track, filePath);
            e.Handled = true;
        }
    }

    private static PatternTrackViewModel? GetDroppedPluginTrack(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(VstPluginDragFormat))
        {
            return null;
        }

        var item = FindVisualAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        return item?.DataContext is ArrangeTimelineTrackViewModel { IsKitPiece: false, SourceTrack.SupportsMidiPlayback: true } timelineTrack
            ? timelineTrack.SourceTrack
            : null;
    }

    private static string? GetDroppedAudioFile(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)
            || e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: 1 } files)
        {
            return null;
        }

        return AudioFileExtensions.Contains(Path.GetExtension(files[0]), StringComparer.OrdinalIgnoreCase)
            ? files[0]
            : null;
    }

    private static PatternTrackViewModel? GetDroppedAudioTrack(DragEventArgs e)
    {
        if (GetDroppedAudioFile(e) is null)
        {
            return null;
        }

        var item = FindVisualAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        return item?.DataContext is ArrangeTimelineTrackViewModel { IsKitPiece: false } timelineTrack ? timelineTrack.SourceTrack : null;
    }

    // ---- Sonator's own effects in the plug-in list ----

    private const string SonEffectDragFormat = "Sonator.SonEffect";

    // A tile of the list: the effect, and its module as it looks in the rack as its picture.
    private sealed record SonEffectTile(EffectDefinition Definition, ImageSource Picture)
    {
        public string Name => Definition.Name;

        public string Description => $"{Definition.Description}: click to put it on the selected track, or drag it onto a track";
    }

    private void FillSonEffectList(MainViewModel viewModel)
    {
        SonEffectList.ItemsSource = EffectCatalog.All.Select(definition => new SonEffectTile(definition, PictureOf(viewModel, definition))).ToList();
    }

    // The effect's module as it looks in the rack, at its defaults (a big choir for the choir; a note sung and a level
    // for those that show them).
    private static ImageSource PictureOf(MainViewModel viewModel, EffectDefinition definition)
    {
        var values = definition.Kind == EffectKind.Choir ? FactoryPresets.For(definition.Kind).FirstOrDefault(preset => preset.Name == "Big Choir")?.Values : null;
        var effect = new TrackEffectViewModel(definition, values: values)
        {
            MeterValue = definition.Kind switch { EffectKind.Harmonizer => 57, EffectKind.Mastering => -14, _ => 0 },
        };
        return EffectRackWindow.PictureOfModule(viewModel, effect);
    }

    private Point? _sonEffectPress;

    private void SonEffect_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _sonEffectPress = e.GetPosition((IInputElement)sender);
        e.Handled = true;
    }

    private void SonEffect_MouseMove(object sender, MouseEventArgs e)
    {
        if (_sonEffectPress is not { } press || e.LeftButton != MouseButtonState.Pressed
            || sender is not FrameworkElement { DataContext: SonEffectTile tile } element)
        {
            return;
        }

        var offset = e.GetPosition(element) - press;
        if (Math.Abs(offset.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(offset.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _sonEffectPress = null;
        DragDrop.DoDragDrop(element, new DataObject(SonEffectDragFormat, tile.Definition), DragDropEffects.Copy);
        e.Handled = true;
    }

    // A click puts the effect on the selected track and opens its rack there.
    private void SonEffect_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var wasClick = _sonEffectPress is not null;
        _sonEffectPress = null;
        if (!wasClick || sender is not FrameworkElement { DataContext: SonEffectTile tile } || DataContext is not MainViewModel viewModel)
        {
            return;
        }

        if (viewModel.SelectedTrack is not { } track)
        {
            SonatorMessageDialog.Show(this, tile.Name, "Select a track first: the effect goes on the selected track.");
            return;
        }

        AddSonEffect(viewModel, track, tile.Definition);
        e.Handled = true;
    }

    private void AddSonEffect(MainViewModel viewModel, PatternTrackViewModel track, EffectDefinition definition)
    {
        viewModel.SelectedTrack = track;
        if (viewModel.AddEffect(track, definition) is { } effect)
        {
            OpenFxRack(viewModel, track, effect);
        }
    }

    private static PatternTrackViewModel? GetDroppedSonEffectTrack(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(SonEffectDragFormat))
        {
            return null;
        }

        var item = FindVisualAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        return item?.DataContext is ArrangeTimelineTrackViewModel { IsKitPiece: false } lane ? lane.SourceTrack : null;
    }

    // KIT on a drum track shows its kit's pieces under it on the timeline, or hides them.
    private void TimelineKit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ArrangeTimelineTrackViewModel lane } && DataContext is MainViewModel viewModel)
        {
            viewModel.ToggleKitOnTimeline(lane.SourceTrack);
        }
    }

    private static T? FindVisualAncestor<T>(DependencyObject? element)
        where T : DependencyObject
    {
        while (element is not null && element is not T)
        {
            element = VisualTreeHelper.GetParent(element);
        }

        return element as T;
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

    // Double-clicking the plug-in name under a track opens that plug-in's window.
    private void TrackPluginName_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2
            || DataContext is not MainViewModel viewModel
            || sender is not FrameworkElement { DataContext: ArrangeTimelineTrackViewModel timelineTrack }
            || timelineTrack.SourceTrack.PluginFilePath is not { } pluginPath)
        {
            return;
        }

        OpenPluginWindowFor(viewModel, timelineTrack.SourceTrack, pluginPath);
        e.Handled = true;
    }

    private void TrackList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // The plug-in name has its own double-click.
        if (DataContext is not MainViewModel viewModel
            || IsInsideTagged(e.OriginalSource, "PluginName")
            || IsInsideTagged(e.OriginalSource, "InstrumentIcon")
            || IsInsideTagged(e.OriginalSource, "TrackName")
            || IsInsideTagged(e.OriginalSource, "VolumeBar"))
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

        OpenPianoRoll(viewModel, viewModel.SelectedTrack);
    }

    // A press starts a drag; a release without a drag opens the plug-in editor, as a click always did.
    private void VstPluginThumbnail_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not VstPluginViewModel plugin)
        {
            return;
        }

        _vstPluginDragStart = e.GetPosition(element);
        _vstPluginDragPath = plugin.FilePath;
        e.Handled = true;
    }

    private void VstPluginThumbnail_MouseMove(object sender, MouseEventArgs e)
    {
        if (_vstPluginDragStart is not { } start
            || _vstPluginDragPath is null
            || e.LeftButton != MouseButtonState.Pressed
            || sender is not FrameworkElement element)
        {
            return;
        }

        var offset = e.GetPosition(element) - start;
        if (Math.Abs(offset.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(offset.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var path = _vstPluginDragPath;
        _vstPluginDragStart = null;
        _vstPluginDragPath = null;
        var data = new DataObject(VstPluginDragFormat, path);
        DragDrop.DoDragDrop(element, data, DragDropEffects.Copy);
        e.Handled = true;
    }

    private void VstPluginThumbnail_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var wasClick = _vstPluginDragStart is not null;
        _vstPluginDragStart = null;
        _vstPluginDragPath = null;
        if (!wasClick || sender is not FrameworkElement element || element.DataContext is not VstPluginViewModel plugin)
        {
            return;
        }

        OpenPluginWindow(plugin.Name, plugin.FilePath, plugin.Kind);
        e.Handled = true;
    }

    // ---- Arranging the plug-in list by dragging ----

    private static readonly System.Windows.Media.Brush DropLineBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE6, 0xC4, 0x5B));

    // The plug-in dragged, read from the drag (a plug-in file) among the listed ones.
    private VstPluginViewModel? DraggedPlugin(DragEventArgs e) =>
        e.Data.GetData(VstPluginDragFormat) is string path && DataContext is MainViewModel viewModel
            ? viewModel.VstPlugins.FirstOrDefault(plugin => string.Equals(plugin.FilePath, path, StringComparison.OrdinalIgnoreCase))
            : null;

    // Dropped on the left half of another plug-in it goes before it, on the right half after it, as the
    // gold line on that side shows.
    private static bool DropsAfter(DragEventArgs e, FrameworkElement target) => e.GetPosition(target).X > target.ActualWidth / 2;

    private void VstPluginThumbnail_DragOver(object sender, DragEventArgs e)
    {
        if (sender is not Border { DataContext: VstPluginViewModel target } border || DraggedPlugin(e) is not { } dragged
            || ReferenceEquals(dragged, target))
        {
            return;
        }

        border.BorderBrush = DropLineBrush;
        border.BorderThickness = DropsAfter(e, border) ? new Thickness(1, 1, 4, 1) : new Thickness(4, 1, 1, 1);
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void VstPluginThumbnail_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is Border border)
        {
            border.BorderBrush = (System.Windows.Media.Brush)FindResource("Line");
            border.BorderThickness = new Thickness(1);
        }
    }

    private void VstPluginThumbnail_Drop(object sender, DragEventArgs e)
    {
        if (sender is not Border { DataContext: VstPluginViewModel target } border || DataContext is not MainViewModel viewModel
            || DraggedPlugin(e) is not { } dragged)
        {
            return;
        }

        VstPluginThumbnail_DragLeave(border, e);
        if (!ReferenceEquals(dragged, target))
        {
            var index = viewModel.VstPlugins.IndexOf(target) + (DropsAfter(e, border) ? 1 : 0);
            var before = viewModel.VstPlugins.Where(plugin => !ReferenceEquals(plugin, dragged)).ElementAtOrDefault(
                index > viewModel.VstPlugins.IndexOf(dragged) ? index - 1 : index);
            viewModel.MoveVstPlugin(dragged, before);
        }
        e.Handled = true;
    }

    // Opens the plug-in's own window: the native GUI for VST3, otherwise the parameter dialog.
    private void OpenPluginWindow(string name, string filePath, string kind)
    {
        var window = CreatePluginWindow(name, filePath, kind);
        window.Owner = this;
        FollowPluginWindow(window);
        window.Show();
        CascadePluginWindow(window);
    }

    private static Window CreatePluginWindow(string name, string filePath, string kind)
    {
        return string.Equals(kind, "VST3", StringComparison.OrdinalIgnoreCase)
            ? new Vst3NativeEditorWindow(name, filePath)
            : new VstPluginEditorDialog(name, filePath, kind);
    }

    // Opens the window of the plug-in the selected track plays through.
    private void PluginWindowButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel || viewModel.SelectedTrack?.PluginFilePath is not { Length: > 0 } filePath)
        {
            MessageBox.Show(this, "Choose a plug-in for the selected track first.", "Plug-in", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        OpenPluginWindowFor(viewModel, viewModel.SelectedTrack!, filePath);
    }

    // A track has one plug-in, so its previous plug-in window closes when another plug-in takes its place.
    private void OpenPluginWindowFor(MainViewModel viewModel, PatternTrackViewModel track, string filePath)
    {
        // The track's window is still open, maybe minimized: it comes back as it was.
        if (_trackPluginWindows.TryGetValue(track, out var open) && open.Tag is string openPath
            && string.Equals(openPath, filePath, StringComparison.OrdinalIgnoreCase))
        {
            ShowPluginWindow(open);
            return;
        }

        var window = PrepareTrackPluginWindow(viewModel, track, filePath, playFromKeyboard: true);
        window.Show();
        CascadePluginWindow(window);
    }

    // Opens the window of a VST3 effect in a rack, or brings it back when it is open or in the dock.
    internal void OpenEffectPluginWindow(PatternTrackViewModel track, TrackEffectViewModel effect)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        if (viewModel.EffectPluginModule(effect) is not { } module)
        {
            SonatorMessageDialog.Show(this, effect.Name, $"{effect.Name} cannot be loaded on this computer:\n{effect.PluginPath}");
            return;
        }

        if (OwnedWindows.OfType<Vst3NativeEditorWindow>().FirstOrDefault(window => ReferenceEquals(window.ModuleHandle, module)) is { } open)
        {
            ShowPluginWindow(open);
            return;
        }

        var window = new Vst3NativeEditorWindow(viewModel.PluginHost, module, $"{effect.Name} — {track.Name}") { Owner = this };
        FollowPluginWindow(window);
        window.Show();
        CascadePluginWindow(window);
    }

    // The VST3 effects for an add menu, under one item; the plug-in check finds out which plug-ins are effects.
    internal static MenuItem VstEffectsMenu(MainViewModel viewModel, Action<VstPluginViewModel> add)
    {
        var menu = new MenuItem { Header = "VST effects" };
        foreach (var plugin in viewModel.EffectPlugins)
        {
            var item = new MenuItem { Header = plugin.Name, ToolTip = plugin.FilePath };
            item.Click += (_, _) => add(plugin);
            menu.Items.Add(item);
        }

        if (menu.Items.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "No VST effects found yet", IsEnabled = false });
        }

        return menu;
    }

    // A plug-in of a project just opened goes to the dock, ready: its window opens from there.
    private void DockSavedInstrument(PatternTrackViewModel track)
    {
        if (DataContext is not MainViewModel viewModel || track.PluginFilePath is not { } filePath
            || _trackPluginWindows.ContainsKey(track))
        {
            return;
        }

        var window = PrepareTrackPluginWindow(viewModel, track, filePath, playFromKeyboard: false);
        PluginDockItems.ItemsSource ??= _dockedPluginWindows;
        _dockedPluginWindows.Add(new DockedPluginWindow(window, Preview: null)); // pictured once it has been open
        window.Closed += (_, _) => UndockPluginWindow(window);
        PluginDock.Visibility = Visibility.Visible;
    }

    // Makes the track's plug-in window, not shown yet, in place of any it had. playFromKeyboard: the keyboard
    // then plays this plug-in, as when its window is opened on purpose.
    private Window PrepareTrackPluginWindow(MainViewModel viewModel, PatternTrackViewModel track, string filePath, bool playFromKeyboard)
    {
        if (_trackPluginWindows.Remove(track, out var previous))
        {
            previous.Close();
        }

        var plugin = viewModel.VstPlugins.FirstOrDefault(item => string.Equals(item.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        var kind = plugin?.Kind ?? (string.Equals(Path.GetExtension(filePath), ".vst3", StringComparison.OrdinalIgnoreCase) ? "VST3" : "VST2");
        var name = plugin?.Name ?? Path.GetFileNameWithoutExtension(filePath);

        // A VST3 track's window shows the plug-in the track plays through, so a sound chosen there is the
        // track's sound, and closing the window leaves it playing.
        Window window;
        if (string.Equals(kind, "VST3", StringComparison.OrdinalIgnoreCase)
            && string.Equals(track.PluginFilePath, filePath, StringComparison.OrdinalIgnoreCase)
            && viewModel.EnsureTrackInstrument(track) is { } instrument)
        {
            if (playFromKeyboard)
            {
                LiveInstrumentRegistry.Set(instrument);
            }
            window = new Vst3NativeEditorWindow(viewModel.PluginHost, instrument, $"{name} — {track.Name}");
        }
        else
        {
            window = CreatePluginWindow(name, filePath, kind);
        }

        window.Closed += (_, _) =>
        {
            if (_trackPluginWindows.TryGetValue(track, out var open) && ReferenceEquals(open, window))
            {
                _trackPluginWindows.Remove(track);
            }
        };
        window.Tag = filePath;
        _trackPluginWindows[track] = window;
        window.Owner = this;
        FollowPluginWindow(window);
        return window;
    }

    // A plug-in window put away in the dock. The dock lists these, not the windows: an ItemsControl given a
    // window would try to place the window itself inside the dock.
    // Preview is a small picture of the window as it was when it went to the dock, shown over its button.
    private sealed record DockedPluginWindow(Window Window, ImageSource? Preview)
    {
        public string Title => Window.Title;
    }

    // The dock's pictures of the plug-ins, in pixels across, and the last good picture of each plug-in window.
    private const int PreviewWidth = 450;
    private readonly Dictionary<Window, System.Windows.Media.Imaging.BitmapSource> _lastPluginPictures = new();

    // The plug-in windows put away in the dock at the bottom, in the order they went there.
    private readonly System.Collections.ObjectModel.ObservableCollection<DockedPluginWindow> _dockedPluginWindows = new();

    // A click on the timeline puts the open plug-in windows away in the dock, each a button with its
    // instrument's name, so the timeline is in view. They come back from the dock or with a double-click on
    // the track's plug-in name.
    private void TimelineBodyListBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // With the right button held, each left click lays one more copy of the selected block after its end.
        if (e.RightButton == MouseButtonState.Pressed && _repeatBlock is { } block && DataContext is MainViewModel model)
        {
            model.RepeatBlock(block, first: !_repeatedBlock);
            _repeatedBlock = true;
            e.Handled = true;
            return;
        }

        foreach (var window in OwnedWindows.OfType<Window>().Where(IsPluginWindow).ToList())
        {
            DockPluginWindow(window);
        }

        StartBlockRectangle(e);
    }

    // ---- A rectangle drawn on the lanes selects the blocks it touches (with Ctrl, adds them) ----

    private Point? _rectangleStart;
    private BlockRectangleAdorner? _rectangle;

    private static T? AncestorOf<T>(object source) where T : DependencyObject
    {
        for (var element = source as DependencyObject; element is not null; element = element is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element))
        {
            if (element is T found)
            {
                return found;
            }
        }

        return null;
    }

    // True for a press on a lane past its header, where the left button draws a rectangle.
    private static bool IsOnLaneArea(MouseButtonEventArgs e) =>
        AncestorOf<ListBoxItem>(e.OriginalSource) is { DataContext: ArrangeTimelineTrackViewModel } item
        && e.GetPosition(item).X >= MainViewModel.TimelineTrackHeaderWidth;

    // A press on the empty part of a lane, past its header and off its blocks and clips, selects the lane's track
    // and starts a rectangle. A double-click there still opens the piano roll.
    private void StartBlockRectangle(MouseButtonEventArgs e)
    {
        if (e.ClickCount != 1 || DataContext is not MainViewModel viewModel || !IsOnLaneArea(e)
            || AncestorOf<ListBoxItem>(e.OriginalSource) is not { DataContext: ArrangeTimelineTrackViewModel lane }
            || IsInsideTagged(e.OriginalSource, "NoteBlock") || IsInsideTagged(e.OriginalSource, "AudioClip")
            || IsInsideTagged(e.OriginalSource, "AutomationLane"))
        {
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control)
        {
            viewModel.SelectedTrack = lane.SourceTrack;
            viewModel.SelectBlocks(Array.Empty<(PatternTrackViewModel, NoteBlock)>());
        }

        _rectangleStart = e.GetPosition(TimelineBodyListBox);
        TimelineBodyListBox.CaptureMouse();
        e.Handled = true;
    }

    private void TimelineBodyListBox_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_rectangleStart is not { } start || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var now = e.GetPosition(TimelineBodyListBox);
        if (_rectangle is null && (Math.Abs(now.X - start.X) > 4 || Math.Abs(now.Y - start.Y) > 4)
            && AdornerLayer.GetAdornerLayer(TimelineBodyListBox) is { } layer)
        {
            _rectangle = new BlockRectangleAdorner(TimelineBodyListBox);
            layer.Add(_rectangle);
        }

        _rectangle?.Show(new Rect(start, now));
        e.Handled = true;
    }

    private void TimelineBodyListBox_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_rectangleStart is not { } start)
        {
            return;
        }

        _rectangleStart = null;
        TimelineBodyListBox.ReleaseMouseCapture();
        if (_rectangle is not null)
        {
            AdornerLayer.GetAdornerLayer(TimelineBodyListBox)?.Remove(_rectangle);
            _rectangle = null;
            if (DataContext is MainViewModel viewModel)
            {
                var area = new Rect(start, e.GetPosition(TimelineBodyListBox));
                viewModel.SelectBlocks(
                    BlockElements()
                        .Where(element => element.TransformToAncestor(TimelineBodyListBox).TransformBounds(new Rect(element.RenderSize)).IntersectsWith(area))
                        .Select(element => (TimelineBlockViewModel)element.DataContext)
                        .Select(block => (block.Track, block.Block)),
                    add: (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control);
            }
        }

        e.Handled = true;
    }

    // The blocks drawn on the lanes.
    private IEnumerable<FrameworkElement> BlockElements()
    {
        var pending = new Stack<DependencyObject>();
        pending.Push(TimelineBodyListBox);
        while (pending.Count > 0)
        {
            var element = pending.Pop();
            if (element is FrameworkElement { Tag: "NoteBlock", DataContext: TimelineBlockViewModel, IsVisible: true } block)
            {
                yield return block;
                continue;
            }

            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
            {
                pending.Push(VisualTreeHelper.GetChild(element, i));
            }
        }
    }

    // The blocks' menu: Join is there when two blocks or more of a track are selected.
    private void NoteBlock_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is FrameworkElement { ContextMenu: { } menu } && DataContext is MainViewModel viewModel)
        {
            foreach (var item in menu.Items.OfType<MenuItem>().Where(item => item.Tag is "Join"))
            {
                item.IsEnabled = viewModel.CanJoinSelectedBlocks;
            }
        }
    }

    private void JoinBlocksMenuItem_Click(object sender, RoutedEventArgs e) => (DataContext as MainViewModel)?.JoinSelectedBlocks();

    private void CopyBlocksMenuItem_Click(object sender, RoutedEventArgs e) => (DataContext as MainViewModel)?.CopySelectedNotes();

    private void PasteBlocksMenuItem_Click(object sender, RoutedEventArgs e) => (DataContext as MainViewModel)?.PasteNotes();

    private void DeleteBlocksMenuItem_Click(object sender, RoutedEventArgs e) => (DataContext as MainViewModel)?.DeleteSelectedBlock();

    // The block selected when the right button went down, copied over and over by left clicks while it is held.
    private PatternTrackViewModel? _repeatBlock;
    private bool _repeatedBlock;

    // The right button keeps the blocks selected, so their menu (Join) acts on them: a block right-clicked
    // outside the selection becomes the one selected.
    private void TimelineBodyListBox_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        _repeatBlock = viewModel.SelectedBlockTrack;
        _repeatedBlock = false;
        if (AncestorOf<FrameworkElement>(e.OriginalSource) is { } source
            && FindTagged(source, "NoteBlock") is { DataContext: TimelineBlockViewModel { IsSelected: false } block })
        {
            viewModel.SelectBlock(block.Track, block.Block);
            _repeatBlock = block.Track;
        }

        if (viewModel.SelectedBlocks.Count > 0)
        {
            e.Handled = true; // the lane's track stays as it is, and with it the blocks selected
        }
    }

    private static FrameworkElement? FindTagged(DependencyObject source, string tag)
    {
        for (var element = source; element is not null; element = VisualTreeHelper.GetParent(element))
        {
            if (element is FrameworkElement { Tag: string elementTag } found && elementTag == tag)
            {
                return found;
            }
        }

        return null;
    }

    // After copies were laid, letting go of the right button opens no menu.
    private void TimelineBodyListBox_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = _repeatedBlock;
        _repeatBlock = null;
        _repeatedBlock = false;
    }

    private static bool IsPluginWindow(Window window) => window is Vst3NativeEditorWindow or VstPluginEditorDialog;

    // Plug-in windows minimized with their own button go to the dock as well: minimized, Windows shows them as
    // small bars with no room for their name. So do the mixer and the FX racks.
    private void FollowPluginWindow(Window window)
    {
        // Pictured while it is in view, every few seconds and each time it loses the focus, so the dock has a
        // good picture even when none can be taken as the window goes there (a plug-in drawn by the graphics
        // card can only be copied from the screen, and while minimizing the screen shows what is behind).
        void Picture()
        {
            if (window.IsVisible && window.WindowState == WindowState.Normal
                && WindowSnapshot.Take(window, PreviewWidth) is { } picture && !WindowSnapshot.IsBlank(picture))
            {
                _lastPluginPictures[window] = picture;
            }
        }

        var pictureTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        pictureTimer.Tick += (_, _) => Picture();
        pictureTimer.Start();
        window.Deactivated += (_, _) => Picture();
        window.Closed += (_, _) =>
        {
            pictureTimer.Stop();
            _lastPluginPictures.Remove(window);
        };
        var shownState = window.WindowState;
        window.StateChanged += (_, _) =>
        {
            if (window.WindowState == WindowState.Minimized)
            {
                window.WindowState = shownState; // back from the dock as it was, maximized too
                DockPluginWindow(window);
            }
            else
            {
                shownState = window.WindowState;
            }
        };
    }

    private void DockPluginWindow(Window window)
    {
        if (!window.IsVisible || _dockedPluginWindows.Any(docked => ReferenceEquals(docked.Window, window)))
        {
            return;
        }

        PluginDockItems.ItemsSource ??= _dockedPluginWindows;
        // The last picture taken while it was in view (at most two seconds old) shows it best: as it goes to
        // the dock, the screen there already shows what is behind it.
        var preview = _lastPluginPictures.GetValueOrDefault(window);
        if (preview is null)
        {
            var taken = WindowSnapshot.Take(window, PreviewWidth);
            preview = taken is not null && !WindowSnapshot.IsBlank(taken) ? taken : null;
        }
        window.Hide();
        _dockedPluginWindows.Add(new DockedPluginWindow(window, preview));
        window.Closed += (_, _) => UndockPluginWindow(window);
        PluginDock.Visibility = Visibility.Visible;
    }

    private void UndockPluginWindow(Window window)
    {
        if (DockPreview.DataContext is DockedPluginWindow { Window: var shown } && ReferenceEquals(shown, window))
        {
            DockPreview.IsOpen = false;
        }

        foreach (var docked in _dockedPluginWindows.Where(docked => ReferenceEquals(docked.Window, window)).ToList())
        {
            _dockedPluginWindows.Remove(docked);
        }
        if (_dockedPluginWindows.Count == 0)
        {
            PluginDock.Visibility = Visibility.Collapsed;
        }
    }

    // Closes the windows, open or in the dock, that show a track's plug-in about to be unloaded.
    private void ClosePluginWindowsOf(Vst3ModuleHandle instrument)
    {
        foreach (var window in OwnedWindows.OfType<Vst3NativeEditorWindow>()
                     .Where(window => ReferenceEquals(window.ModuleHandle, instrument))
                     .ToList())
        {
            window.Close(); // its Closed handlers take it out of the dock and the track's window list
        }
    }

    // Brings a plug-in window back from the dock, in front.
    private void ShowPluginWindow(Window window)
    {
        UndockPluginWindow(window);
        window.Show();
        CascadePluginWindow(window);
        window.Activate();
    }

    // A plug-in window opened right over another one moves down and to the right, step by step, so each
    // window opened shows, as Windows cascades its windows.
    private void CascadePluginWindow(Window window)
    {
        const double step = 32;
        var others = OwnedWindows.OfType<Window>()
            .Where(other => !ReferenceEquals(other, window) && other.IsVisible && IsPluginWindow(other))
            .ToList();
        var workArea = SystemParameters.WorkArea;
        var left = window.Left;
        var top = window.Top;
        for (var tries = 0; tries < 20 && others.Any(other => Math.Abs(other.Left - left) < step / 2 && Math.Abs(other.Top - top) < step / 2); tries++)
        {
            left += step;
            top += step;
            if (left + window.ActualWidth > workArea.Right || top + window.ActualHeight > workArea.Bottom)
            {
                // Off the screen: the cascade starts again from the top left of the work area.
                left = workArea.Left + step * (tries % 4);
                top = workArea.Top + step * (tries % 4);
            }
        }

        window.Left = left;
        window.Top = top;
    }

    private void PluginDockButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DockedPluginWindow docked })
        {
            DockPreview.IsOpen = false;
            ShowPluginWindow(docked.Window);
        }
    }

    // ---- The docked window's picture, over its button: it stays while the mouse is on either ----

    private System.Windows.Threading.DispatcherTimer? _dockPreviewClose;

    private void PluginDockButton_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: DockedPluginWindow docked } button)
        {
            return;
        }

        _dockPreviewClose?.Stop();
        DockPreview.DataContext = docked;
        DockPreview.PlacementTarget = button;
        DockPreview.IsOpen = true;
    }

    private void DockPreview_MouseEnter(object sender, MouseEventArgs e) => _dockPreviewClose?.Stop();

    // Leaving the button or the picture closes it a moment later, unless the mouse has gone onto the other.
    private void PluginDock_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_dockPreviewClose is null)
        {
            _dockPreviewClose = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _dockPreviewClose.Tick += (_, _) =>
            {
                _dockPreviewClose.Stop();
                DockPreview.IsOpen = false;
            };
        }

        _dockPreviewClose.Stop();
        _dockPreviewClose.Start();
    }

    private void DockPreview_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        DockPreview.IsOpen = false;
        if (DockPreview.DataContext is DockedPluginWindow docked)
        {
            ShowPluginWindow(docked.Window);
        }

        e.Handled = true;
    }

    private void DropoutsText_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.Health.ResetDropoutsCommand.Execute(null);
            e.Handled = true;
        }
    }

    // ---- Plug-in list ----

    // Builds the plug-in list's right-click menu: removing the plug-in under the mouse, when there is
    // one, then adding a file or a folder and bringing back removed plug-ins.
    private void VstPluginList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel || sender is not FrameworkElement element)
        {
            return;
        }

        e.Handled = true;
        var plugin = (e.OriginalSource as FrameworkElement)?.DataContext as VstPluginViewModel
            ?? element.DataContext as VstPluginViewModel;
        var menu = new ContextMenu { PlacementTarget = element };

        if (plugin is not null)
        {
            var favorite = new MenuItem { Header = plugin.IsFavorite ? "★ Remove from favorites" : "★ Add to favorites" };
            favorite.Click += (_, _) => viewModel.SetVstPluginFavorite(plugin, !plugin.IsFavorite);
            menu.Items.Add(favorite);

            var remove = new MenuItem
            {
                Header = $"Remove \"{plugin.Name}\" from the list",
                ToolTip = "For plug-ins that do not load. The file stays on disk; it can be brought back later.",
            };
            remove.Click += (_, _) => viewModel.RemoveVstPluginFromList(plugin);
            menu.Items.Add(remove);
            menu.Items.Add(new Separator());
        }

        var addFile = new MenuItem { Header = "Add plug-in file..." };
        addFile.Click += (_, _) => viewModel.AddVstPluginFile();
        menu.Items.Add(addFile);

        var addFolder = new MenuItem { Header = "Add plug-in folder..." };
        addFolder.Click += (_, _) => viewModel.AddVstPluginFolder();
        menu.Items.Add(addFolder);

        var restore = new MenuItem
        {
            Header = $"Restore removed plug-ins ({viewModel.RemovedVstPluginCount})",
            IsEnabled = viewModel.RemovedVstPluginCount > 0,
        };
        restore.Click += (_, _) => viewModel.RestoreRemovedVstPlugins();
        menu.Items.Add(restore);

        var recheck = new MenuItem
        {
            Header = viewModel.UnavailableVstPluginCount > 0
                ? $"Check plug-ins again ({viewModel.UnavailableVstPluginCount} do not load)"
                : "Check plug-ins again",
            ToolTip = "Loads every plug-in again to see which ones work, as after installing or licensing one",
        };
        recheck.Click += (_, _) => viewModel.CheckVstPluginsAgain();
        menu.Items.Add(recheck);

        menu.IsOpen = true;
    }

    // ---- Track effects ----

    private readonly Dictionary<PatternTrackViewModel, EffectRackWindow> _fxRacks = new();

    // + ADD lists Sonator's effects; picking one adds it to the selected track and opens its knobs.
    private void AddEffectButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel || sender is not Button button)
        {
            return;
        }

        if (viewModel.SelectedTrack is null)
        {
            SonatorMessageDialog.Show(this, "Effects", "Select a track first.");
            return;
        }

        var menu = new ContextMenu { PlacementTarget = button, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var definition in viewModel.AvailableEffects)
        {
            var item = new MenuItem { Header = $"{definition.Name}  —  {definition.Description}" };
            item.Click += (_, _) =>
            {
                var track = viewModel.SelectedTrack;
                if (viewModel.AddEffect(track, definition) is { } effect && track is not null)
                {
                    OpenFxRack(viewModel, track, effect);
                }
            };
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(VstEffectsMenu(viewModel, plugin =>
        {
            var track = viewModel.SelectedTrack;
            if (viewModel.AddPluginEffect(track, plugin) is { } effect && track is not null)
            {
                OpenFxRack(viewModel, track, effect);
            }
        }));

        // SonNormalize works once on the track's audio clip rather than live, so it sits apart.
        menu.Items.Add(new Separator());
        var normalize = new MenuItem
        {
            Header = "SonNormalize  —  set the audio clip's peak level",
            IsEnabled = !string.IsNullOrEmpty(viewModel.SelectedTrack.AudioClipPath),
            ToolTip = "For tracks with an audio clip",
        };
        normalize.Click += (_, _) => NormalizeSelectedTrack(viewModel);
        menu.Items.Add(normalize);

        menu.IsOpen = true;
    }

    private void NormalizeSelectedTrack(MainViewModel viewModel)
    {
        if (viewModel.SelectedTrack is not { AudioClipPath: { } path } track || !System.IO.File.Exists(path))
        {
            return;
        }

        var peak = Services.Effects.SonNormalize.PeakDb(Services.AudioClipLoader.Load(path));
        if (double.IsNegativeInfinity(peak))
        {
            SonatorMessageDialog.Show(this, "SonNormalize", "The audio clip is silent, so there is nothing to normalize.");
            return;
        }

        var window = new NormalizeWindow(track.Name, peak) { Owner = this };
        if (window.ShowDialog() == true)
        {
            viewModel.NormalizeAudioClip(track, window.TargetDb);
        }
    }

    private void EffectName_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainViewModel { SelectedTrack: { } track } viewModel
            && sender is FrameworkElement { DataContext: TrackEffectViewModel effect })
        {
            OpenFxRack(viewModel, track, effect);
        }
    }

    // The FX button next to R opens the track's rack, and closes it when it is open.
    private void TrackFxButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel
            || sender is not FrameworkElement { DataContext: ArrangeTimelineTrackViewModel timelineTrack })
        {
            return;
        }

        // The track whose FX is clicked becomes the selected one, so the EFFECTS panel shows the same effects.
        viewModel.SelectedTrack = timelineTrack.SourceTrack;
        if (_fxRacks.TryGetValue(timelineTrack.SourceTrack, out var open))
        {
            // Minimized or in the dock, it is out of sight: the button brings it back rather than closing it.
            if (open.WindowState == WindowState.Minimized || !open.IsVisible)
            {
                BringBack(open);
            }
            else
            {
                open.Close();
            }
            return;
        }

        viewModel.SwitchOnDefaultReverb(timelineTrack.SourceTrack);
        OpenFxRack(viewModel, timelineTrack.SourceTrack, null);
    }

    private void AutomationLane_EditStarting(object? sender, EventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.BeginAutomationEdit();
        }
    }

    private void ShowVolumeAutomation_Click(object sender, RoutedEventArgs e) => ShowAutomation(sender, AutomationTarget.Volume);

    private void ShowPanAutomation_Click(object sender, RoutedEventArgs e) => ShowAutomation(sender, AutomationTarget.Pan);

    private void HideAutomation_Click(object sender, RoutedEventArgs e) => ShowAutomation(sender, AutomationTarget.None);

    private void ClearAutomation_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.ClearShownAutomation(MenuTrack(sender, viewModel));
        }
    }

    // From a track's own menu, that track; from the Track menu, the selected one.
    private static PatternTrackViewModel? MenuTrack(object sender, MainViewModel viewModel) =>
        sender is FrameworkElement { DataContext: ArrangeTimelineTrackViewModel timelineTrack }
            ? timelineTrack.SourceTrack
            : viewModel.SelectedTrack;

    private void ShowAutomation(object sender, AutomationTarget target)
    {
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.ShowAutomation(MenuTrack(sender, viewModel), target);
        }
    }

    // Opens a track's (or the master's) rack from elsewhere, as from the mixer, showing the effect if one is given.
    internal void OpenTrackFxRack(PatternTrackViewModel track, TrackEffectViewModel? effect)
    {
        if (DataContext is MainViewModel viewModel)
        {
            OpenFxRack(viewModel, track, effect);
        }
    }

    // The FX button on the MASTER strip opens the master's rack, and closes it when it is open.
    private void MasterFxButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        if (_fxRacks.TryGetValue(viewModel.MasterTrack, out var open))
        {
            // Minimized or in the dock, it is out of sight: the button brings it back rather than closing it.
            if (open.WindowState == WindowState.Minimized || !open.IsVisible)
            {
                BringBack(open);
            }
            else
            {
                open.Close();
            }
            return;
        }

        OpenFxRack(viewModel, viewModel.MasterTrack, null);
    }

    private void MoveEffectButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel
            && sender is Button { DataContext: TrackEffectViewModel effect, Tag: string direction })
        {
            viewModel.MoveEffect(viewModel.SelectedTrack, effect, int.Parse(direction, System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    private void RemoveEffectButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel && sender is Button { DataContext: TrackEffectViewModel effect })
        {
            viewModel.RemoveEffect(viewModel.SelectedTrack, effect);
        }
    }

    // Brings a window back in front: one put away in the dock comes out of it, and one minimized is restored,
    // as activating it alone would leave it out of sight, and the button that opens it would seem to do nothing.
    private void BringBack(Window window)
    {
        if (_dockedPluginWindows.Any(docked => ReferenceEquals(docked.Window, window)))
        {
            ShowPluginWindow(window);
            return;
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
    }

    // One rack window per track: opening it again brings the open one to the front, showing the effect.
    private void OpenFxRack(MainViewModel viewModel, PatternTrackViewModel track, TrackEffectViewModel? effect)
    {
        if (!_fxRacks.TryGetValue(track, out var rack))
        {
            rack = new EffectRackWindow(viewModel, track) { Owner = this };
            rack.Closed += (_, _) => _fxRacks.Remove(track);
            _fxRacks[track] = rack;
            FollowPluginWindow(rack); // minimized, it goes to the dock with its picture
            rack.Show();
        }

        BringBack(rack);
        if (effect is not null)
        {
            rack.ShowEffect(effect);
        }
    }

    // After undo, redo or opening a project the tracks are new objects: each open rack is reopened, in
    // the same place on screen, on the track now at its old track's position.
    private void ReopenFxRacks()
    {
        if (DataContext is not MainViewModel viewModel || _fxRacks.Count == 0)
        {
            return;
        }

        var stale = _fxRacks
            // Racks still on a current track need nothing, nor does the master's, which is always the same.
            .Where(entry => entry.Key != viewModel.MasterTrack && !viewModel.Tracks.Contains(entry.Key))
            .Select(entry => entry.Value)
            .ToList();
        foreach (var window in stale)
        {
            var (left, top, index) = (window.Left, window.Top, window.TrackIndex);
            window.Close();
            if (index >= 0 && index < viewModel.Tracks.Count)
            {
                var track = viewModel.Tracks[index];
                OpenFxRack(viewModel, track, null);
                _fxRacks[track].Left = left;
                _fxRacks[track].Top = top;
            }
        }
    }

    // Clicking a track's name opens the window that sets its name and MIDI channel.
    private void TrackName_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel
            || sender is not FrameworkElement { DataContext: ArrangeTimelineTrackViewModel timelineTrack })
        {
            return;
        }

        e.Handled = true;
        viewModel.SelectedTrack = timelineTrack.SourceTrack;
        ShowEditTrackDialog(viewModel, timelineTrack.SourceTrack);
    }

    private void TrackInstrumentIcon_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel
            || sender is not FrameworkElement { DataContext: ArrangeTimelineTrackViewModel timelineTrack })
        {
            return;
        }

        var track = timelineTrack.SourceTrack;
        var picker = new InstrumentIconPickerWindow(track.Name, track.InstrumentIconKey) { Owner = this };
        if (picker.ShowDialog() == true)
        {
            viewModel.SetTrackInstrumentIcon(track, picker.SelectedKey);
        }
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
