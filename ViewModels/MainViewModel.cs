using System.Collections.ObjectModel;
using System.IO;

using System.Linq;
using Melanchall.DryWetMidi.Standards;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Microsoft.VisualBasic;
using Sonator.Models;
using Sonator.Services;

using static Sonator.ViewModels.MidiImportTrackBuilder;
using static Sonator.ViewModels.PatternFactory;
using static Sonator.ViewModels.ProjectDocumentMapper;

namespace Sonator.ViewModels;

public sealed class MainViewModel : ViewModelBase, IDisposable
{
    private const int ArrangementPatternLength = 8;
    public const double TimelineTrackHeaderWidth = 260;
    private readonly SequencerEngine _engine;
    private readonly ProjectFileService _projectFileService;
    private readonly TrackClipboardService _trackClipboardService;
    private readonly MidiOutputDeviceService _midiOutputDeviceService;
    private readonly MidiFileImportService _midiFileImportService;
    private readonly VstPluginService _vstPluginService;
    private readonly AppSettingsService _appSettingsService;
    private readonly Stack<ProjectDocument> _undoStack = new();
    private readonly Stack<ProjectDocument> _redoStack = new();
    private PatternViewModel? _selectedPattern;
    private PatternTrackViewModel? _selectedTrack;
    private StepViewModel? _selectedStep;
    private ArrangeCellViewModel? _selectedArrangeCell;
    private ArrangeCellViewModel? _selectedArrangePatternCell;
    private SongArrangeBlockViewModel? _selectedArrangeBlock;
    private MidiOutputDeviceViewModel? _selectedMidiOutputDevice;
    private TrackDocument? _copiedTrackDocument;
    private ObservableCollection<PianoRollRowViewModel> _pianoRollRows = new();
    private double _pianoRollZoom = 1.0;
    private double _tempo = 116.5674;
    private double _masterVolume = 0.75;
    private double _masterSignalLevel;
    private readonly TrackMeterCalculator _trackMeters = new();
    private int _barCount = 32;
    private int _currentStepIndex;
    private int _loopStartStep = 0;
    private int _loopEndStep = 15;
    private string _timeSignature = "4/4";
    private bool _isPlaying;
    private bool _isRecording;
    private bool _isArrangeMode;
    private bool _isLoopEnabled;
    private bool _isFollowingArrangePlayback;
    private bool _isRestoringState;
    private int _previousTimelineStep = -1;
    private int _previousPianoRollStep = -1;
    private string _transportPosition = "00:00.00";
    private string _statusMessage = "Ready";
    private string? _currentProjectPath;
    private string? _vstPluginsPath;
    private double _timelineBarWidth = 180;
    private double _timelineViewportWidth;
    private int _currentSongBarNumber = 1;
    private SongArrangeRowViewModel? _currentPlayingArrangeRow;

    public MainViewModel()
        : this(null, null, null, null, null)
    {
    }

    private void RebuildArrangeBlocks(IReadOnlyList<SongArrangeBlockDocument> blocks)
    {
        ArrangeBlocks.Clear();
        foreach (var block in blocks)
        {
            ArrangeBlocks.Add(new SongArrangeBlockViewModel(
                block.Row,
                block.Column,
                block.RowSpan,
                block.ColumnSpan,
                block.Label,
                block.Detail,
                block.Accent));
        }
    }

    internal MainViewModel(
        SequencerEngine? engine,
        ProjectFileService? projectFileService,
        TrackClipboardService? trackClipboardService,
        MidiOutputDeviceService? midiOutputDeviceService,
        MidiFileImportService? midiFileImportService,
        VstPluginService? vstPluginService = null,
        AppSettingsService? appSettingsService = null)
    {
        _engine = engine ?? new SequencerEngine();
        _projectFileService = projectFileService ?? new ProjectFileService();
        _trackClipboardService = trackClipboardService ?? new TrackClipboardService();
        _midiOutputDeviceService = midiOutputDeviceService ?? new MidiOutputDeviceService();
        _midiFileImportService = midiFileImportService ?? new MidiFileImportService();
        _vstPluginService = vstPluginService ?? new VstPluginService();
        _appSettingsService = appSettingsService ?? new AppSettingsService();
        _engine.StepChanged += OnStepChanged;
        _engine.PatternChanged += OnPlaybackPatternChanged;
        _engine.PlaybackCompleted += OnPlaybackCompleted;
        _engine.PlaybackFaulted += OnPlaybackFaulted;

        Patterns = BuildDefaultPatterns();
        ArrangeBarNumbers = new ObservableCollection<int>();
        ArrangeRows = new ObservableCollection<ArrangeRowViewModel>();
        SongArrangeRows = new ObservableCollection<SongArrangeRowViewModel>();
        VisibleSongArrangeRows = new ObservableCollection<SongArrangeRowViewModel>();
        ArrangeBlocks = new ObservableCollection<SongArrangeBlockViewModel>();
        Tracks = new ObservableCollection<PatternTrackViewModel>();
        TimelineTracks = new ObservableCollection<ArrangeTimelineTrackViewModel>();
        TimelineBarNumbers = new ObservableCollection<int>();
        VstPlugins = new ObservableCollection<VstPluginViewModel>();
        MidiOutputDevices = new ObservableCollection<MidiOutputDeviceViewModel>(
            _midiOutputDeviceService.GetOutputDevices()
                .Select(device => new MidiOutputDeviceViewModel(device.DeviceId, device.DisplayName)));

        InitializeCollections();
        InitializeCommands(
            out var playCommand,
            out var stopCommand,
            out var toggleRecordCommand,
            out var toggleStepCommand,
            out var togglePianoRollCellCommand,
            out var copyTrackCommand,
            out var pasteTrackCommand,
            out var clearArrangeCellCommand,
            out var toggleTrackMuteCommand,
            out var toggleTrackSoloCommand,
            out var movePatternUpCommand,
            out var movePatternDownCommand,
            out var moveArrangePatternUpCommand,
            out var moveArrangePatternDownCommand,
            out var deleteArrangePatternCommand,
            out var createArrangePatternCommand,
            out var copyArrangePatternCommand,
            out var undoCommand,
            out var redoCommand,
            out var saveProjectCommand,
            out var saveProjectAsCommand,
            out var loadProjectCommand,
            out var importMidiCommand,
            out var newProjectCommand,
            out var addTrackCommand,
            out var transposeUpCommand,
            out var transposeDownCommand,
            out var quantizeTrackCommand,
            out var refreshMidiDevicesCommand,
            out var browseVstPluginsPathCommand);

        PlayCommand = playCommand;
        StopCommand = stopCommand;
        ToggleRecordCommand = toggleRecordCommand;
        ToggleStepCommand = toggleStepCommand;
        TogglePianoRollCellCommand = togglePianoRollCellCommand;
        CopyTrackCommand = copyTrackCommand;
        PasteTrackCommand = pasteTrackCommand;
        ClearArrangeCellCommand = clearArrangeCellCommand;
        ToggleTrackMuteCommand = toggleTrackMuteCommand;
        ToggleTrackSoloCommand = toggleTrackSoloCommand;
        MovePatternUpCommand = movePatternUpCommand;
        MovePatternDownCommand = movePatternDownCommand;
        MoveArrangePatternUpCommand = moveArrangePatternUpCommand;
        MoveArrangePatternDownCommand = moveArrangePatternDownCommand;
        DeleteArrangePatternCommand = deleteArrangePatternCommand;
        CreateArrangePatternCommand = createArrangePatternCommand;
        CopyArrangePatternCommand = copyArrangePatternCommand;
        UndoCommand = undoCommand;
        RedoCommand = redoCommand;
        SaveProjectCommand = saveProjectCommand;
        SaveProjectAsCommand = saveProjectAsCommand;
        LoadProjectCommand = loadProjectCommand;
        ImportMidiCommand = importMidiCommand;
        NewProjectCommand = newProjectCommand;
        AddTrackCommand = addTrackCommand;
        TransposeUpCommand = transposeUpCommand;
        TransposeDownCommand = transposeDownCommand;
        QuantizeTrackCommand = quantizeTrackCommand;
        RefreshMidiDevicesCommand = refreshMidiDevicesCommand;
        BrowseVstPluginsPathCommand = browseVstPluginsPathCommand;
        ToggleArrangeModeCommand = new RelayCommand(_ => IsArrangeMode = !IsArrangeMode);
        ToggleLoopCommand = new RelayCommand(_ => IsLoopEnabled = !IsLoopEnabled);
        ZoomTimelineInCommand = new RelayCommand(_ => TimelineBarWidth += 20);
        ZoomTimelineOutCommand = new RelayCommand(_ => TimelineBarWidth -= 20);
        DeleteTrackCommand = new RelayCommand(
            _ => DeleteSelectedTrack(),
            _ => SelectedPattern is not null && SelectedTrack is not null);

        InitializeInitialSelection();
        InitializeVstPlugins();
    }

    private void InitializeCollections()
    {
        ResetArrangeRows();
        ResetSongArrangeRows();
        ResetArrangeBlocks();

        try
        {
            _copiedTrackDocument = _trackClipboardService.Load();
        }
        catch (System.IO.IOException)
        {
            _copiedTrackDocument = null;
        }
        catch (System.Text.Json.JsonException)
        {
            _copiedTrackDocument = null;
        }
    }

