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
/// With IsPan it sets a pan instead: from -1 (left) to +1 (right), filled from the centre.
/// </summary>
public sealed class VolumeBar : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(VolumeBar),
        new FrameworkPropertyMetadata(0.75, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, _) => ((VolumeBar)d).UpdateBubble()));

    public static readonly DependencyProperty DefaultValueProperty = DependencyProperty.Register(
        nameof(DefaultValue), typeof(double), typeof(VolumeBar), new PropertyMetadata(0.75));

    // A channel sunk into the panel (dark at its top, where the light does not reach) with a lit lower edge,
    // and a glossy fill lit from above.
    private static readonly Brush TrackBrush = Vertical(Color.FromRgb(0x03, 0x05, 0x08), Color.FromRgb(0x1A, 0x24, 0x30));
    private static readonly Brush FillBrush = Vertical(Color.FromRgb(0x9C, 0xD9, 0xF5), Color.FromRgb(0x2F, 0x7F, 0xAD));
    private static readonly Brush HoverFillBrush = Vertical(Color.FromRgb(0xC2, 0xE8, 0xFA), Color.FromRgb(0x4C, 0x9F, 0xCC));
    private static readonly Pen OutlinePen = FrozenPen(0x05, 0x08, 0x0B);
    private static readonly Pen LitEdgePen = FrozenPen(0x5F, 0x73, 0x8A);
    private static readonly Pen CentrePen = FrozenPen(0xB8, 0xBA, 0xBC);
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

    // From 0 to 1, or from -1 to 1 for a pan.
    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, Math.Clamp(value, IsPan ? -1 : 0, 1));
    }

    public bool IsPan { get; set; }

    // Where the value sits along the bar, from 0 at the left end to 1 at the right.
    private double Position => Math.Clamp(IsPan ? (Value + 1) / 2 : Value, 0, 1);

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
        drawingContext.DrawLine(LitEdgePen, new Point(bounds.Left + radius, bounds.Bottom + 0.5), new Point(bounds.Right - radius, bounds.Bottom + 0.5));

        var fill = IsMouseOver || IsMouseCaptured ? HoverFillBrush : FillBrush;
        var end = Position * bounds.Width;
        if (IsPan)
        {
            // Filled from the centre towards the side the sound goes to, with the centre always marked.
            var centre = bounds.Width / 2;
            var from = Math.Min(centre, end);
            var width = Math.Abs(end - centre);
            if (width > 0.5)
            {
                drawingContext.DrawRectangle(fill, null, new Rect(from, bounds.Top, width, bounds.Height));
            }
            drawingContext.DrawLine(CentrePen, new Point(centre, bounds.Top - 1.5), new Point(centre, bounds.Bottom + 1.5)); // the centre, where the pan is 0
            DrawThumb(drawingContext, end);
            return;
        }

        if (end > 0.5)
        {
            drawingContext.DrawRoundedRectangle(fill, null, new Rect(0, bounds.Top, end, bounds.Height), radius, radius);
            DrawThumb(drawingContext, end);
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
        if (ActualWidth <= 0)
        {
            return;
        }

        var fraction = point.X / ActualWidth;
        if (!IsPan)
        {
            Value = fraction;
            return;
        }

        // The centre catches the drag a little, so it is easy to find again.
        var pan = fraction * 2 - 1;
        Value = Math.Abs(pan) < 0.05 ? 0 : pan;
    }

    // Puts the value in the bubble and centres the bubble over the end of the fill.
    private void UpdateBubble()
    {
        var percent = Math.Round(Value * 100);
        _bubbleText.Text = !IsPan ? $"{percent:0}%"
            : percent == 0 ? "C"
            : percent < 0 ? $"L{-percent:0}" : $"R{percent:0}";
        if (!_bubble.IsOpen)
        {
            return;
        }

        var child = (FrameworkElement)_bubble.Child;
        child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _bubble.HorizontalOffset = Position * ActualWidth - child.DesiredSize.Width / 2;
        _bubble.VerticalOffset = -child.DesiredSize.Height - 3;
    }

    private static readonly Brush ThumbFace = VerticalStops(
        (Color.FromRgb(0xF6, 0xF7, 0xF8), 0), (Color.FromRgb(0xD5, 0xD8, 0xDB), 0.4), (Color.FromRgb(0x9C, 0xA0, 0xA5), 1));
    private static readonly Pen ThumbEdgePen = FrozenPen(0x1A, 0x1C, 0x1E);
    private static readonly Pen ThumbLinePen = FrozenPen(0x1A, 0x1C, 0x1E);
    private static readonly Brush ThumbShadow = Frozen(0x00, 0x00, 0x00, 0x80);

    // The bar's knob, as a small fader cap of light grey metal standing on it: lit from above, with a shadow
    // and a black line across it where the value is.
    private void DrawThumb(DrawingContext drawingContext, double x)
    {
        var width = Math.Max(7, ActualHeight * 0.62);
        var height = ActualHeight;
        var left = Math.Clamp(x - width / 2, 0, Math.Max(0, ActualWidth - width));
        drawingContext.DrawRoundedRectangle(ThumbShadow, null, new Rect(left + 0.8, 1.5, width, height - 1.5), 2, 2);
        drawingContext.DrawRoundedRectangle(ThumbFace, ThumbEdgePen, new Rect(left, 0.5, width, height - 1.5), 2, 2);
        drawingContext.DrawLine(ThumbLinePen, new Point(left + width / 2, 2.5), new Point(left + width / 2, height - 3));
    }

    private static Brush VerticalStops(params (Color Color, double Offset)[] stops)
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        foreach (var (color, offset) in stops)
        {
            brush.GradientStops.Add(new GradientStop(color, offset));
        }
        brush.Freeze();
        return brush;
    }

    private static Brush Frozen(byte r, byte g, byte b, byte a)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    private static Brush Vertical(Color top, Color bottom)
    {
        var brush = new LinearGradientBrush(top, bottom, 90);
        brush.Freeze();
        return brush;
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
