using Widgets.App.Models;
using Widgets.App.Widgets;

namespace Widgets.App.Controls;

/// <summary>Maps a <see cref="WidgetKind"/> onto its renderer.</summary>
public static class WidgetViewFactory
{
    public static WidgetViewBase Create(WidgetKind kind) => kind switch
    {
        WidgetKind.DigitalClock => new DigitalClockWidget(),
        WidgetKind.AnalogClock => new AnalogClockWidget(),
        WidgetKind.Date => new DateWidget(),
        WidgetKind.Calendar => new CalendarWidget(),
        WidgetKind.Weather => new WeatherWidget(),
        WidgetKind.Countdown => new CountdownWidget(),
        WidgetKind.SystemMonitor => new SystemMonitorWidget(),
        WidgetKind.Battery => new BatteryWidget(),
        WidgetKind.Photo => new PhotoWidget(),
        WidgetKind.Note => new NoteWidget(),
        WidgetKind.Astronomy => new AstronomyWidget(),
        WidgetKind.WorldClock => new WorldClockWidget(),
        WidgetKind.CpuMonitor => new CpuMonitorWidget(),
        WidgetKind.GpuMonitor => new GpuMonitorWidget(),
        WidgetKind.NetworkMonitor => new NetworkMonitorWidget(),
        WidgetKind.DiskMonitor => new DiskMonitorWidget(),
        _ => new DigitalClockWidget(),
    };
}
