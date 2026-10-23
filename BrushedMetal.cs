using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Sonator;

/// <summary>
/// A satin brushed-aluminium texture, as on a mixing console's channel strips: fine streaks running down
/// the panel, each a touch lighter or darker, made once and tiled.
/// </summary>
public static class BrushedMetal
{
    private const int TileWidth = 256;
    private const int TileHeight = 256;

    private static ImageBrush? _brush;

    public static ImageBrush Brush => _brush ??= Make();

    private static ImageBrush Make()
    {
        var random = new Random(4000); // the same streaks every time
        var pixels = new byte[TileWidth * TileHeight * 4];

        // Each column has its own shade, smoothed so streaks are a few pixels wide, and the shade drifts a
        // little down the column, as brushing leaves it.
        var columns = new double[TileWidth];
        for (var x = 0; x < TileWidth; x++)
        {
            columns[x] = random.NextDouble() * 2 - 1;
        }
        var smoothed = new double[TileWidth];
        for (var x = 0; x < TileWidth; x++)
        {
            smoothed[x] = (columns[(x + TileWidth - 1) % TileWidth] + 2 * columns[x] + columns[(x + 1) % TileWidth]) / 4;
        }

        for (var y = 0; y < TileHeight; y++)
        {
            for (var x = 0; x < TileWidth; x++)
            {
                var drift = Math.Sin((y + x * 7) * 2 * Math.PI / TileHeight) * 0.15;
                var shade = 0.72 + smoothed[x] * 0.045 + drift * 0.03 + (random.NextDouble() - 0.5) * 0.02;
                var value = (byte)Math.Clamp(shade * 255, 0, 255);
                var index = (y * TileWidth + x) * 4;
                pixels[index] = (byte)Math.Clamp(value + 4, 0, 255); // a cool, slightly blue aluminium
                pixels[index + 1] = (byte)Math.Clamp(value + 1, 0, 255);
                pixels[index + 2] = value;
                pixels[index + 3] = 255;
            }
        }

        var bitmap = BitmapSource.Create(TileWidth, TileHeight, 96, 96, PixelFormats.Bgra32, null, pixels, TileWidth * 4);
        bitmap.Freeze();
        var brush = new ImageBrush(bitmap)
        {
            TileMode = TileMode.Tile,
            ViewportUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, TileWidth, TileHeight),
            Stretch = Stretch.None,
        };
        brush.Freeze();
        return brush;
    }
}
