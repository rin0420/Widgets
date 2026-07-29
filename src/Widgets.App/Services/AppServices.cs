namespace Widgets.App.Services;

/// <summary>
/// Composition root. The app is small enough that a handful of eagerly-created singletons
/// beats a DI container, and it keeps startup ordering explicit.
/// </summary>
public static class AppServices
{
    private static WidgetStore? _store;
    private static SystemStatsService? _systemStats;
    private static WeatherService? _weather;
    private static StartupService? _startup;

    public static WidgetStore Store => _store ??= new WidgetStore();

    public static SystemStatsService SystemStats => _systemStats ??= new SystemStatsService();

    public static WeatherService Weather => _weather ??= new WeatherService();

    public static StartupService Startup => _startup ??= new StartupService();

    /// <summary>Set once during startup so background services can marshal to the UI thread.</summary>
    public static Microsoft.UI.Dispatching.DispatcherQueue? UiDispatcher { get; set; }

    /// <summary>Runs <paramref name="action"/> on the UI thread, inline if already there.</summary>
    public static void OnUi(Action action)
    {
        var queue = UiDispatcher;
        if (queue is null || queue.HasThreadAccess)
        {
            action();
        }
        else
        {
            queue.TryEnqueue(() => action());
        }
    }

    public static void Shutdown()
    {
        _systemStats?.Dispose();
        _weather?.Dispose();
        _store?.Flush();
    }
}
