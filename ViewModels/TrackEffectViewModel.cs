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

    public TrackEffectViewModel(EffectDefinition definition, bool isEnabled = true, IReadOnlyList<double>? values = null)
    {
        Definition = definition;
        _isEnabled = isEnabled;
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
                    Changed?.Invoke(this, parameter.Index);
                }
            };
        }
    }

    public EffectDefinition Definition { get; }

    public string Name => Definition.Name;

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
}

// What changed in a track's effects: the whole chain when Effect is null, otherwise one effect's
// knob (ParameterIndex) or its on/off switch (ParameterIndex -1).
public sealed record TrackEffectChange(TrackEffectViewModel? Effect, int ParameterIndex);
