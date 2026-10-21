using System.IO;
using System.Windows;
using System.Windows.Controls;
using Sonator.Services.Vst3;
using Sonator.ViewModels;

namespace Sonator;

// One strip per track: the track's volume, mute and solo, and the first parameters of the plug-in it plays through.
public partial class MixerWindow : Window
{
    private const int MaxParametersPerStrip = 16;

    private readonly MainViewModel _viewModel;
    private readonly IVst3PluginHost _host = new Vst3PluginHost();
    private readonly Dictionary<string, Vst3ModuleHandle> _modulesByPath = new(StringComparer.OrdinalIgnoreCase);

    public MixerWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        Loaded += (_, _) => BuildStrips();
        Closed += (_, _) => ReleaseModules();
    }

    private void BuildStrips()
    {
        StripsPanel.Children.Clear();
        foreach (var track in _viewModel.Tracks)
        {
            StripsPanel.Children.Add(BuildStrip(track));
        }
    }

    private FrameworkElement BuildStrip(PatternTrackViewModel track)
    {
        var strip = new StackPanel { Width = 120, Margin = new Thickness(6) };

        strip.Children.Add(new TextBlock
        {
            Text = track.Name,
            FontWeight = FontWeights.Bold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        // The slider is created with its value before the handler is attached, so building the strip does not write back.
        var volume = new Slider
        {
            Orientation = Orientation.Vertical,
            Height = 200,
            Minimum = 0,
            Maximum = 1,
            Value = track.Volume,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 8),
        };
        volume.ValueChanged += (_, e) => track.Volume = e.NewValue;
        strip.Children.Add(volume);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        var mute = new Button { Content = "M", Width = 40 };
        mute.Click += (_, _) => _viewModel.ToggleTrackMuteCommand.Execute(track);
        var solo = new Button { Content = "S", Width = 40 };
        solo.Click += (_, _) => _viewModel.ToggleTrackSoloCommand.Execute(track);
        buttons.Children.Add(mute);
        buttons.Children.Add(solo);
        strip.Children.Add(buttons);

        // The plug-in's parameters sit above the track's name and fader, as in a channel strip.
        var pluginPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        if (string.IsNullOrWhiteSpace(track.PluginFilePath))
        {
            pluginPanel.Children.Add(new TextBlock { Text = "no plug-in", Opacity = 0.7 });
        }
        else
        {
            pluginPanel.Children.Add(new TextBlock
            {
                Text = Path.GetFileNameWithoutExtension(track.PluginFilePath),
                FontSize = 10,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 4),
            });
            AddParameterSliders(track.PluginFilePath, pluginPanel);
        }
        strip.Children.Insert(0, pluginPanel);
        return strip;
    }

    private void AddParameterSliders(string pluginPath, StackPanel strip)
    {
        var handle = GetOrLoadModule(pluginPath, out var error);
        if (handle is null)
        {
            strip.Children.Add(new TextBlock { Text = error ?? "plug-in not loaded", FontSize = 10, TextWrapping = TextWrapping.Wrap });
            return;
        }

        foreach (var parameter in _host.GetParameters(handle).Take(MaxParametersPerStrip))
        {
            strip.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(parameter.Units) ? parameter.Title : $"{parameter.Title} ({parameter.Units})",
                FontSize = 9,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });

            var slider = new Slider { Minimum = 0, Maximum = 1, Value = parameter.CurrentNormalized };
            var parameterId = parameter.Id;
            slider.ValueChanged += (_, e) => _host.SetParameterValue(handle, parameterId, e.NewValue);
            strip.Children.Add(slider);
        }
    }

    // Each plug-in file is loaded once, even when several tracks use it.
    private Vst3ModuleHandle? GetOrLoadModule(string pluginPath, out string? error)
    {
        if (_modulesByPath.TryGetValue(pluginPath, out var existing))
        {
            error = null;
            return existing;
        }

        var handle = _host.TryLoadModule(pluginPath, out error);
        if (handle is not null)
        {
            _modulesByPath[pluginPath] = handle;
        }
        return handle;
    }

    private void ReleaseModules()
    {
        foreach (var handle in _modulesByPath.Values)
        {
            _host.UnloadModule(handle);
        }
        _modulesByPath.Clear();
    }
}
