using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Sonator.Services.Sounds;
using Sonator.ViewModels;

namespace Sonator;

// Asks what kind of music a new song is to be and, for each part of that style (drums, bass, piano...),
// suggests the sounds of the installed plug-ins and grooves that suit it best (see SoundMatcher), to hear and
// choose from. What was chosen is read back once the window closes with CREATE SONG.
public partial class NewSongWindow : Window
{
    public enum Choice
    {
        Cancel,
        Empty,
        Song,
    }

    // A part of the style shown: the sounds suggested for it (a page of them), the one chosen, and whether the
    // song is to have the part.
    private sealed class Part
    {
        public Part(StyleRole role) => Role = role;

        public StyleRole Role { get; }

        public int Page { get; set; }

        public IReadOnlyList<SoundEntry> Shown { get; set; } = Array.Empty<SoundEntry>();

        public SoundEntry? Chosen { get; set; }

        public bool Included { get; set; } = true;
    }

    private const int SoundsPerPart = 8;

    private static readonly Brush Bright = Frozen(Color.FromRgb(0xEF, 0xF0, 0xF2));
    private static readonly Brush Instrument = Frozen(Color.FromRgb(0x9F, 0xD3, 0xF2));
    private static readonly Brush Faint = Frozen(Color.FromRgb(0x98, 0x99, 0x9B));
    private static readonly Brush Gold = Frozen(Color.FromRgb(0xE6, 0xC4, 0x5B));

    private readonly MainViewModel _viewModel;
    private readonly Dictionary<MusicStyle, List<Part>> _parts = new();
    private ILookup<string, SoundEntry> _byCategory = Array.Empty<SoundEntry>().ToLookup(sound => sound.Category);
    private int _shown; // counts the styles shown, so a style still being worked out does not show over a later one

