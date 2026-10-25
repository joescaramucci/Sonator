using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Sonator.ViewModels;

namespace Sonator;

// Chooses one of a plug-in's parameters to automate, from a list that can be long (thousands for some synths):
// typing narrows it, Enter or a double-click takes the one selected.
public sealed class ParameterPickerWindow : Window
{
    private readonly IReadOnlyList<AutomatedParameter> _parameters;
    private readonly TextBox _search;
    private readonly ListBox _list;

    public ParameterPickerWindow(string title, IReadOnlyList<AutomatedParameter> parameters)
    {
        _parameters = parameters;
        Title = title;
        Width = 420;
        Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        Background = new SolidColorBrush(Color.FromRgb(0x2B, 0x2C, 0x2E));
        Foreground = new SolidColorBrush(Color.FromRgb(0xE4, 0xE5, 0xE7));

        _search = new TextBox { Margin = new Thickness(8, 8, 8, 4), Padding = new Thickness(3, 2, 3, 2), FontSize = 13 };
        _search.TextChanged += (_, _) => Filter();
        _search.PreviewKeyDown += Search_PreviewKeyDown;

        _list = new ListBox
        {
            Margin = new Thickness(8, 0, 8, 4),
            Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1F, 0x21)),
            Foreground = Foreground,
            BorderThickness = new Thickness(1),
            DisplayMemberPath = nameof(AutomatedParameter.Name),
        };
        _list.MouseDoubleClick += (_, _) => Choose();
        _list.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Choose();
            }
        };

        var hint = new TextBlock
        {
            Margin = new Thickness(8, 0, 8, 8),
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0x98, 0x99, 0x9B)),
            Text = "Type to find a parameter; Enter or a double-click shows its automation lane.",
            TextWrapping = TextWrapping.Wrap,
        };

        var layout = new DockPanel();
        DockPanel.SetDock(_search, Dock.Top);
        DockPanel.SetDock(hint, Dock.Bottom);
        layout.Children.Add(_search);
        layout.Children.Add(hint);
        layout.Children.Add(_list);
        Content = layout;

        Loaded += (_, _) =>
        {
            WindowFrame.UseDarkFrame(this);
            Filter();
            _search.Focus();
        };
    }

    public AutomatedParameter? Chosen { get; private set; }

    private void Filter()
    {
        var words = _search.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var found = _parameters
            .Where(parameter => words.All(word => parameter.Name.Contains(word, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        _list.ItemsSource = found;
        if (found.Count > 0)
        {
            _list.SelectedIndex = 0;
        }
    }

    // Arrows move through the list and Enter takes the parameter, without leaving the search box.
    private void Search_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down when _list.SelectedIndex < _list.Items.Count - 1:
                _list.SelectedIndex++;
                _list.ScrollIntoView(_list.SelectedItem);
                e.Handled = true;
                break;
            case Key.Up when _list.SelectedIndex > 0:
                _list.SelectedIndex--;
                _list.ScrollIntoView(_list.SelectedItem);
                e.Handled = true;
                break;
            case Key.Enter:
                Choose();
                e.Handled = true;
                break;
            case Key.Escape:
                DialogResult = false;
                e.Handled = true;
                break;
        }
    }

    private void Choose()
    {
        if (_list.SelectedItem is AutomatedParameter parameter)
        {
            Chosen = parameter;
            DialogResult = true;
        }
    }
}
