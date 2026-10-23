using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Sonator;

/// <summary>
/// A moving-needle VU meter, as on the master section of a studio desk: a backlit amber face with the VU
/// scale from -20 to +3 (red above 0), and a needle that takes about 300 ms to get where the sound is and
/// falls back as slowly. 0 VU is at -18 dBFS, the usual calibration. Peak is the sound's highest sample
/// since the last update (0 to 1).
/// </summary>
public sealed class VuMeter : FrameworkElement
{
    public static readonly DependencyProperty PeakProperty = DependencyProperty.Register(
        nameof(Peak), typeof(double), typeof(VuMeter), new PropertyMetadata(0.0));

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(VuMeter),
        new FrameworkPropertyMetadata("VU", FrameworkPropertyMetadataOptions.AffectsRender));

    private const double ReferenceDbfs = -18;
    private const double MinVu = -20;
    private const double MaxVu = 3;
    private const double SweepDegrees = 90; // from the needle's rest at the left to full scale at the right
    private const double IntegrationSeconds = 0.3;

    private static readonly (double Vu, string? Label)[] Marks =
    {
        (-20, "20"), (-10, "10"), (-7, "7"), (-5, "5"), (-4, null), (-3, "3"), (-2, null), (-1, null), (0, "0"), (1, null), (2, null), (3, "3"),
    };

    private static readonly Brush Face = FrozenGradient(Color.FromRgb(0xFB, 0xEC, 0xBE), Color.FromRgb(0xE2, 0xC2, 0x78));
    private static readonly Brush Ink = Frozen(Color.FromRgb(0x1A, 0x16, 0x10));
    private static readonly Brush RedInk = Frozen(Color.FromRgb(0xC0, 0x22, 0x1A));
    private static readonly Pen InkPen = FrozenPen(Ink, 0.9);
    private static readonly Pen RedArcPen = FrozenPen(RedInk, 2.4);
    private static readonly Pen ArcPen = FrozenPen(Ink, 0.9);
    private static readonly Pen NeedlePen = FrozenPen(Frozen(Color.FromRgb(0x14, 0x12, 0x10)), 1.2);
    private static readonly Pen BezelPen = FrozenPen(Frozen(Color.FromRgb(0x05, 0x06, 0x07)), 2);
    private static readonly Brush Glass = FrozenGradient(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF));

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(25) };
    private DateTime _lastTick = DateTime.UtcNow;
    private double _needleVu = MinVu;

    public VuMeter()
    {
        _timer.Tick += (_, _) => Move();
        Loaded += (_, _) => _timer.Start();
        Unloaded += (_, _) => _timer.Stop();
    }

    public double Peak
    {
        get => (double)GetValue(PeakProperty);
        set => SetValue(PeakProperty, value);
    }

    // Printed on the face, under the scale: "L", "R" or "VU".
    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    // Where the sound puts the needle, in VU, from its peak.
    internal static double VuOf(double peak) =>
        peak <= 0 ? MinVu : Math.Clamp(20 * Math.Log10(peak) - ReferenceDbfs, MinVu - 2, MaxVu + 1);

    // The needle moves toward the sound's level by the share of the integration time that has passed.
    private void Move()
    {
        var now = DateTime.UtcNow;
        var elapsed = Math.Min(0.2, (now - _lastTick).TotalSeconds);
        _lastTick = now;
        var target = VuOf(Peak);
        var moved = _needleVu + (target - _needleVu) * (1 - Math.Exp(-elapsed / IntegrationSeconds * 2.2));
        if (Math.Abs(moved - _needleVu) > 0.005)
        {
            _needleVu = moved;
            InvalidateVisual();
        }
    }

    // A VU scale is spaced by voltage, so the top of the scale is wide and the bottom crowded.
    private static double Fraction(double vu)
    {
        var low = Math.Pow(10, MinVu / 20);
        var high = Math.Pow(10, MaxVu / 20);
        return (Math.Pow(10, vu / 20) - low) / (high - low);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 20 || height < 16)
        {
            return;
        }

        var bounds = new Rect(1, 1, width - 2, height - 2);
        drawingContext.DrawRoundedRectangle(Face, BezelPen, bounds, 3, 3);

        // The needle turns about a pivot under the face, so the scale is a shallow arc.
        var pivot = new Point(width / 2, height * 1.06);
        var radius = Math.Min(height * 0.8, width * 0.5);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface(new FontFamily("Arial"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

        Point At(double vu, double r)
        {
            var angle = (-90 - SweepDegrees / 2 + SweepDegrees * Fraction(vu)) * Math.PI / 180;
            return new Point(pivot.X + Math.Cos(angle) * r, pivot.Y + Math.Sin(angle) * r);
        }

        drawingContext.PushClip(new RectangleGeometry(bounds, 3, 3));
        drawingContext.DrawGeometry(null, ArcPen, Arc(At(MinVu, radius), At(0, radius), radius));
        drawingContext.DrawGeometry(null, RedArcPen, Arc(At(0, radius + 1), At(MaxVu, radius + 1), radius + 1));
        foreach (var (vu, label) in Marks)
        {
            var pen = vu > 0 ? FrozenPen(RedInk, 0.9) : InkPen;
            drawingContext.DrawLine(pen, At(vu, radius), At(vu, radius + (label is null ? 3 : 5)));
            if (label is not null)
            {
                var text = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface,
                    Math.Max(5, height * 0.12), vu > 0 ? RedInk : Ink, dpi);
                var at = At(vu, radius + 4 + text.Height * 0.5);
                drawingContext.DrawText(text, new Point(at.X - text.Width / 2, at.Y - text.Height / 2));
            }
        }

        var caption = new FormattedText(Label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, Math.Max(6, height * 0.16), Ink, dpi);
        drawingContext.DrawText(caption, new Point(width / 2 - caption.Width / 2, height * 0.6));

        drawingContext.DrawLine(NeedlePen, At(_needleVu, radius * 0.45), At(_needleVu, radius + 6));
        drawingContext.DrawRectangle(Glass, null, new Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height * 0.45));
        drawingContext.Pop();
    }

    private static Geometry Arc(Point from, Point to, double radius)
    {
        var figure = new PathFigure { StartPoint = from };
        figure.Segments.Add(new ArcSegment(to, new Size(radius, radius), 0, false, SweepDirection.Clockwise, true));
        var geometry = new PathGeometry(new[] { figure });
        geometry.Freeze();
        return geometry;
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Brush FrozenGradient(Color top, Color bottom)
    {
        var brush = new LinearGradientBrush(top, bottom, 90);
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness);
        pen.Freeze();
        return pen;
    }
}
