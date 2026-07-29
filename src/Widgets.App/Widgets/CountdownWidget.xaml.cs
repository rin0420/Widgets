using System.Globalization;
using Microsoft.UI.Xaml;
using Widgets.App.Controls;
using Widgets.App.Models;

namespace Widgets.App.Widgets;

/// <summary>Time remaining to — or elapsed since — a target moment.</summary>
public sealed partial class CountdownWidget : WidgetViewBase
{
    private DateTimeOffset _target = DateTimeOffset.Now;
    private string _unit = "Days";
    private bool _countUp;

    public CountdownWidget()
    {
        InitializeComponent();
    }

    public override TimeSpan TickInterval
        => _unit == "Full" ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(60);

    protected override void OnApply(WidgetRenderContext context)
    {
        var theme = context.Theme;

        _target = context.GetDate(WidgetSettingKeys.TargetDate) ?? DateTimeOffset.Now.AddDays(30);
        _unit = context.GetString(WidgetSettingKeys.CountdownUnit, "Days");
        _countUp = context.GetBool(WidgetSettingKeys.CountUp);

        var (valueSize, labelSize) = context.Size switch
        {
            WidgetSize.Small => (54.0, 13.0),
            WidgetSize.Medium => (72.0, 17.0),
            WidgetSize.Large => (96.0, 21.0),
            WidgetSize.Wide => (80.0, 19.0),
            WidgetSize.Tall => (58.0, 15.0),
            _ => (54.0, 13.0),
        };

        if (_unit == "Full")
        {
            valueSize *= 0.5;
        }

        WidgetVisuals.Style(TitleText, theme, labelSize, WidgetVisuals.Tint(theme), 100);
        WidgetVisuals.Style(ValueText, theme, valueSize, WidgetVisuals.Accent(theme), 200);
        WidgetVisuals.Style(UnitText, theme, valueSize * 0.34, WidgetVisuals.Tint(theme));
        WidgetVisuals.Style(TargetText, theme, labelSize * 0.85, WidgetVisuals.Secondary(theme));

        UnitText.Margin = new Thickness(WidgetVisuals.Size(theme, valueSize) * 0.05, 0, 0,
            WidgetVisuals.Size(theme, valueSize) * 0.12);

        TitleText.Text = context.GetString(WidgetSettingKeys.CountdownTitle, "イベント");
        TitleText.MaxWidth = context.Width;
        TargetText.MaxWidth = context.Width;
        ValueBox.MaxWidth = context.Width;

        OnTick(DateTimeOffset.Now);
    }

    public override void OnTick(DateTimeOffset now)
    {
        var delta = _countUp ? now - _target : _target - now;
        var overdue = delta < TimeSpan.Zero;
        var span = overdue ? -delta : delta;

        var (value, unit) = _unit switch
        {
            "Full" => (FormatFull(span), string.Empty),
            "Auto" => FormatAuto(span),
            _ => (((long)span.TotalDays).ToString(CultureInfo.InvariantCulture), "日"),
        };

        ValueText.Text = value;
        UnitText.Text = unit;
        UnitText.Visibility = unit.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

        var date = _target.ToLocalTime().ToString("yyyy年M月d日 HH:mm", CultureInfo.CurrentCulture);
        TargetText.Text = overdue ? $"{date}（{(_countUp ? "開始前" : "経過")}）" : date;
    }

    private static (string Value, string Unit) FormatAuto(TimeSpan span)
    {
        if (span.TotalDays >= 1)
        {
            return (((long)span.TotalDays).ToString(CultureInfo.InvariantCulture), "日");
        }

        if (span.TotalHours >= 1)
        {
            return (((long)span.TotalHours).ToString(CultureInfo.InvariantCulture), "時間");
        }

        if (span.TotalMinutes >= 1)
        {
            return (((long)span.TotalMinutes).ToString(CultureInfo.InvariantCulture), "分");
        }

        return (((long)span.TotalSeconds).ToString(CultureInfo.InvariantCulture), "秒");
    }

    private static string FormatFull(TimeSpan span)
        => $"{(long)span.TotalDays}日 {span.Hours:00}:{span.Minutes:00}:{span.Seconds:00}";
}