    public NewSongWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        WindowFrame.UseDarkFrame(this);
        _viewModel = viewModel;
        Closed += (_, _) => _viewModel.StopSoundPreview(unload: true);
        Loaded += async (_, _) =>
        {
            StatusText.Text = "Reading the plug-ins' sounds...";
            try
            {
                var sounds = await _viewModel.SoundsAsync();
                _byCategory = sounds.ToLookup(sound => sound.Category);
            }
            catch (Exception ex)
            {
                StatusText.Text = $"The sounds could not be read: {ex.Message}. EMPTY PROJECT still starts a project.";
                return;
            }

            StyleList.ItemsSource = MusicStyles.All;
            StyleList.SelectedIndex = 0;
            StyleList.Focus();
        };
    }

    public Choice Result { get; private set; } = Choice.Cancel;

    public MusicStyle? ChosenStyle => StyleList.SelectedItem as MusicStyle;

    // The tempo typed, or the style's when what is typed is not one.
    public int Tempo =>
        int.TryParse(TempoBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tempo) && tempo is >= 20 and <= 300
            ? tempo
            : ChosenStyle?.Tempo ?? 120;

    // The parts ticked, each with its sound, in the style's order.
    public IReadOnlyList<(string Part, SoundEntry Sound)> Parts =>
        ChosenStyle is { } style && _parts.TryGetValue(style, out var parts)
            ? parts.Where(part => part.Included && part.Chosen is not null).Select(part => (part.Role.Name, part.Chosen!)).ToList()
            : Array.Empty<(string, SoundEntry)>();

    private async void StyleList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (StyleList.SelectedItem is not MusicStyle style)
        {
            return;
        }

        var shown = ++_shown;
        StyleTitle.Text = style.Name;
        StyleDescription.Text = style.Description;
        StyleHits.Text = $"Like: {string.Join("  ·  ", style.Hits)}";
        TempoBox.Text = style.Tempo.ToString(CultureInfo.InvariantCulture);
        if (!_parts.TryGetValue(style, out var parts))
        {
            PartsPanel.Children.Clear();
            CreateButton.IsEnabled = false;
            StatusText.Text = $"Finding the sounds that suit {style.Name}...";
            parts = style.Roles.Select(role => new Part(role)).ToList();
            await Task.Run(() =>
            {
                foreach (var part in parts)
                {
                    part.Shown = Suggest(style, part);
                    part.Chosen = part.Shown.FirstOrDefault();
                    part.Included = part.Chosen is not null;
                }
            });
            _parts[style] = parts;
            if (shown != _shown)
            {
                return; // another style was chosen meanwhile
            }
        }

        ShowParts(style, parts);
        StatusText.Text = "Click a sound to hear it and choose it for its part; untick a part the song is not to have.";
    }

    private IReadOnlyList<SoundEntry> Suggest(MusicStyle style, Part part) =>
        SoundMatcher.Suggest(part.Role.Categories.SelectMany(category => _byCategory[category]), style, part.Role, SoundsPerPart, part.Page);

    private void ShowParts(MusicStyle style, IReadOnlyList<Part> parts)
    {
        PartsPanel.Children.Clear();
        foreach (var part in parts)
        {
            PartsPanel.Children.Add(PartView(style, part));
        }

        UpdateCreateButton();
    }

    // A part: its name, ticked when the song is to have it, OTHERS for more sounds, and its sounds as tiles.
    private FrameworkElement PartView(MusicStyle style, Part part)
    {
        var include = new CheckBox
        {
            Content = part.Role.Name.ToUpperInvariant(),
            IsChecked = part.Included,
            IsEnabled = part.Shown.Count > 0,
            Foreground = Gold,
            FontFamily = new FontFamily("Consolas"),
            FontWeight = FontWeights.Bold,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Ticked: the song gets a track for this part, playing the sound chosen",
        };
        include.Click += (_, _) =>
        {
            part.Included = include.IsChecked == true;
            UpdateCreateButton();
        };

        var others = new Button
        {
            Content = "OTHERS ↻",
            Width = 100,
            Height = 24,
            IsEnabled = part.Shown.Count > 0,
            ToolTip = "More sounds for this part; after the last, back to the first",
        };

        var tiles = new UniformGrid { Columns = 4, Margin = new Thickness(0, 6, 0, 0) };
        FillTiles(tiles, style, part);
        others.Click += async (_, _) =>
        {
            others.IsEnabled = false;
            part.Page++;
            var shown = await Task.Run(() => Suggest(style, part));
            if (shown.Count == 0 && part.Page > 0)
            {
                part.Page = 0;
                shown = await Task.Run(() => Suggest(style, part));
            }

            part.Shown = shown;
            FillTiles(tiles, style, part);
            others.IsEnabled = true;
        };

        var header = new DockPanel();
        DockPanel.SetDock(others, Dock.Right);
        header.Children.Add(others);
        header.Children.Add(include);

        var view = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        view.Children.Add(header);
        view.Children.Add(part.Shown.Count > 0
            ? tiles
            : new TextBlock { Text = "None of your sounds is filed for this part.", Foreground = Faint, Margin = new Thickness(22, 4, 0, 0) });
        return view;
    }

    private void FillTiles(UniformGrid tiles, MusicStyle style, Part part)
    {
        tiles.Children.Clear();
        foreach (var sound in part.Shown)
        {
            var text = new StackPanel();
            text.Children.Add(new TextBlock
            {
                Text = sound.Name,
                Foreground = Bright,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            text.Children.Add(new TextBlock
            {
                Text = Source(sound),
                Foreground = Instrument,
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            var tile = new RadioButton
            {
                Content = text,
                GroupName = $"{style.Name}/{part.Role.Name}",
                IsChecked = ReferenceEquals(sound, part.Chosen),
                Style = (Style)FindResource("SoundTile"),
                ToolTip = Describe(sound),
            };
            tile.Click += (_, _) =>
            {
                part.Chosen = sound;
                if (AutoPreviewToggle.IsChecked == true)
                {
                    Preview(sound);
                }
            };
            tiles.Children.Add(tile);
        }
    }

    // Where a sound comes from: a preset's instrument and bank; a groove's library and style (its instrument
    // is the same for all of them).
    private static string Source(SoundEntry sound)
    {
        if (sound.Kind == SoundKind.KontaktInstrument)
        {
            return $"{(sound.IsHeavy ? "⚖ " : string.Empty)}Kontakt · {sound.DesignerAndWeight}";
        }

        var from = sound.Kind is SoundKind.Groove or SoundKind.Sample && !string.IsNullOrWhiteSpace(sound.Designer) ? sound.Designer : sound.PluginName;
        return string.IsNullOrWhiteSpace(sound.Bank) ? from : $"{from} · {sound.Bank}";
    }

    private static string Describe(SoundEntry sound)
    {
        var lines = new List<string> { $"{sound.Name} ({sound.PluginName})", sound.Category };
        if (!string.IsNullOrWhiteSpace(sound.Subcategory))
        {
            lines.Add(sound.Subcategory);
        }

        if (!string.IsNullOrWhiteSpace(sound.Genres))
        {
            lines.Add($"Genres: {sound.Genres.Replace("|", ", ")}");
        }

        if (sound.Kind == SoundKind.KontaktInstrument)
        {
            lines.Add($"Not heard here: Kontakt opens on the track, and the instrument is dragged into it. {sound.Designer} weighs {sound.Weight}.");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private void Preview(SoundEntry sound)
    {
        Mouse.OverrideCursor = _viewModel.IsSoundReady(sound) ? null : Cursors.Wait; // a plug-in not running yet takes a moment
        try
        {
            _viewModel.PreviewSound(sound, Tempo);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }

        StatusText.Text = _viewModel.StatusMessage;
    }

    private void UpdateCreateButton() => CreateButton.IsEnabled = Parts.Count > 0;

    private void CreateButton_Click(object sender, RoutedEventArgs e)
    {
        Result = Choice.Song;
        DialogResult = true;
    }

    private void EmptyButton_Click(object sender, RoutedEventArgs e)
    {
        Result = Choice.Empty;
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Result = Choice.Cancel;
        DialogResult = false;
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
