using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Sonator;

// Takes a small picture of a window as it is on screen, native content included, such as a plug-in's own
// editor, which WPF cannot draw itself.
internal static class WindowSnapshot
{
    private const uint PrintWindowRenderFullContent = 2; // takes content drawn by the GPU too
    private const int Halftone = 4;

    // A picture of the window's content at most maxWidth pixels wide, or null when it cannot be taken. A
    // plug-in's editor is taken from its own native window: asked of the WPF window around it, Windows gives
    // back a blank picture. A window that is all WPF is drawn by WPF itself.
    public static BitmapSource? Take(Window window, int maxWidth)
    {
        if (window is Vst3NativeEditorWindow { EditorHwnd: var editor } && editor != IntPtr.Zero)
        {
            return TakeNative(editor, maxWidth);
        }

        return window.Content is FrameworkElement { ActualWidth: > 0, ActualHeight: > 0 } content
            ? TakeWpf(content, maxWidth)
            : null;
    }

    private static BitmapSource TakeWpf(FrameworkElement content, int maxWidth)
    {
        var dpi = VisualTreeHelper.GetDpi(content);
        var scale = Math.Min(1.0, maxWidth / (content.ActualWidth * dpi.DpiScaleX));
        var picture = new RenderTargetBitmap(
            Math.Max(1, (int)(content.ActualWidth * dpi.DpiScaleX * scale)),
            Math.Max(1, (int)(content.ActualHeight * dpi.DpiScaleY * scale)),
            96 * dpi.DpiScaleX * scale,
            96 * dpi.DpiScaleY * scale,
            PixelFormats.Pbgra32);
        picture.Render(content);
        picture.Freeze();
        return picture;
    }

    // Asks the window to draw itself; a picture of one colour all over means it could not, and the picture is
    // copied from the screen instead, where the window is in view.
    private static BitmapSource? TakeNative(IntPtr hwnd, int maxWidth)
    {
        var printed = Capture(hwnd, maxWidth, fromScreen: false);
        return printed is not null && !IsBlank(printed) ? printed : Capture(hwnd, maxWidth, fromScreen: true);
    }

    // True when every pixel looked at has the same colour, as in a blank picture.
    public static bool IsBlank(BitmapSource picture)
    {
        var stride = picture.PixelWidth * 4;
        var pixels = new byte[stride * picture.PixelHeight];
        new FormatConvertedBitmap(picture, PixelFormats.Bgra32, null, 0).CopyPixels(pixels, stride, 0);
        var first = BitConverter.ToInt32(pixels, 0);
        for (var offset = 0; offset + 4 <= pixels.Length; offset += 4 * 97)
        {
            if (BitConverter.ToInt32(pixels, offset) != first)
            {
                return false;
            }
        }

        return true;
    }

    private static BitmapSource? Capture(IntPtr hwnd, int maxWidth, bool fromScreen)
    {
        if (!GetWindowRect(hwnd, out var rect))
        {
            return null;
        }

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        var scale = Math.Min(1.0, (double)maxWidth / width);
        var smallWidth = Math.Max(1, (int)(width * scale));
        var smallHeight = Math.Max(1, (int)(height * scale));

        var screen = GetDC(IntPtr.Zero);
        var fullDc = CreateCompatibleDC(screen);
        var smallDc = CreateCompatibleDC(screen);
        var fullBitmap = CreateCompatibleBitmap(screen, width, height);
        var smallBitmap = CreateCompatibleBitmap(screen, smallWidth, smallHeight);
        try
        {
            var oldFull = SelectObject(fullDc, fullBitmap);
            var oldSmall = SelectObject(smallDc, smallBitmap);
            var printed = fromScreen
                ? BitBlt(fullDc, 0, 0, width, height, screen, rect.Left, rect.Top, 0x00CC0020 /* SRCCOPY */)
                : PrintWindow(hwnd, fullDc, PrintWindowRenderFullContent);
            if (printed)
            {
                SetStretchBltMode(smallDc, Halftone);
                StretchBlt(smallDc, 0, 0, smallWidth, smallHeight, fullDc, 0, 0, width, height, 0x00CC0020 /* SRCCOPY */);
            }

            SelectObject(fullDc, oldFull);
            SelectObject(smallDc, oldSmall);
            if (!printed)
            {
                return null;
            }

            var picture = Imaging.CreateBitmapSourceFromHBitmap(smallBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            picture.Freeze();
            return picture;
        }
        finally
        {
            DeleteObject(smallBitmap);
            DeleteObject(fullBitmap);
            DeleteDC(smallDc);
            DeleteDC(fullDc);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr gdiObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr gdiObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern int SetStretchBltMode(IntPtr hdc, int mode);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr destination, int x, int y, int width, int height,
        IntPtr source, int sourceX, int sourceY, uint operation);

    [DllImport("gdi32.dll")]
    private static extern bool StretchBlt(IntPtr destination, int x, int y, int width, int height,
        IntPtr source, int sourceX, int sourceY, int sourceWidth, int sourceHeight, uint operation);
}
