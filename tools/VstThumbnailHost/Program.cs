using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Sonator;
using Sonator.Services.Vst3;

namespace Sonator.VstThumbnailHost;

internal static class Program
{
    private const int ThumbnailWidth = 160;
    private const int ThumbnailHeight = 100;

    [STAThread]
    private static int Main(string[] args)
    {
        NativeMethods.SetErrorMode(0x0001 | 0x0002);

        if (args.Length == 2 && args[0] == "--check")
        {
            return Check(args[1]);
        }

        if (args.Length != 2)
        {
            return 1;
        }

        var application = new Application
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown,
        };
        var exitCode = 2;
        application.Startup += async (_, _) =>
        {
            exitCode = await CaptureAsync(args[0], args[1]);
            application.Shutdown(exitCode);
        };
        application.Run();
        return exitCode;
    }

    // --check: whether Sonator can use the plug-in. 0 when it loads, makes its component and controller
    // and has an audio processor; 3 when it does not load; 7 when it has nothing to play through.
    // A plug-in that crashes or hangs takes down only this process, which Sonator reads as not working.
    private static int Check(string pluginPath)
    {
        var pluginHost = new Vst3PluginHost();
        var module = pluginHost.TryLoadModule(pluginPath, out var error);
        if (module is null)
        {
            // The reason goes to standard output, for whoever runs the check by hand.
            Console.Out.WriteLine(error);
            return 3;
        }

        try
        {
            return pluginHost.HasAudioProcessor(module) ? 0 : 7;
        }
        finally
        {
            pluginHost.UnloadModule(module);
        }
    }

    private static async Task<int> CaptureAsync(string pluginPath, string destinationPath)
    {
        var pluginHost = new Vst3PluginHost();
        var module = pluginHost.TryLoadModule(pluginPath, out _);
        if (module is null)
        {
            return 3;
        }

        Vst3EditorHost? editorHost = null;
        Window? captureWindow = null;
        try
        {
            if (!pluginHost.HasNativeEditor(module))
            {
                return 4;
            }

            var attached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            editorHost = new Vst3EditorHost(pluginHost, module);
            editorHost.EditorAttached += (_, _) => attached.TrySetResult();
            captureWindow = new Window
            {
                Content = editorHost,
                Width = 640,
                Height = 480,
                Left = 0,
                Top = 0,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowActivated = false,
                ShowInTaskbar = false,
            };
            captureWindow.Show();
            var captureHwnd = new WindowInteropHelper(captureWindow).Handle;
            NativeMethods.SetWindowPos(
                captureHwnd,
                new IntPtr(1),
                0,
                0,
                0,
                0,
                0x0001 | 0x0002 | 0x0010);

            var completion = await Task.WhenAny(attached.Task, Task.Delay(TimeSpan.FromSeconds(4)));
            if (completion != attached.Task || editorHost.NativeHandle == IntPtr.Zero)
            {
                return 5;
            }

            captureWindow.Width = Math.Max(1, editorHost.EditorWidth);
            captureWindow.Height = Math.Max(1, editorHost.EditorHeight);
            await Task.Delay(700);
            return TrySaveEditor(editorHost.NativeHandle, destinationPath) ? 0 : 6;
        }
        finally
        {
            if (captureWindow is not null)
            {
                captureWindow.Content = null;
                captureWindow.Close();
            }

            editorHost?.Dispose();
            pluginHost.UnloadModule(module);
        }
    }

    private static bool TrySaveEditor(IntPtr root, string destinationPath)
    {
        foreach (var window in EnumerateWindows(root))
        {
            var snapshot = TryCaptureWindow(window);
            if (snapshot is null || IsVisuallyEmpty(snapshot))
            {
                continue;
            }

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(snapshot));
            using var stream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
            encoder.Save(stream);
            return true;
        }

        return false;
    }

    private static IReadOnlyList<IntPtr> EnumerateWindows(IntPtr root)
    {
        var windows = new List<(IntPtr Handle, long Area)>();
        NativeMethods.EnumChildWindows(root, (child, _) =>
        {
            Add(child);
            return true;
        }, IntPtr.Zero);
        var ordered = windows
            .OrderByDescending(item => item.Area)
            .Select(item => item.Handle)
            .ToList();
        ordered.Add(root);
        return ordered;

        void Add(IntPtr handle)
        {
            if (!NativeMethods.GetClientRect(handle, out var bounds))
            {
                return;
            }

            var width = bounds.Right - bounds.Left;
            var height = bounds.Bottom - bounds.Top;
            if (width > 1 && height > 1)
            {
                windows.Add((handle, (long)width * height));
            }
        }
    }

    private static BitmapSource? TryCaptureWindow(IntPtr hwnd)
    {
        if (!NativeMethods.GetClientRect(hwnd, out var bounds))
        {
            return null;
        }

        var width = bounds.Right - bounds.Left;
        var height = bounds.Bottom - bounds.Top;
        var windowDc = NativeMethods.GetDC(hwnd);
        if (windowDc == IntPtr.Zero)
        {
            return null;
        }

        var memoryDc = NativeMethods.CreateCompatibleDC(windowDc);
        var bitmap = NativeMethods.CreateCompatibleBitmap(windowDc, width, height);
        var previous = NativeMethods.SelectObject(memoryDc, bitmap);
        try
        {
            if (!NativeMethods.PrintWindow(hwnd, memoryDc, 2))
            {
                return null;
            }

            var source = Imaging.CreateBitmapSourceFromHBitmap(
                bitmap,
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            return FitToThumbnail(source);
        }
        finally
        {
            NativeMethods.SelectObject(memoryDc, previous);
            NativeMethods.DeleteObject(bitmap);
            NativeMethods.DeleteDC(memoryDc);
            NativeMethods.ReleaseDC(hwnd, windowDc);
        }
    }

    private static BitmapSource FitToThumbnail(BitmapSource source)
    {
        source = CropUniformBorder(source);
        var scale = Math.Min(
            (double)ThumbnailWidth / source.PixelWidth,
            (double)ThumbnailHeight / source.PixelHeight);
        var width = source.PixelWidth * scale;
        var height = source.PixelHeight * scale;
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.Black, null, new Rect(0, 0, ThumbnailWidth, ThumbnailHeight));
            drawing.DrawImage(
                source,
                new Rect(
                    (ThumbnailWidth - width) / 2,
                    (ThumbnailHeight - height) / 2,
                    width,
                    height));
        }

        var thumbnail = new RenderTargetBitmap(
            ThumbnailWidth,
            ThumbnailHeight,
            96,
            96,
            PixelFormats.Pbgra32);
        thumbnail.Render(visual);
        thumbnail.Freeze();
        return thumbnail;
    }

    private static BitmapSource CropUniformBorder(BitmapSource source)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        var backgroundIndex = pixels.Length - 4;
        var backgroundBlue = pixels[backgroundIndex];
        var backgroundGreen = pixels[backgroundIndex + 1];
        var backgroundRed = pixels[backgroundIndex + 2];
        var minX = converted.PixelWidth;
        var minY = converted.PixelHeight;
        var maxX = -1;
        var maxY = -1;

        for (var y = 0; y < converted.PixelHeight; y++)
        {
            for (var x = 0; x < converted.PixelWidth; x++)
            {
                var index = (y * stride) + (x * 4);
                if (Math.Abs(pixels[index] - backgroundBlue) <= 4
                    && Math.Abs(pixels[index + 1] - backgroundGreen) <= 4
                    && Math.Abs(pixels[index + 2] - backgroundRed) <= 4)
                {
                    continue;
                }

                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }
        }

        if (maxX < minX || maxY < minY)
        {
            return source;
        }

        var cropped = new CroppedBitmap(
            converted,
            new Int32Rect(minX, minY, maxX - minX + 1, maxY - minY + 1));
        cropped.Freeze();
        return cropped;
    }

    private static bool IsVisuallyEmpty(BitmapSource source)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        var firstBlue = pixels[0];
        var firstGreen = pixels[1];
        var firstRed = pixels[2];

        for (var index = 4; index < pixels.Length; index += 4)
        {
            if (Math.Abs(pixels[index] - firstBlue) > 4
                || Math.Abs(pixels[index + 1] - firstGreen) > 4
                || Math.Abs(pixels[index + 2] - firstRed) > 4)
            {
                return false;
            }
        }

        return true;
    }

    private static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr parameter);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetClientRect(IntPtr hwnd, out Rect rect);

        [DllImport("user32.dll")]
        public static extern IntPtr GetDC(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PrintWindow(IntPtr hwnd, IntPtr dc, uint flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr parameter);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowPos(
            IntPtr hwnd,
            IntPtr insertAfter,
            int x,
            int y,
            int width,
            int height,
            uint flags);

        [DllImport("kernel32.dll")]
        public static extern uint SetErrorMode(uint mode);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateCompatibleDC(IntPtr dc);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);

        [DllImport("gdi32.dll")]
        public static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeleteObject(IntPtr obj);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeleteDC(IntPtr dc);
    }
}
