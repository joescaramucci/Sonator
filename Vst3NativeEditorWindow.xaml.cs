using System.Windows;
using System.Windows.Media;
using Sonator.Services.Vst3;
using Sonator.Services;

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
    private readonly IVst3PluginHost _vst3Host;
    private readonly bool _ownsModule;
    private Vst3ModuleHandle? _moduleHandle;
    private Vst3EditorHost? _editorHost;

    public Vst3NativeEditorWindow(IVst3PluginHost vst3Host, Vst3ModuleHandle moduleHandle, string pluginName)
    {
        InitializeComponent();
        WindowFrame.UseDarkFrame(this);
        _vst3Host = vst3Host;
        _moduleHandle = moduleHandle;
        Title = $"Native GUI - {pluginName}";

        AttachEditor();

        Closed += Vst3NativeEditorWindow_Closed;
    }

    public Vst3NativeEditorWindow(string pluginName, string filePath)
    {
        InitializeComponent();
        WindowFrame.UseDarkFrame(this);
        _vst3Host = new Vst3PluginHost();
        _ownsModule = true;
        Title = $"Native GUI - {pluginName}";

        _moduleHandle = _vst3Host.TryLoadModule(filePath, out var loadError);
        if (_moduleHandle is null)
        {
            ShowFallback(loadError ?? "Unable to load the VST3 module.");
        }
        else if (!_vst3Host.HasNativeEditor(_moduleHandle))
        {
            ShowFallback("This plug-in does not expose an embeddable native VST3 editor.");
        }
        else
        {
            if (_vst3Host.HasAudioProcessor(_moduleHandle))
            {
                var selection = AudioOutputSelection.FromSettings(new AppSettingsService().Load());
                if (_vst3Host.TryStartAudio(_moduleHandle, selection, out var audioError))
                {
                    LiveInstrumentRegistry.Set(_moduleHandle);
                }
                else
                {
                    Title = $"Native GUI - {pluginName} (audio unavailable: {audioError})";
                }
            }

            AttachEditor();
        }

        Closed += Vst3NativeEditorWindow_Closed;
    }

    private void AttachEditor()
    {
        if (_moduleHandle is null)
        {
            return;
        }

        _editorHost = new Vst3EditorHost(_vst3Host, _moduleHandle);
        _editorHost.EditorAttached += EditorHost_EditorAttached;
        EditorHostContainer.Content = _editorHost;
    }

    private void ShowFallback(string message)
    {
        EditorHostContainer.Visibility = Visibility.Collapsed;
        FallbackText.Text = message;
        FallbackText.Visibility = Visibility.Visible;
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

        if (_moduleHandle is not null)
        {
            LiveInstrumentRegistry.Clear(_moduleHandle);
        }

        if (_ownsModule && _moduleHandle is not null)
        {
            _vst3Host.StopAudio(_moduleHandle);
            _vst3Host.UnloadModule(_moduleHandle);
            _moduleHandle = null;
        }
    }
}
