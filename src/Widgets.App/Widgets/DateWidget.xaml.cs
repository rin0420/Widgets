using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Widgets.App.Controls;
using Widgets.App.Models;

namespace Widgets.App.Widgets;

/// <summary>Today's date: a large day number flanked by the weekday and month.</summary>
public sealed partial class DateWidget : WidgetViewBase
{
    private string _customFormat = string.Empty;

    public DateWidget()
    {
        InitializeComponent();
    }

    public override TimeSpan TickInterval => TimeSpan.FromSeconds(60);

    protected override void OnApply(WidgetRenderContext context)
    {
        var theme = context.Theme;
        var format = context.GetString(WidgetSettingKeys.DateFormat, "d");
        _customFormat = format is "d" or "" ? string.Empty : format;

        var (daySize, labelSize) = context.Size switch
        {
            WidgetSize.Small => (62.0, 15.0),
            WidgetSize.Medium => (86.0, 20.0),
            WidgetSize.Large => (128.0, 27.0),
            WidgetSize.Wide => (96.0, 22.0),
            WidgetSize.Tall => (76.0, 18.0),
            _ => (62.0, 15.0),
        };

        var horizontal = context.Size is WidgetSize.Medium or WidgetSize.Wide && _customFormat.Length == 0;

        Root.Orientation = horizontal ? Orientation.Horizontal : Orientation.Vertical;
        Root.Spacing = horizontal ? WidgetVisuals.Size(theme, 18) : 0;
        Labels.HorizontalAlignment = horizontal ? HorizontalAlignment.Left : HorizontalAlignment.Center;

        WidgetVisuals.Style(DayText, theme, _customFormat.Length == 0 ? daySize : daySize * 0.42,
            WidgetVisuals.Accent(theme), 200);
        WidgetVisuals.Style(WeekdayText, theme, labelSize, WidgetVisuals.Tint(theme), 100);
        WidgetVisuals.Style(MonthText, theme, labelSize * 0.85, WidgetVisuals.Secondary(theme));

        DayText.TextAlignment = horizontal ? TextAlignment.Right : TextAlignment.Center;
        WeekdayText.TextAlignment = horizontal ? TextAlignment.Left : TextAlignment.Center;
        MonthText.TextAlignment = WeekdayText.TextAlignment;

        DayBox.MaxWidth = context.Width;
        Labels.Visibility = _customFormat.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        OnTick(DateTimeOffset.Now);
    }

    public override void OnTick(DateTimeOffset now)
    {
        var today = now.LocalDateTime;
        var culture = CultureInfo.CurrentCulture;

        if (_customFormat.Length > 0)
        {
            DayText.Text = SafeFormat(today, _customFormat, culture);
            return;
        }

        DayText.Text = today.Day.ToString(CultureInfo.InvariantCulture);
        WeekdayText.Text = today.ToString("dddd", culture);
        MonthText.Text = today.ToString("yyyy年M月", culture);
    }

    private static string SafeFormat(DateTime value, string format, CultureInfo culture)
    {
        try
        {
            return value.ToString(format, culture);
        }
        catch (FormatException)
        {
            return value.ToString("d", culture);
        }
    }
}
