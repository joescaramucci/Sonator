using System.Windows;

namespace Sonator;

public enum SaveChoice
{
    Save,
    DontSave,
    Cancel
}

public partial class SonatorMessageDialog : Window
{
    public SonatorMessageDialog(string title, string message)
    {
        DialogTitle = title;
        Message = message;
        InitializeComponent();
        WindowFrame.UseDarkFrame(this);
        DataContext = this;
    }

    public string DialogTitle { get; }

    public string Message { get; }

    public SaveChoice Choice { get; private set; } = SaveChoice.Cancel;

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        Choice = SaveChoice.Save;
        DialogResult = true;
    }

    private void DontSaveButton_Click(object sender, RoutedEventArgs e)
    {
        Choice = SaveChoice.DontSave;
        DialogResult = true;
    }

    // Asks whether to save the project, with Save, Don't save and Cancel. Closing the dialog counts as Cancel.
    public static SaveChoice AskToSave(Window? owner, string title, string message)
    {
        var dialog = new SonatorMessageDialog(title, message);
        dialog.OkButton.Visibility = Visibility.Collapsed;
        dialog.SaveChoicePanel.Visibility = Visibility.Visible;
        if (owner is not null)
        {
            dialog.Owner = owner;
        }

        dialog.ShowDialog();
        return dialog.Choice;
    }

    public static void Show(Window? owner, string title, string message)
    {
        var dialog = new SonatorMessageDialog(title, message);
        if (owner is not null)
        {
            dialog.Owner = owner;
        }

        dialog.ShowDialog();
    }
}
