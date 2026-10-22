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

        Closing += ReturnFocusToOwner;
        Closed += Vst3NativeEditorWindow_Closed;
    }

    // The plug-in's own child windows hold the focus, and when they go Windows hands it to whatever window
    // is next, often another program. Activating the owner first keeps the focus in Sonator.
    private void ReturnFocusToOwner(object? sender, System.ComponentModel.CancelEventArgs e) => Owner?.Activate();

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

        Closing += ReturnFocusToOwner;
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

            FitTo(_editorHost.EditorWidth, _editorHost.EditorHeight);

            // Some plug-ins (Kontakt, for one) grow their editor after it opens: the window follows.
            _resizeWatch.Tick += (_, _) =>
            {
                if (_editorHost?.TryGetCurrentSize(out var width, out var height) == true
                    && (width != _fittedWidth || height != _fittedHeight))
                {
                    FitTo(width, height);
                }
            };
            _resizeWatch.Start();
        });
    }

    private readonly System.Windows.Threading.DispatcherTimer _resizeWatch = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private int _fittedWidth;
    private int _fittedHeight;

    // Sizes the window around the editor, given in device pixels.
    private void FitTo(int width, int height)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        EditorHostContainer.Width = width / dpi.DpiScaleX;
        EditorHostContainer.Height = height / dpi.DpiScaleY;
        SizeToContent = SizeToContent.WidthAndHeight;
        (_fittedWidth, _fittedHeight) = (width, height);
    }

    private void Vst3NativeEditorWindow_Closed(object? sender, EventArgs e)
    {
        _resizeWatch.Stop();
        if (_editorHost is not null)
        {
            EditorHostContainer.Content = null;
            _editorHost.Dispose();
            _editorHost = null;
        }

        // A plug-in this window did not load (a track's, for one) keeps playing, and the keyboard with it.
        if (_ownsModule && _moduleHandle is not null)
        {
            LiveInstrumentRegistry.Clear(_moduleHandle);
            _vst3Host.StopAudio(_moduleHandle);
            _vst3Host.UnloadModule(_moduleHandle);
            _moduleHandle = null;
        }
    }
}
