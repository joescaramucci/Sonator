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
    private static readonly Brush KnobShadow = FrozenSolid(Color.FromArgb(0x90, 0, 0, 0));
    private static readonly Brush ChromeSkirt = FrozenLinear(
        (Color.FromRgb(0xF2, 0xF4, 0xF6), 0), (Color.FromRgb(0x9C, 0xA4, 0xAD), 0.45), (Color.FromRgb(0x4E, 0x55, 0x5D), 0.75), (Color.FromRgb(0x8A, 0x92, 0x9B), 1));

    private static Brush FrozenSolid(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Brush FrozenLinear(params (Color Color, double Offset)[] stops)
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0.3, 0), EndPoint = new Point(0.7, 1) };
        foreach (var (color, offset) in stops)
        {
            brush.GradientStops.Add(new GradientStop(color, offset));
        }
        brush.Freeze();
        return brush;
    }

    // The colour of the scale's printed dots: dark on a light panel, as SSL's; light on a dark one.
    public static readonly DependencyProperty ScaleBrushProperty = DependencyProperty.Register(
        nameof(ScaleBrush), typeof(Brush), typeof(Knob),
        new FrameworkPropertyMetadata(Frozen(0x3E, 0x42, 0x46), FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush ScaleBrush
    {
        get => (Brush)GetValue(ScaleBrushProperty);
        set => SetValue(ScaleBrushProperty, value);
    }

    // A knob as on SSL's panels, seen from above and a little in front: a light grey fluted barrel standing on
    // the panel, lit from the left and shaded to the right and below, with a smaller matte coloured cap (the
    // Accent) on its metal top, and a pointer across the cap that runs on down the side when it points to the
    // front; under it all a soft shadow, over a printed scale of dots.
    private void DrawConsoleKnob(DrawingContext drawingContext, Point center, double radius)
    {
        var dotRadius = radius - 1.5;
        for (var i = 0; i <= 10; i++)
        {
            var angle = (StartAngle + Sweep * i / 10) * Math.PI / 180;
            var dot = new Point(center.X + Math.Cos(angle) * dotRadius, center.Y + Math.Sin(angle) * dotRadius);
            var big = i == 0 || i == 10 || (IsCentred && i == 5);
            drawingContext.DrawEllipse(ScaleBrush, null, dot, radius * (big ? 0.06 : 0.045), radius * (big ? 0.06 : 0.045));
        }

        // A small barrel standing on the panel, as SSL's knobs: its fluted side swells out in the middle and narrows
        // to its top and to its foot; a smaller coloured cap sits on its top. Sizes as a share of the dots' ring,
        // the whole centred in the ring's open arc.
        var topCentre = new Point(center.X, center.Y - radius * 0.38);
        var topRadius = radius * 0.42;
        var baseCentre = new Point(center.X, center.Y + radius * 0.24);
        var baseRadiusX = radius * 0.42;
        var baseRadiusY = radius * 0.2;
        var middleCentre = new Point(center.X, (topCentre.Y + baseCentre.Y) / 2 + radius * 0.04);
        var middleRadiusX = radius * 0.5;
        var middleRadiusY = (topRadius + baseRadiusY) / 2;

        // A line down the side at an angle, from the top's rim to the foot's, through the swell in the middle.
        Point Top(double angle) => OnRim(topCentre, topRadius, topRadius, angle);
        Point Foot(double angle) => OnRim(baseCentre, baseRadiusX, baseRadiusY, angle);
        Point Swell(double angle)
        {
            var top = Top(angle);
            var foot = Foot(angle);
            var middle = OnRim(middleCentre, middleRadiusX, middleRadiusY, angle);
            return new Point(2 * middle.X - (top.X + foot.X) / 2, 2 * middle.Y - (top.Y + foot.Y) / 2);
        }

        // The barrel's outline: the top circle and the foot's ellipse joined by its swelling sides.
        var sides = new StreamGeometry();
        using (var context = sides.Open())
        {
            context.BeginFigure(Top(Math.PI), true, true);
            context.LineTo(Top(0), false, false);
            context.QuadraticBezierTo(Swell(0), Foot(0), false, false);
            context.LineTo(Foot(Math.PI), false, false);
            context.QuadraticBezierTo(Swell(Math.PI), Top(Math.PI), false, false);
        }
        var top = new EllipseGeometry(topCentre, topRadius, topRadius);
        var bottom = new EllipseGeometry(baseCentre, baseRadiusX, baseRadiusY);
        var body = Geometry.Combine(Geometry.Combine(top, sides, GeometryCombineMode.Union, null), bottom, GeometryCombineMode.Union, null);
        body.Freeze();

        // Its shadow on the panel, to the right and below, where the light from the top left does not reach.
        drawingContext.DrawEllipse(ConeShadow, null, new Point(baseCentre.X + radius * 0.1, baseCentre.Y + radius * 0.14), middleRadiusX * 1.05, baseRadiusY * 1.7);

        drawingContext.DrawGeometry(ConeSide, null, body);
        drawingContext.DrawGeometry(ConeShade, null, body);

        // The fluting: a few grooves cut down the side all round, each a narrow dark channel with its far wall lit
        // from the left; those turned away from the eye crowd together towards the edges.
        drawingContext.PushClip(body);
        const int Flutes = 16;
        var halfWidth = Math.PI / Flutes * 0.3;
        for (var k = 0; k < Flutes; k++)
        {
            var centre = (k + 0.5) * 2 * Math.PI / Flutes;
            if (Math.Sin(centre) < 0.02)
            {
                continue; // at the back, out of sight
            }

            var left = centre + halfWidth; // angles run clockwise from the right, so a larger one is further left
            var right = centre - halfWidth;
            var groove = new StreamGeometry();
            using (var context = groove.Open())
            {
                context.BeginFigure(Top(left), true, true);
                context.LineTo(Top(right), false, false);
                context.QuadraticBezierTo(Swell(right), Foot(right), false, false);
                context.LineTo(Foot(left), false, false);
                context.QuadraticBezierTo(Swell(left), Top(left), false, false);
            }
            groove.Freeze();
            drawingContext.DrawGeometry(GrooveFill, null, groove);
            drawingContext.DrawGeometry(null, GrooveLightPen, DownTheSide(Top(right), Swell(right), Foot(right)));
        }
        drawingContext.Pop();
        drawingContext.DrawGeometry(null, ConeEdgePen, body);

        // The barrel's metal top, and on it the smaller coloured cap, with its side darker beneath it.
        var capColour = Accent is SolidColorBrush solid ? solid.Color : Colors.LightGray;
        drawingContext.DrawEllipse(CapLip, ConeEdgePen, topCentre, topRadius, topRadius);
        var capRadius = topRadius * 0.78;
        var capSide = new SolidColorBrush(Darken(capColour, 0.4));
        capSide.Freeze();
        drawingContext.DrawEllipse(capSide, null, new Point(topCentre.X, topCentre.Y + radius * 0.04), capRadius, capRadius);
        var cap = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.38, 0.32),
            Center = new Point(0.5, 0.5),
            RadiusX = 0.62,
            RadiusY = 0.62,
        };
        cap.GradientStops.Add(new GradientStop(Lighten(capColour, 0.12), 0));
        cap.GradientStops.Add(new GradientStop(capColour, 0.7));
        cap.GradientStops.Add(new GradientStop(Darken(capColour, 0.18), 1));
        cap.Freeze();
        var capCentre = new Point(topCentre.X, topCentre.Y - radius * 0.01);
        drawingContext.DrawEllipse(cap, null, capCentre, capRadius, capRadius);

        // The pointer: dark on a light cap, white on a coloured or dark one; on across the metal top and down the
        // side as well when it points to the front, where the side is in view.
        var angleNow = (StartAngle + Sweep * Math.Clamp(Position, 0, 1)) * Math.PI / 180;
        var cos = Math.Cos(angleNow);
        var sin = Math.Sin(angleNow);
        var luminance = (0.299 * capColour.R + 0.587 * capColour.G + 0.114 * capColour.B) / 255;
        var pointerBrush = luminance > 0.62 ? DarkPointer : LightPointer;
        var pointer = new Pen(pointerBrush, Math.Max(1.6, radius * 0.09)) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat };
        pointer.Freeze();
        drawingContext.DrawLine(pointer, new Point(capCentre.X + cos * capRadius * 0.3, capCentre.Y + sin * capRadius * 0.3),
            new Point(capCentre.X + cos * capRadius, capCentre.Y + sin * capRadius));
        var sidePointer = new Pen(DarkPointer, Math.Max(1.0, radius * 0.05));
        sidePointer.Freeze();
        drawingContext.DrawLine(sidePointer, new Point(topCentre.X + cos * capRadius * 1.04, topCentre.Y + sin * capRadius * 1.04), Top(angleNow));
        if (sin > 0.05)
        {
            drawingContext.PushClip(body);
            drawingContext.DrawGeometry(null, sidePointer, DownTheSide(Top(angleNow), Swell(angleNow), Foot(angleNow)));
            drawingContext.Pop();
        }
    }

    // A line down a knob's side, from its top through its swell to its foot.
    private static Geometry DownTheSide(Point top, Point swell, Point foot)
    {
        var line = new StreamGeometry();
        using (var context = line.Open())
        {
            context.BeginFigure(top, false, false);
            context.QuadraticBezierTo(swell, foot, true, false);
        }
        line.Freeze();
        return line;
    }

    // A point on the rim of an ellipse seen from the front, at an angle clockwise from its right.
    private static Point OnRim(Point centre, double radiusX, double radiusY, double angle) =>
        new(centre.X + Math.Cos(angle) * radiusX, centre.Y + Math.Sin(angle) * radiusY);

    private static readonly Brush ConeSide = FrozenLinearHorizontal(
        (Color.FromRgb(0xD8, 0xD8, 0xD8), 0), (Color.FromRgb(0xC6, 0xC6, 0xC6), 0.22), (Color.FromRgb(0xA2, 0xA2, 0xA2), 0.5), (Color.FromRgb(0x74, 0x74, 0x74), 0.82), (Color.FromRgb(0x86, 0x86, 0x86), 1));
    private static readonly Brush ConeShade = FrozenLinearVertical(
        (Color.FromArgb(0x00, 0, 0, 0), 0.4), (Color.FromArgb(0x30, 0, 0, 0), 0.75), (Color.FromArgb(0x70, 0, 0, 0), 1));
    private static readonly Brush CapLip = FrozenSolid(Color.FromRgb(0xC8, 0xC8, 0xC8));
    private static readonly Brush GrooveFill = FrozenSolid(Color.FromArgb(0x48, 0x28, 0x28, 0x28));
    private static readonly Pen GrooveLightPen = FrozenColourPen(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF), 0.6);

    private static Pen FrozenColourPen(Color colour, double thickness)
    {
        var pen = new Pen(new SolidColorBrush(colour), thickness);
        pen.Freeze();
        return pen;
    }
    private static readonly Brush ConeMetal = FrozenLinearVertical(
        (Color.FromRgb(0xFA, 0xFB, 0xFB), 0), (Color.FromRgb(0xDA, 0xDD, 0xE0), 0.35), (Color.FromRgb(0xA0, 0xA5, 0xAA), 0.72), (Color.FromRgb(0x6A, 0x6F, 0x74), 1));
    private static readonly Brush ConeSideLight = FrozenLinearHorizontal(
        (Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF), 0), (Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.45), (Color.FromArgb(0x40, 0x00, 0x00, 0x00), 1));
    private static readonly Pen ConeEdgePen = FrozenPen(0x3A, 0x3D, 0x40, 0.8);
    private static readonly Brush ConeShadow = FrozenShadow();
    private static readonly Brush Specular = FrozenSolid(Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF));
    private static readonly Brush DarkPointer = FrozenSolid(Color.FromRgb(0x14, 0x15, 0x17));
    private static readonly Brush LightPointer = FrozenSolid(Color.FromRgb(0xF6, 0xF6, 0xF4));

    private static Brush FrozenShadow()
    {
        var brush = new RadialGradientBrush();
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0xB0, 0, 0, 0), 0.5));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0, 0, 0), 1));
        brush.Freeze();
        return brush;
    }

    private static Brush FrozenLinearVertical(params (Color Color, double Offset)[] stops) =>
        FrozenStops(new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) }, stops);

    private static Brush FrozenLinearHorizontal(params (Color Color, double Offset)[] stops) =>
        FrozenStops(new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) }, stops);

    private static Brush FrozenStops(GradientBrush brush, (Color Color, double Offset)[] stops)
    {
        foreach (var (color, offset) in stops)
        {
            brush.GradientStops.Add(new GradientStop(color, offset));
        }
        brush.Freeze();
        return brush;
    }

    private static Color Darken(Color color, double amount) => Color.FromRgb(
        (byte)(color.R * (1 - amount)),
        (byte)(color.G * (1 - amount)),
        (byte)(color.B * (1 - amount)));
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
