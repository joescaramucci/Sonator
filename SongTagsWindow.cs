using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Sonator.Services;

namespace Sonator;

// The tags of the song (title, artist, album, genre, year, track, composer, comment and cover), which every file
// exported carries; or, after OPEN FILE, those of any MP3 or WAV, written into it on SAVE.
public sealed class SongTagsWindow : Window
{
    private static readonly Brush Ink = Frozen(Color.FromRgb(0xE4, 0xE5, 0xE7));
    private static readonly Brush Faint = Frozen(Color.FromRgb(0x98, 0x99, 0x9B));
    private static readonly Brush Field = Frozen(Color.FromRgb(0x1E, 0x1F, 0x21));

    private readonly Action<SongTags> _saveSong;
    private readonly TextBox _title = Box();
    private readonly TextBox _artist = Box();
    private readonly TextBox _album = Box();
    private readonly TextBox _genre = Box();
    private readonly TextBox _year = Box();
    private readonly TextBox _track = Box();
    private readonly TextBox _composer = Box();
    private readonly TextBox _comment = Box();
    private readonly Image _cover = new() { Width = 150, Height = 150, Stretch = Stretch.UniformToFill };
    private readonly TextBlock _target = new() { Foreground = Faint, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
    private byte[]? _coverBytes;
    private string? _coverType;
    private string? _filePath;

    public SongTagsWindow(SongTags songTags, Action<SongTags> saveSong, string? filePath = null)
    {
        _saveSong = saveSong;
        Title = "Song info and tags";
        Width = 620;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = Frozen(Color.FromRgb(0x2B, 0x2C, 0x2E));
        Foreground = Ink;
        _comment.AcceptsReturn = true;
        _comment.TextWrapping = TextWrapping.Wrap;
        _comment.Height = 54;
        _comment.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;

        var fields = new Grid { Margin = new Thickness(0, 0, 14, 0) };
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        fields.ColumnDefinitions.Add(new ColumnDefinition());
        void Row(string label, FrameworkElement input)
        {
            var row = fields.RowDefinitions.Count;
            fields.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var text = new TextBlock { Text = label, Foreground = Faint, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            Grid.SetRow(text, row);
            Grid.SetRow(input, row);
            Grid.SetColumn(input, 1);
            fields.Children.Add(text);
            fields.Children.Add(input);
        }

        Row("Title", _title);
        Row("Artist", _artist);
        Row("Album", _album);
        // The genre: typed, or chosen from those players know.
        var genres = Button("▾", (_, _) => { });
        genres.MinWidth = 26;
        genres.Margin = new Thickness(3, 2, 0, 2);
        genres.ToolTip = "Choose a genre";
        genres.Click += (_, _) =>
        {
            var menu = new ContextMenu { PlacementTarget = genres };
            foreach (var genre in SongTags.CommonGenres)
            {
                var item = new MenuItem { Header = genre };
                item.Click += (_, _) => _genre.Text = genre;
                menu.Items.Add(item);
            }

            menu.IsOpen = true;
        };
        var genreRow = new DockPanel();
        DockPanel.SetDock(genres, Dock.Right);
        genreRow.Children.Add(genres);
        genreRow.Children.Add(_genre);
        Row("Genre", genreRow);
        var numbers = new UniformGrid2(_year, _track);
        Row("Year · Track", numbers);
        Row("Composer", _composer);
        Row("Comment", _comment);

        var choose = Button("PICTURE...", ChooseCover_Click);
        var remove = Button("REMOVE", (_, _) => ShowCover(null, null));
        var coverPanel = new StackPanel { Width = 150 };
        coverPanel.Children.Add(new Border { Background = Field, BorderBrush = Faint, BorderThickness = new Thickness(1), Child = _cover, Height = 152 });
        var coverButtons = new UniformGrid2(choose, remove) { Margin = new Thickness(0, 6, 0, 0) };
        coverPanel.Children.Add(coverButtons);
        coverPanel.Children.Add(new TextBlock { Text = "Cover: a square JPEG or PNG, 1400 px or more for the stores", Foreground = Faint, FontSize = 10, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });

        var body = new DockPanel();
        DockPanel.SetDock(coverPanel, Dock.Right);
        body.Children.Add(coverPanel);
        body.Children.Add(fields);

        var open = Button("OPEN FILE...", OpenFile_Click);
        open.ToolTip = "Edit the tags of any MP3 or WAV instead of the song's";
        var save = Button("SAVE", Save_Click);
        save.IsDefault = true;
        var cancel = Button("CANCEL", (_, _) => Close());
        cancel.IsCancel = true;
        var buttons = new DockPanel { Margin = new Thickness(0, 12, 0, 0), LastChildFill = false };
        DockPanel.SetDock(open, Dock.Left);
        DockPanel.SetDock(cancel, Dock.Right);
        DockPanel.SetDock(save, Dock.Right);
        buttons.Children.Add(open);
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);

        var layout = new StackPanel { Margin = new Thickness(14) };
        layout.Children.Add(_target);
        layout.Children.Add(body);
        layout.Children.Add(buttons);
        Content = layout;

        Show(songTags);
        SetTarget(filePath);
        if (filePath is not null)
        {
            LoadFile(filePath);
        }

        Loaded += (_, _) =>
        {
            WindowFrame.UseDarkFrame(this);
            _title.Focus();
        };
    }

    private void SetTarget(string? filePath)
    {
        _filePath = filePath;
        _target.Text = filePath is null
            ? "The song's info: every file exported (WAV, MP3, stems) carries it. Empty title: the score's title or the project's name."
            : $"The tags of {filePath}: SAVE writes them into the file.";
    }

    private void Show(SongTags tags)
    {
        _title.Text = tags.Title ?? string.Empty;
        _artist.Text = tags.Artist ?? string.Empty;
        _album.Text = tags.Album ?? string.Empty;
        _genre.Text = tags.Genre ?? string.Empty;
        _year.Text = tags.Year?.ToString() ?? string.Empty;
        _track.Text = tags.Track?.ToString() ?? string.Empty;
        _composer.Text = tags.Composer ?? string.Empty;
        _comment.Text = tags.Comment ?? string.Empty;
        ShowCover(tags.Cover, tags.CoverMimeType);
    }

    private void ShowCover(byte[]? picture, string? type)
    {
        _coverBytes = picture;
        _coverType = type;
        _cover.Source = null;
        if (picture is not { Length: > 0 })
        {
            return;
        }

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(picture);
            image.EndInit();
            image.Freeze();
            _cover.Source = image;
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or InvalidOperationException)
        {
            _cover.Source = null; // kept, though it cannot be shown
        }
    }

