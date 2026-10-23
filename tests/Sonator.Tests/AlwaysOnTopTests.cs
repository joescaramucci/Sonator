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
