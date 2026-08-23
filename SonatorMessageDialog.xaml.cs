using System.Windows;

namespace Sonator;

public partial class SonatorMessageDialog : Window
{
    public SonatorMessageDialog(string title, string message)
    {
        DialogTitle = title;
        Message = message;
        InitializeComponent();
        DataContext = this;
    }

    public string DialogTitle { get; }

    public string Message { get; }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
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
