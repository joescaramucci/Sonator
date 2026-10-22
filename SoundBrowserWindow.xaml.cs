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
        Closed += (_, _) => _viewModel.PropertyChanged -= OnViewModelChanged;
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
                : "Double-click a sound, or press Enter, to load it on the selected track. Then play it on the keyboard.";
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
            case Key.Enter:
                LoadSelected();
                e.Handled = true;
                break;
        }
    }

    private void CategoryList_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowSounds();

    private void SoundList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        LoadButton.IsEnabled = SoundList.SelectedItem is SoundEntry;

    private void SoundList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => LoadSelected();

    private void SoundList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            LoadSelected();
            e.Handled = true;
        }
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
