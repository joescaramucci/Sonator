using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Sonator.Services;

namespace Sonator;

// Where the analyser gets the spectrum from: each band's level in dBFS, or false when no sound is going out.
public interface ISpectrumSource
{
    bool ReadSpectrum(double[] levelsDb);
}

/// <summary>
/// A LED spectrum analyser, as on the master section of a desk: a column of LEDs for each band from 31.5 Hz to
/// 16 kHz, green, then yellow from -12 dB and red from -3 dB, with a lit dot holding each band's peak a moment
/// before it falls. It reads its Source about thirty times a second while it is on screen; the columns jump up
/// at once and fall back smoothly.
/// </summary>
public sealed class SpectrumAnalyzer : FrameworkElement
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(ISpectrumSource), typeof(SpectrumAnalyzer), new PropertyMetadata(null));

    private const double RangeDb = 60; // the bottom LED lights at -60 dBFS
    private const double FallDbPerFrame = 1.6;
    private const double PeakFallDbPerFrame = 0.8;
    private const int PeakHoldFrames = 30;

    private static readonly Brush Panel = Solid(0x15, 0x16, 0x18);
    private static readonly Pen PanelEdge = FrozenPen(Color.FromRgb(0x59, 0x59, 0x59), 1);
    private static readonly Brush Green = Solid(0x3C, 0xE0, 0x6A);
    private static readonly Brush Yellow = Solid(0xF2, 0xD0, 0x3C);
    private static readonly Brush Red = Solid(0xFF, 0x3A, 0x2A);
    private static readonly Brush GreenOff = Solid(0x14, 0x2A, 0x1A);
    private static readonly Brush YellowOff = Solid(0x2E, 0x28, 0x12);
    private static readonly Brush RedOff = Solid(0x34, 0x14, 0x12);
    private static readonly Brush LabelInk = Solid(0x7A, 0x80, 0x86);
    private static readonly Typeface LabelFace = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private readonly DispatcherTimer _timer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly double[] _read = new double[SpectrumAnalysis.Bands];
    private readonly double[] _shown = Enumerable.Repeat(-RangeDb, SpectrumAnalysis.Bands).ToArray();
    private readonly double[] _peaks = Enumerable.Repeat(-RangeDb, SpectrumAnalysis.Bands).ToArray();
    private readonly int[] _peakHolds = new int[SpectrumAnalysis.Bands];

    public SpectrumAnalyzer()
    {
        _timer.Tick += (_, _) => Update();
        Loaded += (_, _) => _timer.Start();
        Unloaded += (_, _) => _timer.Stop();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible && IsLoaded)
            {
                _timer.Start();
            }
            else
            {
                _timer.Stop();
            }
        };
    }

    public ISpectrumSource? Source
    {
        get => (ISpectrumSource?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    // It takes the height it is given, up to its MaxHeight.
    protected override Size MeasureOverride(Size availableSize) =>
        new(0, double.IsInfinity(availableSize.Height) ? 80 : availableSize.Height);

    private void Update()
    {
        var sounding = Source?.ReadSpectrum(_read) == true;
        var changed = false;
        for (var band = 0; band < _shown.Length; band++)
        {
            var level = sounding ? Math.Max(_read[band], -RangeDb) : -RangeDb;
            var shown = Math.Max(level, _shown[band] - FallDbPerFrame);
            if (level >= _peaks[band])
            {
                _peaks[band] = level;
                _peakHolds[band] = PeakHoldFrames;
            }
            else if (_peakHolds[band] > 0)
            {
                _peakHolds[band]--;
            }
            else
            {
                _peaks[band] = Math.Max(-RangeDb, _peaks[band] - PeakFallDbPerFrame);
            }

            changed |= Math.Abs(shown - _shown[band]) > 0.01;
            _shown[band] = shown;
        }

        if (changed || sounding)
        {
            InvalidateVisual();
        }
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        if (bounds.Width < 30 || bounds.Height < 30)
        {
            return;
        }

        drawingContext.DrawRoundedRectangle(Panel, PanelEdge, new Rect(0.5, 0.5, bounds.Width - 1, bounds.Height - 1), 2, 2);

        // Room under the LEDs for a few frequencies, when there is room at all.
        var labels = bounds.Height >= 60;
        var area = new Rect(4, 4, bounds.Width - 8, bounds.Height - 8 - (labels ? 9 : 0));
        var bands = _shown.Length;
        var columnWidth = area.Width / bands;
        var ledWidth = Math.Max(1, columnWidth - Math.Max(1, columnWidth * 0.25));
        var segments = Math.Clamp((int)(area.Height / 3.5), 8, 24);
        var rowHeight = area.Height / segments;
        var ledHeight = Math.Max(1, rowHeight - Math.Max(1, rowHeight * 0.3));

        for (var band = 0; band < bands; band++)
        {
            var x = area.X + band * columnWidth + (columnWidth - ledWidth) / 2;
            var lit = Math.Round((_shown[band] + RangeDb) / RangeDb * segments);
            var peak = (int)Math.Round((_peaks[band] + RangeDb) / RangeDb * segments);
            for (var segment = 0; segment < segments; segment++)
            {
                var y = area.Bottom - (segment + 1) * rowHeight + (rowHeight - ledHeight) / 2;
                var db = -RangeDb + (segment + 1) * RangeDb / segments;
                var on = segment < lit || (peak > 0 && segment == peak - 1);
                var brush = db > -3 ? (on ? Red : RedOff) : db > -12 ? (on ? Yellow : YellowOff) : (on ? Green : GreenOff);
                drawingContext.DrawRectangle(brush, null, new Rect(x, y, ledWidth, ledHeight));
            }
        }

        if (labels)
        {
            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            foreach (var (hz, text) in new[] { (63.0, "63"), (250.0, "250"), (1000.0, "1k"), (4000.0, "4k"), (16000.0, "16k") })
            {
                var band = Math.Log(hz / SpectrumAnalysis.LowestHz) / Math.Log(SpectrumAnalysis.HighestHz / SpectrumAnalysis.LowestHz) * (bands - 1);
                var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, LabelFace, 7, LabelInk, dpi);
                var centre = area.X + (band + 0.5) * columnWidth;
                drawingContext.DrawText(formatted, new Point(Math.Clamp(centre - formatted.Width / 2, 1, bounds.Width - formatted.Width - 1), area.Bottom + 1));
            }
        }
    }

    private static Brush Solid(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Color colour, double thickness)
    {
        var pen = new Pen(Solid(colour.R, colour.G, colour.B), thickness);
        pen.Freeze();
        return pen;
    }
}
