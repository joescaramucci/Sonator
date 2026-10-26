using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Sonator;

// A long task's progress, with what it is doing and STOP, which cancels it.
public sealed class ProgressWindow : Window
{
    private static readonly Brush Ink = Frozen(Color.FromRgb(0xE4, 0xE5, 0xE7));
    private static readonly Brush Faint = Frozen(Color.FromRgb(0x98, 0x99, 0x9B));

    private readonly CancellationTokenSource _cancellation = new();
    private readonly TextBlock _text = new() { Foreground = Ink, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
    private readonly ProgressBar _bar = new() { Height = 14, Minimum = 0, Maximum = 1, Foreground = Frozen(Color.FromRgb(0x49, 0xB8, 0x8D)) };
    private readonly TextBlock _elapsed = new() { Foreground = Faint, FontSize = 11, Margin = new Thickness(0, 6, 0, 0) };
    private readonly DateTime _started = DateTime.Now;

    public ProgressWindow(string title)
    {
        Title = title;
        Width = 520;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = Frozen(Color.FromRgb(0x2B, 0x2C, 0x2E));

        var stop = new Button
        {
            Content = "STOP",
            MinWidth = 96,
            Height = 26,
            Margin = new Thickness(0, 10, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            Background = Frozen(Color.FromRgb(0x41, 0x41, 0x41)),
            Foreground = Ink,
            BorderBrush = Frozen(Color.FromRgb(0x5C, 0x7A, 0x99)),
        };
        stop.Click += (_, _) =>
        {
            _cancellation.Cancel();
            _text.Text = "Stopping...";
            stop.IsEnabled = false;
        };

        var layout = new StackPanel { Margin = new Thickness(14) };
        layout.Children.Add(_text);
        layout.Children.Add(_bar);
        layout.Children.Add(_elapsed);
        layout.Children.Add(stop);
        Content = layout;
        Loaded += (_, _) => WindowFrame.UseDarkFrame(this);
        Closing += (_, _) => _cancellation.Cancel(); // closed with its X, the task stops too
    }

    public CancellationToken Cancellation => _cancellation.Token;

    // What is being done, and how much of it (0 to 1).
    public void Report(string text, double done)
    {
        if (_cancellation.IsCancellationRequested)
        {
            return;
        }

        _text.Text = text;
        _bar.Value = Math.Clamp(done, 0, 1);
        var elapsed = DateTime.Now - _started;
        var left = done > 0.02 ? TimeSpan.FromSeconds(elapsed.TotalSeconds * (1 - done) / done) : (TimeSpan?)null;
        _elapsed.Text = $"{elapsed:m\\:ss} elapsed" + (left is { } remaining ? $", about {remaining:m\\:ss} left" : string.Empty);
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
