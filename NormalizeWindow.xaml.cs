using System.ComponentModel;
using System.Globalization;
using System.Windows;

namespace Sonator;

// Asks for the peak level SonNormalize brings a clip to, from -12 to 0 dB, showing the clip's peak now.
public partial class NormalizeWindow : Window, INotifyPropertyChanged
{
    private const double MinimumDb = -12;
    private const double MaximumDb = 0;
    private const double DefaultDb = -0.3;

    private readonly double _currentPeakDb;
    private double _targetDb = DefaultDb;

    public NormalizeWindow(string trackName, double currentPeakDb)
    {
        _currentPeakDb = currentPeakDb;
        Subtitle = $"Set the peak level of the audio clip on {trackName}";
        InitializeComponent();
        WindowFrame.UseDarkFrame(this);
        DataContext = this;
    }

    public string Subtitle { get; }

    // The chosen peak, in dB. Read it when ShowDialog returns true.
    public double TargetDb
    {
        get => _targetDb;
        private set
        {
            _targetDb = Math.Round(Math.Clamp(value, MinimumDb, MaximumDb), 1);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TargetPosition)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TargetText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GainText)));
        }
    }

    public double TargetPosition
    {
        get => (_targetDb - MinimumDb) / (MaximumDb - MinimumDb);
        set => TargetDb = MinimumDb + value * (MaximumDb - MinimumDb);
    }

    public double DefaultTargetPosition => (DefaultDb - MinimumDb) / (MaximumDb - MinimumDb);

    public string TargetText => string.Format(CultureInfo.CurrentCulture, "{0:0.0} dB", _targetDb);

    public string CurrentPeakText => string.Format(CultureInfo.CurrentCulture, "{0:0.0} dB", _currentPeakDb);

    public string GainText => string.Format(CultureInfo.CurrentCulture, "The whole clip is turned {0} by {1:0.0} dB.",
        _targetDb >= _currentPeakDb ? "up" : "down", Math.Abs(_targetDb - _currentPeakDb));

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Apply_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
