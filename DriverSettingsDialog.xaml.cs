using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Sonator.Services;

namespace Sonator;

public partial class DriverSettingsDialog : Window
{
    private readonly string _driverClsid;
    private readonly AppSettingsService _settingsService = new();
    private AsioDriverCapabilities? _capabilities;

    public DriverSettingsDialog(string driverName, string driverClsid)
    {
        InitializeComponent();
        _driverClsid = driverClsid;
        DriverNameText.Text = driverName;
        Loaded += (_, _) => LoadCapabilities();
    }

    private void LoadCapabilities()
    {
        _capabilities = AsioDriverCapabilityReader.TryRead(_driverClsid, out var error);
        if (_capabilities is null)
        {
            StatusText.Text = error ?? "The driver's settings could not be read.";
            BufferCombo.IsEnabled = false;
            RateCombo.IsEnabled = false;
            OkButton.IsEnabled = false;
            return;
        }

        var saved = _settingsService.Load();
        var rate = _capabilities.CurrentSampleRate;

        BufferCombo.Items.Add(new ComboBoxItem
        {
            Content = $"Driver default ({_capabilities.PreferredBuffer})",
            Tag = 0,
        });
        foreach (var size in _capabilities.BufferSizes)
        {
            BufferCombo.Items.Add(new ComboBoxItem
            {
                Content = $"{size} ({(size * 1000.0 / rate).ToString("F2", CultureInfo.InvariantCulture)} ms at {rate:F0} Hz)",
                Tag = size,
            });
        }
        SelectByTag(BufferCombo, saved.AsioBufferFrames ?? 0);

        RateCombo.Items.Add(new ComboBoxItem
        {
            Content = $"Driver default ({rate:F0} Hz)",
            Tag = 0.0,
        });
        foreach (var candidate in _capabilities.SampleRates)
        {
            RateCombo.Items.Add(new ComboBoxItem
            {
                Content = $"{candidate:F0} Hz",
                Tag = candidate,
            });
        }
        SelectByTag(RateCombo, saved.AsioSampleRate ?? 0.0);

        StatusText.Text = $"Current: {_capabilities.CurrentSampleRate:F0} Hz, buffer {_capabilities.PreferredBuffer} samples (preferred).";
    }

    private static void SelectByTag<T>(ComboBox combo, T value)
    {
        foreach (ComboBoxItem item in combo.Items)
        {
            if (item.Tag is T tag && EqualityComparer<T>.Default.Equals(tag, value))
            {
                combo.SelectedItem = item;
                return;
            }
        }

        combo.SelectedIndex = 0;
    }

    private void ControlPanel_Click(object sender, RoutedEventArgs e)
    {
        if (!AsioDriverCapabilityReader.TryOpenControlPanel(_driverClsid, out var error))
        {
            MessageBox.Show(this, error ?? "The driver's control panel could not be opened.", "ASIO driver settings", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var settings = _settingsService.Load();
        var bufferFrames = (int)((ComboBoxItem)BufferCombo.SelectedItem).Tag;
        var sampleRate = (double)((ComboBoxItem)RateCombo.SelectedItem).Tag;

        settings.AsioBufferFrames = bufferFrames == 0 ? null : bufferFrames;
        settings.AsioSampleRate = sampleRate == 0.0 ? null : sampleRate;
        _settingsService.Save(settings);
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
