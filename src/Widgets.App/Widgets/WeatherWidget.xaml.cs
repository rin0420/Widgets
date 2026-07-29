using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Widgets.App.Common;
using Widgets.App.Controls;
using Widgets.App.Models;
using Widgets.App.Services;
using Windows.UI;

namespace Widgets.App.Widgets;

/// <summary>Current conditions plus a short forecast strip, fed by the shared weather service.</summary>
public sealed partial class WeatherWidget : WidgetViewBase
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(15);

    private CancellationTokenSource? _cts;
    private WeatherSnapshot? _snapshot;
    private DateTimeOffset _lastFetch = DateTimeOffset.MinValue;

    public WeatherWidget()
    {
        InitializeComponent();
    }

    public override TimeSpan TickInterval => TimeSpan.FromMinutes(1);

    protected override void OnApply(WidgetRenderContext context)
    {
        CancelPending();

        if (context.IsPreview)
        {
            _snapshot = SampleSnapshot();
            Render(context, _snapshot);
            return;
        }

        Render(context, _snapshot);

        if (_snapshot is null)
        {
            ShowStatus(context, HasLocation(out _, out _) ? "読み込み中…" : "位置情報を設定してください");
        }

        BeginFetch();
    }

    public override void OnTick(DateTimeOffset now)
    {
        if (Context is null || Context.IsPreview)
        {
            return;
        }

        if (now - _lastFetch >= RefreshInterval)
        {
            BeginFetch();
        }
    }

    public override void Cleanup() => CancelPending();

    private void CancelPending()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    private static bool HasLocation(out double latitude, out double longitude)
    {
        var settings = WidgetVisuals.AppSettings();
        latitude = settings.WeatherLatitude;
        longitude = settings.WeatherLongitude;
        return !double.IsNaN(latitude) && !double.IsNaN(longitude);
    }

    private void BeginFetch()
    {
        if (!HasLocation(out var latitude, out var longitude))
        {
            return;
        }

        CancelPending();
        _cts = new CancellationTokenSource();
        _lastFetch = DateTimeOffset.Now;

        var token = _cts.Token;
        _ = LoadAsync(latitude, longitude, token);
    }

    private async Task LoadAsync(double latitude, double longitude, CancellationToken token)
    {
        try
        {
            var snapshot = await AppServices.Weather.GetAsync(latitude, longitude, token).ConfigureAwait(false);
            if (token.IsCancellationRequested)
            {
                return;
            }

            AppServices.OnUi(() =>
            {
                if (token.IsCancellationRequested || Context is null)
                {
                    return;
                }

                if (snapshot is null)
                {
                    if (_snapshot is null)
                    {
                        ShowStatus(Context, "天気を取得できません");
                    }

                    return;
                }

                _snapshot = snapshot;
                Render(Context, snapshot);
            });
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer request.
        }
        catch (Exception ex)
        {
            Crash.Log(ex, "WeatherWidget.LoadAsync");
        }
    }

    private void ShowStatus(WidgetRenderContext context, string message)
    {
        Body.Visibility = Visibility.Collapsed;
        StatusText.Visibility = Visibility.Visible;
        StatusText.Text = message;
        StatusText.MaxWidth = context.Width;
        WidgetVisuals.Style(StatusText, context.Theme, context.Size == WidgetSize.Small ? 12 : 14,
            WidgetVisuals.Secondary(context.Theme));
    }

    private void Render(WidgetRenderContext context, WeatherSnapshot? data)
    {
        if (data is null)
        {
            return;
        }

        StatusText.Visibility = Visibility.Collapsed;
        Body.Visibility = Visibility.Visible;

        var theme = context.Theme;
        var metric = WidgetVisuals.AppSettings().UseMetric;
        var layout = Layout(context.Size);

        var showForecast = context.GetBool(WidgetSettingKeys.ShowForecast, true)
                           && layout.ForecastDays > 0
                           && data.Daily.Count > 0;

        var days = Math.Clamp(context.GetInt(WidgetSettingKeys.ForecastDays, 4), 1, 7);
        days = Math.Min(days, layout.ForecastDays);

        Body.Children.Clear();
        Body.RowDefinitions.Clear();
        Body.ColumnDefinitions.Clear();

        var current = BuildCurrent(context, data, metric, layout);

        if (!showForecast)
        {
            Body.Children.Add(current);
            return;
        }

        var forecast = BuildForecast(theme, data, metric, days, layout);

        if (layout.SideBySide)
        {
            Body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            current.HorizontalAlignment = HorizontalAlignment.Left;
            current.Margin = new Thickness(0, 0, 12, 0);
            Grid.SetColumn(forecast, 1);
        }
        else
        {
            Body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            forecast.Margin = new Thickness(0, 8, 0, 0);
            Grid.SetRow(forecast, 1);
        }

        Body.Children.Add(current);
        Body.Children.Add(forecast);
    }

    private static StackPanel BuildCurrent(
        WidgetRenderContext context,
        WeatherSnapshot data,
        bool metric,
        WeatherLayout layout)
    {
        var theme = context.Theme;
        var tint = WidgetVisuals.Tint(theme);
        var secondary = WidgetVisuals.Secondary(theme);
        var accent = WidgetVisuals.Accent(theme);

        var now = DateTimeOffset.Now;
        var night = now < data.Sunrise || now > data.Sunset;

        var panel = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = layout.SideBySide ? HorizontalAlignment.Left : HorizontalAlignment.Center,
        };

        var headline = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = layout.TemperatureSize * 0.14,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = panel.HorizontalAlignment,
        };

        headline.Children.Add(WidgetVisuals.AutoGlyph(
            theme,
            WeatherService.GlyphForWeatherCode(data.WeatherCode, night),
            WidgetVisuals.Size(theme, layout.GlyphSize),
            accent));

        var temperature = WidgetVisuals.Text(theme, layout.TemperatureSize, tint, 100);
        temperature.Text = FormatTemperature(data.TemperatureC, metric) + "°";
        temperature.VerticalAlignment = VerticalAlignment.Center;
        headline.Children.Add(temperature);

        panel.Children.Add(headline);

        var description = WidgetVisuals.Text(theme, layout.DescriptionSize, secondary);
        description.Text = WeatherService.DescribeWeatherCode(data.WeatherCode);
        description.HorizontalAlignment = panel.HorizontalAlignment;
        description.Margin = new Thickness(0, 2, 0, 0);
        panel.Children.Add(description);

        if (layout.ShowDetails && context.GetBool(WidgetSettingKeys.ShowFeelsLike, true))
        {
            var details = new StackPanel
            {
                Orientation = layout.StackDetails ? Orientation.Vertical : Orientation.Horizontal,
                Spacing = layout.StackDetails ? 1 : 10,
                Margin = new Thickness(0, 6, 0, 0),
                HorizontalAlignment = panel.HorizontalAlignment,
            };

            details.Children.Add(Detail(theme, "体感", FormatTemperature(data.FeelsLikeC, metric) + "°", layout.DetailSize, secondary, tint));
            details.Children.Add(Detail(theme, "湿度", $"{data.Humidity}%", layout.DetailSize, secondary, tint));
            details.Children.Add(Detail(theme, "風", FormatWind(data.WindKph, metric), layout.DetailSize, secondary, tint));

            panel.Children.Add(details);
        }

        return panel;
    }

    private static StackPanel BuildForecast(
        WidgetTheme theme,
        WeatherSnapshot data,
        bool metric,
        int days,
        WeatherLayout layout)
    {
        var tint = WidgetVisuals.Tint(theme);
        var secondary = WidgetVisuals.Secondary(theme);

        var strip = new StackPanel
        {
            Orientation = layout.VerticalForecast ? Orientation.Vertical : Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = layout.VerticalForecast ? 4 : 0,
        };

        foreach (var day in data.Daily.Take(days))
        {
            var date = day.Date.ToDateTime(TimeOnly.MinValue);

            if (layout.VerticalForecast)
            {
                var row = new Grid { ColumnSpacing = 6 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var label = WidgetVisuals.Text(theme, layout.ForecastSize, secondary);
                label.Text = WidgetVisuals.WeekdayJa(date.DayOfWeek);

                var glyph = WidgetVisuals.AutoGlyph(
                    theme,
                    WeatherService.GlyphForWeatherCode(day.WeatherCode, false),
                    WidgetVisuals.Size(theme, layout.ForecastSize * 1.15),
                    tint);
                glyph.HorizontalAlignment = HorizontalAlignment.Left;
                Grid.SetColumn(glyph, 1);

                var range = WidgetVisuals.Text(theme, layout.ForecastSize, tint);
                range.Text = $"{FormatTemperature(day.HighC, metric)}/{FormatTemperature(day.LowC, metric)}";
                Grid.SetColumn(range, 2);

                row.Children.Add(label);
                row.Children.Add(glyph);
                row.Children.Add(range);
                strip.Children.Add(row);
                continue;
            }

            var column = new StackPanel
            {
                Width = layout.ForecastColumnWidth,
                HorizontalAlignment = HorizontalAlignment.Center,
                Spacing = 2,
            };

            var weekday = WidgetVisuals.Text(theme, layout.ForecastSize, secondary);
            weekday.Text = WidgetVisuals.WeekdayJa(date.DayOfWeek);
            weekday.HorizontalAlignment = HorizontalAlignment.Center;
            column.Children.Add(weekday);

            column.Children.Add(WidgetVisuals.AutoGlyph(
                theme,
                WeatherService.GlyphForWeatherCode(day.WeatherCode, false),
                WidgetVisuals.Size(theme, layout.ForecastSize * 1.35),
                tint));

            var high = WidgetVisuals.Text(theme, layout.ForecastSize, tint, 100);
            high.Text = FormatTemperature(day.HighC, metric);
            high.HorizontalAlignment = HorizontalAlignment.Center;
            column.Children.Add(high);

            var low = WidgetVisuals.Text(theme, layout.ForecastSize * 0.9, ColorUtil.Fade(secondary, 0.8));
            low.Text = FormatTemperature(day.LowC, metric);
            low.HorizontalAlignment = HorizontalAlignment.Center;
            column.Children.Add(low);

            strip.Children.Add(column);
        }

        return strip;
    }

    private static StackPanel Detail(
        WidgetTheme theme,
        string label,
        string value,
        double size,
        Color labelColor,
        Color valueColor)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };

        var caption = WidgetVisuals.Text(theme, size, labelColor);
        caption.Text = label;
        panel.Children.Add(caption);

        var text = WidgetVisuals.Text(theme, size, valueColor, 100);
        text.Text = value;
        panel.Children.Add(text);

        return panel;
    }

    private static string FormatTemperature(double celsius, bool metric)
        => Math.Round(WidgetVisuals.CelsiusToDisplay(celsius, metric)).ToString("0", CultureInfo.InvariantCulture);

    private static string FormatWind(double kph, bool metric)
        => metric
            ? $"{Math.Round(kph):0}km/h"
            : $"{Math.Round(kph * 0.621371):0}mph";

    private static WeatherLayout Layout(WidgetSize size) => size switch
    {
        WidgetSize.Small => new WeatherLayout(30, 44, 12, 11, 10, 0, false, false, false, false, 0),
        WidgetSize.Medium => new WeatherLayout(30, 46, 13, 11, 11, 4, true, false, true, false, 44),
        WidgetSize.Large => new WeatherLayout(46, 74, 18, 14, 13, 5, false, false, true, false, 60),
        WidgetSize.Wide => new WeatherLayout(34, 54, 15, 12, 12, 6, true, false, true, false, 52),
        WidgetSize.Tall => new WeatherLayout(32, 44, 13, 11, 11, 4, false, true, true, true, 0),
        _ => new WeatherLayout(30, 44, 12, 11, 10, 0, false, false, false, false, 0),
    };

    private static WeatherSnapshot SampleSnapshot()
    {
        var now = DateTimeOffset.Now;
        var midnight = new DateTimeOffset(now.Date, now.Offset);
        var today = DateOnly.FromDateTime(now.LocalDateTime);

        var daily = new List<DailyForecast>
        {
            new(today, 24, 17, 1, 10),
            new(today.AddDays(1), 26, 18, 2, 20),
            new(today.AddDays(2), 22, 17, 61, 70),
            new(today.AddDays(3), 21, 16, 63, 80),
            new(today.AddDays(4), 25, 18, 3, 30),
            new(today.AddDays(5), 27, 19, 0, 0),
            new(today.AddDays(6), 28, 20, 1, 10),
        };

        return new WeatherSnapshot(
            22.4, 21.7, 2, 9.3, 58, 0.0,
            midnight.AddHours(4.8), midnight.AddHours(18.9),
            daily, now);
    }

    private sealed record WeatherLayout(
        double GlyphSize,
        double TemperatureSize,
        double DescriptionSize,
        double DetailSize,
        double ForecastSize,
        int ForecastDays,
        bool SideBySide,
        bool VerticalForecast,
        bool ShowDetails,
        bool StackDetails,
        double ForecastColumnWidth);
}
