using System.Collections.ObjectModel;
using System.Windows.Media;
using Sonator.Models;

namespace Sonator.ViewModels;

public sealed class PatternTrackViewModel : ViewModelBase
{
    private string _name;
    private string _instrument;
    private int _channel;
    private int _programNumber;
    private int _noteNumber;
    private PatternTrackType _trackType;
    private string _source;
    private bool _isMuted;
    private bool _isSolo;
    private bool _isSelected;
    private bool _isRecordArmed;
    private double _volume;
    private double _signalLevel;
    private SolidColorBrush _trackColorBrush;
    private IReadOnlyList<MidiNoteEventViewModel> _midiNoteEvents;

    public PatternTrackViewModel(
        string name,
        string instrument,
        int channel,
        int noteNumber,
        ObservableCollection<StepViewModel> steps,
        int programNumber = 0,
        IReadOnlyList<MidiNoteEventViewModel>? midiNoteEvents = null,
        PatternTrackType trackType = PatternTrackType.MidiChannel,
        string? source = null,
        double volume = 0.75,
        bool isRecordArmed = false,
        SolidColorBrush? trackColorBrush = null)
    {
        _name = name;
        _instrument = instrument;
        _channel = channel;
        _noteNumber = noteNumber;
        _programNumber = programNumber;
        _trackType = trackType;
        _source = source ?? string.Empty;
        _volume = Math.Clamp(volume, 0.0, 1.0);
        _isRecordArmed = isRecordArmed;
        _trackColorBrush = trackColorBrush ?? new SolidColorBrush(Color.FromRgb(38, 53, 69));
        Steps = steps;
        _midiNoteEvents = midiNoteEvents ?? Array.Empty<MidiNoteEventViewModel>();
        Effects.CollectionChanged += (_, args) =>
        {
            foreach (TrackEffectViewModel effect in args.OldItems ?? Array.Empty<TrackEffectViewModel>())
            {
                effect.Changed -= OnEffectChanged;
            }
            foreach (TrackEffectViewModel effect in args.NewItems ?? Array.Empty<TrackEffectViewModel>())
            {
                effect.Changed += OnEffectChanged;
            }
            EffectsChanged?.Invoke(this, new TrackEffectChange(null, -1));
            RaisePropertyChanged(nameof(HasActiveEffects));
        };
    }

    // True when at least one of the track's effects is switched on: its FX button is lit.
    public bool HasActiveEffects => Effects.Any(effect => effect.IsEnabled);

    // The track's insert effects, in the order the audio goes through them.
    public ObservableCollection<TrackEffectViewModel> Effects { get; } = new();

    private bool _isFxRackOpen;

    // True while the track's FX rack window is open: its FX button is lit.
    public bool IsFxRackOpen
    {
        get => _isFxRackOpen;
        set => SetProperty(ref _isFxRackOpen, value);
    }

    // Raised when an effect is added, removed or moved (Effect null), switched (ParameterIndex -1) or turned.
    public event EventHandler<TrackEffectChange>? EffectsChanged;

    private void OnEffectChanged(TrackEffectViewModel effect, int parameterIndex)
    {
        EffectsChanged?.Invoke(this, new TrackEffectChange(effect, parameterIndex));
        if (parameterIndex < 0)
        {
            RaisePropertyChanged(nameof(HasActiveEffects)); // switched on or off
        }
    }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public string Instrument
    {
        get => _instrument;
        set => SetProperty(ref _instrument, value);
    }

    public int Channel
    {
        get => _channel;
        set
        {
            if (SetProperty(ref _channel, Math.Clamp(value, 1, 16)))
            {
                RaisePropertyChanged(nameof(RoutingDisplay));
            }
        }
    }

    public int NoteNumber
    {
        get => _noteNumber;
        set => SetProperty(ref _noteNumber, value);
    }

    public int ProgramNumber
    {
        get => _programNumber;
        set => SetProperty(ref _programNumber, Math.Clamp(value, 0, 127));
    }

    public PatternTrackType TrackType
    {
        get => _trackType;
        set
        {
            if (SetProperty(ref _trackType, value))
            {
                RaisePropertyChanged(nameof(DisplayTrackType));
                RaisePropertyChanged(nameof(RoutingDisplay));
                RaisePropertyChanged(nameof(SupportsMidiPlayback));
            }
        }
    }

    private string? _pluginFilePath;

