using System.Globalization;
using Microsoft.UI.Xaml.Media;
using Widgets.App.Controls;
using Widgets.App.Models;

namespace Widgets.App.Widgets;

/// <summary>Local time somewhere else, with a day/night hint and the offset from here.</summary>
public sealed partial class WorldClockWidget : WidgetViewBase
{
    private const string DayGlyph = "";
    private const string NightGlyph = "";

    private TimeZoneInfo _zone = TimeZoneInfo.Local;
    private string _timeFormat = "HH:mm";
    private bool _showSeconds;

    public WorldClockWidget()
    {
        InitializeComponent();
    }

    public override TimeSpan TickInterval
        => _showSeconds || _timeFormat.Contains('s') ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(10);

    protected override void OnApply(WidgetRenderContext context)
    {
        var theme = context.Theme;

        _zone = ResolveZone(context.GetString(WidgetSettingKeys.TimeZoneId));
        _timeFormat = context.GetString(WidgetSettingKeys.TimeFormat, "HH:mm");
        if (string.IsNullOrWhiteSpace(_timeFormat))
        {
            _timeFormat = "HH:mm";
        }

        _showSeconds = context.GetBool(WidgetSettingKeys.ShowSeconds);
        if (_showSeconds && !_timeFormat.Contains('s'))
        {
            _timeFormat += ":ss";
        }

        var (timeSize, labelSize) = context.Size switch
        {
            WidgetSize.Small => (44.0, 13.0),
            WidgetSize.Medium => (60.0, 17.0),
            WidgetSize.Large => (80.0, 21.0),
            WidgetSize.Wide => (66.0, 19.0),
            WidgetSize.Tall => (48.0, 15.0),
            _ => (44.0, 13.0),
        };

        WidgetVisuals.Style(CityText, theme, labelSize, WidgetVisuals.Tint(theme), 100);
        WidgetVisuals.Style(TimeText, theme, timeSize, WidgetVisuals.Tint(theme), 100);
        WidgetVisuals.Style(OffsetText, theme, labelSize * 0.85, WidgetVisuals.Secondary(theme));

        PhaseGlyph.FontFamily = WidgetVisuals.IconFont;
        PhaseGlyph.FontSize = WidgetVisuals.Size(theme, labelSize);
        PhaseGlyph.Foreground = new SolidColorBrush(WidgetVisuals.Accent(theme));
        PhaseGlyph.IsColorFontEnabled = false;

        var label = context.GetString(WidgetSettingKeys.CityLabel);
        CityText.Text = string.IsNullOrWhiteSpace(label) ? ZoneLabel(_zone) : label;
        CityText.MaxWidth = context.Width * 0.8;
        TimeBox.MaxWidth = context.Width;
        OffsetText.MaxWidth = context.Width;

        OnTick(DateTimeOffset.Now);
    }

    public override void OnTick(DateTimeOffset now)
    {
        var there = TimeZoneInfo.ConvertTime(now, _zone);

        TimeText.Text = SafeFormat(there, _timeFormat);
        PhaseGlyph.Text = there.Hour is >= 6 and < 18 ? DayGlyph : NightGlyph;
        OffsetText.Text = BuildOffsetLabel(now, there);
    }

    private static string BuildOffsetLabel(DateTimeOffset here, DateTimeOffset there)
    {
        var delta = there.Offset - here.Offset;
        var dayNote = there.LocalDateTime.Date == here.LocalDateTime.Date
            ? string.Empty
            : there.LocalDateTime.Date > here.LocalDateTime.Date ? "翌日 " : "前日 ";

        if (delta == TimeSpan.Zero)
        {
            return dayNote + "現地と同じ時刻";
        }

        var sign = delta < TimeSpan.Zero ? "-" : "+";
        var span = delta.Duration();
        var text = span.Minutes == 0
            ? $"{sign}{(int)span.TotalHours}時間"
            : $"{sign}{(int)span.TotalHours}時間{span.Minutes}分";

        return dayNote + text;
    }

    private static TimeZoneInfo ResolveZone(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return TimeZoneInfo.Local;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex)
        {
            Crash.Log(ex, $"WorldClockWidget.ResolveZone({id})");
            return TimeZoneInfo.Local;
        }
    }

    private static string ZoneLabel(TimeZoneInfo zone)
    {
        var display = zone.DisplayName;
        var close = display.IndexOf(')');
        return close >= 0 && close + 2 < display.Length ? display[(close + 2)..] : display;
    }

    private static string SafeFormat(DateTimeOffset value, string format)
    {
        try
        {
            return value.ToString(format, CultureInfo.CurrentCulture);
        }
        catch (FormatException)
        {
            return value.ToString("HH:mm", CultureInfo.InvariantCulture);
        }
    }
}
