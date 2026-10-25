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
        _hoverDelay.Tick += (_, _) =>
        {
            _hoverDelay.Stop();
            if (_hovered is { } hovered)
            {
                ComeUpFrom(hovered);
            }
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
            if (_viewModel.SelectedTrack != _kitPiece?.KitParent)
            {
                _kitPiece = null;
            }

            ShowTarget();
        }
    }

    // The drum kit's piece the sounds are searched for, when a click on its name opened them: the sound loaded
    // plays it on a new track (see MainViewModel.ReplaceKitPiece).
    private PatternTrackViewModel? _kitPiece;

    private void ShowTarget()
    {
        TargetText.Text = _kitPiece is { KitParent: { } kit } piece
            ? $"Plays the {piece.Name.ToLowerInvariant()} of {kit.Name} on a new track, the kit's own muted"
            : _viewModel.SelectedTrack is { } track
            ? $"Loads on {track.Name}{(track.PluginName is { } plugin ? $" — now {plugin}" : string.Empty)}"
            : "Select a track to load sounds on";
    }

    // The category list's entry for the favorites: not a category of sounds, but the sounds marked.
    private const string FavoritesKey = "★";

    // And the one for the sounds last loaded, the latest first.
    private const string RecentKey = "↺";

    private readonly SoundFavorites _favorites = new();
    private readonly RecentSounds _recent = new();
    private bool _updatingCategories;

    // The categories with their counts, All, the favorites and the last used first; the one selected stays selected.
    private void ShowCategories()
    {
        var keep = (CategoryList.SelectedItem as CategoryItem)?.Category;
        var counts = _sounds.GroupBy(sound => sound.Category).ToDictionary(group => group.Key, group => group.Count());
        var items = new List<CategoryItem>
        {
            new(null, "All sounds", _sounds.Count),
            new(FavoritesKey, "★ Favorites", _sounds.Count(_favorites.Contains)),
            new(RecentKey, "↺ Last used", _recent.Of(_sounds).Count()),
        };
        items.AddRange(SoundCategories.All
            .Where(counts.ContainsKey)
            .Select(category => new CategoryItem(category, category, counts[category])));
        var first = CategoryList.ItemsSource is null;
        _updatingCategories = !first;
        CategoryList.ItemsSource = items;
        CategoryList.SelectedItem = items.FirstOrDefault(item => item.Category == keep) ?? items[0];
        _updatingCategories = false;
    }

    // The column the sounds are sorted by, so that sounds alike come together, or null for the search's own
    // order (sounds with the words in their name first). A second click on a column sorts the other way.
    private string? _sortColumn;
    private bool _sortDescending;

    private void SortHeader_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBlock { Tag: string column })
        {
            return;
        }

        _sortDescending = _sortColumn == column && !_sortDescending;
        _sortColumn = column;
        foreach (var header in SortHeaders.Children.OfType<TextBlock>())
        {
            var title = ((string)header.Tag).ToUpperInvariant();
            header.Text = (string)header.Tag == column ? $"{title} {(_sortDescending ? "▼" : "▲")}" : title;
        }

        ShowSounds();
        e.Handled = true;
    }

    private IEnumerable<SoundEntry> Sorted(IEnumerable<SoundEntry> sounds)
    {
        if (_sortColumn is null)
        {
            return sounds;
        }

        Func<SoundEntry, string> key = _sortColumn switch
        {
            "Category" => sound => sound.Category,
            "Instrument" => sound => sound.PluginName,
            "Bank" => sound => sound.Bank,
            "Designer" => sound => sound.Designer,
            _ => sound => sound.Name,
        };
        var sorted = _sortDescending
            ? sounds.OrderByDescending(key, StringComparer.OrdinalIgnoreCase)
            : sounds.OrderBy(key, StringComparer.OrdinalIgnoreCase);
        return sorted.ThenBy(sound => sound.Name, StringComparer.OrdinalIgnoreCase);
    }

    private void ShowSounds()
    {
        var category = (CategoryList.SelectedItem as CategoryItem)?.Category;
        var pool = category switch
        {
            FavoritesKey => _sounds.Where(_favorites.Contains),
            RecentKey => _recent.Of(_sounds),
            _ => _sounds,
        };
        var ownList = category is FavoritesKey or RecentKey;
        // The last used keep their order, the latest first, until a column is chosen or something is searched.
        var found = category == RecentKey && _sortColumn is null && string.IsNullOrWhiteSpace(SearchBox.Text)
            ? pool.ToList()
            : Sorted(SoundSearch.Filter(pool, SearchBox.Text, ownList ? null : category)).ToList();
        SoundList.ItemsSource = found;
        CountText.Text = $"{found.Count} sounds";
        if (found.Count > 0)
        {
            SoundList.SelectedIndex = 0;
            SoundList.ScrollIntoView(found[0]);
        }
    }

    // Searches all the sounds for these words, as if typed: a drum kit's piece asks for its drum's, and the sound
    // loaded then plays the piece.
    public void Search(string words, PatternTrackViewModel? kitPiece = null)
    {
        _kitPiece = kitPiece;
        ShowTarget();
        SearchBox.Text = words;
        SearchBox.CaretIndex = words.Length;
        if (CategoryList.ItemsSource is IReadOnlyList<CategoryItem> { Count: > 0 } items)
        {
            CategoryList.SelectedItem = items[0];
        }

        SearchBox.Focus();
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

    // A category clicked shows its sounds, and the arrows then go through them at once, without a click in
    // the list first. Chosen with the arrows, the focus stays in the categories, to go on through them.
    private void CategoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingCategories)
        {
            return;
        }

        ShowSounds();
        if (Mouse.LeftButton == MouseButtonState.Pressed)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, FocusSelectedSound);
        }
    }

    private void FocusSelectedSound()
    {
        if (SoundList.SelectedIndex < 0)
        {
            return;
        }

        SoundList.ScrollIntoView(SoundList.SelectedItem);
        SoundList.UpdateLayout();
        if (SoundList.ItemContainerGenerator.ContainerFromIndex(SoundList.SelectedIndex) is ListBoxItem row)
        {
            row.Focus();
        }
    }

    // With AUTO on, a sound is previewed once the selection rests on it a moment, so running through the
    // list with the arrows does not start every plug-in on the way.
    private readonly DispatcherTimer _autoPreviewDelay = new() { Interval = TimeSpan.FromMilliseconds(350) };

    private void SoundList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        LoadButton.IsEnabled = PreviewButton.IsEnabled = SoundList.SelectedItem is SoundEntry;
        _autoPreviewDelay.Stop();
        if (SoundList.SelectedItem is SoundEntry selected)
        {
            // The sounds after the selected one get ready as it is played, so the arrows go on at once.
            if (AutoPreviewToggle.IsChecked == true)
            {
                // A ready sound is heard at once; one not ready yet waits for the selection to rest a moment.
                _autoPreviewDelay.Interval = TimeSpan.FromMilliseconds(_viewModel.IsSoundReady(selected) ? 60 : 350);
                _autoPreviewDelay.Start();
            }

            ComeUpFrom(selected);
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

    // ---- Sounds got ready ahead ----

    // How many sounds after the one under the mouse, or the selected one, are got ready.
    private const int SoundsAhead = 3;

    public static readonly DependencyProperty IsComingUpProperty =
        DependencyProperty.RegisterAttached("IsComingUp", typeof(bool), typeof(SoundBrowserWindow), new PropertyMetadata(false));

    public static readonly DependencyProperty IsReadyProperty =
        DependencyProperty.RegisterAttached("IsReady", typeof(bool), typeof(SoundBrowserWindow), new PropertyMetadata(false));

    public static readonly DependencyProperty IsFavoriteProperty =
        DependencyProperty.RegisterAttached("IsFavorite", typeof(bool), typeof(SoundBrowserWindow), new PropertyMetadata(false));

    public static bool GetIsFavorite(DependencyObject element) => (bool)element.GetValue(IsFavoriteProperty);

    public static void SetIsFavorite(DependencyObject element, bool value) => element.SetValue(IsFavoriteProperty, value);

    // Right-click on a sound: add it to the favorites, or take it out.
    private void SoundList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var row = e.OriginalSource as DependencyObject;
        while (row is not null and not ListBoxItem)
        {
            row = System.Windows.Media.VisualTreeHelper.GetParent(row);
        }

        if (row is not ListBoxItem { DataContext: SoundEntry sound } item)
        {
            e.Handled = true;
            return;
        }

        item.IsSelected = true;
        var favorite = _favorites.Contains(sound);
        var toggle = new MenuItem { Header = favorite ? "Remove from favorites" : "★ Add to favorites" };
        toggle.Click += (_, _) =>
        {
            _favorites.Set(sound, !favorite);
            ShowCategories();
            if ((CategoryList.SelectedItem as CategoryItem)?.Category == FavoritesKey)
            {
                ShowSounds(); // a sound taken out leaves the favorites shown
            }

            MarkRows();
            StatusText.Text = favorite ? $"{sound.Name} taken out of the favorites" : $"{sound.Name} added to the favorites";
        };
        var preview = new MenuItem { Header = "▶ Preview" };
        preview.Click += (_, _) => PreviewSelected();
        var load = new MenuItem { Header = "Load on track" };
        load.Click += (_, _) => LoadSelected();
        SoundList.ContextMenu = new ContextMenu { Items = { toggle, new Separator(), preview, load } };
    }

    public static bool GetIsComingUp(DependencyObject element) => (bool)element.GetValue(IsComingUpProperty);

    public static void SetIsComingUp(DependencyObject element, bool value) => element.SetValue(IsComingUpProperty, value);

    public static bool GetIsReady(DependencyObject element) => (bool)element.GetValue(IsReadyProperty);

    public static void SetIsReady(DependencyObject element, bool value) => element.SetValue(IsReadyProperty, value);

    // The sound the mouse or the selection rests on, then the ones after it, lit in the list and got ready one
    // at a time while the window is idle, so the plug-ins starting do not hold up the mouse or the arrows.
    private List<SoundEntry> _comingUp = new();
    private readonly DispatcherTimer _hoverDelay = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private SoundEntry? _hovered;
    private bool _preparing;

    private void SoundRow_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: SoundEntry sound })
        {
            _hovered = sound;
            _hoverDelay.Stop();
            _hoverDelay.Start();
        }
    }

    private void SoundList_ScrollChanged(object sender, ScrollChangedEventArgs e) => MarkRows();

    // The sounds from this one on are the ones to get ready.
    private void ComeUpFrom(SoundEntry sound)
    {
        if (SoundList.ItemsSource is not List<SoundEntry> list)
        {
            return;
        }

        var index = list.IndexOf(sound);
        if (index < 0)
        {
            return;
        }

        _comingUp = list.Skip(index).Take(SoundsAhead + 1).ToList();
        // The selected sound stays wanted, so moving the mouse elsewhere does not take its instance away.
        var wanted = SoundList.SelectedItem is SoundEntry selected && !_comingUp.Contains(selected)
            ? _comingUp.Prepend(selected).ToList()
            : _comingUp;
        _viewModel.WantSoundsReady(wanted);
        MarkRows();
        PrepareNext();
    }

    // Gets the next sound coming up ready, then the next, each when the window has nothing else to do.
    private void PrepareNext()
    {
        if (_preparing || _comingUp.FirstOrDefault(sound => !_viewModel.IsSoundReady(sound)) is not { } next)
        {
            return;
        }

        _preparing = true;
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            try
            {
                if (_comingUp.Contains(next) && !_viewModel.IsSoundReady(next))
                {
                    _viewModel.PrepareSound(next);
                }
            }
            finally
            {
                _preparing = false;
            }

            MarkRows();
            PrepareNext();
        });
    }

    // Lights the rows coming up and dots the ready ones, on the rows the list has made.
    private void MarkRows()
    {
        foreach (var row in Rows(SoundList))
        {
            if (row.DataContext is SoundEntry sound)
            {
                SetIsComingUp(row, _comingUp.Contains(sound) && !ReferenceEquals(sound, SoundList.SelectedItem));
                SetIsReady(row, _viewModel.IsSoundReady(sound));
                SetIsFavorite(row, _favorites.Contains(sound));
            }
        }
    }

    private static IEnumerable<ListBoxItem> Rows(DependencyObject parent)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is ListBoxItem row)
            {
                yield return row;
                continue;
            }

            foreach (var inner in Rows(child))
            {
                yield return inner;
            }
        }
    }

    private void PreviewSelected()
    {
        if (SoundList.SelectedItem is not SoundEntry sound)
        {
            return;
        }

        var ready = _viewModel.IsSoundReady(sound);
        Mouse.OverrideCursor = ready ? null : Cursors.Wait; // a plug-in not running yet takes a moment to start
        try
        {
            _viewModel.PreviewSound(sound);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }

        StatusText.Text = _viewModel.StatusMessage;
        MarkRows();
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
            if (_kitPiece is { } piece)
            {
                // The next sound goes on the new track, in place of this one.
                if (_viewModel.ReplaceKitPiece(piece, sound) is not null)
                {
                    _kitPiece = null;
                }
            }
            else
            {
                _viewModel.LoadSound(sound);
            }
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }

        _recent.Used(sound);
        ShowCategories();

        StatusText.Text = _viewModel.StatusMessage;
        ShowTarget();
    }
}
