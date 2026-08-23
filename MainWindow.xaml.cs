using System.Windows;
using System.Globalization;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Sonator.ViewModels;

namespace Sonator;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
        Closed += OnClosed;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (DataContext is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox)
        {
            return;
        }

        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        if (e.Key == Key.Space)
        {
            viewModel.TogglePlayback();
            e.Handled = true;
            return;
        }

        if (e.Key is Key.OemPeriod or Key.Decimal)
        {
            viewModel.RestartPlayback();
            e.Handled = true;
            return;
        }

        if (e.Key is Key.Left or Key.Right)
        {
            viewModel.MovePlaybackByBars(e.Key == Key.Left ? -1 : 1);
            e.Handled = true;
        }
    }

    private void PianoRollCell_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element)
        {
            return;
        }

        if (DataContext is not MainViewModel viewModel || element.DataContext is not PianoRollCellViewModel cell)
        {
            return;
        }

        if (e.ChangedButton == MouseButton.Right)
        {
            viewModel.PaintPianoRollCell(cell, false);
        }
        else
        {
            viewModel.PaintPianoRollCell(cell, true);
        }

        element.CaptureMouse();
        e.Handled = true;
    }

    private void PianoRollCell_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement element)
        {
            return;
        }

        if (DataContext is not MainViewModel viewModel || element.DataContext is not PianoRollCellViewModel cell)
        {
            return;
        }

        if (Mouse.LeftButton == MouseButtonState.Pressed)
        {
            viewModel.PaintPianoRollCell(cell, true);
        }
        else if (Mouse.RightButton == MouseButtonState.Pressed)
        {
            viewModel.PaintPianoRollCell(cell, false);
        }
    }

    private void PianoRollCell_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is UIElement element)
        {
            element.ReleaseMouseCapture();
        }
    }

    private void ArrangeCell_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element)
        {
            return;
        }

        if (DataContext is not MainViewModel viewModel || element.DataContext is not ArrangeCellViewModel cell)
        {
            return;
        }

        viewModel.SelectArrangeCell(cell);
        if (e.ChangedButton == MouseButton.Left && e.ClickCount == 2)
        {
            ShowArrangePatternEditorDialog(viewModel);
        }
        e.Handled = true;
    }

    private void ArrangeBlock_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element)
        {
            return;
        }

        if (DataContext is not MainViewModel viewModel || element.DataContext is not SongArrangeBlockViewModel block)
        {
            return;
        }

        viewModel.SelectArrangeBlock(block);
        e.Handled = true;
    }

    private void ArrangeBar_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element)
        {
            return;
        }

        if (DataContext is not MainViewModel viewModel || element.DataContext is not SongArrangeRowViewModel row)
        {
            return;
        }

        viewModel.SelectSongArrangeRow(row);
        e.Handled = true;
    }

    private void ArrangePatternRow_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element)
        {
            return;
        }

        if (DataContext is not MainViewModel viewModel || element.DataContext is not SongArrangeRowViewModel row)
        {
            return;
        }

        viewModel.SelectSongArrangeRow(row);
        if (e.ChangedButton == MouseButton.Left && e.ClickCount == 2)
        {
            ShowArrangePatternEditorDialog(viewModel);
        }
        e.Handled = true;
    }

    private void ShowArrangePatternEditorDialog(MainViewModel viewModel)
    {
        if (!viewModel.TryGetSelectedArrangePatternEditorState(out var patternName, out var assignmentNumber, out var subChannelIndex))
        {
            return;
        }

        var dialog = new ArrangePatternEditDialog(patternName, assignmentNumber, subChannelIndex)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        if (!viewModel.TryApplySelectedArrangePatternEditorState(
                dialog.PatternName,
                dialog.AssignmentNumber,
                dialog.SubChannelIndex,
                out var errorMessage))
        {
            MessageBox.Show(errorMessage, "Arrange Pattern", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ArrangePatternRow_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SongArrangeRowViewModel row })
        {
            row.IsPointerOver = true;
        }
    }

    private void ArrangePatternRow_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SongArrangeRowViewModel row })
        {
            row.IsPointerOver = false;
        }
    }

    private void EditableTextBox_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not TextBox { IsReadOnly: false, IsEnabled: true } textBox)
        {
            return;
        }

        var increment = e.Delta > 0 ? 1 : -1;
        if (int.TryParse(textBox.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var integerValue))
        {
            textBox.Text = (integerValue + increment).ToString(CultureInfo.CurrentCulture);
        }
        else if (double.TryParse(textBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var decimalValue))
        {
            textBox.Text = (decimalValue + increment).ToString("0.###", CultureInfo.CurrentCulture);
        }
        else
        {
            return;
        }

        textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        e.Handled = true;
    }

}
