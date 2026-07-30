using System.Globalization;
using System.Text.Json.Serialization;

namespace Widgets.App.Models;

/// <summary>
/// One placed widget: what it shows, how it looks, and where it lives on the desktop.
/// This is the unit of persistence — the whole app state is a list of these plus <see cref="AppSettings"/>.
/// </summary>
public sealed class WidgetDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public WidgetKind Kind { get; set; } = WidgetKind.DigitalClock;

    /// <summary>User-facing label shown in the manager list. Defaults to the catalog display name.</summary>
    public string Name { get; set; } = string.Empty;

    public WidgetSize Size { get; set; } = WidgetSize.Small;

    /// <summary>Footprint in logical pixels when <see cref="Size"/> is <see cref="WidgetSize.Custom"/>.</summary>
    public double CustomWidth { get; set; } = 176;

    public double CustomHeight { get; set; } = 176;

    /// <summary>Position in physical (device) pixels on the virtual screen, so multi-monitor DPI mixes stay stable.</summary>
    public int X { get; set; }

    public int Y { get; set; }

    /// <summary>Extra size multiplier on top of <see cref="Size"/>, 0.5 – 3.0.</summary>
    public double Scale { get; set; } = 1.0;

    /// <summary>When locked the widget cannot be dragged or resized.</summary>
    public bool Locked { get; set; }

    /// <summary>When false the widget window is not shown, but the definition is kept.</summary>
    public bool IsVisible { get; set; } = true;

    public ZOrderMode ZOrder { get; set; } = ZOrderMode.Desktop;

    /// <summary>When true mouse input passes straight through to whatever is underneath.</summary>
    public bool ClickThrough { get; set; }

    public WidgetTheme Theme { get; set; } = WidgetTheme.CreateDefault();

    /// <summary>Per-kind configuration. Keys are defined by each widget renderer; see <c>WidgetSettingKeys</c>.</summary>
    public Dictionary<string, string> Settings { get; set; } = new();

    /// <summary>
    /// Widgetsmith-style "timed widget": when non-empty, the widget swaps to a different
    /// kind/settings during the listed time ranges.
    /// </summary>
    public List<TimedSlot> TimedSlots { get; set; } = new();

    public WidgetDefinition Clone()
    {
        var copy = (WidgetDefinition)MemberwiseClone();
        copy.Theme = Theme.Clone();
        copy.Settings = new Dictionary<string, string>(Settings);
        copy.TimedSlots = TimedSlots.Select(s => s.Clone()).ToList();
        return copy;
    }

    // ---- Typed settings access -------------------------------------------------
    // Settings are stored as strings so unknown keys survive round-tripping through
    // older/newer builds. These helpers use InvariantCulture so a widget configured on a
    // ja-JP machine reads back identically on en-US.

    public string GetString(string key, string fallback = "")
        => Settings.TryGetValue(key, out var v) ? v : fallback;

    public void Set(string key, string value) => Settings[key] = value;

    public int GetInt(string key, int fallback = 0)
        => Settings.TryGetValue(key, out var v)
           && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n : fallback;

    public void Set(string key, int value)
        => Settings[key] = value.ToString(CultureInfo.InvariantCulture);

    public double GetDouble(string key, double fallback = 0)
        => Settings.TryGetValue(key, out var v)
           && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
            ? n : fallback;

    public void Set(string key, double value)
        => Settings[key] = value.ToString("R", CultureInfo.InvariantCulture);

    public bool GetBool(string key, bool fallback = false)
        => Settings.TryGetValue(key, out var v) && bool.TryParse(v, out var b) ? b : fallback;

    public void Set(string key, bool value) => Settings[key] = value ? "true" : "false";

    public DateTimeOffset? GetDate(string key)
        => Settings.TryGetValue(key, out var v)
           && DateTimeOffset.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d)
            ? d : null;

    public void Set(string key, DateTimeOffset value)
        => Settings[key] = value.ToString("O", CultureInfo.InvariantCulture);
}

/// <summary>
/// A scheduled override. During <see cref="Start"/>–<see cref="End"/> (local wall-clock time,
/// wrapping past midnight when End &lt; Start) the host renders <see cref="Kind"/> instead.
/// </summary>
public sealed class TimedSlot
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Minutes from local midnight, 0 – 1439.</summary>
    public int StartMinutes { get; set; }

    /// <summary>Minutes from local midnight, 0 – 1439.</summary>
    public int EndMinutes { get; set; } = 60;

    public WidgetKind Kind { get; set; } = WidgetKind.DigitalClock;

    public Dictionary<string, string> Settings { get; set; } = new();

    [JsonIgnore]
    public TimeSpan Start => TimeSpan.FromMinutes(StartMinutes);

    [JsonIgnore]
    public TimeSpan End => TimeSpan.FromMinutes(EndMinutes);

    /// <summary>True when <paramref name="now"/> falls inside this slot, handling ranges that wrap midnight.</summary>
    public bool Contains(TimeSpan now)
        => EndMinutes >= StartMinutes
            ? now >= Start && now < End
            : now >= Start || now < End;

    public TimedSlot Clone()
    {
        var copy = (TimedSlot)MemberwiseClone();
        copy.Settings = new Dictionary<string, string>(Settings);
        return copy;
    }
}
