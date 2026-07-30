using Microsoft.UI.Xaml.Controls;
using Widgets.App.Models;

namespace Widgets.App.Controls;

/// <summary>
/// Everything a renderer needs to draw itself: the definition (possibly overridden by an
/// active <see cref="TimedSlot"/>), the resolved theme, and the footprint in logical pixels.
/// </summary>
public sealed class WidgetRenderContext
{
    public required WidgetDefinition Definition { get; init; }

    /// <summary>The kind actually being rendered — differs from <c>Definition.Kind</c> inside a timed slot.</summary>
    public required WidgetKind EffectiveKind { get; init; }

    /// <summary>Settings merged with any active timed-slot overrides.</summary>
    public required IReadOnlyDictionary<string, string> Settings { get; init; }

    public required WidgetTheme Theme { get; init; }

    public required double Width { get; init; }

    public required double Height { get; init; }

    /// <summary>
    /// The footprint the renderer should lay itself out for. For <see cref="WidgetSize.Custom"/>
    /// this is the preset the free-form size most resembles, so every renderer's existing
    /// <c>context.Size switch</c> reflows with the drag instead of falling through to the smallest
    /// layout. Use <see cref="Width"/>/<see cref="Height"/> for anything that needs exact pixels.
    /// </summary>
    public required WidgetSize Size { get; init; }

    /// <summary>True when the user is sizing this widget by hand rather than picking a preset.</summary>
    public bool IsCustom { get; init; }

    /// <summary>True when drawn in the editor preview, where live data may be replaced by samples.</summary>
    public bool IsPreview { get; init; }

    public string GetString(string key, string fallback = "")
        => Settings.TryGetValue(key, out var v) ? v : fallback;

    public bool GetBool(string key, bool fallback = false)
        => Settings.TryGetValue(key, out var v) && bool.TryParse(v, out var b) ? b : fallback;

    public int GetInt(string key, int fallback = 0)
        => Settings.TryGetValue(key, out var v)
           && int.TryParse(v, System.Globalization.NumberStyles.Integer,
                           System.Globalization.CultureInfo.InvariantCulture, out var n)
            ? n : fallback;

    public double GetDouble(string key, double fallback = 0)
        => Settings.TryGetValue(key, out var v)
           && double.TryParse(v, System.Globalization.NumberStyles.Float,
                              System.Globalization.CultureInfo.InvariantCulture, out var n)
            ? n : fallback;

    public DateTimeOffset? GetDate(string key)
        => Settings.TryGetValue(key, out var v)
           && DateTimeOffset.TryParse(v, System.Globalization.CultureInfo.InvariantCulture,
                                      System.Globalization.DateTimeStyles.RoundtripKind, out var d)
            ? d : null;
}

/// <summary>
/// Base class for every widget renderer. The host owns the window chrome, backdrop and
/// border; a renderer only draws content inside the padded content area.
/// </summary>
public abstract class WidgetViewBase : UserControl
{
    /// <summary>The most recent context, available to subclasses during <see cref="OnTick"/>.</summary>
    protected WidgetRenderContext? Context { get; private set; }

    /// <summary>How often <see cref="OnTick"/> should run. <see cref="TimeSpan.Zero"/> means never.</summary>
    public virtual TimeSpan TickInterval => TimeSpan.Zero;

    /// <summary>Applies a context. Called on creation and whenever the definition or theme changes.</summary>
    public void Apply(WidgetRenderContext context)
    {
        Context = context;
        OnApply(context);
    }

    protected abstract void OnApply(WidgetRenderContext context);

    /// <summary>Called on the UI thread every <see cref="TickInterval"/> while the widget is visible.</summary>
    public virtual void OnTick(DateTimeOffset now)
    {
    }

    /// <summary>Release timers, file watchers, and event subscriptions.</summary>
    public virtual void Cleanup()
    {
    }
}
