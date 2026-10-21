using System.Windows;

namespace Sonator;

public partial class ArrangePatternEditDialog : Window
{
    public ArrangePatternEditDialog(string patternName, string assignmentNumber, int subChannelIndex)
    {
        PatternName = patternName;
        AssignmentNumber = assignmentNumber;
        SubChannelIndex = subChannelIndex;
        InitializeComponent();
        WindowFrame.UseDarkFrame(this);
        DataContext = this;
        Loaded += (_, _) => PatternNameTextBox.Focus();
    }

    public string PatternName { get; set; }

    public string AssignmentNumber { get; set; }

    public int SubChannelIndex { get; set; }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
