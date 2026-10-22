using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Sonator;

/// <summary>
/// A rotary knob over 270 degrees. Drag up or down to turn it (Shift for fine steps), use the mouse
/// wheel to nudge it, and double-click to go back to the default. Position runs from 0 to 1.
/// </summary>
public sealed class Knob : FrameworkElement
{
    public static readonly DependencyProperty PositionProperty = DependencyProperty.Register(
        nameof(Position), typeof(double), typeof(Knob),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty DefaultPositionProperty = DependencyProperty.Register(
        nameof(DefaultPosition), typeof(double), typeof(Knob), new PropertyMetadata(0.0));

    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(Brush), typeof(Knob),
        new FrameworkPropertyMetadata(Brushes.DeepSkyBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    private const double StartAngle = 135; // degrees clockwise from the right, at the bottom left
    private const double Sweep = 270;
    private const double DragRange = 200; // pixels of drag for the whole travel
    private const double WheelStep = 0.02;

    private static readonly Brush BodyBrush = Frozen(0x26, 0x35, 0x45);
    private static readonly Pen BodyPen = FrozenPen(0x5A, 0x72, 0x8D, 1);
    private static readonly Pen TrackPen = FrozenPen(0x16, 0x20, 0x2B, 4);
    private static readonly Pen PointerPen = FrozenPen(0xE8, 0xEE, 0xF4, 2);

    private Point _dragStart;
    private double _positionAtDragStart;

    public Knob()
    {
        Cursor = Cursors.SizeNS;
        Focusable = false;
    }

    public double Position
    {
        get => (double)GetValue(PositionProperty);
        set => SetValue(PositionProperty, Math.Clamp(value, 0, 1));
    }

    public double DefaultPosition
    {
        get => (double)GetValue(DefaultPositionProperty);
        set => SetValue(DefaultPositionProperty, value);
    }

    public Brush Accent
    {
        get => (Brush)GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 8)
        {
            return;
        }

        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var ringRadius = size / 2 - 3;
        var bodyRadius = ringRadius - 5;

        drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        drawingContext.DrawGeometry(null, TrackPen, Arc(center, ringRadius, StartAngle, Sweep));
        var position = Math.Clamp(Position, 0, 1);
        if (position > 0.001)
        {
            var valuePen = new Pen(Accent, 4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            drawingContext.DrawGeometry(null, valuePen, Arc(center, ringRadius, StartAngle, Sweep * position));
        }

        drawingContext.DrawEllipse(BodyBrush, BodyPen, center, bodyRadius, bodyRadius);
        var angle = (StartAngle + Sweep * position) * Math.PI / 180;
        var tip = new Point(center.X + Math.Cos(angle) * (bodyRadius - 2), center.Y + Math.Sin(angle) * (bodyRadius - 2));
        var inner = new Point(center.X + Math.Cos(angle) * bodyRadius * 0.35, center.Y + Math.Sin(angle) * bodyRadius * 0.35);
        drawingContext.DrawLine(PointerPen, inner, tip);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (e.ClickCount == 2)
        {
            Position = DefaultPosition;
            return;
        }

        _dragStart = e.GetPosition(this);
        _positionAtDragStart = Position;
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!IsMouseCaptured)
        {
            return;
        }

        var range = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? DragRange * 5 : DragRange;
        Position = _positionAtDragStart + (_dragStart.Y - e.GetPosition(this).Y) / range;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? WheelStep / 5 : WheelStep;
        Position += Math.Sign(e.Delta) * step;
        e.Handled = true;
    }

    private static Geometry Arc(Point center, double radius, double fromDegrees, double sweepDegrees)
    {
        var from = fromDegrees * Math.PI / 180;
        var to = (fromDegrees + sweepDegrees) * Math.PI / 180;
        var figure = new PathFigure { StartPoint = new Point(center.X + Math.Cos(from) * radius, center.Y + Math.Sin(from) * radius) };
        figure.Segments.Add(new ArcSegment(
            new Point(center.X + Math.Cos(to) * radius, center.Y + Math.Sin(to) * radius),
            new Size(radius, radius),
            0,
            sweepDegrees > 180,
            SweepDirection.Clockwise,
            true));
        return new PathGeometry(new[] { figure });
    }

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(byte r, byte g, byte b, double thickness)
    {
        var pen = new Pen(Frozen(r, g, b), thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        return pen;
    }
}
