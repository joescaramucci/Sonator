using System.Diagnostics;
using System.Collections.ObjectModel;
using System.IO;

using System.Linq;
using Melanchall.DryWetMidi.Standards;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Microsoft.VisualBasic;
using Sonator.Models;
using Sonator.Services;
using Sonator.Services.Effects;
using Sonator.Services.Vst3;

using static Sonator.ViewModels.MidiImportTrackBuilder;
using static Sonator.ViewModels.PatternFactory;
using static Sonator.ViewModels.TimelineFormatting;
using static Sonator.ViewModels.ProjectDocumentMapper;

namespace Sonator.ViewModels;

public sealed record CountInOption(int Bars, string Label);

public sealed record ClickOption(int ClicksPerBeat, string Label);

public sealed class MainViewModel : ViewModelBase, IDisposable
{
    private const int ArrangementPatternLength = 8;
    public const double TimelineTrackHeaderWidth = 260;
    private readonly SequencerEngine _engine;
    private readonly ProjectFileService _projectFileService;
    private readonly TrackClipboardService _trackClipboardService;
    private readonly MidiOutputDeviceService _midiOutputDeviceService;
    private readonly MidiFileImportService _midiFileImportService;
    private readonly MidiInputService _midiInput = new();
    private readonly MetronomeClickPlayer _clickPlayer = new();
    private readonly Dictionary<int, (double StartStep, int Velocity)> _recordingNotes = new();
    private readonly DispatcherTimer _recordRefreshTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private PatternTrackViewModel? _recordRefreshTrack;
    private bool _recordTakeHasNotes;
    private int _playbackStartStep;

    // Grid recorded notes snap to, in steps. Null means no quantization: notes keep the exact time they were played.
    public IReadOnlyList<RecordGrid> RecordGrids { get; } = new[]
    {
        new RecordGrid(null, "Off"),
        new RecordGrid(1.0, "1/16"),
        new RecordGrid(2.0 / 3.0, "1/16T"),
        new RecordGrid(0.5, "1/32"),
        new RecordGrid(4.0 / 3.0, "1/8T"),
        new RecordGrid(2.0, "1/8"),
        new RecordGrid(4.0, "1/4"),
    };

    private RecordGrid _recordGrid = new(null, "Off");

    public RecordGrid RecordGrid
    {
        get => _recordGrid;
        set
        {
            if (SetProperty(ref _recordGrid, value))
            {
                RaisePropertyChanged(nameof(RecordGridText));
            }
        }
    }

    public string RecordGridText => $"QUANT {RecordGrid.Label}";

    public RelayCommand CycleRecordGridCommand { get; }