    // The VST3 plug-in this track plays through. Null when the track has no plug-in assigned. Given a plug-in,
    // a MIDI track, or an audio track with no clip, becomes a VST track, its instrument named after the plug-in
    // while it still bore its type's default name.
    public string? PluginFilePath
    {
        get => _pluginFilePath;
        set
        {
            if (SetProperty(ref _pluginFilePath, string.IsNullOrWhiteSpace(value) ? null : value))
            {
                if (_pluginFilePath is not null
                    && (TrackType == PatternTrackType.MidiChannel
                        || (TrackType == PatternTrackType.AudioInput && string.IsNullOrEmpty(AudioClipPath))))
                {
                    var defaultName = string.IsNullOrWhiteSpace(Instrument)
                        || Instrument == NewTrackDefaults.InstrumentName(TrackType)
                        || Instrument == NewTrackDefaults.InstrumentName(PatternTrackType.VstInstrument);
                    TrackType = PatternTrackType.VstInstrument;
                    if (defaultName)
                    {
                        Instrument = PluginName ?? Instrument;
                    }
                }

                RaisePropertyChanged(nameof(PluginName));
                RaisePropertyChanged(nameof(PluginAndSoundName));
                RaisePropertyChanged(nameof(HasPlugin));
            }
        }
    }

    private string? _soundName;

    // The sound last loaded on the track from the sound browser, such as "Grand Piano". Null when none is.
    public string? SoundName
    {
        get => _soundName;
        set
        {
            if (SetProperty(ref _soundName, string.IsNullOrWhiteSpace(value) ? null : value))
            {
                RaisePropertyChanged(nameof(PluginAndSoundName));
            }
        }
    }

    // What the track shows under its name: the plug-in, and the sound loaded from the browser after a dash,
    // as "Emulator II V - Grand Piano".
    public string? PluginAndSoundName => PluginName is null
        ? null
        : SoundName is null ? PluginName : $"{PluginName} - {SoundName}";

    public bool HasPlugin => PluginFilePath is not null;

    // The plug-in's own state, the sound and settings chosen in it, as taken when the project was last saved:
    // the processor's part and the editor's. The plug-in gets them back when it starts. Null when there is none.
    public byte[]? PluginState { get; set; }

    public byte[]? PluginEditorState { get; set; }

    // True once the track's FX rack has been opened in this session (see MainViewModel.SwitchOnDefaultReverb).
    public bool HasOpenedFxRack { get; set; }

    // The plug-in's name as shown on the track: its file or bundle name without the extension.
    public string? PluginName => PluginFilePath is null
        ? null
        : System.IO.Path.GetFileNameWithoutExtension(PluginFilePath.TrimEnd('\\', '/'));

    private string? _instrumentIconKey;

    // The instrument icon shown under the track's name, by its key in InstrumentIcons. Null when none is chosen.
    public string? InstrumentIconKey
    {
        get => _instrumentIconKey;
        set
        {
            if (SetProperty(ref _instrumentIconKey, InstrumentIcons.Find(value)?.Key))
            {
                RaisePropertyChanged(nameof(InstrumentIcon));
            }
        }
    }

    public InstrumentIcon? InstrumentIcon => InstrumentIcons.Find(_instrumentIconKey);

    // True when Sonator makes this track's sound itself, through a plug-in or an audio clip: its meter
    // then shows the real signal. Other tracks send MIDI out, and their meter follows the notes.
    public bool HasOwnAudio => HasPlugin || !string.IsNullOrEmpty(AudioClipPath);

    // A WAV file with the audio clip this track plays, when it was filled from the waveform editor.
    public string? AudioClipPath { get; set; }

    // Where the clip starts on the timeline, in sixteenth-note steps from the start of the pattern.
    public int AudioClipStartStep { get; set; }

    public string Source
    {
        get => _source;
        set
        {
            if (SetProperty(ref _source, value ?? string.Empty))
            {
                RaisePropertyChanged(nameof(RoutingDisplay));
            }
        }
    }

    public string DisplayTrackType => TrackType switch
    {
        PatternTrackType.VstInstrument => "VST",
        PatternTrackType.AudioInput => "AUDIO",
        _ => "MIDI"
    };

    public string RoutingDisplay => string.IsNullOrWhiteSpace(Source)
        ? (TrackType == PatternTrackType.MidiChannel ? $"CH {Channel}" : string.Empty)
        : Source;

    public bool SupportsMidiPlayback => TrackType != PatternTrackType.AudioInput;

    public ObservableCollection<StepViewModel> Steps { get; }

    // The controllers played with the notes, as recorded: sustain pedal, mod wheel, pitch bend...
    public IReadOnlyList<MidiControllerEventViewModel> MidiControllerEvents { get; set; } = Array.Empty<MidiControllerEventViewModel>();

    // ---- A drum kit's pieces ----

    // The plug-in's other outputs as channels of their own: a drum plug-in set to play each drum on its own
    // output (the kick, the snare, the hi-hat...) has each one here, with its own fader, effects and sends. Each
    // takes the output KitOutput (1 and up); the track's own channel is output 1/2.
    public ObservableCollection<PatternTrackViewModel> KitPieces { get; } = new();

