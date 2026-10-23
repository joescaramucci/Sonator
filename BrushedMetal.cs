using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Sonator;

/// <summary>
/// A satin brushed-metal texture in anthracite, as on an SSL desk's channel strips: fine streaks running
/// down the panel, each a touch lighter or darker, with a few brighter scratches, made once and tiled.
/// </summary>
public static class BrushedMetal
{
    private const int TileWidth = 256;
    private const int TileHeight = 256;

    private static ImageBrush? _brush;

    // The mixer's modules: SSL's smooth charcoal, with only the faintest grain.
    public static ImageBrush Brush => _brush ??= Make(0.19, 0, 0.12);

    private static ImageBrush? _light;

    // The light grey satin aluminium of SSL's plug-in panels, for the FX racks.
    public static ImageBrush Light => _light ??= Make(0.76, 3, 0.6);

    // base: the metal's shade from 0 to 1; tint: how much bluer than grey it is; roughness: how strong its
    // streaks are, 1 for brushed metal, near 0 for a smooth painted panel.
    private static ImageBrush Make(double baseShade, int tint, double roughness)
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

        // Here and there a brighter scratch catches the light.
        var scratches = new double[TileWidth];
        for (var i = 0; i < 18; i++)
        {
            scratches[random.Next(TileWidth)] = 0.02 + random.NextDouble() * 0.035;
        }

        for (var y = 0; y < TileHeight; y++)
        {
            for (var x = 0; x < TileWidth; x++)
            {
                var drift = Math.Sin((y + x * 7) * 2 * Math.PI / TileHeight);
                var shade = baseShade + (smoothed[x] * 0.055 + scratches[x] * 0.6 + drift * 0.008) * roughness + (random.NextDouble() - 0.5) * 0.03 * Math.Max(0.25, roughness);
                var value = (byte)Math.Clamp(shade * 255, 0, 255);
                var index = (y * TileWidth + x) * 4;
                pixels[index] = (byte)Math.Clamp(value + tint, 0, 255); // a touch blue, as SSL's metal
                pixels[index + 1] = (byte)Math.Clamp(value + tint / 2, 0, 255);
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
