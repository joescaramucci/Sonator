using System.Globalization;
using System.Windows.Media;

namespace Sonator.ViewModels;

// One icon of the instrument set: outlines drawn with a stroke and optional solid parts, on a 24x24 grid.
public sealed record InstrumentIcon(string Key, string Name, Geometry Strokes, Geometry? Fills);

// The icons a track can show under its name, in the order the picker lists them.
public static class InstrumentIcons
{
    public static IReadOnlyList<InstrumentIcon> All { get; } = new[]
    {
        Icon("piano", "Piano",
            "M3,6 H21 V18 H3 Z M7.5,12 V18 M12,12 V18 M16.5,12 V18",
            "M6.3,6 H8.7 V12 H6.3 Z M10.8,6 H13.2 V12 H10.8 Z M15.3,6 H17.7 V12 H15.3 Z"),
        Icon("grand-piano", "Grand piano",
            "M3,13 V9.5 C3,6 6,4.5 9,4.5 C12,4.5 13,7 16,7.5 C19,8 21,9.5 21,13 Z M3,13 V15 H21 V13 M5,15 V20 M19,15 V20 M12,15 V18"),
        Icon("organ", "Organ",
            "M2,21 H22 M2.8,19 V12 H5.2 V19 M6.8,19 V7.5 H9.2 V19 M10.8,19 V4 H13.2 V19 M14.8,19 V7.5 H17.2 V19 M18.8,19 V12 H21.2 V19 "
            + "M2.8,16.5 H5.2 M6.8,16.5 H9.2 M10.8,16.5 H13.2 M14.8,16.5 H17.2 M18.8,16.5 H21.2"),
        Icon("synth", "Synth",
            "M2,7 H22 V19 H2 Z M2,13 H22 M6,13 V19 M10,13 V19 M14,13 V19 M18,13 V19 M16,8.8 V11.2 M19,8.8 V11.2 "
            + Circle(5, 10, 1.1) + Circle(8.5, 10, 1.1) + Circle(12, 10, 1.1)),
        Icon("pad", "Pad",
            "M2,10 C5,5 8,5 11,10 C14,15 17,15 20,10 C20.8,8.7 21.4,8 22,7.8 M2,15 C5,10 8,10 11,15 C14,20 17,20 20,15 C20.8,13.7 21.4,13 22,12.8"),
        Icon("lead", "Lead",
            "M2,17 L8,7 V17 L14,7 V17 L20,7 V17 H22"),
        Icon("arp", "Arp",
            "M2.5,20.5 H6.5 V16 H10.5 V11.5 H14.5 V7 H18.5 V3.5 H21.5"),
        Icon("bass", "Bass",
            "M11.2,12.5 V3 M12.8,12.5 V3 M10.5,1 H13.5 V3 H10.5 Z M8.8,1.5 H10.5 M8.8,3 H10.5 M13.5,1.5 H15.2 M13.5,3 H15.2 "
            + "M10,12.5 C8.5,11.5 6.5,12 6.5,14 C6.5,15.5 7.2,16 6.8,17.5 C6.2,20 7.5,22.5 12,22.5 C16.5,22.5 17.8,20 17.2,17.5 "
            + "C16.8,16 17.5,15 17.2,13.5 C16.8,12 15.5,11.8 14,12.5 Z M9.5,17.5 H14.5"),
        Icon("guitar", "Guitar",
            "M10,10.5 C6.5,10.5 6,14 8,15 C4.5,16 4.5,22 12,22 C19.5,22 19.5,16 16,15 C18,14 17.5,10.5 14,10.5 Z "
            + "M11,10.5 V3.5 M13,10.5 V3.5 M10.3,1.5 H13.7 V3.5 H10.3 Z M10.5,19.5 H13.5 " + Circle(12, 15, 1.7)),
        Icon("electric-guitar", "E. guitar",
            "M11.2,11 V3 M12.8,11 V3 M10.5,1 H13.5 V3 H10.5 Z "
            + "M10,11 C8.5,9.5 6,10 6,12.5 C6,14 7,15 6.5,16.5 C5.8,19 7,22 12,22 C17,22 18.2,19 17.5,16.5 C17,15 18,13.5 17.5,12 "
            + "C17,10 15.5,9.8 14,11 Z M9.5,15 H14.5 M9.5,17.5 H14.5"),
        Icon("violin", "Violin",
            "M12,4 V9 M12,9 C9.5,9 8.3,10 8.7,11.8 C9,13 9.2,13.3 8.6,14.3 C7.3,16.8 8.3,21 12,21 C15.7,21 16.7,16.8 15.4,14.3 "
            + "C14.8,13.3 15,13 15.3,11.8 C15.7,10 14.5,9 12,9 Z M10.4,14 V16.6 M13.6,14 V16.6 M12,9 V18.5 M10.8,18.5 H13.2 "
            + Circle(12, 2.8, 1.2)),
        Icon("cello", "Cello",
            "M12,3.5 V7.5 M12,7.5 C8.8,7.5 7.4,8.6 7.8,10.8 C8.1,12.2 8.4,12.6 7.7,13.8 C6.2,16.6 7.2,20.5 12,20.5 "
            + "C16.8,20.5 17.8,16.6 16.3,13.8 C15.6,12.6 15.9,12.2 16.2,10.8 C16.6,8.6 15.2,7.5 12,7.5 Z "
            + "M10,13 V16 M14,13 V16 M12,7.5 V18 M10.6,18 H13.4 M12,20.5 V23 " + Circle(12, 2.3, 1.1)),
        Icon("harp", "Harp",
            "M5,21.5 V3 M5,3 C9,2 11,7 20,9.5 L7,21.5 M3.5,21.5 H8.5 M8.5,3.6 V20.1 M12,6.4 V16.9 M15.5,8.3 V13.6"),
        Icon("trumpet", "Trumpet",
            "M2,11 H4.5 M4.5,10 V12 M4.5,10.2 H15 M4.5,11.8 H15 M15,10.2 L21,6.5 V15.5 L15,11.8 "
            + "M7.5,10.2 V6.5 M10,10.2 V6.5 M12.5,10.2 V6.5 M6.8,6.5 H8.2 M9.3,6.5 H10.7 M11.8,6.5 H13.2 M7,11.8 C7,16 14,16 14,11.8"),
        Icon("sax", "Sax",
            "M5,3 L8.5,4 C11.3,4.8 12.5,6 12.5,8.5 V16 C12.5,19.6 14.5,21.5 17,21.5 C19.5,21.5 20.5,19.6 20.5,17 V13 M18.8,13 H22.2",
            Circle(10.9, 9.5, 0.8) + Circle(10.9, 12, 0.8) + Circle(10.9, 14.5, 0.8)),
        Icon("flute", "Flute",
            "M2.5,18.5 L18.5,4.5 L20,6 L4,20 Z",
            Circle(8.85, 14.35, 0.65) + Circle(11.25, 12.25, 0.65) + Circle(13.65, 10.15, 0.65) + Circle(16.05, 8.05, 0.65)),
        Icon("drums", "Drums",
            "M4,9 a8,3 0 1,0 16,0 a8,3 0 1,0 -16,0 M4,9 V17 M20,9 V17 M4,17 a8,3 0 0,0 16,0 "
            + "M4,12.5 L8,19.5 L12,12 L16,19.5 L20,12.5 M5,1.5 L10.5,6.5 M19,1.5 L13.5,6.5"),
        Icon("kick", "Kick",
            Circle(12, 11.5, 8.5) + Circle(12, 11.5, 3) + "M6.5,18 L4.5,22 M17.5,18 L19.5,22"),
        Icon("hi-hat", "Hi-hat",
            "M3,8 Q12,4.5 21,8 Z M3,11 Q12,14.5 21,11 Z M12,6.3 V3.5 M12,8 V11 M12,12.8 V22 M8,22 H16"),
        Icon("conga", "Conga",
            "M7,4.5 a5,1.6 0 1,0 10,0 a5,1.6 0 1,0 -10,0 M7,4.5 C6.2,10 8.8,12.5 8.5,15 L9.2,21.5 H14.8 L15.5,15 C15.2,12.5 17.8,10 17,4.5"),
        Icon("percussion", "Percussion",
            Circle(8, 7.5, 4) + Circle(16, 7.5, 4) + "M9.2,11.3 L11,21.5 M14.8,11.3 L13,21.5"),
        Icon("mallets", "Mallets",
            "M3,6 h3 v12 h-3 Z M7.5,7 h3 v10 h-3 Z M12,8 h3 v8 h-3 Z M16.5,9 h3 v6 h-3 Z M14.5,22 L20.2,15.8",
            Circle(21, 15, 1.4)),
        Icon("bells", "Bells",
            "M12,2.5 V4.5 M5.5,17.5 C7,15.5 7,13.5 7,10 C7,6.8 9.2,4.5 12,4.5 C14.8,4.5 17,6.8 17,10 C17,13.5 17,15.5 18.5,17.5 Z "
            + Circle(12, 19.6, 1.4)),
        Icon("vocals", "Vocals",
            "M9,6 A3,3 0 0,1 15,6 V11 A3,3 0 0,1 9,11 Z M6,10.5 A6,6 0 0,0 18,10.5 M12,16.5 V21 M8,21 H16 M9,8 H15"),
        Icon("choir", "Choir",
            Circle(6, 12, 2) + Circle(12, 10.3, 2.5) + Circle(18, 12, 2)
            + "M2.5,18.5 C2.5,15.5 9.5,15.5 9.5,18.5 M14.5,18.5 C14.5,15.5 21.5,15.5 21.5,18.5 M7,19.5 C7,12 17,12 17,19.5"),
        Icon("sampler", "Sampler",
            Square(3, 3) + Square(9.7, 3) + Square(16.4, 3) + Square(3, 9.7) + Square(16.4, 9.7)
            + Square(3, 16.4) + Square(9.7, 16.4) + Square(16.4, 16.4),
            Square(9.7, 9.7)),
        Icon("fx", "FX",
            "M12,3 L13.8,10.2 L21,12 L13.8,13.8 L12,21 L10.2,13.8 L3,12 L10.2,10.2 Z "
            + "M19,2.5 L19.6,4.4 L21.5,5 L19.6,5.6 L19,7.5 L18.4,5.6 L16.5,5 L18.4,4.4 Z"),
        Icon("audio", "Audio",
            "M3,10 V14 M6.5,7 V17 M10,4 V20 M13.5,8 V16 M17,5.5 V18.5 M20.5,9.5 V14.5"),
        Icon("note", "Note",
            "M10.4,18 V4 C11.5,6.5 17.5,7 17.5,11.5",
            "M4.5,18.5 a3,2.2 -20 1,0 6,0 a3,2.2 -20 1,0 -6,0"),
    };

    public static InstrumentIcon? Find(string? key)
    {
        return key is null ? null : All.FirstOrDefault(icon => icon.Key == key);
    }

    private static InstrumentIcon Icon(string key, string name, string strokes, string? fills = null)
    {
        return new InstrumentIcon(key, name, Parse(strokes), fills is null ? null : Parse(fills));
    }

    private static Geometry Parse(string data)
    {
        var geometry = Geometry.Parse(data);
        geometry.Freeze();
        return geometry;
    }

    private static string Circle(double cx, double cy, double r)
    {
        return string.Create(CultureInfo.InvariantCulture, $" M{cx - r},{cy} a{r},{r} 0 1,0 {2 * r},0 a{r},{r} 0 1,0 {-2 * r},0 ");
    }

    private static string Square(double x, double y)
    {
        return string.Create(CultureInfo.InvariantCulture, $" M{x},{y} h4.6 v4.6 h-4.6 Z ");
    }
}
