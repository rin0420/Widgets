namespace Widgets.App.Models;

/// <summary>Application-wide preferences, persisted alongside the widget list.</summary>
public sealed class AppSettings
{
    public bool LaunchAtStartup { get; set; }

    public SnapMode Snap { get; set; } = SnapMode.Grid;

    /// <summary>Grid pitch in logical pixels when <see cref="Snap"/> is <see cref="SnapMode.Grid"/>.</summary>
    public int GridSize { get; set; } = 16;

    /// <summary>Master lock: when true no widget can be dragged, regardless of its own flag.</summary>
    public bool LockAllWidgets { get; set; }

    /// <summary>Hide every widget while a foreground window is running full-screen (games, video).</summary>
    public bool HideOnFullscreen { get; set; } = true;

    /// <summary>Start minimized to the notification area instead of showing the manager window.</summary>
    public bool StartMinimized { get; set; }

    /// <summary>"Light", "Dark", or "Default" — the theme of the manager window itself.</summary>
    public string AppTheme { get; set; } = "Default";

    /// <summary>The Widgetsmith-style palette of saved custom colors (six slots).</summary>
    public List<string> SavedColors { get; set; } = new();

    /// <summary>Cached location for the weather and astronomy widgets.</summary>
    public string WeatherLocationName { get; set; } = string.Empty;

    public double WeatherLatitude { get; set; } = double.NaN;

    public double WeatherLongitude { get; set; } = double.NaN;

    /// <summary>True for °C, false for °F.</summary>
    public bool UseMetric { get; set; } = true;

    /// <summary>Re-capture the desktop periodically so animated wallpapers show through widgets.</summary>
    public bool FollowAnimatedWallpaper { get; set; }

    public const int MaxSavedColors = 6;
}

/// <summary>The complete on-disk document.</summary>
public sealed class WidgetDocument
{
    /// <summary>Schema version, bumped when a migration is needed on load.</summary>
    public int Version { get; set; } = 1;

    public AppSettings Settings { get; set; } = new();

    public List<WidgetDefinition> Widgets { get; set; } = new();

    /// <summary>Theme presets the user saved from the editor.</summary>
    public List<ThemePreset> CustomPresets { get; set; } = new();
}
