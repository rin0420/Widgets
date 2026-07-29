namespace Widgets.App.Services;

/// <summary>
/// Pure math, no network: lunar phase and the NOAA/"sunrise equation" solar-position algorithm
/// (the same approach used by SunCalc), good to within a minute or two for widget purposes.
/// </summary>
public static class AstronomyCalculator
{
    private const double Rad = Math.PI / 180.0;
    private const double J1970 = 2440588.0;
    private const double J2000 = 2451545.0;
    private const double J0 = 0.0009;
    private static readonly double Obliquity = Rad * 23.4397;

    private static readonly string[] PhaseNames =
        ["新月", "三日月", "上弦の月", "十三夜月", "満月", "寝待月", "下弦の月", "有明月"];

    private static readonly string[] PhaseGlyphs =
        ["🌑", "🌒", "🌓", "🌔", "🌕", "🌖", "🌗", "🌘"];

    private const double SynodicMonthDays = 29.530588853;

    /// <summary>0.0 = new moon, 0.5 = full moon, wrapping back to 1.0 = new moon.</summary>
    public static double MoonPhase(DateTimeOffset utc)
    {
        // A known new moon (2000-01-06 18:14 UTC) anchors the synodic cycle.
        var reference = new DateTimeOffset(2000, 1, 6, 18, 14, 0, TimeSpan.Zero);
        var days = (utc.ToUniversalTime() - reference).TotalDays;
        var phase = days % SynodicMonthDays / SynodicMonthDays;
        return phase < 0 ? phase + 1.0 : phase;
    }

    public static string MoonPhaseName(double phase) => PhaseNames[BucketIndex(phase)];

    public static string MoonGlyph(double phase) => PhaseGlyphs[BucketIndex(phase)];

    private static int BucketIndex(double phase)
    {
        var normalized = phase % 1.0;
        if (normalized < 0)
        {
            normalized += 1.0;
        }

        return (int)Math.Round(normalized * 8.0) % 8;
    }

    /// <summary>
    /// Sunrise/sunset for <paramref name="localDate"/>'s calendar day at the given coordinates.
    /// Returns null during polar day or polar night, when the sun never crosses the horizon.
    /// </summary>
    public static (DateTimeOffset Sunrise, DateTimeOffset Sunset)? SunTimes(double lat, double lon, DateTimeOffset localDate)
    {
        var noon = new DateTimeOffset(localDate.Year, localDate.Month, localDate.Day, 12, 0, 0, localDate.Offset);

        var d = ToDays(noon);
        var lw = Rad * -lon;
        var phi = Rad * lat;

        var n = JulianCycle(d, lw);
        var meanSolarNoon = ApproxTransit(0, lw, n);

        var m = SolarMeanAnomaly(meanSolarNoon);
        var l = EclipticLongitude(m);
        var dec = Declination(l);

        var jNoon = SolarTransitJ(meanSolarNoon, m, l);

        // -0.833° accounts for atmospheric refraction and the sun's apparent radius.
        var h0 = -0.833 * Rad;
        var cosH = (Math.Sin(h0) - Math.Sin(phi) * Math.Sin(dec)) / (Math.Cos(phi) * Math.Cos(dec));
        if (cosH is < -1 or > 1)
        {
            return null;
        }

        var w = Math.Acos(cosH);
        var jSet = SolarTransitJ(ApproxTransit(w, lw, n), m, l);
        var jRise = jNoon - (jSet - jNoon);

        return (FromJulian(jRise), FromJulian(jSet));
    }

    private static double ToDays(DateTimeOffset date) => ToJulian(date) - J2000;

    private static double ToJulian(DateTimeOffset date)
        => date.ToUnixTimeMilliseconds() / 86400000.0 - 0.5 + J1970;

    private static DateTimeOffset FromJulian(double j)
    {
        var ms = (j + 0.5 - J1970) * 86400000.0;
        return DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(ms));
    }

    private static double JulianCycle(double d, double lw) => Math.Round(d - J0 - lw / (2 * Math.PI));

    private static double ApproxTransit(double ht, double lw, double n) => J0 + (ht + lw) / (2 * Math.PI) + n;

    private static double SolarTransitJ(double ds, double m, double l)
        => J2000 + ds + 0.0053 * Math.Sin(m) - 0.0069 * Math.Sin(2 * l);

    private static double SolarMeanAnomaly(double d) => Rad * (357.5291 + 0.98560028 * d);

    private static double EclipticLongitude(double m)
    {
        var c = Rad * (1.9148 * Math.Sin(m) + 0.02 * Math.Sin(2 * m) + 0.0003 * Math.Sin(3 * m));
        var p = Rad * 102.9372;
        return m + c + p + Math.PI;
    }

    private static double Declination(double l)
        => Math.Asin(Math.Sin(Obliquity) * Math.Sin(l));
}
