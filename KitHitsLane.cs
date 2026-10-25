using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Sonator.ViewModels;

namespace Sonator;

// A kit piece's lane on the timeline: a tick for each hit of its drum, in the track's colour (fainter when the lane
// shows all the kit's hits, for the room or the overheads).
public sealed class KitHitsLane : FrameworkElement
{
    public static readonly DependencyProperty HitsProperty = DependencyProperty.Register(
        nameof(Hits), typeof(IList), typeof(KitHitsLane), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StepWidthProperty = DependencyProperty.Register(
        nameof(StepWidth), typeof(double), typeof(KitHitsLane), new FrameworkPropertyMetadata(10.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HitBrushProperty = DependencyProperty.Register(
        nameof(HitBrush), typeof(Brush), typeof(KitHitsLane), new FrameworkPropertyMetadata(Brushes.SteelBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsFaintProperty = DependencyProperty.Register(
        nameof(IsFaint), typeof(bool), typeof(KitHitsLane), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Pen Edge = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0xC0, 0xF7, 0xF8, 0xFA)), 0.6));

    public IList? Hits
    {
        get => (IList?)GetValue(HitsProperty);
        set => SetValue(HitsProperty, value);
    }

    public double StepWidth
    {
        get => (double)GetValue(StepWidthProperty);
        set => SetValue(StepWidthProperty, value);
    }

    public Brush HitBrush
    {
        get => (Brush)GetValue(HitBrushProperty);
        set => SetValue(HitBrushProperty, value);
    }

    public bool IsFaint
    {
        get => (bool)GetValue(IsFaintProperty);
        set => SetValue(IsFaintProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (Hits is not { Count: > 0 } hits || ActualHeight <= 0)
        {
            return;
        }

        var width = Math.Clamp(StepWidth * 0.55, 2, 10);
        var height = ActualHeight * 0.62;
        var top = (ActualHeight - height) / 2;
        drawingContext.PushOpacity(IsFaint ? 0.45 : 1);
        foreach (var hit in hits)
        {
            if (hit is double step)
            {
                drawingContext.DrawRoundedRectangle(HitBrush, Edge, new Rect(step * StepWidth, top, width, height), 1.5, 1.5);
            }
        }

        drawingContext.Pop();
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}

// The timeline's rows: a track's, or the narrower one of a piece of its drum kit.
public sealed class TimelineLaneTemplateSelector : DataTemplateSelector
{
    public DataTemplate? TrackTemplate { get; set; }

    public DataTemplate? PieceTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) =>
        item is ArrangeTimelineTrackViewModel { IsKitPiece: true } ? PieceTemplate : TrackTemplate;
}
