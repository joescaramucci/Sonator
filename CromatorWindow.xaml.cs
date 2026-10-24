using System.ComponentModel;
using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Effects;
using System.Windows.Media;
using System.Windows.Threading;
using Sonator.Services.Score;
using Sonator.ViewModels;

namespace Sonator;

// Cromator: the score of a track, its notes on a staff with the words under them, as it prints; printed on paper or
// saved as PDF. It follows the project as it changes.
public partial class CromatorWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly DispatcherTimer _redraw = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private ScoreDocument? _document;
    private bool _filling;

    public CromatorWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        WindowFrame.UseDarkFrame(this);
        _viewModel = viewModel;
        _redraw.Tick += (_, _) =>
        {
            _redraw.Stop();
            Redraw();
        };

        _filling = true;
        TitleBox.Text = viewModel.ScoreTitle;
        AuthorBox.Text = viewModel.ScoreAuthor;
        _filling = false;
        FillTracks(viewModel.SelectedTrack);

        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        viewModel.TrackNotesChanged += OnTrackNotesChanged;
        Closed += (_, _) =>
        {
            viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            viewModel.TrackNotesChanged -= OnTrackNotesChanged;
        };
    }

    private PatternTrackViewModel? Track => TrackBox.SelectedItem as PatternTrackViewModel;

    // The tracks with notes, the one asked for (or the one shown so far) chosen when it is among them.
    private void FillTracks(PatternTrackViewModel? preferred)
    {
        _filling = true;
        var voices = _viewModel.VoiceTracks;
        var voice = VoiceBox.SelectedItem;
        VoiceBox.ItemsSource = voices;
        VoiceBox.SelectedItem = voices.FirstOrDefault(track => ReferenceEquals(track, voice)) ?? voices.FirstOrDefault();
        var tracks = _viewModel.ScoreTracks;
        TrackBox.ItemsSource = tracks;
        TrackBox.SelectedItem = tracks.FirstOrDefault(track => ReferenceEquals(track, preferred)) ?? tracks.FirstOrDefault();
        LyricsBox.Text = Track?.Lyrics ?? string.Empty;
        LyricsBox.IsEnabled = Track is not null;
        _filling = false;
        Redraw();
    }

    // Shows the score of another track: called when the window is asked for again.
    public void ShowTrack(PatternTrackViewModel? track)
    {
        if (track is not null && !ReferenceEquals(track, Track))
        {
            FillTracks(track);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.SelectedPattern):
                FillTracks(Track);
                break;
            case nameof(MainViewModel.IsArrangeMode) or nameof(MainViewModel.TimeSignature) or nameof(MainViewModel.Tempo)
                or nameof(MainViewModel.SongTitle):
                _redraw.Start();
                break;
        }
    }

    private void OnTrackNotesChanged(PatternTrackViewModel track)
    {
        if (!_viewModel.ScoreTracks.SequenceEqual((IEnumerable<PatternTrackViewModel>)TrackBox.ItemsSource))
        {
            FillTracks(Track ?? track);
            return;
        }

        _redraw.Stop();
        _redraw.Start();
    }

    private void Redraw()
    {
        Pages.Children.Clear();
        _document = null;
        var track = Track;
        EmptyText.Visibility = track is null ? Visibility.Visible : Visibility.Collapsed;
        if (track is null)
        {
            return;
        }

        var (notes, totalSteps) = _viewModel.ScoreNotesOf(track);
        var meter = _viewModel.Meter;
        var sheet = ScoreLyrics.Apply(ScoreBuilder.Build(notes, meter.Numerator, meter.Denominator, totalSteps), track.Lyrics);
        var title = string.IsNullOrWhiteSpace(_viewModel.ScoreTitle) ? _viewModel.SongTitle : _viewModel.ScoreTitle;
        _document = new ScoreDocument(sheet, title, _viewModel.ScoreAuthor, _viewModel.Tempo);
        for (var page = 0; page < _document.PageCount; page++)
        {
            Pages.Children.Add(new Border
            {
                Margin = new Thickness(0, 0, 0, 24),
                Background = Brushes.White,
                Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 3, Opacity = 0.5 },
                Child = new ScorePageView(_document, page),
            });
        }
    }

    private void TrackBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling)
        {
            return;
        }

        _filling = true;
        LyricsBox.Text = Track?.Lyrics ?? string.Empty;
        LyricsBox.IsEnabled = Track is not null;
        _filling = false;
        Redraw();
    }

    private void LyricsBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_filling || Track is not { } track)
        {
            return;
        }

        track.Lyrics = LyricsBox.Text;
        _redraw.Stop();
        _redraw.Start();
    }

    private void ScoreText_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_filling)
        {
            return;
        }

        _viewModel.ScoreTitle = TitleBox.Text;
        _viewModel.ScoreAuthor = AuthorBox.Text;
        _redraw.Stop();
        _redraw.Start();
    }

    private void ZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (PageScale is not null)
        {
            PageScale.ScaleX = PageScale.ScaleY = e.NewValue;
        }
    }

    private void Print_Click(object sender, RoutedEventArgs e)
    {
        if (_document is null)
        {
            return;
        }

        var dialog = new PrintDialog();
        if (dialog.ShowDialog() == true)
        {
            dialog.PrintDocument(new ScorePaginator(_document), PrintName);
        }
    }

    // Saves the score as a PDF through Windows' own PDF printer, which asks where to save it.
    private void Pdf_Click(object sender, RoutedEventArgs e)
    {
        if (_document is null)
        {
            return;
        }

        PrintQueue? pdf;
        try
        {
            using var server = new LocalPrintServer();
            pdf = server.GetPrintQueues().FirstOrDefault(queue => queue.Name.Contains("PDF", StringComparison.OrdinalIgnoreCase));
        }
        catch (PrintSystemException)
        {
            pdf = null;
        }

        if (pdf is null)
        {
            SonatorMessageDialog.Show(this, "Cromator", "Windows' PDF printer (Microsoft Print to PDF) was not found. Turn it on in Windows Features, or use PRINT and choose another PDF printer.");
            return;
        }

        var dialog = new PrintDialog { PrintQueue = pdf };
        dialog.PrintDocument(new ScorePaginator(_document), PrintName);
    }

    // ---- From a sung take ----

    private CancellationTokenSource? _transcribing;

    // Writes the melody of the chosen take on a MIDI track, with the words heard when WORDS is on; a second click
    // stops it. The speech model is downloaded first, if the player agrees, the first time words are asked for.
    private async void Transcribe_Click(object sender, RoutedEventArgs e)
    {
        if (_transcribing is not null)
        {
            _transcribing.Cancel();
            return;
        }

        if (VoiceBox.SelectedItem is not PatternTrackViewModel voice)
        {
            VoiceStatus.Text = "Record a take on an audio track first: it shows here.";
            return;
        }

        var words = WordsBox.IsChecked == true;
        var language = (LanguageBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "auto";
        var progress = new Progress<string>(text => VoiceStatus.Text = text);
        _transcribing = new CancellationTokenSource();
        TranscribeButton.Content = "STOP";
        try
        {
            if (words && !SungWords.IsModelReady)
            {
                if (!SonatorMessageDialog.Ask(this, "Cromator",
                        "To hear the words, Cromator needs Whisper's speech model (about 470 MB), downloaded once and kept on this computer. The words are then heard here, without the internet. Download it now?",
                        "Download", "Notes only"))
                {
                    words = false;
                }
                else
                {
                    await SungWords.DownloadModelAsync(progress, _transcribing.Token);
                }
            }

            var track = await _viewModel.TranscribeVoiceAsync(voice, words, language, progress, _transcribing.Token);
            VoiceStatus.Text = _viewModel.StatusMessage;
            if (track is not null)
            {
                FillTracks(track);
            }
        }
        catch (OperationCanceledException)
        {
            VoiceStatus.Text = "Stopped";
        }
        catch (Exception ex) when (ex is System.IO.IOException or System.Net.Http.HttpRequestException or InvalidOperationException or DllNotFoundException)
        {
            VoiceStatus.Text = $"Could not finish: {ex.Message}";
        }
        finally
        {
            _transcribing.Dispose();
            _transcribing = null;
            TranscribeButton.Content = "WRITE FROM VOICE";
        }
    }

    private string PrintName => string.IsNullOrWhiteSpace(_viewModel.ScoreTitle) ? _viewModel.SongTitle : _viewModel.ScoreTitle;
}
