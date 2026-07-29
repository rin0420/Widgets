using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Widgets.App.Common;
using Widgets.App.Controls;
using Widgets.App.Models;
using Widgets.App.Services;
using Windows.UI;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace Widgets.App.Widgets;

/// <summary>Sunrise / sunset times and the moon phase, drawn as a real illuminated disc.</summary>
public sealed partial class AstronomyWidget : WidgetViewBase
{
    private const string SunriseGlyph = "";
    private const string SunsetGlyph = "";

    private CancellationTokenSource? _cts;
    private DateTimeOffset? _sunrise;
    private DateTimeOffset? _sunset;
    private string _mode = "Both";

    public AstronomyWidget()
    {
        InitializeComponent();
    }

    public override TimeSpan TickInterval => TimeSpan.FromMinutes(5);

    protected override void OnApply(WidgetRenderContext context)
    {
        _mode = context.GetString(WidgetSettingKeys.AstronomyMode, "Both");
        Render(context);

        if (!context.IsPreview)
        {
            BeginFetch();
        }
    }

    public override void OnTick(DateTimeOffset now)
    {
        if (Context is { } context)
        {
            Render(context);
        }
    }

    public override void Cleanup()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    private void BeginFetch()
    {
        var settings = WidgetVisuals.AppSettings();
        if (double.IsNaN(settings.WeatherLatitude) || double.IsNaN(settings.WeatherLongitude))
        {
            return;
        }

        Cleanup();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        _ = LoadAsync(settings.WeatherLatitude, settings.WeatherLongitude, token);
    }

    private async Task LoadAsync(double latitude, double longitude, CancellationToken token)
    {
        try
        {
            var snapshot = await AppServices.Weather.GetAsync(latitude, longitude, token).ConfigureAwait(false);
            if (snapshot is null || token.IsCancellationRequested)
            {
                return;
            }

            AppServices.OnUi(() =>
            {
                if (token.IsCancellationRequested || Context is null)
                {
                    return;
                }

                _sunrise = snapshot.Sunrise;
                _sunset = snapshot.Sunset;
                Render(Context);
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Crash.Log(ex, "AstronomyWidget.LoadAsync");
        }
    }

    private void Render(WidgetRenderContext context)
    {
        var theme = context.Theme;
        var now = DateTimeOffset.Now;

        Root.Children.Clear();
        Root.RowDefinitions.Clear();
        Root.ColumnDefinitions.Clear();

        var showMoon = _mode is "MoonPhase" or "Both";
        var showSun = _mode is "SunTimes" or "Both";

        var discSize = context.Size switch
        {
            WidgetSize.Large => 128.0,
            WidgetSize.Tall => 88.0,
            WidgetSize.Small => 62.0,
            _ => 72.0,
        };

        var textSize = context.Size switch
        {
            WidgetSize.Large => 17.0,
            WidgetSize.Small => 12.0,
            _ => 14.0,
        };

        var moon = showMoon ? BuildMoon(theme, now, discSize, textSize, useDisc: context.Size is WidgetSize.Large or WidgetSize.Tall) : null;
        var sun = showSun ? BuildSun(theme, context, now, textSize) : null;

        if (moon is not null && sun is not null)
        {
            var sideBySide = context.Size is WidgetSize.Medium or WidgetSize.Wide or WidgetSize.Large;

            if (sideBySide)
            {
                Root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                moon.Margin = new Thickness(0, 0, 14, 0);
                Grid.SetColumn(sun, 1);
            }
            else
            {
                Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                sun.Margin = new Thickness(0, 10, 0, 0);
                Grid.SetRow(sun, 1);
            }

            Root.Children.Add(moon);
            Root.Children.Add(sun);
            return;
        }

        var single = moon ?? sun;
        if (single is not null)
        {
            Root.Children.Add(single);
        }
    }

    private static FrameworkElement BuildMoon(
        WidgetTheme theme,
        DateTimeOffset now,
        double discSize,
        double textSize,
        bool useDisc)
    {
        var phase = AstronomyCalculator.MoonPhase(now.ToUniversalTime());
        var illuminated = (1 - Math.Cos(2 * Math.PI * phase)) / 2;

        var tint = WidgetVisuals.Tint(theme);
        var secondary = WidgetVisuals.Secondary(theme);

        var panel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 4,
        };

        if (useDisc)
        {
            panel.Children.Add(MoonDisc(discSize, phase, tint, ColorUtil.Fade(secondary, 0.35)));
        }
        else
        {
            panel.Children.Add(WidgetVisuals.AutoGlyph(
                theme,
                AstronomyCalculator.MoonGlyph(phase),
                WidgetVisuals.Size(theme, discSize * 0.62),
                tint));
        }

        var name = WidgetVisuals.Text(theme, textSize, tint, 100);
        name.Text = AstronomyCalculator.MoonPhaseName(phase);
        name.HorizontalAlignment = HorizontalAlignment.Center;
        panel.Children.Add(name);

        var percent = WidgetVisuals.Text(theme, textSize * 0.85, secondary);
        percent.Text = $"輝面比 {Math.Round(illuminated * 100).ToString("0", CultureInfo.InvariantCulture)}%";
        percent.HorizontalAlignment = HorizontalAlignment.Center;
        panel.Children.Add(percent);

        return panel;
    }

    private FrameworkElement BuildSun(
        WidgetTheme theme,
        WidgetRenderContext context,
        DateTimeOffset now,
        double textSize)
    {
        var tint = WidgetVisuals.Tint(theme);
        var secondary = WidgetVisuals.Secondary(theme);
        var accent = WidgetVisuals.Accent(theme);

        var panel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 6,
        };

        var times = ResolveSunTimes(context, now);
        if (times is null)
        {
            var message = WidgetVisuals.Text(theme, textSize, secondary);
            message.Text = "位置情報を設定してください";
            message.TextWrapping = TextWrapping.Wrap;
            message.TextAlignment = TextAlignment.Center;
            message.MaxWidth = context.Width;
            panel.Children.Add(message);
            return panel;
        }

        var (sunrise, sunset) = times.Value;

        panel.Children.Add(SunRow(theme, SunriseGlyph, "日の出", sunrise, textSize, accent, secondary, tint));
        panel.Children.Add(SunRow(theme, SunsetGlyph, "日の入", sunset, textSize, accent, secondary, tint));

        if (context.Size is WidgetSize.Large or WidgetSize.Wide)
        {
            var length = sunset - sunrise;
            var daylight = WidgetVisuals.Text(theme, textSize * 0.85, secondary);
            daylight.Text = $"昼の長さ {(int)length.TotalHours}時間{length.Minutes}分";
            daylight.HorizontalAlignment = HorizontalAlignment.Center;
            panel.Children.Add(daylight);
        }

        return panel;
    }

