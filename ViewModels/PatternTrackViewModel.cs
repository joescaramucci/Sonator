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
        };
    }

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

    private void OnEffectChanged(TrackEffectViewModel effect, int parameterIndex) =>
        EffectsChanged?.Invoke(this, new TrackEffectChange(effect, parameterIndex));

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

    // The VST3 plug-in this track plays through. Null when the track has no plug-in assigned.
    public string? PluginFilePath
    {
        get => _pluginFilePath;
        set
        {
            if (SetProperty(ref _pluginFilePath, string.IsNullOrWhiteSpace(value) ? null : value))
            {
                RaisePropertyChanged(nameof(PluginName));
                RaisePropertyChanged(nameof(HasPlugin));
            }
        }
    }

    public bool HasPlugin => PluginFilePath is not null;

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
        set => SetProperty(ref _volume, Math.Clamp(value, 0.0, 1.0));
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
