using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Sonator;

/// <summary>
/// The dB scale printed beside a console fader: a tick and its number at each mark, placed where the fader
/// puts that level. A fader position p (0 to 1) gives p / 0.75 of the sound, so 0 dB is at three quarters.
/// ThumbLength is the fader cap's height: the cap's middle travels between half of it from each end.
/// </summary>
public sealed class FaderScale : FrameworkElement
{
    private static readonly (double Db, string Label)[] Marks =
    {
        (2.5, "+2"), (0, "0"), (-3, "3"), (-6, "6"), (-10, "10"), (-20, "20"), (double.NegativeInfinity, "∞"),
    };

    private static readonly Brush InkBrush = Frozen(0x2A, 0x2C, 0x2E); // printed dark on the light aluminium
    private static readonly Pen TickPen = FrozenPen(0x2A, 0x2C, 0x2E, 1);
    private static readonly Pen UnityPen = FrozenPen(0x10, 0x11, 0x12, 1.6);

    public double ThumbLength { get; set; } = 24;

    protected override void OnRender(DrawingContext drawingContext)
    {
        var travel = ActualHeight - ThumbLength;
        if (travel <= 0)
        {
            return;
        }

        var typeface = new Typeface(new FontFamily("Arial Narrow, Arial"), FontStyles.Normal, FontWeights.Bold, FontStretches.Condensed);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        foreach (var (db, label) in Marks)
        {
            var position = double.IsNegativeInfinity(db) ? 0 : Math.Min(1, 0.75 * Math.Pow(10, db / 20));
            var y = ThumbLength / 2 + (1 - position) * travel;
            drawingContext.DrawLine(db == 0 ? UnityPen : TickPen, new Point(ActualWidth - 5, y), new Point(ActualWidth, y));
            var text = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 8, InkBrush, dpi);
            drawingContext.DrawText(text, new Point(ActualWidth - 6 - text.Width, y - text.Height / 2));
        }
    }

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(byte r, byte g, byte b, double thickness)
    {
        var pen = new Pen(Frozen(r, g, b), thickness);
        pen.Freeze();
        return pen;
    }
}
