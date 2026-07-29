using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Widgets.App.Common;
using Widgets.App.Models;
using Windows.UI;
using Windows.UI.Text;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace Widgets.App.Widgets;

/// <summary>
/// Typography and color plumbing shared by every renderer, so a theme change lands
/// identically across all twelve widgets.
/// </summary>
internal static class WidgetVisuals
{
    public const string DefaultFontFamily = "Segoe UI Variable Display";

    public static readonly FontFamily IconFont = new("Segoe Fluent Icons");

    public static readonly string[] Weekdays = ["日", "月", "火", "水", "木", "金", "土"];

    public static readonly Color SundayColor = Color.FromArgb(255, 255, 122, 122);

    public static readonly Color SaturdayColor = Color.FromArgb(255, 122, 176, 255);

    public static readonly Color WarningColor = Color.FromArgb(255, 255, 168, 82);

    public static readonly Color DangerColor = Color.FromArgb(255, 255, 99, 99);

    private static readonly Dictionary<string, FontFamily> FontCache = new(StringComparer.OrdinalIgnoreCase);

    public static FontFamily Font(WidgetTheme theme)
    {
        var name = string.IsNullOrWhiteSpace(theme.FontFamily) ? DefaultFontFamily : theme.FontFamily.Trim();
        if (!FontCache.TryGetValue(name, out var family))
        {
            family = new FontFamily(name);
            FontCache[name] = family;
        }

        return family;
    }

    public static FontWeight Weight(WidgetTheme theme, int delta = 0)
        => new() { Weight = (ushort)Math.Clamp(theme.FontWeight + delta, 100, 900) };

    /// <summary>Applies the theme's type scale to a renderer's intrinsic font size.</summary>
    public static double Size(WidgetTheme theme, double intrinsic)
        => Math.Max(6.0, intrinsic * Math.Clamp(theme.FontScale, 0.5, 2.0));

    public static Color Tint(WidgetTheme theme)
        => ColorUtil.Parse(theme.TintColor, Colors.White);

    public static Color Accent(WidgetTheme theme)
        => ColorUtil.Parse(theme.AccentColor, Color.FromArgb(255, 76, 194, 255));

    public static Color Secondary(WidgetTheme theme)
        => ColorUtil.Parse(theme.SecondaryColor, Color.FromArgb(153, 255, 255, 255));

    public static Color Background(WidgetTheme theme)
        => ColorUtil.Parse(theme.BackgroundColor, Color.FromArgb(229, 32, 32, 32));

    public static SolidColorBrush TintBrush(WidgetTheme theme) => new(Tint(theme));

    public static SolidColorBrush AccentBrush(WidgetTheme theme) => new(Accent(theme));

    public static SolidColorBrush SecondaryBrush(WidgetTheme theme) => new(Secondary(theme));

    public static SolidColorBrush Brush(Color color) => new(color);

