using System.Globalization;
using Microsoft.UI.Xaml;
using Widgets.App.Controls;
using Widgets.App.Models;

namespace Widgets.App.Widgets;

/// <summary>Large wall-clock time with an optional seconds tail and a date line.</summary>
public sealed partial class DigitalClockWidget : WidgetViewBase
{
    private string _timeFormat = "HH:mm";
    private string _dateFormat = "M月d日 dddd";
    private bool _showSeconds;

    public DigitalClockWidget()
    {
        InitializeComponent();
    }

    public override TimeSpan TickInterval
        => _showSeconds ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(10);

    protected override void OnApply(WidgetRenderContext context)
    {
        var theme = context.Theme;

        _timeFormat = context.GetString(WidgetSettingKeys.TimeFormat, "HH:mm");
        if (string.IsNullOrWhiteSpace(_timeFormat))
        {
            _timeFormat = "HH:mm";
        }

        _showSeconds = context.GetBool(WidgetSettingKeys.ShowSeconds);
        _dateFormat = context.GetString(WidgetSettingKeys.DateFormat, "M月d日 dddd");

        // A format that already asks for seconds makes the separate seconds tail redundant.
        var formatHasSeconds = _timeFormat.Contains('s');
        SecondsText.Visibility = _showSeconds && !formatHasSeconds ? Visibility.Visible : Visibility.Collapsed;

        var (timeSize, dateSize) = context.Size switch
        {
            WidgetSize.Small => (52.0, 13.0),
            WidgetSize.Medium => (72.0, 17.0),
            WidgetSize.Large => (92.0, 21.0),
            WidgetSize.Wide => (80.0, 19.0),
            WidgetSize.Tall => (56.0, 15.0),
            _ => (52.0, 13.0),
        };

        WidgetVisuals.Style(TimeText, theme, timeSize, WidgetVisuals.Tint(theme), 100);
        WidgetVisuals.Style(SecondsText, theme, timeSize * 0.38, WidgetVisuals.Accent(theme), 100);
        WidgetVisuals.Style(DateText, theme, dateSize, WidgetVisuals.Secondary(theme));

        SecondsText.Margin = new Thickness(WidgetVisuals.Size(theme, timeSize) * 0.06, 0, 0, WidgetVisuals.Size(theme, timeSize) * 0.1);

        TimeBox.MaxWidth = context.Width;
        DateText.MaxWidth = context.Width;
        DateText.Visibility = string.IsNullOrWhiteSpace(_dateFormat) ? Visibility.Collapsed : Visibility.Visible;

        OnTick(DateTimeOffset.Now);
    }

    public override void OnTick(DateTimeOffset now)
    {
        var local = now.LocalDateTime;
        var culture = CultureInfo.CurrentCulture;

        TimeText.Text = Format(local, _timeFormat, culture);
        SecondsText.Text = local.ToString("ss", CultureInfo.InvariantCulture);

        if (!string.IsNullOrWhiteSpace(_dateFormat))
        {
            DateText.Text = Format(local, _dateFormat, culture);
        }
    }

    private static string Format(DateTime value, string format, CultureInfo culture)
    {
        try
        {
            return value.ToString(format, culture);
        }
        catch (FormatException)
        {
            return value.ToString("HH:mm", culture);
        }
    }
}
