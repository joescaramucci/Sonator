using System.Runtime.InteropServices;
using System.Windows.Interop;
using Sonator.Services.Vst3;

namespace Sonator;

/// <summary>
/// WPF <see cref="HwndHost"/> that embeds a loaded VST3 plugin's native
/// editor (Steinberg IPlugView) as a child HWND. The actual view creation
/// and IPlugView::attached()/removed() calls happen in the native
/// VstHostBridge.dll (see native/VstHostBridge/src/VstHostBridge.cpp); this
/// class only wires that up to WPF's Win32 interop layer.
/// </summary>
public sealed class Vst3EditorHost : HwndHost
{
    private readonly IVst3PluginHost _vst3Host;
    private readonly Vst3ModuleHandle _moduleHandle;

    public Vst3EditorHost(IVst3PluginHost vst3Host, Vst3ModuleHandle moduleHandle)
    {
        _vst3Host = vst3Host;
        _moduleHandle = moduleHandle;
    }

    /// <summary>
    /// Set by <see cref="BuildWindowCore"/> once the editor is attached, in
    /// pixels as reported by the plugin. Used by the host window to size
    /// itself to the plugin's native (usually fixed) editor size.
    /// </summary>
    public int EditorWidth { get; private set; }

    /// <summary>
    /// Set by <see cref="BuildWindowCore"/> once the editor is attached, in
    /// pixels as reported by the plugin.
    /// </summary>
    public int EditorHeight { get; private set; }

    public IntPtr NativeHandle { get; private set; }

    /// <summary>
    /// Raised right after the native editor view is successfully attached,
    /// so the containing window can resize itself to fit.
    /// </summary>
    public event EventHandler? EditorAttached;

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        var parentHwnd = hwndParent.Handle;
        var attached = _vst3Host.TryOpenNativeEditor(_moduleHandle, parentHwnd, out var childHwnd, out var width, out var height, out var error);
        if (!attached || childHwnd == IntPtr.Zero)
        {
            // No native editor available: return an invisible placeholder
            // child window so WPF's HwndHost machinery has something valid
            // to work with, instead of throwing and tearing down the dialog.
            var placeholder = CreatePlaceholderWindow(parentHwnd);
            return new HandleRef(this, placeholder);
        }

        EditorWidth = width;
        EditorHeight = height;
        NativeHandle = childHwnd;
        EditorAttached?.Invoke(this, EventArgs.Empty);
        return new HandleRef(this, childHwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        NativeHandle = IntPtr.Zero;
        _vst3Host.CloseNativeEditor(_moduleHandle);
    }

    private static IntPtr CreatePlaceholderWindow(IntPtr parentHwnd)
    {
        const int wsChild = 0x40000000;
        return NativeMethods.CreateWindowEx(0, "STATIC", string.Empty, wsChild, 0, 0, 1, 1, parentHwnd, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateWindowEx(
            int exStyle, string className, string windowName, int style,
            int x, int y, int width, int height,
            IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    }
}
