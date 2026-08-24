using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Sonator.ViewModels;

namespace Sonator.Services.Vst3;

public sealed class Vst3ThumbnailService
{
    private const int ThumbnailWidth = 160;
    private const int ThumbnailHeight = 100;
    private static readonly SemaphoreSlim CaptureSlots = new(3);
    private readonly string _cacheDirectory;

    public Vst3ThumbnailService()
    {
        _cacheDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Sonator",
            "VstThumbnails");
    }

    public async Task<ImageSource> GetOrCreateAsync(VstPluginViewModel plugin)
    {
        Directory.CreateDirectory(_cacheDirectory);
        var thumbnailPath = GetCachePath(plugin);
        if (!File.Exists(thumbnailPath))
        {
            var captured = false;
            if (string.Equals(plugin.Kind, "VST3", StringComparison.OrdinalIgnoreCase))
            {
                await CaptureSlots.WaitAsync();
                try
                {
                    captured = await TryGenerateNativeThumbnailAsync(plugin.FilePath, thumbnailPath);
                }
                finally
                {
                    CaptureSlots.Release();
                }
            }

            if (!captured)
            {
                GenerateThumbnail(plugin.Name, plugin.Badge, thumbnailPath);
            }
        }

        using var stream = new FileStream(thumbnailPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private string GetCachePath(VstPluginViewModel plugin)
    {
        var lastWrite = Directory.Exists(plugin.FilePath)
            ? Directory.GetLastWriteTimeUtc(plugin.FilePath)
            : File.GetLastWriteTimeUtc(plugin.FilePath);
        var identity = $"isolated-v2|{plugin.FilePath}|{lastWrite.Ticks}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return Path.Combine(_cacheDirectory, $"{hash}.png");
    }

    private static async Task<bool> TryGenerateNativeThumbnailAsync(string pluginPath, string thumbnailPath)
    {
        var helperPath = Path.Combine(
            AppContext.BaseDirectory,
            "VstThumbnailHost",
            "Sonator.VstThumbnailHost.exe");
        if (!File.Exists(helperPath))
        {
            return false;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = helperPath,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(pluginPath);
        startInfo.ArgumentList.Add(thumbnailPath);

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Win32Exception)
        {
            return false;
        }

        if (process is null)
        {
            return false;
        }

        using (process)
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }

                return false;
            }

            return process.ExitCode == 0 && File.Exists(thumbnailPath);
        }
    }

    private static void GenerateThumbnail(string name, string badge, string destinationPath)
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(
                new LinearGradientBrush(Color.FromRgb(31, 42, 57), Color.FromRgb(13, 18, 27), 35),
                null,
                new Rect(0, 0, ThumbnailWidth, ThumbnailHeight));
            drawing.DrawRoundedRectangle(
                new SolidColorBrush(Color.FromRgb(45, 62, 82)),
                new Pen(new SolidColorBrush(Color.FromRgb(89, 180, 255)), 2),
                new Rect(56, 10, 48, 48),
                8,
                8);

            var badgeText = CreateText(badge, 18, FontWeights.Bold, Brushes.White);
            drawing.DrawText(badgeText, new Point((ThumbnailWidth - badgeText.Width) / 2, 22));

            var displayName = name.Length > 24 ? $"{name[..21]}..." : name;
            var nameText = CreateText(displayName, 12, FontWeights.SemiBold, Brushes.White);
            drawing.DrawText(nameText, new Point((ThumbnailWidth - nameText.Width) / 2, 70));
        }

        var bitmap = new RenderTargetBitmap(
            ThumbnailWidth,
            ThumbnailHeight,
            96,
            96,
            PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
        encoder.Save(stream);
    }

    private static FormattedText CreateText(string text, double size, FontWeight weight, Brush brush)
    {
        return new FormattedText(
            text,
            System.Globalization.CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, weight, FontStretches.Normal),
            size,
            brush,
            1);
    }
}
