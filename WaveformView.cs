using System.Windows;
using System.Windows.Media;
using Sonator.Services;

namespace Sonator;

/// <summary>
/// An audio clip's wave on the timeline, drawn column by column at the clip's width: in each column a line from
/// the lowest to the highest sample there, so beats and quiet passages show, and more detail as the timeline zooms
/// in. A thin line marks the middle.
/// </summary>
public sealed class WaveformView : FrameworkElement
{
    public static readonly DependencyProperty SummaryProperty = DependencyProperty.Register(
        nameof(Summary), typeof(AudioWaveformSummary), typeof(WaveformView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty WaveBrushProperty = DependencyProperty.Register(
        nameof(WaveBrush), typeof(Brush), typeof(WaveformView),
        new FrameworkPropertyMetadata(Frozen(Color.FromRgb(0x62, 0xB7, 0xA4)), FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Pen MiddlePen = FrozenPen(Color.FromArgb(0x70, 0x62, 0xB7, 0xA4), 0.5);

    public WaveformView()
    {
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
    }

    public AudioWaveformSummary? Summary
    {
        get => (AudioWaveformSummary?)GetValue(SummaryProperty);
        set => SetValue(SummaryProperty, value);
    }

    public Brush WaveBrush
    {
        get => (Brush)GetValue(WaveBrushProperty);
        set => SetValue(WaveBrushProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (Summary is not { Maximums.Length: > 0 } summary || width < 1 || height < 2)
        {
            return;
        }

        var middle = height / 2;
        drawingContext.DrawLine(MiddlePen, new Point(0, middle), new Point(width, middle));

        // One column a device pixel wide, each from its slices' lowest sample to their highest.
        var pixel = 1 / VisualTreeHelper.GetDpi(this).DpiScaleX;
        var columns = (int)Math.Ceiling(width / pixel);
        var slices = summary.Maximums.Length;
        var wave = new StreamGeometry();
        using (var context = wave.Open())
        {
            for (var column = 0; column < columns; column++)
            {
                var from = (int)((long)column * slices / columns);
                var to = Math.Max(from + 1, (int)((long)(column + 1) * slices / columns));
                var low = 0f;
                var high = 0f;
                for (var slice = from; slice < to && slice < slices; slice++)
                {
                    low = Math.Min(low, summary.Minimums[slice]);
                    high = Math.Max(high, summary.Maximums[slice]);
                }

                var x = column * pixel;
                var top = middle - high * middle;
                var bottom = middle - low * middle;
                if (bottom - top < pixel)
                {
                    continue; // silence: the middle line shows it
                }

                context.BeginFigure(new Point(x, top), true, true);
                context.LineTo(new Point(x + pixel, top), false, false);
                context.LineTo(new Point(x + pixel, bottom), false, false);
                context.LineTo(new Point(x, bottom), false, false);
            }
        }

        wave.Freeze();
        drawingContext.DrawGeometry(WaveBrush, null, wave);
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