    private static string DefaultVstDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
        "VST3");

    private void InitializeVstPlugins()
    {
        var settings = _appSettingsService.Load();
        _vstPluginsPath = settings.VstPluginsPath;
        RaisePropertyChanged(nameof(VstPluginsPath));
        RescanVstPlugins();
    }

    private void RescanVstPlugins()
    {
        VstPlugins.Clear();

        var scannedFolders = new List<string>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var plugins = new List<(string Name, string FilePath, string Kind)>();

        if (Directory.Exists(DefaultVstDirectory))
        {
            scannedFolders.Add(DefaultVstDirectory);
            foreach (var plugin in _vstPluginService.ScanDirectory(DefaultVstDirectory))
            {
                if (seenPaths.Add(plugin.FilePath))
                {
                    plugins.Add(plugin);
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(_vstPluginsPath)
            && !string.Equals(_vstPluginsPath, DefaultVstDirectory, StringComparison.OrdinalIgnoreCase))
        {
            scannedFolders.Add(_vstPluginsPath);
            foreach (var plugin in _vstPluginService.ScanDirectory(_vstPluginsPath))
            {
                if (seenPaths.Add(plugin.FilePath))
                {
                    plugins.Add(plugin);
                }
            }
        }

        foreach (var plugin in plugins.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            VstPlugins.Add(new VstPluginViewModel(plugin.Name, plugin.FilePath, plugin.Kind));
        }

        var scannedFoldersText = scannedFolders.Count > 0 ? string.Join(", ", scannedFolders) : "no folders";
        StatusMessage = VstPlugins.Count > 0
            ? $"Found {VstPlugins.Count} VST plug-ins ({scannedFoldersText})"
            : $"No VST plug-ins found ({scannedFoldersText})";
    }

    private void BrowseVstPluginsPath()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select the VST plug-in folder",
            Multiselect = false
        };

        if (!string.IsNullOrWhiteSpace(_vstPluginsPath) && Directory.Exists(_vstPluginsPath))
        {
            dialog.InitialDirectory = _vstPluginsPath;
        }

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        VstPluginsPath = dialog.FolderName;
        _appSettingsService.Save(new AppSettings { VstPluginsPath = VstPluginsPath });
        RescanVstPlugins();
    }

    private void InitializeInitialSelection()
    {
        SelectedMidiOutputDevice = MidiOutputDevices.FirstOrDefault();
        _engine.SetLoopRange(LoopStartStep, LoopEndStep);

        var initialArrangeCell = SongArrangeRows
            .SelectMany(row => row.Cells)
            .FirstOrDefault(cell => !string.IsNullOrWhiteSpace(cell.Label));
        if (initialArrangeCell is not null)
        {
            SelectArrangeCell(initialArrangeCell);
        }
        else
        {
            SelectedPattern = Patterns.FirstOrDefault();
        }

        RebuildTimeline();
    }

    private void InitializeCommands(
        out RelayCommand playCommand,
        out RelayCommand stopCommand,
        out RelayCommand toggleRecordCommand,
        out RelayCommand toggleStepCommand,
        out RelayCommand togglePianoRollCellCommand,
        out RelayCommand copyTrackCommand,
        out RelayCommand pasteTrackCommand,
        out RelayCommand clearArrangeCellCommand,
        out RelayCommand toggleTrackMuteCommand,
        out RelayCommand toggleTrackSoloCommand,
        out RelayCommand movePatternUpCommand,
        out RelayCommand movePatternDownCommand,
        out RelayCommand moveArrangePatternUpCommand,
        out RelayCommand moveArrangePatternDownCommand,
        out RelayCommand deleteArrangePatternCommand,
        out RelayCommand createArrangePatternCommand,
        out RelayCommand copyArrangePatternCommand,
        out RelayCommand undoCommand,
        out RelayCommand redoCommand,
        out RelayCommand saveProjectCommand,
        out RelayCommand saveProjectAsCommand,
        out RelayCommand loadProjectCommand,
        out RelayCommand importMidiCommand,
        out RelayCommand newProjectCommand,
        out RelayCommand addTrackCommand,
        out RelayCommand transposeUpCommand,
        out RelayCommand transposeDownCommand,
        out RelayCommand quantizeTrackCommand,
        out RelayCommand refreshMidiDevicesCommand,
        out RelayCommand browseVstPluginsPathCommand)
    {
        playCommand = new RelayCommand(_ => StartPlayback(), _ => SelectedPattern is not null && !IsPlaying);
        stopCommand = new RelayCommand(_ => StopPlayback(), _ => IsPlaying);
        toggleRecordCommand = new RelayCommand(_ =>
        {
            IsRecording = !IsRecording;
            StatusMessage = IsRecording ? "Recording armed" : "Recording disarmed";
        });
        toggleStepCommand = new RelayCommand(parameter => ToggleStep(parameter), parameter => parameter is StepViewModel);
        togglePianoRollCellCommand = new RelayCommand(parameter => TogglePianoRollCell(parameter), parameter => parameter is PianoRollCellViewModel);
        copyTrackCommand = new RelayCommand(_ => CopySelectedTrack(), _ => SelectedTrack is not null);
        pasteTrackCommand = new RelayCommand(_ => PasteTrack(), _ => _selectedTrack is not null && _copiedTrackDocument is not null);
        clearArrangeCellCommand = new RelayCommand(_ => ClearSelectedArrangeCell(), _ => SelectedArrangeCell is not null);
        toggleTrackMuteCommand = new RelayCommand(parameter => ToggleTrackMute(parameter), parameter => parameter is PatternTrackViewModel);
        toggleTrackSoloCommand = new RelayCommand(parameter => ToggleTrackSolo(parameter), parameter => parameter is PatternTrackViewModel);
        movePatternUpCommand = new RelayCommand(parameter => MovePattern(parameter, -1), parameter => CanMovePattern(parameter, -1));
        movePatternDownCommand = new RelayCommand(parameter => MovePattern(parameter, 1), parameter => CanMovePattern(parameter, 1));
        moveArrangePatternUpCommand = new RelayCommand(_ => MoveSelectedArrangePattern(-1), _ => CanMoveSelectedArrangePattern(-1));
        moveArrangePatternDownCommand = new RelayCommand(_ => MoveSelectedArrangePattern(1), _ => CanMoveSelectedArrangePattern(1));
        deleteArrangePatternCommand = new RelayCommand(_ => DeleteSelectedArrangePattern(), _ => CanDeleteSelectedArrangePattern());
        createArrangePatternCommand = new RelayCommand(_ => CreateArrangePattern());
        copyArrangePatternCommand = new RelayCommand(_ => CopySelectedArrangePattern(), _ => CanCopySelectedArrangePattern());
        undoCommand = new RelayCommand(_ => Undo(), _ => _undoStack.Count > 0);
        redoCommand = new RelayCommand(_ => Redo(), _ => _redoStack.Count > 0);
        saveProjectCommand = new RelayCommand(_ => SaveProject(), _ => SelectedPattern is not null);
        saveProjectAsCommand = new RelayCommand(_ => SaveProjectAs(), _ => SelectedPattern is not null);
        loadProjectCommand = new RelayCommand(_ => LoadProject());
        importMidiCommand = new RelayCommand(_ => ImportMidiFile());
        newProjectCommand = new RelayCommand(_ => CreateNewProject());
        addTrackCommand = new RelayCommand(parameter => AddTrack(parameter), _ => SelectedPattern is not null);
        transposeUpCommand = new RelayCommand(_ => TransposeSelectedTrack(1), _ => SelectedTrack is not null);
        transposeDownCommand = new RelayCommand(_ => TransposeSelectedTrack(-1), _ => SelectedTrack is not null);
        quantizeTrackCommand = new RelayCommand(_ => QuantizeSelectedTrack(), _ => SelectedTrack is not null);
        refreshMidiDevicesCommand = new RelayCommand(_ => ReloadMidiOutputDevices());
        browseVstPluginsPathCommand = new RelayCommand(_ => BrowseVstPluginsPath());
    }

    public ObservableCollection<PatternViewModel> Patterns { get; }

    public ObservableCollection<ArrangeRowViewModel> ArrangeRows { get; }

    public ObservableCollection<SongArrangeRowViewModel> SongArrangeRows { get; }

    public ObservableCollection<SongArrangeRowViewModel> VisibleSongArrangeRows { get; }

    public ObservableCollection<SongArrangeBlockViewModel> ArrangeBlocks { get; }

    public ObservableCollection<int> ArrangeBarNumbers { get; }

    public ObservableCollection<PatternTrackViewModel> Tracks { get; }

    public ObservableCollection<ArrangeTimelineTrackViewModel> TimelineTracks { get; }

    public ObservableCollection<int> TimelineBarNumbers { get; }

    public ObservableCollection<MidiOutputDeviceViewModel> MidiOutputDevices { get; }

    public ObservableCollection<VstPluginViewModel> VstPlugins { get; }

    public string? VstPluginsPath
    {
        get => _vstPluginsPath;
        set => SetProperty(ref _vstPluginsPath, value);
    }

    public PatternViewModel? SelectedPattern
    {
        get => _selectedPattern;
        set
        {
            if (SetProperty(ref _selectedPattern, value))
            {
                Tracks.Clear();
                if (value is not null)
                {
                    foreach (var track in value.Tracks)
                    {
                        Tracks.Add(track);
                    }

                    SelectedTrack = value.Tracks.FirstOrDefault();
                }
                else
                {
                    SelectedTrack = null;
                }

                SelectedArrangeCell = null;
                RaisePropertyChanged(nameof(SelectedArrangePatternLength));
                RaisePropertyChanged(nameof(SelectedArrangePatternStart));
                RaisePropertyChanged(nameof(SelectedArrangePatternEnd));
                RaisePropertyChanged(nameof(SelectedArrangePatternLengthDisplay));
                RaisePropertyChanged(nameof(SelectedPatternTranspose));
                if (!_isFollowingArrangePlayback)
                {
                    ConfigurePlaybackSource();
                    RebuildTimeline();
                }
                UpdateSelectedArrangeBlockState();
                ResetTrackMeters();
                StatusMessage = value is null ? "No pattern selected" : $"Loaded pattern {value.Name}";
                PlayCommand?.RaiseCanExecuteChanged();
                StopCommand?.RaiseCanExecuteChanged();
                SaveProjectCommand?.RaiseCanExecuteChanged();
                SaveProjectAsCommand?.RaiseCanExecuteChanged();
                QuantizeTrackCommand?.RaiseCanExecuteChanged();
                MovePatternUpCommand?.RaiseCanExecuteChanged();
                MovePatternDownCommand?.RaiseCanExecuteChanged();
                AddTrackCommand?.RaiseCanExecuteChanged();
                RefreshPianoRoll();
            }
        }
    }

    public PatternTrackViewModel? SelectedTrack
    {
        get => _selectedTrack;
        set
        {
            if (SetProperty(ref _selectedTrack, value))
            {
                foreach (var track in Tracks)
                {
                    track.IsSelected = ReferenceEquals(track, value);
                }

                SelectedStep = value?.Steps.FirstOrDefault(step => step.IsActive) ?? value?.Steps.FirstOrDefault();
                RaisePropertyChanged(nameof(SelectedTrackIndex));
                RaisePropertyChanged(nameof(SelectedTrackHeader));
                RefreshPianoRoll();
                TransposeUpCommand?.RaiseCanExecuteChanged();
                TransposeDownCommand?.RaiseCanExecuteChanged();
                QuantizeTrackCommand?.RaiseCanExecuteChanged();
                CopyTrackCommand?.RaiseCanExecuteChanged();
                PasteTrackCommand?.RaiseCanExecuteChanged();
                DeleteTrackCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    public int SelectedTrackIndex => SelectedTrack is null ? 0 : Tracks.IndexOf(SelectedTrack) + 1;

    public string SelectedTrackHeader => $"TRACK: {SelectedTrackIndex}";

    public SongArrangeBlockViewModel? SelectedArrangeBlock
    {
        get => _selectedArrangeBlock;
        set
        {
            if (SetProperty(ref _selectedArrangeBlock, value))
            {
                UpdateSelectedArrangeBlockState();
                if (value is not null)
                {
                    SelectedPattern = Patterns.FirstOrDefault(pattern => string.Equals(pattern.Name, value.Label, StringComparison.OrdinalIgnoreCase));
                }
            }
        }
    }

    public MidiOutputDeviceViewModel? SelectedMidiOutputDevice
    {
        get => _selectedMidiOutputDevice;
        set
        {
            if (SetProperty(ref _selectedMidiOutputDevice, value))
            {
                ApplySelectedMidiOutputDevice();
            }
        }
    }

    public StepViewModel? SelectedStep
    {
        get => _selectedStep;
        set => SetProperty(ref _selectedStep, value);
    }

    public ArrangeCellViewModel? SelectedArrangeCell
    {
        get => _selectedArrangeCell;
        set
        {
            if (SetProperty(ref _selectedArrangeCell, value))
            {
                UpdateSelectedArrangeCellState();
                RaisePropertyChanged(nameof(ArrangeCellLabel));
                RaisePropertyChanged(nameof(ArrangeCellDetail));
                ClearArrangeCellCommand?.RaiseCanExecuteChanged();
                MoveArrangePatternUpCommand?.RaiseCanExecuteChanged();
                MoveArrangePatternDownCommand?.RaiseCanExecuteChanged();
                DeleteArrangePatternCommand?.RaiseCanExecuteChanged();
                CopyArrangePatternCommand?.RaiseCanExecuteChanged();
                RaisePropertyChanged(nameof(SelectedArrangePatternLength));
            }
        }
    }

    public string ArrangeCellLabel
    {
        get => SelectedArrangeCell?.Label ?? string.Empty;
        set
        {
            if (SelectedArrangeCell is null || string.Equals(SelectedArrangeCell.Label, value, StringComparison.Ordinal))
            {
                return;
            }

            CaptureUndoStateIfNeeded();
            SelectedArrangeCell.Label = value;
            RaisePropertyChanged();
        }
    }

    public string ArrangeCellDetail
    {
        get => SelectedArrangeCell?.Detail ?? string.Empty;
        set
        {
            if (SelectedArrangeCell is null || string.Equals(SelectedArrangeCell.Detail, value, StringComparison.Ordinal))
            {
                return;
            }

            CaptureUndoStateIfNeeded();
            SelectedArrangeCell.Detail = value;
            RaisePropertyChanged();
        }
    }

    public int SelectedArrangePatternLength
    {
        get => TryGetSelectedArrangePatternRange(out _, out _, out var length) ? length : 0;
        set => ResizeSelectedArrangePattern(value);
    }

    public string SelectedArrangePatternStart
    {
        get => TryGetSelectedArrangePatternRange(out var startRow, out _, out _)
            ? $"{startRow + 1}:1:1:1"
            : string.Empty;
    }

    public string SelectedArrangePatternEnd
    {
        get
        {
            if (!TryGetSelectedArrangePatternRange(out var startRow, out _, out var length))
            {
                return string.Empty;
            }

            return $"{startRow + length + 1}:1:1:1";
        }
    }

    public string SelectedArrangePatternLengthDisplay
    {
        get => TryGetSelectedArrangePatternRange(out _, out _, out var length)
            ? $"{length}:0:0:0"
            : string.Empty;
        set => SetSelectedArrangePatternLength(value);
    }

    public int SelectedPatternTranspose
    {
        get => SelectedPattern?.Transpose ?? 0;
        set
        {
            var pattern = SelectedPattern;
            if (pattern is null || pattern.Transpose == value)
            {
                return;
            }

            CaptureUndoStateIfNeeded();
            var transposeDelta = value - pattern.Transpose;
            foreach (var track in pattern.Tracks)
            {
                track.NoteNumber = Math.Clamp(track.NoteNumber + transposeDelta, 0, 127);
                foreach (var step in track.Steps)
                {
                    if (step.IsActive && step.MidiNoteNumber.HasValue)
                    {
                        step.MidiNoteNumber = Math.Clamp(step.MidiNoteNumber.Value + transposeDelta, 0, 127);
                    }
                }

                if (track.MidiNoteEvents.Count > 0)
                {
                    track.MidiNoteEvents = track.MidiNoteEvents
                        .Select(e => e with { NoteNumber = Math.Clamp(e.NoteNumber + transposeDelta, 0, 127) })
                        .ToList();
                }
            }

            pattern.Transpose = value;
            RefreshPianoRoll();
            StatusMessage = $"Pattern transposed by {value:+#;-#;0} semitones";
            RaisePropertyChanged();
        }
    }

    public ObservableCollection<PianoRollRowViewModel> PianoRollRows
    {
        get => _pianoRollRows;
        private set => SetProperty(ref _pianoRollRows, value);
    }

    public double PianoRollZoom
    {
        get => _pianoRollZoom;
        set
        {
            if (SetProperty(ref _pianoRollZoom, Math.Clamp(value, 0.75, 2.5)))
            {
                RaisePropertyChanged(nameof(PianoRollCellWidth));
                RaisePropertyChanged(nameof(PianoRollCellHeight));
                RaisePropertyChanged(nameof(PianoRollNoteLabelWidth));
            }
        }
    }

    public double PianoRollCellWidth => 16 * PianoRollZoom;

    public double PianoRollCellHeight => 22 * PianoRollZoom;

    public double PianoRollNoteLabelWidth => 58 * PianoRollZoom;

    public double Tempo
    {
        get => _tempo;
        set
        {
            if (_tempo == value)
            {
                return;
            }

            CaptureUndoStateIfNeeded();
            if (SetProperty(ref _tempo, value))
            {
                _engine.UpdateTempo(value);
            }
        }
    }

    public double MasterVolume
    {
        get => _masterVolume;
        set
        {
            var clamped = Math.Clamp(value, 0.0, 1.0);
            if (_masterVolume == clamped)
            {
                return;
            }

            if (SetProperty(ref _masterVolume, clamped))
            {
                _engine.UpdateMasterVolume(clamped);
            }
        }
    }

    public double MasterSignalLevel
    {
        get => _masterSignalLevel;
        private set => SetProperty(ref _masterSignalLevel, Math.Clamp(value, 0.0, 1.0));
    }

    public int BarCount
    {
        get => _barCount;
        set
        {
            if (_barCount == value)
            {
                return;
            }

            CaptureUndoStateIfNeeded();
            if (SetProperty(ref _barCount, value))
            {
                RaisePropertyChanged(nameof(TimelineTotalSteps));
            }
        }
    }

    public int CurrentStepIndex
    {
        get => _currentStepIndex;
        private set
        {
            if (SetProperty(ref _currentStepIndex, value))
            {
                RaisePropertyChanged(nameof(CurrentStepBar));
                RaisePropertyChanged(nameof(TimelinePositionStep));
            }
        }
    }

    public int TimelinePositionStep
    {
        get => CurrentStepIndex;
        set => SeekToStep(value);
    }

    public int TimelineTotalSteps => Math.Max(16, BarCount * 16);

    public string CurrentStepBar => FormatSubdivisionPosition(CurrentStepIndex);

    private static string FormatSubdivisionPosition(int stepIndex)
    {
        var bar = stepIndex / 16;
        var remainder = stepIndex % 16;
        var quarter = remainder / 4;
        var eighth = (remainder % 4) / 2;
        var sixteenth = remainder % 2;

        return $"{bar:D2} {quarter:D2} {eighth:D2} {sixteenth:D2}";
    }

    private int GetSelectedPatternStepCount()
    {
        return Math.Max(
            16,
            SelectedPattern?.Tracks.Select(track => track.Steps.Count).DefaultIfEmpty(16).Max() ?? 16);
    }

    public int LoopStartStep
    {
        get => _loopStartStep;
        set
        {
            var clamped = Math.Clamp(value, 0, GetSelectedPatternStepCount() - 1);
            if (_loopStartStep == clamped)
            {
                return;
            }

            CaptureUndoStateIfNeeded();
            if (SetProperty(ref _loopStartStep, clamped))
            {
                if (_loopEndStep < _loopStartStep)
                {
                    _loopEndStep = _loopStartStep;
                    RaisePropertyChanged(nameof(LoopEndStep));
                }

                _engine.SetLoopRange(_loopStartStep, _loopEndStep);
            }
        }
    }

    public int LoopEndStep
    {
        get => _loopEndStep;
        set
        {
            var clamped = Math.Clamp(value, 0, GetSelectedPatternStepCount() - 1);
            if (_loopEndStep == clamped)
            {
                return;
            }

            CaptureUndoStateIfNeeded();
            if (SetProperty(ref _loopEndStep, clamped))
            {
                if (_loopEndStep < _loopStartStep)
                {
                    _loopStartStep = _loopEndStep;
                    RaisePropertyChanged(nameof(LoopStartStep));
                }

                _engine.SetLoopRange(_loopStartStep, _loopEndStep);
            }
        }
    }

    public string TimeSignature
    {
        get => _timeSignature;
        set
        {
            if (string.Equals(_timeSignature, value, StringComparison.Ordinal))
            {
                return;
            }

            CaptureUndoStateIfNeeded();
            SetProperty(ref _timeSignature, value);
        }
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        set
        {
            if (SetProperty(ref _isPlaying, value))
            {
                PlayCommand?.RaiseCanExecuteChanged();
                StopCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsRecording
    {
        get => _isRecording;
        set => SetProperty(ref _isRecording, value);
    }

    public bool IsArrangeMode
    {
        get => _isArrangeMode;
        set
        {
            if (!SetProperty(ref _isArrangeMode, value))
            {
                return;
            }

            if (IsPlaying)
            {
                StopPlayback();
            }

            if (value && _isLoopEnabled)
            {
                _isLoopEnabled = false;
                RaisePropertyChanged(nameof(IsLoopEnabled));
                RaisePropertyChanged(nameof(LoopModeText));
            }

            ConfigurePlaybackSource();
            RebuildTimeline();
            CurrentStepIndex = 0;
            TransportPosition = "00:00.00";
            RaisePropertyChanged(nameof(ArrangeModeText));
            StatusMessage = value
                ? "Arrange ON: playing the complete song."
                : "Arrange OFF: playing the selected pattern.";
        }
    }

    public string ArrangeModeText => IsArrangeMode ? "ON" : "OFF";

    public bool IsLoopEnabled
    {
        get => _isLoopEnabled;
        set
        {
            if (!SetProperty(ref _isLoopEnabled, value))
            {
                return;
            }

            if (IsPlaying)
            {
                StopPlayback();
            }

            if (value && _isArrangeMode)
            {
                _isArrangeMode = false;
                RaisePropertyChanged(nameof(IsArrangeMode));
                RaisePropertyChanged(nameof(ArrangeModeText));
            }

            ConfigurePlaybackSource();
            RebuildTimeline();
            CurrentStepIndex = 0;
            TransportPosition = "00:00.00";
            RaisePropertyChanged(nameof(LoopModeText));
            StatusMessage = value
                ? "Loop ON: repeating the selected pattern."
                : "Loop OFF: the selected pattern plays once.";
        }
    }

    public string LoopModeText => IsLoopEnabled ? "ON" : "OFF";

    public IReadOnlyList<int> SixteenthLabels { get; } = Enumerable.Range(1, 16).ToArray();

    public double TimelineBarWidth
    {
        get => _timelineBarWidth;
        set
        {
            var clamped = Math.Clamp(value, 80, 480);
            if (SetProperty(ref _timelineBarWidth, clamped))
            {
                RaisePropertyChanged(nameof(TimelineContentWidth));
                RaisePropertyChanged(nameof(TimelineWorkspaceWidth));
                RaisePropertyChanged(nameof(TimelineSixteenthWidth));
            }
        }
    }

    public double TimelineSixteenthWidth => TimelineBarWidth / 16.0;

    public double TimelineContentWidth => Math.Max(
        TimelineBarWidth,
        Math.Max(TimelineBarNumbers.Count * TimelineBarWidth, TimelineViewportWidth - TimelineTrackHeaderWidth));

    public double TimelineWorkspaceWidth => TimelineTrackHeaderWidth + TimelineContentWidth;

    public double TimelineViewportWidth
    {
        get => _timelineViewportWidth;
        set
        {
            if (SetProperty(ref _timelineViewportWidth, Math.Max(0, value)))
            {
                RaisePropertyChanged(nameof(TimelineContentWidth));
                RaisePropertyChanged(nameof(TimelineWorkspaceWidth));
            }
        }
    }

    public int CurrentSongBarNumber
    {
        get => _currentSongBarNumber;
        private set => SetProperty(ref _currentSongBarNumber, value);
    }

    public SongArrangeRowViewModel? CurrentPlayingArrangeRow
    {
        get => _currentPlayingArrangeRow;
        private set => SetProperty(ref _currentPlayingArrangeRow, value);
    }

    public string TransportPosition
    {
        get => _transportPosition;
        set => SetProperty(ref _transportPosition, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public string? CurrentProjectPath
    {
        get => _currentProjectPath;
        set
        {
            if (SetProperty(ref _currentProjectPath, value))
            {
                RaisePropertyChanged(nameof(SongTitle));
                RaisePropertyChanged(nameof(WindowTitle));
            }
        }
    }

    public string SongTitle => string.IsNullOrWhiteSpace(CurrentProjectPath)
        ? "New project"
        : System.IO.Path.GetFileNameWithoutExtension(CurrentProjectPath);

    public string WindowTitle => $"Sonator - {SongTitle}";

    public string NotesSummary => "Next milestones: MIDI device routing, pattern copy/paste, save/load, and a real piano roll.";

    public RelayCommand PlayCommand { get; }

    public RelayCommand StopCommand { get; }

    public RelayCommand ToggleRecordCommand { get; }

    public RelayCommand ToggleStepCommand { get; }

    public RelayCommand TogglePianoRollCellCommand { get; }

    public RelayCommand CopyTrackCommand { get; }

    public RelayCommand PasteTrackCommand { get; }

    public RelayCommand ClearArrangeCellCommand { get; }

    public RelayCommand ToggleTrackMuteCommand { get; }

    public RelayCommand ToggleTrackSoloCommand { get; }

    public RelayCommand MovePatternUpCommand { get; }

    public RelayCommand MovePatternDownCommand { get; }

    public RelayCommand MoveArrangePatternUpCommand { get; }

    public RelayCommand MoveArrangePatternDownCommand { get; }

    public RelayCommand DeleteArrangePatternCommand { get; }

    public RelayCommand CreateArrangePatternCommand { get; }

    public RelayCommand CopyArrangePatternCommand { get; }

    public RelayCommand UndoCommand { get; }

    public RelayCommand RedoCommand { get; }

    public RelayCommand SaveProjectCommand { get; }

    public RelayCommand SaveProjectAsCommand { get; }

    public RelayCommand LoadProjectCommand { get; }

    public RelayCommand ImportMidiCommand { get; }

    public RelayCommand NewProjectCommand { get; }

    public RelayCommand AddTrackCommand { get; }

    public RelayCommand DeleteTrackCommand { get; }

    public RelayCommand TransposeUpCommand { get; }

    public RelayCommand TransposeDownCommand { get; }

    public RelayCommand QuantizeTrackCommand { get; }

    public RelayCommand RefreshMidiDevicesCommand { get; }

    public RelayCommand BrowseVstPluginsPathCommand { get; }

    public RelayCommand ToggleArrangeModeCommand { get; }

    public RelayCommand ToggleLoopCommand { get; }

    public RelayCommand ZoomTimelineInCommand { get; }

    public RelayCommand ZoomTimelineOutCommand { get; }

    public void Dispose()
    {
        _engine.StepChanged -= OnStepChanged;
        _engine.PatternChanged -= OnPlaybackPatternChanged;
        _engine.PlaybackCompleted -= OnPlaybackCompleted;
        _engine.PlaybackFaulted -= OnPlaybackFaulted;
        _engine.Dispose();
    }

    private void CaptureUndoStateIfNeeded()
    {
        if (_isRestoringState)
        {
            return;
        }

        _undoStack.Push(BuildProjectDocument());
        _redoStack.Clear();
        RaiseHistoryStateChanged();
    }

    private void RaiseHistoryStateChanged()
    {
        UndoCommand?.RaiseCanExecuteChanged();
        RedoCommand?.RaiseCanExecuteChanged();
    }

    private void Undo()
    {
        if (_undoStack.Count == 0)
        {
            return;
        }

        _redoStack.Push(BuildProjectDocument());
        RestoreProjectDocument(_undoStack.Pop());
        RaiseHistoryStateChanged();
    }

    private void Redo()
    {
        if (_redoStack.Count == 0)
        {
            return;
        }

        _undoStack.Push(BuildProjectDocument());
        RestoreProjectDocument(_redoStack.Pop());
        RaiseHistoryStateChanged();
    }

    private void CreateNewProject()
    {
        _undoStack.Clear();
        _redoStack.Clear();
        _isRestoringState = true;
        try
        {
            CurrentStepIndex = 0;
            TransportPosition = "00:00.00";
            Patterns.Clear();
            foreach (var pattern in BuildDefaultPatterns())
            {
                Patterns.Add(pattern);
            }
            ResetArrangeRows();
            ResetSongArrangeRows();
            ResetArrangeBlocks();
            SelectedArrangeCell = null;
            SelectedPattern = Patterns.FirstOrDefault();
            Tempo = 116.5674;
            MasterVolume = 0.75;
            BarCount = 32;
            LoopStartStep = 0;
            LoopEndStep = 15;
            TimeSignature = "4/4";
            CurrentProjectPath = null;
            StatusMessage = "New project created";
        }
        finally
        {
            _isRestoringState = false;
        }

        SaveProjectCommand?.RaiseCanExecuteChanged();
        SaveProjectAsCommand?.RaiseCanExecuteChanged();
        PasteTrackCommand?.RaiseCanExecuteChanged();
        RaiseHistoryStateChanged();
        ConfigurePlaybackSource();
        RebuildTimeline();
    }

    private void ApplySelectedMidiOutputDevice()
    {
        var device = SelectedMidiOutputDevice;
        if (device is null)
        {
            return;
        }

        if (!_engine.TrySetMidiOutputDevice(device.DeviceId))
        {
            StatusMessage = $"Unable to open MIDI output device: {device.DisplayName}";
        }
        else
        {
            StatusMessage = $"MIDI output set to {device.DisplayName}";
        }
    }

    private void ReloadMidiOutputDevices()
    {
        var selectedName = SelectedMidiOutputDevice?.DisplayName;
        MidiOutputDevices.Clear();
        foreach (var device in _midiOutputDeviceService.GetOutputDevices())
        {
            MidiOutputDevices.Add(new MidiOutputDeviceViewModel(device.DeviceId, device.DisplayName));
        }

        SelectedMidiOutputDevice = MidiOutputDevices.FirstOrDefault(device => device.DisplayName == selectedName)
            ?? MidiOutputDevices.FirstOrDefault();
        StatusMessage = "MIDI output devices refreshed";
    }

    private void SaveProject()
    {
        var path = CurrentProjectPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            path = PromptForSavePath();
            if (path is null)
            {
                return;
            }
        }

        SaveProjectToPath(path);
    }

    private void SaveProjectAs()
    {
        var path = PromptForSavePath();
        if (path is null)
        {
            return;
        }

        SaveProjectToPath(path);
    }

    private string? PromptForSavePath()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "Sonator project (*.sonator.json)|*.sonator.json|JSON files (*.json)|*.json",
            FileName = string.IsNullOrWhiteSpace(CurrentProjectPath)
                ? "sonator-project.sonator.json"
                : System.IO.Path.GetFileName(CurrentProjectPath)
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private void SaveProjectToPath(string path)
    {
        try
        {
            _projectFileService.Save(path, BuildProjectDocument());
            CurrentProjectPath = path;
            StatusMessage = $"Project saved to {path}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Save project failed", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusMessage = "Save failed";
        }
    }

    private void LoadProject()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Sonator project (*.sonator.json)|*.sonator.json|JSON files (*.json)|*.json"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var document = _projectFileService.Load(dialog.FileName);
            _undoStack.Clear();
            _redoStack.Clear();
            RestoreProjectDocument(document);
            CurrentProjectPath = dialog.FileName;
            StatusMessage = $"Project loaded from {dialog.FileName}";
            SaveProjectCommand?.RaiseCanExecuteChanged();
            SaveProjectAsCommand?.RaiseCanExecuteChanged();
            PasteTrackCommand?.RaiseCanExecuteChanged();
            RaiseHistoryStateChanged();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Load project failed", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusMessage = "Load failed";
        }
    }

    private void ImportMidiFile()
    {
        var dialog = new MidiImportDialog
        {
            Owner = Application.Current?.MainWindow
        };
        if (dialog.ShowDialog() != true)
        {
            StatusMessage = "MIDI import cancelled";
            return;
        }

        try
        {
            var importedSong = _midiFileImportService.Import(dialog.FilePath);
            ApplyMidiImport(importedSong);
            CurrentProjectPath = null;
            StatusMessage = $"Imported SONG: {importedSong.Bars.Count} bars from {importedSong.SourceTrackCount} MIDI tracks";
        }
        catch (InvalidDataException ex)
        {
            SonatorMessageDialog.Show(Application.Current?.MainWindow, "MIDI import failed", ex.Message);
            StatusMessage = "MIDI import failed";
        }
        catch (IOException ex)
        {
            SonatorMessageDialog.Show(Application.Current?.MainWindow, "MIDI import failed", ex.Message);
            StatusMessage = "MIDI import failed";
        }
        catch (UnauthorizedAccessException ex)
        {
            SonatorMessageDialog.Show(Application.Current?.MainWindow, "MIDI import failed", ex.Message);
            StatusMessage = "MIDI import failed";
        }
    }

    private void ApplyMidiImport(MidiImportDocument importedSong)
    {
        if (importedSong.Bars.Count == 0)
        {
            throw new InvalidDataException("The imported MIDI file contains no musical bars.");
        }

        StopPlayback();
        _isRestoringState = true;
        try
        {
            Patterns.Clear();
            var stepCount = importedSong.Bars.Count * 16;
            var tracks = new ObservableCollection<PatternTrackViewModel>(
                BuildImportedSongLanes(importedSong).Select(lane => CreateImportedTrack(lane, stepCount)));
            var songPattern = new PatternViewModel(
                $"{importedSong.Name} SONG",
                $"Imported MIDI song: {importedSong.Bars.Count} bars",
                "01",
                tracks);
            Patterns.Add(songPattern);

            var assignments = importedSong.Bars
                .Select(_ => new[] { songPattern.Slot, string.Empty, string.Empty, string.Empty })
                .ToList();
            RebuildSongArrangeRows(assignments);
            ArrangeBlocks.Clear();
            BarCount = importedSong.Bars.Count;
            TimeSignature = "4/4";
            Tempo = importedSong.TempoBpm;
            LoopStartStep = 0;
            IsLoopEnabled = false;
            _selectedArrangePatternCell = SongArrangeRows.Count > 0 && SongArrangeRows[0].Cells.Count > 0
                ? SongArrangeRows[0].Cells[0]
                : null;
            SelectedPattern = songPattern;
            LoopEndStep = Math.Max(0, stepCount - 1);
            SelectedArrangeCell = _selectedArrangePatternCell;
            _undoStack.Clear();
            _redoStack.Clear();
        }
        finally
        {
            _isRestoringState = false;
        }

        SaveProjectCommand?.RaiseCanExecuteChanged();
        SaveProjectAsCommand?.RaiseCanExecuteChanged();
        PasteTrackCommand?.RaiseCanExecuteChanged();
        RaiseHistoryStateChanged();
        ConfigurePlaybackSource();
        RebuildTimeline();
    }

    private void StartPlayback()
    {
        _engine.Start();
        IsPlaying = true;
        StatusMessage = "Playback started";
    }

    private void StopPlayback()
    {
        _engine.Stop();
        IsPlaying = false;
        StatusMessage = "Playback paused";
    }

    private void OnStepChanged(object? sender, int stepIndex)
    {
        if (!IsPlaying)
        {
            return;
        }

        CurrentStepIndex = stepIndex;
        UpdateSongTimelinePosition(stepIndex);
        TransportPosition = TimeSpan
            .FromSeconds(stepIndex * 60.0 / Tempo / 4.0)
            .ToString(@"mm\:ss\.ff");
        UpdatePlayhead(stepIndex);
        UpdateTrackMeters(stepIndex);
    }

    private void OnPlaybackPatternChanged(object? sender, PatternViewModel pattern)
    {
        if (!IsArrangeMode || ReferenceEquals(SelectedPattern, pattern))
        {
            return;
        }

        _isFollowingArrangePlayback = true;
        try
        {
            SelectedPattern = pattern;
        }
        finally
        {
            _isFollowingArrangePlayback = false;
        }
    }

    private void OnPlaybackCompleted(object? sender, EventArgs e)
    {
        IsPlaying = false;
        _engine.SetPlaybackPosition(0);
        CurrentStepIndex = 0;
        TransportPosition = "00:00.00";
        StatusMessage = IsArrangeMode
            ? "Arrange playback completed."
            : "Pattern playback completed.";
    }

    private void OnPlaybackFaulted(object? sender, Exception exception)
    {
        IsPlaying = false;
        StatusMessage = $"Playback error: {exception.Message}";
    }

    private void ConfigurePlaybackSource()
    {
        if (IsArrangeMode && Patterns.Count > 0)
        {
            _engine.LoadSong(GetArrangePlaybackPatterns());
            return;
        }

        _engine.LoadPattern(SelectedPattern, IsLoopEnabled);
    }

    private List<PatternViewModel> GetArrangePlaybackPatterns()
    {
        var sequence = new List<PatternViewModel>();
        for (var rowIndex = 0; rowIndex < SongArrangeRows.Count;)
        {
            var slot = SongArrangeRows[rowIndex].Cells
                .FirstOrDefault(cell => !string.IsNullOrWhiteSpace(cell.Label))?.Label;
            var pattern = Patterns.FirstOrDefault(candidate =>
                string.Equals(candidate.Slot, slot, StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidate.Name, slot, StringComparison.OrdinalIgnoreCase));
            if (pattern is null)
            {
                rowIndex++;
                continue;
            }

            var assignedBars = 1;
            while (rowIndex + assignedBars < SongArrangeRows.Count)
            {
                var nextSlot = SongArrangeRows[rowIndex + assignedBars].Cells
                    .FirstOrDefault(cell => !string.IsNullOrWhiteSpace(cell.Label))?.Label;
                if (!string.Equals(nextSlot, slot, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                assignedBars++;
            }

            var repetitions = Math.Max(
                1,
                (int)Math.Ceiling(assignedBars / (double)GetPatternTimelineBarCount(pattern)));
            sequence.AddRange(Enumerable.Repeat(pattern, repetitions));
            rowIndex += assignedBars;
        }

        return sequence.Count > 0 ? sequence : Patterns.ToList();
    }

    private void RebuildTimeline()
    {
        var timelinePatterns = IsArrangeMode
            ? GetArrangePlaybackPatterns()
            : GetSelectedPatternTimelinePatterns();

        TimelineTracks.Clear();
        TimelineBarNumbers.Clear();
        _previousTimelineStep = -1;
        var timelineBarCount = timelinePatterns.Sum(GetPatternTimelineBarCount);
        for (var bar = 1; bar <= timelineBarCount; bar++)
        {
            TimelineBarNumbers.Add(bar);
        }

        var trackCount = timelinePatterns.Select(pattern => pattern.Tracks.Count).DefaultIfEmpty(0).Max();
        for (var trackIndex = 0; trackIndex < trackCount; trackIndex++)
        {
            var sourceTrack = timelinePatterns
                .Where(pattern => trackIndex < pattern.Tracks.Count)
                .Select(pattern => pattern.Tracks[trackIndex])
                .FirstOrDefault();
            if (sourceTrack is null)
            {
                continue;
            }

            var timelineSteps = new ObservableCollection<ArrangeTimelineStepViewModel>();
            var absoluteStep = 0;
            foreach (var pattern in timelinePatterns)
            {
                var track = trackIndex < pattern.Tracks.Count ? pattern.Tracks[trackIndex] : null;
                var timelineStepCount = GetPatternTimelineBarCount(pattern) * 16;
                for (var stepIndex = 0; stepIndex < timelineStepCount; stepIndex++)
                {
                    var sourceStep = track is not null && stepIndex < track.Steps.Count
                        ? track.Steps[stepIndex]
                        : null;
                    timelineSteps.Add(new ArrangeTimelineStepViewModel(absoluteStep++, sourceStep));
                }
            }

            TimelineTracks.Add(new ArrangeTimelineTrackViewModel(sourceTrack, timelineSteps));
        }

        RaisePropertyChanged(nameof(TimelineContentWidth));
        RaisePropertyChanged(nameof(TimelineWorkspaceWidth));
        UpdateSongTimelinePosition(CurrentStepIndex);
    }

    private List<PatternViewModel> GetSelectedPatternTimelinePatterns()
    {
        if (SelectedPattern is null)
        {
            return new List<PatternViewModel>();
        }

        var barCount = TryGetSelectedArrangePatternRange(out _, out _, out var length)
            ? length
            : GetPatternTimelineBarCount(SelectedPattern);
        var repetitions = Math.Max(
            1,
            (int)Math.Ceiling(barCount / (double)GetPatternTimelineBarCount(SelectedPattern)));
        return Enumerable.Repeat(SelectedPattern, repetitions).ToList();
    }

    private static int GetPatternTimelineBarCount(PatternViewModel pattern)
    {
        var stepCount = pattern.Tracks
            .Select(track => track.Steps.Count)
            .DefaultIfEmpty(16)
            .Max();
        return Math.Max(1, (stepCount + 15) / 16);
    }

    private void UpdateSongTimelinePosition(int absoluteStep)
    {
        foreach (var track in TimelineTracks)
        {
            if (_previousTimelineStep >= 0 && _previousTimelineStep < track.Steps.Count)
            {
                track.Steps[_previousTimelineStep].IsCurrent = false;
            }

            if (absoluteStep >= 0 && absoluteStep < track.Steps.Count)
            {
                track.Steps[absoluteStep].IsCurrent = true;
            }
        }
        _previousTimelineStep = absoluteStep;

        CurrentSongBarNumber = Math.Max(1, absoluteStep / 16 + 1);
        var rowIndex = Math.Clamp(CurrentSongBarNumber - 1, 0, Math.Max(0, SongArrangeRows.Count - 1));
        var playingRow = IsArrangeMode && SongArrangeRows.Count > 0
            ? SongArrangeRows
                .Take(rowIndex + 1)
                .LastOrDefault(row => VisibleSongArrangeRows.Contains(row))
            : null;
        foreach (var row in SongArrangeRows)
        {
            row.IsPlaying = ReferenceEquals(row, playingRow);
        }

        CurrentPlayingArrangeRow = playingRow;
    }

    private void ToggleStep(object? parameter)
    {
        if (parameter is not StepViewModel step)
        {
            return;
        }

        CaptureUndoStateIfNeeded();
        step.IsActive = !step.IsActive;
        if (step.IsActive && step.MidiNoteNumber is null && SelectedTrack is not null)
        {
            step.MidiNoteNumber = SelectedTrack.NoteNumber;
        }

        SelectedStep = step;
        StatusMessage = $"Step {step.Label} {(step.IsActive ? "enabled" : "disabled")}";
    }

    private void TogglePianoRollCell(object? parameter)
    {
        if (parameter is not PianoRollCellViewModel cell)
        {
            return;
        }

        var step = cell.Step;
        CaptureUndoStateIfNeeded();
        SelectedStep = step;
        if (step.IsActive && step.MidiNoteNumber == cell.MidiNoteNumber)
        {
            step.IsActive = false;
            step.MidiNoteNumber = null;
            StatusMessage = $"Removed {cell.NoteLabel} at step {step.Label}";
            return;
        }

        step.MidiNoteNumber = cell.MidiNoteNumber;
        step.IsActive = true;
        StatusMessage = $"Placed {cell.NoteLabel} at step {step.Label}";
    }

    private void CopySelectedTrack()
    {
        var track = SelectedTrack;
        if (track is null)
        {
            return;
        }

        _copiedTrackDocument = BuildTrackDocument(track);
        try
        {
            _trackClipboardService.Save(_copiedTrackDocument);
            StatusMessage = $"Copied {track.Name}";
        }
        catch (System.IO.IOException ex)
        {
            StatusMessage = $"Copied {track.Name}, but clipboard file save failed: {ex.Message}";
        }
        catch (UnauthorizedAccessException ex)
        {
            StatusMessage = $"Copied {track.Name}, but clipboard file save failed: {ex.Message}";
        }
        finally
        {
            PasteTrackCommand?.RaiseCanExecuteChanged();
        }
    }

    private void PasteTrack()
    {
        var targetTrack = SelectedTrack;
        var sourceTrack = _copiedTrackDocument;
        if (targetTrack is null || sourceTrack is null)
        {
            return;
        }

        CaptureUndoStateIfNeeded();
        ApplyTrackDocumentToTrack(targetTrack, sourceTrack);
        RefreshPianoRoll();
        StatusMessage = $"Pasted into {targetTrack.Name}";
    }

    public void PaintPianoRollCell(PianoRollCellViewModel cell, bool activate)
    {
        var step = cell.Step;
        var note = cell.MidiNoteNumber;
        var alreadyActive = step.IsActive && step.MidiNoteNumber == note;
        var alreadyEmpty = !step.IsActive && step.MidiNoteNumber is null;
        if (activate && alreadyActive)
        {
            return;
        }

        if (!activate && alreadyEmpty)
        {
            return;
        }

        CaptureUndoStateIfNeeded();
        SelectedStep = step;

        if (activate)
        {
            step.MidiNoteNumber = note;
            step.IsActive = true;
            StatusMessage = $"Placed {cell.NoteLabel} at step {step.Label}";
            return;
        }

        step.IsActive = false;
        step.MidiNoteNumber = null;
        StatusMessage = $"Removed {cell.NoteLabel} at step {step.Label}";
    }

    public void MovePlaybackByBars(int barOffset)
    {
        const int stepsPerBar = 16;
        var targetStep = Math.Clamp(
            CurrentStepIndex + barOffset * stepsPerBar,
            0,
            Math.Max(0, TimelineTotalSteps - 1));
        SeekToStep(targetStep);
    }

    public void SeekToStep(int stepIndex)
    {
        var targetStep = Math.Clamp(stepIndex, 0, Math.Max(0, TimelineTotalSteps - 1));
        if (targetStep == CurrentStepIndex)
        {
            return;
        }

        _engine.SetPlaybackPosition(targetStep);
        CurrentStepIndex = targetStep;
        UpdatePlayhead(targetStep);
        TransportPosition = TimeSpan
            .FromSeconds(targetStep * 60.0 / Tempo / 4.0)
            .ToString(@"mm\:ss\.ff");
        StatusMessage = $"Position: bar {targetStep / 16 + 1}";
    }

    public void TogglePlayback()
    {
        if (IsPlaying)
        {
            StopPlayback();
        }
        else
        {
            StartPlayback();
        }
    }

    public void RestartPlayback()
    {
        if (IsPlaying)
        {
            StopPlayback();
        }

        _engine.SetPlaybackPosition(0);
        CurrentStepIndex = 0;
        UpdatePlayhead(0);
        TransportPosition = "00:00.00";
        StartPlayback();
    }

    public void SelectArrangeCell(ArrangeCellViewModel? cell)
    {
        if (cell is not null && string.IsNullOrWhiteSpace(cell.Label))
        {
            cell = SongArrangeRows
                .FirstOrDefault(row => row.Cells.Contains(cell))?
                .Cells.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate.Label));
        }

        if (cell is not null && !string.IsNullOrWhiteSpace(cell.Label))
        {
            _selectedArrangePatternCell = cell;
            SelectedPattern = Patterns.FirstOrDefault(pattern =>
                string.Equals(pattern.Slot, cell.Label, StringComparison.OrdinalIgnoreCase));
        }

        SelectedArrangeCell = cell;
        if (!IsArrangeMode)
        {
            RebuildTimeline();
        }
    }

    public void SelectSongArrangeRow(SongArrangeRowViewModel? row)
    {
        SelectArrangeCell(row?.Cells.FirstOrDefault(cell => !string.IsNullOrWhiteSpace(cell.Label)));
    }

    public void MoveArrangePattern(
        SongArrangeRowViewModel sourceRow,
        SongArrangeRowViewModel targetRow,
        bool insertAfter)
    {
        var sourceRowIndex = SongArrangeRows.IndexOf(sourceRow);
        var targetRowIndex = SongArrangeRows.IndexOf(targetRow);
        var sourceColumn = sourceRow.Cells
            .Select((cell, index) => (cell, index))
            .FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.cell.Label))
            .index;
        var targetColumn = targetRow.Cells
            .Select((cell, index) => (cell, index))
            .FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.cell.Label))
            .index;

        if (sourceRowIndex < 0 ||
            targetRowIndex < 0 ||
            ReferenceEquals(sourceRow, targetRow) ||
            !TryGetArrangePatternRange(sourceRowIndex, sourceColumn, out var sourceStartRow, out _, out var sourceLength) ||
            !TryGetArrangePatternRange(targetRowIndex, targetColumn, out var targetStartRow, out _, out var targetLength))
        {
            return;
        }

        var insertionRow = insertAfter ? targetStartRow + targetLength : targetStartRow;
        var assignments = GetSongArrangeAssignments();
        var movedRows = assignments.GetRange(sourceStartRow, sourceLength);
        assignments.RemoveRange(sourceStartRow, sourceLength);
        if (insertionRow > sourceStartRow)
        {
            insertionRow -= sourceLength;
        }

        insertionRow = Math.Clamp(insertionRow, 0, assignments.Count);
        if (insertionRow == sourceStartRow)
        {
            return;
        }

        CaptureUndoStateIfNeeded();
        assignments.InsertRange(insertionRow, movedRows);
        RebuildSongArrangeRows(assignments);
        _selectedArrangePatternCell = SongArrangeRows[insertionRow].Cells[sourceColumn];
        SelectArrangeCell(_selectedArrangePatternCell);
        RaisePropertyChanged(nameof(SelectedArrangePatternLength));
        RaisePropertyChanged(nameof(SelectedArrangePatternStart));
        RaisePropertyChanged(nameof(SelectedArrangePatternEnd));
        RaisePropertyChanged(nameof(SelectedArrangePatternLengthDisplay));
        StatusMessage = $"Pattern moved to bar {insertionRow + 1}";
    }

    public void MoveArrangePatternToEnd(SongArrangeRowViewModel sourceRow)
    {
        var sourceRowIndex = SongArrangeRows.IndexOf(sourceRow);
        var sourceColumn = sourceRow.Cells
            .Select((cell, index) => (cell, index))
            .FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.cell.Label))
            .index;
        if (sourceRowIndex < 0 ||
            !TryGetArrangePatternRange(sourceRowIndex, sourceColumn, out var sourceStartRow, out _, out var sourceLength) ||
            sourceStartRow + sourceLength >= SongArrangeRows.Count)
        {
            return;
        }

        CaptureUndoStateIfNeeded();
        var assignments = GetSongArrangeAssignments();
        var movedRows = assignments.GetRange(sourceStartRow, sourceLength);
        assignments.RemoveRange(sourceStartRow, sourceLength);
        var insertionRow = assignments.Count;
        assignments.InsertRange(insertionRow, movedRows);
        RebuildSongArrangeRows(assignments);
        _selectedArrangePatternCell = SongArrangeRows[insertionRow].Cells[sourceColumn];
        SelectArrangeCell(_selectedArrangePatternCell);
        RaisePropertyChanged(nameof(SelectedArrangePatternLength));
        RaisePropertyChanged(nameof(SelectedArrangePatternStart));
        RaisePropertyChanged(nameof(SelectedArrangePatternEnd));
        RaisePropertyChanged(nameof(SelectedArrangePatternLengthDisplay));
        StatusMessage = $"Pattern moved to bar {insertionRow + 1}";
    }

    public bool TryGetSelectedArrangePatternEditorState(out string patternName, out string assignmentNumber, out int subChannelIndex)
    {
        patternName = string.Empty;
        assignmentNumber = string.Empty;
        subChannelIndex = 0;

        if (SelectedPattern is null || !TryGetSelectedArrangePatternRange(out _, out var column, out _))
        {
            return false;
        }

        patternName = SelectedPattern.Name;
        assignmentNumber = int.TryParse(SelectedPattern.Slot, out var slotNumber)
            ? slotNumber.ToString()
            : SelectedPattern.Slot;
        subChannelIndex = Math.Clamp(column, 0, 3);
        return true;
    }

    public bool TryApplySelectedArrangePatternEditorState(
        string patternName,
        string assignmentNumber,
        int subChannelIndex,
        out string errorMessage)
    {
        errorMessage = string.Empty;

        var targetPatternName = patternName.Trim();
        if (string.IsNullOrWhiteSpace(targetPatternName))
        {
            errorMessage = "Pattern name cannot be empty.";
            return false;
        }

        if (!int.TryParse(assignmentNumber.Trim(), out var slotNumber) || slotNumber < 1 || slotNumber > 99)
        {
            errorMessage = "Assignment number must be between 1 and 99.";
            return false;
        }

        if (subChannelIndex < 0 || subChannelIndex > 3)
        {
            errorMessage = "Sub channel must be a, b, c or d.";
            return false;
        }

        if (SelectedPattern is null || !TryGetSelectedArrangePatternRange(out var startRow, out var currentColumn, out var length))
        {
            errorMessage = "Select a pattern in ARRANGE first.";
            return false;
        }

        var selectedPattern = SelectedPattern;
        var newSlot = slotNumber.ToString("00");
        var slotConflict = Patterns.Any(pattern =>
            !ReferenceEquals(pattern, selectedPattern) &&
            string.Equals(pattern.Slot, newSlot, StringComparison.OrdinalIgnoreCase));
        if (slotConflict)
        {
            errorMessage = $"Assignment {newSlot} is already used by another pattern.";
            return false;
        }

        if (subChannelIndex != currentColumn)
        {
            for (var row = startRow; row < startRow + length; row++)
            {
                var targetLabel = SongArrangeRows[row].Cells[subChannelIndex].Label;
                if (!string.IsNullOrWhiteSpace(targetLabel))
                {
                    errorMessage = $"Sub channel {(char)('a' + subChannelIndex)} is occupied in the selected bar range.";
                    return false;
                }
            }
        }

        CaptureUndoStateIfNeeded();

        var oldSlot = selectedPattern.Slot;
        if (!string.Equals(selectedPattern.Name, targetPatternName, StringComparison.Ordinal))
        {
            selectedPattern.Name = targetPatternName;
        }

        if (!string.Equals(oldSlot, newSlot, StringComparison.OrdinalIgnoreCase))
        {
            selectedPattern.Slot = newSlot;
            foreach (var row in SongArrangeRows)
            {
                foreach (var cell in row.Cells)
                {
                    if (string.Equals(cell.Label, oldSlot, StringComparison.OrdinalIgnoreCase))
                    {
                        cell.Label = newSlot;
                    }
                }
            }
        }

        var effectiveSlot = selectedPattern.Slot;
        if (subChannelIndex != currentColumn)
        {
            for (var row = startRow; row < startRow + length; row++)
            {
                SongArrangeRows[row].Cells[currentColumn].Label = string.Empty;
                SongArrangeRows[row].Cells[currentColumn].Detail = string.Empty;
                SongArrangeRows[row].Cells[subChannelIndex].Label = effectiveSlot;
            }
        }

        _selectedArrangePatternCell = SongArrangeRows[startRow].Cells[subChannelIndex];
        SelectedArrangeCell = _selectedArrangePatternCell;
        RefreshSongArrangeRowStates();
        RaisePropertyChanged(nameof(SelectedArrangePatternLength));
        RaisePropertyChanged(nameof(SelectedArrangePatternStart));
        RaisePropertyChanged(nameof(SelectedArrangePatternEnd));
        RaisePropertyChanged(nameof(SelectedArrangePatternLengthDisplay));
        StatusMessage = $"Pattern updated: {targetPatternName}{(string.IsNullOrWhiteSpace(effectiveSlot) ? string.Empty : $" ({effectiveSlot})")} in {(char)('A' + subChannelIndex)}";
        return true;
    }

    public void SelectArrangeBlock(SongArrangeBlockViewModel? block)
    {
        SelectedArrangeBlock = block;
    }

    private void UpdateSelectedArrangeCellState()
    {
        foreach (var row in ArrangeRows)
        {
            foreach (var cell in row.Cells)
            {
                cell.IsSelected = ReferenceEquals(cell, SelectedArrangeCell);
            }
        }

        foreach (var row in SongArrangeRows)
        {
            foreach (var cell in row.Cells)
            {
                cell.IsSelected = ReferenceEquals(cell, SelectedArrangeCell);
            }
        }

        RefreshSongArrangeRowStates();
        ClearArrangeCellCommand?.RaiseCanExecuteChanged();
    }

    private void UpdateSelectedArrangeBlockState()
    {
        foreach (var block in ArrangeBlocks)
        {
            block.IsSelected = ReferenceEquals(block, SelectedArrangeBlock);
        }
    }

    private void ClearSelectedArrangeCell()
    {
        var cell = SelectedArrangeCell;
        if (cell is null)
        {
            return;
        }

        CaptureUndoStateIfNeeded();
        cell.Label = string.Empty;
        cell.Detail = string.Empty;
        RaisePropertyChanged(nameof(ArrangeCellLabel));
        RaisePropertyChanged(nameof(ArrangeCellDetail));
        StatusMessage = $"Cleared bar {cell.BarIndex}";
    }

    private void TransposeSelectedTrack(int semitones)
    {
        var track = SelectedTrack;
        if (track is null)
        {
            return;
        }

        CaptureUndoStateIfNeeded();
        track.NoteNumber = Math.Clamp(track.NoteNumber + semitones, 0, 127);
        foreach (var step in track.Steps)
        {
            if (!step.IsActive)
            {
                continue;
            }

            var note = step.MidiNoteNumber ?? track.NoteNumber;
            step.MidiNoteNumber = Math.Clamp(note + semitones, 0, 127);
        }

        if (track.MidiNoteEvents.Count > 0)
        {
            track.MidiNoteEvents = track.MidiNoteEvents
                .Select(e => e with { NoteNumber = Math.Clamp(e.NoteNumber + semitones, 0, 127) })
                .ToList();
        }

        RefreshPianoRoll();
        StatusMessage = semitones > 0 ? "Track transposed up" : "Track transposed down";
    }

    private void ToggleTrackMute(object? parameter)
    {
        if (parameter is not PatternTrackViewModel track)
        {
            return;
        }

        track.IsMuted = !track.IsMuted;
        if (track.IsMuted)
        {
            track.SignalLevel = 0;
        }
        StatusMessage = track.IsMuted ? $"{track.Name} muted" : $"{track.Name} unmuted";
    }

    private void ToggleTrackSolo(object? parameter)
    {
        if (parameter is not PatternTrackViewModel track)
        {
            return;
        }

        track.IsSolo = !track.IsSolo;
        StatusMessage = track.IsSolo ? $"{track.Name} soloed" : $"{track.Name} solo disabled";
    }

    public void SetTrackColor(PatternTrackViewModel track, string? colorHex)
    {
        if (track is null)
        {
            return;
        }

        CaptureUndoStateIfNeeded();
        track.TrackColorBrush = CreateTrackColorBrush(colorHex);
        StatusMessage = string.IsNullOrWhiteSpace(colorHex)
            ? $"{track.Name} color reset"
            : $"{track.Name} color updated";
    }

    private void QuantizeSelectedTrack()
    {
        var track = SelectedTrack;
        if (track is null)
        {
            return;
        }

        CaptureUndoStateIfNeeded();
        foreach (var step in track.Steps)
        {
            if (!step.IsActive)
            {
                continue;
            }

            if (step.MidiNoteNumber is null)
            {
                step.MidiNoteNumber = track.NoteNumber;
            }

            step.DurationSteps = 1;
            step.Velocity = 96;
        }

        RefreshPianoRoll();
        StatusMessage = "Selected track quantized";
    }

    private void ResetTrackMeters()
    {
        _trackMeters.Reset(Tracks);
        MasterSignalLevel = 0;
    }

    private void UpdateTrackMeters(int stepIndex)
    {
        MasterSignalLevel = _trackMeters.Update(Tracks, stepIndex, MasterVolume);
    }

    public bool TryGetNewTrackDefaults(
        PatternTrackType trackType,
        out string name,
        out string instrument,
        out string source,
        out int channel,
        out int noteNumber)
    {
        var pattern = SelectedPattern;
        if (pattern is null)
        {
            name = string.Empty;
            instrument = string.Empty;
            source = string.Empty;
            channel = 1;
            noteNumber = 36;
            return false;
        }

        var nextIndex = pattern.Tracks
            .Select(track => track.Name.StartsWith("Track ", StringComparison.OrdinalIgnoreCase) &&
                             int.TryParse(track.Name["Track ".Length..], out var index)
                ? index
                : 0)
            .DefaultIfEmpty(0)
            .Max() + 1;
        var highestChannel = pattern.Tracks
            .Select(track => track.Channel)
            .DefaultIfEmpty(0)
            .Max();
        channel = highestChannel < 16
            ? highestChannel + 1
            : Enumerable.Range(1, 16).FirstOrDefault(candidate =>
                pattern.Tracks.All(track => track.Channel != candidate));
        if (channel == 0)
        {
            channel = 16;
        }

        noteNumber = 36;
        name = $"Track {nextIndex}";
        instrument = trackType switch
        {
            PatternTrackType.VstInstrument => "VST Instrument",
            PatternTrackType.AudioInput => "Audio Input",
            _ => "MIDI Channel"
        };
        source = trackType switch
        {
            PatternTrackType.VstInstrument => "VSTi",
            PatternTrackType.AudioInput => "Microphone / External",
            _ => string.Empty
        };

        return true;
    }

    public bool TryAddTrack(
        PatternTrackType trackType,
        string name,
        string instrument,
        string source,
        int channel,
        int noteNumber,
        out string errorMessage)
    {
        var pattern = SelectedPattern;
        if (pattern is null)
        {
            errorMessage = "No pattern selected.";
            return false;
        }

        if (!ValidateTrackDefinition(name, channel, noteNumber, out errorMessage))
        {
            return false;
        }

        CaptureUndoStateIfNeeded();

        var track = new PatternTrackViewModel(
            name.Trim(),
            instrument,
            channel,
            noteNumber,
            BuildDefaultTrackSteps(),
            trackType: trackType,
            source: source.Trim());

        pattern.Tracks.Add(track);
        Tracks.Add(track);
        SelectedTrack = track;
        ConfigurePlaybackSource();
        RebuildTimeline();
        StatusMessage = $"{track.Name} added ({track.DisplayTrackType})";
        errorMessage = string.Empty;
        return true;
    }

    private void DeleteSelectedTrack()
    {
        var pattern = SelectedPattern;
        var track = SelectedTrack;
        if (pattern is null || track is null)
        {
            return;
        }

        var trackIndex = pattern.Tracks.IndexOf(track);
        if (trackIndex < 0)
        {
            return;
        }

        CaptureUndoStateIfNeeded();
        pattern.Tracks.RemoveAt(trackIndex);
        Tracks.Remove(track);
        SelectedTrack = pattern.Tracks.Count == 0
            ? null
            : pattern.Tracks[Math.Min(trackIndex, pattern.Tracks.Count - 1)];
        ConfigurePlaybackSource();
        RebuildTimeline();
        StatusMessage = $"{track.Name} deleted";
    }

    public bool TryUpdateTrack(
        PatternTrackViewModel track,
        PatternTrackType trackType,
        string name,
        string instrument,
        string source,
        int channel,
        int noteNumber,
        out string errorMessage)
    {
        if (!ValidateTrackDefinition(name, channel, noteNumber, out errorMessage))
        {
            return false;
        }

        CaptureUndoStateIfNeeded();
        track.Name = name.Trim();
        track.Instrument = instrument.Trim();
        track.TrackType = trackType;
        track.Source = source.Trim();
        track.Channel = channel;
        track.NoteNumber = noteNumber;
        if (track.TrackType == PatternTrackType.AudioInput)
        {
            track.MidiNoteEvents = Array.Empty<MidiNoteEventViewModel>();
            foreach (var step in track.Steps)
            {
                step.IsActive = false;
                step.MidiNoteNumber = null;
            }
        }

        if (ReferenceEquals(track, SelectedTrack))
        {
            RefreshPianoRoll();
        }

        StatusMessage = $"{track.Name} updated";
        errorMessage = string.Empty;
        return true;
    }

    private void AddTrack(object? parameter)
    {
        var trackType = parameter?.ToString()?.ToLowerInvariant() switch
        {
            "vst" => PatternTrackType.VstInstrument,
            "audio" => PatternTrackType.AudioInput,
            _ => PatternTrackType.MidiChannel
        };

        if (!TryGetNewTrackDefaults(trackType, out var name, out var instrument, out var source, out var channel, out var noteNumber))
        {
            return;
        }

        TryAddTrack(trackType, name, instrument, source, channel, noteNumber, out _);
    }

    private static bool ValidateTrackDefinition(string name, int channel, int noteNumber, out string errorMessage)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            errorMessage = "Track name is required.";
            return false;
        }

        if (channel < 1 || channel > 16)
        {
            errorMessage = "MIDI channel must be between 1 and 16.";
            return false;
        }

        if (noteNumber < 0 || noteNumber > 127)
        {
            errorMessage = "Base note must be between 0 and 127.";
            return false;
        }

        errorMessage = string.Empty;
        return true;
    }

    private static ArrangeRowViewModel CreateArrangeRow(string track, string instrument, IReadOnlyList<string> labels)
    {
        var cells = new ObservableCollection<ArrangeCellViewModel>();
        var accentColors = new[]
        {
            "#FF92D36E",
            "#FF76B9FF",
            "#FFF7C46B",
            "#FFFF8DA1"
        };

        var sectionBars = new Dictionary<int, string>
        {
            { 1, labels[0] },
            { 5, labels[1] },
            { 9, labels[2] },
            { 13, labels[3] }
        };

        for (var bar = 1; bar <= 16; bar++)
        {
            var isStart = sectionBars.TryGetValue(bar, out var label);
            var sectionLabel = isStart ? sectionBars[bar] : string.Empty;
            var accent = isStart
                ? (SolidColorBrush)new BrushConverter().ConvertFromString(accentColors[(bar - 1) / 4 % accentColors.Length])!
                : (SolidColorBrush)new BrushConverter().ConvertFromString("#FF242424")!;

            cells.Add(new ArrangeCellViewModel(
                bar,
                sectionLabel,
                isStart ? $"Bar {bar}" : string.Empty,
                accent));
        }

        return new ArrangeRowViewModel(track, instrument, cells);
    }

    private void ResetArrangeRows()
    {
        ArrangeRows.Clear();
        ArrangeRows.Add(CreateArrangeRow("Track 1", string.Empty, new[] { string.Empty, string.Empty, string.Empty, string.Empty }));
        ArrangeRows.Add(CreateArrangeRow("Track 2", string.Empty, new[] { string.Empty, string.Empty, string.Empty, string.Empty }));
        ArrangeRows.Add(CreateArrangeRow("Track 3", string.Empty, new[] { string.Empty, string.Empty, string.Empty, string.Empty }));
        ArrangeRows.Add(CreateArrangeRow("Track 4", string.Empty, new[] { string.Empty, string.Empty, string.Empty, string.Empty }));
        UpdatePlayhead(CurrentStepIndex);
    }

    private void ResetSongArrangeRows()
    {
        SongArrangeRows.Clear();
        ArrangeBarNumbers.Clear();

        if (Patterns.Count == 0)
        {
            RefreshSongArrangeRowStates();
            return;
        }

        var totalBars = Patterns.Count * ArrangementPatternLength;
        for (var bar = 1; bar <= totalBars; bar++)
        {
            ArrangeBarNumbers.Add(bar);
            var pattern = Patterns[(bar - 1) / ArrangementPatternLength];
            SongArrangeRows.Add(new SongArrangeRowViewModel(
                bar,
                CreateSongArrangeCells(bar, new[] { pattern.Slot, string.Empty, string.Empty, string.Empty })));
        }

        RefreshSongArrangeRowStates();
    }

    private void ResetArrangeBlocks()
    {
        ArrangeBlocks.Clear();

        ArrangeBlocks.Add(new SongArrangeBlockViewModel(0, 0, 2, 1, "INTRO", "5", "#FFE7E7E7"));
        ArrangeBlocks.Add(new SongArrangeBlockViewModel(2, 0, 1, 1, "BREAK", string.Empty, "#FFF0F0F0"));
        ArrangeBlocks.Add(new SongArrangeBlockViewModel(3, 0, 3, 1, "HIPHOP", "20", "#FFD9D9D9"));
        ArrangeBlocks.Add(new SongArrangeBlockViewModel(4, 1, 2, 1, "FILLS", "11", "#FFDCDCDC"));
        ArrangeBlocks.Add(new SongArrangeBlockViewModel(6, 1, 2, 1, "NEWSET", "14", "#FFE1E1E1"));
        ArrangeBlocks.Add(new SongArrangeBlockViewModel(6, 0, 2, 1, "HAPPY", "3", "#FFC9C9C9"));
        ArrangeBlocks.Add(new SongArrangeBlockViewModel(9, 2, 2, 1, "SOLO", "12", "#FFCECECE"));
        ArrangeBlocks.Add(new SongArrangeBlockViewModel(11, 0, 3, 1, "HAPPY", "3", "#FFC9C9C9"));
        ArrangeBlocks.Add(new SongArrangeBlockViewModel(12, 3, 2, 1, "SOLO", "12", "#FFCECECE"));
        ArrangeBlocks.Add(new SongArrangeBlockViewModel(14, 3, 2, 1, "SOLO", "12", "#FFCECECE"));
    }

    private static ObservableCollection<ArrangeCellViewModel> CreateSongArrangeCells(int row, IReadOnlyList<string> labels)
    {
        var palette = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "INTRO", "#FFE7E7E7" },
            { "BREAK", "#FFF0F0F0" },
            { "HIPHOP", "#FFD9D9D9" },
            { "HAPPY", "#FFC9C9C9" },
            { "FILLS", "#FFDCDCDC" },
            { "NEWSET", "#FFE1E1E1" },
            { "SOLO", "#FFCECECE" }
        };

        var cells = new ObservableCollection<ArrangeCellViewModel>();
        for (var i = 0; i < 4; i++)
        {
            var label = labels[i];
            var accent = string.IsNullOrWhiteSpace(label)
                ? "#FFFFFFFF"
                : (palette.TryGetValue(label, out var value) ? value : "#FFE7E7E7");

            cells.Add(new ArrangeCellViewModel(
                row,
                label,
                string.Empty,
                (SolidColorBrush)new BrushConverter().ConvertFromString(accent)!));
        }

        return cells;
    }

    private void MoveSelectedArrangePattern(int direction)
    {
        if (!TryGetSelectedArrangePatternRange(out var startRow, out var column, out var length))
        {
            return;
        }

        var targetRow = direction < 0 ? startRow - 1 : startRow + length;
        if (targetRow < 0 || targetRow >= SongArrangeRows.Count || !TryGetArrangePatternRange(targetRow, column, out var targetStartRow, out _, out var targetLength))
        {
            return;
        }

        CaptureUndoStateIfNeeded();
        var assignments = GetSongArrangeAssignments();
        var selectedRows = assignments.GetRange(startRow, length);
        assignments.RemoveRange(startRow, length);

        if (direction < 0)
        {
            assignments.InsertRange(targetStartRow, selectedRows);
        }
        else
        {
            assignments.InsertRange(targetStartRow + targetLength - length, selectedRows);
        }

        RebuildSongArrangeRows(assignments);
        var newStartRow = direction < 0 ? targetStartRow : targetStartRow + targetLength - length;
        _selectedArrangePatternCell = SongArrangeRows[newStartRow].Cells[column];
        SelectedArrangeCell = _selectedArrangePatternCell;
        RaisePropertyChanged(nameof(SelectedArrangePatternLength));
        RaisePropertyChanged(nameof(SelectedArrangePatternStart));
        RaisePropertyChanged(nameof(SelectedArrangePatternEnd));
        RaisePropertyChanged(nameof(SelectedArrangePatternLengthDisplay));
        StatusMessage = direction < 0 ? "Pattern moved earlier" : "Pattern moved later";
    }

    private bool CanMoveSelectedArrangePattern(int direction)
    {
        if (!TryGetSelectedArrangePatternRange(out var startRow, out var column, out var length))
        {
            return false;
        }

        var targetRow = direction < 0 ? startRow - 1 : startRow + length;
        return targetRow >= 0 &&
               targetRow < SongArrangeRows.Count &&
               TryGetArrangePatternRange(targetRow, column, out _, out _, out _);
    }

    private void ResizeSelectedArrangePattern(int requestedLength)
    {
        if (requestedLength < 1 || !TryGetSelectedArrangePatternRange(out var startRow, out var column, out var currentLength) || requestedLength == currentLength)
        {
            return;
        }

        CaptureUndoStateIfNeeded();
        var assignments = GetSongArrangeAssignments();
        var label = assignments[startRow][column];
        if (requestedLength > currentLength)
        {
            for (var count = 0; count < requestedLength - currentLength; count++)
            {
                var insertedRow = new[] { string.Empty, string.Empty, string.Empty, string.Empty };
                insertedRow[column] = label;
                assignments.Insert(startRow + currentLength + count, insertedRow);
            }
        }
        else
        {
            assignments.RemoveRange(startRow + requestedLength, currentLength - requestedLength);
        }

        RebuildSongArrangeRows(assignments);
        _selectedArrangePatternCell = SongArrangeRows[startRow].Cells[column];
        SelectedArrangeCell = _selectedArrangePatternCell;
        RaisePropertyChanged(nameof(SelectedArrangePatternLength));
        RaisePropertyChanged(nameof(SelectedArrangePatternStart));
        RaisePropertyChanged(nameof(SelectedArrangePatternEnd));
        RaisePropertyChanged(nameof(SelectedArrangePatternLengthDisplay));
        RebuildTimeline();
        StatusMessage = $"Pattern duration set to {requestedLength} bars";
    }

    private void SetSelectedArrangePatternLength(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            RaisePropertyChanged(nameof(SelectedArrangePatternLengthDisplay));
            return;
        }

        var components = value.Split(':', StringSplitOptions.TrimEntries);
        if (components.Length != 4 ||
            !int.TryParse(components[0], out var bars) ||
            !int.TryParse(components[1], out var quarters) ||
            !int.TryParse(components[2], out var eighths) ||
            !int.TryParse(components[3], out var sixteenths) ||
            bars < 1 ||
            quarters != 0 ||
            eighths != 0 ||
            sixteenths != 0)
        {
            StatusMessage = "LENGTH accepts whole bars only (for example 8:0:0:0)";
            RaisePropertyChanged(nameof(SelectedArrangePatternLengthDisplay));
            return;
        }

        ResizeSelectedArrangePattern(bars);
    }

    private void DeleteSelectedArrangePattern()
    {
        if (!TryGetSelectedArrangePatternRange(out var startRow, out _, out var length))
        {
            return;
        }

        CaptureUndoStateIfNeeded();
        var assignments = GetSongArrangeAssignments();
        assignments.RemoveRange(startRow, length);
        RebuildSongArrangeRows(assignments);
        _selectedArrangePatternCell = null;
        SelectedArrangeCell = null;
        RebuildTimeline();
        StatusMessage = "Pattern removed from arrangement";
    }

    private void CreateArrangePattern()
    {
        var patternName = Interaction.InputBox(
            "Enter the new pattern name:",
            "Create new pattern",
            $"PATTERN {Patterns.Count + 1}").Trim();
        if (string.IsNullOrWhiteSpace(patternName))
        {
            StatusMessage = "New pattern creation cancelled";
            return;
        }

        if (Patterns.Any(pattern => string.Equals(pattern.Name, patternName, StringComparison.OrdinalIgnoreCase)))
        {
            StatusMessage = $"A pattern named {patternName} already exists";
            return;
        }

        CaptureUndoStateIfNeeded();
        var insertPatternIndex = SelectedPattern is null ? Patterns.Count : Patterns.IndexOf(SelectedPattern) + 1;
        var patternSlot = GetNextAvailablePatternSlot();
        if (patternSlot is null)
        {
            StatusMessage = "No pattern identifiers available";
            return;
        }

        var pattern = CreatePattern(patternName, "New pattern", patternSlot);
        Patterns.Insert(insertPatternIndex, pattern);

        var assignments = GetSongArrangeAssignments();
        var insertBarIndex = TryGetSelectedArrangePatternRange(out var startRow, out _, out var length)
            ? startRow + length
            : assignments.Count;
        for (var bar = 0; bar < ArrangementPatternLength; bar++)
        {
            assignments.Insert(insertBarIndex + bar, new[] { patternSlot, string.Empty, string.Empty, string.Empty });
        }

        RebuildSongArrangeRows(assignments);
        _selectedArrangePatternCell = SongArrangeRows[insertBarIndex].Cells[0];
        SelectedPattern = pattern;
        SelectedArrangeCell = _selectedArrangePatternCell;
        StatusMessage = $"Created {patternName}";
    }

    private void CopySelectedArrangePattern()
    {
        var sourcePattern = SelectedPattern;
        if (sourcePattern is null || !TryGetSelectedArrangePatternRange(out var startRow, out var column, out var length))
        {
            return;
        }

        var confirmation = MessageBox.Show(
            $"Pattern \"{sourcePattern.Name}\" will be copied immediately after its current position. Continue?",
            "Copy pattern",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirmation != MessageBoxResult.Yes)
        {
            StatusMessage = "Pattern copy cancelled";
            return;
        }

        CaptureUndoStateIfNeeded();
        var copyName = CreateUniquePatternCopyName(sourcePattern.Name);
        var patternCopy = ApplyPatternDocument(BuildPatternDocument(sourcePattern));
        patternCopy.Name = copyName;
        patternCopy.Transpose = sourcePattern.Transpose;
        var insertPatternIndex = Patterns.IndexOf(sourcePattern) + 1;
        var patternSlot = GetNextAvailablePatternSlot();
        if (patternSlot is null)
        {
            StatusMessage = "No pattern identifiers available";
            return;
        }

        patternCopy.Slot = patternSlot;
        Patterns.Insert(insertPatternIndex, patternCopy);

        var assignments = GetSongArrangeAssignments();
        var insertBarIndex = startRow + length;
        for (var bar = 0; bar < length; bar++)
        {
            var insertedRow = new[] { string.Empty, string.Empty, string.Empty, string.Empty };
            insertedRow[column] = patternSlot;
            assignments.Insert(insertBarIndex + bar, insertedRow);
        }

        RebuildSongArrangeRows(assignments);
        _selectedArrangePatternCell = SongArrangeRows[insertBarIndex].Cells[column];
        SelectedPattern = patternCopy;
        SelectedArrangeCell = _selectedArrangePatternCell;
        StatusMessage = $"Copied {sourcePattern.Name} as {copyName}";
    }

    private bool CanCopySelectedArrangePattern()
    {
        return SelectedPattern is not null &&
               TryGetSelectedArrangePatternRange(out var startRow, out var column, out var length) &&
               startRow >= 0 &&
               column >= 0 &&
               length > 0;
    }

    private string CreateUniquePatternCopyName(string sourceName)
    {
        var copyNumber = 1;
        var copyName = $"{sourceName} COPY";
        while (Patterns.Any(pattern => string.Equals(pattern.Name, copyName, StringComparison.OrdinalIgnoreCase)))
        {
            copyNumber++;
            copyName = $"{sourceName} COPY {copyNumber}";
        }

        return copyName;
    }

    private string? GetNextAvailablePatternSlot()
    {
        var usedSlots = new HashSet<string>(Patterns.Select(pattern => pattern.Slot), StringComparer.OrdinalIgnoreCase);
        for (var identifier = 1; identifier <= 99; identifier++)
        {
            var slot = identifier.ToString("00");
            if (!usedSlots.Contains(slot))
            {
                return slot;
            }
        }

        return null;
    }

    private bool CanDeleteSelectedArrangePattern()
    {
        return TryGetSelectedArrangePatternRange(out var startRow, out var column, out var length) &&
               startRow >= 0 &&
               column >= 0 &&
               length > 0;
    }

    private bool TryGetSelectedArrangePatternRange(out int startRow, out int column, out int length)
    {
        startRow = -1;
        column = -1;
        length = 0;
        var selectedCell = _selectedArrangePatternCell ?? SelectedArrangeCell;

        if (selectedCell is not null)
        {
            for (var row = 0; row < SongArrangeRows.Count; row++)
            {
                var candidateColumn = SongArrangeRows[row].Cells.IndexOf(selectedCell);
                if (candidateColumn >= 0)
                {
                    return TryGetArrangePatternRange(row, candidateColumn, out startRow, out column, out length);
                }
            }
        }

        var selectedPatternName = SelectedPattern?.Name;
        if (string.IsNullOrWhiteSpace(selectedPatternName))
        {
            return false;
        }

        for (var row = 0; row < SongArrangeRows.Count; row++)
        {
            for (var candidateColumn = 0; candidateColumn < SongArrangeRows[row].Cells.Count; candidateColumn++)
            {
                if (string.Equals(
                    SongArrangeRows[row].Cells[candidateColumn].Label,
                    selectedPatternName,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return TryGetArrangePatternRange(row, candidateColumn, out startRow, out column, out length);
                }
            }
        }

        return false;
    }

    private bool TryGetArrangePatternRange(int row, int column, out int startRow, out int rangeColumn, out int length)
    {
        startRow = -1;
        rangeColumn = column;
        length = 0;
        var label = SongArrangeRows[row].Cells[column].Label;
        if (string.IsNullOrWhiteSpace(label))
        {
            return false;
        }

        startRow = row;
        while (startRow > 0 && string.Equals(SongArrangeRows[startRow - 1].Cells[column].Label, label, StringComparison.OrdinalIgnoreCase))
        {
            startRow--;
        }

        var endRow = row;
        while (endRow + 1 < SongArrangeRows.Count && string.Equals(SongArrangeRows[endRow + 1].Cells[column].Label, label, StringComparison.OrdinalIgnoreCase))
        {
            endRow++;
        }

        length = endRow - startRow + 1;
        return true;
    }

    private List<string[]> GetSongArrangeAssignments()
    {
        return SongArrangeRows
            .Select(row => row.Cells.Select(cell => cell.Label).ToArray())
            .ToList();
    }

    private List<SongArrangeBlockDocument> GetSongArrangeBlocks()
    {
        return ArrangeBlocks
            .Select(block => new SongArrangeBlockDocument(
                block.Row,
                block.Column,
                block.RowSpan,
                block.ColumnSpan,
                block.Label,
                block.Detail,
                block.Accent))
            .ToList();
    }

    private void RebuildSongArrangeRows(IReadOnlyList<string[]> assignments)
    {
        SongArrangeRows.Clear();
        VisibleSongArrangeRows.Clear();
        ArrangeBarNumbers.Clear();
        for (var index = 0; index < assignments.Count; index++)
        {
            var bar = index + 1;
            ArrangeBarNumbers.Add(bar);
            SongArrangeRows.Add(new SongArrangeRowViewModel(bar, CreateSongArrangeCells(bar, assignments[index])));
        }

        RefreshSongArrangeRowStates();
    }

    private void RefreshSongArrangeRowStates()
    {
        var selectedStartRow = TryGetSelectedArrangePatternRange(out var startRow, out _, out _)
            ? startRow
            : -1;

        for (var rowIndex = 0; rowIndex < SongArrangeRows.Count; rowIndex++)
        {
            var row = SongArrangeRows[rowIndex];
            row.IsSelected = rowIndex == selectedStartRow;
            var isPatternStart = false;
            for (var column = 0; column < row.Cells.Count; column++)
            {
                var cell = row.Cells[column];
                if (!string.IsNullOrWhiteSpace(cell.Label) &&
                    (rowIndex == 0 || !string.Equals(
                        SongArrangeRows[rowIndex - 1].Cells[column].Label,
                        cell.Label,
                        StringComparison.OrdinalIgnoreCase)))
                {
                    isPatternStart = true;
                    break;
                }
            }

            row.DisplayBarNumber = isPatternStart ? row.RowIndex.ToString() : string.Empty;
            var referencedPattern = Patterns.FirstOrDefault(pattern =>
                string.Equals(pattern.Slot, row.PatternName, StringComparison.OrdinalIgnoreCase));
            row.DisplayPatternName = isPatternStart ? referencedPattern?.Name ?? row.PatternName : string.Empty;
            for (var column = 0; column < row.Cells.Count; column++)
            {
                var cell = row.Cells[column];
                var isCellPatternStart = !string.IsNullOrWhiteSpace(cell.Label) &&
                    (rowIndex == 0 || !string.Equals(
                        SongArrangeRows[rowIndex - 1].Cells[column].Label,
                        cell.Label,
                        StringComparison.OrdinalIgnoreCase));
                cell.DisplayLabel = isCellPatternStart ? cell.Label : string.Empty;
            }
        }

        VisibleSongArrangeRows.Clear();
        foreach (var row in SongArrangeRows.Where(row => !string.IsNullOrWhiteSpace(row.DisplayBarNumber)))
        {
            VisibleSongArrangeRows.Add(row);
        }
    }

    private void UpdatePlayhead(int stepIndex)
    {
        var currentBar = stepIndex + 1;
        foreach (var row in ArrangeRows)
        {
            foreach (var cell in row.Cells)
            {
                cell.IsPlayhead = cell.BarIndex == currentBar;
            }
        }

        foreach (var row in PianoRollRows)
        {
            if (_previousPianoRollStep >= 0 && _previousPianoRollStep < row.Cells.Count)
            {
                row.Cells[_previousPianoRollStep].IsPlayhead = false;
            }

            if (stepIndex >= 0 && stepIndex < row.Cells.Count)
            {
                row.Cells[stepIndex].IsPlayhead = true;
            }
        }
        _previousPianoRollStep = stepIndex;
    }

    private void RefreshPianoRoll()
    {
        foreach (var row in PianoRollRows)
        {
            foreach (var cell in row.Cells)
            {
                cell.Dispose();
            }
        }

        var track = SelectedTrack;
        if (track is null)
        {
            PianoRollRows = new ObservableCollection<PianoRollRowViewModel>();
            return;
        }

        var rows = new ObservableCollection<PianoRollRowViewModel>();
        _previousPianoRollStep = -1;
        var topNote = track.NoteNumber + 5;

        for (var midiNote = topNote; midiNote >= topNote - 11; midiNote--)
        {
            var cells = new ObservableCollection<PianoRollCellViewModel>();
            foreach (var step in track.Steps)
            {
                cells.Add(new PianoRollCellViewModel(step, midiNote));
            }

            rows.Add(new PianoRollRowViewModel(midiNote, cells));
        }

        PianoRollRows = rows;
        UpdatePlayhead(CurrentStepIndex);
    }

    private ProjectDocument BuildProjectDocument()
    {
        return new ProjectDocument(
            "1.4",
            Tempo,
            BarCount,
            TimeSignature,
            SelectedPattern?.Slot,
            SelectedMidiOutputDevice?.DisplayName,
            LoopStartStep,
            LoopEndStep,
            Patterns.Select(BuildPatternDocument).ToList(),
            ArrangeRows.Select(BuildArrangeRowDocument).ToList(),
            GetSongArrangeAssignments(),
            GetSongArrangeBlocks(),
            MasterVolume);
    }

    private void RestoreProjectDocument(ProjectDocument document)
    {
        _isRestoringState = true;
        try
        {
            CurrentStepIndex = 0;
            TransportPosition = "00:00.00";
            Tempo = document.Tempo;
            BarCount = document.BarCount;
            TimeSignature = document.TimeSignature;
            MasterVolume = document.MasterVolume;

            Patterns.Clear();
            foreach (var patternDocument in document.Patterns)
            {
                Patterns.Add(ApplyPatternDocument(patternDocument));
            }

            ArrangeRows.Clear();
            foreach (var arrangeDocument in document.ArrangeRows)
            {
                ArrangeRows.Add(ApplyArrangeRowDocument(arrangeDocument));
            }

            if (document.SongGrid != null)
            {
                RebuildSongArrangeRows(document.SongGrid);
            }
            else
            {
                ResetSongArrangeRows();
            }

            if (document.ArrangeBlocks != null)
            {
                RebuildArrangeBlocks(document.ArrangeBlocks);
            }
            else
            {
                ResetArrangeBlocks();
            }

            SelectedArrangeCell = null;
            SelectedPattern = Patterns.FirstOrDefault(pattern => pattern.Slot == document.SelectedPatternSlot) ?? Patterns.FirstOrDefault();
            if (IsLegacyProject(document.Version))
            {
                LoopStartStep = 0;
                LoopEndStep = 15;
            }
            else
            {
                LoopStartStep = document.LoopStartStep;
                LoopEndStep = document.LoopEndStep;
            }

            SelectedMidiOutputDevice = MidiOutputDevices.FirstOrDefault(device => device.DisplayName == document.SelectedMidiOutputDeviceName)
                ?? MidiOutputDevices.FirstOrDefault();
            SaveProjectCommand?.RaiseCanExecuteChanged();
            SaveProjectAsCommand?.RaiseCanExecuteChanged();
            PasteTrackCommand?.RaiseCanExecuteChanged();
            RaiseHistoryStateChanged();
        }
        finally
        {
            _isRestoringState = false;
        }
    }

    private void MovePattern(object? parameter, int offset)
    {
        if (parameter is not PatternViewModel pattern)
        {
            return;
        }

        var currentIndex = Patterns.IndexOf(pattern);
        var newIndex = currentIndex + offset;
        if (currentIndex < 0 || newIndex < 0 || newIndex >= Patterns.Count)
        {
            return;
        }

        CaptureUndoStateIfNeeded();
        Patterns.Move(currentIndex, newIndex);
        SelectedPattern = pattern;
    }

    private bool CanMovePattern(object? parameter, int offset)
    {
        return parameter is PatternViewModel pattern && Patterns.IndexOf(pattern) + offset >= 0 && Patterns.IndexOf(pattern) + offset < Patterns.Count;
    }
}