    private (DateTimeOffset Sunrise, DateTimeOffset Sunset)? ResolveSunTimes(WidgetRenderContext context, DateTimeOffset now)
    {
        if (_sunrise is { } cachedRise && _sunset is { } cachedSet && cachedRise.LocalDateTime.Date == now.LocalDateTime.Date)
        {
            return (cachedRise, cachedSet);
        }

        var settings = WidgetVisuals.AppSettings();
        var latitude = settings.WeatherLatitude;
        var longitude = settings.WeatherLongitude;

        if (double.IsNaN(latitude) || double.IsNaN(longitude))
        {
            if (!context.IsPreview)
            {
                return null;
            }

            latitude = 35.68;
            longitude = 139.77;
        }

        return AstronomyCalculator.SunTimes(latitude, longitude, now);
    }

    private static Grid SunRow(
        WidgetTheme theme,
        string glyph,
        string label,
        DateTimeOffset time,
        double textSize,
        Color glyphColor,
        Color labelColor,
        Color valueColor)
    {
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = WidgetVisuals.AutoGlyph(theme, glyph, WidgetVisuals.Size(theme, textSize * 1.05), glyphColor);
        icon.HorizontalAlignment = HorizontalAlignment.Left;

        var caption = WidgetVisuals.Text(theme, textSize * 0.9, labelColor);
        caption.Text = label;
        caption.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(caption, 1);

        var value = WidgetVisuals.Text(theme, textSize, valueColor, 100);
        value.Text = time.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
        value.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(value, 2);

        row.Children.Add(icon);
        row.Children.Add(caption);
        row.Children.Add(value);
        return row;
    }

    /// <summary>
    /// The lit side is a half-disc joined to a half-ellipse whose width is the terminator's
    /// projection, mirrored once the moon starts waning.
    /// </summary>
    private static FrameworkElement MoonDisc(double size, double phase, Color lit, Color dark)
    {
        var radius = size / 2;
        var illuminated = (1 - Math.Cos(2 * Math.PI * phase)) / 2;

        var host = new Grid { Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Center };

        host.Children.Add(new Ellipse
        {
            Width = size,
            Height = size,
            Fill = new SolidColorBrush(dark),
        });

        var figure = new PathFigure
        {
            StartPoint = new Windows.Foundation.Point(radius, 0),
            IsClosed = true,
            IsFilled = true,
        };

        figure.Segments.Add(new ArcSegment
        {
            Point = new Windows.Foundation.Point(radius, size),
            Size = new Windows.Foundation.Size(radius, radius),
            SweepDirection = SweepDirection.Clockwise,
        });

        figure.Segments.Add(new ArcSegment
        {
            Point = new Windows.Foundation.Point(radius, 0),
            Size = new Windows.Foundation.Size(radius * Math.Abs(2 * illuminated - 1), radius),
            SweepDirection = illuminated > 0.5 ? SweepDirection.Clockwise : SweepDirection.Counterclockwise,
        });

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);

        var shape = new Path
        {
            Data = geometry,
            Fill = new SolidColorBrush(lit),
            Width = size,
            Height = size,
        };

        if (phase >= 0.5)
        {
            shape.RenderTransform = new ScaleTransform { ScaleX = -1, CenterX = radius };
        }

        host.Children.Add(shape);
        return host;
    }
}
