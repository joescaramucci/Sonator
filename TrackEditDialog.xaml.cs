using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using Sonator.Models;

namespace Sonator;

public partial class TrackEditDialog : Window, INotifyPropertyChanged
{
    private string _trackName;
    private string _instrument;
    private PatternTrackType _trackType;
    private string _source;
    private string _channelText;
    private string _noteNumberText;
    private string _errorMessage = string.Empty;

    public TrackEditDialog(
        string trackName,
        string instrument,
        PatternTrackType trackType,
        string source,
        int channel,
        int noteNumber,
        bool isNewTrack)
    {
        _trackName = trackName;
        _instrument = instrument;
        _trackType = trackType;
        _source = source;
        _channelText = channel.ToString();
        _noteNumberText = noteNumber.ToString();
        InitializeComponent();
        WindowFrame.UseDarkFrame(this);
        DataContext = this;
        Title = isNewTrack ? "New track" : "Edit track";
        Loaded += (_, _) => TrackNameTextBox.Focus();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string TrackName
    {
        get => _trackName;
        set => SetField(ref _trackName, value);
    }

    public string Instrument
    {
        get => _instrument;
        set => SetField(ref _instrument, value);
    }

    public PatternTrackType TrackType
    {
        get => _trackType;
        set => SetField(ref _trackType, value);
    }

    public string Source
    {
        get => _source;
        set => SetField(ref _source, value);
    }

    public string ChannelText
    {
        get => _channelText;
        set => SetField(ref _channelText, value);
    }

    public string NoteNumberText
    {
        get => _noteNumberText;
        set => SetField(ref _noteNumberText, value);
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        set => SetField(ref _errorMessage, value);
    }

    public int Channel { get; private set; }

    public int NoteNumber { get; private set; }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TrackName))
        {
            ErrorMessage = "Track name is required.";
            return;
        }

        if (!int.TryParse(ChannelText, out var channel) || channel < 1 || channel > 16)
        {
            ErrorMessage = "MIDI channel must be between 1 and 16.";
            return;
        }

        if (!int.TryParse(NoteNumberText, out var noteNumber) || noteNumber < 0 || noteNumber > 127)
        {
            ErrorMessage = "Base note must be between 0 and 127.";
            return;
        }

        Channel = channel;
        NoteNumber = noteNumber;
        ErrorMessage = string.Empty;
        DialogResult = true;
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        if (!string.IsNullOrEmpty(ErrorMessage))
        {
            ErrorMessage = string.Empty;
        }
    }
}
