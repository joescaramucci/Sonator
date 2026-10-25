using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Sonator;

// The folders whose samples (WAV, AIFF, FLAC, MP3, OGG) and Kontakt instruments the sound browser lists:
// added and taken out here. Read back from Folders once the window closes with SAVE.
public sealed class SoundFoldersWindow : Window
{
    private static readonly Brush Ink = Frozen(Color.FromRgb(0xE4, 0xE5, 0xE7));
    private static readonly Brush Faint = Frozen(Color.FromRgb(0x98, 0x99, 0x9B));
    private static readonly Brush Field = Frozen(Color.FromRgb(0x1E, 0x1F, 0x21));

    private readonly ListBox _list = new()
    {
        Height = 220,
        Background = Field,
        Foreground = Ink,
        BorderBrush = Faint,
        FontFamily = new FontFamily("Consolas"),
    };

    public SoundFoldersWindow(IEnumerable<string> folders)
    {
        Title = "Sound folders";
        Width = 700;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = Frozen(Color.FromRgb(0x2B, 0x2C, 0x2E));
        Foreground = Ink;
        foreach (var folder in folders)
        {
            _list.Items.Add(folder);
        }

        var add = Button("ADD FOLDER...", (_, _) => AddFolder());
        var remove = Button("REMOVE", (_, _) =>
        {
            if (_list.SelectedItem is { } selected)
            {
                _list.Items.Remove(selected);
            }
        });
        var save = Button("SAVE", (_, _) => DialogResult = true);
        save.IsDefault = true;
        var cancel = Button("CANCEL", (_, _) => DialogResult = false);
        cancel.IsCancel = true;

        var buttons = new DockPanel { Margin = new Thickness(0, 10, 0, 0), LastChildFill = false };
        DockPanel.SetDock(add, Dock.Left);
        DockPanel.SetDock(remove, Dock.Left);
        DockPanel.SetDock(cancel, Dock.Right);
        DockPanel.SetDock(save, Dock.Right);
        buttons.Children.Add(add);
        buttons.Children.Add(remove);
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);

        var layout = new StackPanel { Margin = new Thickness(14) };
        layout.Children.Add(new TextBlock
        {
            Text = "Your samples and Kontakt libraries: every WAV, AIFF, FLAC, MP3 and OGG file and every Kontakt instrument (.nki) "
                   + "in these folders and the folders inside them is listed in Sounds, filed by its name and folders. "
                   + "Big folders take a moment to read the first time Sounds opens.",
            Foreground = Faint,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        });
        layout.Children.Add(_list);
        layout.Children.Add(buttons);
        Content = layout;
        Loaded += (_, _) => WindowFrame.UseDarkFrame(this);
    }

    public IReadOnlyList<string> Folders => _list.Items.Cast<string>().ToList();

    private void AddFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "A folder of samples or Kontakt libraries", Multiselect = true };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        foreach (var folder in dialog.FolderNames)
        {
            // A folder already listed, or inside one listed, is read already.
            if (!Folders.Any(listed => IsInside(folder, listed)))
            {
                _list.Items.Add(folder);
            }
        }
    }

    private static bool IsInside(string folder, string listed)
    {
        var full = Path.GetFullPath(folder).TrimEnd('\\') + "\\";
        var outer = Path.GetFullPath(listed).TrimEnd('\\') + "\\";
        return full.StartsWith(outer, StringComparison.OrdinalIgnoreCase);
    }

    private static Button Button(string text, RoutedEventHandler click)
    {
        var button = new Button
        {
            Content = text,
            MinWidth = 96,
            Height = 26,
            Margin = new Thickness(0, 0, 6, 0),
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
}
