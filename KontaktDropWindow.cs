using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Sonator.Services.Sounds;

namespace Sonator;

// After a new song, the Kontakt instruments chosen for its parts, to drag each into its track's Kontakt window:
// Kontakt takes an instrument no other way. Kontakt is open on those tracks already.
public sealed class KontaktDropWindow : Window
{
    private static readonly Brush Ink = Frozen(Color.FromRgb(0xE4, 0xE5, 0xE7));
    private static readonly Brush Faint = Frozen(Color.FromRgb(0x98, 0x99, 0x9B));
    private static readonly Brush Gold = Frozen(Color.FromRgb(0xE6, 0xC4, 0x5B));
    private static readonly Brush Tile = Frozen(Color.FromRgb(0x33, 0x33, 0x33));

    public KontaktDropWindow(IEnumerable<(string Part, SoundEntry Sound)> parts)
    {
        Title = "Load in Kontakt";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Topmost = true; // over the Kontakt windows the instruments are dropped on
        Background = Frozen(Color.FromRgb(0x2B, 0x2C, 0x2E));
        Foreground = Ink;

        var layout = new StackPanel { Margin = new Thickness(14) };
        layout.Children.Add(new TextBlock
        {
            Text = "Kontakt is open on these tracks. Drag each instrument onto its track's Kontakt window: Kontakt loads it there. "
                   + "Big libraries take a while.",
            Foreground = Faint,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        });
        foreach (var (part, sound) in parts)
        {
            layout.Children.Add(Row(part, sound));
        }

        Content = layout;
        Loaded += (_, _) => WindowFrame.UseDarkFrame(this);
    }

    // A part's instrument, dragged out as its file.
    private static FrameworkElement Row(string part, SoundEntry sound)
    {
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = $"{part.ToUpperInvariant()}  ←  {sound.Name}", Foreground = Gold, FontWeight = FontWeights.SemiBold });
        text.Children.Add(new TextBlock { Text = $"{sound.DesignerAndWeight} · {sound.Bank}", Foreground = Faint, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis });
        var row = new Border
        {
            Background = Tile,
            BorderBrush = Faint,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(0, 0, 0, 6),
            Cursor = Cursors.Hand,
            ToolTip = $"Drag onto the Kontakt window of {part}",
            Child = text,
        };
        row.MouseMove += (_, e) =>
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                DragDrop.DoDragDrop(row, new DataObject(DataFormats.FileDrop, new[] { sound.PresetPath }), DragDropEffects.Copy);
            }
        };
        return row;
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
