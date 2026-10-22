using System.Windows;
using Sonator.ViewModels;

namespace Sonator;

// Shows every instrument icon so one can be picked for a track. The track's current icon has a gold border.
public partial class InstrumentIconPickerWindow : Window
{
    public InstrumentIconPickerWindow(string trackName, string? currentKey)
    {
        Heading = $"Choose an icon for {trackName}";
        Choices = InstrumentIcons.All.Select(icon => new IconChoice(icon, icon.Key == currentKey)).ToList();
        InitializeComponent();
        WindowFrame.UseDarkFrame(this);
        DataContext = this;
    }

    public string Heading { get; }

    public IReadOnlyList<IconChoice> Choices { get; }

    // The picked icon's key, or null for no icon. Read it when ShowDialog returns true.
    public string? SelectedKey { get; private set; }

    private void IconChoice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: IconChoice choice })
        {
            SelectedKey = choice.Icon.Key;
            DialogResult = true;
        }
    }

    private void NoIcon_Click(object sender, RoutedEventArgs e)
    {
        SelectedKey = null;
        DialogResult = true;
    }

    public sealed record IconChoice(InstrumentIcon Icon, bool IsCurrent);
}
