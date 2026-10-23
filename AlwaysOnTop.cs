using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Sonator;

/// <summary>
/// "Always on top" for any Sonator window: an item in its system menu (right-click on the title bar, or
/// Alt+Space), ticked while it is on, and Ctrl+Alt+T. The window then stays in front of every other window,
/// other programs' too, as a mixer or a compressor kept in view. Sonator remembers it per window — the
/// mixer, or a plug-in window by its title, such as "Supercharger — Track 3" — in
/// %AppData%\Sonator\always-on-top.json, so it opens in front again next time.
/// </summary>
internal static class AlwaysOnTop
{
    // A system menu command: below 0xF000, with the low four bits clear as Windows asks.
    private const int CommandId = 0x01A0;
    private const int WmSysCommand = 0x0112;
    private const uint MfString = 0x0000;
    private const uint MfSeparator = 0x0800;
    private const uint MfByCommand = 0x0000;
    private const uint MfChecked = 0x0008;
    private const uint MfUnchecked = 0x0000;

    // Settable for tests.
    internal static string StorePath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sonator", "always-on-top.json");

    private static HashSet<string>? _remembered;

    // Dialogs are watched per thread, as ComponentDispatcher reports them.
    [ThreadStatic]
    private static bool _watchingDialogs;

    [ThreadStatic]
    private static List<Window>? _steppedBack;

    // While a dialog waits for an answer (it disables every other window), the windows kept on top step back
    // so it cannot open hidden behind them, leaving nothing that can be clicked; they come forward again after.
    private static void WatchDialogs()
    {
        if (_watchingDialogs)
        {
            return;
        }

        _watchingDialogs = true;
        _steppedBack = new List<Window>();
        var steppedBack = _steppedBack;
        ComponentDispatcher.EnterThreadModal += (_, _) =>
        {
            foreach (var window in Application.Current?.Windows.OfType<Window>().Where(window => window.Topmost).ToList() ?? new())
            {
                window.Topmost = false;
                steppedBack.Add(window);
            }

            // No longer on top, they would still cover the dialog, as Windows puts a dialog just over the window
            // that owns it: once it is up, the dialog (the one window the others wait for, still enabled) comes
            // to the front.
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                foreach (var window in Application.Current?.Windows.OfType<Window>().ToList() ?? new())
                {
                    var hwnd = new WindowInteropHelper(window).Handle;
                    if (window.IsVisible && hwnd != IntPtr.Zero && IsWindowEnabled(hwnd))
                    {
                        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
                    }
                }
            });
        };
        ComponentDispatcher.LeaveThreadModal += (_, _) =>
        {
            foreach (var window in steppedBack)
            {
                window.Topmost = true;
            }
            steppedBack.Clear();
        };
    }

    public static void Attach(Window window)
    {
        WatchDialogs();
        window.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            var menu = GetSystemMenu(hwnd, false);
            if (menu != IntPtr.Zero)
            {
                AppendMenu(menu, MfSeparator, 0, null);
                AppendMenu(menu, MfString, CommandId, "Always on top\tCtrl+Alt+T");
            }

            HwndSource.FromHwnd(hwnd)?.AddHook((IntPtr _, int message, IntPtr wParam, IntPtr _, ref bool handled) =>
            {
                if (message == WmSysCommand && (wParam.ToInt64() & 0xFFF0) == CommandId)
                {
                    Set(window, !window.Topmost, remember: true);
                    handled = true;
                }
                return IntPtr.Zero;
            });

            if (Remembered().Contains(KeyOf(window)))
            {
                Set(window, true, remember: false);
            }
        };

        window.PreviewKeyDown += (_, e) =>
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.T && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Alt))
            {
                Set(window, !window.Topmost, remember: true);
                e.Handled = true;
            }
        };
    }

    private static void Set(Window window, bool onTop, bool remember)
    {
        window.Topmost = onTop;
        var menu = GetSystemMenu(new WindowInteropHelper(window).Handle, false);
        if (menu != IntPtr.Zero)
        {
            CheckMenuItem(menu, CommandId, MfByCommand | (onTop ? MfChecked : MfUnchecked));
        }

        if (remember)
        {
            var keys = Remembered();
            if (onTop ? keys.Add(KeyOf(window)) : keys.Remove(KeyOf(window)))
            {
                Save(keys);
            }
        }
    }

    // Plug-in windows and FX racks are told apart by their titles (the plug-in or track they show); other
    // windows by their kind.
    internal static string KeyOf(Window window) =>
        window is Vst3NativeEditorWindow or EffectRackWindow ? $"{window.GetType().Name}|{window.Title}" : window.GetType().Name;

    // Read once from StorePath (again after Forget, for tests).
    internal static void Forget() => _remembered = null;

    private static HashSet<string> Remembered()
    {
        if (_remembered is not null)
        {
            return _remembered;
        }

        try
        {
            _remembered = File.Exists(StorePath)
                ? JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(StorePath)) ?? new()
                : new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _remembered = new();
        }
        return _remembered;
    }

    private static void Save(HashSet<string> keys)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(keys.OrderBy(key => key)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Only a convenience: the window stays on top now, it just will not open on top next time.
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetSystemMenu(IntPtr hwnd, bool revert);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, int id, string? text);

    [DllImport("user32.dll")]
    private static extern uint CheckMenuItem(IntPtr menu, int id, uint check);

    [DllImport("user32.dll")]
    private static extern bool IsWindowEnabled(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