    // The fields as tags; a year or track that is not a number is left out.
    private SongTags Tags() => new(
        Text(_title), Text(_artist), Text(_album), Text(_genre),
        int.TryParse(_year.Text, out var year) && year > 0 ? year : null,
        int.TryParse(_track.Text, out var track) && track > 0 ? track : null,
        Text(_composer), Text(_comment), _coverBytes, _coverType);

    private static string? Text(TextBox box) => string.IsNullOrWhiteSpace(box.Text) ? null : box.Text.Trim();

    private void ChooseCover_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Cover picture", Filter = "Pictures (*.jpg;*.jpeg;*.png)|*.jpg;*.jpeg;*.png" };
        if (dialog.ShowDialog(this) == true)
        {
            var picture = File.ReadAllBytes(dialog.FileName);
            ShowCover(picture, SongTags.MimeTypeOf(picture));
        }
    }

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Edit the tags of a song file", Filter = "Song files (*.mp3;*.wav;*.flac;*.m4a;*.ogg)|*.mp3;*.wav;*.flac;*.m4a;*.ogg" };
        if (dialog.ShowDialog(this) == true)
        {
            LoadFile(dialog.FileName);
        }
    }

    private void LoadFile(string path)
    {
        try
        {
            Show(SongTags.Read(path));
            SetTarget(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TagLib.CorruptFileException or TagLib.UnsupportedFormatException)
        {
            SonatorMessageDialog.Show(this, "Tags", $"The tags of {Path.GetFileName(path)} could not be read: {ex.Message}");
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_filePath is null)
        {
            _saveSong(Tags());
            Close();
            return;
        }

        try
        {
            Tags().Write(_filePath);
            SonatorMessageDialog.Show(this, "Tags", $"Tags written into {Path.GetFileName(_filePath)}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TagLib.CorruptFileException or TagLib.UnsupportedFormatException)
        {
            SonatorMessageDialog.Show(this, "Tags", $"The tags could not be written: {ex.Message}");
        }
    }

    private static TextBox Box() => new()
    {
        Margin = new Thickness(0, 2, 0, 2),
        Padding = new Thickness(3, 2, 3, 2),
        Background = Field,
        Foreground = Ink,
        CaretBrush = Ink,
        BorderBrush = Faint,
    };

    private static Button Button(string text, RoutedEventHandler click)
    {
        var button = new Button
        {
            Content = text,
            MinWidth = 86,
            Height = 26,
            Margin = new Thickness(4, 0, 0, 0),
            Padding = new Thickness(8, 0, 8, 0),
            Background = Frozen(Color.FromRgb(0x41, 0x41, 0x41)),
            Foreground = Ink,
            BorderBrush = Frozen(Color.FromRgb(0x5C, 0x7A, 0x99)),
        };
        button.Click += click;
        return button;
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    // Two controls side by side, sharing the width.
    private sealed class UniformGrid2 : System.Windows.Controls.Primitives.UniformGrid
    {
        public UniformGrid2(UIElement first, UIElement second)
        {
            Columns = 2;
            if (first is FrameworkElement left)
            {
                left.Margin = new Thickness(0, 2, 3, 2);
            }

            Children.Add(first);
            Children.Add(second);
        }
    }
}
