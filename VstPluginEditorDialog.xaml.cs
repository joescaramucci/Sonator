using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Sonator.Services.Vst3;
using Sonator.Services;

namespace Sonator;

public partial class VstPluginEditorDialog : Window
{
    // Showing all parameters of a large synth (some report 1000+, e.g.
    // TAL-NoiseMaker exposes 2169 including per-MIDI-CC automation lanes)
    // would make the dialog unusably slow/long, so only the first N are
    // rendered. The rest remain fully readable/settable via
    // IVst3PluginHost.GetParameters/SetParameterValue for other UI.
    private const int MaxParametersShown = 200;

    private readonly IVst3PluginHost _vst3Host;
    private readonly string _filePath;
    private Vst3ModuleHandle? _moduleHandle;

    public VstPluginEditorDialog(string name, string filePath, string kind, IVst3PluginHost? vst3Host = null)
    {
        InitializeComponent();
        WindowFrame.UseDarkFrame(this);
        _vst3Host = vst3Host ?? new Vst3PluginHost();
        _filePath = filePath;
        PluginNameText.Text = name;
        PluginKindText.Text = kind;
        PluginPathText.Text = filePath;
        BadgeText.Text = kind.Length >= 2 ? kind[..2].ToUpperInvariant() : kind.ToUpperInvariant();
        Title = $"VST Plug-in - {name}";

        Loaded += VstPluginEditorDialog_Loaded;
        Closed += VstPluginEditorDialog_Closed;
    }

    private void VstPluginEditorDialog_Loaded(object sender, RoutedEventArgs e)
    {
        LoadModuleAndPopulateParameters();
    }

    private void VstPluginEditorDialog_Closed(object? sender, EventArgs e)
    {
        if (_moduleHandle is not null)
        {
            LiveInstrumentRegistry.Clear(_moduleHandle);
            _vst3Host.StopAudio(_moduleHandle);
            _vst3Host.UnloadModule(_moduleHandle);
            _moduleHandle = null;
        }
    }

    private void LoadModuleAndPopulateParameters()
    {
        var handle = _vst3Host.TryLoadModule(_filePath, out var error);
        if (handle is null)
        {
            StatusText.Text = error ?? "Unable to load the VST3 module.";
            return;
        }

        _moduleHandle = handle;

        var displayName = _vst3Host.GetPluginDisplayName(handle);
        if (!string.IsNullOrWhiteSpace(displayName))
        {
            PluginNameText.Text = displayName;
            Title = $"VST Plug-in - {displayName}";
        }

        var hasNativeEditor = _vst3Host.HasNativeEditor(handle);
        OpenNativeEditorButton.Content = hasNativeEditor ? "Open native editor" : "Native GUI unavailable";
        OpenNativeEditorButton.IsEnabled = hasNativeEditor;

        // Start real-time audio right away (if the plugin supports it) so
        // that clicking on-screen preview keys in the native editor - or
        // moving the parameter sliders below - can actually be heard.
        var audioStatus = string.Empty;
        var asioAudioStarted = false;
        if (_vst3Host.HasAudioProcessor(handle))
        {
            var selection = AudioOutputSelection.FromSettings(new AppSettingsService().Load());
            var started = _vst3Host.TryStartAudio(handle, selection, out var audioError);
            if (started)
            {
                LiveInstrumentRegistry.Set(handle);
            }
            asioAudioStarted = started && selection.UseAsio;
            audioStatus = started
                ? " Real-time audio active."
                : $" Audio did not start: {audioError}";
        }

        // Latency can be measured only through an ASIO session, where the output and input are known to the bridge.
        MeasureLatencyButton.IsEnabled = asioAudioStarted;

        var parameters = _vst3Host.GetParameters(handle);
        if (parameters.Count == 0)
        {
            StatusText.Text = "VST3 module loaded, but the plug-in exposes no readable parameters." + audioStatus;
            return;
        }

        StatusText.Text = (parameters.Count > MaxParametersShown
            ? $"{parameters.Count} real parameters read from the plug-in (the first {MaxParametersShown} are shown below)."
            : $"{parameters.Count} real parameters read from the plug-in.") + audioStatus;

        ParametersPanel.Children.Clear();
        foreach (var parameter in parameters.Take(MaxParametersShown))
        {
            ParametersPanel.Children.Add(BuildParameterRow(parameter));
        }
    }

    private async void MeasureLatencyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_moduleHandle is null)
        {
            return;
        }

        var handle = _moduleHandle;
        MeasureLatencyButton.IsEnabled = false;
        StatusText.Text = "Measuring latency: a short 1 kHz burst is sent to output 1. Keep the monitor volume low.";

        var result = await Task.Run(() =>
        {
            var measured = _vst3Host.TryMeasureAsioLatency(handle, out var milliseconds, out var error);
            var timing = _vst3Host.TryGetAsioTiming(handle, out var bufferFrames, out var outputLatency, out var sampleRate);
            return (measured, milliseconds, error, timing, bufferFrames, outputLatency, sampleRate);
        });

        if (result.measured)
        {
            StatusText.Text = $"Measured round trip: {result.milliseconds.ToString("F2", CultureInfo.InvariantCulture)} ms.";
            if (result.timing)
            {
                StatusText.Text += $" Buffer {result.bufferFrames} samples; the driver declares {result.outputLatency} samples of output latency.";
            }
        }
        else
        {
            StatusText.Text = $"Latency not measured: {result.error}";
        }

        MeasureLatencyButton.IsEnabled = true;
    }

    private static FrameworkElement BuildParameterRow(Vst3ParameterInfo parameter)
    {
        var container = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };

        var titleText = string.IsNullOrWhiteSpace(parameter.Title) ? $"Param #{parameter.Id}" : parameter.Title;
        var label = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(parameter.Units) ? titleText : $"{titleText} ({parameter.Units})",
            FontSize = 11,
        };
        container.Children.Add(label);

        var slider = new Slider
        {
            Minimum = 0,
            Maximum = 1000,
            Value = parameter.CurrentNormalized * 1000.0,
            Tag = parameter.Id,
        };
        slider.ValueChanged += ParameterSlider_ValueChanged;
        container.Children.Add(slider);

        return container;
    }

    private static void ParameterSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (sender is not Slider { Tag: uint parameterId } slider)
        {
            return;
        }

        if (Window.GetWindow(slider) is VstPluginEditorDialog dialog && dialog._moduleHandle is not null)
        {
            dialog._vst3Host.SetParameterValue(dialog._moduleHandle, parameterId, slider.Value / 1000.0);
        }
    }

    private void OpenNativeEditorButton_Click(object sender, RoutedEventArgs e)
    {
        if (_moduleHandle is null)
        {
            MessageBox.Show(
                this,
                "The VST3 module is not loaded correctly yet.",
                "Native GUI unavailable",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (!_vst3Host.HasNativeEditor(_moduleHandle))
        {
            MessageBox.Show(
                this,
                "This plug-in does not expose an embeddable native GUI (IPlugView) on this platform.\n\n" +
                "The plug-in's real parameters remain readable and editable above.",
                "Native GUI unavailable",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var editorWindow = new Vst3NativeEditorWindow(_vst3Host, _moduleHandle, PluginNameText.Text)
        {
            Owner = this,
        };
        editorWindow.Show();
    }
}
