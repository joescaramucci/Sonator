using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Sonator;

/// <summary>
/// Gives a window's title bar and border Sonator's dark colours instead of
/// the white Windows default. Windows 10 shows its dark frame; Windows 11
/// also takes the exact caption, border and title colours below. Every Sonator
/// window calls this, so it also gives the window its "Always on top" option.
/// </summary>
internal static class WindowFrame
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    public static void UseDarkFrame(Window window)
    {
        window.SourceInitialized += (_, _) => Apply(new WindowInteropHelper(window).Handle);
        AlwaysOnTop.Attach(window);
    }

    private static void Apply(IntPtr hwnd)
    {
        var darkMode = 1;
        DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref darkMode, sizeof(int));

        var caption = ColorRef(0x2C, 0x3B, 0x4D);
        var border = ColorRef(0x48, 0x5B, 0x72);
        var text = ColorRef(0xC4, 0xCF, 0xDA);
        DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref caption, sizeof(int));
        DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref border, sizeof(int));
        DwmSetWindowAttribute(hwnd, DwmwaTextColor, ref text, sizeof(int));
    }

    // COLORREF is 0x00BBGGRR.
    private static int ColorRef(byte red, byte green, byte blue) => red | (green << 8) | (blue << 16);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
