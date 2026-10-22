using System.Windows;
using Sonator.Models;

namespace Sonator;

// Picks a time signature: one click on a common one, or any beats-per-bar and beat note from the two lists.
public partial class TimeSignatureWindow : Window
{
    public TimeSignatureWindow(Meter current)
    {
        InitializeComponent();
        WindowFrame.UseDarkFrame(this);
        DataContext = this;
        NumeratorBox.SelectedItem = current.Numerator;
        DenominatorBox.SelectedItem = current.Denominator;
        Selected = current;
    }

    public IReadOnlyList<Meter> Presets => Meter.Presets;

    public IReadOnlyList<int> Numerators => Meter.Numerators;

    public IReadOnlyList<int> Denominators => Meter.Denominators;

    // The chosen time signature. Read it when ShowDialog returns true.
    public Meter Selected { get; private set; }

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Meter meter })
        {
            Selected = meter;
            DialogResult = true;
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (NumeratorBox.SelectedItem is int numerator && DenominatorBox.SelectedItem is int denominator)
        {
            Selected = new Meter(numerator, denominator);
            DialogResult = true;
        }
    }
}