    private double? SnapToRecordGrid(double position)
    {
        return RecordGrid.Steps is { } grid ? Math.Round(position / grid) * grid : null;
    }
    private readonly DispatcherTimer _midiActivityTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private bool _isMidiActivityVisible;
    private readonly IVst3PluginHost _liveInstrumentHost = new Vst3PluginHost();
    private bool _isLiveKeyboardEnabled = true;
    private CountInOption _selectedCountIn = new(1, "1 bar");
    private int _snapMode; // 0 bar, 1 beat, 2 sixteenth
    private ClickOption _selectedClickOption = new(1, "Quarter");
    private readonly TrackEffectsHost _trackEffects = new();
    private readonly TrackInstrumentsHost _trackInstruments;
    private readonly VstPluginService _vstPluginService;
    private readonly VstPluginScanner _vstPluginScanner;
    private readonly AudioOutputDeviceService _audioOutputDeviceService;
    private readonly AsioDriverService _asioDriverService = new();
    private bool _isAsioBackend;
    private readonly AppSettingsService _appSettingsService;
    private readonly UndoHistory<ProjectDocument> _history = new();
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
    private const double MinTempoBpm = 1;
    private const double MaxTempoBpm = 999;
    private double _tempo = 120;
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
    private string _transportPosition = TransportClock.Zero;
    private string _statusMessage = "Ready";
    private string? _currentProjectPath;
    private string? _vstPluginsPath;
    private double _timelineBarWidth = 180;
    private readonly Dictionary<string, AudioWaveformSummary> _audioWaveforms = new(StringComparer.OrdinalIgnoreCase);
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
        AppSettingsService? appSettingsService = null,
        AudioOutputDeviceService? audioOutputDeviceService = null)
    {
        _trackInstruments = new TrackInstrumentsHost(_liveInstrumentHost, track => _trackEffects.ChainFor(track));
        _engine = engine ?? new SequencerEngine();
        _projectFileService = projectFileService ?? new ProjectFileService();
        _trackClipboardService = trackClipboardService ?? new TrackClipboardService();
        _midiOutputDeviceService = midiOutputDeviceService ?? new MidiOutputDeviceService();
        _midiFileImportService = midiFileImportService ?? new MidiFileImportService();
        _vstPluginService = vstPluginService ?? new VstPluginService();
        _vstPluginScanner = new VstPluginScanner(_vstPluginService.ScanDirectory, Directory.Exists);
        _appSettingsService = appSettingsService ?? new AppSettingsService();
        _audioOutputDeviceService = audioOutputDeviceService ?? new AudioOutputDeviceService();
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
        AudioOutputDevices = new ObservableCollection<AudioOutputDeviceViewModel>();
        AsioDrivers = new ObservableCollection<AudioOutputDeviceViewModel>();
        IsAsioSupported = DetectAsioSupport();
        SelectAudioBackendCommand = new RelayCommand(parameter => SelectAudioBackend(parameter as string));
        SelectAsioDriverCommand = new RelayCommand(parameter => SelectAsioDriver(parameter as AudioOutputDeviceViewModel));
        SelectAudioOutputCommand = new RelayCommand(parameter => SelectAudioOutputDevice(parameter as AudioOutputDeviceViewModel));
        RefreshAudioOutputsCommand = new RelayCommand(_ => RefreshAudioOutputDevices());
        MidiOutputDevices = new ObservableCollection<MidiOutputDeviceViewModel>(
            new[] { new MidiOutputDeviceViewModel(null, "Off", isOff: true) }
                .Concat(_midiOutputDeviceService.GetOutputDevices()
                    .Select(device => new MidiOutputDeviceViewModel(device.DeviceId, device.DisplayName))));
        MidiInputDevices = new ObservableCollection<MidiInputDeviceViewModel>();
        ReloadMidiInputDevices();
        _engine.PluginNoteRouter = RouteSequencerNoteToPlugin;
        _clickPlayer.Start();
        _engine.ClickSound = _clickPlayer.Trigger;
        _midiInput.NoteReceived += OnKeyboardNote;
        _midiInput.MessageReceived += packed => Application.Current?.Dispatcher.BeginInvoke(() => OnMidiMessageReceived(packed));
        _midiActivityTimer.Tick += (_, _) => IsMidiActivityVisible = false;
        _audioMeterTimer.Tick += (_, _) => ShowAudioLevels();
        _audioMeterTimer.Start();
        _recordRefreshTimer.Tick += (_, _) => RefreshRecordedTrack();
        PreviewClickCommand = new RelayCommand(_ =>
        {
            if (_engine.IsClickLoopRunning)
            {
                _engine.StopClickLoop();
            }
            else
            {
                _engine.StartClickLoop();
            }
            RaisePropertyChanged(nameof(IsClickLoopRunning));
        });
        CycleClickOptionCommand = new RelayCommand(_ => SelectedClickOption = NextOption(ClickOptions, SelectedClickOption));
        CycleCountInCommand = new RelayCommand(_ => SelectedCountIn = NextOption(CountInOptions, SelectedCountIn));
        SelectMidiInputCommand = new RelayCommand(parameter =>
        {
            if (parameter is MidiInputDeviceViewModel device)
            {
                SelectMidiInput(device);
                SaveMidiInputChoice(device);
            }
        });

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
            out var refreshMidiDevicesCommand,
            out var browseVstPluginsPathCommand);

        PlayCommand = playCommand;
        StopCommand = stopCommand;
        ToggleRecordCommand = toggleRecordCommand;
        CycleRecordGridCommand = new RelayCommand(_ => RecordGrid = NextRecordGrid());
        ToggleTrackRecordArmCommand = new RelayCommand(parameter => ToggleTrackRecordArm(parameter as PatternTrackViewModel));
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
        RefreshMidiDevicesCommand = refreshMidiDevicesCommand;
        BrowseVstPluginsPathCommand = browseVstPluginsPathCommand;
        ToggleArrangeModeCommand = new RelayCommand(_ => IsArrangeMode = !IsArrangeMode);
        ToggleLoopCommand = new RelayCommand(_ => IsLoopEnabled = !IsLoopEnabled);
        ZoomTimelineInCommand = new RelayCommand(_ => TimelineBarWidth += 20);
        ZoomTimelineOutCommand = new RelayCommand(_ => TimelineBarWidth -= 20);
        ZoomToFitCommand = new RelayCommand(_ => ZoomToFit());
        CycleSnapCommand = new RelayCommand(_ =>
        {
            _snapMode = (_snapMode + 1) % 3;
            RaisePropertyChanged(nameof(SnapText));
        });
        DeleteTrackCommand = new RelayCommand(
            _ => DeleteSelectedTrack(),
            _ => SelectedPattern is not null && SelectedTrack is not null);

        ResetTrackCommand = new RelayCommand(_ => ResetSelectedTrack(), _ => SelectedTrack is not null);
        InitializeInitialSelection();
        InitializeVstPlugins();
        RefreshAudioOutputDevices();
        MarkProjectSaved();
    }

    // The project as it was last saved, opened or created, to tell whether closing would lose changes.
    private string? _savedProjectSnapshot;

    public bool HasUnsavedChanges => ProjectSnapshot() != _savedProjectSnapshot;

    private void MarkProjectSaved() => _savedProjectSnapshot = ProjectSnapshot();

    // Which pattern is selected and the MIDI output are not edits to the song.
    private string ProjectSnapshot() => System.Text.Json.JsonSerializer.Serialize(
        BuildProjectDocument() with { SelectedPatternSlot = null, SelectedMidiOutputDeviceName = null });

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
        var scanResult = _vstPluginScanner.Scan(DefaultVstDirectory, _vstPluginsPath);
        var settings = _appSettingsService.Load();
        var plugins = VstPluginListRules.Arrange(
            scanResult.Plugins.Select(plugin => (plugin.Name, plugin.FilePath, plugin.Kind)),
            settings.AddedVstPluginFiles,
            settings.RemovedVstPlugins);

        // Only plug-ins that work are listed. Those already known to work show at once; new or updated
        // ones are checked in the background and join the list as they pass.
        VstPlugins.Clear();
        UnavailableVstPluginCount = 0;
        var generation = ++_vstScanGeneration;
        var toCheck = new List<VstPluginViewModel>();
        foreach (var (name, filePath, kind) in plugins)
        {
            var plugin = new VstPluginViewModel(name, filePath, kind);
            switch (_vstPluginCheck.KnownResult(filePath))
            {
                case true:
                    VstPlugins.Add(plugin);
                    break;
                case false:
                    UnavailableVstPluginCount++;
                    break;
                default:
                    toCheck.Add(plugin);
                    break;
            }
        }

        RemovedVstPluginCount = settings.RemovedVstPlugins.Count;
        if (toCheck.Count > 0)
        {
            _ = CheckNewVstPluginsAsync(toCheck, generation, scanResult.ScannedFolders);
            return;
        }

        ReportVstPlugins(scanResult.ScannedFolders);
    }

    private readonly Vst3PluginCheckService _vstPluginCheck = new();
    private int _vstScanGeneration;
    private int _unavailableVstPluginCount;

    // Plug-ins left out of the list because they do not load in Sonator.
    public int UnavailableVstPluginCount
    {
        get => _unavailableVstPluginCount;
        private set => SetProperty(ref _unavailableVstPluginCount, value);
    }

    private async Task CheckNewVstPluginsAsync(IReadOnlyList<VstPluginViewModel> plugins, int generation, IReadOnlyList<string> scannedFolders)
    {
        var checkedCount = 0;
        await Task.WhenAll(plugins.Select(async plugin =>
        {
            var works = await _vstPluginCheck.WorksAsync(plugin.FilePath);
            if (generation != _vstScanGeneration)
            {
                return; // a newer scan has replaced this one
            }

            checkedCount++;
            if (works == false)
            {
                UnavailableVstPluginCount++;
            }
            else
            {
                InsertByName(plugin);
            }

            StatusMessage = $"Checking VST plug-ins: {checkedCount} of {plugins.Count}";
        }));

        if (generation == _vstScanGeneration)
        {
            ReportVstPlugins(scannedFolders);
        }
    }

    private void InsertByName(VstPluginViewModel plugin)
    {
        var index = 0;
        while (index < VstPlugins.Count && string.Compare(VstPlugins[index].Name, plugin.Name, StringComparison.CurrentCultureIgnoreCase) < 0)
        {
            index++;
        }

        VstPlugins.Insert(index, plugin);
    }

    private void ReportVstPlugins(IReadOnlyList<string> scannedFolders)
    {
        var scannedFoldersText = scannedFolders.Count > 0 ? string.Join(", ", scannedFolders) : "no folders";
        var leftOut = UnavailableVstPluginCount > 0 ? $"; {UnavailableVstPluginCount} that do not load are left out" : string.Empty;
        StatusMessage = VstPlugins.Count > 0
            ? $"Found {VstPlugins.Count} working VST plug-ins{leftOut} ({scannedFoldersText})"
            : $"No working VST plug-ins found{leftOut} ({scannedFoldersText})";
    }

    // Checks every plug-in again, as after installing or licensing one.
    public void CheckVstPluginsAgain()
    {
        _vstPluginCheck.Forget();
        RescanVstPlugins();
    }

    private int _removedVstPluginCount;

    // How many plug-ins were taken out of the list, for "Restore removed plug-ins".
    public int RemovedVstPluginCount
    {
        get => _removedVstPluginCount;
        private set => SetProperty(ref _removedVstPluginCount, value);
    }

    // Takes a plug-in out of the list, such as one that does not load. Its file is left where it is,
    // and "Restore removed plug-ins" brings it back.
    public void RemoveVstPluginFromList(VstPluginViewModel plugin)
    {
        var settings = _appSettingsService.Load();
        if (!settings.RemovedVstPlugins.Contains(plugin.FilePath, StringComparer.OrdinalIgnoreCase))
        {
            settings.RemovedVstPlugins.Add(plugin.FilePath);
        }

        settings.AddedVstPluginFiles.RemoveAll(path => string.Equals(path, plugin.FilePath, StringComparison.OrdinalIgnoreCase));
        _appSettingsService.Save(settings);
        RescanVstPlugins();
        StatusMessage = $"{plugin.Name} removed from the plug-in list (the file is still on disk)";
    }

    // Adds one plug-in file to the list, from anywhere on disk.
    public void AddVstPluginFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Add a VST3 plug-in",
            Filter = "VST3 plug-ins (*.vst3)|*.vst3",
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var settings = _appSettingsService.Load();
        settings.RemovedVstPlugins.RemoveAll(path => string.Equals(path, dialog.FileName, StringComparison.OrdinalIgnoreCase));
        if (!settings.AddedVstPluginFiles.Contains(dialog.FileName, StringComparer.OrdinalIgnoreCase))
        {
            settings.AddedVstPluginFiles.Add(dialog.FileName);
        }

        _appSettingsService.Save(settings);
        RescanVstPlugins();
        StatusMessage = $"{Path.GetFileNameWithoutExtension(dialog.FileName)} added to the plug-in list";
    }

    public void AddVstPluginFolder() => BrowseVstPluginsPath();

    // Puts every removed plug-in back in the list.
    public void RestoreRemovedVstPlugins()
    {
        var settings = _appSettingsService.Load();
        var count = settings.RemovedVstPlugins.Count;
        settings.RemovedVstPlugins.Clear();
        _appSettingsService.Save(settings);
        RescanVstPlugins();
        StatusMessage = $"{count} plug-in(s) back in the list";
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
        var settings = _appSettingsService.Load();
        settings.VstPluginsPath = VstPluginsPath;
        _appSettingsService.Save(settings);
        RescanVstPlugins();
    }

    private void RefreshAudioOutputDevices()
    {
        var settings = _appSettingsService.Load();
        var savedDeviceId = settings.AudioOutputDeviceId;
        AudioOutputDevices.Clear();
        AudioOutputDevices.Add(new AudioOutputDeviceViewModel(null, "System default"));
        foreach (var device in _audioOutputDeviceService.GetOutputDevices())
        {
            AudioOutputDevices.Add(new AudioOutputDeviceViewModel(device.Id, device.Name));
        }

        // A saved device that is not connected stays selected, so it works again when it returns.
        var savedDeviceFound = savedDeviceId is null || AudioOutputDevices.Any(device => device.DeviceId == savedDeviceId);
        if (!savedDeviceFound)
        {
            AudioOutputDevices.Add(new AudioOutputDeviceViewModel(savedDeviceId, "Saved device (not connected)"));
        }

        foreach (var device in AudioOutputDevices)
        {
            device.IsSelected = device.DeviceId == savedDeviceId;
        }

        AsioDrivers.Clear();
        foreach (var driver in _asioDriverService.GetInstalledDrivers())
        {
            AsioDrivers.Add(new AudioOutputDeviceViewModel(driver.Clsid, driver.Name)
            {
                IsSelected = driver.Clsid == settings.AsioDriverClsid
            });
        }

        IsAsioBackend = IsAsioSupported && AudioOutputSelection.FromSettings(settings).UseAsio;
    }

    private void SelectAudioOutputDevice(AudioOutputDeviceViewModel? device)
    {
        if (device is null)
        {
            return;
        }

        foreach (var candidate in AudioOutputDevices)
        {
            candidate.IsSelected = ReferenceEquals(candidate, device);
        }

        var settings = _appSettingsService.Load();
        settings.AudioOutputDeviceId = device.DeviceId;
        _appSettingsService.Save(settings);
        StatusMessage = $"Audio output: {device.DisplayName}. It applies the next time a plug-in starts audio.";
    }

    private void SelectAudioBackend(string? backendName)
    {
        var useAsio = string.Equals(backendName, AudioOutputSelection.AsioBackendName, StringComparison.Ordinal);
        if (useAsio && !IsAsioSupported)
        {
            StatusMessage = "ASIO is not included in this build of the VST3 bridge.";
            return;
        }

        var settings = _appSettingsService.Load();
        settings.AudioBackend = useAsio ? AudioOutputSelection.AsioBackendName : "WASAPI";
        _appSettingsService.Save(settings);
        IsAsioBackend = useAsio;
        StatusMessage = $"Audio backend: {(useAsio ? "ASIO" : "WASAPI")}. It applies the next time a plug-in starts audio.";
    }

    private void SelectAsioDriver(AudioOutputDeviceViewModel? driver)
    {
        if (driver is null)
        {
            return;
        }

        foreach (var candidate in AsioDrivers)
        {
            candidate.IsSelected = ReferenceEquals(candidate, driver);
        }

        var settings = _appSettingsService.Load();
        settings.AsioDriverClsid = driver.DeviceId;
        _appSettingsService.Save(settings);
        StatusMessage = $"ASIO driver: {driver.DisplayName}. It applies the next time a plug-in starts audio.";
    }

    private static bool DetectAsioSupport()
    {
        try
        {
            return Vst3HostInterop.VstHostBridge_IsAsioSupported() != 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            // An older bridge without the ASIO exports: ASIO is simply not offered.
            return false;
        }
    }

    private void InitializeInitialSelection()
    {
        SelectedMidiOutputDevice = MidiOutputDevices.FirstOrDefault(device => device.IsOff) ?? MidiOutputDevices.FirstOrDefault();
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
        out RelayCommand refreshMidiDevicesCommand,
        out RelayCommand browseVstPluginsPathCommand)
    {
        playCommand = new RelayCommand(_ => StartPlayback(), _ => SelectedPattern is not null && !IsPlaying);
        // STOP with nothing playing silences every plug-in, for a note left sounding.
        stopCommand = new RelayCommand(_ =>
        {
            if (IsPlaying)
            {
                StopPlayback();
                return;
            }

            ReleaseAllPluginNotes();
            StatusMessage = "All notes off";
        });
        toggleRecordCommand = new RelayCommand(_ =>
        {
            SetRecording(!IsRecording);
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
        undoCommand = new RelayCommand(_ => Undo(), _ => _history.CanUndo);
        redoCommand = new RelayCommand(_ => Redo(), _ => _history.CanRedo);
        saveProjectCommand = new RelayCommand(_ => SaveProject(), _ => SelectedPattern is not null);
        saveProjectAsCommand = new RelayCommand(_ => SaveProjectAs(), _ => SelectedPattern is not null);
        loadProjectCommand = new RelayCommand(_ => LoadProject());
        importMidiCommand = new RelayCommand(_ => ImportMidiFile());
        newProjectCommand = new RelayCommand(_ => CreateNewProject());
        addTrackCommand = new RelayCommand(parameter => AddTrack(parameter), _ => SelectedPattern is not null);
        transposeUpCommand = new RelayCommand(_ => TransposeSelectedTrack(1), _ => SelectedTrack is not null);
        transposeDownCommand = new RelayCommand(_ => TransposeSelectedTrack(-1), _ => SelectedTrack is not null);
        refreshMidiDevicesCommand = new RelayCommand(_ =>
        {
            ReloadMidiOutputDevices();
            ReloadMidiInputDevices();
        });
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

    public ObservableCollection<AudioOutputDeviceViewModel> AudioOutputDevices { get; }

    public ObservableCollection<AudioOutputDeviceViewModel> AsioDrivers { get; }

    public bool IsAsioSupported { get; }

    public bool IsAsioBackend
    {
        get => _isAsioBackend;
        private set
        {
            if (SetProperty(ref _isAsioBackend, value))
            {
                RaisePropertyChanged(nameof(IsWasapiBackend));
            }
        }
    }

    public bool IsWasapiBackend => !IsAsioBackend;

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
            if (!ReferenceEquals(value, _selectedBlockTrack))
            {
                ClearBlockSelection();
            }

            if (SetProperty(ref _selectedTrack, value))
            {
                foreach (var track in Tracks)
                {
                    track.IsSelected = ReferenceEquals(track, value);
                }

                SelectedStep = value?.Steps.FirstOrDefault(step => step.IsActive) ?? value?.Steps.FirstOrDefault();
                PointKeyboardAtSelectedTrack();
                StartSelectedTrackInstrumentSoon();
                RaisePropertyChanged(nameof(SelectedTrackIndex));
                RaisePropertyChanged(nameof(SelectedTrackHeader));
                RefreshPianoRoll();
                TransposeUpCommand?.RaiseCanExecuteChanged();
                TransposeDownCommand?.RaiseCanExecuteChanged();
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

            if (!double.IsFinite(value) || value < MinTempoBpm || value > MaxTempoBpm)
            {
                StatusMessage = $"Tempo must be between {MinTempoBpm} and {MaxTempoBpm} BPM.";
                RaisePropertyChanged();
                return;
            }

            CaptureUndoStateIfNeeded();
            if (SetProperty(ref _tempo, value))
            {
                _engine.UpdateTempo(value);
                if (Tracks.Any(track => !string.IsNullOrEmpty(track.AudioClipPath)))
                {
                    RebuildTimeline(); // audio clip lengths on the timeline depend on the tempo
                }
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
                _trackEffects.MasterVolume = clamped;
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

    public int TimelineTotalSteps => Math.Max(StepsPerBar, BarCount * StepsPerBar);

    public string CurrentStepBar => FormatBarBeat(CurrentStepIndex, StepsPerBar, Meter.StepsPerBeat);

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

    // Text such as "3/4". Text that is not a valid time signature is ignored.
    public string TimeSignature
    {
        get => _timeSignature;
        set
        {
            if (!Models.Meter.TryParse(value, out var meter) || meter.ToString() == _timeSignature)
            {
                return;
            }

            CaptureUndoStateIfNeeded();
            SetProperty(ref _timeSignature, meter.ToString());
            ApplyMeterToEngine();
            foreach (var name in new[]
                     {
                         nameof(Meter), nameof(StepsPerBar), nameof(TimelineTotalSteps), nameof(TimelineSixteenthWidth),
                         nameof(TimelineBarGridLines), nameof(CurrentStepBar), nameof(SnapText),
                     })
            {
                RaisePropertyChanged(name);
            }

            if (!_isRestoringState)
            {
                RebuildTimeline();
                StatusMessage = $"Time signature {meter}";
            }
        }
    }

    public Models.Meter Meter => Models.Meter.ParseOrCommon(_timeSignature);

    // Sixteenth steps in one bar: 16 in 4/4, 12 in 3/4, 14 in 7/8.
    public int StepsPerBar => Meter.StepsPerBar;

    // Hands the bar, the beat and the click spacing of the time signature to the engine.
    private void ApplyMeterToEngine()
    {
        var meter = Meter;
        _engine.StepsPerBar = meter.StepsPerBar;
        _engine.StepsPerBeat = meter.StepsPerBeat;
        _engine.ClickStepInterval = Math.Max(1, meter.StepsPerBeat / Math.Max(1, _selectedClickOption.ClicksPerBeat));
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
        set
        {
            if (SetProperty(ref _isRecording, value))
            {
                RaisePropertyChanged(nameof(RecordButtonText));
            }
        }
    }

    public string RecordButtonText => IsRecording ? "RECORD ON" : "RECORD OFF";

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
            TransportPosition = TransportClock.Zero;
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
            TransportPosition = TransportClock.Zero;
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

    public double TimelineSixteenthWidth => TimelineBarWidth / StepsPerBar;

    // The vertical lines inside one bar of a lane, one per sixteenth: none at the bar line itself,
    // brighter on each beat.
    public IReadOnlyList<Brush> TimelineBarGridLines => Enumerable.Range(0, StepsPerBar)
        .Select(step => step == 0 ? Brushes.Transparent : step % Meter.StepsPerBeat == 0 ? BeatLineBrush : SixteenthLineBrush)
        .ToList();

    private static readonly Brush BeatLineBrush = FrozenBrush(0x3D, 0x56, 0x6D);
    private static readonly Brush SixteenthLineBrush = FrozenBrush(0x29, 0x3C, 0x4E);

    private static Brush FrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    // The nearest snap point to a step, never before the start of the pattern.
    public int SnapStep(int step)
    {
        var snapSteps = _snapMode switch
        {
            0 => StepsPerBar,
            1 => Meter.StepsPerBeat,
            _ => 1,
        };
        var rounded = (int)Math.Round(step / (double)snapSteps) * snapSteps;
        return Math.Max(0, rounded);
    }

    // Moves the block of a track by a number of steps.
    public void MoveBlock(PatternTrackViewModel track, int deltaSteps)
    {
        if (track.MidiNoteEvents.Count == 0 || deltaSteps == 0)
        {
            return;
        }

        var notes = track.MidiNoteEvents
            .Select(note => note with { StepIndex = Math.Max(0, note.StepIndex + deltaSteps) })
            .ToList();
        ReplaceTrackNotes(track, notes);
    }

    // Moves the start of a block to newStart, keeping its end: the notes stretch in proportion.
    public void StretchBlock(PatternTrackViewModel track, int newStart, int end)
    {
        if (track.MidiNoteEvents.Count == 0 || newStart < 0 || newStart >= end)
        {
            return;
        }

        var start = track.MidiNoteEvents.Min(note => note.StepIndex);
        var length = track.MidiNoteEvents.Max(note => note.StepIndex + Math.Max(1, note.DurationSteps)) - start;
        if (length <= 0)
        {
            return;
        }

        var ratio = (end - newStart) / (double)length;
        var notes = track.MidiNoteEvents
            .Select(note => note with
            {
                StepIndex = newStart + (int)Math.Round((note.StepIndex - start) * ratio),
                DurationSteps = Math.Max(1, (int)Math.Round(note.DurationSteps * ratio)),
            })
            .ToList();
        ReplaceTrackNotes(track, notes);
    }

    // Changes the length of a block by stretching its notes in proportion, keeping its start.
    public void ResizeBlock(PatternTrackViewModel track, int newLength)
    {
        if (track.MidiNoteEvents.Count == 0 || newLength <= 0)
        {
            return;
        }

        var start = track.MidiNoteEvents.Min(note => note.StepIndex);
        var length = track.MidiNoteEvents.Max(note => note.StepIndex + Math.Max(1, note.DurationSteps)) - start;
        if (length <= 0)
        {
            return;
        }

        var ratio = newLength / (double)length;
        var notes = track.MidiNoteEvents
            .Select(note => note with
            {
                StepIndex = start + (int)Math.Round((note.StepIndex - start) * ratio),
                DurationSteps = Math.Max(1, (int)Math.Round(note.DurationSteps * ratio)),
            })
            .ToList();
        ReplaceTrackNotes(track, notes);
    }

    // Sets the bar width so every bar of the timeline fits in the visible part of the track area.
    public void ZoomToFit()
    {
        var available = TimelineViewportWidth - TimelineTrackHeaderWidth;
        if (available <= 0 || TimelineBarNumbers.Count == 0)
        {
            return;
        }

        TimelineBarWidth = Math.Floor(available / TimelineBarNumbers.Count);
    }

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

    public RelayCommand ToggleTrackRecordArmCommand { get; }

    private RecordGrid NextRecordGrid()
    {
        var index = RecordGrids.ToList().FindIndex(grid => grid.Label == RecordGrid.Label);
        return RecordGrids[(index + 1) % RecordGrids.Count];
    }

    // Raised when a track's notes change, so an open piano roll can show them.
    public event Action<PatternTrackViewModel>? TrackNotesChanged;

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

    public SystemHealthViewModel Health { get; } = new();

    public RelayCommand SaveProjectCommand { get; }

    public RelayCommand SaveProjectAsCommand { get; }

    public RelayCommand LoadProjectCommand { get; }

    public RelayCommand ImportMidiCommand { get; }

    public RelayCommand NewProjectCommand { get; }

    public RelayCommand AddTrackCommand { get; }

    public RelayCommand DeleteTrackCommand { get; }

    public RelayCommand TransposeUpCommand { get; }

    public RelayCommand TransposeDownCommand { get; }

    public RelayCommand ResetTrackCommand { get; }

    public RelayCommand RefreshMidiDevicesCommand { get; }

    public ObservableCollection<MidiInputDeviceViewModel> MidiInputDevices { get; }

    // Lit briefly each time the MIDI input device sends a message.
    public bool IsMidiActivityVisible
    {
        get => _isMidiActivityVisible;
        private set => SetProperty(ref _isMidiActivityVisible, value);
    }

    public MidiInputDeviceViewModel? SelectedMidiInputDevice => MidiInputDevices.FirstOrDefault(device => device.IsSelected);

    public RelayCommand SelectMidiInputCommand { get; }

    // When on, notes played on the MIDI keyboard sound on the selected track's channel.
    public bool IsLiveKeyboardEnabled
    {
        get => _isLiveKeyboardEnabled;
        set => SetProperty(ref _isLiveKeyboardEnabled, value);
    }

    public RelayCommand SelectAudioOutputCommand { get; }

    public RelayCommand RefreshAudioOutputsCommand { get; }

    public RelayCommand SelectAudioBackendCommand { get; }

    public RelayCommand SelectAsioDriverCommand { get; }

    public RelayCommand BrowseVstPluginsPathCommand { get; }

    public RelayCommand ToggleArrangeModeCommand { get; }

    public RelayCommand ToggleLoopCommand { get; }

    public bool IsMetronomeOn
    {
        get => _engine.IsMetronomeOn;
        set
        {
            _engine.IsMetronomeOn = value;
            RaisePropertyChanged();
        }
    }

    // Whether the click keeps going under the song after the count-in, or stops once the count-in is over.
    public bool IsClickCountInOnly
    {
        get => _engine.IsClickCountInOnly;
        set
        {
            _engine.IsClickCountInOnly = value;
            RaisePropertyChanged();
            StatusMessage = value ? "Metronome: count-in only, then quiet" : "Metronome: clicks all through the song";
        }
    }

    public IReadOnlyList<ClickOption> ClickOptions { get; } = new[]
    {
        new ClickOption(1, "Quarter"),
        new ClickOption(2, "Eighth"),
    };

    // The click and count-in choices are cycled by a button, so they keep the look of the other buttons.
    public RelayCommand CycleClickOptionCommand { get; }

    public RelayCommand CycleCountInCommand { get; }

    public ClickOption SelectedClickOption
    {
        get => _selectedClickOption;
        set
        {
            if (SetProperty(ref _selectedClickOption, value))
            {
                ApplyMeterToEngine();
            }
        }
    }

    public RelayCommand PreviewClickCommand { get; }

    // True while the click plays on repeat from the CLICK button.
    public bool IsClickLoopRunning => _engine.IsClickLoopRunning;

    // Bars of clicks played before recording starts.
    public IReadOnlyList<CountInOption> CountInOptions { get; } = new[]
    {
        new CountInOption(0, "No count-in"),
        new CountInOption(1, "1 bar"),
        new CountInOption(2, "2 bars"),
        new CountInOption(3, "3 bars"),
    };

    public CountInOption SelectedCountIn
    {
        get => _selectedCountIn;
        set => SetProperty(ref _selectedCountIn, value);
    }

    public RelayCommand ZoomTimelineInCommand { get; }

    public RelayCommand ZoomTimelineOutCommand { get; }

    public RelayCommand ZoomToFitCommand { get; }

    // How blocks snap when moved or resized: to a bar, a beat or a sixteenth. Shown on the SNAP button.
    public RelayCommand CycleSnapCommand { get; }

    public string SnapText => _snapMode switch
    {
        0 => "SNAP BAR",
        1 => $"SNAP 1/{Meter.Denominator}",
        _ => "SNAP 1/16",
    };

    public void Dispose()
    {
        _audioMeterTimer.Stop();
        _trackInstruments.Dispose();
        _engine.StepChanged -= OnStepChanged;
        _engine.PatternChanged -= OnPlaybackPatternChanged;
        _engine.PlaybackCompleted -= OnPlaybackCompleted;
        _engine.PlaybackFaulted -= OnPlaybackFaulted;
        _midiInput.Dispose();
        _clickPlayer.Dispose();
        _engine.Dispose();
        AudioTrackPlayback.Stop();
        _trackEffects.Dispose();
    }

    // ---- Track effects ----

    // The effects offered by the add menu.
    public IReadOnlyList<EffectDefinition> AvailableEffects => EffectCatalog.All;

    // The user's own effect presets, shared by all projects.
    public EffectPresetStore EffectPresets { get; } = new();

    // Saves the effect's knobs as a user preset with the name, and marks the effect as being on it.
    public EffectPreset SaveEffectPreset(TrackEffectViewModel effect, string name)
    {
        var preset = EffectPresets.Save(effect.Definition, name, effect.Values);
        effect.MarkSavedAs(preset);
        StatusMessage = $"Preset \"{preset.Name}\" saved for {effect.Name}";
        return preset;
    }

    // Adds an effect at the end of the track's chain, at its default settings.
    public TrackEffectViewModel? AddEffect(PatternTrackViewModel? track, EffectDefinition definition)
    {
        if (track is null)
        {
            return null;
        }

        CaptureUndoStateIfNeeded();
        var effect = new TrackEffectViewModel(definition);
        track.Effects.Add(effect);
        StatusMessage = $"{definition.Name} added to {track.Name}";
        return effect;
    }

    public void RemoveEffect(PatternTrackViewModel? track, TrackEffectViewModel effect)
    {
        if (track is null || !track.Effects.Contains(effect))
        {
            return;
        }

        CaptureUndoStateIfNeeded();
        track.Effects.Remove(effect);
        StatusMessage = $"{effect.Name} removed from {track.Name}";
    }

    // Moves an effect one place earlier (-1) or later (+1) in the chain.
    public void MoveEffect(PatternTrackViewModel? track, TrackEffectViewModel effect, int direction)
    {
        if (track is null)
        {
            return;
        }

        var from = track.Effects.IndexOf(effect);
        var to = from + direction;
        if (from < 0 || to < 0 || to >= track.Effects.Count)
        {
            return;
        }

        CaptureUndoStateIfNeeded();
        track.Effects.Move(from, to);
    }

    // Moves an effect to a place in the chain, as when it is dragged in the rack.
    public void MoveEffectTo(PatternTrackViewModel track, TrackEffectViewModel effect, int index)
    {
        var from = track.Effects.IndexOf(effect);
        var to = Math.Clamp(index, 0, track.Effects.Count - 1);
        if (from < 0 || from == to)
        {
            return;
        }

        CaptureUndoStateIfNeeded();
        track.Effects.Move(from, to);
        StatusMessage = $"{effect.Name} moved to place {to + 1} in the rack of {track.Name}";
    }

    // The effect's input and output peaks since the previous call, for its IN and OUT meters.
    public (double Input, double Output) TakeEffectLevels(PatternTrackViewModel track, TrackEffectViewModel effect)
    {
        var slot = track.Effects.IndexOf(effect);
        return slot < 0 || _trackEffects.ChainFor(track) is not { } chain ? (0, 0) : chain.TakeLevels(slot);
    }

    // The effect's own meter, such as a compressor's gain reduction, while its track's chain is running.
    public double EffectMeter(PatternTrackViewModel track, TrackEffectViewModel effect)
    {
        var slot = track.Effects.IndexOf(effect);
        return slot < 0 ? 0 : _trackEffects.ChainFor(track)?.Meter(slot) ?? 0;
    }

    private void CaptureUndoStateIfNeeded()
    {
        if (_isRestoringState)
        {
            return;
        }

        _history.RecordChange(BuildProjectDocument());
        RaiseHistoryStateChanged();
    }

    private void RaiseHistoryStateChanged()
    {
        UndoCommand?.RaiseCanExecuteChanged();
        RedoCommand?.RaiseCanExecuteChanged();
    }

    private void Undo()
    {
        if (!_history.CanUndo)
        {
            return;
        }

        RestoreProjectDocument(_history.Undo(BuildProjectDocument()), keepInstruments: true);
        RaiseHistoryStateChanged();
    }

    private void Redo()
    {
        if (!_history.CanRedo)
        {
            return;
        }

        RestoreProjectDocument(_history.Redo(BuildProjectDocument()), keepInstruments: true);
        RaiseHistoryStateChanged();
    }

    private void CreateNewProject()
    {
        _history.Clear();
        _isRestoringState = true;
        try
        {
            CurrentStepIndex = 0;
            TransportPosition = TransportClock.Zero;
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
            Tempo = 120;
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
        MarkProjectSaved();
    }

    private void ApplySelectedMidiOutputDevice()
    {
        var device = SelectedMidiOutputDevice;
        if (device is null)
        {
            return;
        }

        if (device.IsOff)
        {
            _engine.ClearMidiOutputDevice();
            StatusMessage = "MIDI output off: tracks without a plug-in are silent";
        }
        else if (!_engine.TrySetMidiOutputDevice(device.DeviceId))
        {
            StatusMessage = $"Unable to open MIDI output device: {device.DisplayName}";
        }
        else
        {
            StatusMessage = $"MIDI output set to {device.DisplayName}";
        }
    }

    private static T NextOption<T>(IReadOnlyList<T> options, T current)
    {
        var index = options.ToList().IndexOf(current);
        return options[(index + 1) % options.Count];
    }

    private void ReloadMidiInputDevices()
    {
        var selectedName = SelectedMidiInputDevice?.DisplayName ?? _appSettingsService.Load().MidiInputDeviceName;
        MidiInputDevices.Clear();
        MidiInputDevices.Add(new MidiInputDeviceViewModel(null, "Off"));
        foreach (var device in MidiInputService.GetInputDevices())
        {
            MidiInputDevices.Add(new MidiInputDeviceViewModel(device.DeviceId, device.DisplayName));
        }

        // Without a saved choice, the A-PRO 1 port of the keyboard is used when it is connected.
        SelectMidiInput(MidiInputDevices.FirstOrDefault(device => device.DisplayName == selectedName)
            ?? MidiInputDevices.FirstOrDefault(device => IsKeyboardMidiInput(device.DisplayName))
            ?? MidiInputDevices[0]);
    }

    private static bool IsKeyboardMidiInput(string displayName)
    {
        var normalized = new string(displayName.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        return normalized.EndsWith("APRO1", StringComparison.Ordinal);
    }

    private void SaveMidiInputChoice(MidiInputDeviceViewModel device)
    {
        var settings = _appSettingsService.Load();
        settings.MidiInputDeviceName = device.DisplayName;
        _appSettingsService.Save(settings);
    }

    private void SelectMidiInput(MidiInputDeviceViewModel? device)
    {
        if (device is null)
        {
            return;
        }

        foreach (var item in MidiInputDevices)
        {
            item.IsSelected = ReferenceEquals(item, device);
        }
        RaisePropertyChanged(nameof(SelectedMidiInputDevice));

        _midiInput.Close();
        if (device.DeviceId is not { } deviceId)
        {
            StatusMessage = "MIDI input off";
            return;
        }

        StatusMessage = _midiInput.Open(deviceId)
            ? $"MIDI input set to {device.DisplayName}"
            : $"Unable to open MIDI input device: {device.DisplayName}";
    }

    // Shows the raw bytes of controller and other messages, so it is visible what the keyboard sends besides notes.
    private void OnMidiMessageReceived(int packed)
    {
        FlashMidiActivity();

        // Notes write their own route message in the status line, so only the other messages show up here.
        var status = packed & 0xFF;
        if (status >= 0xF8 || (status >= 0x80 && status < 0xB0))
        {
            return;
        }

        StatusMessage = $"MIDI in: {status:X2} {(packed >> 8) & 0xFF:X2} {(packed >> 16) & 0xFF:X2}";
    }

    // The host that runs the tracks' plug-ins, for windows that show one of them.
    public IVst3PluginHost PluginHost => _liveInstrumentHost;

    // Starts the track's own plug-in if it is not running yet, and returns it. Every track has its own,
    // so all the tracks sound together. Null when the track has no plug-in or it cannot start.
    public Vst3ModuleHandle? EnsureTrackInstrument(PatternTrackViewModel track)
    {
        var handle = _trackInstruments.Ensure(track, AudioOutputSelection.FromSettings(new AppSettingsService().Load()), out var error);
        if (error is not null)
        {
            StatusMessage = $"{track.Name}: {error}";
        }

        return handle;
    }

    // The keyboard plays the selected track's plug-in. When it is not running yet, the first key starts it.
    private void PointKeyboardAtSelectedTrack()
    {
        if (SelectedTrack is { } track && _trackInstruments.RunningFor(track) is { } instrument)
        {
            LiveInstrumentRegistry.Set(instrument);
        }
        else if (LiveInstrumentRegistry.Current is { } current)
        {
            LiveInstrumentRegistry.Clear(current);
        }
    }

    // Starts the selected track's plug-in as soon as the window has drawn the selection, so the first key
    // played on it sounds at once instead of waiting for the plug-in to load.
    private void StartSelectedTrackInstrumentSoon()
    {
        Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (SelectedTrack is { SupportsMidiPlayback: true, HasPlugin: true } track && _trackInstruments.RunningFor(track) is null)
            {
                EnsureTrackInstrument(track);
                PointKeyboardAtSelectedTrack();
            }
        });
    }

    // Stops the plug-ins of tracks that are gone or play another plug-in now.
    private void ReleaseUnusedInstruments() =>
        _trackInstruments.KeepOnly(Patterns.SelectMany(pattern => pattern.Tracks));

    // Plays a sequencer note through the track's own plug-in, when it is running. Called on the sequencer thread.
    private bool RouteSequencerNoteToPlugin(PatternTrackViewModel track, int channel, int noteNumber, int velocity, bool isOn) =>
        _trackInstruments.RunningFor(track) is { } instrument
        && _liveInstrumentHost.TrySendNote(instrument, channel, noteNumber, velocity, isOn);

    private void FlashMidiActivity()
    {
        IsMidiActivityVisible = true;
        _midiActivityTimer.Stop();
        _midiActivityTimer.Start();
    }

    // The block of notes copied with Ctrl+C, with its first note at step 0.
    private IReadOnlyList<MidiNoteEventViewModel>? _copiedNotes;

    // The block the player has clicked. It is highlighted, and copy, cut and the block handles act on it.
    private PatternTrackViewModel? _selectedBlockTrack;

    public void SelectBlock(PatternTrackViewModel? track)
    {
        _selectedBlockTrack = track;
        foreach (var lane in TimelineTracks)
        {
            lane.IsBlockSelected = track is not null && ReferenceEquals(lane.SourceTrack, track);
        }

        if (track is not null)
        {
            SelectedTrack = track;
        }
    }

    private void ClearBlockSelection()
    {
        _selectedBlockTrack = null;
        foreach (var lane in TimelineTracks)
        {
            lane.IsBlockSelected = false;
        }
    }

    public void CutSelectedNotes()
    {
        CopySelectedNotes();
        var track = _selectedBlockTrack ?? SelectedTrack;
        if (track is null || track.MidiNoteEvents.Count == 0 || _copiedNotes is null)
        {
            return;
        }

        ReplaceTrackNotes(track, Array.Empty<MidiNoteEventViewModel>());
        StatusMessage = $"Cut the block from {track.Name}";
    }

    // Delete removes the recorded block the player clicked on the timeline. Only a block clicked on
    // the timeline is deleted, never a whole track that is merely selected; Undo brings it back.
    public bool DeleteSelectedBlock()
    {
        var track = _selectedBlockTrack;
        if (track is null || track.MidiNoteEvents.Count == 0)
        {
            return false;
        }

        var count = track.MidiNoteEvents.Count;
        ReplaceTrackNotes(track, Array.Empty<MidiNoteEventViewModel>());
        ClearBlockSelection();
        StatusMessage = $"Deleted the block of {track.Name} ({count} notes). Undo brings it back.";
        return true;
    }

    public void CopySelectedNotes()
    {
        var track = _selectedBlockTrack ?? SelectedTrack;
        if (track is null || track.MidiNoteEvents.Count == 0)
        {
            StatusMessage = "Nothing to copy: the selected track has no notes";
            return;
        }

        var blockStart = track.MidiNoteEvents.Min(note => note.StepIndex);
        _copiedNotes = track.MidiNoteEvents.Select(note => note with { StepIndex = note.StepIndex - blockStart }).ToList();
        StatusMessage = $"Copied {_copiedNotes.Count} notes from {track.Name}";
    }

    // Pastes the copied block on the selected track, starting at the playhead.
    public void PasteNotes()
    {
        var track = SelectedTrack;
        if (track is null || _copiedNotes is null)
        {
            return;
        }

        var notes = track.MidiNoteEvents.ToList();
        notes.AddRange(_copiedNotes.Select(note => note with { StepIndex = note.StepIndex + CurrentStepIndex }));
        ReplaceTrackNotes(track, notes);
        SelectBlock(track);
    }

    // Recording is turned on and off here; playback is started by the transport (space bar or START).
    private void SetRecording(bool recording)
    {
        IsRecording = recording;
    }

    // Arming a track turns recording on; recording stops when no track is armed.
    private void ToggleTrackRecordArm(PatternTrackViewModel? track)
    {
        if (track is null || !track.SupportsMidiPlayback)
        {
            StatusMessage = "Only MIDI tracks can be armed for recording.";
            return;
        }

        track.IsRecordArmed = !track.IsRecordArmed;
        SetRecording(Tracks.Any(item => item.IsRecordArmed));
        StatusMessage = track.IsRecordArmed ? $"{track.Name} armed for recording" : $"{track.Name} disarmed";
    }

    // While recording, a key played becomes a note of the selected track: it starts at the playhead and lasts until it is released.
    private int _keyboardNotesPlayed;
    private int _keyboardNotesRecorded;

    // The track recorded into: the first armed MIDI track, or the selected one.
    private PatternTrackViewModel? RecordTarget =>
        Tracks.FirstOrDefault(item => item.IsRecordArmed && item.SupportsMidiPlayback) ?? SelectedTrack;

    // position is where the sequencer was when the key arrived, taken on the MIDI thread.
    private void RecordLiveNote(MidiNoteMessage note, double position)
    {
        var track = RecordTarget;
        if (!IsRecording || !IsPlaying || track is null || !track.SupportsMidiPlayback)
        {
            return;
        }

        var at = Math.Max(0, SnapToRecordGrid(position) ?? position);
        if (note.IsOn)
        {
            _keyboardNotesPlayed++;
            _recordingNotes[note.NoteNumber] = (at, note.Velocity);
            return;
        }

        if (_recordingNotes.Remove(note.NoteNumber, out var started))
        {
            AddRecordedNote(track, note.NoteNumber, started.Velocity, started.StartStep, at);
        }
    }

    private void AddRecordedNote(PatternTrackViewModel track, int noteNumber, int velocity, double start, double end)
    {
        // One undo step for the whole take, taken before its first note.
        if (!_recordTakeHasNotes)
        {
            CaptureUndoStateIfNeeded();
            _recordTakeHasNotes = true;
        }

        var stepStart = Math.Floor(start);
        var length = Math.Max(RecordGrid.Steps ?? 0.05, end - start);
        var wholeSteps = (int)Math.Floor(length);
        var notes = track.MidiNoteEvents.ToList();
        notes.Add(new MidiNoteEventViewModel((int)stepStart, wholeSteps, noteNumber, velocity, start - stepStart, length - wholeSteps));
        track.MidiNoteEvents = notes;
        _keyboardNotesRecorded++;
        _recordRefreshTrack = track;
        if (!_recordRefreshTimer.IsEnabled)
        {
            _recordRefreshTimer.Start();
        }
    }

    // Redraws the timeline and the piano roll for the notes recorded since the last refresh.
    private void RefreshRecordedTrack()
    {
        _recordRefreshTimer.Stop();
        if (_recordRefreshTrack is not { } track)
        {
            return;
        }

        _recordRefreshTrack = null;
        RebuildTimeline();
        TrackNotesChanged?.Invoke(track);
        SelectBlock(track);
        StatusMessage = $"{track.Name}: {track.MidiNoteEvents.Count} notes (played {_keyboardNotesPlayed}, recorded {_keyboardNotesRecorded})";
    }

    // Keys still held when playback stops end where it stopped.
    private void FinishRecordingTake(double stopPosition)
    {
        if (RecordTarget is { } track)
        {
            foreach (var (noteNumber, started) in _recordingNotes)
            {
                AddRecordedNote(track, noteNumber, started.Velocity, started.StartStep, Math.Max(started.StartStep, stopPosition));
            }
        }

        _recordingNotes.Clear();
        RefreshRecordedTrack();
        _recordTakeHasNotes = false;
    }

    // Runs on the MIDI thread. A note for a running plug-in is sent at once, without waiting for the UI thread;
    // recording and the status line are handled on the UI thread.
    private int _keyboardInputDelayMs;

    // The plug-in each key held down on the keyboard was played on, so its release goes there too.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, Vst3ModuleHandle> _heldKeys = new();

    // Releases every note still sounding on any plug-in: those of the tracks and the one the keyboard plays.
    private void ReleaseAllPluginNotes()
    {
        _trackInstruments.ReleaseAllNotes();
        if (LiveInstrumentRegistry.Current is { } keyboardInstrument)
        {
            _liveInstrumentHost.ReleaseAllNotes(keyboardInstrument);
        }
        _heldKeys.Clear();
    }

    // The ASIO buffer and output latency the running plug-in is using, for the status line.
    private string DescribeAudioBuffer()
    {
        if (LiveInstrumentRegistry.Current is not { } running
            || !_liveInstrumentHost.TryGetAsioTiming(running, out var bufferFrames, out var outputLatency, out _))
        {
            return string.Empty;
        }

        return $", ASIO buffer {bufferFrames} samples, output latency {outputLatency}";
    }

    private enum KeyboardRoute
    {
        None,
        Plugin,
        MidiOutput,
    }

    // Runs on the MIDI thread. A note goes out at once to a running plug-in or to the MIDI output; a track whose
    // plug-in is not running yet is left to the UI thread, which starts it.
    private void OnKeyboardNote(MidiNoteMessage note)
    {
        // Taken here, when the key arrives: by the time the UI thread handles the note the sequencer has moved on.
        var position = _engine.LivePosition;
        var route = KeyboardRoute.None;
        if (IsLiveKeyboardEnabled)
        {
            if (!note.IsOn && _heldKeys.TryRemove(note.NoteNumber, out var struck))
            {
                // Released on the plug-in it was played on, even if the keyboard has moved to another since.
                _liveInstrumentHost.TrySendNote(struck, note.Channel, note.NoteNumber, note.Velocity, isOn: false);
                route = KeyboardRoute.Plugin;
            }
            else if (LiveInstrumentRegistry.Current is { } running
                && _liveInstrumentHost.TrySendNote(running, note.Channel, note.NoteNumber, note.Velocity, note.IsOn))
            {
                if (note.IsOn)
                {
                    _heldKeys[note.NoteNumber] = running;
                }
                route = KeyboardRoute.Plugin;
            }
            else if (SelectedTrack is not { PluginFilePath: { Length: > 0 } })
            {
                var channel = SelectedTrack is { } track ? track.Channel - 1 : 0;
                try
                {
                    _engine.PlayLiveNote(channel, note.NoteNumber, note.Velocity, note.IsOn);
                    route = KeyboardRoute.MidiOutput;
                }
                catch (InvalidOperationException)
                {
                    route = KeyboardRoute.None;
                }
            }
        }

        _keyboardInputDelayMs = _midiInput.LastInputDelayMs;
        Application.Current?.Dispatcher.BeginInvoke(() => PlayLiveKeyboardNote(note, route, position));
    }

    private void PlayLiveKeyboardNote(MidiNoteMessage note, KeyboardRoute route, double position)
    {
        RecordLiveNote(note, position);
        ShowLiveNoteOnMeter(note);

        if (route != KeyboardRoute.None)
        {
            if (note.IsOn)
            {
                StatusMessage = route == KeyboardRoute.Plugin
                    ? $"Keyboard note {note.NoteNumber} sent to the plug-in (input delay {_keyboardInputDelayMs} ms{DescribeAudioBuffer()}, step {CurrentStepIndex}, recording {IsRecording}, playing {IsPlaying}, notes played {_keyboardNotesPlayed}, recorded {_keyboardNotesRecorded})"
                    : $"Keyboard note {note.NoteNumber} sent to the MIDI output (input delay {_keyboardInputDelayMs} ms, step {CurrentStepIndex}, recording {IsRecording}, notes played {_keyboardNotesPlayed}, recorded {_keyboardNotesRecorded})";
            }
            return;
        }

        if (note.IsOn && SelectedTrack is { SupportsMidiPlayback: true, PluginFilePath: { Length: > 0 } } selected
            && EnsureTrackInstrument(selected) is { } started)
        {
            LiveInstrumentRegistry.Set(started);
        }

        if (!IsLiveKeyboardEnabled)
        {
            return;
        }

        // A plug-in whose editor has audio running hears the keyboard directly; otherwise the note goes to the MIDI output.
        var instrument = LiveInstrumentRegistry.Current;
        if (!note.IsOn && _heldKeys.TryRemove(note.NoteNumber, out var struck))
        {
            instrument = struck;
        }

        if (instrument is not null && _liveInstrumentHost.TrySendNote(instrument, note.Channel, note.NoteNumber, note.Velocity, note.IsOn))
        {
            if (note.IsOn)
            {
                _heldKeys[note.NoteNumber] = instrument;
            }

            if (note.IsOn)
            {
                StatusMessage = $"Keyboard note {note.NoteNumber} sent to the plug-in (input delay {_keyboardInputDelayMs} ms)";
            }
            return;
        }

        var channel = SelectedTrack is { } track ? track.Channel - 1 : 0;
        try
        {
            _engine.PlayLiveNote(channel, note.NoteNumber, note.Velocity, note.IsOn);
            if (note.IsOn)
            {
                StatusMessage = instrument is null
                    ? $"Keyboard note {note.NoteNumber} sent to the MIDI output (no plug-in audio running, input delay {_keyboardInputDelayMs} ms)"
                    : $"Keyboard note {note.NoteNumber} sent to the MIDI output (plug-in did not accept it, input delay {_keyboardInputDelayMs} ms)";
            }
        }
        catch (InvalidOperationException ex)
        {
            StatusMessage = ex.Message;
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
            ?? MidiOutputDevices.FirstOrDefault(device => device.IsOff);
        StatusMessage = "MIDI output devices refreshed";
    }

    // Saves to the current file, or asks for one. False when the user cancels or the save fails.
    public bool SaveProject()
    {
        var path = CurrentProjectPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            path = PromptForSavePath();
            if (path is null)
            {
                return false;
            }
        }

        return SaveProjectToPath(path);
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

    private bool SaveProjectToPath(string path)
    {
        try
        {
            // The sounds chosen in the plug-ins are saved with the project.
            _trackInstruments.CaptureStates();
            _projectFileService.Save(path, BuildProjectDocument());
            CurrentProjectPath = path;
            StatusMessage = $"Project saved to {path}";
            MarkProjectSaved();
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Save project failed", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusMessage = "Save failed";
            return false;
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
            _history.Clear();
            RestoreProjectDocument(document);
            CurrentProjectPath = dialog.FileName;
            StatusMessage = $"Project loaded from {dialog.FileName}";
            SaveProjectCommand?.RaiseCanExecuteChanged();
            SaveProjectAsCommand?.RaiseCanExecuteChanged();
            PasteTrackCommand?.RaiseCanExecuteChanged();
            RaiseHistoryStateChanged();
            MarkProjectSaved();
            StartSavedInstruments();
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
            _history.Clear();
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

    // Plays the audio clips of the unmuted tracks from the current position with the sequence, each
    // through its track's effects.
    private void StartAudioTracks()
    {
        var tracks = new List<(AudioClip Clip, NativeEffectChain? Effects)>();
        foreach (var track in Tracks.Where(track => !track.IsMuted && !string.IsNullOrEmpty(track.AudioClipPath)))
        {
            var clip = AudioTrackMixer.Mix(new[] { new AudioPlacement(track.AudioClipPath!, 0) }, Tempo, CurrentStepIndex, AudioClipLoader.Load);
            if (clip is not null)
            {
                tracks.Add((clip, _trackEffects.ChainFor(track)));
            }
        }

        if (tracks.Count == 0)
        {
            AudioTrackPlayback.Stop();
            return;
        }
        AudioTrackPlayback.Play(tracks);
    }

    // Makes a track play through a plug-in, as when a plug-in is dropped on it from the plug-in list.
    public void AssignPlugin(PatternTrackViewModel track, string pluginPath)
    {
        CaptureUndoStateIfNeeded();
        if (!string.Equals(track.PluginFilePath, pluginPath, StringComparison.OrdinalIgnoreCase))
        {
            // A saved sound belongs to the plug-in it was made in.
            track.PluginState = null;
            track.PluginEditorState = null;
        }

        track.PluginFilePath = pluginPath;
        ReleaseUnusedInstruments();
        StartSelectedTrackInstrumentSoon();
        StatusMessage = $"{track.Name} now plays through {Path.GetFileNameWithoutExtension(pluginPath)}";
    }

    // SonNormalize: the track's clip with its peak brought to targetDb, saved as a new file so the
    // original stays as it was and the change can be undone. Returns false when there is nothing to do.
    public bool NormalizeAudioClip(PatternTrackViewModel? track, double targetDb)
    {
        if (track?.AudioClipPath is not { } path || !File.Exists(path))
        {
            StatusMessage = "SonNormalize: the track has no audio clip";
            return false;
        }

        var clip = AudioClipLoader.Load(path);
        var before = SonNormalize.PeakDb(clip);
        if (double.IsNegativeInfinity(before))
        {
            StatusMessage = "SonNormalize: the clip is silent";
            return false;
        }

        CaptureUndoStateIfNeeded();
        track.AudioClipPath = AudioClipLoader.SaveToLibrary(SonNormalize.Apply(clip, targetDb));
        StatusMessage = string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            "SonNormalize: {0} peak {1:0.0} dB → {2:0.0} dB ({3:+0.0;-0.0} dB)",
            track.Name, before, targetDb, targetDb - before);
        RebuildTimeline();
        return true;
    }

    // Puts a clip into an empty track and makes the track an audio track.
    public void AssignAudioClip(PatternTrackViewModel track, string clipPath)
    {
        CaptureUndoStateIfNeeded();
        track.AudioClipPath = clipPath;
        track.TrackType = PatternTrackType.AudioInput;
        track.Instrument = "Audio clip";
        StatusMessage = $"{track.Name} now plays the audio clip";
        RebuildTimeline();
    }

    // Loads an audio file dropped on a track into it. Only empty tracks take a file.
    public void AssignAudioFile(PatternTrackViewModel track, string sourcePath)
    {
        var fileName = Path.GetFileName(sourcePath);
        if (!IsEmptyTrack(track))
        {
            StatusMessage = $"{track.Name} is not empty; {fileName} was not added";
            return;
        }

        try
        {
            var clip = AudioClipLoader.Load(sourcePath);
            var libraryPath = AudioClipLoader.SaveToLibrary(clip);
            _audioWaveforms[libraryPath] = AudioWaveformSummary.FromClip(clip);
            AssignAudioClip(track, libraryPath);
            StatusMessage = $"{track.Name} now plays {fileName} ({clip.Duration.TotalSeconds:0.00} s)";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not load {fileName}: {ex.Message}";
        }
    }

    // The outline of a track's clip, decoded once per file. Null when the track has no readable clip.
    private AudioWaveformSummary? LoadAudioWaveform(PatternTrackViewModel track)
    {
        var path = track.AudioClipPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return null;
        }

        if (_audioWaveforms.TryGetValue(path, out var cached))
        {
            return cached;
        }

        try
        {
            var summary = AudioWaveformSummary.FromClip(AudioClipLoader.Load(path));
            _audioWaveforms[path] = summary;
            return summary;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not read the audio clip on {track.Name}: {ex.Message}";
            return null;
        }
    }

    public static bool IsEmptyTrack(PatternTrackViewModel track)
    {
        return track.MidiNoteEvents.Count == 0
            && !track.Steps.Any(step => step.IsActive)
            && string.IsNullOrEmpty(track.AudioClipPath);
    }

    // Gives the selected pattern as many bars as its arrangement block, so playback loops over the whole block.
    private bool FitSelectedPatternToArrangeLength()
    {
        // With no arrangement block selected, the pattern still plays the default block length, so it does not loop on one bar.
        var bars = SelectedArrangePatternLength > 0 ? SelectedArrangePatternLength : ArrangementPatternLength;
        if (SelectedPattern is not { } pattern)
        {
            return false;
        }

        var added = false;
        foreach (var track in pattern.Tracks)
        {
            while (track.Steps.Count < bars * StepsPerBar)
            {
                track.Steps.Add(new StepViewModel(track.Steps.Count, false));
                added = true;
            }
        }

        return added;
    }

    // The tracks whose plug-ins playback starts: every plug-in track with notes to play, and the selected
    // plug-in track when it is being recorded on, so it is heard as it is played.
    internal static IReadOnlyList<PatternTrackViewModel> PlaybackInstrumentTracks(IEnumerable<PatternTrackViewModel> tracks, PatternTrackViewModel? selected, bool recording)
    {
        static bool HasPlugin(PatternTrackViewModel track) => track.SupportsMidiPlayback && !string.IsNullOrEmpty(track.PluginFilePath);
        static bool HasNotes(PatternTrackViewModel track) => track.MidiNoteEvents.Count > 0 || track.Steps.Any(step => step.IsActive);

        var playing = tracks.Where(track => HasPlugin(track) && HasNotes(track)).ToList();
        if (recording && selected is not null && HasPlugin(selected) && !playing.Contains(selected))
        {
            playing.Add(selected);
        }

        return playing;
    }

    private void StartPlayback()
    {
        if (FitSelectedPatternToArrangeLength())
        {
            ConfigurePlaybackSource();
            RebuildTimeline();
        }

        ReleaseUnusedInstruments();
        foreach (var pluginTrack in PlaybackInstrumentTracks(Patterns.SelectMany(pattern => pattern.Tracks), SelectedTrack, IsRecording))
        {
            EnsureTrackInstrument(pluginTrack);
        }
        PointKeyboardAtSelectedTrack();

        _playbackStartStep = CurrentStepIndex;
        _recordingNotes.Clear();
        if (IsRecording && SelectedCountIn.Bars > 0)
        {
            IsPlaying = true;
            StatusMessage = $"Count-in: {SelectedCountIn.Bars} bar(s)";
            _ = _engine.StartAfterCountInAsync(SelectedCountIn.Bars, StartAudioTracks);
            return;
        }

        StartAudioTracks();
        _engine.Start();
        IsPlaying = true;
        StatusMessage = "Playback started";
    }

    private void StopPlayback()
    {
        var stopPosition = _engine.LivePosition;
        var recordedTake = _recordTakeHasNotes || _recordingNotes.Count > 0;
        AudioTrackPlayback.Stop();
        _engine.Stop();
        ReleaseAllPluginNotes();
        RaisePropertyChanged(nameof(IsClickLoopRunning));
        IsPlaying = false;
        StatusMessage = "Playback paused";
        if (recordedTake)
        {
            FinishRecordingTake(stopPosition);
            SeekToStep(_playbackStartStep); // back to where the take began, ready to listen to it
        }
    }

    private void OnStepChanged(object? sender, int stepIndex)
    {
        if (!IsPlaying)
        {
            return;
        }

        CurrentStepIndex = stepIndex;
        UpdateSongTimelinePosition(stepIndex);
        TransportPosition = TransportClock.FormatPosition(stepIndex, Tempo);
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
        TransportPosition = TransportClock.Zero;
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
                (int)Math.Ceiling(assignedBars / (double)PatternBarCount(pattern, StepsPerBar)));
            sequence.AddRange(Enumerable.Repeat(pattern, repetitions));
            rowIndex += assignedBars;
        }

        return sequence.Count > 0 ? sequence : Patterns.ToList();
    }

    // The step a timeline cell shows. A track with MIDI note events shows those notes, as playback does; otherwise its step grid.
    private static StepViewModel? TimelineStepFor(PatternTrackViewModel? track, int stepIndex)
    {
        if (track is null)
        {
            return null;
        }

        if (track.MidiNoteEvents.Count > 0)
        {
            // The notes form one block, from the first note started to the last note released.
            var blockStart = track.MidiNoteEvents.Min(item => item.StepIndex);
            var blockEnd = track.MidiNoteEvents.Max(item => item.StepIndex + Math.Max(1, item.DurationSteps));
            return stepIndex >= blockStart && stepIndex < blockEnd ? new StepViewModel(stepIndex, true) : null;
        }

        return stepIndex < track.Steps.Count ? track.Steps[stepIndex] : null;
    }

    private double _lastRebuildMs;

    private void RebuildTimeline()
    {
        var rebuildStart = Stopwatch.GetTimestamp();
        var timelinePatterns = IsArrangeMode
            ? GetArrangePlaybackPatterns()
            : GetSelectedPatternTimelinePatterns();

        TimelineTracks.Clear();
        TimelineBarNumbers.Clear();
        _previousTimelineStep = -1;
        var timelineBarCount = timelinePatterns.Sum(pattern => PatternBarCount(pattern, StepsPerBar));
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
                var timelineStepCount = PatternBarCount(pattern, StepsPerBar) * StepsPerBar;
                for (var stepIndex = 0; stepIndex < timelineStepCount; stepIndex++)
                {
                    var sourceStep = TimelineStepFor(track, stepIndex);
                    timelineSteps.Add(new ArrangeTimelineStepViewModel(absoluteStep++, sourceStep));
                }
            }

            TimelineTracks.Add(new ArrangeTimelineTrackViewModel(sourceTrack, timelineSteps, LoadAudioWaveform(sourceTrack), Tempo));
        }

        RaisePropertyChanged(nameof(TimelineContentWidth));
        RaisePropertyChanged(nameof(TimelineWorkspaceWidth));
        UpdateSongTimelinePosition(CurrentStepIndex);
        foreach (var lane in TimelineTracks)
        {
            lane.IsBlockSelected = ReferenceEquals(lane.SourceTrack, _selectedBlockTrack);
        }

        _lastRebuildMs = Stopwatch.GetElapsedTime(rebuildStart).TotalMilliseconds;
    }

    private List<PatternViewModel> GetSelectedPatternTimelinePatterns()
    {
        if (SelectedPattern is null)
        {
            return new List<PatternViewModel>();
        }

        var barCount = TryGetSelectedArrangePatternRange(out _, out _, out var length)
            ? length
            : PatternBarCount(SelectedPattern, StepsPerBar);
        var repetitions = Math.Max(
            1,
            (int)Math.Ceiling(barCount / (double)PatternBarCount(SelectedPattern, StepsPerBar)));
        return Enumerable.Repeat(SelectedPattern, repetitions).ToList();
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

        CurrentSongBarNumber = Math.Max(1, absoluteStep / StepsPerBar + 1);
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
        var targetStep = Math.Clamp(
            CurrentStepIndex + barOffset * StepsPerBar,
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
        TransportPosition = TransportClock.FormatPosition(targetStep, Tempo);
        StatusMessage = $"Position: bar {targetStep / StepsPerBar + 1}";
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
        TransportPosition = TransportClock.Zero;
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

        var requestedRow = insertAfter ? targetStartRow + targetLength : targetStartRow;
        var assignments = GetSongArrangeAssignments();
        var insertionRow = SongArrangeLayout.ResolveMoveRow(sourceStartRow, sourceLength, requestedRow, assignments.Count);
        if (insertionRow == sourceStartRow)
        {
            return;
        }

        CaptureUndoStateIfNeeded();
        SongArrangeLayout.MoveRows(assignments, sourceStartRow, sourceLength, insertionRow);
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
        var insertionRow = assignments.Count - sourceLength;
        SongArrangeLayout.MoveRows(assignments, sourceStartRow, sourceLength, insertionRow);
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

        if (!ArrangePatternEditorRules.TryParseInput(
                patternName,
                assignmentNumber,
                subChannelIndex,
                out var targetPatternName,
                out var newSlot,
                out errorMessage))
        {
            return false;
        }

        if (SelectedPattern is null || !TryGetSelectedArrangePatternRange(out var startRow, out var currentColumn, out var length))
        {
            errorMessage = "Select a pattern in ARRANGE first.";
            return false;
        }

        var selectedPattern = SelectedPattern;
        var otherSlots = Patterns
            .Where(pattern => !ReferenceEquals(pattern, selectedPattern))
            .Select(pattern => pattern.Slot);
        if (ArrangePatternEditorRules.IsSlotTaken(otherSlots, newSlot))
        {
            errorMessage = $"Assignment {newSlot} is already used by another pattern.";
            return false;
        }

        if (subChannelIndex != currentColumn &&
            ArrangePatternEditorRules.IsSubChannelOccupied(startRow, length, row => SongArrangeRows[row].Cells[subChannelIndex].Label))
        {
            errorMessage = $"Sub channel {(char)('a' + subChannelIndex)} is occupied in the selected bar range.";
            return false;
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
        TrackEditOperations.Transpose(track, semitones);

        RefreshPianoRoll();
        StatusMessage = semitones > 0 ? "Track transposed up" : "Track transposed down";
    }

    // Replaces the track's recorded notes from the piano roll. One call is one undo step.
    public void ReplaceTrackNotes(PatternTrackViewModel track, IReadOnlyList<MidiNoteEventViewModel> notes)
    {
        CaptureUndoStateIfNeeded();
        track.MidiNoteEvents = notes.ToList();
        StatusMessage = $"{track.Name}: {notes.Count} notes";
        TrackNotesChanged?.Invoke(track);
        RebuildTimeline();
    }

    // Brings the selected track back to the settings it had when it was created.
    private void ResetSelectedTrack()
    {
        var track = SelectedTrack;
        if (track is null)
        {
            return;
        }

        CaptureUndoStateIfNeeded();
        track.Volume = 0.75;
        track.IsMuted = false;
        track.IsSolo = false;
        track.TrackColorBrush = CreateTrackColorBrush(null);
        StatusMessage = $"{track.Name} reset to its default settings";
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

    public void SetTrackInstrumentIcon(PatternTrackViewModel track, string? iconKey)
    {
        if (track is null || track.InstrumentIconKey == iconKey)
        {
            return;
        }

        CaptureUndoStateIfNeeded();
        track.InstrumentIconKey = iconKey;
        StatusMessage = track.InstrumentIcon is { } icon
            ? $"{track.Name} icon: {icon.Name}"
            : $"{track.Name} icon removed";
    }


    // A key played on the keyboard lights the meter of the track that hears it, the selected one.
    // While stopped, nothing else moves the meters, so a short timer lets them fall back.
    private void ShowLiveNoteOnMeter(MidiNoteMessage note)
    {
        // A track with its own audio shows the measured sound instead, for as long as it lasts.
        if (!note.IsOn || note.Velocity == 0 || SelectedTrack is not { IsMuted: false, HasOwnAudio: false } track)
        {
            return;
        }

        track.SignalLevel = Math.Max(track.SignalLevel, note.Velocity / 127.0 * track.Volume);
        if (_liveMeterTimer is null)
        {
            _liveMeterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
            _liveMeterTimer.Tick += (_, _) =>
            {
                if (IsPlaying || !TrackMeterCalculator.Release(Tracks))
                {
                    _liveMeterTimer.Stop();
                }
            };
        }

        if (!IsPlaying)
        {
            _liveMeterTimer.Start();
        }
    }

    private DispatcherTimer? _liveMeterTimer;

    // Reads the measured output of every track that makes its own sound, about twenty times a second.
    private readonly DispatcherTimer _audioMeterTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };

    private void ShowAudioLevels()
    {
        foreach (var track in Patterns.SelectMany(pattern => pattern.Tracks).Where(track => track.HasOwnAudio))
        {
            var peak = _trackEffects.ExistingChainFor(track)?.TakePeak() ?? 0;
            TrackMeterCalculator.ShowAudioLevel(track, peak);
        }
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

        name = NewTrackDefaults.NextTrackName(pattern.Tracks.Select(track => track.Name));
        channel = NewTrackDefaults.NextChannel(pattern.Tracks.Select(track => track.Channel));
        noteNumber = 36;
        instrument = NewTrackDefaults.InstrumentName(trackType);
        source = NewTrackDefaults.SourceName(trackType);

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

        if (!TrackDefinitionRules.Validate(name, channel, noteNumber, out errorMessage))
        {
            return false;
        }

        CaptureUndoStateIfNeeded();

        var track = WithDefaultEffects(new PatternTrackViewModel(
            name.Trim(),
            instrument,
            channel,
            noteNumber,
            BuildDefaultTrackSteps(),
            trackType: trackType,
            source: source.Trim()));

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
        if (!TrackDefinitionRules.Validate(name, channel, noteNumber, out errorMessage))
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
        SongArrangeLayout.ResizeRun(assignments, startRow, column, currentLength, requestedLength);
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

        if (!SongArrangeLayout.TryParseWholeBars(value, out var bars))
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

        var patternSlot = GetNextAvailablePatternSlot();
        if (patternSlot is null)
        {
            StatusMessage = "No pattern identifiers available";
            return;
        }

        CaptureUndoStateIfNeeded();
        var insertPatternIndex = SelectedPattern is null ? Patterns.Count : Patterns.IndexOf(SelectedPattern) + 1;
        var pattern = CreatePattern(patternName, "New pattern", patternSlot);
        Patterns.Insert(insertPatternIndex, pattern);

        var assignments = GetSongArrangeAssignments();
        var insertBarIndex = TryGetSelectedArrangePatternRange(out var startRow, out _, out var length)
            ? startRow + length
            : assignments.Count;
        SongArrangeLayout.InsertPatternRows(assignments, insertBarIndex, ArrangementPatternLength, column: 0, patternSlot);

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

        var patternSlot = GetNextAvailablePatternSlot();
        if (patternSlot is null)
        {
            StatusMessage = "No pattern identifiers available";
            return;
        }

        CaptureUndoStateIfNeeded();
        var copyName = CreateUniquePatternCopyName(sourcePattern.Name);
        var patternCopy = ApplyPatternDocument(BuildPatternDocument(sourcePattern));
        patternCopy.Name = copyName;
        patternCopy.Transpose = sourcePattern.Transpose;
        patternCopy.Slot = patternSlot;
        var insertPatternIndex = Patterns.IndexOf(sourcePattern) + 1;
        Patterns.Insert(insertPatternIndex, patternCopy);

        var assignments = GetSongArrangeAssignments();
        var insertBarIndex = startRow + length;
        SongArrangeLayout.InsertPatternRows(assignments, insertBarIndex, length, column, patternSlot);

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
        return PatternIdentifiers.CreateUniqueCopyName(Patterns.Select(pattern => pattern.Name), sourceName);
    }

    private string? GetNextAvailablePatternSlot()
    {
        return PatternIdentifiers.NextAvailableSlot(Patterns.Select(pattern => pattern.Slot));
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
        rangeColumn = column;
        return SongArrangeLayout.TryFindPatternRange(
            SongArrangeRows.Count,
            candidateRow => SongArrangeRows[candidateRow].Cells[column].Label,
            row,
            out startRow,
            out length);
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

    // keepInstruments: the tracks' running plug-ins go on playing in the tracks that take their places, as for
    // undo and redo. Otherwise, as for a project opened, each track starts its plug-in with its saved sound.
    private void RestoreProjectDocument(ProjectDocument document, bool keepInstruments = false)
    {
        var previousTracks = Patterns.Select(pattern => pattern.Tracks.ToList()).ToList();
        _isRestoringState = true;
        try
        {
            CurrentStepIndex = 0;
            TransportPosition = TransportClock.Zero;
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
                ?? MidiOutputDevices.FirstOrDefault(device => device.IsOff);
            SaveProjectCommand?.RaiseCanExecuteChanged();
            SaveProjectAsCommand?.RaiseCanExecuteChanged();
            PasteTrackCommand?.RaiseCanExecuteChanged();
            RaiseHistoryStateChanged();
        }
        finally
        {
            _isRestoringState = false;
        }

        if (keepInstruments)
        {
            // A track takes over the plug-in of the track that was in its place, pattern by pattern.
            _trackInstruments.HandOver(track =>
            {
                for (var p = 0; p < previousTracks.Count && p < Patterns.Count; p++)
                {
                    var index = previousTracks[p].IndexOf(track);
                    if (index >= 0)
                    {
                        return index < Patterns[p].Tracks.Count ? Patterns[p].Tracks[index] : null;
                    }
                }

                return null;
            });
        }
        else
        {
            _trackInstruments.KeepOnly(Array.Empty<PatternTrackViewModel>());
        }

        ReleaseUnusedInstruments();
        PointKeyboardAtSelectedTrack();
        TracksReplaced?.Invoke();
    }

    // Starts, one after another without holding up the window, the plug-ins of a project just opened that
    // were saved with a sound of their own, so they are ready when it plays.
    private void StartSavedInstruments()
    {
        var tracks = Patterns.SelectMany(pattern => pattern.Tracks)
            .Where(track => track.HasPlugin && track.PluginState is not null)
            .ToList();
        foreach (var track in tracks)
        {
            Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                if (Patterns.Any(pattern => pattern.Tracks.Contains(track)))
                {
                    EnsureTrackInstrument(track);
                }
            });
        }
    }

    // Raised after undo, redo or opening a project has replaced the tracks with new ones, so windows
    // showing a track (such as an FX rack) can switch to the track now in its place.
    public event Action? TracksReplaced;

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
