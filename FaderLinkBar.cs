using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Sonator;

/// <summary>
/// The aluminium bar laid across the master's two fader caps while they are linked, as engineers once did on
/// real desks to ride the final mix with one finger: it rests on both caps (slanting if they stand at
/// different heights) and dragging it moves both. Left and Right are the faders' values, 0 to 1; dragging sets
/// Left, and the link moves the right fader with it. It lies over two fader columns of equal width, each with
/// a cap ThumbLength tall.
/// </summary>
public sealed class FaderLinkBar : FrameworkElement
{
    public static readonly DependencyProperty LeftProperty = DependencyProperty.Register(
        nameof(Left), typeof(double), typeof(FaderLinkBar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty RightProperty = DependencyProperty.Register(
        nameof(Right), typeof(double), typeof(FaderLinkBar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double ThumbLength { get; set; } = 26;

    private static readonly Pen Shadow = FrozenPen(new SolidColorBrush(Color.FromArgb(0x60, 0, 0, 0)), 7);
    private static readonly Pen Edge = FrozenPen(new SolidColorBrush(Color.FromRgb(0x4A, 0x4C, 0x4F)), 7);
    private static readonly Pen Metal = FrozenPen(new LinearGradientBrush(
        new GradientStopCollection
        {
            new(Color.FromRgb(0xF4, 0xF5, 0xF6), 0),
            new(Color.FromRgb(0xC6, 0xC8, 0xCB), 0.45),
            new(Color.FromRgb(0x9A, 0x9D, 0xA1), 0.55),
            new(Color.FromRgb(0xD2, 0xD4, 0xD7), 1),
        }, 90), 5.4);
    private static readonly Pen Brushing = FrozenPen(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), 0.6);

    private double _dragStartY;
    private double _leftAtDragStart;

    public FaderLinkBar()
    {
        Cursor = Cursors.SizeNS;
    }

    public double Left
    {
        get => (double)GetValue(LeftProperty);
        set => SetValue(LeftProperty, value);
    }

    public double Right
    {
        get => (double)GetValue(RightProperty);
        set => SetValue(RightProperty, value);
    }

    // The fader's travel in pixels, and the middle of its cap for a value, as the slider's track places it.
    private double Travel => Math.Max(1, ActualHeight - ThumbLength);

    private double CapMiddle(double value) => (1 - Math.Clamp(value, 0, 1)) * Travel + ThumbLength / 2;

    private (Point From, Point To) Ends()
    {
        var column = ActualWidth / 2;
        return (new Point(column * 0.18, CapMiddle(Left)), new Point(ActualWidth - column * 0.18, CapMiddle(Right)));
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (ActualWidth < 10 || ActualHeight <= ThumbLength)
        {
            return;
        }

        var (from, to) = Ends();
        var down = new Vector(0.8, 2);
        drawingContext.DrawLine(Shadow, from + down, to + down);
        drawingContext.DrawLine(Edge, from, to);
        drawingContext.DrawLine(Metal, from, to);
        drawingContext.DrawLine(Brushing, from + new Vector(0, -1.2), to + new Vector(0, -1.2));
    }

    // Only the bar takes the mouse, not the space around it.
    protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters)
    {
        var (from, to) = Ends();
        var point = hitTestParameters.HitPoint;
        var along = to - from;
        var t = Math.Clamp(Vector.Multiply(point - from, along) / Math.Max(along.LengthSquared, 1), 0, 1);
        var nearest = from + along * t;
        return (point - nearest).Length <= 6 ? new PointHitTestResult(this, point) : null;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        _dragStartY = e.GetPosition(this).Y;
        _leftAtDragStart = Left;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (IsMouseCaptured)
        {
            Left = Math.Clamp(_leftAtDragStart - (e.GetPosition(this).Y - _dragStartY) / Travel, 0, 1);
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    private static Pen FrozenPen(Brush brush, double thickness)
    {
        brush.Freeze();
        var pen = new Pen(brush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        return pen;
    }
}
