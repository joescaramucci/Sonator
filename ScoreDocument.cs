using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Sonator.Services.Score;

namespace Sonator;

// A sheet laid out on A4 pages and drawn: the title, author and tempo at the top of the first page, then systems of
// bars across the page, each a staff with its clef and key (and the time on the first), the notes, rests, ties,
// beams and the words under them; a page number at the foot. Sizes are in WPF units (1/96 inch).
public sealed class ScoreDocument
{
    public const double PageWidth = 793.7;  // A4, 210 mm
    public const double PageHeight = 1122.5; // 297 mm
    private const double Margin = 60;
    private const double Sp = 8; // one staff space
    private const double SystemHeight = 17 * Sp;
    private const double TitleHeight = 96;

    private static readonly Typeface Text = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface TextBold = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface Serif = new(new FontFamily("Times New Roman"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    private static readonly Typeface Music = new(new FontFamily("Segoe UI Symbol"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Brush Ink = Brushes.Black;
    private static readonly Pen Line = Frozen(new Pen(Ink, 0.8));
    private static readonly Pen Stem = Frozen(new Pen(Ink, 1.0));
    private static readonly Pen Bar = Frozen(new Pen(Ink, 1.0));
    private static readonly Pen ThickBar = Frozen(new Pen(Ink, 3.0));
    private static readonly Pen TiePen = Frozen(new Pen(Ink, 1.1));

    private readonly ScoreSheet _sheet;
    private readonly string _title;
    private readonly string _author;
    private readonly double _tempo;
    private readonly List<List<SystemLayout>> _pages = new();

    public ScoreDocument(ScoreSheet sheet, string title, string author, double tempo)
    {
        _sheet = sheet;
        _title = title;
        _author = author;
        _tempo = tempo;
        Layout();
    }

    public int PageCount => _pages.Count;

    // ---- Layout ----

    // A system: its bars, where each starts and how wide it is, and the width of its clef, key and time.
    private sealed record SystemLayout(IReadOnlyList<(ScoreBar Bar, double X, double Width, IReadOnlyList<double> EventX)> Bars, double Prefix, bool ShowsTime);

    private double ContentWidth => PageWidth - 2 * Margin;

    private static double DurationWidth(int duration) => duration switch
    {
        1 => 2.4, 2 => 3.0, 3 => 3.4, 4 => 3.8, 6 => 4.4, 8 => 5.2, 12 => 6.0, _ => 7.0,
    } * Sp;

    // The least room an event needs: for its value, an accidental before it, and its syllable.
    private static double EventWidth(ScoreEvent item)
    {
        if (item.IsWholeBarRest)
        {
            return 7 * Sp;
        }

        var width = DurationWidth(item.Duration) + (item.Pitches.Any(pitch => pitch.Accidental != Accidental.None) ? 1.4 * Sp : 0);
        if (item.Syllable is { Length: > 0 } syllable)
        {
            width = Math.Max(width, Measure(syllable, Text, 11).Width + (item.HyphenAfter ? 1.6 * Sp : 0.8 * Sp));
        }

        return width;
    }

    private const double BarPadLeft = 1.4 * Sp;
    private const double BarPadRight = 0.8 * Sp;

    private double PrefixWidth(bool withTime) =>
        4.0 * Sp + Math.Abs(_sheet.KeyFifths) * 1.1 * Sp + (withTime ? 2.8 * Sp : 0) + 0.6 * Sp;

    private void Layout()
    {
        var systems = new List<SystemLayout>();
        var pending = new List<(ScoreBar Bar, IReadOnlyList<double> Widths)>();
        var used = 0.0;
        var first = true;

        void Close()
        {
            if (pending.Count == 0)
            {
                return;
            }

            var prefix = PrefixWidth(first);
            var natural = pending.Sum(bar => bar.Widths.Sum() + BarPadLeft + BarPadRight);
            var stretch = Math.Max(1, (ContentWidth - prefix) / natural);
            // The last system of the song is not stretched far when it holds few bars.
            if (ReferenceEquals(pending[^1].Bar, _sheet.Bars[^1]) && natural < (ContentWidth - prefix) * 0.6)
            {
                stretch = Math.Min(stretch, 1.25);
            }

            var x = prefix;
            var bars = new List<(ScoreBar, double, double, IReadOnlyList<double>)>();
            foreach (var (bar, widths) in pending)
            {
                var eventX = new List<double>();
                var at = BarPadLeft * stretch;
                foreach (var width in widths)
                {
                    eventX.Add(at);
                    at += width * stretch;
                }

                var barWidth = at + BarPadRight * stretch;
                bars.Add((bar, x, barWidth, eventX));
                x += barWidth;
            }

            systems.Add(new SystemLayout(bars, prefix, first));
            first = false;
            pending = new List<(ScoreBar, IReadOnlyList<double>)>();
            used = 0;
        }

        foreach (var bar in _sheet.Bars)
        {
            var widths = bar.Events.Select(EventWidth).ToList();
            var barWidth = widths.Sum() + BarPadLeft + BarPadRight;
            if (pending.Count > 0 && PrefixWidth(first) + used + barWidth > ContentWidth)
            {
                Close();
            }

            pending.Add((bar, widths));
            used += barWidth;
        }

        Close();

        var page = new List<SystemLayout>();
        var room = PageHeight - 2 * Margin - TitleHeight - 24;
        foreach (var system in systems)
        {
            if (room < SystemHeight && page.Count > 0)
            {
                _pages.Add(page);
                page = new List<SystemLayout>();
                room = PageHeight - 2 * Margin - 24;
            }

            page.Add(system);
            room -= SystemHeight;
        }

        _pages.Add(page);
    }

    // ---- Drawing ----

    // Draws one page, white paper included.
    public void DrawPage(DrawingContext dc, int pageIndex)
    {
        dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, PageWidth, PageHeight));
        if (pageIndex < 0 || pageIndex >= _pages.Count)
        {
            return;
        }

        var y = Margin;
        if (pageIndex == 0)
        {
            var title = Format(_title, TextBold, 24);
            dc.DrawText(title, new Point((PageWidth - title.Width) / 2, y));
            if (!string.IsNullOrWhiteSpace(_author))
            {
                var author = Format(_author, Text, 12);
                dc.DrawText(author, new Point(PageWidth - Margin - author.Width, y + 44));
            }

            var tempo = Format("♩ = " + Math.Round(_tempo).ToString(CultureInfo.InvariantCulture), Music, 13);
            dc.DrawText(tempo, new Point(Margin, y + 64));
            y += TitleHeight;
        }

        foreach (var system in _pages[pageIndex])
        {
            DrawSystem(dc, system, y);
            y += SystemHeight;
        }

        var number = Format($"{pageIndex + 1} / {_pages.Count}", Text, 10);
        dc.DrawText(number, new Point((PageWidth - number.Width) / 2, PageHeight - Margin + 16));
    }

    private int BottomDiatonic => _sheet.Clef == Clef.Treble ? 30 : 18; // E4, G2
    private int MiddleDiatonic => BottomDiatonic + 4;

    private void DrawSystem(DrawingContext dc, SystemLayout system, double top)
    {
        var left = Margin;
        var staffTop = top + 5 * Sp;
        var staffBottom = staffTop + 4 * Sp;
        double Y(int diatonic) => staffBottom - (diatonic - BottomDiatonic) * Sp / 2;

        var right = left + system.Bars.Sum(bar => bar.Width) + system.Prefix;
        for (var line = 0; line < 5; line++)
        {
            var lineY = staffTop + line * Sp;
            dc.DrawLine(Line, new Point(left, lineY), new Point(right, lineY));
        }

        dc.DrawLine(Bar, new Point(left, staffTop), new Point(left, staffBottom));

        // Clef, key and time.
        var x = left + 0.6 * Sp;
        if (_sheet.Clef == Clef.Treble)
        {
            DrawGlyph(dc, "\U0001D11E", new Rect(x, staffTop - 1.8 * Sp, 3 * Sp, 7.6 * Sp));
        }
        else
        {
            DrawGlyph(dc, "\U0001D122", new Rect(x, staffTop - 0.05 * Sp, 3 * Sp, 3.3 * Sp));
        }

        x = left + 4.0 * Sp;
        var sharps = _sheet.KeyFifths > 0;
        int[] sharpPlaces = [38, 35, 39, 36, 33, 37, 34];
        int[] flatPlaces = [34, 37, 33, 36, 32, 35, 31];
        var shift = _sheet.Clef == Clef.Treble ? 0 : -14;
        for (var i = 0; i < Math.Abs(_sheet.KeyFifths); i++)
        {
            var place = (sharps ? sharpPlaces[i] : flatPlaces[i]) + shift;
            DrawAccidental(dc, sharps ? Accidental.Sharp : Accidental.Flat, x + 0.5 * Sp, Y(place));
            x += 1.1 * Sp;
        }

        if (system.ShowsTime)
        {
            var numerator = Format(_sheet.Numerator.ToString(CultureInfo.InvariantCulture), Serif, 2.6 * Sp);
            var denominator = Format(_sheet.Denominator.ToString(CultureInfo.InvariantCulture), Serif, 2.6 * Sp);
            var timeX = x + 0.4 * Sp;
            var width = Math.Max(numerator.Width, denominator.Width);
            dc.DrawText(numerator, new Point(timeX + (width - numerator.Width) / 2, staffTop + Sp - numerator.Height / 2));
            dc.DrawText(denominator, new Point(timeX + (width - denominator.Width) / 2, staffTop + 3 * Sp - denominator.Height / 2));
        }

        // The bar number over the first bar of the system.
        if (system.Bars.Count > 0)
        {
            var label = Format(system.Bars[0].Bar.Number.ToString(CultureInfo.InvariantCulture), Text, 9);
            dc.DrawText(label, new Point(left, staffTop - 2.4 * Sp - label.Height));
        }

        // The notes, then the ties and beams that join them.
        var heads = new List<(ScoreEvent Event, double X, IReadOnlyList<double> Ys, bool StemUp)>();
        foreach (var (bar, barX, barWidth, eventX) in system.Bars)
        {
            var groups = BeamGroups(bar);
            for (var i = 0; i < bar.Events.Count; i++)
            {
                var item = bar.Events[i];
                var eventLeft = left + barX + eventX[i];
                if (item.IsWholeBarRest)
                {
                    var middle = left + barX + barWidth / 2;
                    dc.DrawRectangle(Ink, null, new Rect(middle - 0.7 * Sp, staffTop + Sp, 1.4 * Sp, 0.55 * Sp));
                    heads.Add((item, middle, Array.Empty<double>(), true));
                    continue;
                }

                var headX = eventLeft + (item.Pitches.Any(pitch => pitch.Accidental != Accidental.None) ? 1.4 * Sp : 0) + 0.7 * Sp;
                if (item.IsRest)
                {
                    DrawRest(dc, item.Duration, headX, staffTop);
                    heads.Add((item, headX, Array.Empty<double>(), true));
                    continue;
                }

                var group = groups.FirstOrDefault(candidate => candidate.Contains(i));
                var stemUp = StemUp(group is null ? new[] { item } : group.Select(index => bar.Events[index]).ToArray());
                var ys = DrawNote(dc, item, headX, Y, staffTop, staffBottom, stemUp, drawStem: group is null);
                heads.Add((item, headX, ys, stemUp));
            }

            foreach (var group in groups)
            {
                DrawBeam(dc, group.Select(index => (bar.Events[index], heads[heads.Count - bar.Events.Count + index])).ToList(), Y);
            }

            var barLineX = left + barX + barWidth;
            if (ReferenceEquals(bar, _sheet.Bars[^1]))
            {
                dc.DrawLine(Bar, new Point(barLineX - 4, staffTop), new Point(barLineX - 4, staffBottom));
                dc.DrawLine(ThickBar, new Point(barLineX - 0.5, staffTop), new Point(barLineX - 0.5, staffBottom));
            }
            else
            {
                dc.DrawLine(Bar, new Point(barLineX, staffTop), new Point(barLineX, staffBottom));
            }
        }

        // Ties: to the next note, or to the end of the staff when it goes on in the next system.
        for (var i = 0; i < heads.Count; i++)
        {
            var (item, headX, ys, stemUp) = heads[i];
            if (!item.TieToNext || ys.Count == 0)
            {
                continue;
            }

            var toX = i + 1 < heads.Count ? heads[i + 1].X - 0.8 * Sp : right - 0.4 * Sp;
            foreach (var noteY in ys)
            {
                DrawTie(dc, headX + 0.8 * Sp, toX, noteY, below: stemUp);
            }
        }

        // The words, under the staff, with a hyphen between the syllables of a word.
        var lyricY = staffBottom + 4.2 * Sp;
        for (var i = 0; i < heads.Count; i++)
        {
            var (item, headX, _, _) = heads[i];
            if (item.Syllable is not { Length: > 0 } syllable)
            {
                continue;
            }

            var text = Format(syllable, Text, 11);
            dc.DrawText(text, new Point(headX - text.Width / 2, lyricY));
            if (item.HyphenAfter)
            {
                var next = heads.Skip(i + 1).FirstOrDefault(other => other.Event.Syllable is { Length: > 0 });
                var nextLeft = next.Event is not null
                    ? next.X - Format(next.Event.Syllable!, Text, 11).Width / 2
                    : headX + text.Width / 2 + 2 * Sp;
                var from = headX + text.Width / 2;
                var hyphenX = (from + nextLeft) / 2;
                dc.DrawLine(Stem, new Point(hyphenX - 0.35 * Sp, lyricY + 8.5), new Point(hyphenX + 0.35 * Sp, lyricY + 8.5));
            }
        }
    }

    // The runs of eighths and sixteenths within a beat (a dotted quarter's worth in 6/8 and the like), joined by a
    // beam: the indices of their events, two or more to a run.
    private List<List<int>> BeamGroups(ScoreBar bar)
    {
        var compound = _sheet.Denominator == 8 && _sheet.Numerator % 3 == 0 && _sheet.Numerator > 3;
        var beat = compound ? 6 : Math.Max(_sheet.StepsPerBeat, 2);
        if (_sheet.Denominator == 8 && !compound)
        {
            beat = 2 * _sheet.StepsPerBeat; // pairs of eighths
        }

        var groups = new List<List<int>>();
        var current = new List<int>();
        var currentBeat = -1;
        for (var i = 0; i < bar.Events.Count; i++)
        {
            var item = bar.Events[i];
            var beamable = !item.IsRest && item.Duration <= 3;
            var itemBeat = item.Start / beat;
            if (!beamable || itemBeat != currentBeat)
            {
                if (current.Count > 1)
                {
                    groups.Add(current);
                }

                current = new List<int>();
                currentBeat = itemBeat;
            }

            if (beamable)
            {
                current.Add(i);
            }
        }

        if (current.Count > 1)
        {
            groups.Add(current);
        }

        return groups;
    }

    private bool StemUp(IReadOnlyList<ScoreEvent> events)
    {
        var places = events.SelectMany(item => item.Pitches).Select(pitch => pitch.Diatonic).ToList();
        if (places.Count == 0)
        {
            return true;
        }

        var highest = places.Max() - MiddleDiatonic;
        var lowest = MiddleDiatonic - places.Min();
        return lowest >= highest;
    }

    // Draws a note or chord: accidentals, heads, ledger lines, dot and, when not beamed, its stem and flags.
    // Returns the heads' heights.
    private IReadOnlyList<double> DrawNote(DrawingContext dc, ScoreEvent item, double x, Func<int, double> y,
        double staffTop, double staffBottom, bool stemUp, bool drawStem)
    {
        var filled = item.Duration < 8;
        var ys = new List<double>();
        var sorted = item.Pitches.OrderBy(pitch => pitch.Diatonic).ToList();
        for (var i = 0; i < sorted.Count; i++)
        {
            var pitch = sorted[i];
            var noteY = y(pitch.Diatonic);
            // A second above the note below sits beside it, on the side away from the stem.
            var headX = x;
            if (i > 0 && pitch.Diatonic - sorted[i - 1].Diatonic == 1 && Math.Abs(ys[^1] - noteY) < Sp)
            {
                headX += stemUp ? 1.3 * Sp : -1.3 * Sp;
            }

            // Ledger lines, above or below the staff.
            for (var ledger = staffBottom + Sp; ledger <= noteY + 0.1; ledger += Sp)
            {
                dc.DrawLine(Line, new Point(headX - 1.1 * Sp, ledger), new Point(headX + 1.1 * Sp, ledger));
            }

            for (var ledger = staffTop - Sp; ledger >= noteY - 0.1; ledger -= Sp)
            {
                dc.DrawLine(Line, new Point(headX - 1.1 * Sp, ledger), new Point(headX + 1.1 * Sp, ledger));
            }

            if (pitch.Accidental != Accidental.None)
            {
                DrawAccidental(dc, pitch.Accidental, x - 1.8 * Sp, noteY);
            }

            DrawHead(dc, headX, noteY, filled, item.Duration == 16);
            if (item.Duration is 3 or 6 or 12)
            {
                var onLine = ((pitch.Diatonic - BottomDiatonic) % 2 + 2) % 2 == 0;
                dc.DrawEllipse(Ink, null, new Point(x + 1.4 * Sp, noteY - (onLine ? Sp / 2 : 0)), 1.6, 1.6);
            }

            ys.Add(noteY);
        }

        if (drawStem && item.Duration < 16)
        {
            var stemX = stemUp ? x + 0.62 * Sp : x - 0.62 * Sp;
            var fromY = stemUp ? ys.Max() : ys.Min();
            var toY = stemUp ? ys.Min() - 3.5 * Sp : ys.Max() + 3.5 * Sp;
            dc.DrawLine(Stem, new Point(stemX, fromY), new Point(stemX, toY));
            var flags = item.Duration switch { 1 => 2, 2 or 3 => 1, _ => 0 };
            for (var flag = 0; flag < flags; flag++)
            {
                DrawFlag(dc, stemX, toY + (stemUp ? flag : -flag) * 0.9 * Sp, stemUp);
            }
        }

        return ys;
    }

    private static void DrawHead(DrawingContext dc, double x, double y, bool filled, bool whole)
    {
        var head = new EllipseGeometry(new Point(x, y), 0.68 * Sp, 0.46 * Sp, whole ? null : new RotateTransform(-22, x, y));
        if (filled)
        {
            dc.DrawGeometry(Ink, null, head);
        }
        else
        {
            dc.DrawGeometry(null, new Pen(Ink, whole ? 1.6 : 1.3), head);
        }
    }

    private static void DrawFlag(DrawingContext dc, double stemX, double tipY, bool stemUp)
    {
        var direction = stemUp ? 1 : -1;
        var figure = new PathFigure { StartPoint = new Point(stemX, tipY), IsClosed = true };
        figure.Segments.Add(new BezierSegment(
            new Point(stemX + 0.2 * Sp, tipY + direction * 1.2 * Sp),
            new Point(stemX + 1.5 * Sp, tipY + direction * 1.5 * Sp),
            new Point(stemX + 1.0 * Sp, tipY + direction * 3.0 * Sp), true));
        figure.Segments.Add(new BezierSegment(
            new Point(stemX + 1.2 * Sp, tipY + direction * 1.9 * Sp),
            new Point(stemX + 0.3 * Sp, tipY + direction * 1.6 * Sp),
            new Point(stemX, tipY + direction * 1.0 * Sp), true));
        dc.DrawGeometry(Ink, null, new PathGeometry([figure]));
    }

    // A beam over a run of eighths and sixteenths: flat, beyond the furthest note, a second beam between sixteenths
    // and a short one for a sixteenth on its own.
    private void DrawBeam(DrawingContext dc, IReadOnlyList<(ScoreEvent Event, (ScoreEvent Event, double X, IReadOnlyList<double> Ys, bool StemUp) Head)> notes, Func<int, double> y)
    {
        var stemUp = notes[0].Head.StemUp;
        var stems = notes.Select(note => stemUp ? note.Head.X + 0.62 * Sp : note.Head.X - 0.62 * Sp).ToList();
        var beamY = stemUp
            ? notes.Min(note => note.Head.Ys.Min()) - 3.5 * Sp
            : notes.Max(note => note.Head.Ys.Max()) + 3.5 * Sp;
        for (var i = 0; i < notes.Count; i++)
        {
            var from = stemUp ? notes[i].Head.Ys.Max() : notes[i].Head.Ys.Min();
            dc.DrawLine(Stem, new Point(stems[i], from), new Point(stems[i], beamY));
        }

        var thickness = 0.5 * Sp;
        var direction = stemUp ? 1 : -1;
        Rect BeamRect(double x1, double x2, int level)
        {
            var top = beamY + direction * level * 0.8 * Sp;
            return new Rect(x1, stemUp ? top : top - thickness, x2 - x1, thickness);
        }

        dc.DrawRectangle(Ink, null, BeamRect(stems[0], stems[^1] + 0.5, 0));
        for (var i = 0; i < notes.Count; i++)
        {
            if (notes[i].Event.Duration != 1)
            {
                continue;
            }

            if (i + 1 < notes.Count && notes[i + 1].Event.Duration == 1)
            {
                dc.DrawRectangle(Ink, null, BeamRect(stems[i], stems[i + 1] + 0.5, 1));
            }
            else if (!(i > 0 && notes[i - 1].Event.Duration == 1))
            {
                var stub = i > 0 ? stems[i] - 1.2 * Sp : stems[i];
                dc.DrawRectangle(Ink, null, BeamRect(stub, stub + 1.2 * Sp, 1));
            }
        }
    }

    private static void DrawRest(DrawingContext dc, int duration, double x, double staffTop)
    {
        var middle = staffTop + 2 * Sp;
        switch (duration)
        {
            case 16 or 12:
                dc.DrawRectangle(Ink, null, new Rect(x - 0.7 * Sp, staffTop + Sp, 1.4 * Sp, 0.55 * Sp));
                break;
            case 8 or 6:
                dc.DrawRectangle(Ink, null, new Rect(x - 0.7 * Sp, middle - 0.55 * Sp, 1.4 * Sp, 0.55 * Sp));
                break;
            case 4 or 3:
                DrawGlyph(dc, "\U0001D13D", new Rect(x - 0.6 * Sp, middle - 1.5 * Sp, 1.2 * Sp, 3 * Sp));
                break;
            case 2:
                DrawGlyph(dc, "\U0001D13E", new Rect(x - 0.6 * Sp, middle - 1.0 * Sp, 1.2 * Sp, 2 * Sp));
                break;
            default:
                DrawGlyph(dc, "\U0001D13F", new Rect(x - 0.7 * Sp, middle - 1.0 * Sp, 1.4 * Sp, 3 * Sp));
                break;
        }

        if (duration is 3 or 6 or 12)
        {
            dc.DrawEllipse(Ink, null, new Point(x + 1.2 * Sp, middle - Sp / 2), 1.6, 1.6);
        }
    }

    private static void DrawAccidental(DrawingContext dc, Accidental accidental, double x, double y)
    {
        switch (accidental)
        {
            case Accidental.Sharp:
                DrawGlyph(dc, "♯", new Rect(x - 0.5 * Sp, y - 1.3 * Sp, 1.0 * Sp, 2.6 * Sp));
                break;
            case Accidental.Flat:
                DrawGlyph(dc, "♭", new Rect(x - 0.45 * Sp, y - 1.7 * Sp, 0.9 * Sp, 2.2 * Sp));
                break;
            case Accidental.Natural:
                DrawGlyph(dc, "♮", new Rect(x - 0.4 * Sp, y - 1.3 * Sp, 0.8 * Sp, 2.6 * Sp));
                break;
        }
    }

    private static void DrawTie(DrawingContext dc, double fromX, double toX, double y, bool below)
    {
        if (toX - fromX < 2)
        {
            return;
        }

        var direction = below ? 1 : -1;
        var startY = y + direction * 0.6 * Sp;
        var bulge = direction * Math.Min(1.2 * Sp, 0.25 * (toX - fromX) + 2);
        var figure = new PathFigure { StartPoint = new Point(fromX, startY) };
        figure.Segments.Add(new BezierSegment(new Point(fromX + (toX - fromX) * 0.25, startY + bulge),
            new Point(fromX + (toX - fromX) * 0.75, startY + bulge), new Point(toX, startY), true));
        dc.DrawGeometry(null, TiePen, new PathGeometry([figure]));
    }

    // Draws a symbol of the music font scaled into a box, whatever the font's own metrics.
    private static void DrawGlyph(DrawingContext dc, string glyph, Rect box)
    {
        var geometry = Format(glyph, Music, 40).BuildGeometry(new Point(0, 0));
        var bounds = geometry.Bounds;
        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        var scale = Math.Min(box.Width / bounds.Width, box.Height / bounds.Height);
        var group = new TransformGroup();
        group.Children.Add(new TranslateTransform(-bounds.X, -bounds.Y));
        group.Children.Add(new ScaleTransform(scale, scale));
        group.Children.Add(new TranslateTransform(box.X + (box.Width - bounds.Width * scale) / 2, box.Y + (box.Height - bounds.Height * scale) / 2));
        geometry.Transform = group;
        dc.DrawGeometry(Ink, null, geometry);
    }

    private static FormattedText Format(string text, Typeface typeface, double size) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size, Ink, 1.0);

    private static Size Measure(string text, Typeface typeface, double size)
    {
        var formatted = Format(text, typeface, size);
        return new Size(formatted.WidthIncludingTrailingWhitespace, formatted.Height);
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
