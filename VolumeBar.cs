using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Sonator;

/// <summary>
/// A horizontal volume bar with no knob: the filled part is the value. Click or drag along it to set
/// the value, use the mouse wheel to nudge it, and double-click to go back to the default.
/// </summary>
public sealed class VolumeBar : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(VolumeBar),
        new FrameworkPropertyMetadata(0.75, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, _) => ((VolumeBar)d).UpdateToolTip()));

    public static readonly DependencyProperty DefaultValueProperty = DependencyProperty.Register(
        nameof(DefaultValue), typeof(double), typeof(VolumeBar), new PropertyMetadata(0.75));

    private static readonly Brush TrackBrush = Frozen(0x12, 0x1A, 0x24);
    private static readonly Brush FillBrush = Frozen(0x5C, 0xAF, 0xD6);
    private static readonly Brush HoverFillBrush = Frozen(0x86, 0xC8, 0xE8);
    private static readonly Pen OutlinePen = FrozenPen(0x3A, 0x4D, 0x63);
    private static readonly Pen EdgePen = FrozenPen(0xE4, 0xEC, 0xF4);

    // Each notch of the mouse wheel moves the volume by this much.
    private const double WheelStep = 0.02;

    public VolumeBar()
    {
        Cursor = Cursors.SizeWE;
        UpdateToolTip();
    }

    // From 0 to 1.
    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, Math.Clamp(value, 0, 1));
    }

    public double DefaultValue
    {
        get => (double)GetValue(DefaultValueProperty);
        set => SetValue(DefaultValueProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        var radius = Math.Min(3, bounds.Height / 2);
        drawingContext.DrawRoundedRectangle(TrackBrush, OutlinePen, bounds, radius, radius);

        var fillWidth = Math.Clamp(Value, 0, 1) * bounds.Width;
        if (fillWidth > 0.5)
        {
            drawingContext.DrawRoundedRectangle(IsMouseOver ? HoverFillBrush : FillBrush, null, new Rect(0, 0, fillWidth, bounds.Height), radius, radius);
            drawingContext.DrawLine(EdgePen, new Point(fillWidth, 1), new Point(fillWidth, bounds.Height - 1));
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (e.ClickCount == 2)
        {
            Value = DefaultValue;
            return;
        }

        CaptureMouse();
        SetFromPoint(e.GetPosition(this));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (IsMouseCaptured)
        {
            SetFromPoint(e.GetPosition(this));
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

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        Value += Math.Sign(e.Delta) * WheelStep;
        e.Handled = true;
    }

    protected override void OnMouseEnter(MouseEventArgs e) => InvalidateVisual();

    protected override void OnMouseLeave(MouseEventArgs e) => InvalidateVisual();

    private void SetFromPoint(Point point)
    {
        if (ActualWidth > 0)
        {
            Value = point.X / ActualWidth;
        }
    }

    private void UpdateToolTip()
    {
        ToolTip = $"Volume {Math.Round(Value * 100):0}%. Drag or use the mouse wheel; double-click for {Math.Round(DefaultValue * 100):0}%.";
    }

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(byte r, byte g, byte b)
    {
        var pen = new Pen(Frozen(r, g, b), 1);
        pen.Freeze();
        return pen;
    }
}
