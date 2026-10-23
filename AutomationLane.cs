using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Sonator.ViewModels;

namespace Sonator;

/// <summary>
/// A track's volume or pan automation, drawn over its row in the timeline: a line through its points.
/// Click to add a point, drag one to move it (with Shift only up and down), right-click or double-click
/// it to remove it. While there are no points a dashed line shows the fader's value.
/// </summary>
public sealed class AutomationLane : FrameworkElement
{
    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(
        nameof(Points), typeof(IReadOnlyList<AutomationPoint>), typeof(AutomationLane),
        new FrameworkPropertyMetadata(Array.Empty<AutomationPoint>(), FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty StepWidthProperty = DependencyProperty.Register(
        nameof(StepWidth), typeof(double), typeof(AutomationLane),
        new FrameworkPropertyMetadata(10.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(double), typeof(AutomationLane),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IdleValueProperty = DependencyProperty.Register(
        nameof(IdleValue), typeof(double), typeof(AutomationLane),
        new FrameworkPropertyMetadata(0.75, FrameworkPropertyMetadataOptions.AffectsRender));

    private const double Padding = 4;
    private const double HitRadius = 6;

    private static readonly Brush ShadeBrush = Frozen(Color.FromArgb(0x99, 0x0C, 0x12, 0x19));
    private static readonly Brush AreaBrush = Frozen(Color.FromArgb(0x30, 0xFF, 0xD2, 0x4C));
    private static readonly Brush PointBrush = Frozen(Color.FromRgb(0xFF, 0xD2, 0x4C));
    private static readonly Brush ActiveBrush = Frozen(Color.FromRgb(0xFF, 0xFF, 0xFF));
    private static readonly Brush TextBrush = Frozen(Color.FromRgb(0xE8, 0xEE, 0xF4));
    private static readonly Pen LinePen = FrozenPen(Color.FromRgb(0xFF, 0xD2, 0x4C), 1.5);
    private static readonly Pen PointOutline = FrozenPen(Color.FromRgb(0x0C, 0x12, 0x19), 1);
    private static readonly Pen IdlePen = FrozenPen(Color.FromArgb(0xB0, 0xFF, 0xD2, 0x4C), 1, dashed: true);
    private static readonly Pen CentrePen = FrozenPen(Color.FromArgb(0x60, 0xE4, 0xEC, 0xF4), 1, dashed: true);

    private int _dragIndex = -1;
    private bool _editStarted;
    private int _hoverIndex = -1;

    public AutomationLane()
    {
        Cursor = Cursors.Cross;
    }

    // Raised before a point is added, moved or removed, so the change can be undone.
    public event EventHandler? EditStarting;

    public IReadOnlyList<AutomationPoint> Points
    {
        get => (IReadOnlyList<AutomationPoint>)GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    // The width of one sixteenth step in the timeline.
    public double StepWidth
    {
        get => (double)GetValue(StepWidthProperty);
        set => SetValue(StepWidthProperty, value);
    }

    // The value at the bottom of the lane: 0 for a volume, -1 (left) for a pan. The top is always 1.
    public double Minimum
    {
        get => (double)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double IdleValue
    {
        get => (double)GetValue(IdleValueProperty);
        set => SetValue(IdleValueProperty, value);
    }

    private bool IsPan => Minimum < 0;

    private double Y(double value)
    {
        var span = 1 - Minimum;
        var fraction = span <= 0 ? 0 : (value - Minimum) / span;
        return Padding + (1 - Math.Clamp(fraction, 0, 1)) * Math.Max(0, ActualHeight - 2 * Padding);
    }

    private double ValueAt(double y)
    {
        var height = Math.Max(1, ActualHeight - 2 * Padding);
        var fraction = 1 - (y - Padding) / height;
        var value = Minimum + Math.Clamp(fraction, 0, 1) * (1 - Minimum);
        return IsPan && Math.Abs(value) < 0.03 ? 0 : value; // the centre catches a pan
    }

    private double X(double step) => step * StepWidth;

    private double StepAt(double x) => Math.Round(Math.Max(0, x / Math.Max(0.001, StepWidth)), 2);

    private string Describe(double value)
    {
        var percent = Math.Round(value * 100);
        return !IsPan ? $"{percent:0}%" : percent == 0 ? "C" : percent < 0 ? $"L{-percent:0}" : $"R{percent:0}";
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        drawingContext.DrawRectangle(ShadeBrush, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (IsPan)
        {
            drawingContext.DrawLine(CentrePen, new Point(0, Y(0)), new Point(ActualWidth, Y(0)));
        }

        var points = Points;
        if (points.Count == 0)
        {
            var y = Y(IdleValue);
            drawingContext.DrawLine(IdlePen, new Point(0, y), new Point(ActualWidth, y));
            return;
        }

        var line = new StreamGeometry();
        using (var context = line.Open())
        {
            context.BeginFigure(new Point(0, Y(points[0].Value)), false, false);
            foreach (var point in points)
            {
                context.LineTo(new Point(X(point.Step), Y(point.Value)), true, true);
            }
            context.LineTo(new Point(ActualWidth, Y(points[^1].Value)), true, true);
        }
        line.Freeze();

        if (!IsPan)
        {
            // The volume's area is shaded down to silence, so louder reads as more.
            var area = new StreamGeometry();
            using (var context = area.Open())
            {
                context.BeginFigure(new Point(0, ActualHeight - Padding), true, true);
                context.LineTo(new Point(0, Y(points[0].Value)), false, false);
                foreach (var point in points)
                {
                    context.LineTo(new Point(X(point.Step), Y(point.Value)), false, false);
                }
                context.LineTo(new Point(ActualWidth, Y(points[^1].Value)), false, false);
                context.LineTo(new Point(ActualWidth, ActualHeight - Padding), false, false);
            }
            area.Freeze();
            drawingContext.DrawGeometry(AreaBrush, null, area);
        }

        drawingContext.DrawGeometry(null, LinePen, line);

        for (var i = 0; i < points.Count; i++)
        {
            var centre = new Point(X(points[i].Step), Y(points[i].Value));
            var active = i == _dragIndex || i == _hoverIndex;
            drawingContext.DrawEllipse(active ? ActiveBrush : PointBrush, PointOutline, centre, active ? 5 : 3.5, active ? 5 : 3.5);
        }

        var shown = _dragIndex >= 0 ? _dragIndex : _hoverIndex;
        if (shown >= 0 && shown < points.Count)
        {
            var text = new FormattedText(
                Describe(points[shown].Value), CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                11, TextBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            var x = Math.Clamp(X(points[shown].Step) + 7, 0, Math.Max(0, ActualWidth - text.Width));
            var y = Math.Clamp(Y(points[shown].Value) - text.Height / 2, 0, Math.Max(0, ActualHeight - text.Height));
            drawingContext.DrawRectangle(ShadeBrush, null, new Rect(x - 2, y, text.Width + 4, text.Height));
            drawingContext.DrawText(text, new Point(x, y));
        }
    }

    private int HitPoint(Point position)
    {
        var points = Points;
        for (var i = points.Count - 1; i >= 0; i--)
        {
            var dx = X(points[i].Step) - position.X;
            var dy = Y(points[i].Value) - position.Y;
            if (dx * dx + dy * dy <= HitRadius * HitRadius)
            {
                return i;
            }
        }

        return -1;
    }

    private void StartEdit()
    {
        if (!_editStarted)
        {
            _editStarted = true;
            EditStarting?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        e.Handled = true;
        var position = e.GetPosition(this);
        var hit = HitPoint(position);
        _editStarted = false;
        if (hit >= 0 && e.ClickCount == 2)
        {
            RemovePoint(hit);
            return;
        }

        if (hit < 0)
        {
            StartEdit();
            (Points, hit) = Automation.Add(Points, new AutomationPoint(StepAt(position.X), ValueAt(position.Y)));
        }

        _dragIndex = hit;
        CaptureMouse();
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var position = e.GetPosition(this);
        if (!IsMouseCaptured || _dragIndex < 0 || _dragIndex >= Points.Count)
        {
            var hover = HitPoint(position);
            Cursor = hover >= 0 ? Cursors.Hand : Cursors.Cross;
            if (hover != _hoverIndex)
            {
                _hoverIndex = hover;
                InvalidateVisual();
            }
            return;
        }

        StartEdit();
        var current = Points[_dragIndex];
        var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? current.Step : StepAt(position.X);
        Points = Automation.Move(Points, _dragIndex, new AutomationPoint(step, ValueAt(position.Y)));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        _dragIndex = -1;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        if (_hoverIndex >= 0)
        {
            _hoverIndex = -1;
            InvalidateVisual();
        }
    }

    private bool _removedOnRightClick;

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        var hit = HitPoint(e.GetPosition(this));
        _removedOnRightClick = hit >= 0;
        if (hit >= 0)
        {
            _editStarted = false;
            RemovePoint(hit);
            e.Handled = true;
        }
    }

    // A right-click that removed a point does not also open the row's menu.
    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        if (_removedOnRightClick)
        {
            _removedOnRightClick = false;
            e.Handled = true;
        }
    }

    private void RemovePoint(int index)
    {
        StartEdit();
        Points = Automation.Remove(Points, index);
        _hoverIndex = -1;
        InvalidateVisual();
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Color color, double thickness, bool dashed = false)
    {
        var pen = new Pen(Frozen(color), thickness);
        if (dashed)
        {
            pen.DashStyle = new DashStyle(new double[] { 3, 3 }, 0);
        }
        pen.Freeze();
        return pen;
    }
}
