using System.Windows;
using System.Windows.Media;

namespace Sonator;

/// <summary>
/// Shows digits like an LED clock: seven segments per digit, lit in Foreground, with the unlit
/// segments faintly visible in GhostBrush. Understands 0-9, ':' and '.', and a space as a short gap.
/// </summary>
public sealed class SevenSegmentDisplay : FrameworkElement
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(SevenSegmentDisplay),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DigitHeightProperty = DependencyProperty.Register(
        nameof(DigitHeight), typeof(double), typeof(SevenSegmentDisplay),
        new FrameworkPropertyMetadata(28.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
        nameof(Foreground), typeof(Brush), typeof(SevenSegmentDisplay),
        new FrameworkPropertyMetadata(Brushes.LightGreen, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GhostBrushProperty = DependencyProperty.Register(
        nameof(GhostBrush), typeof(Brush), typeof(SevenSegmentDisplay),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    // Segments a to g, as bits: a top, b top right, c bottom right, d bottom, e bottom left, f top left, g middle.
    private static readonly Dictionary<char, int> DigitSegments = new()
    {
        ['0'] = 0b0111111,
        ['1'] = 0b0000110,
        ['2'] = 0b1011011,
        ['3'] = 0b1001111,
        ['4'] = 0b1100110,
        ['5'] = 0b1101101,
        ['6'] = 0b1111101,
        ['7'] = 0b0000111,
        ['8'] = 0b1111111,
        ['9'] = 0b1101111,
        ['-'] = 0b1000000,
    };

    // Leans the digits like a real LED display.
    private const double Slant = 0.08;

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public double DigitHeight
    {
        get => (double)GetValue(DigitHeightProperty);
        set => SetValue(DigitHeightProperty, value);
    }

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public Brush? GhostBrush
    {
        get => (Brush?)GetValue(GhostBrushProperty);
        set => SetValue(GhostBrushProperty, value);
    }

    private double DigitWidth => DigitHeight * 0.55;

    private double Thickness => DigitHeight * 0.11;

    private double Spacing => Thickness * 0.9;

    private double WidthOf(char c) => c switch
    {
        ':' or '.' => Thickness * 1.6,
        ' ' => Thickness * 2.2,
        _ => DigitWidth,
    };

    protected override Size MeasureOverride(Size availableSize)
    {
        var text = Text ?? string.Empty;
        var width = text.Sum(WidthOf) + Math.Max(0, text.Length - 1) * Spacing + DigitHeight * Slant;
        return new Size(width, DigitHeight);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var text = Text ?? string.Empty;
        var x = DigitHeight * Slant;
        drawingContext.PushTransform(new MatrixTransform(1, 0, -Slant, 1, 0, 0));
        foreach (var c in text)
        {
            if (DigitSegments.TryGetValue(c, out var lit))
            {
                DrawDigit(drawingContext, x, lit);
            }
            else if (c is ':' or '.')
            {
                DrawDots(drawingContext, x, c == ':');
            }

            x += WidthOf(c) + Spacing;
        }

        drawingContext.Pop();
    }

    private void DrawDigit(DrawingContext dc, double left, int lit)
    {
        var w = DigitWidth;
        var h = DigitHeight;
        var t = Thickness;
        var gap = t * 0.18;
        var half = t / 2;
        var segments = new[]
        {
            Horizontal(left + half + gap, left + w - half - gap, half, t),            // a
            Vertical(left + w - half, half + gap, h / 2 - gap, t),                    // b
            Vertical(left + w - half, h / 2 + gap, h - half - gap, t),                // c
            Horizontal(left + half + gap, left + w - half - gap, h - half, t),        // d
            Vertical(left + half, h / 2 + gap, h - half - gap, t),                    // e
            Vertical(left + half, half + gap, h / 2 - gap, t),                        // f
            Horizontal(left + half + gap, left + w - half - gap, h / 2, t),           // g
        };

        for (var i = 0; i < segments.Length; i++)
        {
            var brush = (lit & (1 << i)) != 0 ? Foreground : GhostBrush;
            if (brush is not null)
            {
                dc.DrawGeometry(brush, null, segments[i]);
            }
        }
    }

    private void DrawDots(DrawingContext dc, double left, bool colon)
    {
        var size = Thickness;
        var x = left + (WidthOf(colon ? ':' : '.') - size) / 2;
        if (colon)
        {
            dc.DrawRectangle(Foreground, null, new Rect(x, DigitHeight * 0.3 - size / 2, size, size));
            dc.DrawRectangle(Foreground, null, new Rect(x, DigitHeight * 0.7 - size / 2, size, size));
        }
        else
        {
            dc.DrawRectangle(Foreground, null, new Rect(x, DigitHeight - size, size, size));
        }
    }

    // A segment with pointed ends, lying along y from x1 to x2.
    private static Geometry Horizontal(double x1, double x2, double y, double t) => Polygon(
        new Point(x1, y), new Point(x1 + t / 2, y - t / 2), new Point(x2 - t / 2, y - t / 2),
        new Point(x2, y), new Point(x2 - t / 2, y + t / 2), new Point(x1 + t / 2, y + t / 2));

    // A segment with pointed ends, standing along x from y1 to y2.
    private static Geometry Vertical(double x, double y1, double y2, double t) => Polygon(
        new Point(x, y1), new Point(x + t / 2, y1 + t / 2), new Point(x + t / 2, y2 - t / 2),
        new Point(x, y2), new Point(x - t / 2, y2 - t / 2), new Point(x - t / 2, y1 + t / 2));

    private static Geometry Polygon(params Point[] points)
    {
        var figure = new PathFigure { StartPoint = points[0], IsClosed = true, IsFilled = true };
        figure.Segments.Add(new PolyLineSegment(points.Skip(1), true));
        var geometry = new PathGeometry(new[] { figure });
        geometry.Freeze();
        return geometry;
    }
}
