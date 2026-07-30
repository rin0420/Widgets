namespace Widgets.App.Models;

/// <summary>The kind of content a widget renders. Each value maps to one entry in the widget catalog.</summary>
public enum WidgetKind
{
    DigitalClock,
    AnalogClock,
    Date,
    Calendar,
    Weather,
    Countdown,
    SystemMonitor,
    Battery,
    Photo,
    Note,
    Astronomy,
    WorldClock,
    CpuMonitor,
    GpuMonitor,
    NetworkMonitor,
    DiskMonitor,
}

/// <summary>
/// Widget footprints, mirroring the iOS widget grid Widgetsmith is built around.
/// Values are logical (96 DPI) pixels; the host scales them per-monitor.
/// </summary>
public enum WidgetSize
{
    Small,
    Medium,
    Large,
    Wide,
    Tall,

    /// <summary>
    /// Free-form footprint held in <see cref="WidgetDefinition.CustomWidth"/> /
    /// <see cref="WidgetDefinition.CustomHeight"/>. This is the only size that can be dragged;
    /// the presets above are fixed by definition.
    /// </summary>
    Custom,
}

/// <summary>Where a widget window sits in the desktop z-order.</summary>
public enum ZOrderMode
{
    /// <summary>Pinned to the desktop: above the wallpaper, below every normal window.</summary>
    Desktop,

    /// <summary>Behaves like an ordinary window.</summary>
    Normal,

    /// <summary>Always above other windows.</summary>
    TopMost,
}

/// <summary>How a widget paints the area behind its content.</summary>
public enum BackdropMode
{
    /// <summary>Flat fill using <see cref="WidgetTheme.BackgroundColor"/>.</summary>
    Solid,

    /// <summary>Desktop acrylic — the translucent, blurred "frosted" look.</summary>
    Frosted,

    /// <summary>Mica, tinted by the desktop wallpaper.</summary>
    Mica,

    /// <summary>Fully transparent so the wallpaper shows through.</summary>
    Clear,

    /// <summary>A user-supplied image, drawn behind the content.</summary>
    Photo,
}

/// <summary>Which corner/edge a widget snaps to when the user drags it near a screen edge.</summary>
public enum SnapMode
{
    None,
    Grid,
    Edges,
}
