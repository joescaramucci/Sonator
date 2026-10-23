using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Sonator.ViewModels;

namespace Sonator;

/// <summary>
/// The background of one track's lane on the timeline, drawn at once rather than as an element for every
/// sixteenth, so a long song (thousands of steps a lane) is laid out and drawn again quickly: a line for each
/// sixteenth, brighter on the beats and brightest on the bars, and the track's active steps in its colour with
/// their note. The track's blocks of notes are drawn over it. Steps are the lane's ArrangeTimelineStepViewModels; ShowSteps
/// leaves them out, as for an audio track.
/// </summary>
public sealed class TimelineLane : FrameworkElement
{
    public static readonly DependencyProperty StepsProperty = DependencyProperty.Register(
        nameof(Steps), typeof(IList), typeof(TimelineLane),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnStepsChanged));

    public static readonly DependencyProperty StepWidthProperty = DependencyProperty.Register(
        nameof(StepWidth), typeof(double), typeof(TimelineLane), new FrameworkPropertyMetadata(10.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StepsPerBarProperty = DependencyProperty.Register(
        nameof(StepsPerBar), typeof(int), typeof(TimelineLane), new FrameworkPropertyMetadata(16, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StepsPerBeatProperty = DependencyProperty.Register(
        nameof(StepsPerBeat), typeof(int), typeof(TimelineLane), new FrameworkPropertyMetadata(4, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StepBrushProperty = DependencyProperty.Register(
        nameof(StepBrush), typeof(Brush), typeof(TimelineLane), new FrameworkPropertyMetadata(Brushes.SteelBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowStepsProperty = DependencyProperty.Register(
        nameof(ShowSteps), typeof(bool), typeof(TimelineLane), new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Pen BarPen = FrozenPen(0x4F, 0x8F, 0x83);
    private static readonly Pen BeatPen = FrozenPen(0x3D, 0x56, 0x6D);
    private static readonly Pen SixteenthPen = FrozenPen(0x29, 0x3C, 0x4E);
    private static readonly Pen StepEdgePen = FrozenPen(0xF7, 0xF8, 0xFA);
    private static readonly Typeface LabelFace = new("Segoe UI");

    public IList? Steps
    {
        get => (IList?)GetValue(StepsProperty);
        set => SetValue(StepsProperty, value);
    }

    public double StepWidth
    {
        get => (double)GetValue(StepWidthProperty);
        set => SetValue(StepWidthProperty, value);
    }

    public int StepsPerBar
    {
        get => (int)GetValue(StepsPerBarProperty);
        set => SetValue(StepsPerBarProperty, value);
    }

    public int StepsPerBeat
    {
        get => (int)GetValue(StepsPerBeatProperty);
        set => SetValue(StepsPerBeatProperty, value);
    }

    public Brush StepBrush
    {
        get => (Brush)GetValue(StepBrushProperty);
        set => SetValue(StepBrushProperty, value);
    }

    public bool ShowSteps
    {
        get => (bool)GetValue(ShowStepsProperty);
        set => SetValue(ShowStepsProperty, value);
    }

    private static void OnStepsChanged(DependencyObject owner, DependencyPropertyChangedEventArgs e)
    {
        var lane = (TimelineLane)owner;
        if (e.OldValue is INotifyCollectionChanged old)
        {
            old.CollectionChanged -= lane.OnStepsCollectionChanged;
        }

        if (e.NewValue is INotifyCollectionChanged now)
        {
            now.CollectionChanged += lane.OnStepsCollectionChanged;
        }
    }

    private void OnStepsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    protected override void OnRender(DrawingContext drawingContext)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        var stepWidth = StepWidth;
        if (width <= 0 || height <= 0 || stepWidth <= 0)
        {
            return;
        }

        drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));

        // The grid: one line a sixteenth, as three figures (bars, beats and the rest). Beat and sixteenth lines
        // closer than a few pixels are left out, so narrow bars do not turn into a solid colour.
        var stepsPerBar = Math.Max(1, StepsPerBar);
        var stepsPerBeat = Math.Max(1, StepsPerBeat);
        var bars = new StreamGeometry();
        var beats = new StreamGeometry();
        var sixteenths = new StreamGeometry();
        using (var barLines = bars.Open())
        using (var beatLines = beats.Open())
        using (var sixteenthLines = sixteenths.Open())
        {
            var count = (int)Math.Ceiling(width / stepWidth);
            for (var step = 0; step < count; step++)
            {
                var inBar = step % stepsPerBar;
                if (inBar == 0)
                {
                    var barX = Math.Round(step * stepWidth) + 0.5;
                    barLines.BeginFigure(new Point(barX, 0), false, false);
                    barLines.LineTo(new Point(barX, height), true, false);
                    continue;
                }

                var x = Math.Round(step * stepWidth) + 0.5;
                var onBeat = inBar % stepsPerBeat == 0;
                if (onBeat ? stepWidth * stepsPerBeat < 4 : stepWidth < 3)
                {
                    continue;
                }

                var lines = onBeat ? beatLines : sixteenthLines;
                lines.BeginFigure(new Point(x, 0), false, false);
                lines.LineTo(new Point(x, height), true, false);
            }
        }

        bars.Freeze();
        beats.Freeze();
        sixteenths.Freeze();
        drawingContext.DrawGeometry(null, SixteenthPen, sixteenths);
        drawingContext.DrawGeometry(null, BeatPen, beats);
        drawingContext.DrawGeometry(null, BarPen, bars);

        if (!ShowSteps || Steps is not { Count: > 0 } steps)
        {
            return;
        }

        // The active steps, in the track's colour with a light edge, and their note when there is room.
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        for (var i = 0; i < steps.Count; i++)
        {
            if (steps[i] is not ArrangeTimelineStepViewModel { IsActive: true } step)
            {
                continue;
            }

            var cell = new Rect(i * stepWidth + 1, 8, Math.Max(1, stepWidth - 2), Math.Max(1, height - 16));
            drawingContext.DrawRoundedRectangle(StepBrush, StepEdgePen, cell, 2, 2);
            if (!string.IsNullOrEmpty(step.MidiNoteLabel) && stepWidth >= 14)
            {
                var label = new FormattedText(step.MidiNoteLabel, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, LabelFace, 10, Brushes.White, dpi)
                {
                    MaxTextWidth = Math.Max(1, cell.Width),
                    Trimming = TextTrimming.CharacterEllipsis,
                    MaxLineCount = 1,
                };
                drawingContext.DrawText(label, new Point(cell.X + (cell.Width - Math.Min(label.Width, cell.Width)) / 2, cell.Y + (cell.Height - label.Height) / 2));
            }
        }
    }

    private static Pen FrozenPen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        var pen = new Pen(brush, 1);
        pen.Freeze();
        return pen;
    }
}
