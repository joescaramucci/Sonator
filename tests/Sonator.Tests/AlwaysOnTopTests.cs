using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Xunit;

namespace Sonator.Tests;

public sealed class AlwaysOnTopTests
{
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetSystemMenu(IntPtr hwnd, bool revert);

    [DllImport("user32.dll")]
    private static extern uint GetMenuState(IntPtr menu, int id, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint command);

    // True when first is in front of second: second comes after it, going down the windows from the top.
    private static bool IsInFront(IntPtr first, IntPtr second)
    {
        for (var hwnd = GetWindow(first, 2); hwnd != IntPtr.Zero; hwnd = GetWindow(hwnd, 2)) // GW_HWNDNEXT
        {
            if (hwnd == second)
            {
                return true;
            }
        }
        return false;
    }

    [Fact]
    public void Dialog_OpensInFrontOfAWindowKeptOnTop_WhichIsOnTopAgainAfter()
    {
        var store = Path.Combine(Path.GetTempPath(), $"sonator-on-top-{Guid.NewGuid():N}.json");
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                AlwaysOnTop.StorePath = store;
                AlwaysOnTop.Forget();
                var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

                Window Make(Window? owner)
                {
                    var window = new Window { Left = -20000, Top = -20000, Width = 300, Height = 200, ShowActivated = false, ShowInTaskbar = false, Owner = owner };
                    WindowFrame.UseDarkFrame(window);
                    return window;
                }

                var main = Make(null);
                main.Show();
                var mixer = Make(main);
                mixer.Show();
                var mixerHwnd = new WindowInteropHelper(mixer).Handle;
                SendMessage(mixerHwnd, 0x0112, new IntPtr(0x01A0), IntPtr.Zero); // always on top
                Assert.True(mixer.Topmost);

                var dialog = Make(main);
                var dialogWasInFront = false;
                var mixerWasOnTopMeanwhile = true;
                var order = string.Empty;
                dialog.Loaded += (_, _) => dialog.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
                {
                    var dialogHwnd = new WindowInteropHelper(dialog).Handle;
                    var mainHwnd = new WindowInteropHelper(main).Handle;
                    var index = 0;
                    for (var hwnd = GetWindow(dialogHwnd, 0); hwnd != IntPtr.Zero; hwnd = GetWindow(hwnd, 2), index++) // from GW_HWNDFIRST
                    {
                        if (hwnd == dialogHwnd) order += $" dialog@{index}";
                        if (hwnd == mixerHwnd) order += $" mixer@{index}";
                        if (hwnd == mainHwnd) order += $" main@{index}";
                    }
                    dialogWasInFront = IsInFront(dialogHwnd, mixerHwnd);
                    mixerWasOnTopMeanwhile = mixer.Topmost;
                    dialog.Close();
                });
                dialog.ShowDialog();

                Assert.True(dialogWasInFront, order);
                Assert.False(mixerWasOnTopMeanwhile);
                Assert.True(mixer.Topmost); // back on top once the dialog has gone
                mixer.Close();
                main.Close();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        File.Delete(store);
        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException(failure.ToString());
        }
    }

    [Fact]
    public void SystemMenuItem_PutsTheWindowOnTop_AndItOpensOnTopNextTime()
    {
        var store = Path.Combine(Path.GetTempPath(), $"sonator-on-top-{Guid.NewGuid():N}.json");
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                AlwaysOnTop.StorePath = store;
                AlwaysOnTop.Forget();

                Window Open()
                {
                    var window = new Window { Left = -20000, Top = -20000, Width = 200, Height = 100, ShowActivated = false, ShowInTaskbar = false };
                    WindowFrame.UseDarkFrame(window);
                    window.Show();
                    return window;
                }

                var first = Open();
                Assert.False(first.Topmost);
                var hwnd = new WindowInteropHelper(first).Handle;
                SendMessage(hwnd, 0x0112, new IntPtr(0x01A0), IntPtr.Zero); // the "Always on top" item
                Assert.True(first.Topmost);
                Assert.Equal(0x0008u, GetMenuState(GetSystemMenu(hwnd, false), 0x01A0, 0) & 0x0008u); // ticked
                first.Close();

                AlwaysOnTop.Forget(); // as when Sonator starts again
                var second = Open();
                Assert.True(second.Topmost);
                SendMessage(new WindowInteropHelper(second).Handle, 0x0112, new IntPtr(0x01A0), IntPtr.Zero);
                Assert.False(second.Topmost);
                second.Close();

                AlwaysOnTop.Forget();
                var third = Open();
                Assert.False(third.Topmost);
                third.Close();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        File.Delete(store);
        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException(failure.ToString());
        }
    }
}
