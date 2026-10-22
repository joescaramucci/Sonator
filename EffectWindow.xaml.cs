using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using Sonator.Services.Effects;
using Sonator.ViewModels;

namespace Sonator;

// The knobs of one effect on one track. Changes are heard at once; the compressor and the limiter
// also show how much they are turning the sound down.
public partial class EffectWindow : Window, INotifyPropertyChanged
{
    private readonly MainViewModel _viewModel;
    private readonly PatternTrackViewModel _track;
    private readonly DispatcherTimer _meterTimer = new() { Interval = TimeSpan.FromMilliseconds(60) };
    private double _meterValue;

    public EffectWindow(MainViewModel viewModel, PatternTrackViewModel track, TrackEffectViewModel effect)
    {
        _viewModel = viewModel;
        _track = track;
        TrackEffect = effect;
        InitializeComponent();
        WindowFrame.UseDarkFrame(this);
        DataContext = this;

        if (HasMeter)
        {
            _meterTimer.Tick += (_, _) => MeterValue = _viewModel.EffectMeter(_track, TrackEffect);
            _meterTimer.Start();
        }

        Closed += (_, _) => _meterTimer.Stop();
    }

    public TrackEffectViewModel TrackEffect { get; }

    public string WindowTitle => $"{TrackEffect.Name} — {_track.Name}";

    public string Subtitle => $"{TrackEffect.Definition.Description} on {_track.Name}";

    private bool HasMeter => TrackEffect.Definition.Kind is EffectKind.Compress or EffectKind.Limiter;

    public Visibility MeterVisibility => HasMeter ? Visibility.Visible : Visibility.Collapsed;

    public double MeterValue
    {
        get => _meterValue;
        private set
        {
            if (Math.Abs(_meterValue - value) < 0.05)
            {
                return;
            }

            _meterValue = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(MeterText));
        }
    }

    public string MeterText => _meterValue < 0.05
        ? string.Format(CultureInfo.CurrentCulture, "{0:0.0} dB", 0.0)
        : string.Format(CultureInfo.CurrentCulture, "-{0:0.0} dB", _meterValue);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        foreach (var parameter in TrackEffect.Parameters)
        {
            parameter.ResetToDefault();
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