    public static Color Mix(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromArgb(
            (byte)(a.A + (b.A - a.A) * t),
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));
    }

    /// <summary>A faint wash of the tint color, for gauge tracks and grid rules.</summary>
    public static Color Track(WidgetTheme theme)
        => ColorUtil.Fade(Secondary(theme), 0.28);

    public static void Style(TextBlock text, WidgetTheme theme, double intrinsic, Color color, int weightDelta = 0)
    {
        text.FontFamily = Font(theme);
        text.FontSize = Size(theme, intrinsic);
        text.FontWeight = Weight(theme, weightDelta);
        text.Foreground = new SolidColorBrush(color);
        text.LineHeight = 0;
    }

    public static TextBlock Text(WidgetTheme theme, double intrinsic, Color color, int weightDelta = 0)
    {
        var text = new TextBlock
        {
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
        };

        Style(text, theme, intrinsic, color, weightDelta);
        return text;
    }

    /// <summary>
    /// Picks the right font for <paramref name="glyph"/>: the icon font for private-use
    /// codepoints, the theme font (which falls back to the emoji font) for anything else.
    /// </summary>
    public static TextBlock AutoGlyph(WidgetTheme theme, string glyph, double size, Color color)
    {
        var isIcon = glyph.Length > 0 && glyph[0] is >= '\uE000' and <= '\uF8FF';
        if (isIcon)
        {
            return Glyph(glyph, size, color);
        }

        var text = Glyph(glyph, size, color);
        text.FontFamily = Font(theme);
        text.IsColorFontEnabled = true;
        return text;
    }

    public static TextBlock Glyph(string glyph, double size, Color color) => new()
    {
        Text = glyph,
        FontFamily = IconFont,
        FontSize = size,
        Foreground = new SolidColorBrush(color),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        IsColorFontEnabled = false,
    };

    /// <summary>Wraps <paramref name="child"/> so it shrinks — never grows — to fit the widget.</summary>
    public static Viewbox Shrink(FrameworkElement child) => new()
    {
        Child = child,
        Stretch = Stretch.Uniform,
        StretchDirection = StretchDirection.DownOnly,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    public static string WeekdayJa(DayOfWeek day) => Weekdays[(int)day];

    // ---- Gauges ---------------------------------------------------------------

    public static Ellipse RingTrack(double size, double thickness, Color color) => new()
    {
        Width = size,
        Height = size,
        Stroke = new SolidColorBrush(color),
        StrokeThickness = thickness,
    };

    public static Path Ring(double size, double thickness, Color color)
    {
        var path = new Path
        {
            Width = size,
            Height = size,
            Stroke = new SolidColorBrush(color),
            StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        };

        SetRing(path, size, thickness, 0);
        return path;
    }

    public static void SetRing(Path path, double size, double thickness, double fraction)
    {
        fraction = Math.Clamp(fraction, 0, 1);
        path.Visibility = fraction < 0.004 ? Visibility.Collapsed : Visibility.Visible;

        var radius = Math.Max(0.5, (size - thickness) / 2);
        var center = size / 2;

        // A full turn would put the arc's end point on its start point, which renders as nothing.
        var sweep = fraction * 359.99;
        var angle = (sweep - 90) * Math.PI / 180.0;

        var figure = new PathFigure
        {
            StartPoint = new Windows.Foundation.Point(center, center - radius),
            IsClosed = false,
            IsFilled = false,
        };

        figure.Segments.Add(new ArcSegment
        {
            Point = new Windows.Foundation.Point(
                center + radius * Math.Cos(angle),
                center + radius * Math.Sin(angle)),
            Size = new Windows.Foundation.Size(radius, radius),
            IsLargeArc = sweep > 180,
            SweepDirection = SweepDirection.Clockwise,
        });

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        path.Data = geometry;
    }

    /// <summary>A rounded track with a fill child; call <see cref="SetBar"/> to update it.</summary>
    public static Grid Bar(double width, double height, Color track, Color fill)
    {
        var host = new Grid
        {
            Width = width,
            Height = height,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        host.Children.Add(new Border
        {
            Background = new SolidColorBrush(track),
            CornerRadius = new CornerRadius(height / 2),
        });

        host.Children.Add(new Border
        {
            Background = new SolidColorBrush(fill),
            CornerRadius = new CornerRadius(height / 2),
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = 0,
        });

        return host;
    }

    public static void SetBar(Grid bar, double fraction)
    {
        if (bar.Children.Count > 1 && bar.Children[1] is Border fill)
        {
            fill.Width = Math.Max(0, bar.Width * Math.Clamp(fraction, 0, 1));
        }
    }

    public static Polyline Spark(double width, double height, Color color, double thickness = 1.8) => new()
    {
        Width = width,
        Height = height,
        Stroke = new SolidColorBrush(color),
        StrokeThickness = thickness,
        StrokeLineJoin = PenLineJoin.Round,
    };

    /// <summary>Plots <paramref name="samples"/> (0–1) left-to-right, oldest first.</summary>
    public static void SetSpark(Polyline line, IReadOnlyList<double> samples, int capacity)
        => line.Points = Plot(line.Width, line.Height, samples, capacity);

    /// <summary>A filled counterpart to <see cref="Spark"/>, for graphs that want body under the line.</summary>
    public static Polygon Area(double width, double height, Color color, double opacity = 0.28) => new()
    {
        Width = width,
        Height = height,
        Fill = new SolidColorBrush(ColorUtil.Fade(color, opacity)),
    };

    /// <summary>Plots <paramref name="samples"/> like <see cref="SetSpark"/>, closed along the baseline.</summary>
    public static void SetArea(Polygon area, IReadOnlyList<double> samples, int capacity)
    {
        var points = Plot(area.Width, area.Height, samples, capacity);
        if (points.Count >= 2)
        {
            // Drop down to the baseline and run back to the first sample so the shape closes.
            points.Add(new Windows.Foundation.Point(points[^1].X, area.Height));
            points.Add(new Windows.Foundation.Point(points[0].X, area.Height));
        }
        else
        {
            points.Clear();
        }

        area.Points = points;
    }

    /// <summary>
    /// Maps 0–1 samples onto a polyline spanning the full width, newest sample at the right edge.
    /// </summary>
    private static PointCollection Plot(double width, double height, IReadOnlyList<double> samples, int capacity)
    {
        var points = new PointCollection();
        if (samples.Count == 0)
        {
            return points;
        }

        var slots = Math.Max(2, capacity);
        var take = Math.Min(samples.Count, slots);

        // Index of the oldest sample that fits in the window; a longer history keeps its newest end.
        var first = samples.Count - take;

        // A just-placed widget has one or two samples, and drawing only those would leave the graph
        // as a stub in the right-hand corner until the buffer fills a minute later. Holding the
        // oldest reading across the empty part spans the full width immediately, and real history
        // still scrolls in from the right.
        var pad = slots - take;
        var step = width / (slots - 1);

        for (var i = 0; i < slots; i++)
        {
            var sample = samples[first + Math.Max(0, i - pad)];
            var y = height - Math.Clamp(sample, 0, 1) * (height - 2) - 1;
            points.Add(new Windows.Foundation.Point(i * step, y));
        }

        return points;
    }

    /// <summary>A bottom-anchored bar, for the per-core columns in the CPU widget.</summary>
    public static Grid VBar(double width, double height, Color track, Color fill)
    {
        var radius = Math.Min(width, height) / 2;

        var host = new Grid
        {
            Width = width,
            Height = height,
            VerticalAlignment = VerticalAlignment.Bottom,
        };

        host.Children.Add(new Border
        {
            Background = new SolidColorBrush(track),
            CornerRadius = new CornerRadius(radius),
        });

        host.Children.Add(new Border
        {
            Background = new SolidColorBrush(fill),
            CornerRadius = new CornerRadius(radius),
            VerticalAlignment = VerticalAlignment.Bottom,
            Height = 0,
        });

        return host;
    }

    public static void SetVBar(Grid bar, double fraction)
    {
        if (bar.Children.Count > 1 && bar.Children[1] is Border fill)
        {
            fill.Height = Math.Max(0, bar.Height * Math.Clamp(fraction, 0, 1));
        }
    }

    public static void SetVBarColor(Grid bar, Color fill) => SetBarColor(bar, fill);

    /// <summary>Recolors the fill of a <see cref="Bar"/> or <see cref="VBar"/> in place.</summary>
    public static void SetBarColor(Grid bar, Color fill)
    {
        if (bar.Children.Count > 1 && bar.Children[1] is Border { Background: SolidColorBrush brush })
        {
            brush.Color = fill;
        }
    }

    // ---- Formatting -----------------------------------------------------------

    /// <summary>
    /// Gauge color for a load level: accent normally, warning past 75%, danger past 90%.
    /// Pass <paramref name="enabled"/> = false to keep the theme accent at every level.
    /// </summary>
    public static Color LoadColor(WidgetTheme theme, double fraction, bool enabled = true)
        => !enabled ? Accent(theme)
            : fraction >= 0.90 ? DangerColor
            : fraction >= 0.75 ? WarningColor
            : Accent(theme);

    /// <summary>Network throughput, in the bits-per-second units link speeds are quoted in.</summary>
    public static string FormatBitrate(double kbps)
    {
        kbps = Math.Max(0, kbps);
        return kbps >= 1_000_000 ? Fixed(kbps / 1_000_000, 2) + " Gbps"
            : kbps >= 1_000 ? Fixed(kbps / 1_000, 1) + " Mbps"
            : Fixed(kbps, 0) + " kbps";
    }

    /// <summary>Disk and network throughput in bytes, the unit file transfers are measured in.</summary>
    public static string FormatByteRate(double bytesPerSec)
    {
        const double kb = 1024.0;
        bytesPerSec = Math.Max(0, bytesPerSec);

        return bytesPerSec >= kb * kb * kb ? Fixed(bytesPerSec / (kb * kb * kb), 2) + " GB/s"
            : bytesPerSec >= kb * kb ? Fixed(bytesPerSec / (kb * kb), 1) + " MB/s"
            : Fixed(bytesPerSec / kb, 0) + " KB/s";
    }

    /// <summary>Capacity, scaled down to MB or up to TB so it always reads as 3–4 characters.</summary>
    public static string FormatGb(double gb)
    {
        gb = Math.Max(0, gb);
        return gb >= 1024 ? Fixed(gb / 1024, 2) + " TB"
            : gb >= 100 ? Fixed(gb, 0) + " GB"
            : gb >= 1 ? Fixed(gb, 1) + " GB"
            : Fixed(gb * 1024, 0) + " MB";
    }

    public static string FormatClock(double mhz)
        => mhz >= 1000 ? Fixed(mhz / 1000, 2) + " GHz" : Fixed(Math.Max(0, mhz), 0) + " MHz";

    public static string FormatUptime(TimeSpan uptime)
    {
        if (uptime < TimeSpan.Zero)
        {
            uptime = TimeSpan.Zero;
        }

        return uptime.Days > 0 ? $"{uptime.Days}日 {uptime.Hours}時間"
            : uptime.Hours > 0 ? $"{uptime.Hours}時間{uptime.Minutes}分"
            : $"{uptime.Minutes}分";
    }

    private static string Fixed(double value, int decimals)
        => value.ToString("F" + decimals.ToString(System.Globalization.CultureInfo.InvariantCulture),
                          System.Globalization.CultureInfo.InvariantCulture);

    public static double CelsiusToDisplay(double celsius, bool metric)
        => metric ? celsius : celsius * 9.0 / 5.0 + 32.0;

    public static string TemperatureUnit(bool metric) => metric ? "°C" : "°F";

    /// <summary>Reads the shared document defensively — renderers also run in the editor preview.</summary>
    public static AppSettings AppSettings()
    {
        try
        {
            return Services.AppServices.Store.Document.Settings;
        }
        catch (Exception ex)
        {
            Crash.Log(ex, "WidgetVisuals.AppSettings");
            return new AppSettings();
        }
    }
}
