using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Sonator.Services.Sounds;
using Sonator.ViewModels;

namespace Sonator;

// Finds a sound among all the installed plug-ins' sounds, by category and by words, and loads it on the
// selected track.
public partial class SoundBrowserWindow : Window
{
    // A row of the category list: All, or one category, with how many sounds it holds.
    private sealed record CategoryItem(string? Category, string Label, int Count);

    private readonly MainViewModel _viewModel;
    private readonly DispatcherTimer _searchDelay = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private IReadOnlyList<SoundEntry> _sounds = Array.Empty<SoundEntry>();

    public SoundBrowserWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        WindowFrame.UseDarkFrame(this);
        _viewModel = viewModel;
        _searchDelay.Tick += (_, _) =>
        {
            _searchDelay.Stop();
            ShowSounds();
        };

        ShowTarget();
        _viewModel.PropertyChanged += OnViewModelChanged;
        Closed += (_, _) =>
        {
            _viewModel.PropertyChanged -= OnViewModelChanged;
            _viewModel.StopSoundPreview(unload: true); // the preview's plug-in instance goes with the browser
        };
        _autoPreviewDelay.Tick += (_, _) =>
        {
            _autoPreviewDelay.Stop();
            PreviewSelected();
        };
        Loaded += async (_, _) =>
        {
            SearchBox.Focus();
            StatusText.Text = "Reading the plug-ins' sounds...";
            try
            {
                _sounds = await _viewModel.SoundsAsync();
            }
            catch (Exception ex)
            {
                StatusText.Text = $"The sounds could not be read: {ex.Message}";
                return;
            }

            ShowCategories();
            StatusText.Text = _sounds.Count == 0
                ? "No sounds found. Sounds are read from the presets of Arturia's installed instruments."
                : "Double-click, or press Enter: a sound loads on the selected track, to play on the keyboard; a groove's notes go into the track from the playhead's bar.";
        };
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.SelectedTrack))
        {
            ShowTarget();
        }
    }

    private void ShowTarget()
    {
        TargetText.Text = _viewModel.SelectedTrack is { } track
            ? $"Loads on {track.Name}{(track.PluginName is { } plugin ? $" — now {plugin}" : string.Empty)}"
            : "Select a track to load sounds on";
    }

    private void ShowCategories()
    {
        var counts = _sounds.GroupBy(sound => sound.Category).ToDictionary(group => group.Key, group => group.Count());
        var items = new List<CategoryItem> { new(null, "All sounds", _sounds.Count) };
        items.AddRange(SoundCategories.All
            .Where(counts.ContainsKey)
            .Select(category => new CategoryItem(category, category, counts[category])));
        CategoryList.ItemsSource = items;
        CategoryList.SelectedIndex = 0;
    }

    private void ShowSounds()
    {
        var category = (CategoryList.SelectedItem as CategoryItem)?.Category;
        var found = SoundSearch.Filter(_sounds, SearchBox.Text, category).ToList();
        SoundList.ItemsSource = found;
        CountText.Text = $"{found.Count} sounds";
        if (found.Count > 0)
        {
            SoundList.SelectedIndex = 0;
            SoundList.ScrollIntoView(found[0]);
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchDelay.Stop();
        _searchDelay.Start();
    }

    // Arrows move through the sounds and Enter loads one, without leaving the search box.
    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (SoundList.Items.Count == 0)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Down:
                SoundList.SelectedIndex = Math.Min(SoundList.SelectedIndex + 1, SoundList.Items.Count - 1);
                SoundList.ScrollIntoView(SoundList.SelectedItem);
                e.Handled = true;
                break;
            case Key.Up:
                SoundList.SelectedIndex = Math.Max(SoundList.SelectedIndex - 1, 0);
                SoundList.ScrollIntoView(SoundList.SelectedItem);
                e.Handled = true;
                break;
            case Key.Enter when (Keyboard.Modifiers & ModifierKeys.Control) != 0:
                PreviewSelected(); // Ctrl+Enter previews from the search box, where Space types a space
                e.Handled = true;
                break;
            case Key.Enter:
                LoadSelected();
                e.Handled = true;
                break;
        }
    }

    private void CategoryList_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowSounds();

    // With AUTO on, a sound is previewed once the selection rests on it a moment, so running through the
    // list with the arrows does not start every plug-in on the way.
    private readonly DispatcherTimer _autoPreviewDelay = new() { Interval = TimeSpan.FromMilliseconds(350) };

    private void SoundList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        LoadButton.IsEnabled = PreviewButton.IsEnabled = SoundList.SelectedItem is SoundEntry;
        _autoPreviewDelay.Stop();
        if (AutoPreviewToggle.IsChecked == true && SoundList.SelectedItem is SoundEntry)
        {
            _autoPreviewDelay.Start();
        }
    }

    private void SoundList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => LoadSelected();

    private void SoundList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            LoadSelected();
            e.Handled = true;
        }
        else if (e.Key == Key.Space)
        {
            PreviewSelected();
            e.Handled = true;
        }
    }

    private void PreviewButton_Click(object sender, RoutedEventArgs e) => PreviewSelected();

    private void PreviewSelected()
    {
        if (SoundList.SelectedItem is not SoundEntry sound)
        {
            return;
        }

        Mouse.OverrideCursor = Cursors.Wait; // a plug-in not running yet takes a moment to start
        try
        {
            _viewModel.PreviewSound(sound);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }

        StatusText.Text = _viewModel.StatusMessage;
    }

    private void LoadButton_Click(object sender, RoutedEventArgs e) => LoadSelected();

    private void LoadSelected()
    {
        if (SoundList.SelectedItem is not SoundEntry sound)
        {
            return;
        }

        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            _viewModel.LoadSound(sound);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }

        StatusText.Text = _viewModel.StatusMessage;
        ShowTarget();
    }
}
