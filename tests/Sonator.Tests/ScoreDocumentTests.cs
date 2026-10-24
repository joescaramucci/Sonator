using System.Windows.Media;
using Sonator.Services.Score;
using Xunit;

namespace Sonator.Tests;

public sealed class ScoreDocumentTests
{
    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException(failure.ToString());
        }
    }

    [Fact]
    public void ALongSong_FillsSeveralPages_AndEachDraws()
    {
        OnSta(() =>
        {
            // Sixty-four bars of eighths, sixteenths, a dotted note, ties, a chord and rests, with words.
            var notes = new List<(double, double, int)>();
            for (var bar = 0; bar < 64; bar++)
            {
                var start = bar * 16;
                notes.Add((start, 2, 60 + bar % 12));
                notes.Add((start + 2, 2, 64));
                notes.Add((start + 4, 1, 67));
                notes.Add((start + 5, 1, 69));
                notes.Add((start + 6, 3, 71));
                notes.Add((start + 9, 1, 72));
                notes.Add((start + 12, 6, 62));
                notes.Add((start + 12, 6, 66));
            }

            var sheet = ScoreLyrics.Apply(ScoreBuilder.Build(notes, 4, 4, 64 * 16), string.Join(" ", Enumerable.Repeat("la-la la", 200)));
            var document = new ScoreDocument(sheet, "Test", "Me", 96);

            Assert.True(document.PageCount > 1);
            for (var page = 0; page < document.PageCount; page++)
            {
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    document.DrawPage(dc, page);
                }

                Assert.False(visual.ContentBounds.IsEmpty);
            }
        });
    }

    [Fact]
    public void ABassPart_InAFlatKey_Draws()
    {
        OnSta(() =>
        {
            var sheet = ScoreBuilder.Build([(0, 4, 41), (4, 4, 46), (8, 8, 39), (16, 16, 34)], 3, 4, 48);
            var document = new ScoreDocument(sheet, "Bass", string.Empty, 120);

            Assert.Equal(Clef.Bass, sheet.Clef);
            Assert.Equal(1, document.PageCount);
        });
    }
}
