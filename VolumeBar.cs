using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Sonator;

/// <summary>
/// A horizontal volume bar with no knob: the filled part is the value. Click or drag along it to set
/// the value, use the mouse wheel to nudge it, and double-click to go back to the default. While the
/// mouse is over it, a small bubble above the end of the fill shows the value as it changes.
/// </summary>
public sealed class VolumeBar : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(VolumeBar),
        new FrameworkPropertyMetadata(0.75, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, _) => ((VolumeBar)d).UpdateBubble()));

    public static readonly DependencyProperty DefaultValueProperty = DependencyProperty.Register(
        nameof(DefaultValue), typeof(double), typeof(VolumeBar), new PropertyMetadata(0.75));

    private static readonly Brush TrackBrush = Frozen(0x12, 0x1A, 0x24);
    private static readonly Brush FillBrush = Frozen(0x5C, 0xAF, 0xD6);
    private static readonly Brush HoverFillBrush = Frozen(0x86, 0xC8, 0xE8);
    private static readonly Pen OutlinePen = FrozenPen(0x3A, 0x4D, 0x63);
    private static readonly Pen EdgePen = FrozenPen(0xE4, 0xEC, 0xF4);

    // Each notch of the mouse wheel moves the volume by this much.
    private const double WheelStep = 0.02;

    private readonly Popup _bubble;
    private readonly TextBlock _bubbleText;

    public VolumeBar()
    {
        Cursor = Cursors.SizeWE;
        _bubbleText = new TextBlock
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            FontWeight = FontWeights.Bold,
            Foreground = Frozen(0xE8, 0xEE, 0xF4),
        };
        _bubble = new Popup
        {
            AllowsTransparency = true,
            Placement = PlacementMode.Relative,
            PlacementTarget = this,
            IsHitTestVisible = false,
            Focusable = false,
            Child = new Border
            {
                Background = Frozen(0x0C, 0x12, 0x19),
                BorderBrush = Frozen(0x5C, 0xAF, 0xD6),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 1, 6, 1),
                Child = _bubbleText,
            },
        };
        Unloaded += (_, _) => _bubble.IsOpen = false;
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

    // How tall the drawn bar is, centred in the control.
    public double BarThickness { get; set; } = 5;

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        // The whole height takes the mouse, so the thin bar is easy to grab.
        drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        var thickness = Math.Min(BarThickness, ActualHeight);
        var bounds = new Rect(0, (ActualHeight - thickness) / 2, ActualWidth, thickness);

        var radius = Math.Min(3, bounds.Height / 2);
        drawingContext.DrawRoundedRectangle(TrackBrush, OutlinePen, bounds, radius, radius);

        var fillWidth = Math.Clamp(Value, 0, 1) * bounds.Width;
        if (fillWidth > 0.5)
        {
            drawingContext.DrawRoundedRectangle(IsMouseOver || IsMouseCaptured ? HoverFillBrush : FillBrush, null, new Rect(0, bounds.Top, fillWidth, bounds.Height), radius, radius);
            drawingContext.DrawLine(EdgePen, new Point(fillWidth, bounds.Top - 1), new Point(fillWidth, bounds.Bottom + 1));
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

    // A drag that ends outside the bar hides the bubble once the mouse is let go.
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        InvalidateVisual();
        if (!IsMouseOver)
        {
            _bubble.IsOpen = false;
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        Value += Math.Sign(e.Delta) * WheelStep;
        e.Handled = true;
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        InvalidateVisual();
        _bubble.IsOpen = true;
        UpdateBubble();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        InvalidateVisual();
        if (!IsMouseCaptured)
        {
            _bubble.IsOpen = false;
        }
    }

    private void SetFromPoint(Point point)
    {
        if (ActualWidth > 0)
        {
            Value = point.X / ActualWidth;
        }
    }

    // Puts the value in the bubble and centres the bubble over the end of the fill.
    private void UpdateBubble()
    {
        _bubbleText.Text = $"{Math.Round(Value * 100):0}%";
        if (!_bubble.IsOpen)
        {
            return;
        }

        var child = (FrameworkElement)_bubble.Child;
        child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _bubble.HorizontalOffset = Math.Clamp(Value, 0, 1) * ActualWidth - child.DesiredSize.Width / 2;
        _bubble.VerticalOffset = -child.DesiredSize.Height - 3;
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
