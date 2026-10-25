using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Sonator;

/// <summary>
/// The fades of an audio clip on the timeline: the fade in from its left edge and the fade out to its right one,
/// each shaded over the waveform down to its curve, with a handle on top to drag it longer or shorter. Only the
/// handles take the mouse; the rest of the clip drags the clip as before.
/// </summary>
public sealed class ClipFadeOverlay : FrameworkElement
{
    public static readonly DependencyProperty FadeInProperty = DependencyProperty.Register(
        nameof(FadeIn), typeof(double), typeof(ClipFadeOverlay),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty FadeOutProperty = DependencyProperty.Register(
        nameof(FadeOut), typeof(double), typeof(ClipFadeOverlay),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty ClipSecondsProperty = DependencyProperty.Register(
        nameof(ClipSeconds), typeof(double), typeof(ClipFadeOverlay),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private const double HandleSize = 8;
    private const double HitRadius = 8;

    private static readonly Brush ShadeBrush = Frozen(Color.FromArgb(0x88, 0x0C, 0x12, 0x19));
    private static readonly Brush HandleBrush = Frozen(Color.FromRgb(0xE8, 0xF4, 0xF0));
    private static readonly Pen CurvePen = FrozenPen(Color.FromRgb(0xE8, 0xF4, 0xF0), 1.2);
    private static readonly Pen HandleOutline = FrozenPen(Color.FromRgb(0x24, 0x46, 0x3F), 1);

    private enum Handle
    {
        None,
        In,
        Out,
    }

    private Handle _dragging;
    private bool _editStarted;

    public ClipFadeOverlay()
    {
        ToolTip = "Drag the handle at the top corner to fade the clip in or out";
    }

    // Raised before a fade is changed, so the change can be undone.
    public event EventHandler? EditStarting;

    // The fade in, in seconds of the clip.
    public double FadeIn
    {
        get => (double)GetValue(FadeInProperty);
        set => SetValue(FadeInProperty, value);
    }

    public double FadeOut
    {
        get => (double)GetValue(FadeOutProperty);
        set => SetValue(FadeOutProperty, value);
    }

    // How long the clip is, to place the fades along its width.
    public double ClipSeconds
    {
        get => (double)GetValue(ClipSecondsProperty);
        set => SetValue(ClipSecondsProperty, value);
    }

    private double PixelsPerSecond => ClipSeconds > 0 ? ActualWidth / ClipSeconds : 0;

    private double InX => Math.Clamp(FadeIn * PixelsPerSecond, 0, ActualWidth);

    private double OutX => Math.Clamp(ActualWidth - FadeOut * PixelsPerSecond, 0, ActualWidth);

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0 || ClipSeconds <= 0)
        {
            return;
        }

        if (InX > 0.5)
        {
            DrawFade(drawingContext, 0, InX, rising: true);
        }

        if (OutX < ActualWidth - 0.5)
        {
            DrawFade(drawingContext, OutX, ActualWidth, rising: false);
        }

        DrawHandle(drawingContext, Math.Max(HandleSize / 2, InX));
        DrawHandle(drawingContext, Math.Min(ActualWidth - HandleSize / 2, OutX));
    }

    // The part of the clip above the fade's curve is shaded: equal power, as the bridge plays it.
    private void DrawFade(DrawingContext drawingContext, double from, double to, bool rising)
    {
        var width = to - from;
        var height = ActualHeight;
        var curve = new StreamGeometry();
        var shade = new StreamGeometry();
        using (var line = curve.Open())
        using (var area = shade.Open())
        {
            area.BeginFigure(new Point(from, 0), true, true);
            var steps = Math.Max(2, (int)(width / 2));
            for (var i = 0; i <= steps; i++)
            {
                var t = (double)i / steps;
                var level = Math.Sin(Math.PI / 2 * (rising ? t : 1 - t));
                var point = new Point(from + t * width, height - level * height);
                if (i == 0)
                {
                    line.BeginFigure(point, false, false);
                }
                else
                {
                    line.LineTo(point, true, false);
                }

                area.LineTo(point, false, false);
            }

            area.LineTo(new Point(to, 0), false, false);
        }

        curve.Freeze();
        shade.Freeze();
        drawingContext.DrawGeometry(ShadeBrush, null, shade);
        drawingContext.DrawGeometry(null, CurvePen, curve);
    }

    private static void DrawHandle(DrawingContext drawingContext, double x) =>
        drawingContext.DrawRectangle(HandleBrush, HandleOutline, new Rect(x - HandleSize / 2, 0, HandleSize, HandleSize));

    private Handle HandleAt(Point position)
    {
        if (position.Y > HandleSize + HitRadius / 2)
        {
            return Handle.None;
        }

        var inDistance = Math.Abs(position.X - Math.Max(HandleSize / 2, InX));
        var outDistance = Math.Abs(position.X - Math.Min(ActualWidth - HandleSize / 2, OutX));
        if (Math.Min(inDistance, outDistance) > HitRadius)
        {
            return Handle.None;
        }

        return inDistance <= outDistance ? Handle.In : Handle.Out;
    }

    // Only the handles are part of the overlay for the mouse.
    protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters) =>
        _dragging != Handle.None || HandleAt(hitTestParameters.HitPoint) != Handle.None
            ? new PointHitTestResult(this, hitTestParameters.HitPoint)
            : null;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Cursor = _dragging != Handle.None || HandleAt(e.GetPosition(this)) != Handle.None ? Cursors.SizeWE : null;
        if (_dragging == Handle.None || PixelsPerSecond <= 0)
        {
            return;
        }

        if (!_editStarted)
        {
            _editStarted = true;
            EditStarting?.Invoke(this, EventArgs.Empty);
        }

        var x = Math.Clamp(e.GetPosition(this).X, 0, ActualWidth);
        if (_dragging == Handle.In)
        {
            FadeIn = Math.Round(Math.Clamp(x / PixelsPerSecond, 0, Math.Max(0, ClipSeconds - FadeOut)), 3);
        }
        else
        {
            FadeOut = Math.Round(Math.Clamp((ActualWidth - x) / PixelsPerSecond, 0, Math.Max(0, ClipSeconds - FadeIn)), 3);
        }

        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        _dragging = HandleAt(e.GetPosition(this));
        if (_dragging != Handle.None)
        {
            _editStarted = false;
            CaptureMouse();
            e.Handled = true;
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_dragging != Handle.None)
        {
            _dragging = Handle.None;
            ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Color color, double thickness)
    {
        var pen = new Pen(new SolidColorBrush(color), thickness);
        pen.Freeze();
        return pen;
    }
}
