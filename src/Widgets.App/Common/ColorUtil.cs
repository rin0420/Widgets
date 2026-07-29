using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Widgets.App.Common;

/// <summary>
/// Conversions between the "#AARRGGBB" strings used in the persisted theme and WinUI colors.
/// Parsing is deliberately forgiving so a hand-edited widgets.json can never crash the app.
/// </summary>
public static class ColorUtil
{
    /// <summary>Parses "#AARRGGBB", "#RRGGBB", "#ARGB" or "#RGB". Returns <paramref name="fallback"/> on anything else.</summary>
    public static Color Parse(string? value, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var s = value.Trim();
        if (s[0] == '#')
        {
            s = s[1..];
        }

        // Expand the shorthand forms by doubling each nibble: #RGB -> #RRGGBB.
        if (s.Length is 3 or 4)
        {
            s = string.Concat(s.Select(c => new string(c, 2)));
        }

        if (s.Length == 6)
        {
            s = "FF" + s;
        }

        if (s.Length != 8 || !uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var packed))
        {
            return fallback;
        }

        return Color.FromArgb(
            (byte)((packed >> 24) & 0xFF),
            (byte)((packed >> 16) & 0xFF),
            (byte)((packed >> 8) & 0xFF),
            (byte)(packed & 0xFF));
    }

    public static Color Parse(string? value) => Parse(value, Colors.White);

    public static string ToHex(Color color)
        => $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

    public static SolidColorBrush Brush(string? value, Color fallback)
        => new(Parse(value, fallback));

    public static SolidColorBrush Brush(string? value) => new(Parse(value));

    public static Color WithAlpha(Color color, byte alpha)
        => Color.FromArgb(alpha, color.R, color.G, color.B);

    /// <summary>Multiplies the existing alpha by <paramref name="factor"/> (0–1).</summary>
    public static Color Fade(Color color, double factor)
        => Color.FromArgb((byte)Math.Clamp(color.A * factor, 0, 255), color.R, color.G, color.B);

    /// <summary>
    /// Relative luminance per WCAG 2.1, used to pick readable foregrounds and to decide
    /// whether a swatch needs a light or dark check mark.
    /// </summary>
    public static double Luminance(Color color)
    {
        static double Channel(byte v)
        {
            var c = v / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
    }

    /// <summary>WCAG contrast ratio between two opaque colors, 1.0 – 21.0.</summary>
    public static double ContrastRatio(Color a, Color b)
    {
        var la = Luminance(a);
        var lb = Luminance(b);
        var (hi, lo) = la > lb ? (la, lb) : (lb, la);
        return (hi + 0.05) / (lo + 0.05);
    }

    /// <summary>Black or white, whichever reads better on <paramref name="background"/>.</summary>
    public static Color ReadableForeground(Color background)
        => Luminance(background) > 0.45 ? Colors.Black : Colors.White;
}
