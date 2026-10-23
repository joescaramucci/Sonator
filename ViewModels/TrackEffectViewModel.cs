using System.Collections.ObjectModel;
using System.Globalization;
using Sonator.Services.Effects;

namespace Sonator.ViewModels;

// One knob of an effect on a track, in the units the user sees.
public sealed class EffectParameterViewModel : ViewModelBase
{
    private double _value;

    public EffectParameterViewModel(EffectParameterDefinition definition, int index, double value)
    {
        Definition = definition;
        Index = index;
        _value = Clamp(value);
    }

    public EffectParameterDefinition Definition { get; }

    public int Index { get; }

    public string Name => Definition.Name;

    public double Value
    {
        get => _value;
        set
        {
            if (SetProperty(ref _value, Clamp(value)))
            {
                RaisePropertyChanged(nameof(DisplayValue));
                RaisePropertyChanged(nameof(Position));
                RaisePropertyChanged(nameof(IsOn));
            }
        }
    }

    // Where a double-click puts the knob back.
    public double DefaultPosition => PositionOf(Definition.Default);

    // The knob's travel from 0 to 1. Logarithmic knobs (times, frequencies) spread their range evenly by ratio.
    public double Position
    {
        get => PositionOf(_value);
        set
        {
            var position = Math.Clamp(value, 0, 1);
            Value = Definition.IsLogarithmic && Definition.Minimum > 0
                ? Definition.Minimum * Math.Pow(Definition.Maximum / Definition.Minimum, position)
                : Definition.Minimum + position * (Definition.Maximum - Definition.Minimum);
        }
    }

    // For switches, such as the delay's ping-pong.
    public bool IsOn
    {
        get => _value >= 0.5;
        set => Value = value ? 1 : 0;
    }

    public string DisplayValue => Definition.IsSwitch
        ? (IsOn ? "ON" : "OFF")
        : Math.Round(_value, Definition.Decimals).ToString("F" + Definition.Decimals, CultureInfo.CurrentCulture)
          + (Definition.Unit.StartsWith(':') || Definition.Unit.Length == 0 ? Definition.Unit : " " + Definition.Unit);

    public void ResetToDefault() => Value = Definition.Default;

    private double Clamp(double value) => Math.Clamp(value, Definition.Minimum, Definition.Maximum);

    private double PositionOf(double value) => Definition.IsLogarithmic && Definition.Minimum > 0
        ? Math.Log(value / Definition.Minimum) / Math.Log(Definition.Maximum / Definition.Minimum)
        : (value - Definition.Minimum) / (Definition.Maximum - Definition.Minimum);
}

// One of Sonator's effects in a track's chain.
public sealed class TrackEffectViewModel : ViewModelBase
{
    private bool _isEnabled;

