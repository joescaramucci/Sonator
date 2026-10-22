using System.Globalization;
using System.Windows.Media;
using System.Windows.Threading;
using Sonator.Services;

namespace Sonator.ViewModels;

// The CPU, DSP and RAM meters at the right of the top bar, refreshed once a second.
public sealed class SystemHealthViewModel : ViewModelBase
{
    private static readonly SolidColorBrush Calm = Frozen(0x4F, 0xB8, 0x7B);
    private static readonly SolidColorBrush Busy = Frozen(0xE6, 0xC4, 0x5B);
    private static readonly SolidColorBrush Strained = Frozen(0xD9, 0x4C, 0x4C);

    private readonly SystemHealthMonitor _monitor = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private SystemHealthSample? _sample;

    public SystemHealthViewModel()
    {
        _timer.Tick += (_, _) => Update(_monitor.Sample());
        ResetDropoutsCommand = new RelayCommand(() =>
        {
            SystemHealthMonitor.ResetDropouts();
            if (_sample is not null)
            {
                Update(_sample with { Dropouts = 0 });
            }
        });
    }

    public RelayCommand ResetDropoutsCommand { get; }

    public double CpuLoad => _sample?.SystemCpu ?? 0;

    public string CpuText => Percent(CpuLoad);

    public string CpuToolTip => _sample is null
        ? "CPU"
        : $"Whole PC: {Percent(_sample.SystemCpu)}\nSonator and its plug-ins: {Percent(_sample.SonatorCpu)}";

    public Brush CpuBrush => BrushFor(CpuLoad);

    // The heaviest audio block of the last second, as a share of the time the block lasts.
    public double DspLoad => _sample?.DspPeak ?? 0;

    public string DspText => _sample?.DspPeak is null ? "--" : Percent(DspLoad);

    public string DspToolTip => _sample?.DspPeak is null
        ? "Audio load: no plug-in is playing audio"
        : $"Audio load of the plug-ins: {Percent(_sample.DspPeak.Value)} at the peak, {Percent(_sample.DspAverage ?? 0)} on average.\nAbove 100% the audio cannot be rendered in time and clicks.";

    public Brush DspBrush => BrushFor(DspLoad);

    public long Dropouts => _sample?.Dropouts ?? 0;

    public string DropoutsText => Dropouts == 1 ? "1 drop" : $"{Dropouts} drops";

    public bool HasDropouts => Dropouts > 0;

    public double RamLoad => _sample?.SystemMemoryLoad ?? 0;

    // Memory in use on the whole PC over the memory installed, such as "11,0/16 GB".
    public string RamText => _sample is null
        ? "--"
        : FormatUsedOfTotal(_sample.SystemMemoryUsedBytes, _sample.SystemMemoryTotalBytes);

    public string RamToolTip => _sample is null
        ? "RAM"
        : $"Memory in use on the whole PC: {FormatUsedOfTotal(_sample.SystemMemoryUsedBytes, _sample.SystemMemoryTotalBytes)} ({Percent(_sample.SystemMemoryLoad)})\nSonator and its plug-ins: {FormatBytes(_sample.SonatorMemoryBytes)}";

    public Brush RamBrush => BrushFor(RamLoad);

    public void Start()
    {
        Update(_monitor.Sample());
        _timer.Start();
    }

    public void Stop() => _timer.Stop();

    private void Update(SystemHealthSample sample)
    {
        _sample = sample;
        foreach (var name in new[]
                 {
                     nameof(CpuLoad), nameof(CpuText), nameof(CpuToolTip), nameof(CpuBrush),
                     nameof(DspLoad), nameof(DspText), nameof(DspToolTip), nameof(DspBrush),
                     nameof(Dropouts), nameof(DropoutsText), nameof(HasDropouts),
                     nameof(RamLoad), nameof(RamText), nameof(RamToolTip), nameof(RamBrush),
                 })
        {
            RaisePropertyChanged(name);
        }
    }

    internal static Brush BrushFor(double load) => load >= 0.85 ? Strained : load >= 0.6 ? Busy : Calm;

    internal static string Percent(double share) => $"{Math.Round(share * 100):0}%";

    // The total is rounded to whole gigabytes, as the memory is sold: 15.9 GB usable shows as 16 GB.
    internal static string FormatUsedOfTotal(long usedBytes, long totalBytes) => string.Format(
        CultureInfo.CurrentCulture, "{0:0.0}/{1:0} GB", usedBytes / (double)(1L << 30), Math.Round(totalBytes / (double)(1L << 30)));

    internal static string FormatBytes(long bytes) => bytes >= 1L << 30
        ? string.Format(CultureInfo.CurrentCulture, "{0:0.0} GB", bytes / (double)(1L << 30))
        : string.Format(CultureInfo.CurrentCulture, "{0:0} MB", bytes / (double)(1L << 20));

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
