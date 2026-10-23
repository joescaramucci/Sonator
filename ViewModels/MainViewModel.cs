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

public sealed class MainViewModel : ViewModelBase, IDisposable, ISpectrumSource
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
    private readonly TrackEffectsHost _trackEffects;

    // The master: not a track of any pattern, but it has an FX rack like one, run on the whole mix.
    public PatternTrackViewModel MasterTrack { get; } = NewMasterTrack();

    // The buses (effect returns) the tracks send to, as the mixer shows them between the tracks and the master.
    public ObservableCollection<PatternTrackViewModel> Buses { get; } = new();

    private int _busesVersion;

    // Changes whenever the buses are added, removed or replaced, so the mixer's send controls are made again.
    public int BusesVersion
    {
        get => _busesVersion;
        private set => SetProperty(ref _busesVersion, value);
    }

    // Adds a bus with a reverb all wet in it, ready to be sent to: the tracks keep their own dry sound.
    public PatternTrackViewModel? AddBus()
    {
        if (Buses.Count >= NativeEffectChain.MaxBuses)
        {
            StatusMessage = $"There is room for {NativeEffectChain.MaxBuses} buses";
            return null;
        }

        CaptureUndoStateIfNeeded();
        var number = 1;
        while (Buses.Any(bus => bus.Name == $"Bus {number}"))
        {
            number++;
        }

        var added = PatternFactory.CreateTrack($"Bus {number}", "Bus", 1, 60, Array.Empty<bool>());
        added.Effects.Clear();
        var verb = new TrackEffectViewModel(EffectCatalog.Get(EffectKind.Verb));
        verb.Parameters[4].Value = 100; // Mix: only the reverb comes back from the bus
        added.Effects.Add(verb);
        added.TrackColorBrush = new SolidColorBrush(Color.FromRgb(0x2F, 0x8F, 0x83));
        Buses.Add(added);
        ApplyBuses();
        StatusMessage = $"{added.Name} added: turn up a track's send to it in the mixer";
        return added;
    }

    // Removes a bus, and every track's send to it.
    public void RemoveBus(PatternTrackViewModel bus)
    {
        if (!Buses.Contains(bus))
        {
            return;
        }

        _trackEffects.CaptureStates();
        CaptureUndoStateIfNeeded();
        Buses.Remove(bus);
        foreach (var track in Patterns.SelectMany(pattern => pattern.Tracks))
        {
            track.SetSend(bus.BusId, 0);
        }
        ApplyBuses();
        _trackEffects.ReleaseUnusedPlugins(Patterns.SelectMany(pattern => pattern.Tracks));
        StatusMessage = $"{bus.Name} removed";
    }

    private void ApplyBuses()
    {
        _trackEffects.SetBuses(Buses);
        BusesVersion++;
    }

    private void RestoreBuses(IEnumerable<TrackDocument>? documents)
    {
        Buses.Clear();
        foreach (var document in documents ?? Enumerable.Empty<TrackDocument>())
        {
            Buses.Add(ApplyTrackDocument(document));
        }
        ApplyBuses();
    }

    // Unlike a new track, the master starts with an empty rack.
    private static PatternTrackViewModel NewMasterTrack()
    {
        var master = PatternFactory.CreateTrack("Master", "Master", 1, 60, Array.Empty<bool>());
        master.Effects.Clear();
        return master;
    }
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
        _trackEffects = new TrackEffectsHost(_liveInstrumentHost);
        _inputRecorder = new AudioInputRecorder(_liveInstrumentHost, () => AudioOutputSelection.FromSettings(new AppSettingsService().Load()));
        _trackInstruments = new TrackInstrumentsHost(_liveInstrumentHost, track => _trackEffects.ChainFor(track));
        _trackEffects.UseAsMaster(MasterTrack);
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
        _engine.Looped += OnLooped;

        Patterns = BuildDefaultPatterns();
        ArrangeBarNumbers = new ObservableCollection<int>();
        ArrangeRows = new ObservableCollection<ArrangeRowViewModel>();
        SongArrangeRows = new ObservableCollection<SongArrangeRowViewModel>();
        VisibleSongArrangeRows = new ObservableCollection<SongArrangeRowViewModel>();
        ArrangeBlocks = new ObservableCollection<SongArrangeBlockViewModel>();
        Tracks = new ObservableCollection<PatternTrackViewModel>();
        Tracks.CollectionChanged += (_, _) => RefreshSolo();
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
        _engine.PluginControllerRouter = (track, channel, controller, value) =>
        {
            if (_trackInstruments.RunningFor(track) is { } instrument)
            {
                _liveInstrumentHost.TrySendController(instrument, channel, controller, value);
            }
        };
        _clickPlayer.Start();
        _engine.ClickSound = _clickPlayer.Trigger;
        _midiInput.NoteReceived += OnKeyboardNote;
        _midiInput.ControllerReceived += OnKeyboardController;
        _midiInput.MessageReceived += packed => Application.Current?.Dispatcher.BeginInvoke(() => OnMidiMessageReceived(packed));
        _midiActivityTimer.Tick += (_, _) => IsMidiActivityVisible = false;
        _audioMeterTimer.Tick += (_, _) => ShowAudioLevels();
        _audioMeterTimer.Start();
        _automationTimer.Tick += (_, _) => ApplyAutomationAt(IsPlaying ? _engine.LivePosition : CurrentStepIndex);
        _automationTimer.Start();
        _recordRefreshTimer.Tick += (_, _) => RefreshRecordedTrack();
        _autosaveTimer.Tick += (_, _) => AutosaveNow();
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
        ZoomTimelineInCommand = new RelayCommand(_ => TimelineBarWidth = ZoomedBarWidth(TimelineBarWidth, zoomIn: true));
        ZoomTimelineOutCommand = new RelayCommand(_ => TimelineBarWidth = ZoomedBarWidth(TimelineBarWidth, zoomIn: false));
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

    // An edit to the song, or a sound changed in a plug-in's window since it started or was saved.
    public bool HasUnsavedChanges => ProjectSnapshot() != _savedProjectSnapshot || _trackInstruments.AnySoundChanged();

    private void MarkProjectSaved()
    {
        _savedProjectSnapshot = ProjectSnapshot();
        // Saved, opened or new, the project has nothing left to recover.
        _autosave?.Clear();
        _lastAutosaveSnapshot = null;
    }

    // ---- Autosave ----

    // Where the project is autosaved, once the main window has turned autosaving on; none for a view model made
    // in tests, so they never touch a real autosave.
    private ProjectAutosave? _autosave;
    private readonly DispatcherTimer _autosaveTimer = new();
    private string? _lastAutosaveSnapshot;
    private int _autosaveMinutes = ProjectAutosave.DefaultMinutes;

    public IReadOnlyList<int> AutosaveIntervalChoices => ProjectAutosave.IntervalChoices;

    // How often the project is autosaved while it has unsaved changes, in minutes; 0 is off. Kept in the settings.
    public int AutosaveMinutes
    {
        get => _autosaveMinutes;
        set
        {
            if (!ProjectAutosave.IntervalChoices.Contains(value) || !SetProperty(ref _autosaveMinutes, value))
            {
                return;
            }

            var settingsService = new AppSettingsService();
            var settings = settingsService.Load();
            settings.AutosaveMinutes = value;
            settingsService.Save(settings);
            ScheduleAutosave();
            StatusMessage = value == 0 ? "Autosave off" : $"Autosave every {value} min";
        }
    }

    // Starts autosaving into the given place, every AutosaveMinutes as kept in the settings.
    public void UseAutosave(ProjectAutosave autosave)
    {
        _autosave = autosave;
        _autosaveMinutes = ProjectAutosave.MinutesFrom(new AppSettingsService().Load());
        RaisePropertyChanged(nameof(AutosaveMinutes));
        ScheduleAutosave();
    }

    private void ScheduleAutosave()
    {
        _autosaveTimer.Stop();
        if (_autosave is not null && _autosaveMinutes > 0)
        {
            _autosaveTimer.Interval = TimeSpan.FromMinutes(_autosaveMinutes);
            _autosaveTimer.Start();
        }
    }

    // Takes a copy of the project when it has changes not saved, and not autosaved yet either; never while
    // recording, so as not to disturb the take. The project stays unsaved: only the player's saves write its file.
    public void AutosaveNow()
    {
        if (_autosave is null || SelectedPattern is null || IsRecording || !HasUnsavedChanges)
        {
            return;
        }

        if (ProjectSnapshot() == _lastAutosaveSnapshot && !_trackInstruments.AnySoundChanged())
        {
            return;
        }

        try
        {
            // The sounds chosen in the plug-ins go into the copy too.
            _trackInstruments.CaptureStates();
            _trackEffects.CaptureStates();
            _autosave.Write(BuildProjectDocument(), CurrentProjectPath);
            _lastAutosaveSnapshot = ProjectSnapshot();
            StatusMessage = $"Autosaved at {DateTime.Now:HH:mm}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Autosave failed: {ex.Message}";
        }
    }

    // Opens the project autosaved by the last session, which ended without saving it, as unsaved work on the
    // file it came from. The autosave becomes this session's, kept until the project is saved. False when it
    // cannot be read.
    public bool RecoverAutosave(ProjectAutosave autosave, AutosaveInfo info)
    {
        try
        {
            var document = autosave.Load(info);
            _history.Clear();
            RestoreProjectDocument(document);
            CurrentProjectPath = info.ProjectPath;
            StatusMessage = $"Recovered the work autosaved at {info.SavedAt:HH:mm}: save it to keep it";
            SaveProjectCommand?.RaiseCanExecuteChanged();
            SaveProjectAsCommand?.RaiseCanExecuteChanged();
            PasteTrackCommand?.RaiseCanExecuteChanged();
            RaiseHistoryStateChanged();
            _savedProjectSnapshot = null; // not saved yet
            autosave.Adopt(info);
            _lastAutosaveSnapshot = ProjectSnapshot();
            StartSavedInstruments();
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Recover autosave failed", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusMessage = "Recovery failed";
            return false;
        }
    }

    // Closing Sonator properly leaves nothing to recover.
    public void ClearAutosave()
    {
        _autosaveTimer.Stop();
        _autosave?.Clear();
    }

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
            var plugin = new VstPluginViewModel(name, filePath, kind) { IsFavorite = _pluginShelf.IsFavorite(filePath) };
            switch (_vstPluginCheck.KnownResult(filePath))
            {
                case true:
                    VstPlugins.Add(plugin);
                    LearnPluginRole(plugin);
                    break;
                case false:
                    UnavailableVstPluginCount++;
                    break;
                default:
                    toCheck.Add(plugin);
                    break;
            }
        }

        ArrangeVstPlugins();
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
        ArrangeVstPlugins();
        LearnPluginRole(plugin);
    }

    private readonly PluginShelf _pluginShelf = new();

    // Puts the plug-ins in the order the player arranged them, favorites first.
    private void ArrangeVstPlugins()
    {
        var order = _pluginShelf.Arrange(VstPlugins.Select(plugin => plugin.FilePath));
        for (var target = 0; target < order.Count; target++)
        {
            var current = VstPlugins.ToList().FindIndex(plugin => string.Equals(plugin.FilePath, order[target], StringComparison.OrdinalIgnoreCase));
            if (current >= 0 && current != target)
            {
                VstPlugins.Move(current, target);
            }
        }
    }

    // Moves a plug-in in the list to just before another, or to the end when there is none, as dragged there.
    public void MoveVstPlugin(VstPluginViewModel plugin, VstPluginViewModel? before)
    {
        if (ReferenceEquals(plugin, before) || !VstPlugins.Contains(plugin))
        {
            return;
        }

        var order = VstPlugins.Where(other => !ReferenceEquals(other, plugin)).ToList();
        var index = before is null ? order.Count : Math.Max(0, order.IndexOf(before));
        order.Insert(index, plugin);
        _pluginShelf.SetOrder(order.Select(other => other.FilePath));
        ArrangeVstPlugins();
    }

    public void SetVstPluginFavorite(VstPluginViewModel plugin, bool favorite)
    {
        _pluginShelf.SetFavorite(plugin.FilePath, favorite);
        plugin.IsFavorite = favorite;
        ArrangeVstPlugins();
        StatusMessage = favorite ? $"{plugin.Name} added to the favorites" : $"{plugin.Name} removed from the favorites";
    }

    // Finds out, in the background the first time, whether a VST3 plug-in is an instrument or an effect.
    private void LearnPluginRole(VstPluginViewModel plugin)
    {
        if (!string.Equals(plugin.Kind, "VST3", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (_vstPluginCheck.KnownKind(plugin.FilePath) is { } known)
        {
            plugin.Role = known;
            return;
        }

        _ = LearnPluginRoleAsync(plugin);
    }

    private async Task LearnPluginRoleAsync(VstPluginViewModel plugin)
    {
        if (await _vstPluginCheck.KindAsync(plugin.FilePath) is { } kind)
        {
            plugin.Role = kind;
        }
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
            if (!_selectedBlocks.Any(item => ReferenceEquals(item.Track, value)))
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

    // The overall master volume: the higher of the two master faders, which is in every track's gain. Set from a
    // single fader (the main window's) or a project, it moves both faders by the same amount.
    public double MasterVolume
    {
        get => _masterVolume;
        set
        {
            var shift = Math.Clamp(value, 0.0, 1.0) - Math.Max(_masterVolumeLeft, _masterVolumeRight);
            SetMasterFaders(_masterVolumeLeft + shift, _masterVolumeRight + shift);
        }
    }

    // ---- The master's two faders ----

    private double _masterVolumeLeft = 0.75;
    private double _masterVolumeRight = 0.75;
    private bool _isMasterLinked = true;

    // The master's left and right faders, as on a desk's stereo master. While they are linked, moving one moves
    // the other by as much, keeping any difference between them; unlinked, each sets its own side, to put right
    // a mix leaning to one side.
    public double MasterVolumeLeft
    {
        get => _masterVolumeLeft;
        set => SetMasterFaders(value, _isMasterLinked ? _masterVolumeRight + (value - _masterVolumeLeft) : _masterVolumeRight);
    }

    public double MasterVolumeRight
    {
        get => _masterVolumeRight;
        set => SetMasterFaders(_isMasterLinked ? _masterVolumeLeft + (value - _masterVolumeRight) : _masterVolumeLeft, value);
    }

    public bool IsMasterLinked
    {
        get => _isMasterLinked;
        set
        {
            if (SetProperty(ref _isMasterLinked, value))
            {
                StatusMessage = value ? "Master faders linked" : "Master faders unlinked: each sets its own side";
            }
        }
    }

    // The master's level as its readout shows it: one figure while both sides are the same, else both.
    public string MasterVolumeDisplay => Math.Abs(_masterVolumeLeft - _masterVolumeRight) < 0.005
        ? $"{_masterVolumeLeft * 100:0}%"
        : $"{_masterVolumeLeft * 100:0} | {_masterVolumeRight * 100:0}";

    private void SetMasterFaders(double left, double right)
    {
        left = double.IsFinite(left) ? Math.Clamp(left, 0.0, 1.0) : _masterVolumeLeft;
        right = double.IsFinite(right) ? Math.Clamp(right, 0.0, 1.0) : _masterVolumeRight;
        var leftChanged = SetProperty(ref _masterVolumeLeft, left, nameof(MasterVolumeLeft));
        var rightChanged = SetProperty(ref _masterVolumeRight, right, nameof(MasterVolumeRight));
        if (leftChanged || rightChanged)
        {
            RaisePropertyChanged(nameof(MasterVolumeDisplay));
        }

        var top = Math.Max(left, right);
        if (SetProperty(ref _masterVolume, top, nameof(MasterVolume)))
        {
            _engine.UpdateMasterVolume(top);
            _trackEffects.MasterVolume = top;
        }

        _trackEffects.MasterBalance = MasterBalanceOf(left, right);
    }

    // The higher fader sets the master volume; the lower side is turned down from it by the master's balance,
    // which keeps one side at unity and takes the other down by the ratio of the two faders.
    internal static double MasterBalanceOf(double left, double right) =>
        left <= 0 && right <= 0 ? 0 : left >= right ? right / left - 1 : 1 - left / right;

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

    // ---- The loop's range, chosen by dragging along the ruler ----

    private bool _hasLoopRange;

    // True once part of the pattern is chosen to loop: with LOOP on, only the steps from LoopStartStep to
    // LoopEndStep repeat, until playback is stopped; otherwise the whole pattern does.
    public bool HasLoopRange
    {
        get => _hasLoopRange;
        set
        {
            if (SetProperty(ref _hasLoopRange, value))
            {
                _engine.IsLoopRangeActive = value;
                RaisePropertyChanged(nameof(LoopRangeSteps));
            }
        }
    }

    // How many steps the range covers, for drawing it.
    public double LoopRangeSteps => _hasLoopRange ? _loopEndStep - _loopStartStep + 1 : 0;

    // Chooses the steps from start to end (both included) to loop, as the ruler's drag does.
    public void SetLoopRange(int start, int end)
    {
        if (end < start)
        {
            (start, end) = (end, start);
        }

        FitSelectedPatternToArrangeLength(); // the pattern's steps exist as far as the timeline shows
        var last = Math.Max(0, Math.Min(GetSelectedPatternStepCount(), TimelineTotalSteps) - 1);
        start = Math.Clamp(start, 0, last);
        end = Math.Clamp(end, start, last);
        LoopStartStep = start;
        LoopEndStep = end;
        _engine.SetLoopRange(_loopStartStep, _loopEndStep);
        HasLoopRange = true;
        RaisePropertyChanged(nameof(LoopRangeSteps));
        StatusMessage = $"Loop: {DescribeLoopRange()}" + (IsLoopEnabled ? string.Empty : " — turn LOOP on to repeat it");
    }

    public void ClearLoopRange()
    {
        if (!_hasLoopRange)
        {
            return;
        }

        HasLoopRange = false;
        StatusMessage = "Loop range cleared: LOOP repeats the whole pattern";
    }

    private string DescribeLoopRange() =>
        $"bar {_loopStartStep / StepsPerBar + 1}.{_loopStartStep % StepsPerBar + 1} to bar {(_loopEndStep + 1) / StepsPerBar + 1}.{(_loopEndStep + 1) % StepsPerBar + 1}";

    // At each turn of the loop, the clips start again from its start, in time with the notes.
    private void OnLooped(object? sender, int step)
    {
        if (IsPlaying && Tracks.Any(track => !string.IsNullOrEmpty(track.AudioClipPath)))
        {
            SeekAudioTracks(step);
        }
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
                RaisePropertyChanged(nameof(LoopRangeSteps));
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
                RaisePropertyChanged(nameof(LoopRangeSteps));
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

            // The timeline is drawn again only when the arrangement gives way to the pattern: turning the loop on
            // or off changes nothing on it, and drawing it again takes seconds with a long clip.
            var leftArrangeMode = value && _isArrangeMode;
            if (leftArrangeMode)
            {
                _isArrangeMode = false;
                RaisePropertyChanged(nameof(IsArrangeMode));
                RaisePropertyChanged(nameof(ArrangeModeText));
            }

            ConfigurePlaybackSource();
            if (leftArrangeMode)
            {
                RebuildTimeline();
            }

            CurrentStepIndex = 0;
            UpdatePlayhead(0);
            UpdateSongTimelinePosition(0);
            TransportPosition = TransportClock.Zero;
            RaisePropertyChanged(nameof(LoopModeText));
            StatusMessage = value
                ? (_hasLoopRange ? $"Loop ON: repeating {DescribeLoopRange()} until STOP." : "Loop ON: repeating the selected pattern.")
                : "Loop OFF: the selected pattern plays once.";
        }
    }

    public string LoopModeText => IsLoopEnabled ? "ON" : "OFF";

    public IReadOnlyList<int> SixteenthLabels { get; } = Enumerable.Range(1, 16).ToArray();

    // How narrow and how wide a bar of the timeline can be, in pixels.
    public const double MinTimelineBarWidth = 16;
    public const double MaxTimelineBarWidth = 480;

    // One step of zoom: a quarter wider or narrower, so the narrow bars change as visibly as the wide ones.
    public static double ZoomedBarWidth(double width, bool zoomIn) =>
        Math.Clamp(Math.Round(zoomIn ? width * 1.25 : width / 1.25), MinTimelineBarWidth, MaxTimelineBarWidth);

    public double TimelineBarWidth
    {
        get => _timelineBarWidth;
        set
        {
            var clamped = Math.Clamp(value, MinTimelineBarWidth, MaxTimelineBarWidth);
            if (SetProperty(ref _timelineBarWidth, clamped))
            {
                RaisePropertyChanged(nameof(TimelineContentWidth));
                RaisePropertyChanged(nameof(TimelineWorkspaceWidth));
                RaisePropertyChanged(nameof(TimelineSixteenthWidth));
                RaisePropertyChanged(nameof(RulerFlags));
            }
        }
    }

    public double TimelineSixteenthWidth => TimelineBarWidth / StepsPerBar;

    // ---- Tempo changes and markers ----

    private IReadOnlyList<TempoChange> _tempoChanges = Array.Empty<TempoChange>();
    private IReadOnlyList<SongMarker> _markers = Array.Empty<SongMarker>();

    // The song's changes of tempo after its start, in order of step; the project's tempo plays until the first.
    public IReadOnlyList<TempoChange> TempoChanges
    {
        get => _tempoChanges;
        private set
        {
            _tempoChanges = new TempoMap(Tempo, value).Changes;
            RaisePropertyChanged();
            OnTempoMapChanged();
        }
    }

    // The named places of the song, in order of step.
    public IReadOnlyList<SongMarker> Markers
    {
        get => _markers;
        private set
        {
            _markers = value.OrderBy(marker => marker.Step).ToList();
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(RulerFlags));
        }
    }

    public TempoMap CurrentTempoMap => new(Tempo, _tempoChanges);

    private void OnTempoMapChanged()
    {
        _engine.UpdateTempoMap(CurrentTempoMap);
        RaisePropertyChanged(nameof(RulerFlags));
        if (!IsPlaying)
        {
            TransportPosition = TransportClock.FormatPosition(CurrentStepIndex, CurrentTempoMap);
        }
    }

    // The tempo changes and markers as flags on the timeline's ruler, placed for the zoom.
    public IReadOnlyList<RulerFlag> RulerFlags =>
        _tempoChanges.Select(change => new RulerFlag(change.Step, $"{change.Bpm:0.##} BPM", true, change.Step * TimelineSixteenthWidth))
            .Concat(_markers.Select(marker => new RulerFlag(marker.Step, marker.Name, false, marker.Step * TimelineSixteenthWidth)))
            .ToList();

    // From this step on the song plays at bpm. At the very start, that is the project's tempo.
    public void SetTempoChange(int step, double bpm)
    {
        if (!double.IsFinite(bpm) || bpm < MinTempoBpm || bpm > MaxTempoBpm)
        {
            StatusMessage = $"Tempo must be between {MinTempoBpm} and {MaxTempoBpm} BPM.";
            return;
        }

        if (step <= 0)
        {
            Tempo = bpm;
            return;
        }

        CaptureUndoStateIfNeeded();
        TempoChanges = _tempoChanges.Where(change => change.Step != step).Append(new TempoChange(step, bpm)).ToList();
        StatusMessage = $"Tempo {bpm:0.##} BPM from bar {FormatBarBeat(step, StepsPerBar, Meter.StepsPerBeat)}";
    }

    public void RemoveTempoChange(int step)
    {
        if (_tempoChanges.All(change => change.Step != step))
        {
            return;
        }

        CaptureUndoStateIfNeeded();
        TempoChanges = _tempoChanges.Where(change => change.Step != step).ToList();
        StatusMessage = "Tempo change removed";
    }

    // Adds a marker, or renames the one at that step.
    public void SetMarker(int step, string name)
    {
        name = name.Trim();
        if (name.Length == 0)
        {
            return;
        }

        CaptureUndoStateIfNeeded();
        Markers = _markers.Where(marker => marker.Step != step).Append(new SongMarker(Math.Max(0, step), name)).ToList();
    }

    public void RemoveMarker(int step)
    {
        if (_markers.All(marker => marker.Step != step))
        {
            return;
        }

        CaptureUndoStateIfNeeded();
        Markers = _markers.Where(marker => marker.Step != step).ToList();
    }

    // A name for a new marker: Verse, Chorus... are up to the player; this one only needs to be new.
    public string NextMarkerName()
    {
        var number = _markers.Count + 1;
        while (_markers.Any(marker => marker.Name == $"Marker {number}"))
        {
            number++;
        }
        return $"Marker {number}";
    }

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

    // Moves a track's audio clip to start at startStep, on the same track or, when target is another empty
    // track, onto that one, which becomes the audio track. False when nothing moved.
    public bool MoveAudioClip(PatternTrackViewModel track, int startStep, PatternTrackViewModel? target = null)
    {
        if (string.IsNullOrEmpty(track.AudioClipPath))
        {
            return false;
        }

        startStep = Math.Max(0, startStep);
        var destination = target is not null && !ReferenceEquals(target, track) && IsEmptyTrack(target) ? target : track;
        if (ReferenceEquals(destination, track) && startStep == track.AudioClipStartStep)
        {
            return false;
        }

        CaptureUndoStateIfNeeded();
        if (!ReferenceEquals(destination, track))
        {
            destination.AudioClipPath = track.AudioClipPath;
            destination.TrackType = PatternTrackType.AudioInput;
            destination.Instrument = track.Instrument;
            track.AudioClipPath = null;
            track.AudioClipStartStep = 0;
            SelectedTrack = destination;
        }

        destination.AudioClipStartStep = startStep;
        StatusMessage = $"{destination.Name}: the audio clip starts at bar {startStep / 16 + 1}, step {startStep % 16 + 1}";
        LengthenPatternForClip(destination);
        RebuildTimeline();
        return true;
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
        _autosaveTimer.Stop();
        _inputRecorder.Dispose();
        StopTone();
        StopPreview();
        _audioMeterTimer.Stop();
        _automationTimer.Stop();
        _previewer?.Dispose();
        _trackInstruments.Dispose();
        _engine.StepChanged -= OnStepChanged;
        _engine.PatternChanged -= OnPlaybackPatternChanged;
        _engine.PlaybackCompleted -= OnPlaybackCompleted;
        _engine.PlaybackFaulted -= OnPlaybackFaulted;
        _engine.Looped -= OnLooped;
        _midiInput.Dispose();
        _clickPlayer.Dispose();
        _engine.Dispose();
        StopAudioTracks();
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

    // ---- Export ----

    // How long the song lasts, in steps: in arrange mode to its last bar in the arrangement; otherwise to the
    // end of the selected pattern's last note, step or audio clip, at least a bar. Rounded up to the bar.
    internal int ExportLengthSteps()
    {
        double end;
        if (IsArrangeMode)
        {
            var lastBar = SongArrangeRows.Select((row, index) => (row, index))
                .LastOrDefault(item => item.row.Cells.Any(cell => !string.IsNullOrWhiteSpace(cell.Label)));
            end = lastBar.row is null ? 0 : (lastBar.index + 1) * StepsPerBar;
        }
        else
        {
            end = Tracks.Select(TrackEndSteps).DefaultIfEmpty(0).Max();
        }

        var bars = Math.Max(1, (int)Math.Ceiling(end / StepsPerBar));
        return bars * StepsPerBar;
    }

    private double TrackEndSteps(PatternTrackViewModel track)
    {
        if (track.IsMuted)
        {
            return 0;
        }

        var notes = track.MidiNoteEvents.Select(note => note.StepIndex + note.StartOffset + note.DurationSteps + note.LengthFraction)
            .DefaultIfEmpty(0).Max();
        var steps = track.Steps.Where(step => step.IsActive).Select(step => step.Index + (double)Math.Max(1, step.DurationSteps))
            .DefaultIfEmpty(0).Max();
        var clip = 0.0;
        if (track.AudioClipPath is { } path && File.Exists(path))
        {
            try
            {
                using var reader = new NAudio.Wave.MediaFoundationReader(path);
                clip = track.AudioClipStartStep + reader.TotalTime.TotalSeconds * Tempo / 60.0 * 4;
            }
            catch (Exception ex) when (ex is IOException or System.Runtime.InteropServices.COMException or InvalidOperationException)
            {
                // A clip that cannot be read does not make the song longer.
            }
        }

        return Math.Max(notes, Math.Max(steps, clip));
    }

    // Writes the song as a standard MIDI file, to open in another program: in arrange mode the arrangement's
    // patterns one after another, otherwise the selected pattern, with the tempo, its changes and the markers.
    public bool ExportMidi(string path)
    {
        var patterns = new List<(PatternViewModel Pattern, int StartStep, int? LengthSteps)>();
        if (IsArrangeMode && Patterns.Count > 0)
        {
            var start = 0;
            foreach (var pattern in GetArrangePlaybackPatterns())
            {
                var length = Math.Max(1, pattern.Tracks.Select(track => track.Steps.Count).DefaultIfEmpty(16).Max());
                patterns.Add((pattern, start, length));
                start += length;
            }
        }
        else if (SelectedPattern is { } pattern)
        {
            patterns.Add((pattern, 0, null));
        }

        try
        {
            MidiFileExport.Build(patterns, CurrentTempoMap, TimeSignature, _markers).Write(path, overwriteFile: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusMessage = $"MIDI export failed: {ex.Message}";
            return false;
        }

        StatusMessage = $"MIDI exported to {path}";
        return true;
    }

    // True while the song is being exported.
    public bool IsExporting { get; private set; }

    // Plays the song from its start to its end, and two seconds more for reverb and releases to ring out,
    // recording the mix that comes out of the audio output (every plug-in and audio clip, through their
    // effects and volumes), then writes it to path as a 24-bit WAV. A real-time export, as plug-ins play live.
    // Instruments outside Sonator (on the MIDI output) are not heard in it.
    public async Task<bool> ExportAudioAsync(string path)
    {
        if (IsExporting)
        {
            return false;
        }

        if (IsPlaying)
        {
            StopPlayback();
        }

        var lengthSteps = ExportLengthSteps();
        var seconds = CurrentTempoMap.SecondsAt(lengthSteps);
        const double tail = 2.0;
        var wasLooping = IsLoopEnabled;
        var startStep = CurrentStepIndex;
        IsExporting = true;
        try
        {
            IsLoopEnabled = false;
            SeekToStep(0);

            // The plug-ins start first, so the recording begins with the song and not with their loading.
            foreach (var pluginTrack in PlaybackInstrumentTracks(Patterns.SelectMany(pattern => pattern.Tracks), SelectedTrack, recording: false))
            {
                EnsureTrackInstrument(pluginTrack);
            }

            if (!_liveInstrumentHost.TryStartMixCapture(seconds + tail + 5))
            {
                StatusMessage = "Export: the audio engine is not available";
                return false;
            }

            StartPlayback();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.Elapsed.TotalSeconds < seconds && IsPlaying)
            {
                StatusMessage = $"Exporting... {clock.Elapsed.TotalSeconds:0} of {seconds:0} s";
                await Task.Delay(250);
            }

            if (IsPlaying)
            {
                StopPlayback(); // the notes are released and ring out in the tail
            }

            await Task.Delay(TimeSpan.FromSeconds(tail));
            var mix = _liveInstrumentHost.StopMixCapture(out var sampleRate);
            if (mix is null)
            {
                StatusMessage = "Export: nothing was heard. Only plug-in and audio tracks are exported";
                return false;
            }

            await Task.Run(() => WriteWav(path, mix, sampleRate));
            StatusMessage = $"Exported {seconds + tail:0} s to {path}";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _liveInstrumentHost.StopMixCapture(out _);
            StatusMessage = $"Export failed: {ex.Message}";
            return false;
        }
        finally
        {
            IsExporting = false;
            IsLoopEnabled = wasLooping;
            var message = StatusMessage;
            SeekToStep(startStep);
            StatusMessage = message; // the export's outcome, not the playhead's new place
        }
    }

    private static void WriteWav(string path, float[] interleaved, int sampleRate)
    {
        using var writer = new NAudio.Wave.WaveFileWriter(path, new NAudio.Wave.WaveFormat(sampleRate, 24, 2));
        writer.WriteSamples(interleaved, 0, interleaved.Length);
    }

    // ---- Sounds ----

    // The sounds of the installed plug-ins, read once in the background the first time they are asked for.
    private Task<IReadOnlyList<Services.Sounds.SoundEntry>>? _sounds;

    public Task<IReadOnlyList<Services.Sounds.SoundEntry>> SoundsAsync()
    {
        if (_sounds is null || _sounds.IsFaulted)
        {
            var installed = VstPlugins
                .GroupBy(plugin => Path.GetFileNameWithoutExtension(plugin.FilePath.TrimEnd('\\', '/')), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().FilePath, StringComparer.OrdinalIgnoreCase);
            _sounds = Task.Run(() => (IReadOnlyList<Services.Sounds.SoundEntry>)Services.Sounds.ArturiaSoundSource.Read(installed)
                .Concat(Services.Sounds.NksSoundSource.Read(installed))
                .Concat(Services.Sounds.ToontrackGrooveSource.Read(installed))
                .ToList());
        }

        return _sounds;
    }

    // Loads a sound on the selected track: the track takes the sound's plug-in if it plays another, and the
    // plug-in takes the preset. The keyboard then plays it. False, with the reason in the status line, when
    // it cannot.
    public bool LoadSound(Services.Sounds.SoundEntry sound)
    {
        if (SelectedTrack is not { SupportsMidiPlayback: true } track)
        {
            StatusMessage = "Select a MIDI track to load the sound on";
            return false;
        }

        StopSoundPreview();

        if (!string.Equals(track.PluginFilePath, sound.PluginPath, StringComparison.OrdinalIgnoreCase))
        {
            AssignPlugin(track, sound.PluginPath);
        }

        if (EnsureTrackInstrument(track) is not { } instrument)
        {
            return false;
        }

        if (sound.Kind == Services.Sounds.SoundKind.Groove)
        {
            return LoadGroove(track, instrument, sound);
        }

        byte[] preset;
        try
        {
            preset = sound.Kind == Services.Sounds.SoundKind.NksPreset
                ? Services.Sounds.NksSoundSource.StateFor(sound.PresetPath, _liveInstrumentHost.TrySaveState(instrument, 0) ?? Array.Empty<byte>())
                : File.ReadAllBytes(sound.PresetPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            StatusMessage = $"{sound.Name}: the preset could not be read";
            return false;
        }

        if (!_liveInstrumentHost.TryLoadState(instrument, 0, preset))
        {
            StatusMessage = $"{sound.PluginName} did not take the sound {sound.Name}";
            return false;
        }

        LiveInstrumentRegistry.Set(instrument);
        track.SoundName = SoundDisplayName(sound);
        StatusMessage = $"{track.Name} now plays {sound.Name} ({sound.PluginName})";
        return true;
    }

    // A sound's name as a track shows it after its plug-in's: NKS names repeat their family first
    // ("Strings: Discover - Violins 1"), which the track does without ("Discover - Violins 1").
    internal static string SoundDisplayName(Services.Sounds.SoundEntry sound)
    {
        var colon = sound.Name.IndexOf(": ", StringComparison.Ordinal);
        return sound.Kind == Services.Sounds.SoundKind.NksPreset && colon > 0 ? sound.Name[(colon + 2)..] : sound.Name;
    }

    // Previews of the sound browser, in a plug-in instance of their own, made on the first preview.
    private SoundPreviewer? _previewer;

    // Plays a sound before it is loaded: a short phrase, or the start of a groove at the song's tempo.
    public bool PreviewSound(Services.Sounds.SoundEntry sound)
    {
        _previewer ??= new SoundPreviewer(_liveInstrumentHost);
        var error = _previewer.Preview(sound, Tempo);
        StatusMessage = error ?? $"Previewing {sound.Name} ({sound.PluginName})";
        return error is null;
    }

    // The sounds the browser wants ready to be heard at once: the one tried and the next ones in its list.
    public void WantSoundsReady(IReadOnlyList<Services.Sounds.SoundEntry> sounds)
    {
        _previewer ??= new SoundPreviewer(_liveInstrumentHost);
        _previewer.Want(sounds);
    }

    // Gets one sound ready for a preview, in an instance of its own. True when it is ready.
    public bool PrepareSound(Services.Sounds.SoundEntry sound)
    {
        _previewer ??= new SoundPreviewer(_liveInstrumentHost);
        return _previewer.Prepare(sound) is null;
    }

    public bool IsSoundReady(Services.Sounds.SoundEntry sound) => _previewer?.IsReady(sound) == true;

    // Stops the preview playing; with unload, its plug-in instance goes too, as when the browser closes.
    public void StopSoundPreview(bool unload = false)
    {
        if (unload)
        {
            _previewer?.Dispose();
            _previewer = null;
        }
        else
        {
            _previewer?.Stop();
        }
    }

    // A groove's notes go into the track from the start of the bar the playhead is in, each at its exact
    // place, added to the notes already there. One undo takes them all away.
    private bool LoadGroove(PatternTrackViewModel track, Vst3ModuleHandle instrument, Services.Sounds.SoundEntry groove)
    {
        IReadOnlyList<(double Start, double Length, int NoteNumber, int Velocity)> notes;
        try
        {
            notes = Services.Sounds.GrooveReader.Read(groove.PresetPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                       or Melanchall.DryWetMidi.Common.MidiException)
        {
            StatusMessage = $"{groove.Name}: the groove could not be read";
            return false;
        }

        if (notes.Count == 0)
        {
            StatusMessage = $"{groove.Name}: the groove has no notes";
            return false;
        }

        var barStart = CurrentStepIndex / StepsPerBar * StepsPerBar;
        var placed = notes.Select(note =>
        {
            var start = barStart + note.Start;
            var step = (int)Math.Floor(start);
            var wholeSteps = (int)Math.Floor(note.Length);
            return new MidiNoteEventViewModel(step, wholeSteps, note.NoteNumber, note.Velocity, start - step, note.Length - wholeSteps);
        });
        ReplaceTrackNotes(track, track.MidiNoteEvents.Concat(placed).ToList());

        var bars = Math.Max(1, (int)Math.Ceiling(notes.Max(note => note.Start + note.Length) / StepsPerBar));
        LiveInstrumentRegistry.Set(instrument);
        StatusMessage = $"{track.Name}: groove {groove.Name} ({groove.Bank}), {bars} bar{(bars == 1 ? "" : "s")} from bar {barStart / StepsPerBar + 1}, played by {groove.PluginName}";
        return true;
    }

    // The first time a track's FX rack is opened, its SonVerb comes on when it is still the one every new
    // track starts with: switched off, on Small Room, untouched. It costs nothing until then. Opened again,
    // the rack leaves the reverb as the player left it. Returns true when the reverb was switched on.
    public bool SwitchOnDefaultReverb(PatternTrackViewModel track)
    {
        if (track.HasOpenedFxRack)
        {
            return false;
        }

        track.HasOpenedFxRack = true;
        if (track.Effects is not [{ Definition.Kind: EffectKind.Verb, IsEnabled: false, IsPresetModified: false } verb]
            || verb.PresetName != PatternFactory.DefaultVerbPreset)
        {
            return false;
        }

        CaptureUndoStateIfNeeded();
        verb.IsEnabled = true;
        return true;
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

        _trackEffects.CaptureStates(); // undoing brings a plug-in back as it was set
        CaptureUndoStateIfNeeded();
        track.Effects.Remove(effect);
        _trackEffects.ReleaseUnusedPlugins(Patterns.SelectMany(pattern => pattern.Tracks));
        StatusMessage = $"{effect.Name} removed from {track.Name}";
    }

    // The VST3 effects found among the plug-ins, for the racks' add menus.
    public IEnumerable<VstPluginViewModel> EffectPlugins => VstPlugins.Where(plugin => plugin.IsEffect);

    // Adds a VST3 effect at the end of the track's chain. Its plug-in loads at once and starts running.
    public TrackEffectViewModel? AddPluginEffect(PatternTrackViewModel? track, VstPluginViewModel plugin)
    {
        if (track is null)
        {
            return null;
        }

        CaptureUndoStateIfNeeded();
        var effect = TrackEffectViewModel.ForPlugin(plugin.FilePath, plugin.Name);
        _trackEffects.ChainFor(track); // the plug-in loads with the chain, also on a track that is not playing yet
        track.Effects.Add(effect);
        StatusMessage = _trackEffects.IsPluginMissing(effect)
            ? $"{plugin.Name} could not be loaded"
            : $"{plugin.Name} added to {track.Name}";
        return effect;
    }

    // The loaded plug-in of a VST3 effect, for its window. Null when it cannot be loaded.
    public Vst3ModuleHandle? EffectPluginModule(TrackEffectViewModel effect) => _trackEffects.PluginModuleFor(effect);

    public bool IsEffectPluginMissing(TrackEffectViewModel effect) => _trackEffects.IsPluginMissing(effect);

    // Raised before an effect's plug-in is unloaded, so a window showing it closes first.
    public event Action<Vst3ModuleHandle>? EffectPluginUnloading
    {
        add => _trackEffects.PluginUnloading += value;
        remove => _trackEffects.PluginUnloading -= value;
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

    // After the arrangement is rebuilt its selection points at rows that are gone: the selected pattern's
    // first place in the new arrangement is selected instead, as when Sonator starts.
    private void SelectPatternInArrangement()
    {
        _selectedArrangePatternCell = null;
        if (SelectedPattern is { } selectedPattern
            && SongArrangeRows.SelectMany(row => row.Cells)
                .FirstOrDefault(cell => string.Equals(cell.Label, selectedPattern.Slot, StringComparison.OrdinalIgnoreCase)) is { } firstCell)
        {
            SelectArrangeCell(firstCell);
        }
    }

    private void CreateNewProject()
    {
        if (IsPlaying)
        {
            StopPlayback();
        }

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
            SelectPatternInArrangement();
            Tempo = 120;
            IsMasterLinked = true;
            SetMasterFaders(0.75, 0.75);
            RestoreMasterEffects(null);
            RestoreBuses(null);
            TempoChanges = Array.Empty<TempoChange>();
            Markers = Array.Empty<SongMarker>();
            BarCount = 32;
            LoopStartStep = 0;
            LoopEndStep = 15;
            HasLoopRange = false;
            TimeSignature = "4/4";
            CurrentProjectPath = null;
            StatusMessage = "New project created";
        }
        finally
        {
            _isRestoringState = false;
        }

        _trackEffects.ReleaseAllPlugins();
        _trackEffects.KeepOnly(Patterns.SelectMany(pattern => pattern.Tracks));

        SaveProjectCommand?.RaiseCanExecuteChanged();
        SaveProjectAsCommand?.RaiseCanExecuteChanged();
        PasteTrackCommand?.RaiseCanExecuteChanged();
        RaiseHistoryStateChanged();
        ConfigurePlaybackSource();
        RebuildTimeline();
        MarkProjectSaved();

        // The old tracks' plug-ins go with them, and their windows, in view or in the dock, close.
        StopAudioTracks();
        _trackInstruments.KeepOnly(Array.Empty<PatternTrackViewModel>());
        PointKeyboardAtSelectedTrack();
        TracksReplaced?.Invoke();
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

    // Raised before a track's plug-in is unloaded, as when the track takes another plug-in or goes, so a
    // window showing that plug-in closes first.
    public event Action<Vst3ModuleHandle>? TrackInstrumentStopping
    {
        add => _trackInstruments.Stopping += value;
        remove => _trackInstruments.Stopping -= value;
    }

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

    // ---- Blocks of notes on the timeline ----

    // The blocks a track's notes are in (see NoteBlocks.Of).
    public IReadOnlyList<NoteBlock> BlocksOf(PatternTrackViewModel track) => NoteBlocks.Of(track.NoteBlocks, track.MidiNoteEvents, StepsPerBar);

    // The notes copied with Ctrl+C, from the start of the first block copied, the blocks they were in, from there
    // too, and how long they last.
    private IReadOnlyList<MidiNoteEventViewModel>? _copiedNotes;
    private IReadOnlyList<NoteBlock> _copiedBlocks = Array.Empty<NoteBlock>();
    private int _copiedLengthSteps;

    private string Bars(int steps)
    {
        var bars = Math.Max(1, (int)Math.Round(steps / (double)StepsPerBar));
        return bars == 1 ? "1 bar" : $"{bars} bars";
    }

    private static string Blocks(int count) => count == 1 ? "1 block" : $"{count} blocks";

    // The blocks the player has clicked or drawn a rectangle around. They are highlighted, and copy, cut, delete,
    // join and the block handles act on them.
    private readonly List<(PatternTrackViewModel Track, NoteBlock Block)> _selectedBlocks = new();

    public IReadOnlyList<(PatternTrackViewModel Track, NoteBlock Block)> SelectedBlocks => _selectedBlocks;

    // The track of the first block selected, or null when no block is.
    public PatternTrackViewModel? SelectedBlockTrack => _selectedBlocks.Count > 0 ? _selectedBlocks[0].Track : null;

    // Selects one block of a track, the track's last when none is given. With add, the block is added to the
    // blocks selected, or taken from them when it is one already.
    public void SelectBlock(PatternTrackViewModel? track, NoteBlock? block = null, bool add = false)
    {
        if (!add)
        {
            _selectedBlocks.Clear();
        }

        if (track is not null && (block ?? BlocksOf(track).LastOrDefault()) is { LengthSteps: > 0 } chosen)
        {
            var index = _selectedBlocks.FindIndex(item => ReferenceEquals(item.Track, track) && item.Block == chosen);
            if (index < 0)
            {
                _selectedBlocks.Add((track, chosen));
            }
            else if (add)
            {
                _selectedBlocks.RemoveAt(index);
            }
        }

        ShowBlockSelection();
        if (track is not null)
        {
            SelectedTrack = track;
        }
    }

    // Selects the blocks given (a rectangle drawn around them), or with add, adds them to the blocks selected.
    public void SelectBlocks(IEnumerable<(PatternTrackViewModel Track, NoteBlock Block)> blocks, bool add = false)
    {
        if (!add)
        {
            _selectedBlocks.Clear();
        }

        foreach (var item in blocks)
        {
            if (!IsBlockSelected(item.Track, item.Block))
            {
                _selectedBlocks.Add(item);
            }
        }

        ShowBlockSelection();
        if (SelectedBlockTrack is { } track)
        {
            SelectedTrack = track;
        }
    }

    private void ClearBlockSelection()
    {
        _selectedBlocks.Clear();
        ShowBlockSelection();
    }

    private bool IsBlockSelected(PatternTrackViewModel track, NoteBlock block) =>
        _selectedBlocks.Any(item => ReferenceEquals(item.Track, track) && item.Block == block);

    private void ShowBlockSelection()
    {
        foreach (var block in TimelineTracks.SelectMany(lane => lane.Blocks))
        {
            block.IsSelected = IsBlockSelected(block.Track, block.Block);
        }
    }

    // The blocks copy and cut act on: those selected on the first selected block's track, or with none selected,
    // every block of the selected track. Null when there are none.
    private (PatternTrackViewModel Track, IReadOnlyList<NoteBlock> Blocks)? BlocksToCopy()
    {
        if (SelectedBlockTrack is { } track)
        {
            return (track, _selectedBlocks.Where(item => ReferenceEquals(item.Track, track)).Select(item => item.Block).OrderBy(block => block.StartStep).ToList());
        }

        return SelectedTrack is { } selected && BlocksOf(selected) is { Count: > 0 } all ? (selected, all) : null;
    }

    public void CopySelectedNotes()
    {
        if (BlocksToCopy() is not { } work)
        {
            StatusMessage = "Nothing to copy: the selected track has no notes";
            return;
        }

        var (track, blocks) = work;
        var start = blocks[0].StartStep;
        _copiedLengthSteps = blocks.Max(block => block.EndStep) - start;
        _copiedBlocks = blocks.Select(block => block with { StartStep = block.StartStep - start }).ToList();
        _copiedNotes = track.MidiNoteEvents
            .Where(note => blocks.Any(block => block.Holds(note)))
            .Select(note => note with { StepIndex = note.StepIndex - start })
            .ToList();
        StatusMessage = $"Copied {Blocks(blocks.Count)}, {Bars(_copiedLengthSteps)}, from {track.Name}: Ctrl+V puts them after its last block";
    }

    public void CutSelectedNotes()
    {
        if (BlocksToCopy() is not { } work)
        {
            return;
        }

        CopySelectedNotes();
        RemoveBlocks(work.Track, work.Blocks);
        ClearBlockSelection();
        StatusMessage = $"Cut {Blocks(work.Blocks.Count)} from {work.Track.Name}";
    }

    // Delete removes the blocks the player clicked on the timeline. Only clicked blocks are deleted, never a whole
    // track that is merely selected; Undo brings them back.
    public bool DeleteSelectedBlock()
    {
        if (_selectedBlocks.Count == 0)
        {
            return false;
        }

        var count = _selectedBlocks.Count;
        CaptureUndoStateIfNeeded();
        foreach (var group in _selectedBlocks.GroupBy(item => item.Track).ToList())
        {
            RemoveBlocks(group.Key, group.Select(item => item.Block).ToList(), captureUndo: false);
        }

        ClearBlockSelection();
        StatusMessage = $"Deleted {Blocks(count)}. Undo brings {(count == 1 ? "it" : "them")} back.";
        return true;
    }

    // Takes blocks and their notes out of a track; when no note is left, the pedal and wheels played with them go too.
    private void RemoveBlocks(PatternTrackViewModel track, IReadOnlyList<NoteBlock> blocks, bool captureUndo = true)
    {
        if (captureUndo)
        {
            CaptureUndoStateIfNeeded();
        }

        track.NoteBlocks = BlocksOf(track).Where(block => !blocks.Contains(block)).ToList();
        track.MidiNoteEvents = track.MidiNoteEvents.Where(note => !blocks.Any(block => block.Holds(note))).ToList();
        if (track.MidiNoteEvents.Count == 0)
        {
            track.MidiControllerEvents = Array.Empty<MidiControllerEventViewModel>();
        }

        TrackNotesChanged?.Invoke(track);
        RebuildTimeline();
    }

    // Pastes the copied blocks on the selected track right after its last block, so pasting again and again lays
    // the copies one after the other, each a block of its own; on a track with no notes, at the bar the playhead is
    // in. The pattern grows to play them. One undo step.
    public void PasteNotes()
    {
        var track = SelectedTrack;
        if (track is null || _copiedNotes is null)
        {
            return;
        }

        var blocks = BlocksOf(track);
        var at = blocks.Count > 0 ? blocks.Max(block => block.EndStep) : CurrentStepIndex / StepsPerBar * StepsPerBar;
        CaptureUndoStateIfNeeded();
        var pasted = _copiedBlocks.Select(block => block with { StartStep = block.StartStep + at }).ToList();
        track.NoteBlocks = blocks.Concat(pasted).ToList();
        track.MidiNoteEvents = track.MidiNoteEvents.Concat(_copiedNotes.Select(note => note with { StepIndex = note.StepIndex + at })).ToList();
        LengthenPatternTo(track, at + _copiedLengthSteps);
        TrackNotesChanged?.Invoke(track);
        RebuildTimeline();
        SelectBlocks(pasted.Select(block => (track, block)));
        StatusMessage = $"Pasted {Bars(_copiedLengthSteps)} into {track.Name} at bar {at / StepsPerBar + 1}";
    }

    // Lays one more copy of the selected blocks of the track (its last block, when none of its blocks is selected)
    // after its last block. The first of a run copies them first, so the run repeats them as they were, not what
    // the copies have made of the track.
    public void RepeatBlock(PatternTrackViewModel track, bool first)
    {
        if (track.MidiNoteEvents.Count == 0)
        {
            return;
        }

        if (first)
        {
            if (!ReferenceEquals(SelectedBlockTrack, track))
            {
                SelectBlock(track);
            }

            CopySelectedNotes();
        }

        PasteNotes();
    }

    // True when two blocks or more of one track are selected, which Join makes one.
    public bool CanJoinSelectedBlocks => _selectedBlocks.GroupBy(item => item.Track).Any(group => group.Count() > 1);

    // Makes the selected blocks of each track one block, from the first one's start to the last one's end; a block
    // between them is taken in too. One undo step.
    public void JoinSelectedBlocks()
    {
        var groups = _selectedBlocks.GroupBy(item => item.Track).Where(group => group.Count() > 1).ToList();
        if (groups.Count == 0)
        {
            StatusMessage = "To join blocks, select two or more on a track: draw a rectangle around them";
            return;
        }

        CaptureUndoStateIfNeeded();
        var joined = new List<(PatternTrackViewModel Track, NoteBlock Block)>();
        foreach (var group in groups)
        {
            var start = group.Min(item => item.Block.StartStep);
            var end = group.Max(item => item.Block.EndStep);
            var block = new NoteBlock(start, end - start);
            group.Key.NoteBlocks = BlocksOf(group.Key)
                .Where(other => other.EndStep <= start || other.StartStep >= end)
                .Append(block)
                .OrderBy(other => other.StartStep)
                .ToList();
            joined.Add((group.Key, block));
        }

        RebuildTimeline();
        SelectBlocks(joined);
        StatusMessage = joined.Count == 1
            ? $"Joined into one block of {Bars(joined[0].Block.LengthSteps)} on {joined[0].Track.Name}"
            : $"Joined the blocks of {joined.Count} tracks";
    }

    // After a block has changed: the piano roll and the timeline show it, and it stays selected.
    private void ShowChangedBlock(PatternTrackViewModel track, NoteBlock block)
    {
        TrackNotesChanged?.Invoke(track);
        RebuildTimeline();
        SelectBlock(track, block);
    }

    // Moves a block and its notes by a number of steps; the pattern grows to play it. One undo step.
    public void MoveBlock(PatternTrackViewModel track, NoteBlock block, int deltaSteps)
    {
        deltaSteps = Math.Max(deltaSteps, -block.StartStep);
        if (deltaSteps == 0)
        {
            return;
        }

        CaptureUndoStateIfNeeded();
        var moved = block with { StartStep = block.StartStep + deltaSteps };
        track.NoteBlocks = BlocksOf(track).Select(other => other == block ? moved : other).ToList();
        track.MidiNoteEvents = track.MidiNoteEvents
            .Select(note => block.Holds(note) ? note with { StepIndex = note.StepIndex + deltaSteps } : note)
            .ToList();
        LengthenPatternTo(track, moved.EndStep);
        ShowChangedBlock(track, moved);
    }

    // Gives a block new bounds, its notes stretched or squeezed in proportion to fit them. One undo step.
    public void StretchBlock(PatternTrackViewModel track, NoteBlock block, int newStart, int newEnd)
    {
        if (newStart < 0 || newEnd <= newStart || block.LengthSteps <= 0 || (newStart == block.StartStep && newEnd == block.EndStep))
        {
            return;
        }

        CaptureUndoStateIfNeeded();
        var stretched = new NoteBlock(newStart, newEnd - newStart);
        var ratio = stretched.LengthSteps / (double)block.LengthSteps;
        track.NoteBlocks = BlocksOf(track).Select(other => other == block ? stretched : other).ToList();
        track.MidiNoteEvents = track.MidiNoteEvents
            .Select(note => block.Holds(note)
                ? note with
                {
                    StepIndex = Math.Min(newEnd - 1, newStart + (int)Math.Round((note.StepIndex - block.StartStep) * ratio)),
                    DurationSteps = Math.Max(1, (int)Math.Round(note.DurationSteps * ratio)),
                }
                : note)
            .ToList();
        LengthenPatternTo(track, newEnd);
        ShowChangedBlock(track, stretched);
    }

    // Recording is turned on and off here; playback is started by the transport (space bar or START).
    private void SetRecording(bool recording)
    {
        IsRecording = recording;
    }

    // Arming a track turns recording on; recording stops when no track is armed. An audio track records from
    // the sound card's input: arming it opens the input, whose level then shows on the track's meter; one
    // audio track is armed at a time.
    private void ToggleTrackRecordArm(PatternTrackViewModel? track)
    {
        if (track is null)
        {
            return;
        }

        var isAudio = track.TrackType == PatternTrackType.AudioInput;
        if (isAudio && !track.IsRecordArmed)
        {
            foreach (var other in Tracks.Where(item => item.IsRecordArmed && item.TrackType == PatternTrackType.AudioInput))
            {
                other.IsRecordArmed = false;
            }
        }

        track.IsRecordArmed = !track.IsRecordArmed;
        SetRecording(Tracks.Any(item => item.IsRecordArmed));
        StatusMessage = track.IsRecordArmed ? $"{track.Name} armed for recording" : $"{track.Name} disarmed";
        if (isAudio)
        {
            UpdateInputArming();
        }
    }

    // ---- Recording an audio track from the sound card's input ----

    private readonly AudioInputRecorder _inputRecorder;

    // The audio track armed to record, when there is one.
    private PatternTrackViewModel? AudioRecordTarget =>
        Tracks.FirstOrDefault(item => item.IsRecordArmed && item.TrackType == PatternTrackType.AudioInput);

    // The input is open while an audio track is armed.
    private void UpdateInputArming()
    {
        if (AudioRecordTarget is { } track)
        {
            _inputRecorder.Arm();
            if (_inputRecorder.Problem is { } problem)
            {
                StatusMessage = $"{track.Name}: {problem}";
            }
            else if (_inputRecorder.IsArmed)
            {
                StatusMessage = $"{track.Name} armed: it records from the input {(AudioOutputSelection.FromSettings(new AppSettingsService().Load()).UseAsio ? "1 of the ASIO driver" : "Windows uses by default")} when RECORD and START are on";
            }
        }
        else
        {
            _inputRecorder.Disarm();
        }
    }

    // A take starts with playback, while recording, when an audio track is armed.
    private void BeginAudioTake()
    {
        if (IsRecording && AudioRecordTarget is not null && _inputRecorder.IsArmed)
        {
            _inputRecorder.StartTake();
        }
    }

    // When playback stops, the take becomes the armed track's clip, where playback had started; one undo
    // brings back the clip the track had. False when nothing was recorded.
    private bool FinishAudioTake()
    {
        if (!_inputRecorder.IsRecording)
        {
            return false;
        }

        var clip = _inputRecorder.EndTake();
        if (clip is null || AudioRecordTarget is not { } track)
        {
            return false;
        }

        if (!_recordTakeHasNotes)
        {
            CaptureUndoStateIfNeeded();
            _recordTakeHasNotes = true;
        }

        var path = AudioClipLoader.SaveToLibrary(clip);
        _decodedClips[path] = clip;
        _audioWaveforms[path] = AudioWaveformSummary.FromClip(clip);
        track.AudioClipPath = path;
        track.AudioClipStartStep = _playbackStartStep;
        track.Instrument = "Audio clip";
        LengthenPatternForClip(track);
        RebuildTimeline();
        StatusMessage = $"{track.Name}: recorded {clip.Duration.TotalSeconds:0.0} s from bar {_playbackStartStep / StepsPerBar + 1}"
            + (_inputRecorder.Problem is { } problem ? $" ({problem})" : string.Empty);
        return true;
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

    // Runs on the MIDI thread. A controller (sustain pedal, mod wheel, pitch bend...) goes at once to the
    // plug-in the keyboard plays; while recording, the UI thread writes it into the track being recorded.
    private void OnKeyboardController(MidiControllerMessage controller)
    {
        var position = _engine.LivePosition;
        if (IsLiveKeyboardEnabled && LiveInstrumentRegistry.Current is { } instrument)
        {
            _liveInstrumentHost.TrySendController(instrument, controller.Channel, controller.Controller, controller.Value);
        }

        Application.Current?.Dispatcher.BeginInvoke(() => RecordLiveController(controller, position));
    }

    // While recording, a controller moved becomes part of the take, at the playhead's exact place.
    private void RecordLiveController(MidiControllerMessage controller, double position)
    {
        var track = RecordTarget;
        if (!IsRecording || !IsPlaying || track is null || !track.SupportsMidiPlayback)
        {
            return;
        }

        if (!_recordTakeHasNotes)
        {
            CaptureUndoStateIfNeeded(); // one undo for the whole take, notes and controllers
            _recordTakeHasNotes = true;
        }

        track.MidiControllerEvents = track.MidiControllerEvents
            .Append(new MidiControllerEventViewModel(Math.Max(0, position), controller.Controller, controller.Value))
            .ToList();
    }

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
            _trackEffects.CaptureStates();

            // The clips go into the project's audio folder, beside its file, so the song can be moved whole.
            var document = ProjectAudioFiles.Pack(BuildProjectDocument(), path, out var moved);
            _projectFileService.Save(path, document);
            UseProjectCopies(moved);
            CurrentProjectPath = path;
            ThrowAwayUnusedProjectClips(path, ProjectAudioFiles.Unpack(document, path));
            StatusMessage = moved.Count == 0
                ? $"Project saved to {path}"
                : $"Project saved to {path}, its audio clips in \"{Path.GetFileName(ProjectAudioFiles.FolderFor(path))}\" beside it";
            MarkProjectSaved();
            RememberRecentProject(path);
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

        if (dialog.ShowDialog() == true)
        {
            OpenProject(dialog.FileName);
        }
    }

    // After a save, the tracks play the copies in the project's audio folder, so later saves and edits go on
    // from there; the clips already decoded are kept under their new names.
    private void UseProjectCopies(IReadOnlyDictionary<string, string> moved)
    {
        if (moved.Count == 0)
        {
            return;
        }

        foreach (var track in Patterns.SelectMany(pattern => pattern.Tracks).Concat(Buses))
        {
            if (track.AudioClipPath is { } path && moved.TryGetValue(Path.GetFullPath(path), out var copy))
            {
                track.AudioClipPath = copy;
                if (_decodedClips.TryGetValue(path, out var clip))
                {
                    _decodedClips[copy] = clip;
                }

                if (_audioWaveforms.TryGetValue(path, out var waveform))
                {
                    _audioWaveforms[copy] = waveform;
                }
            }
        }
    }

    // The files of the project's audio folder that neither the song nor any undo can play any more go to the
    // Recycle Bin, so the folder does not grow with every edit.
    private void ThrowAwayUnusedProjectClips(string projectPath, ProjectDocument saved)
    {
        foreach (var file in ProjectAudioFiles.Unused(projectPath, _history.States.Append(saved)))
        {
            SendToRecycleBin(file);
        }
    }

    private static bool SendToRecycleBin(string file)
    {
        try
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(file,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            return false;
        }
    }

    // ---- The old audio library ----

    // Where clips were kept for every project before each project had its own audio folder, and where clips
    // still go until their project is saved.
    private static string AudioLibraryFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sonator", "audio");

    // The files of the audio library that nothing known plays: not the song open now nor its undos, not an
    // autosave, not a copied track, not any recent project. Their total size in bytes.
    public (IReadOnlyList<string> Files, long Bytes) UnusedLibraryClips()
    {
        if (!Directory.Exists(AudioLibraryFolder))
        {
            return (Array.Empty<string>(), 0);
        }

        var documents = new List<ProjectDocument> { BuildProjectDocument() };
        documents.AddRange(_history.States);
        foreach (var project in RecentProjects)
        {
            try
            {
                documents.Add(ProjectAudioFiles.Unpack(_projectFileService.Load(project), project));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
            {
                // A recent project that cannot be read keeps nothing.
            }
        }

        var autosaves = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sonator", "Autosave");
        if (Directory.Exists(autosaves))
        {
            foreach (var file in Directory.EnumerateFiles(autosaves, "*.sonator.json"))
            {
                try
                {
                    documents.Add(_projectFileService.Load(file));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
                {
                }
            }
        }

        var wanted = documents.SelectMany(ProjectAudioFiles.ClipsOf)
            .Append(_copiedTrackDocument?.AudioClipPath)
            .OfType<string>()
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unused = Directory.EnumerateFiles(AudioLibraryFolder).Where(file => !wanted.Contains(Path.GetFullPath(file))).ToList();
        return (unused, unused.Sum(file => new FileInfo(file).Length));
    }

    // Sends the files to the Recycle Bin, whence they can be brought back. Returns how many went.
    public int ThrowAwayLibraryClips(IEnumerable<string> files)
    {
        var count = files.Count(SendToRecycleBin);
        StatusMessage = $"{count} unused audio clip(s) moved to the Recycle Bin";
        return count;
    }

    // The projects last saved or opened, the latest first, those still on disk only.
    public IReadOnlyList<string> RecentProjects =>
        new AppSettingsService().Load().RecentProjects.Where(File.Exists).ToList();

    private void RememberRecentProject(string path)
    {
        var settingsService = new AppSettingsService();
        var settings = settingsService.Load();
        settings.RecentProjects = RecentProjectList.WithLatest(settings.RecentProjects, path);
        settingsService.Save(settings);
    }

    // Opens the project file at path in place of the one open now. Returns false when it cannot be read.
    public bool OpenProject(string path)
    {
        try
        {
            var document = ProjectAudioFiles.Unpack(_projectFileService.Load(path), path);
            _history.Clear();
            RestoreProjectDocument(document);
            CurrentProjectPath = path;
            RememberRecentProject(path);
            var missing = ProjectAudioFiles.ClipsOf(document).Count(clip => !File.Exists(clip));
            StatusMessage = missing == 0
                ? $"Project loaded from {path}"
                : $"Project loaded from {path}; {missing} audio clip(s) not found: keep the \"{Path.GetFileName(ProjectAudioFiles.FolderFor(path))}\" folder beside the project";
            SaveProjectCommand?.RaiseCanExecuteChanged();
            SaveProjectAsCommand?.RaiseCanExecuteChanged();
            PasteTrackCommand?.RaiseCanExecuteChanged();
            RaiseHistoryStateChanged();
            MarkProjectSaved();
            StartSavedInstruments();
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Load project failed", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusMessage = "Load failed";
            return false;
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
    // The audio tracks' clips playing in the plug-ins' mix, one module each, until playback stops.
    private readonly List<(Vst3ModuleHandle Module, PatternTrackViewModel Track)> _playingClips = new();

    // True while the clips play through Windows' default output, without the native bridge.
    private bool _clipsOnFallback;

    // The clips decoded last, by file, so playing again or looping does not read them from disk each time.
    private readonly Dictionary<string, AudioClip> _decodedClips = new(StringComparer.OrdinalIgnoreCase);

    private AudioClip DecodedClip(string path)
    {
        if (_decodedClips.TryGetValue(path, out var clip))
        {
            return clip;
        }

        clip = AudioClipLoader.Load(path);
        var inUse = Tracks.Select(track => track.AudioClipPath).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var unused in _decodedClips.Keys.Where(key => !inUse.Contains(key)).ToList())
        {
            _decodedClips.Remove(unused);
        }

        _decodedClips[path] = clip;
        return clip;
    }

    // Plays each audio track's clip from where playback starts, through the track's effects and volume, in
    // the same output as the plug-ins, so they sound together, on ASIO too. Without the native bridge the
    // clips play through Windows' default output instead.
    private void StartAudioTracks() => StartAudioTracks(CurrentStepIndex);

    // Plays the clips from fromStep, as when playback starts or a loop goes back to its start.
    private void StartAudioTracks(int fromStep)
    {
        StopAudioTracks();
        var tempoMap = CurrentTempoMap;
        var selection = AudioOutputSelection.FromSettings(new AppSettingsService().Load());
        var fallback = new List<(AudioClip Clip, NativeEffectChain? Effects)>();
        // Muted and unsoloed clips start too, silent, so they come in when unmuted or soloed while playing.
        // The clip of the track about to be recorded on is not played under the new take.
        var recording = IsRecording && _inputRecorder.IsArmed ? AudioRecordTarget : null;
        foreach (var track in Tracks.Where(track => !string.IsNullOrEmpty(track.AudioClipPath) && !ReferenceEquals(track, recording)))
        {
            AudioClip clip;
            try
            {
                clip = DecodedClip(track.AudioClipPath!);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
            {
                StatusMessage = $"{track.Name}: the audio clip could not be read";
                continue;
            }

            // From the playhead into the clip; negative while the playhead is still before it, so it waits.
            var secondsFromStart = tempoMap.SecondsAt(fromStep) - tempoMap.SecondsAt(track.AudioClipStartStep);
            if (_liveInstrumentHost.TryCreateClip(clip.Samples, clip.Channels, clip.SampleRate, secondsFromStart) is { } module)
            {
                _trackEffects.ChainFor(track)?.AttachTo(module);
                if (_liveInstrumentHost.TryStartAudio(module, selection, out var error))
                {
                    _playingClips.Add((module, track));
                    continue;
                }

                _liveInstrumentHost.UnloadModule(module);
                StatusMessage = $"{track.Name}: the audio clip did not start: {error}";
                continue;
            }

            // Without the bridge, only the clips heard when playing starts.
            if (!_trackEffects.IsSilenced(track)
                && AudioTrackMixer.Mix(new[] { new AudioPlacement(track.AudioClipPath!, track.AudioClipStartStep) }, Tempo, fromStep, _ => clip) is { } mixed)
            {
                fallback.Add((mixed, _trackEffects.ChainFor(track)));
            }
        }

        if (fallback.Count > 0)
        {
            AudioTrackPlayback.Play(fallback);
            _clipsOnFallback = true;
        }
    }

    // Takes the playing clips to fromStep, as at each turn of a loop: the bridge's clips are only told where
    // to go on from; those played without it start again.
    private void SeekAudioTracks(int fromStep)
    {
        if (_clipsOnFallback || _playingClips.Count == 0)
        {
            StartAudioTracks(fromStep);
            return;
        }

        var tempoMap = CurrentTempoMap;
        foreach (var (module, track) in _playingClips)
        {
            _liveInstrumentHost.SeekClip(module, tempoMap.SecondsAt(fromStep) - tempoMap.SecondsAt(track.AudioClipStartStep));
        }
    }

    private void StopAudioTracks()
    {
        AudioTrackPlayback.Stop();
        _clipsOnFallback = false;
        foreach (var (module, _) in _playingClips)
        {
            _liveInstrumentHost.StopAudio(module);
            _liveInstrumentHost.UnloadModule(module);
        }
        _playingClips.Clear();
    }

    // Makes a track play through a plug-in, as when a plug-in is dropped on it from the plug-in list.
    public void AssignPlugin(PatternTrackViewModel track, string pluginPath)
    {
        CaptureUndoStateIfNeeded();
        if (!string.Equals(track.PluginFilePath, pluginPath, StringComparison.OrdinalIgnoreCase))
        {
            // A saved sound belongs to the plug-in it was made in, and so does its name.
            track.PluginState = null;
            track.PluginEditorState = null;
            track.SoundName = null;
        }

        track.PluginFilePath = pluginPath; // the track becomes a VST track
        UpdateInputArming(); // an armed audio track that took a plug-in no longer records from the input
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
        LengthenPatternForClip(track);
        RebuildTimeline();
    }

    // Makes the selected pattern long enough, in whole bars, to play the track's clip through to its end,
    // as a longer recording would: the pattern grows, it never shrinks.
    private void LengthenPatternForClip(PatternTrackViewModel track)
    {
        if (SelectedPattern is not { } pattern || !pattern.Tracks.Contains(track) || LoadAudioWaveform(track) is not { } waveform)
        {
            return;
        }

        var tempoMap = CurrentTempoMap;
        var endStep = tempoMap.StepAt(tempoMap.SecondsAt(track.AudioClipStartStep) + waveform.DurationSeconds);
        if (LengthenPatternTo(track, endStep) is { } bars)
        {
            StatusMessage = $"{StatusMessage}; the pattern now lasts {bars} bars, to play the whole clip";
        }
    }

    // Makes the selected pattern, when it holds the track, last in whole bars at least to endStep: it grows,
    // it never shrinks. The bars it now lasts, or null when it was long enough.
    private int? LengthenPatternTo(PatternTrackViewModel track, double endStep)
    {
        if (SelectedPattern is not { } pattern || !pattern.Tracks.Contains(track))
        {
            return null;
        }

        var bars = (int)Math.Ceiling(endStep / StepsPerBar - 1e-6);
        if (bars <= PatternBarCount(pattern, StepsPerBar))
        {
            return null;
        }

        if (SelectedArrangePatternLength > 0 && bars > SelectedArrangePatternLength)
        {
            ResizeSelectedArrangePattern(bars);
        }

        foreach (var patternTrack in pattern.Tracks)
        {
            while (patternTrack.Steps.Count < bars * StepsPerBar)
            {
                patternTrack.Steps.Add(new StepViewModel(patternTrack.Steps.Count, false));
            }
        }

        ConfigurePlaybackSource();
        return bars;
    }

    // ---- Tempo detection ----

    // The tempo and first beat of the track's clip, as BPM detection finds them; null when it cannot tell.
    public DetectedTempo? DetectClipTempo(PatternTrackViewModel track)
    {
        if (track.AudioClipPath is not { } path || !File.Exists(path))
        {
            return null;
        }

        try
        {
            return TempoDetection.Detect(DecodedClip(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    // Makes the song follow the track's clip: the song's tempo becomes bpm, and the clip is lined up with the
    // beats, starting on a beat with silence put before it, so its beats fall on the grid and the metronome.
    // The clip's file is kept; the lined-up clip is a new one, and one undo brings everything back.
    public void SyncSongToClip(PatternTrackViewModel track, double bpm, double firstBeatSeconds)
    {
        if (track.AudioClipPath is not { } path || !File.Exists(path) || bpm < MinTempoBpm || bpm > MaxTempoBpm)
        {
            return;
        }

        var clip = DecodedClip(path);
        var beat = 60 / bpm;
        var intoBeat = ((firstBeatSeconds % beat) + beat) % beat;
        var lead = intoBeat < 0.002 || beat - intoBeat < 0.002 ? 0 : beat - intoBeat;
        var leadFrames = (int)Math.Round(lead * clip.SampleRate);

        CaptureUndoStateIfNeeded();
        _isRestoringState = true; // one undo step for the tempo, the clip and its place
        try
        {
            Tempo = Math.Round(bpm, 2);
            if (leadFrames > 0)
            {
                var samples = new float[clip.Samples.Length + leadFrames * clip.Channels];
                Array.Copy(clip.Samples, 0, samples, leadFrames * clip.Channels, clip.Samples.Length);
                var lined = new AudioClip(samples, clip.Channels, clip.SampleRate);
                var linedPath = AudioClipLoader.SaveToLibrary(lined);
                _decodedClips[linedPath] = lined;
                _audioWaveforms[linedPath] = AudioWaveformSummary.FromClip(lined);
                track.AudioClipPath = linedPath;
            }

            // On a beat, so the clip's beats fall on the beats of the grid.
            var stepsPerBeat = Math.Max(1, Meter.StepsPerBeat);
            track.AudioClipStartStep = (int)Math.Round(track.AudioClipStartStep / (double)stepsPerBeat) * stepsPerBeat;
            LengthenPatternForClip(track);
        }
        finally
        {
            _isRestoringState = false;
        }

        ConfigurePlaybackSource();
        RebuildTimeline();
        StatusMessage = $"Song tempo set to {Tempo:0.##} BPM, following {track.Name}'s clip"
            + (leadFrames > 0 ? $"; the clip starts {lead * 1000:0} ms later, so its beats fall on the grid" : string.Empty);
    }

    // Puts an edited clip in place of a track's clip, where the old one started; undo brings the old one back.
    public void ReplaceAudioClip(PatternTrackViewModel track, string clipPath)
    {
        CaptureUndoStateIfNeeded();
        track.AudioClipPath = clipPath;
        StatusMessage = $"{track.Name}: the edited clip replaces the old one";
        LengthenPatternForClip(track);
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
            var barsBefore = SelectedPattern is { } before ? PatternBarCount(before, StepsPerBar) : 0;
            AssignAudioClip(track, libraryPath);
            var barsAfter = SelectedPattern is { } after ? PatternBarCount(after, StepsPerBar) : 0;
            StatusMessage = $"{track.Name} now plays {fileName} ({clip.Duration.TotalSeconds:0.00} s)"
                + (barsAfter > barsBefore ? $"; the pattern now lasts {barsAfter} bars, to play it all" : string.Empty);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not load {fileName}: {ex.Message}";
        }
    }

    // File > Import audio: puts the file into the selected track when it is empty, else into the pattern's
    // first empty track, else into a new audio track. False when there is no pattern to put it in.
    public bool ImportAudioFile(string sourcePath)
    {
        var pattern = SelectedPattern;
        if (pattern is null)
        {
            StatusMessage = "Choose a pattern to import the audio into";
            return false;
        }

        var track = SelectedTrack is { } selected && pattern.Tracks.Contains(selected) && IsEmptyTrack(selected)
            ? selected
            : pattern.Tracks.FirstOrDefault(IsEmptyTrack);
        if (track is null)
        {
            if (!TryGetNewTrackDefaults(PatternTrackType.AudioInput, out var name, out var instrument, out var source, out var channel, out var noteNumber)
                || !TryAddTrack(PatternTrackType.AudioInput, name, instrument, source, channel, noteNumber, out _))
            {
                StatusMessage = "No empty track for the audio, and a new one could not be added";
                return false;
            }

            track = SelectedTrack!;
        }

        SelectedTrack = track;
        AssignAudioFile(track, sourcePath);
        return true;
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
            _ = _engine.StartAfterCountInAsync(SelectedCountIn.Bars, () =>
            {
                StartAudioTracks();
                BeginAudioTake();
            });
            return;
        }

        // Looping part of the pattern, playback starts in it.
        if (IsLoopEnabled && _hasLoopRange && (CurrentStepIndex < _loopStartStep || CurrentStepIndex > _loopEndStep))
        {
            SeekToStep(_loopStartStep);
        }

        StartAudioTracks();
        BeginAudioTake();
        _engine.Start();
        IsPlaying = true;
        StatusMessage = _inputRecorder.IsRecording ? $"Recording {AudioRecordTarget?.Name}..." : "Playback started";
    }

    private void StopPlayback()
    {
        var stopPosition = _engine.LivePosition;
        var audioTake = FinishAudioTake();
        var recordedTake = _recordTakeHasNotes || _recordingNotes.Count > 0;
        StopAudioTracks();
        _engine.Stop();
        ReleaseAllPluginNotes();
        RaisePropertyChanged(nameof(IsClickLoopRunning));
        IsPlaying = false;
        if (!audioTake)
        {
            StatusMessage = "Playback paused";
        }

        if (recordedTake)
        {
            var takeMessage = StatusMessage;
            FinishRecordingTake(stopPosition);
            SeekToStep(_playbackStartStep); // back to where the take began, ready to listen to it
            if (audioTake)
            {
                StatusMessage = takeMessage;
            }
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
        TransportPosition = TransportClock.FormatPosition(stepIndex, CurrentTempoMap);
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
        StopAudioTracks();
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
        StopAudioTracks();
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
            return null; // the notes show as blocks, drawn over the lane
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
            var blocks = new List<TimelineBlockViewModel>();
            var shownPatterns = new HashSet<PatternViewModel>();
            var absoluteStep = 0;
            foreach (var pattern in timelinePatterns)
            {
                var track = trackIndex < pattern.Tracks.Count ? pattern.Tracks[trackIndex] : null;
                var isRepeat = !shownPatterns.Add(pattern);
                if (track is not null)
                {
                    blocks.AddRange(BlocksOf(track).Select(block => new TimelineBlockViewModel(track, block, absoluteStep, isRepeat)));
                }

                var timelineStepCount = PatternBarCount(pattern, StepsPerBar) * StepsPerBar;
                for (var stepIndex = 0; stepIndex < timelineStepCount; stepIndex++)
                {
                    var sourceStep = TimelineStepFor(track, stepIndex);
                    timelineSteps.Add(new ArrangeTimelineStepViewModel(absoluteStep++, sourceStep));
                }
            }

            TimelineTracks.Add(new ArrangeTimelineTrackViewModel(sourceTrack, timelineSteps, LoadAudioWaveform(sourceTrack), Tempo) { Blocks = blocks });
        }

        RaisePropertyChanged(nameof(TimelineContentWidth));
        RaisePropertyChanged(nameof(TimelineWorkspaceWidth));
        UpdateSongTimelinePosition(CurrentStepIndex);
        ShowBlockSelection();

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

    private int _timelinePlayheadStep;

    // Where the song plays on the timeline, in steps, for the playhead line drawn over all the lanes.
    public int TimelinePlayheadStep
    {
        get => _timelinePlayheadStep;
        private set => SetProperty(ref _timelinePlayheadStep, value);
    }

    private void UpdateSongTimelinePosition(int absoluteStep)
    {
        TimelinePlayheadStep = Math.Max(0, absoluteStep);

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

        _trackEffects.CaptureStates(); // the copy takes the effects' plug-ins as they sound now
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
        TransportPosition = TransportClock.FormatPosition(targetStep, CurrentTempoMap);
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
        RefreshSolo();
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
        RefreshSolo();
        StatusMessage = track.IsSolo ? $"{track.Name} soloed" : $"{track.Name} solo disabled";
    }

    // The tracks that make their own sound follow the pattern's solos, as the MIDI tracks do.
    private void RefreshSolo() => _trackEffects.IsSoloActive = Tracks.Any(track => track.IsSolo);

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

        track.SignalLevel = Math.Max(track.SignalLevel, note.Velocity / 127.0 * track.MixVolume);
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

        (MasterPeakLeft, MasterPeakRight) = _trackEffects.TakeMasterPeaks();
        ShowMasterLevels();

        // The armed audio track's meter shows what comes in; a take collects it as it comes.
        if (_inputRecorder.IsArmed && AudioRecordTarget is { } armed)
        {
            _inputRecorder.Collect();
            TrackMeterCalculator.ShowAudioLevel(armed, _inputRecorder.PeakSinceLastAsked());
        }
    }

    private double _masterPeakLeft;
    private double _masterPeakRight;

    // The whole mix's highest left and right samples over the last twentieth of a second, for the
    // master's VU meters.
    public double MasterPeakLeft
    {
        get => _masterPeakLeft;
        private set => SetProperty(ref _masterPeakLeft, value);
    }

    public double MasterPeakRight
    {
        get => _masterPeakRight;
        private set => SetProperty(ref _masterPeakRight, value);
    }

    private double _masterLevelLeft;
    private double _masterLevelRight;

    // The master's LED meters, left and right, from 0 to 1 on the meters' dB scale: what the mix's two sides
    // measure, or the level worked out for the MIDI tracks when that is higher. They jump up at once and fall
    // back gently, as the effects' meters.
    public double MasterLevelLeft
    {
        get => _masterLevelLeft;
        private set => SetProperty(ref _masterLevelLeft, value);
    }

    public double MasterLevelRight
    {
        get => _masterLevelRight;
        private set => SetProperty(ref _masterLevelRight, value);
    }

    private const double MasterMeterFall = 0.04;

    // A sample this close to full scale (-0.1 dBFS) counts as clipping.
    internal const double ClipPeak = 0.989;
    private static readonly TimeSpan ClipHold = TimeSpan.FromSeconds(1.5);
    private DateTime _clipLeftUntil;
    private DateTime _clipRightUntil;
    private bool _isMasterClippingLeft;
    private bool _isMasterClippingRight;

    // The red overload lamps over the VU meters: lit when that side of the mix reaches full scale, and held
    // a moment so a short clip is seen.
    public bool IsMasterClippingLeft
    {
        get => _isMasterClippingLeft;
        private set => SetProperty(ref _isMasterClippingLeft, value);
    }

    public bool IsMasterClippingRight
    {
        get => _isMasterClippingRight;
        private set => SetProperty(ref _isMasterClippingRight, value);
    }

    private void ShowMasterLevels()
    {
        var now = DateTime.UtcNow;
        if (_masterPeakLeft >= ClipPeak)
        {
            _clipLeftUntil = now + ClipHold;
        }

        if (_masterPeakRight >= ClipPeak)
        {
            _clipRightUntil = now + ClipHold;
        }

        IsMasterClippingLeft = now < _clipLeftUntil;
        IsMasterClippingRight = now < _clipRightUntil;
        MasterLevelLeft = Math.Max(Math.Max(TrackEffectViewModel.ToMeter(_masterPeakLeft), _masterLevelLeft - MasterMeterFall), MasterSignalLevel);
        MasterLevelRight = Math.Max(Math.Max(TrackEffectViewModel.ToMeter(_masterPeakRight), _masterLevelRight - MasterMeterFall), MasterSignalLevel);
    }

    // ---- The master's spectrum analyser ----

    private readonly SpectrumAnalysis _spectrum = new();
    private readonly float[] _scope = new float[SpectrumAnalysis.Size];

    // What goes out to the audio output, as the master's LED analyser shows it: each band's level in dBFS.
    public bool ReadSpectrum(double[] levelsDb)
    {
        if (_liveInstrumentHost.ReadOutputScope(_scope, out var sampleRate) < _scope.Length || sampleRate <= 0)
        {
            return false;
        }

        _spectrum.Analyse(_scope, sampleRate, levelsDb);
        return true;
    }

    // ---- Previewing a clip, as the waveform editor does ----

    private Vst3ModuleHandle? _previewModule;

    // Plays interleaved samples once through Sonator's own output, the one the plug-ins and the clips use (ASIO
    // too), only to be heard: past the master, and never in an export. False without the native bridge.
    public bool StartPreview(float[] interleaved, int channels, int sampleRate)
    {
        StopPreview();
        if (interleaved.Length < channels
            || _liveInstrumentHost.TryCreateClip(interleaved, channels, sampleRate, 0) is not { } module)
        {
            return false;
        }

        _liveInstrumentHost.SetClipMonitor(module, true, 0);
        if (!_liveInstrumentHost.TryStartAudio(module, AudioOutputSelection.FromSettings(new AppSettingsService().Load()), out _))
        {
            _liveInstrumentHost.UnloadModule(module);
            return false;
        }

        _previewModule = module;
        return true;
    }

    public void StopPreview()
    {
        if (_previewModule is { } module)
        {
            _previewModule = null;
            _liveInstrumentHost.StopAudio(module);
            _liveInstrumentHost.UnloadModule(module);
        }
    }

    // ---- The master's oscillator ----

    // The test tone, as a clip of the bridge's playing round and round through the master, while it is on.
    private Vst3ModuleHandle? _toneModule;
    private bool _isToneOn;
    private int _toneFrequency = 1000;
    private double _toneLevelDbfs = TestTone.DefaultLevelDbfs;
    private double _vuReferenceDbfs = new AppSettingsService().Load().VuReferenceDbfs ?? VuMeter.DefaultReferenceDbfs;

    public const double MinVuReferenceDbfs = -24;
    public const double MaxVuReferenceDbfs = -12;

    // Sends the oscillator's tone through the master, to line the VU meters up: with the tone at the level 0 VU
    // stands for, the CAL knob is turned until the needles sit on 0.
    public bool IsToneOn
    {
        get => _isToneOn;
        set
        {
            if (!SetProperty(ref _isToneOn, value))
            {
                return;
            }

            if (value)
            {
                StartTone();
            }
            else
            {
                StopTone();
                StatusMessage = "Test tone off";
            }
        }
    }

    // The tone's frequency: 100, 1000, 10000 or 20000 Hz.
    public int ToneFrequency
    {
        get => _toneFrequency;
        set
        {
            if (TestTone.Frequencies.Contains(value) && SetProperty(ref _toneFrequency, value) && _isToneOn)
            {
                StartTone();
            }
        }
    }

    // How loud the tone is heard, in dBFS at its peak. The meters see it at TestTone.ReferenceLevelDbfs
    // whatever this is, so turning it never moves the needles.
    public double ToneLevelDbfs
    {
        get => _toneLevelDbfs;
        set
        {
            var level = Math.Round(Math.Clamp(value, TestTone.MinLevelDbfs, TestTone.MaxLevelDbfs), 1);
            if (!SetProperty(ref _toneLevelDbfs, level))
            {
                return;
            }

            RaisePropertyChanged(nameof(ToneLevelPosition));
            RaisePropertyChanged(nameof(ToneLevelDisplay));
            if (_toneModule is { } module)
            {
                _liveInstrumentHost.SetClipPlayback(module, true, TestTone.GainOf(level));
            }
        }
    }

    // The level knob's travel, 0 to 1.
    public double ToneLevelPosition
    {
        get => (_toneLevelDbfs - TestTone.MinLevelDbfs) / (TestTone.MaxLevelDbfs - TestTone.MinLevelDbfs);
        set => ToneLevelDbfs = TestTone.MinLevelDbfs + Math.Clamp(value, 0, 1) * (TestTone.MaxLevelDbfs - TestTone.MinLevelDbfs);
    }

    public string ToneLevelDisplay => $"{_toneLevelDbfs:0.0} dBFS";

    // The level in dBFS that reads 0 on the master's VU meters, as the CAL knob sets it; kept in the settings,
    // as a calibration is.
    public double VuReferenceDbfs
    {
        get => _vuReferenceDbfs;
        set
        {
            var reference = Math.Round(Math.Clamp(value, MinVuReferenceDbfs, MaxVuReferenceDbfs), 1);
            if (!SetProperty(ref _vuReferenceDbfs, reference))
            {
                return;
            }

            RaisePropertyChanged(nameof(VuReferencePosition));
            RaisePropertyChanged(nameof(VuReferenceDisplay));
            var settingsService = new AppSettingsService();
            var settings = settingsService.Load();
            settings.VuReferenceDbfs = reference;
            settingsService.Save(settings);
        }
    }

    // The CAL knob's travel, 0 to 1, its middle at -18 dBFS.
    public double VuReferencePosition
    {
        get => (_vuReferenceDbfs - MinVuReferenceDbfs) / (MaxVuReferenceDbfs - MinVuReferenceDbfs);
        set => VuReferenceDbfs = MinVuReferenceDbfs + Math.Clamp(value, 0, 1) * (MaxVuReferenceDbfs - MinVuReferenceDbfs);
    }

    public string VuReferenceDisplay => $"0 VU = {_vuReferenceDbfs:0.0} dBFS";

    // Starts the tone at its frequency and level, in place of the one playing, in the same output as the
    // plug-ins; it opens that output when nothing else plays.
    private void StartTone()
    {
        StopTone();
        if (_liveInstrumentHost.TryCreateClip(TestTone.Cycle(_toneFrequency), 2, TestTone.SampleRate, 0) is not { } module)
        {
            StatusMessage = "The test tone needs Sonator's audio engine (VstHostBridge.dll)";
            SetProperty(ref _isToneOn, false, nameof(IsToneOn));
            return;
        }

        _liveInstrumentHost.SetClipPlayback(module, true, TestTone.GainOf(_toneLevelDbfs));
        _liveInstrumentHost.SetClipMonitor(module, true, TestTone.GainOf(TestTone.ReferenceLevelDbfs));
        if (!_liveInstrumentHost.TryStartAudio(module, AudioOutputSelection.FromSettings(new AppSettingsService().Load()), out var error))
        {
            _liveInstrumentHost.UnloadModule(module);
            StatusMessage = $"The test tone did not start: {error}";
            SetProperty(ref _isToneOn, false, nameof(IsToneOn));
            return;
        }

        _toneModule = module;
        StatusMessage = $"Test tone: {TestTone.Label(_toneFrequency)}Hz, at {TestTone.ReferenceLevelDbfs:0} dBFS on the meters: turn CAL until they read 0";
    }

    private void StopTone()
    {
        if (_toneModule is { } module)
        {
            _toneModule = null;
            _liveInstrumentHost.StopAudio(module);
            _liveInstrumentHost.UnloadModule(module);
        }
    }

    // ---- Automation ----

    // Moves each track's volume and pan to its automation where the song is: often enough while it plays for
    // fades to sound smooth (the bridge glides between the values), and where the playhead stands when stopped.
    private readonly DispatcherTimer _automationTimer = new() { Interval = TimeSpan.FromMilliseconds(15) };

    internal void ApplyAutomationAt(double step)
    {
        foreach (var track in Tracks)
        {
            track.AutomatedVolume = Automation.ValueAt(track.VolumeAutomation, step);
            track.AutomatedPan = Automation.ValueAt(track.PanAutomation, step);
        }
    }

    // Shows the track's volume or pan automation over its row in the timeline, or hides it (None).
    public void ShowAutomation(PatternTrackViewModel? track, AutomationTarget target)
    {
        if (track is null)
        {
            return;
        }

        track.ShownAutomation = target;
        StatusMessage = target switch
        {
            AutomationTarget.Volume => $"{track.Name}: volume automation. Click to add a point, drag to move it, right-click to remove it",
            AutomationTarget.Pan => $"{track.Name}: pan automation. Click to add a point, drag to move it, right-click to remove it",
            _ => $"{track.Name}: automation hidden",
        };
    }

    // Called as a point is added, moved or removed in a lane, before the change, so it can be undone.
    public void BeginAutomationEdit() => CaptureUndoStateIfNeeded();

    // Removes the points of the lane shown on the track; its fader sets the value again.
    public void ClearShownAutomation(PatternTrackViewModel? track)
    {
        if (track is null || track.ShownAutomationPoints.Count == 0)
        {
            return;
        }

        CaptureUndoStateIfNeeded();
        track.ShownAutomationPoints = Array.Empty<AutomationPoint>();
        StatusMessage = $"{track.Name}: {track.ShownAutomation.ToString().ToLowerInvariant()} automation cleared";
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

        // The arrangement's cells hold the pattern's slot ("01"); older projects hold its name.
        if (SelectedPattern is not { } selectedPattern)
        {
            return false;
        }

        for (var row = 0; row < SongArrangeRows.Count; row++)
        {
            for (var candidateColumn = 0; candidateColumn < SongArrangeRows[row].Cells.Count; candidateColumn++)
            {
                var label = SongArrangeRows[row].Cells[candidateColumn].Label;
                if (string.Equals(label, selectedPattern.Slot, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(label, selectedPattern.Name, StringComparison.OrdinalIgnoreCase))
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
            MasterVolume,
            BuildEffectDocuments(MasterTrack.Effects),
            Buses.Count == 0 ? null : Buses.Select(BuildTrackDocument).ToList(),
            _tempoChanges.Count == 0 ? null : _tempoChanges.Select(change => new TempoChangeDocument(change.Step, change.Bpm)).ToList(),
            _markers.Count == 0 ? null : _markers.Select(marker => new MarkerDocument(marker.Step, marker.Name)).ToList(),
            MasterVolumeLeft,
            MasterVolumeRight,
            IsMasterLinked,
            HasLoopRange);
    }

    private void RestoreMasterEffects(IEnumerable<EffectDocument>? documents)
    {
        MasterTrack.Effects.Clear();
        foreach (var effect in BuildEffects(documents))
        {
            MasterTrack.Effects.Add(effect);
        }
    }

    // keepInstruments: the tracks' running plug-ins go on playing in the tracks that take their places, as for
    // undo and redo. Otherwise, as for a project opened, each track starts its plug-in with its saved sound.
    private void RestoreProjectDocument(ProjectDocument document, bool keepInstruments = false)
    {
        var previousTracks = Patterns.Select(pattern => pattern.Tracks.ToList()).ToList();
        if (!keepInstruments)
        {
            _trackEffects.ReleaseAllPlugins(); // another project: its effects start from their saved settings
        }
        _isRestoringState = true;
        try
        {
            CurrentStepIndex = 0;
            TransportPosition = TransportClock.Zero;
            Tempo = document.Tempo;
            BarCount = document.BarCount;
            TimeSignature = document.TimeSignature;
            // Projects saved before the master had two faders have only the one volume.
            IsMasterLinked = document.MasterFadersLinked;
            SetMasterFaders(document.MasterVolumeLeft ?? document.MasterVolume, document.MasterVolumeRight ?? document.MasterVolume);
            RestoreMasterEffects(document.MasterEffects);
            RestoreBuses(document.Buses);
            TempoChanges = document.TempoChanges?.Select(change => new TempoChange(change.Step, change.Bpm)).ToList()
                ?? (IReadOnlyList<TempoChange>)Array.Empty<TempoChange>();
            Markers = document.Markers?.Select(marker => new SongMarker(marker.Step, marker.Name)).ToList()
                ?? (IReadOnlyList<SongMarker>)Array.Empty<SongMarker>();

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

            SelectPatternInArrangement();
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

            HasLoopRange = document.HasLoopRange;

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

            // The automation lanes open stay open on the tracks in their places.
            for (var p = 0; p < previousTracks.Count && p < Patterns.Count; p++)
            {
                for (var t = 0; t < previousTracks[p].Count && t < Patterns[p].Tracks.Count; t++)
                {
                    Patterns[p].Tracks[t].ShownAutomation = previousTracks[p][t].ShownAutomation;
                }
            }
        }
        else
        {
            _trackInstruments.KeepOnly(Array.Empty<PatternTrackViewModel>());
        }

        // The old tracks' chains go; their plug-in effects stay with the tracks that took their places.
        _trackEffects.KeepOnly(Patterns.SelectMany(pattern => pattern.Tracks));
        ApplyAutomationAt(CurrentStepIndex);
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
                if (Patterns.Any(pattern => pattern.Tracks.Contains(track)) && EnsureTrackInstrument(track) is not null)
                {
                    SavedInstrumentStarted?.Invoke(track);
                }
            });
        }
    }

    // Raised when a plug-in of a project just opened has started with its saved sound, so the window can
    // show it in the dock.
    public event Action<PatternTrackViewModel>? SavedInstrumentStarted;

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
