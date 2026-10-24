using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace Sonator;

// One page of a score on screen, as it prints.
public sealed class ScorePageView : FrameworkElement
{
    private readonly ScoreDocument _document;
    private readonly int _page;

    public ScorePageView(ScoreDocument document, int page)
    {
        _document = document;
        _page = page;
        Width = ScoreDocument.PageWidth;
        Height = ScoreDocument.PageHeight;
    }

    protected override void OnRender(DrawingContext drawingContext) => _document.DrawPage(drawingContext, _page);
}

// The pages of a score, for the printer (or for Microsoft Print to PDF).
public sealed class ScorePaginator : DocumentPaginator
{
    private readonly ScoreDocument _document;

    public ScorePaginator(ScoreDocument document)
    {
        _document = document;
    }

    public override DocumentPage GetPage(int pageNumber)
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            _document.DrawPage(drawing, pageNumber);
        }

        var size = new Size(ScoreDocument.PageWidth, ScoreDocument.PageHeight);
        return new DocumentPage(visual, size, new Rect(size), new Rect(size));
    }

    public override bool IsPageCountValid => true;

    public override int PageCount => _document.PageCount;

    public override Size PageSize
    {
        get => new(ScoreDocument.PageWidth, ScoreDocument.PageHeight);
        set { }
    }

    public override IDocumentPaginatorSource? Source => null;
}