    // The track a kit piece belongs to; null for a track.
    public PatternTrackViewModel? KitParent { get; set; }

    public int KitOutput { get; set; }

    public bool IsKitPiece => KitParent is not null;

    // A new kit piece of the track for one of its plug-in's outputs, named after the output: "Out 3/4" for
    // "EZdrummer 3 3/4". Its strip takes the track's colour.
    public static PatternTrackViewModel CreateKitPiece(PatternTrackViewModel track, int output, string outputName)
    {
        var name = outputName;
        if (track.PluginName is { Length: > 0 } plugin)
        {
            name = name.Replace(plugin, string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        name = name.Trim(' ', '-', '_', '.');
        return new PatternTrackViewModel(
            name.Length == 0 ? $"Out {output + 1}" : $"Out {name}",
            track.Instrument,
            track.Channel,
            track.NoteNumber,
            new ObservableCollection<StepViewModel>(),
            trackType: track.TrackType,
            trackColorBrush: track.TrackColorBrush)
        {
            KitParent = track,
            KitOutput = output,
        };
    }

    private bool _isHeard;

    // True once a kit piece's output has played sound: only those show in the mixer, as most outputs of a drum
    // plug-in are often unused.
    public bool IsHeard
    {
        get => _isHeard;
        set
        {
            if (SetProperty(ref _isHeard, value))
            {
                KitParent?.RaisePropertyChanged(nameof(HasKitPieces));
            }
        }
    }

    // True when the track has kit pieces that have played, which the mixer can open beside it.
    public bool HasKitPieces => KitPieces.Any(piece => piece.IsHeard);

    private bool _isKitOpen;

    // True while the mixer shows the kit's pieces beside the track.
    public bool IsKitOpen
    {
        get => _isKitOpen;
        set => SetProperty(ref _isKitOpen, value);
    }

    // The blocks the notes are kept in on the timeline (see NoteBlocks.Of, which fills in any the notes need).
    public IReadOnlyList<NoteBlock> NoteBlocks { get; set; } = Array.Empty<NoteBlock>();

    public IReadOnlyList<MidiNoteEventViewModel> MidiNoteEvents
    {
        get => _midiNoteEvents;
        set => SetProperty(ref _midiNoteEvents, value);
    }

    public bool IsMuted
    {
        get => _isMuted;
        set => SetProperty(ref _isMuted, value);
    }

    public bool IsSolo
    {
        get => _isSolo;
        set => SetProperty(ref _isSolo, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public double Volume
    {
        get => _volume;
        set
        {
            if (SetProperty(ref _volume, Math.Clamp(value, 0.0, 1.0)))
            {
                RaisePropertyChanged(nameof(MixVolume));
                RaisePropertyChanged(nameof(VolumeDisplay));
                RaisePropertyChanged(nameof(ShownAutomationIdleValue));
            }
        }
    }

    private double _pan;

    // Where the track sits between the speakers: -1 hard left, 0 the centre, +1 hard right.
    public double Pan
    {
        get => _pan;
        set
        {
            if (SetProperty(ref _pan, Math.Clamp(value, -1.0, 1.0)))
            {
                RaisePropertyChanged(nameof(PanDisplay));
                RaisePropertyChanged(nameof(MixPan));
                RaisePropertyChanged(nameof(PanPosition));
                RaisePropertyChanged(nameof(ShownAutomationIdleValue));
            }
        }
    }

    // The pan as a mixer shows it: C, or L/R and how far, such as L40.
    public string PanDisplay => Math.Round(MixPan * 100) switch
    {
        0 => "C",
        < 0 and var left => $"L{-left:0}",
        var right => $"R{right:0}",
    };

    // ---- Buses and sends ----

    // Who this is when it is a bus, for the tracks' sends to name it. Kept through undo and saving.
    public Guid BusId { get; init; } = Guid.NewGuid();

    private IReadOnlyDictionary<Guid, double> _sends = new Dictionary<Guid, double>();

    // How much of the track goes to each bus, after its fader and pan, by the bus's id. A level is a fader
    // position, as Volume: 0.75 sends the sound as it is, 0 nothing. Buses with no send are left out.
    public IReadOnlyDictionary<Guid, double> Sends
    {
        get => _sends;
        set => SetProperty(ref _sends, value);
    }

    public double SendLevel(Guid bus) => _sends.TryGetValue(bus, out var level) ? level : 0;

    public void SetSend(Guid bus, double level)
    {
        level = Math.Clamp(level, 0.0, 1.0);
        if (SendLevel(bus) == level)
        {
            return;
        }

        var sends = _sends.ToDictionary(entry => entry.Key, entry => entry.Value);
        if (level <= 0)
        {
            sends.Remove(bus);
        }
        else
        {
            sends[bus] = level;
        }
        Sends = sends;
    }

    // ---- Automation ----

    private IReadOnlyList<AutomationPoint> _volumeAutomation = Array.Empty<AutomationPoint>();
    private IReadOnlyList<AutomationPoint> _panAutomation = Array.Empty<AutomationPoint>();
    private AutomationTarget _shownAutomation;
    private double? _automatedVolume;
    private double? _automatedPan;

    // The volume and pan drawn over the song, in order of step. Empty when the fader alone sets them.
    public IReadOnlyList<AutomationPoint> VolumeAutomation
    {
        get => _volumeAutomation;
        set
        {
            if (SetProperty(ref _volumeAutomation, value))
            {
                RaiseAutomationChanged();
            }
        }
    }

    public IReadOnlyList<AutomationPoint> PanAutomation
    {
        get => _panAutomation;
        set
        {
            if (SetProperty(ref _panAutomation, value))
            {
                RaiseAutomationChanged();
            }
        }
    }

    public bool HasAutomation => _volumeAutomation.Count > 0 || _panAutomation.Count > 0;

    // The automation lane drawn over the track's row in the timeline, or None.
    public AutomationTarget ShownAutomation
    {
        get => _shownAutomation;
        set
        {
            if (SetProperty(ref _shownAutomation, value))
            {
                RaisePropertyChanged(nameof(ShownAutomationPoints));
                RaisePropertyChanged(nameof(ShownAutomationMinimum));
                RaisePropertyChanged(nameof(ShownAutomationIdleValue));
            }
        }
    }

    // The points of the lane shown, read and written by it.
    public IReadOnlyList<AutomationPoint> ShownAutomationPoints
    {
        get => _shownAutomation switch
        {
            AutomationTarget.Volume => _volumeAutomation,
            AutomationTarget.Pan => _panAutomation,
            _ => Array.Empty<AutomationPoint>(),
        };
        set
        {
            if (_shownAutomation == AutomationTarget.Volume)
            {
                VolumeAutomation = value;
            }
            else if (_shownAutomation == AutomationTarget.Pan)
            {
                PanAutomation = value;
            }
        }
    }

    public double ShownAutomationMinimum => Automation.Minimum(_shownAutomation);

    // The fader's value, which the lane shows as a dashed line while it has no points.
    public double ShownAutomationIdleValue => _shownAutomation == AutomationTarget.Pan ? _pan : _volume;

    private void RaiseAutomationChanged()
    {
        RaisePropertyChanged(nameof(ShownAutomationPoints));
        RaisePropertyChanged(nameof(HasAutomation));
    }

    // The automation's value where the song is, set as it plays; null where the track has none.
    public double? AutomatedVolume
    {
        get => _automatedVolume;
        set
        {
            if (SetProperty(ref _automatedVolume, value))
            {
                RaisePropertyChanged(nameof(MixVolume));
                RaisePropertyChanged(nameof(VolumeDisplay));
            }
        }
    }

    public double? AutomatedPan
    {
        get => _automatedPan;
        set
        {
            if (SetProperty(ref _automatedPan, value))
            {
                RaisePropertyChanged(nameof(MixPan));
                RaisePropertyChanged(nameof(PanPosition));
                RaisePropertyChanged(nameof(PanDisplay));
            }
        }
    }

    // What the track sounds at: the automation's value where there is one, else the fader's. The faders
    // show these, so they move with the automation; moved by hand, they set the fader's own value.
    public double MixVolume
    {
        get => _automatedVolume ?? _volume;
        set => Volume = value;
    }

    // The volume as a mixer shows it, in dB from the fader's unity position: "0.0 dB", "-6.0 dB", "-inf".
    public string VolumeDisplay => MixVolume <= 0.0001
        ? "-inf"
        : string.Format(System.Globalization.CultureInfo.CurrentCulture, "{0:+0.0;-0.0;0.0} dB", 20 * Math.Log10(MixVolume / TrackEffectsHost.UnityVolume));

    public double MixPan
    {
        get => _automatedPan ?? _pan;
        set => Pan = value;
    }

    // The pan as a knob's position: 0 hard left, 0.5 the centre, 1 hard right.
    public double PanPosition
    {
        get => (MixPan + 1) / 2;
        set => Pan = value * 2 - 1;
    }

    public bool IsRecordArmed
    {
        get => _isRecordArmed;
        set => SetProperty(ref _isRecordArmed, value);
    }

    public double SignalLevel
    {
        get => _signalLevel;
        set => SetProperty(ref _signalLevel, Math.Clamp(value, 0.0, 1.0));
    }

    public SolidColorBrush TrackColorBrush
    {
        get => _trackColorBrush;
        set => SetProperty(ref _trackColorBrush, value);
    }
}
