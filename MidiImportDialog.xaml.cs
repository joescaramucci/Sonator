using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using Microsoft.Win32;

namespace Sonator;

public partial class MidiImportDialog : Window, INotifyPropertyChanged
{
    private string _filePath = string.Empty;
    private string _errorMessage = string.Empty;

    public MidiImportDialog()
    {
        InitializeComponent();
        DataContext = this;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string FilePath
    {
        get => _filePath;
        set
        {
            if (_filePath == value)
            {
                return;
            }

            _filePath = value;
            OnPropertyChanged();
            ErrorMessage = string.Empty;
        }
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        set
        {
            if (_errorMessage == value)
            {
                return;
            }

            _errorMessage = value;
            OnPropertyChanged();
        }
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "MIDI files (*.mid;*.midi)|*.mid;*.midi"
        };

        if (dialog.ShowDialog() == true)
        {
            FilePath = dialog.FileName;
        }
    }

    private void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        var trimmed = FilePath.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            ErrorMessage = "Select a MIDI file first.";
            return;
        }

        if (!File.Exists(trimmed))
        {
            ErrorMessage = "Selected file does not exist.";
            return;
        }

        FilePath = trimmed;
        DialogResult = true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
