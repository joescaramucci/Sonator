using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Sonator;

/// <summary>
/// A vertical LED level meter: green segments with red ones at the top, the unlit segments faintly
/// visible. The highest level reached stays lit for a moment as a peak, then falls.
/// </summary>
public sealed class LedMeter : FrameworkElement
{
    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level), typeof(double), typeof(LedMeter),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((LedMeter)d).OnLevelChanged()));

    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.Register(
        nameof(Segments), typeof(int), typeof(LedMeter),
        new FrameworkPropertyMetadata(12, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush Green = Frozen(0x4C, 0xD9, 0x6E);
    private static readonly Brush GreenOff = Frozen(0x16, 0x2E, 0x20);
    private static readonly Brush Red = Frozen(0xFF, 0x4D, 0x4D);
    private static readonly Brush RedOff = Frozen(0x3A, 0x18, 0x1B);

    // How long the peak stays lit, and how fast it then falls, as a share of the scale per tick.
    private static readonly TimeSpan PeakHold = TimeSpan.FromSeconds(1);
    private const double PeakFall = 0.06;

    private readonly DispatcherTimer _peakTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private double _peak;
    private DateTime _peakAt;

    public LedMeter()
    {
        _peakTimer.Tick += (_, _) => FallPeak();
        Unloaded += (_, _) => _peakTimer.Stop();
    }

    // The level to show, from 0 to 1.
    public double Level
    {
        get => (double)GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    public int Segments
    {
        get => (int)GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public static readonly DependencyProperty RedSegmentsProperty = DependencyProperty.Register(
        nameof(RedSegments), typeof(int), typeof(LedMeter),
        new FrameworkPropertyMetadata(2, FrameworkPropertyMetadataOptions.AffectsRender));

    // How many segments at the top are red.
    public int RedSegments
    {
        get => (int)GetValue(RedSegmentsProperty);
        set => SetValue(RedSegmentsProperty, value);
    }

    private bool IsRed(int segment) => segment >= Segments - RedSegments;

    protected override void OnRender(DrawingContext drawingContext)
    {
        var count = Math.Max(1, Segments);
        const double gap = 1.0;
        var height = (ActualHeight - gap * (count - 1)) / count;
        if (height <= 0 || ActualWidth <= 0)
        {
            return;
        }

        var lit = (int)Math.Round(Math.Clamp(Level, 0, 1) * count);
        var peak = _peak > 0.001 ? Math.Min(count - 1, (int)Math.Ceiling(_peak * count) - 1) : -1;
        for (var segment = 0; segment < count; segment++)
        {
            var on = segment < lit || segment == peak;
            var brush = IsRed(segment) ? (on ? Red : RedOff) : (on ? Green : GreenOff);
            var top = ActualHeight - (segment + 1) * height - segment * gap;
            drawingContext.DrawRectangle(brush, null, new Rect(0, top, ActualWidth, height));
        }
    }

    private void OnLevelChanged()
    {
        var level = Math.Clamp(Level, 0, 1);
        if (level >= _peak)
        {
            _peak = level;
            _peakAt = DateTime.UtcNow;
        }

        if (_peak > level && !_peakTimer.IsEnabled)
        {
            _peakTimer.Start();
        }
    }

    private void FallPeak()
    {
        var level = Math.Clamp(Level, 0, 1);
        if (DateTime.UtcNow - _peakAt >= PeakHold)
        {
            _peak = Math.Max(level, _peak - PeakFall);
            InvalidateVisual();
        }

        if (_peak <= level)
        {
            _peakTimer.Stop();
        }
    }

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