    public TrackEffectViewModel(
        EffectDefinition definition,
        bool isEnabled = true,
        IReadOnlyList<double>? values = null,
        string? presetName = null,
        bool isPresetModified = false)
    {
        Definition = definition;
        _isEnabled = isEnabled;
        _presetName = presetName;
        _isPresetModified = isPresetModified;
        Parameters = new ReadOnlyCollection<EffectParameterViewModel>(definition.Parameters
            .Select((parameter, index) => new EffectParameterViewModel(
                parameter,
                index,
                values is not null && index < values.Count ? values[index] : parameter.Default))
            .ToList());
        foreach (var parameter in Parameters)
        {
            parameter.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(EffectParameterViewModel.Value))
                {
                    if (!_isApplyingPreset && _presetName is not null)
                    {
                        IsPresetModified = true;
                    }

                    Changed?.Invoke(this, parameter.Index);
                }
            };
        }
    }

    public EffectDefinition Definition { get; }

    public string Name => Definition.Name;

    // ---- A VST3 effect plug-in ----

    // An effect that runs a VST3 plug-in: its file, and its state (the processor's and the editor's parts)
    // as last taken, which it gets back when it is loaded again. Such an effect has no knobs of Sonator's.
    public static TrackEffectViewModel ForPlugin(string pluginPath, string pluginName, bool isEnabled = true,
        byte[]? state = null, byte[]? editorState = null, Guid? instanceId = null) =>
        new(EffectCatalog.ForPlugin(pluginName), isEnabled)
        {
            PluginPath = pluginPath,
            PluginState = state,
            PluginEditorState = editorState,
            InstanceId = instanceId ?? Guid.NewGuid(),
        };

    // Which plug-in instance this is, kept through undo and saving: the effect that takes this one's place
    // after an undo goes on running the same loaded plug-in, with every setting made in its window.
    public Guid InstanceId { get; private init; } = Guid.NewGuid();

    public string? PluginPath { get; private init; }

    public byte[]? PluginState { get; set; }

    public byte[]? PluginEditorState { get; set; }

    public bool IsPlugin => PluginPath is not null;

    // The same plug-in effect, as another instance starting from this one's state.
    public TrackEffectViewModel AsNewInstance() =>
        ForPlugin(PluginPath!, Name, IsEnabled, PluginState, PluginEditorState);

    private bool _isPluginMissing;

    // True when the plug-in could not be loaded, as on a computer without it: the sound passes through.
    public bool IsPluginMissing
    {
        get => _isPluginMissing;
        set => SetProperty(ref _isPluginMissing, value);
    }

    // Sonator's own effects have presets and knobs; a plug-in has its own window instead.
    public bool HasKnobs => !IsPlugin;

    public IReadOnlyList<EffectParameterViewModel> Parameters { get; }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (SetProperty(ref _isEnabled, value))
            {
                Changed?.Invoke(this, -1);
            }
        }
    }

    // Raised with the index of the knob that moved, or -1 when the effect is switched on or off.
    public event Action<TrackEffectViewModel, int>? Changed;

    // ---- Presets ----

    private IReadOnlyList<EffectPreset> _presets = Array.Empty<EffectPreset>();
    private EffectPreset? _selectedPreset;
    private string? _presetName;
    private bool _isPresetModified;
    private bool _isApplyingPreset;

    // The presets offered for this effect: the factory ones, then the user's. Filled by the rack window.
    public IReadOnlyList<EffectPreset> Presets
    {
        get => _presets;
        set
        {
            SetProperty(ref _presets, value);
            // Show the preset in use, if it is still in the list, without applying it again.
            _selectedPreset = value.FirstOrDefault(preset => string.Equals(preset.Name, _presetName, StringComparison.CurrentCultureIgnoreCase));
            RaisePropertyChanged(nameof(SelectedPreset));
        }
    }

    // Choosing a preset sets every knob to it.
    public EffectPreset? SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (value is null || ReferenceEquals(value, _selectedPreset) && !_isPresetModified)
            {
                return;
            }

            _selectedPreset = value;
            ApplyPreset(value);
            RaisePropertyChanged();
        }
    }

    // The name of the preset the knobs were last set from, or null when none was chosen. Saved with the project.
    public string? PresetName
    {
        get => _presetName;
        private set => SetProperty(ref _presetName, value);
    }

    // True once a knob has moved after the preset was chosen.
    public bool IsPresetModified
    {
        get => _isPresetModified;
        private set => SetProperty(ref _isPresetModified, value);
    }

    public IReadOnlyList<double> Values => Parameters.Select(parameter => parameter.Value).ToList();

    public void ApplyPreset(EffectPreset preset)
    {
        _isApplyingPreset = true;
        try
        {
            for (var i = 0; i < Parameters.Count && i < preset.Values.Count; i++)
            {
                Parameters[i].Value = preset.Values[i];
            }
        }
        finally
        {
            _isApplyingPreset = false;
        }

        PresetName = preset.Name;
        IsPresetModified = false;
    }

    // After the current knobs were saved as a preset, the effect is on that preset, unmodified.
    public void MarkSavedAs(EffectPreset preset)
    {
        PresetName = preset.Name;
        IsPresetModified = false;
        _selectedPreset = preset;
        RaisePropertyChanged(nameof(SelectedPreset));
    }

    // The compressor and the limiter show how far they turn the sound down.
    public bool HasMeter => Definition.Kind is EffectKind.Compress or EffectKind.Limiter;

    private double _meterValue;

    // Gain reduction in dB, refreshed by the rack window while it is open.
    public double MeterValue
    {
        get => _meterValue;
        set
        {
            if (Math.Abs(_meterValue - value) >= 0.05 && SetProperty(ref _meterValue, value))
            {
                RaisePropertyChanged(nameof(MeterText));
            }
        }
    }

    // The effects whose sound level changes by design show the level going in and coming out.
    public bool HasLevelMeters => Definition.Kind is EffectKind.Compress or EffectKind.Limiter or EffectKind.EQ;

    // The meters' scale: -48 dBFS at the bottom, 0 dBFS at the top.
    private const double MeterFloorDb = -48;

    // How far a meter may fall in one refresh, so it falls back smoothly after a peak.
    private const double MeterFall = 0.04;

    private double _inputLevel;
    private double _outputLevel;

    // The IN and OUT meters, from 0 to 1 on the dB scale above.
    public double InputLevel
    {
        get => _inputLevel;
        private set => SetProperty(ref _inputLevel, value);
    }

    public double OutputLevel
    {
        get => _outputLevel;
        private set => SetProperty(ref _outputLevel, value);
    }

    // Takes the peaks measured since the last refresh: a meter jumps up at once and falls back gently.
    public void ShowLevels(double inputPeak, double outputPeak)
    {
        InputLevel = Math.Max(ToMeter(inputPeak), _inputLevel - MeterFall);
        OutputLevel = Math.Max(ToMeter(outputPeak), _outputLevel - MeterFall);
    }

    internal static double ToMeter(double peak) =>
        peak <= 0 ? 0 : Math.Clamp((20 * Math.Log10(peak) - MeterFloorDb) / -MeterFloorDb, 0, 1);

    public string MeterText => _meterValue < 0.05
        ? string.Format(CultureInfo.CurrentCulture, "{0:0.0} dB", 0.0)
        : string.Format(CultureInfo.CurrentCulture, "-{0:0.0} dB", _meterValue);
}

// What changed in a track's effects: the whole chain when Effect is null, otherwise one effect's
// knob (ParameterIndex) or its on/off switch (ParameterIndex -1).
public sealed record TrackEffectChange(TrackEffectViewModel? Effect, int ParameterIndex);
