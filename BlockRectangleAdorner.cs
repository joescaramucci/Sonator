using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace Sonator;

// The rectangle drawn on the timeline's lanes to select the blocks it touches.
public sealed class BlockRectangleAdorner : Adorner
{
    private static readonly Brush Fill = Frozen(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xD2, 0x4C)));
    private static readonly Pen Edge = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0xD2, 0x4C)), 1) { DashStyle = DashStyles.Dash });

    private Rect _area;

    public BlockRectangleAdorner(UIElement lanes) : base(lanes)
    {
        IsHitTestVisible = false;
    }

    public void Show(Rect area)
    {
        _area = area;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext) => drawingContext.DrawRectangle(Fill, Edge, _area);

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
