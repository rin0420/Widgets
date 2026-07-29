using System.Text.Json.Serialization;

namespace Widgets.App.Models;

/// <summary>
/// The complete visual description of a widget. Every field is user-editable from the
/// editor, and a <see cref="ThemePreset"/> is just a named, prefilled instance of this type.
/// Colors are stored as "#AARRGGBB" strings so the JSON stays human-readable and diffable.
/// </summary>
public sealed class WidgetTheme
{
    public string FontFamily { get; set; } = "Segoe UI Variable Display";

    /// <summary>Multiplier applied to each widget's intrinsic type scale. 0.5 – 2.0.</summary>
    public double FontScale { get; set; } = 1.0;

    /// <summary>OpenType weight, 100 – 900.</summary>
    public int FontWeight { get; set; } = 400;

    /// <summary>Primary foreground — the main text and glyphs.</summary>
    public string TintColor { get; set; } = "#FFFFFFFF";

    /// <summary>Highlight color for the element a widget wants to emphasize (today's date, the current temperature…).</summary>
    public string AccentColor { get; set; } = "#FF4CC2FF";

    /// <summary>De-emphasized text — labels, captions, secondary rows.</summary>
    public string SecondaryColor { get; set; } = "#99FFFFFF";

    public string BackgroundColor { get; set; } = "#E5202020";

    public string BorderColor { get; set; } = "#33FFFFFF";

    public double BorderThickness { get; set; } = 1.0;

    public double CornerRadius { get; set; } = 16.0;

    /// <summary>Opacity of the whole widget window, 0.1 – 1.0.</summary>
    public double Opacity { get; set; } = 1.0;

    public BackdropMode Backdrop { get; set; } = BackdropMode.Solid;

    /// <summary>Absolute path to the image used when <see cref="Backdrop"/> is <see cref="BackdropMode.Photo"/>.</summary>
    public string? BackgroundImagePath { get; set; }

    public double BackgroundImageOpacity { get; set; } = 1.0;

    /// <summary>Inner padding in logical pixels.</summary>
    public double Padding { get; set; } = 14.0;

    public bool DropShadow { get; set; } = true;

    public WidgetTheme Clone() => (WidgetTheme)MemberwiseClone();

    /// <summary>The app's default dark theme.</summary>
    public static WidgetTheme CreateDefault() => new();
}

/// <summary>A named, categorized theme shown in the "Aesthetics" gallery.</summary>
public sealed class ThemePreset
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Category { get; init; }
    public required WidgetTheme Theme { get; init; }

    [JsonIgnore]
    public bool IsBuiltIn { get; init; } = true;
}
