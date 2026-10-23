using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Sonator;

/// <summary>
/// The lamp under a hardware button's cap, as on SSL's panels: the cap is always light grey metal, and a button
/// that is on glows in its colour. A button is on when its Background is a lively colour (the green, red or
/// yellow its style gives it when switched on) or, for a switch, when it is checked; the unlit blue-greys light
/// nothing. Values: Background, then IsChecked, then (for "ink") Tag. The parameter says what to give: "brush"
/// (the lamp's colour, null when off), "color" (for its glow), "opacity" (1 when lit, else 0) or "ink" (the
/// label's colour: a soft white on the dark face, dark when lit or on a light cap, Tag "LightCap").
/// </summary>
public sealed class LampConverter : IMultiValueConverter
{
    // A switch that is on with no colour of its own glows amber, as SSL's.
    private static readonly Color Amber = Color.FromRgb(0xFF, 0x96, 0x3A);

    private static readonly Brush DarkInk = Frozen(Color.FromRgb(0x22, 0x22, 0x22));
    private static readonly Brush LightInk = Frozen(Color.FromRgb(0xCF, 0xD0, 0xD2));

    public object? Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var lamp = LampColour(values.ElementAtOrDefault(0) as Brush, values.ElementAtOrDefault(1) as bool?);
        return (parameter as string) switch
        {
            "color" => lamp ?? Colors.Transparent,
            "opacity" => lamp is null ? 0.0 : 1.0,
            "ink" => lamp is not null || Equals(values.ElementAtOrDefault(2), "LightCap") ? DarkInk : LightInk,
            _ => lamp is { } colour ? Frozen(colour) : null,
        };
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    internal static Color? LampColour(Brush? background, bool? isChecked)
    {
        if (background is SolidColorBrush { Color: var colour } && IsLively(colour))
        {
            return colour;
        }

        return isChecked == true ? Amber : null;
    }

    // Saturated and not too dark: a state's colour, not one of the panel's blue-greys.
    internal static bool IsLively(Color colour)
    {
        var max = Math.Max(colour.R, Math.Max(colour.G, colour.B)) / 255.0;
        var min = Math.Min(colour.R, Math.Min(colour.G, colour.B)) / 255.0;
        var saturation = max <= 0 ? 0 : (max - min) / max;
        return colour.A > 0x80 && saturation >= 0.5 && max >= 0.4;
    }

    private static Brush Frozen(Color colour)
    {
        var brush = new SolidColorBrush(colour);
        brush.Freeze();
        return brush;
    }
}
