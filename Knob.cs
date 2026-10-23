using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Sonator;

// How a knob is drawn: flat with a coloured value arc, as in the FX racks, or as on a mixing console's
// panel, a dark knurled body with a coloured cap and a white line, over a printed scale of dots.
public enum KnobLook
{
    Flat,
    Console,
}

/// <summary>
/// A rotary knob over 270 degrees. Drag up or down to turn it (Shift for fine steps), use the mouse
/// wheel to nudge it, and double-click to go back to the default. Position runs from 0 to 1.
/// </summary>
public sealed class Knob : FrameworkElement
{
    public static readonly DependencyProperty LookProperty = DependencyProperty.Register(
        nameof(Look), typeof(KnobLook), typeof(Knob),
        new FrameworkPropertyMetadata(KnobLook.Flat, FrameworkPropertyMetadataOptions.AffectsRender));

    public KnobLook Look
    {
        get => (KnobLook)GetValue(LookProperty);
        set => SetValue(LookProperty, value);
    }

    // For the console look: the knob's middle is marked on the scale, as for a pan.
    public bool IsCentred { get; set; }
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
        if (Look == KnobLook.Console)
        {
            DrawConsoleKnob(drawingContext, center, size / 2);
            return;
        }

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

    private static readonly Brush ScaleDotBrush = Frozen(0xC8, 0xCD, 0xD2); // printed light on the dark panel
    private static readonly Pen KnobEdgePen = FrozenPen(0x08, 0x09, 0x0A, 1);
    private static readonly Pen ConsolePointerPen = FrozenPen(0xF4, 0xF4, 0xF0, 1.6);
    private static readonly Brush KnurledBody = FrozenRadial(Color.FromRgb(0x5A, 0x60, 0x66), Color.FromRgb(0x14, 0x16, 0x19));

    // A console knob: the scale's dots printed around it, a dark knurled body, the Accent as its cap, lit
    // from the top left, and a white line showing where it points.
    private void DrawConsoleKnob(DrawingContext drawingContext, Point center, double radius)
    {
        var dotRadius = radius - 1.5;
        for (var i = 0; i <= 10; i++)
        {
            var angle = (StartAngle + Sweep * i / 10) * Math.PI / 180;
            var dot = new Point(center.X + Math.Cos(angle) * dotRadius, center.Y + Math.Sin(angle) * dotRadius);
            var big = i == 0 || i == 10 || (IsCentred && i == 5);
            drawingContext.DrawEllipse(ScaleDotBrush, null, dot, big ? 1.3 : 0.8, big ? 1.3 : 0.8);
        }

        var bodyRadius = radius - 4;
        drawingContext.DrawEllipse(KnurledBody, KnobEdgePen, center, bodyRadius, bodyRadius);
        var capColor = Accent is SolidColorBrush solid ? solid.Color : Colors.SteelBlue;
        var cap = new RadialGradientBrush(Lighten(capColor, 0.45), capColor)
        {
            GradientOrigin = new Point(0.35, 0.3),
            Center = new Point(0.45, 0.4),
            RadiusX = 0.7,
            RadiusY = 0.7,
        };
        cap.Freeze();
        var capRadius = bodyRadius * 0.62;
        drawingContext.DrawEllipse(cap, KnobEdgePen, center, capRadius, capRadius);

        var angleNow = (StartAngle + Sweep * Math.Clamp(Position, 0, 1)) * Math.PI / 180;
        var tip = new Point(center.X + Math.Cos(angleNow) * (bodyRadius - 1), center.Y + Math.Sin(angleNow) * (bodyRadius - 1));
        var inner = new Point(center.X + Math.Cos(angleNow) * capRadius * 0.2, center.Y + Math.Sin(angleNow) * capRadius * 0.2);
        drawingContext.DrawLine(ConsolePointerPen, inner, tip);
    }

    private static Color Lighten(Color color, double amount) => Color.FromRgb(
        (byte)(color.R + (255 - color.R) * amount),
        (byte)(color.G + (255 - color.G) * amount),
        (byte)(color.B + (255 - color.B) * amount));

    private static Brush FrozenRadial(Color inner, Color outer)
    {
        var brush = new RadialGradientBrush(inner, outer) { GradientOrigin = new Point(0.35, 0.3) };
        brush.Freeze();
        return brush;
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
        var position = _positionAtDragStart + (_dragStart.Y - e.GetPosition(this).Y) / range;
        Position = IsCentred && Math.Abs(position - 0.5) < 0.02 ? 0.5 : position; // the centre catches a pan
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
