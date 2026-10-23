using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace Sonator;

/// <summary>
/// The "?" at the top right of every window that has help: resting the mouse on it shows what the window is
/// for and how to use it, in a card in Sonator's own look; a click keeps the card open until a click elsewhere.
/// It is laid over the window's content, so no window's layout has to make room for it. Hooked once for all
/// windows by Install, it finds each window's help in HelpTexts by the window's type.
/// </summary>
public static class HelpBadge
{
    public static void Install() =>
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnWindowLoaded));

    // The help of one part of a window, by its topic in HelpTexts: a "?" at the part's top right corner, as a
    // panel's title strip or a box of the top bar. IsSmall gives a smaller one, for the top bar's boxes.
    public static readonly DependencyProperty TopicProperty = DependencyProperty.RegisterAttached(
        "Topic", typeof(string), typeof(HelpBadge), new PropertyMetadata(null, OnTopicChanged));

    public static string? GetTopic(DependencyObject element) => (string?)element.GetValue(TopicProperty);

    public static void SetTopic(DependencyObject element, string? value) => element.SetValue(TopicProperty, value);

    public static readonly DependencyProperty IsSmallProperty = DependencyProperty.RegisterAttached(
        "IsSmall", typeof(bool), typeof(HelpBadge), new PropertyMetadata(false));

    public static bool GetIsSmall(DependencyObject element) => (bool)element.GetValue(IsSmallProperty);

    public static void SetIsSmall(DependencyObject element, bool value) => element.SetValue(IsSmallProperty, value);

    private static void OnTopicChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is FrameworkElement part)
        {
            part.Loaded -= OnPartLoaded;
            part.Loaded += OnPartLoaded;
        }
    }

    private static void OnPartLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement part
            || GetTopic(part) is not { } topic
            || HelpTexts.ForTopic(topic) is not { } help
            || AdornerLayer.GetAdornerLayer(part) is not { } layer
            || layer.GetAdorners(part)?.OfType<BadgeAdorner>().Any() == true)
        {
            return;
        }

        var small = GetIsSmall(part);
        layer.Add(new BadgeAdorner(part, help, small ? 13 : 15, small ? 2 : 5, atWindowCorner: false));
    }

    private static void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Window { Content: UIElement content } window
            || !ReferenceEquals(e.OriginalSource, window)
            || HelpTexts.For(window.GetType()) is not { } help
            || AdornerLayer.GetAdornerLayer(content) is not { } layer
            || layer.GetAdorners(content)?.OfType<BadgeAdorner>().Any() == true)
        {
            return;
        }

        layer.Add(new BadgeAdorner(content, help, 17, 3, atWindowCorner: true));
    }

    // The card: the window's name in amber over what it does, on a dark panel lit from the top.
    private static Border Card(HelpText help)
    {
        var text = new StackPanel { MaxWidth = 380 };
        text.Children.Add(new TextBlock
        {
            Text = help.Title,
            FontFamily = new FontFamily("Segoe UI"),
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            Foreground = Brush(0xFF, 0x96, 0x3A),
            Margin = new Thickness(0, 0, 0, 6),
        });
        foreach (var paragraph in help.Paragraphs)
        {
            text.Children.Add(new TextBlock
            {
                Text = paragraph,
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 12,
                LineHeight = 17,
                Foreground = Brush(0xD2, 0xD3, 0xD5),
                Margin = new Thickness(0, 0, 0, 5),
            });
        }

        var face = new LinearGradientBrush(Color.FromRgb(0x30, 0x32, 0x35), Color.FromRgb(0x22, 0x24, 0x26), 90);
        face.Freeze();
        return new Border
        {
            Background = face,
            BorderBrush = Brush(0x4A, 0x4C, 0x4F),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(14, 11, 14, 7),
            Margin = new Thickness(0, 0, 8, 8),
            Child = text,
            Effect = new DropShadowEffect { ShadowDepth = 3, BlurRadius = 10, Opacity = 0.55, Color = Colors.Black },
        };
    }

    private static SolidColorBrush Brush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private sealed class BadgeAdorner : Adorner
    {
        private readonly double Size;
        private readonly double Inset;
        private readonly bool _atWindowCorner;

        private readonly VisualCollection _children;
        private readonly ToggleButton _badge;
        private readonly Popup _pinned;

        public BadgeAdorner(UIElement adorned, HelpText help, double size, double inset, bool atWindowCorner)
            : base(adorned)
        {
            Size = size;
            Inset = inset;
            _atWindowCorner = atWindowCorner;
            _badge = new ToggleButton
            {
                Width = Size,
                Height = Size,
                Focusable = false,
                Cursor = Cursors.Help,
                Template = BadgeTemplate(),
                ToolTip = new ToolTip
                {
                    Content = Card(help),
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(0),
                    HasDropShadow = false,
                    Placement = PlacementMode.Bottom,
                    HorizontalOffset = -360,
                },
            };
            ToolTipService.SetInitialShowDelay(_badge, 150);
            ToolTipService.SetShowDuration(_badge, 120000);

            // A click keeps the card open, where the tooltip would close as the mouse leaves.
            _pinned = new Popup
            {
                Child = Card(help),
                PlacementTarget = _badge,
                Placement = PlacementMode.Bottom,
                HorizontalOffset = -360,
                StaysOpen = false,
                AllowsTransparency = true,
                PopupAnimation = PopupAnimation.Fade,
            };
            _badge.Checked += (_, _) =>
            {
                if (_badge.ToolTip is ToolTip tip)
                {
                    tip.IsOpen = false;
                }

                _pinned.IsOpen = true;
            };
            _badge.Unchecked += (_, _) => _pinned.IsOpen = false;
            _pinned.Closed += (_, _) => _badge.IsChecked = false;
            ToolTipService.SetIsEnabled(_badge, true);
            _pinned.Opened += (_, _) => ToolTipService.SetIsEnabled(_badge, false);
            _pinned.Closed += (_, _) => ToolTipService.SetIsEnabled(_badge, true);

            _children = new VisualCollection(this) { _badge };
        }

        protected override int VisualChildrenCount => _children.Count;

        protected override Visual GetVisualChild(int index) => _children[index];

        protected override Size MeasureOverride(Size constraint)
        {
            _badge.Measure(new Size(Size, Size));
            return AdornedElement.RenderSize;
        }

        // At the window's top right corner, past any margin the window's content keeps.
        protected override Size ArrangeOverride(Size finalSize)
        {
            var corner = new Point(AdornedElement.RenderSize.Width, 0);
            if (_atWindowCorner && VisualTreeHelper.GetParent(this) is AdornerLayer layer && layer.ActualWidth > 0)
            {
                var offset = AdornedElement.TranslatePoint(new Point(0, 0), layer);
                corner = new Point(layer.ActualWidth - offset.X, -offset.Y);
            }

            _badge.Arrange(new Rect(corner.X - Size - Inset, corner.Y + Inset, Size, Size));
            return finalSize;
        }

        // A round anthracite key with a light "?", amber when the mouse is on it or its card is open.
        private ControlTemplate BadgeTemplate()
        {
            var template = new ControlTemplate(typeof(ToggleButton));
            var root = new FrameworkElementFactory(typeof(Grid));

            var shadow = new FrameworkElementFactory(typeof(Ellipse));
            shadow.SetValue(Shape.FillProperty, Brush(0x0A, 0x0B, 0x0C));
            shadow.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 1, 0, -1));
            root.AppendChild(shadow);

            var face = new FrameworkElementFactory(typeof(Ellipse), "Face");
            var gradient = new LinearGradientBrush(Color.FromRgb(0x5C, 0x5E, 0x61), Color.FromRgb(0x3A, 0x3C, 0x3F), 90);
            gradient.Freeze();
            face.SetValue(Shape.FillProperty, gradient);
            face.SetValue(Shape.StrokeProperty, Brush(0x6E, 0x70, 0x73));
            face.SetValue(Shape.StrokeThicknessProperty, 0.8);
            root.AppendChild(face);

            var mark = new FrameworkElementFactory(typeof(TextBlock), "Mark");
            mark.SetValue(TextBlock.TextProperty, "?");
            mark.SetValue(TextBlock.FontFamilyProperty, new FontFamily("Segoe UI"));
            mark.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold);
            mark.SetValue(TextBlock.FontSizeProperty, Size >= 16 ? 11.0 : Size >= 14 ? 10.0 : 9.0);
            mark.SetValue(TextBlock.ForegroundProperty, Brush(0xE4, 0xE5, 0xE7));
            mark.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            mark.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            mark.SetValue(FrameworkElement.MarginProperty, new Thickness(0, -1, 0, 0));
            root.AppendChild(mark);

            template.VisualTree = root;
            var lit = new LinearGradientBrush(Color.FromRgb(0xFF, 0xB0, 0x60), Color.FromRgb(0xE0, 0x7A, 0x22), 90);
            lit.Freeze();
            foreach (var property in new[] { UIElement.IsMouseOverProperty, ToggleButton.IsCheckedProperty })
            {
                var trigger = new Trigger { Property = property, Value = true };
                trigger.Setters.Add(new Setter(Shape.FillProperty, lit, "Face"));
                trigger.Setters.Add(new Setter(TextBlock.ForegroundProperty, Brush(0x22, 0x18, 0x0C), "Mark"));
                template.Triggers.Add(trigger);
            }

            return template;
        }
    }
}
