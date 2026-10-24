using System.ComponentModel;
using System.Globalization;
using System.Numerics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Sonator.Services.Effects;
using Sonator.ViewModels;

namespace Sonator;

/// <summary>
/// The screen beside an effect's knobs in the FX rack, as on SSL's plug-ins: a dark display drawing what the effect
/// does from its knobs (the reverb's decay, the delay's echoes, the compressor's curve with its attack and
/// release, the equalizer's response, the limiter's ceiling) with handles to drag that turn the same knobs.
/// A double-click on a handle puts its knobs back to their defaults; the wheel on the equalizer's middle handle
/// sets its width.
/// </summary>
public sealed class EffectDisplay : FrameworkElement
{
    public static readonly DependencyProperty EffectProperty = DependencyProperty.Register(
        nameof(Effect), typeof(TrackEffectViewModel), typeof(EffectDisplay),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender,
            (owner, args) => ((EffectDisplay)owner).Watch(args.OldValue as TrackEffectViewModel, args.NewValue as TrackEffectViewModel)));

    public TrackEffectViewModel? Effect
    {
        get => (TrackEffectViewModel?)GetValue(EffectProperty);
        set => SetValue(EffectProperty, value);
    }

    // A point to drag on the screen, made afresh at each drawing.
    private sealed record Handle(string Id, Point At, Action<Point> Drag, int[] Knobs, Action<int>? Wheel = null);

    private readonly List<Handle> _handles = new();
    private string? _hovered;
    private string? _dragged;

    public EffectDisplay()
    {
        Loaded += (_, _) => Watch(null, Effect);
        Unloaded += (_, _) => Watch(Effect, null);
    }

    private void Watch(TrackEffectViewModel? old, TrackEffectViewModel? now)
    {
        if (old is not null)
        {
            old.Changed -= OnEffectChanged;
            old.PropertyChanged -= OnEffectPropertyChanged;
        }

        if (now is not null && IsLoaded)
        {
            now.Changed -= OnEffectChanged;
            now.PropertyChanged -= OnEffectPropertyChanged;
            now.Changed += OnEffectChanged;
            now.PropertyChanged += OnEffectPropertyChanged;
        }
    }

    private void OnEffectChanged(TrackEffectViewModel effect, int knob) => InvalidateVisual();

    private void OnEffectPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(TrackEffectViewModel.InputLevel) or nameof(TrackEffectViewModel.OutputLevel)
            or nameof(TrackEffectViewModel.MeterValue) or nameof(TrackEffectViewModel.IsEnabled))
        {
            InvalidateVisual();
        }
    }

    // ---- Colours, as on SSL's screens ----

    private static readonly Brush ScreenTop = Solid(0x25, 0x28, 0x2B);
    private static readonly Brush GridInk = Solid(0x7A, 0x80, 0x86);
    private static readonly Brush TitleInk = Solid(0x96, 0x9C, 0xA1);
    private static readonly Color Teal = Color.FromRgb(0x3C, 0xC8, 0xA6);
    private static readonly Color Orange = Color.FromRgb(0xFF, 0x96, 0x3A);
    private static readonly Pen GridPen = FrozenPen(Color.FromArgb(0x50, 0x6A, 0x72, 0x7A), 0.6);
    private static readonly Pen AxisPen = FrozenPen(Color.FromArgb(0x90, 0x6A, 0x72, 0x7A), 0.8);
    private static readonly Pen DashedPen = FrozenPen(Color.FromArgb(0x80, 0x6A, 0x72, 0x7A), 0.8, dashed: true);
    private static readonly Pen CurvePen = FrozenPen(Teal, 1.6);
    private static readonly Pen CurveGlowPen = FrozenPen(Color.FromArgb(0x40, Teal.R, Teal.G, Teal.B), 4.5);
    private static readonly Pen ThresholdPen = FrozenPen(Color.FromArgb(0xA0, Orange.R, Orange.G, Orange.B), 0.9, dashed: true);
    private static readonly Pen HandlePen = FrozenPen(Orange, 1.4);
    private static readonly Pen HandleHaloPen = FrozenPen(Color.FromArgb(0x50, Orange.R, Orange.G, Orange.B), 3);
    private static readonly Brush HandleFill = Solid(0x1E, 0x20, 0x23);
    private static readonly Brush OrangeBrush = Frozen(new SolidColorBrush(Orange));
    private static readonly Brush DryBrush = Solid(0xD8, 0xDC, 0xDF);
    private static readonly Typeface LabelFace = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    protected override void OnRender(DrawingContext drawingContext)
    {
        _handles.Clear();
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        if (bounds.Width < 70 || bounds.Height < 50 || Effect is not { HasKnobs: true } effect)
        {
            return;
        }

        DrawScreen(drawingContext, bounds);
        var plot = new Rect(bounds.X + 9, bounds.Y + 17, bounds.Width - 18, bounds.Height - 30);
        drawingContext.PushClip(new RectangleGeometry(new Rect(bounds.X + 2, bounds.Y + 2, bounds.Width - 4, bounds.Height - 4), 3, 3));
        drawingContext.PushOpacity(effect.IsEnabled ? 1 : 0.45);
        switch (effect.Definition.Kind)
        {
            case EffectKind.Verb:
                DrawVerb(drawingContext, plot, effect);
                break;
            case EffectKind.Delay:
                DrawDelay(drawingContext, plot, effect);
                break;
            case EffectKind.Compress:
                DrawCompressor(drawingContext, plot, effect);
                break;
            case EffectKind.EQ:
                DrawEqualizer(drawingContext, plot, effect);
                break;
            case EffectKind.Limiter:
                DrawLimiter(drawingContext, plot, effect);
                break;
            case EffectKind.Harmonizer:
                DrawHarmonizer(drawingContext, plot, effect);
                break;
        }

        foreach (var handle in _handles)
        {
            DrawHandle(drawingContext, handle.At, handle.Id == _hovered || handle.Id == _dragged);
        }

        drawingContext.Pop();
        drawingContext.Pop();
        DrawGlass(drawingContext, bounds);
    }

    // The screen sunk into the panel: a light edge below it, a dark one above, and the glass lit from the top.
    private static void DrawScreen(DrawingContext drawingContext, Rect bounds)
    {
        var lip = FrozenPen(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF), 1);
        drawingContext.DrawRoundedRectangle(null, lip, new Rect(bounds.X + 0.5, bounds.Y + 1.5, bounds.Width - 1, bounds.Height - 1.5), 4, 4);
        var face = new LinearGradientBrush(((SolidColorBrush)ScreenTop).Color, Color.FromRgb(0x18, 0x1A, 0x1C), 90);
        face.Freeze();
        drawingContext.DrawRoundedRectangle(face, FrozenPen(Color.FromRgb(0x2C, 0x2E, 0x31), 1), new Rect(bounds.X + 0.5, bounds.Y + 0.5, bounds.Width - 1, bounds.Height - 1.5), 4, 4);
        var inner = new LinearGradientBrush(Color.FromArgb(0x90, 0, 0, 0), Color.FromArgb(0, 0, 0, 0), 90);
        inner.Freeze();
        drawingContext.DrawRoundedRectangle(inner, null, new Rect(bounds.X + 1, bounds.Y + 1, bounds.Width - 2, 5), 3, 3);
    }

    private static void DrawGlass(DrawingContext drawingContext, Rect bounds)
    {
        var glass = new LinearGradientBrush(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF), Color.FromArgb(0, 0xFF, 0xFF, 0xFF), 90);
        glass.Freeze();
        drawingContext.DrawRoundedRectangle(glass, null, new Rect(bounds.X + 1.5, bounds.Y + 1.5, bounds.Width - 3, (bounds.Height - 3) * 0.45), 3, 3);
    }

    private static void DrawHandle(DrawingContext drawingContext, Point at, bool lit)
    {
        if (lit)
        {
            drawingContext.DrawEllipse(null, HandleHaloPen, at, 6.5, 6.5);
        }

        drawingContext.DrawEllipse(lit ? OrangeBrush : HandleFill, HandlePen, at, 4.2, 4.2);
    }

    // ---- The reverb: its impulse, left above the line and right below, fading over its decay time ----

    private void DrawVerb(DrawingContext drawingContext, Rect plot, TrackEffectViewModel effect)
    {
        var size = Knob(effect, 0) / 100;
        var damping = Knob(effect, 1) / 100;
        var width = Knob(effect, 2) / 100;
        var preDelay = Knob(effect, 3) / 1000;
        var mix = Knob(effect, 4) / 100;
        var decay = DecaySeconds(size);
        var brightDecay = decay * (1 - 0.75 * damping);

        // Time runs on a square-root scale, so the first moments, where the pre-delay is, get room.
        const double Span = 4;
        double X(double seconds) => plot.X + plot.Width * Math.Sqrt(Math.Clamp(seconds / Span, 0, 1));
        double Seconds(double x) => Span * Math.Pow(Math.Clamp((x - plot.X) / plot.Width, 0, 1), 2);

        Title(drawingContext, plot, "DECAY", string.Format(CultureInfo.CurrentCulture, "{0:0.0} s", decay));
        var middle = plot.Y + plot.Height / 2;
        var half = plot.Height / 2 - 1;
        foreach (var (seconds, label) in new[] { (0.1, "0.1"), (0.5, "0.5"), (1.0, "1 s"), (2.0, "2"), (3.0, "3") })
        {
            drawingContext.DrawLine(GridPen, new Point(X(seconds), plot.Top), new Point(X(seconds), plot.Bottom));
            AxisLabel(drawingContext, plot, X(seconds), label);
        }
        drawingContext.DrawLine(AxisPen, new Point(plot.X, middle), new Point(plot.Right, middle));

        // The dry sound, then after the pre-delay the wet tail, its bright part dying sooner with damping.
        var dry = half * (1 - mix * 0.8);
        var dryPen = FrozenPen(Color.FromRgb(0xD8, 0xDC, 0xDF), 1.6);
        drawingContext.DrawLine(dryPen, new Point(X(0) + 1, middle - dry), new Point(X(0) + 1, middle + dry));
        var level = 0.45 + 0.55 * mix;
        var bodyPen = FrozenPen(Color.FromArgb(0xB0, 0x2A, 0x9C, 0x82), 1.1);
        var brightPen = FrozenPen(Color.FromArgb(0xE0, 0x7F, 0xEA, 0xCF), 1.1);
        var outline = new StreamGeometry();
        using (var top = outline.Open())
        {
            var started = false;
            for (var x = X(preDelay) + 1; x < plot.Right; x += 1.6)
            {
                var since = Seconds(x) - preDelay;
                var buildUp = 1 - Math.Exp(-since / 0.012);
                var envelope = Math.Exp(-6.9 * since / decay) * buildUp;
                var bright = Math.Exp(-6.9 * since / brightDecay) * buildUp;
                var seed = (int)(x * 7.3);
                var left = Noise(seed);
                var right = left + (Noise(seed + 9173) - left) * width;
                var height = half * 0.92 * level;
                drawingContext.DrawLine(bodyPen, new Point(x, middle - height * envelope * left), new Point(x, middle + height * envelope * right));
                drawingContext.DrawLine(brightPen, new Point(x, middle - height * bright * left * 0.8), new Point(x, middle + height * bright * right * 0.8));
                if (envelope < 0.004 && since > 0.05)
                {
                    break; // died away
                }

                var point = new Point(x, middle - height * envelope);
                if (!started)
                {
                    top.BeginFigure(point, false, false);
                    started = true;
                }
                else
                {
                    top.LineTo(point, true, true);
                }
            }
        }
        outline.Freeze();
        drawingContext.DrawGeometry(null, CurvePen, outline);

        // Drag the pre-delay along the time; drag the tail for its length (the room's size), and up or down for
        // how soon its bright part dies (the damping).
        _handles.Add(new Handle("pre", new Point(X(preDelay), middle), point =>
            SetKnob(effect, 3, Seconds(point.X) * 1000), new[] { 3 }));
        var tailAt = new Point(X(preDelay + decay * 0.12), plot.Y + 4 + damping * (half - 6));
        _handles.Add(new Handle("size", tailAt, point =>
        {
            var newDecay = Math.Max(0.05, (Seconds(point.X) - preDelay) / 0.12);
            SetKnob(effect, 0, SizeForDecay(newDecay) * 100);
            SetKnob(effect, 1, Math.Clamp((point.Y - plot.Y - 4) / (half - 6), 0, 1) * 100);
        }, new[] { 0, 1 }));
    }

    // The native reverb's combs (Freeverb's, 31 ms long on average) fed back by 0.7 to 0.98 with the size:
    // the time it takes to die down by 60 dB.
    private const double CombSeconds = 0.03125;

    internal static double DecaySeconds(double size) => -3 * CombSeconds / Math.Log10(Math.Clamp(size, 0, 1) * 0.28 + 0.7);

    internal static double SizeForDecay(double seconds) =>
        Math.Clamp((Math.Pow(10, -3 * CombSeconds / Math.Max(seconds, 0.01)) - 0.7) / 0.28, 0, 1);

    // ---- The delay: the echoes, each quieter by the feedback and duller by the high cut ----

    private void DrawDelay(DrawingContext drawingContext, Rect plot, TrackEffectViewModel effect)
    {
        var time = Knob(effect, 0) / 1000;
        var feedback = Knob(effect, 1) / 100;
        var mix = Knob(effect, 2) / 100;
        var pingPong = Knob(effect, 3) >= 0.5;
        var highCut = Knob(effect, 4);

        const double Span = 2.2;
        double X(double seconds) => plot.X + 2 + (plot.Width - 4) * Math.Clamp(seconds / Span, 0, 1);
        double Seconds(double x) => (x - plot.X - 2) / (plot.Width - 4) * Span;

        Title(drawingContext, plot, pingPong ? "ECHOES  L / R" : "ECHOES", string.Format(CultureInfo.CurrentCulture, "{0:0} ms", time * 1000));
        var middle = plot.Y + plot.Height / 2;
        var half = plot.Height / 2 - 2;
        foreach (var (seconds, label) in new[] { (0.5, "0.5 s"), (1.0, "1"), (1.5, "1.5"), (2.0, "2") })
        {
            drawingContext.DrawLine(GridPen, new Point(X(seconds), plot.Top), new Point(X(seconds), plot.Bottom));
            AxisLabel(drawingContext, plot, X(seconds), label);
        }
        drawingContext.DrawLine(AxisPen, new Point(plot.X, middle), new Point(plot.Right, middle));

        var level = 0.35 + 0.65 * mix;
        var dry = half * (1 - mix * 0.8);
        drawingContext.DrawLine(FrozenPen(Color.FromRgb(0xD8, 0xDC, 0xDF), 2), new Point(X(0), middle - dry), new Point(X(0), middle + dry));

        // The decay the echoes follow, then the echoes themselves.
        var trend = new StreamGeometry();
        using (var context = trend.Open())
        {
            context.BeginFigure(new Point(X(time), middle - half * level), false, false);
            for (var x = X(time); x <= plot.Right; x += 2)
            {
                var repeats = Seconds(x) / Math.Max(time, 0.001) - 1;
                context.LineTo(new Point(x, middle - half * level * Math.Pow(Math.Max(feedback, 0.001), repeats)), true, true);
            }
        }
        trend.Freeze();
        drawingContext.DrawGeometry(null, DashedPen, trend);

        var dullness = Math.Clamp(Math.Log(20000 / highCut) / Math.Log(40), 0, 1);
        for (var echo = 1; echo <= 64; echo++)
        {
            var at = echo * time;
            var height = half * level * Math.Pow(feedback, echo - 1);
            if (at > Span || height < 0.6)
            {
                break;
            }

            // Each repeat goes through the high cut again: brighter teal at first, duller after.
            var fade = Math.Pow(1 - dullness * 0.6, echo - 1);
            var colour = Color.FromArgb((byte)(0x60 + 0x9F * fade), (byte)(Teal.R * fade + 0x30 * (1 - fade)), (byte)(Teal.G * fade + 0x58 * (1 - fade)), (byte)(Teal.B * fade + 0x50 * (1 - fade)));
            var pen = FrozenPen(colour, 2.4);
            var glow = FrozenPen(Color.FromArgb((byte)(0x38 * fade), Teal.R, Teal.G, Teal.B), 6);
            var x = X(at);
            var up = !pingPong || echo % 2 == 1;
            var down = !pingPong || echo % 2 == 0;
            var from = new Point(x, middle - (up ? height : 0));
            var to = new Point(x, middle + (down ? height : 0));
            drawingContext.DrawLine(glow, from, to);
            drawingContext.DrawLine(pen, from, to);
        }

        // The first echo: drag it along for the time, up or down for the mix; the second: up or down for the
        // feedback.
        _handles.Add(new Handle("time", new Point(X(time), middle - half * level), point =>
        {
            SetKnob(effect, 0, Math.Max(Seconds(point.X), 0.001) * 1000);
            SetKnob(effect, 2, Math.Clamp(((middle - point.Y) / half - 0.35) / 0.65, 0, 1) * 100);
        }, new[] { 0, 2 }));
        if (2 * time <= Span)
        {
            var second = half * level * feedback;
            _handles.Add(new Handle("feedback", new Point(X(2 * time), pingPong ? middle + second : middle - second), point =>
                SetKnob(effect, 1, Math.Clamp(Math.Abs(point.Y - middle) / (half * level), 0, 0.95) * 100), new[] { 1 }));
        }
    }

    // ---- The compressor: how loud the sound comes out for how loud it goes in, and how fast it reacts ----

    private void DrawCompressor(DrawingContext drawingContext, Rect plot, TrackEffectViewModel effect)
    {
        var threshold = Knob(effect, 0);
        var ratio = Math.Max(Knob(effect, 1), 1);
        var attack = Knob(effect, 2);
        var release = Knob(effect, 3);
        var makeup = Knob(effect, 4);
        var knee = Knob(effect, 5);

        var side = Math.Min(plot.Height, plot.Width >= 150 ? plot.Width * 0.42 : plot.Width);
        var curveArea = new Rect(plot.X, plot.Y, side, plot.Height);
        Title(drawingContext, plot, "COMPRESSION", string.Format(CultureInfo.CurrentCulture, "{0:0.0}:1", ratio));
        DrawTransfer(drawingContext, curveArea, -60, input => input + CompressorGain(input, threshold, ratio, knee) + makeup, effect.InputLevel);
        var thresholdX = curveArea.X + curveArea.Width * (threshold + 60) / 60;
        drawingContext.DrawLine(ThresholdPen, new Point(thresholdX, curveArea.Top), new Point(thresholdX, curveArea.Bottom));

        double CurveY(double db) => curveArea.Bottom - curveArea.Height * Math.Clamp((db + 60) / 60, 0, 1);
        double CurveDb(double y) => (curveArea.Bottom - y) / curveArea.Height * 60 - 60;
        _handles.Add(new Handle("threshold", new Point(thresholdX, CurveY(threshold + CompressorGain(threshold, threshold, ratio, knee) + makeup)),
            point => SetKnob(effect, 0, (point.X - curveArea.X) / curveArea.Width * 60 - 60), new[] { 0 }));
        _handles.Add(new Handle("ratio", new Point(curveArea.Right, CurveY(CompressorGain(0, threshold, ratio, knee) + makeup)), point =>
        {
            // The output wanted at full level sets the slope above the threshold.
            var wanted = Math.Clamp(CurveDb(point.Y) - makeup, threshold + 0.01, 0);
            SetKnob(effect, 1, threshold < -0.5 ? -threshold / (wanted - threshold) : ratio);
        }, new[] { 1 }));

        // Beside it, the attack and the release: how the gain goes down when a loud sound comes in, and comes
        // back when it stops, each on its own time scale.
        var envelope = new Rect(curveArea.Right + 12, plot.Y, plot.Right - curveArea.Right - 12, plot.Height);
        if (envelope.Width < 70)
        {
            return;
        }

        DrawEnvelope(drawingContext, envelope, effect, 2, attack, 3, release);
    }

    // The native compressor's gain change in dB for a level going in, with its soft knee.
    internal static double CompressorGain(double input, double threshold, double ratio, double knee)
    {
        var slope = 1 / Math.Max(ratio, 1) - 1;
        var over = input - threshold;
        if (knee > 0 && Math.Abs(over) <= knee / 2)
        {
            var into = over + knee / 2;
            return slope * into * into / (2 * knee);
        }

        return over > 0 ? slope * over : 0;
    }

    private const double AttackFrom = 0.05;
    private const double AttackTo = 300;
    private const double ReleaseFrom = 1;
    private const double ReleaseTo = 5000;

    // With no attack knob, the gain goes down at once, as the limiter's, and the whole width is its release.
    private void DrawEnvelope(DrawingContext drawingContext, Rect area, TrackEffectViewModel effect, int? attackKnob, double attack, int releaseKnob, double release)
    {
        var middle = attackKnob is null ? area.X + 4 : area.X + area.Width / 2;
        var top = area.Y + 3;
        var depth = area.Height - 8;
        drawingContext.DrawLine(GridPen, new Point(middle, area.Top), new Point(middle, area.Bottom));
        drawingContext.DrawLine(AxisPen, new Point(area.X, top), new Point(area.Right, top));
        if (attackKnob is not null)
        {
            Label(drawingContext, "ATTACK", new Point(area.X + 2, area.Bottom - 9), GridInk, 7.5);
        }
        Label(drawingContext, "RELEASE", new Point(area.Right - 2, area.Bottom - 9), GridInk, 7.5, alignRight: true);

        // Times on a log scale on each side: the attack from 0.05 to 300 ms, the release from 1 ms to 5 s.
        double AttackX(double ms) => area.X + (middle - area.X) * Math.Log(ms / AttackFrom) / Math.Log(AttackTo / AttackFrom);
        double ReleaseX(double ms) => middle + (area.Right - middle) * Math.Log(ms / ReleaseFrom) / Math.Log(ReleaseTo / ReleaseFrom);
        double AttackMs(double x) => AttackFrom * Math.Pow(AttackTo / AttackFrom, Math.Clamp((x - area.X) / (middle - area.X), 0, 1));
        double ReleaseMs(double x) => ReleaseFrom * Math.Pow(ReleaseTo / ReleaseFrom, Math.Clamp((x - middle) / (area.Right - middle), 0, 1));

        // The loud sound that sets it off, faint, along the top.
        var burst = FrozenPen(Color.FromArgb(0x70, Orange.R, Orange.G, Orange.B), 1.2);
        drawingContext.DrawLine(burst, new Point(area.X, top - 1.5), new Point(middle, top - 1.5));

        var curve = new StreamGeometry();
        var fill = new StreamGeometry();
        using (var line = curve.Open())
        using (var shape = fill.Open())
        {
            line.BeginFigure(new Point(area.X, top), false, false);
            shape.BeginFigure(new Point(area.X, top), true, true);
            for (var x = area.X; x <= middle; x += 1)
            {
                var point = new Point(x, top + depth * (1 - Math.Exp(-AttackMs(x) / attack)));
                line.LineTo(point, true, true);
                shape.LineTo(point, false, false);
            }
            var down = attackKnob is null ? 1 : 1 - Math.Exp(-AttackTo / attack);
            for (var x = middle; x <= area.Right; x += 1)
            {
                var point = new Point(x, top + depth * down * Math.Exp(-ReleaseMs(x) / release));
                line.LineTo(point, true, true);
                shape.LineTo(point, false, false);
            }
            shape.LineTo(new Point(area.Right, top), false, false);
        }
        curve.Freeze();
        fill.Freeze();
        drawingContext.DrawGeometry(UnderCurve(area, fromTop: true), null, fill);
        drawingContext.DrawGeometry(null, CurveGlowPen, curve);
        drawingContext.DrawGeometry(null, CurvePen, curve);

        // The handles sit where each has gone two thirds of the way: drag them along for the times.
        if (attackKnob is { } attackIndex)
        {
            _handles.Add(new Handle("attack", new Point(AttackX(attack), top + depth * (1 - Math.Exp(-1))),
                point => SetKnob(effect, attackIndex, AttackMs(point.X)), new[] { attackIndex }));
        }
        var full = attackKnob is null ? 1 : 1 - Math.Exp(-AttackTo / attack);
        _handles.Add(new Handle("release", new Point(ReleaseX(release), top + depth * full * Math.Exp(-1)),
            point => SetKnob(effect, releaseKnob, ReleaseMs(point.X)), new[] { releaseKnob }));
    }

    // ---- The equalizer: the sum of its three bands, from 20 Hz to 20 kHz ----

    private void DrawEqualizer(DrawingContext drawingContext, Rect plot, TrackEffectViewModel effect)
    {
        const double Range = 18;
        double X(double hz) => plot.X + plot.Width * Math.Log(hz / 20) / Math.Log(1000);
        double Hz(double x) => 20 * Math.Pow(1000, Math.Clamp((x - plot.X) / plot.Width, 0, 1));
        double Y(double db) => plot.Y + plot.Height / 2 - plot.Height / 2 * Math.Clamp(db / Range, -1, 1);
        double Db(double y) => (plot.Y + plot.Height / 2 - y) / (plot.Height / 2) * Range;

        Title(drawingContext, plot, "RESPONSE", null);
        foreach (var hz in new[] { 50.0, 200, 500, 2000, 5000 })
        {
            drawingContext.DrawLine(GridPen, new Point(X(hz), plot.Top), new Point(X(hz), plot.Bottom));
        }
        foreach (var (hz, label) in new[] { (100.0, "100"), (1000.0, "1k"), (10000.0, "10k") })
        {
            drawingContext.DrawLine(AxisPen, new Point(X(hz), plot.Top), new Point(X(hz), plot.Bottom));
            AxisLabel(drawingContext, plot, X(hz), label);
        }
        foreach (var db in new[] { -12.0, -6, 6, 12 })
        {
            drawingContext.DrawLine(GridPen, new Point(plot.X, Y(db)), new Point(plot.Right, Y(db)));
        }
        drawingContext.DrawLine(AxisPen, new Point(plot.X, Y(0)), new Point(plot.Right, Y(0)));

        var bands = new[]
        {
            Band.LowShelf(Knob(effect, 0), Knob(effect, 1)),
            Band.Peak(Knob(effect, 2), Knob(effect, 3), Knob(effect, 4)),
            Band.HighShelf(Knob(effect, 5), Knob(effect, 6)),
        };
        var curve = new StreamGeometry();
        var fill = new StreamGeometry();
        using (var line = curve.Open())
        using (var shape = fill.Open())
        {
            shape.BeginFigure(new Point(plot.X, Y(0)), true, true);
            for (var x = plot.X; x <= plot.Right + 0.5; x += 1)
            {
                var hz = Hz(x);
                var point = new Point(x, Y(bands.Sum(band => band.GainDb(hz))));
                if (x == plot.X)
                {
                    line.BeginFigure(point, false, false);
                }
                else
                {
                    line.LineTo(point, true, true);
                }
                shape.LineTo(point, false, false);
            }
            shape.LineTo(new Point(plot.Right, Y(0)), false, false);
        }
        curve.Freeze();
        fill.Freeze();

        // The fill glows with the sound coming out.
        var glow = Math.Clamp(effect.OutputLevel, 0, 1);
        var fillBrush = new LinearGradientBrush(
            Color.FromArgb((byte)(0x40 + 0x50 * glow), Teal.R, Teal.G, Teal.B),
            Color.FromArgb((byte)(0x10 + 0x30 * glow), Teal.R, Teal.G, Teal.B), 90);
        fillBrush.Freeze();
        drawingContext.DrawGeometry(fillBrush, null, fill);
        drawingContext.DrawGeometry(null, CurveGlowPen, curve);
        drawingContext.DrawGeometry(null, CurvePen, curve);

        // A handle for each band: along for its frequency, up or down for its gain; the wheel on the middle one
        // for its width.
        _handles.Add(new Handle("low", new Point(X(Knob(effect, 1)), Y(Knob(effect, 0))), point =>
        {
            SetKnob(effect, 1, Hz(point.X));
            SetKnob(effect, 0, Db(point.Y));
        }, new[] { 0, 1 }));
        _handles.Add(new Handle("mid", new Point(X(Knob(effect, 3)), Y(Knob(effect, 2))), point =>
        {
            SetKnob(effect, 3, Hz(point.X));
            SetKnob(effect, 2, Db(point.Y));
        }, new[] { 2, 3, 4 }, notches => SetKnob(effect, 4, Knob(effect, 4) * Math.Pow(1.12, notches))));
        _handles.Add(new Handle("high", new Point(X(Knob(effect, 6)), Y(Knob(effect, 5))), point =>
        {
            SetKnob(effect, 6, Hz(point.X));
            SetKnob(effect, 5, Db(point.Y));
        }, new[] { 5, 6 }));
    }

    // One of the equalizer's bands, with the native bridge's filters (RBJ's cookbook, at 48 kHz).
    internal readonly record struct Band(double B0, double B1, double B2, double A1, double A2, bool Flat)
    {
        private const double SampleRate = 48000;

        public static Band LowShelf(double gainDb, double hz) => Make(0, gainDb, hz, 0.7071);

        public static Band Peak(double gainDb, double hz, double q) => Make(1, gainDb, hz, Math.Max(0.1, q));

        public static Band HighShelf(double gainDb, double hz) => Make(2, gainDb, hz, 0.7071);

        private static Band Make(int shape, double gainDb, double hz, double q)
        {
            if (Math.Abs(gainDb) < 0.01)
            {
                return new Band(1, 0, 0, 0, 0, true);
            }

            var a = Math.Pow(10, gainDb / 40);
            var w0 = 2 * Math.PI * Math.Clamp(hz, 10, SampleRate * 0.45) / SampleRate;
            var cos = Math.Cos(w0);
            var alpha = Math.Sin(w0) / (2 * q);
            double b0, b1, b2, a0, a1, a2;
            if (shape == 1)
            {
                (b0, b1, b2) = (1 + alpha * a, -2 * cos, 1 - alpha * a);
                (a0, a1, a2) = (1 + alpha / a, -2 * cos, 1 - alpha / a);
            }
            else
            {
                var rootA = 2 * Math.Sqrt(a) * alpha;
                var sign = shape == 0 ? 1 : -1;
                b0 = a * ((a + 1) - sign * (a - 1) * cos + rootA);
                b1 = sign * 2 * a * ((a - 1) - sign * (a + 1) * cos);
                b2 = a * ((a + 1) - sign * (a - 1) * cos - rootA);
                a0 = (a + 1) + sign * (a - 1) * cos + rootA;
                a1 = -sign * 2 * ((a - 1) + sign * (a + 1) * cos);
                a2 = (a + 1) + sign * (a - 1) * cos - rootA;
            }

            return new Band(b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0, false);
        }

        public double GainDb(double hz)
        {
            if (Flat)
            {
                return 0;
            }

            var z = Complex.FromPolarCoordinates(1, -2 * Math.PI * hz / SampleRate);
            var response = (B0 + B1 * z + B2 * z * z) / (1 + A1 * z + A2 * z * z);
            return 20 * Math.Log10(Math.Max(response.Magnitude, 1e-9));
        }
    }

    // ---- The limiter: nothing comes out above the ceiling ----

    private void DrawLimiter(DrawingContext drawingContext, Rect plot, TrackEffectViewModel effect)
    {
        var ceiling = Knob(effect, 0);
        var drive = Knob(effect, 2);
        const double Floor = -36;

        var side = Math.Min(plot.Height, plot.Width);
        var area = new Rect(plot.X, plot.Y, side, plot.Height);
        Title(drawingContext, plot, "CEILING", string.Format(CultureInfo.CurrentCulture, "{0:0.0} dB", ceiling));
        DrawTransfer(drawingContext, area, Floor, input => Math.Min(input + drive, ceiling), effect.InputLevel);
        double X(double db) => area.X + area.Width * (db - Floor) / -Floor;
        double Y(double db) => area.Bottom - area.Height * Math.Clamp((db - Floor) / -Floor, 0, 1);
        drawingContext.DrawLine(ThresholdPen, new Point(area.X, Y(ceiling)), new Point(plot.Right, Y(ceiling)));

        // How far it turns the sound down now, as a bar beside the curve.
        var bar = new Rect(area.Right + 14, area.Y, Math.Min(10, plot.Right - area.Right - 14), area.Height);
        if (bar.Width >= 6)
        {
            drawingContext.DrawRectangle(Solid(0x14, 0x15, 0x17), GridPen, bar);
            var reduction = Math.Clamp(effect.MeterValue / 24, 0, 1);
            drawingContext.DrawRectangle(OrangeBrush, null, new Rect(bar.X + 1, bar.Y + 1, bar.Width - 2, (bar.Height - 2) * reduction));
            Label(drawingContext, "GR", new Point(bar.X + bar.Width + 3, bar.Y), GridInk, 7.5);
        }

        var releaseArea = new Rect(bar.Right + 22, plot.Y, plot.Right - bar.Right - 22, plot.Height);
        if (releaseArea.Width >= 60)
        {
            DrawEnvelope(drawingContext, releaseArea, effect, null, 0, 1, Knob(effect, 1));
        }

        // The knee: along for the drive, up or down for the ceiling.
        _handles.Add(new Handle("ceiling", new Point(X(ceiling - drive), Y(ceiling)), point =>
        {
            var ceilingNow = Math.Clamp(Floor - (point.Y - area.Bottom) / area.Height * -Floor, -12, 0);
            SetKnob(effect, 0, ceilingNow);
            SetKnob(effect, 2, ceilingNow - (Floor + (point.X - area.X) / area.Width * -Floor));
        }, new[] { 0, 2 }));
    }

    // ---- The harmonizer: the note sung in the middle of an octave either way, the harmony voices above and below it ----

    private void DrawHarmonizer(DrawingContext drawingContext, Rect plot, TrackEffectViewModel effect)
    {
        var key = (int)Math.Round(Knob(effect, 0));
        var minor = Knob(effect, 1) >= 0.5;
        var sung = effect.MeterValue > 0 ? (int)Math.Round(effect.MeterValue) : (int?)null;
        Title(drawingContext, plot, key == 0 ? "NO KEY" : $"{Harmony.KeyNames[key]} {(minor ? "MINOR" : "MAJOR")}",
            sung is { } note ? $"SINGING {Harmony.NoteName(note)}" : "SING");

        // Semitones from the voice, from an octave below to an octave above; the scale's notes marked when in a key.
        double Y(double semitones) => plot.Y + plot.Height / 2 - semitones / 13 * (plot.Height / 2);
        var reference = sung ?? 60;
        for (var semitones = -12; semitones <= 12; semitones++)
        {
            var pitchClass = ((reference + semitones - (key - 1)) % 12 + 12) % 12;
            var inScale = key > 0 && (minor ? new[] { 0, 2, 3, 5, 7, 8, 10 } : new[] { 0, 2, 4, 5, 7, 9, 11 }).Contains(pitchClass);
            if (semitones % 12 == 0 || inScale)
            {
                drawingContext.DrawLine(semitones % 12 == 0 ? AxisPen : GridPen, new Point(plot.X, Y(semitones)), new Point(plot.Right, Y(semitones)));
            }
        }

        // The voice, then each harmony voice at its shift, as wide as it is loud, named with its note.
        var middleX = plot.X + plot.Width * 0.18;
        var barWidth = plot.Width * 0.62;
        drawingContext.DrawRoundedRectangle(DryBrush, null, new Rect(middleX, Y(0) - 3, barWidth * Knob(effect, 7) / 100 + 4, 6), 2, 2);
        Label(drawingContext, "VOICE", new Point(plot.X, Y(0) - 6), TitleInk, 8);
        for (var voice = 0; voice < 2; voice++)
        {
            var steps = (int)Math.Round(Knob(effect, 2 + voice));
            if (steps == 0)
            {
                continue;
            }

            var shift = Harmony.Shift(reference, steps, key, minor);
            var level = Knob(effect, 4 + voice) / 100;
            var y = Y(shift);
            var brush = voice == 0 ? (Brush)OrangeBrush : Solid(0x3C, 0xC8, 0xA6);
            drawingContext.DrawRoundedRectangle(brush, null, new Rect(middleX, y - 3, barWidth * level + 4, 6), 2, 2);
            Label(drawingContext, voice == 0 ? "V1" : "V2", new Point(plot.X, y - 6), TitleInk, 8);
            if (sung is not null)
            {
                Label(drawingContext, Harmony.NoteName(reference + shift), new Point(plot.Right, y - 6), TitleInk, 8, alignRight: true);
            }
        }
    }

    // A level curve on a square of decibels from the floor to 0, over the 1:1 line, with the sound going in now
    // shown as a dot on it.
    private void DrawTransfer(DrawingContext drawingContext, Rect area, double floor, Func<double, double> output, double inputLevel)
    {
        double X(double db) => area.X + area.Width * (db - floor) / -floor;
        double Y(double db) => area.Bottom - area.Height * Math.Clamp((db - floor) / -floor, 0, 1);
        for (var db = floor; db <= 0.1; db += -floor / 5)
        {
            drawingContext.DrawLine(GridPen, new Point(X(db), area.Top), new Point(X(db), area.Bottom));
            drawingContext.DrawLine(GridPen, new Point(area.X, Y(db)), new Point(area.Right, Y(db)));
        }
        drawingContext.DrawLine(DashedPen, new Point(area.X, area.Bottom), new Point(area.Right, area.Top));

        var curve = new StreamGeometry();
        var fill = new StreamGeometry();
        using (var line = curve.Open())
        using (var shape = fill.Open())
        {
            shape.BeginFigure(new Point(area.X, area.Bottom), true, true);
            for (var x = area.X; x <= area.Right + 0.5; x += 1)
            {
                var input = floor + (x - area.X) / area.Width * -floor;
                var point = new Point(x, Y(output(input)));
                if (x == area.X)
                {
                    line.BeginFigure(point, false, false);
                }
                else
                {
                    line.LineTo(point, true, true);
                }
                shape.LineTo(point, false, false);
            }
            shape.LineTo(new Point(area.Right, area.Bottom), false, false);
        }
        curve.Freeze();
        fill.Freeze();
        drawingContext.DrawGeometry(UnderCurve(area, fromTop: false), null, fill);
        drawingContext.DrawGeometry(null, CurveGlowPen, curve);
        drawingContext.DrawGeometry(null, CurvePen, curve);

        // The meters' scale runs from -48 dBFS.
        if (inputLevel > 0.01)
        {
            var input = Math.Max(-48 + inputLevel * 48, floor);
            var dot = new Point(X(input), Y(output(input)));
            drawingContext.DrawEllipse(Frozen(new SolidColorBrush(Color.FromArgb(0x50, Orange.R, Orange.G, Orange.B))), null, dot, 6, 6);
            drawingContext.DrawEllipse(OrangeBrush, null, dot, 2.6, 2.6);
        }
    }

    private static Brush UnderCurve(Rect area, bool fromTop)
    {
        var brush = new LinearGradientBrush(Color.FromArgb(0x48, Teal.R, Teal.G, Teal.B), Color.FromArgb(0x08, Teal.R, Teal.G, Teal.B), fromTop ? 90 : 270);
        brush.Freeze();
        return brush;
    }

    // ---- Labels ----

    private void Title(DrawingContext drawingContext, Rect plot, string title, string? value)
    {
        Label(drawingContext, title, new Point(plot.X - 1, plot.Y - 14), TitleInk, 8);
        if (value is not null)
        {
            Label(drawingContext, value, new Point(plot.Right + 1, plot.Y - 14), OrangeBrush, 8, alignRight: true);
        }
    }

    private void AxisLabel(DrawingContext drawingContext, Rect plot, double x, string text) =>
        Label(drawingContext, text, new Point(x, plot.Bottom + 1), GridInk, 7.5, centred: true);

    private void Label(DrawingContext drawingContext, string text, Point at, Brush brush, double size, bool alignRight = false, bool centred = false)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, LabelFace, size, brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        var x = alignRight ? at.X - formatted.Width : centred ? at.X - formatted.Width / 2 : at.X;
        drawingContext.DrawText(formatted, new Point(x, at.Y));
    }

    // ---- Dragging the handles ----

    private Handle? HandleAt(Point point) => _handles
        .Where(handle => (handle.At - point).Length <= 8)
        .OrderBy(handle => (handle.At - point).Length)
        .FirstOrDefault();

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (HandleAt(e.GetPosition(this)) is not { } handle)
        {
            return;
        }

        e.Handled = true;
        if (e.ClickCount == 2 && Effect is { } effect)
        {
            foreach (var knob in handle.Knobs)
            {
                effect.Parameters[knob].ResetToDefault();
            }
            return;
        }

        _dragged = handle.Id;
        CaptureMouse();
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var point = e.GetPosition(this);
        if (_dragged is not null && IsMouseCaptured)
        {
            _handles.FirstOrDefault(handle => handle.Id == _dragged)?.Drag(point);
            return;
        }

        var hovered = HandleAt(point)?.Id;
        if (hovered != _hovered)
        {
            _hovered = hovered;
            Cursor = hovered is null ? null : Cursors.Hand;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_dragged is null)
        {
            return;
        }

        _dragged = null;
        ReleaseMouseCapture();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        if (_hovered is not null && _dragged is null)
        {
            _hovered = null;
            Cursor = null;
            InvalidateVisual();
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (HandleAt(e.GetPosition(this)) is { Wheel: { } wheel })
        {
            wheel(Math.Sign(e.Delta));
            e.Handled = true;
        }
    }

    // ---- Helpers ----

    private static double Knob(TrackEffectViewModel effect, int index) =>
        index < effect.Parameters.Count ? effect.Parameters[index].Value : 0;

    private static void SetKnob(TrackEffectViewModel effect, int index, double value)
    {
        if (index < effect.Parameters.Count && double.IsFinite(value))
        {
            effect.Parameters[index].Value = value;
        }
    }

    // A steady pseudo-random height from 0.35 to 1, the same for the same seed, for the reverb's grain.
    private static double Noise(int seed)
    {
        var hash = unchecked((uint)seed * 2654435761u);
        hash ^= hash >> 15;
        hash = unchecked(hash * 2246822519u);
        hash ^= hash >> 13;
        return 0.35 + 0.65 * (hash % 1000) / 999.0;
    }

    private static Brush Solid(byte r, byte g, byte b) => Frozen(new SolidColorBrush(Color.FromRgb(r, g, b)));

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Color colour, double thickness, bool dashed = false)
    {
        var pen = new Pen(Frozen(new SolidColorBrush(colour)), thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (dashed)
        {
            pen.DashStyle = new DashStyle(new[] { 2.0, 2.5 }, 0);
        }
        pen.Freeze();
        return pen;
    }
}
