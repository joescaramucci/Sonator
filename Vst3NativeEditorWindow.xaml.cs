using System.Windows;
using System.Windows.Media;
using Sonator.Services.Vst3;

namespace Sonator;

/// <summary>
/// Small standalone window that hosts a VST3 plugin's native editor
/// (Steinberg IPlugView, embedded via <see cref="Vst3EditorHost"/>). Kept
/// separate from <see cref="VstPluginEditorDialog"/> (which shows the real
/// parameter list) since the plugin's own GUI usually has its own fixed
/// size/skin unrelated to Sonator's parameter list layout.
/// </summary>
public partial class Vst3NativeEditorWindow : Window
{
    private Vst3EditorHost? _editorHost;

    public Vst3NativeEditorWindow(IVst3PluginHost vst3Host, Vst3ModuleHandle moduleHandle, string pluginName)
    {
        InitializeComponent();
        Title = $"GUI nativa - {pluginName}";

        _editorHost = new Vst3EditorHost(vst3Host, moduleHandle);
        _editorHost.EditorAttached += EditorHost_EditorAttached;
        EditorHostContainer.Content = _editorHost;

        Closed += Vst3NativeEditorWindow_Closed;
    }

    private void EditorHost_EditorAttached(object? sender, EventArgs e)
    {
        if (_editorHost is null)
        {
            return;
        }

        // The plugin reports its size in real device pixels. WPF sizes
        // elements in device-independent units (1 DIP = 1/96 inch), so on a
        // scaled display (e.g. 125%/150%) we must divide by the current DPI
        // scale factor - otherwise the window ends up too small and WPF
        // shrink-fits the embedded native content into it (the "80% zoom"
        // look). Doing this makes the plugin's editor render at its real,
        // full native size.
        Dispatcher.BeginInvoke(() =>
        {
            if (_editorHost.EditorWidth <= 0 || _editorHost.EditorHeight <= 0)
            {
                return;
            }

            var dpi = VisualTreeHelper.GetDpi(this);
            var contentWidth = _editorHost.EditorWidth / dpi.DpiScaleX;
            var contentHeight = _editorHost.EditorHeight / dpi.DpiScaleY;

            EditorHostContainer.Width = contentWidth;
            EditorHostContainer.Height = contentHeight;
            SizeToContent = SizeToContent.WidthAndHeight;
        });
    }

    private void Vst3NativeEditorWindow_Closed(object? sender, EventArgs e)
    {
        if (_editorHost is not null)
        {
            EditorHostContainer.Content = null;
            _editorHost.Dispose();
            _editorHost = null;
        }
    }
}
