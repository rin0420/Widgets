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

/// <summary>GPU load, VRAM usage and adapter name, with a graceful no-GPU fallback.</summary>
public sealed partial class GpuMonitorWidget : WidgetViewBase
{
    private const int HistoryCapacity = 60;

    private readonly Queue<double> _history = new(HistoryCapacity);

    private Path? _ring;
    private Grid? _bar;
    private Polygon? _area;
    private Polyline? _spark;
    private SolidColorBrush? _gaugeBrush;
    private SolidColorBrush? _areaBrush;
    private SolidColorBrush? _sparkBrush;
    private TextBlock? _percentText;
    private SolidColorBrush? _percentBrush;
    private StackPanel? _vramRow;
    private Grid? _vramBar;
    private TextBlock? _vramText;
    private TextBlock? _caption;

    private double _gaugeSize;
    private double _gaugeThickness;
    private bool _colorByLoad = true;
    private bool _vramAllowed;
    private bool _showGpuName;
    private bool _showFps;
    private bool _subscribed;

    public GpuMonitorWidget()
    {
        InitializeComponent();
    }

    protected override void OnApply(WidgetRenderContext context)
    {
        var theme = context.Theme;
        var style = context.GetString(WidgetSettingKeys.GaugeStyle, "Ring");
        var showText = context.GetBool(WidgetSettingKeys.ShowPercentageText, true);
        _colorByLoad = context.GetBool(WidgetSettingKeys.ColorByLoad, true);
        _showGpuName = context.GetBool(WidgetSettingKeys.ShowGpuName, true);
        _showFps = context.GetBool(WidgetSettingKeys.ShowFps, true);
        var showVramSetting = context.GetBool(WidgetSettingKeys.ShowVram, true);
        _vramAllowed = context.Size != WidgetSize.Small && showVramSetting;

        var scale = context.Size switch
        {
            WidgetSize.Small => 1.0,
            WidgetSize.Medium => 1.15,
            WidgetSize.Large => 1.9,
            WidgetSize.Wide => 1.2,
            WidgetSize.Tall => 1.25,
            _ => 1.0,
        };

        Root.Children.Clear();
        _ring = null;
        _bar = null;
        _area = null;
        _spark = null;
        _gaugeBrush = null;
        _areaBrush = null;
        _sparkBrush = null;
        _percentText = null;
        _percentBrush = null;
        _vramRow = null;
        _vramBar = null;
        _vramText = null;
        _caption = null;

        var root = context.Size == WidgetSize.Wide
            ? BuildWideLayout(theme, context, style, showText, scale)
            : BuildStackedLayout(theme, context, style, showText, scale);

        Root.Children.Add(root);

        if (!_subscribed)
        {
            AppServices.SystemStats.Updated += OnStatsUpdated;
            _subscribed = true;
        }

        Update(context.IsPreview ? SampleStats() : AppServices.SystemStats.Current);
    }

    public override void Cleanup()
    {
        if (_subscribed)
        {
            AppServices.SystemStats.Updated -= OnStatsUpdated;
            _subscribed = false;
        }
    }

    private void OnStatsUpdated(object? sender, SystemStats stats)
        => AppServices.OnUi(() =>
        {
            if (Context is { IsPreview: false })
            {
                Update(stats);
            }
        });

    private FrameworkElement BuildStackedLayout(
        WidgetTheme theme, WidgetRenderContext context, string style, bool showText, double scale)
    {
        var stack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 6 * scale,
        };

        var (gaugeWidth, gaugeHeight) = GaugeExtent(context, style, scale);
        stack.Children.Add(BuildGauge(theme, style, gaugeWidth, gaugeHeight, showText));

        if (_vramAllowed)
        {
            var vramWidth = Math.Max(70, Math.Min(context.Width * 0.7, 220 * scale));
            stack.Children.Add(BuildVram(theme, vramWidth, scale));
        }

        _caption = WidgetVisuals.Text(theme, 12 * scale, WidgetVisuals.Secondary(theme));
        _caption.HorizontalAlignment = HorizontalAlignment.Center;
        _caption.MaxWidth = context.Width;
        stack.Children.Add(_caption);

        return stack;
    }

    private FrameworkElement BuildWideLayout(
        WidgetTheme theme, WidgetRenderContext context, string style, bool showText, double scale)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 24,
        };

        var (gaugeWidth, gaugeHeight) = GaugeExtent(context, style, scale);
        row.Children.Add(BuildGauge(theme, style, gaugeWidth, gaugeHeight, showText));

        var sideWidth = Math.Max(90, context.Width * 0.26);
        var side = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 8 * scale };

        if (_vramAllowed)
        {
            side.Children.Add(BuildVram(theme, sideWidth, scale));
        }

        _caption = WidgetVisuals.Text(theme, 13 * scale, WidgetVisuals.Secondary(theme));
        _caption.MaxWidth = sideWidth;
        side.Children.Add(_caption);

        row.Children.Add(side);
        return row;
    }

    private static (double Width, double Height) GaugeExtent(WidgetRenderContext context, string style, double scale)
    {
        if (style == "Bar")
        {
            var width = Math.Max(70, Math.Min(context.Width * 0.8, 220 * scale));
            return (width, Math.Max(10, width * 0.09));
        }

        if (style == "Sparkline")
        {
            var width = Math.Max(70, Math.Min(context.Width * 0.86, 230 * scale));
            var height = Math.Max(36, Math.Min(context.Height * 0.42, 84 * scale));
            return (width, height);
        }

        var ring = Math.Max(40, Math.Min(context.Width, context.Height * 0.62) * (context.Size == WidgetSize.Large ? 0.6 : 0.72));
        return (ring, ring);
    }

    private FrameworkElement BuildGauge(WidgetTheme theme, string style, double width, double height, bool showText) => style switch
    {
        "Bar" => BuildBarGauge(theme, width, height, showText),
        "Sparkline" => BuildSparkGauge(theme, width, height, showText),
        _ => BuildRingGauge(theme, width, showText),
    };

    private FrameworkElement BuildRingGauge(WidgetTheme theme, double size, bool showText)
    {
        _gaugeSize = size;
        _gaugeThickness = Math.Max(4, size * 0.11);

        var dial = new Grid
        {
            Width = _gaugeSize,
            Height = _gaugeSize,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        dial.Children.Add(WidgetVisuals.RingTrack(_gaugeSize, _gaugeThickness, WidgetVisuals.Track(theme)));

        _gaugeBrush = new SolidColorBrush(WidgetVisuals.Accent(theme));
        _ring = WidgetVisuals.Ring(_gaugeSize, _gaugeThickness, WidgetVisuals.Accent(theme));
        _ring.Stroke = _gaugeBrush;
        dial.Children.Add(_ring);

        if (showText)
        {
            _percentText = WidgetVisuals.Text(theme, _gaugeSize * 0.26, WidgetVisuals.Tint(theme), 100);
            _percentBrush = (SolidColorBrush)_percentText.Foreground;
            _percentText.HorizontalAlignment = HorizontalAlignment.Center;
            _percentText.VerticalAlignment = VerticalAlignment.Center;
            dial.Children.Add(_percentText);
        }

        return dial;
    }

    private FrameworkElement BuildBarGauge(WidgetTheme theme, double width, double height, bool showText)
    {
        var stack = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center };

        if (showText)
        {
            _percentText = WidgetVisuals.Text(theme, Math.Max(14, height * 1.6), WidgetVisuals.Tint(theme), 100);
            _percentBrush = (SolidColorBrush)_percentText.Foreground;
            _percentText.HorizontalAlignment = HorizontalAlignment.Center;
            stack.Children.Add(_percentText);
        }

        _bar = WidgetVisuals.Bar(width, height, WidgetVisuals.Track(theme), WidgetVisuals.Accent(theme));
        stack.Children.Add(_bar);

        return stack;
    }

    private FrameworkElement BuildSparkGauge(WidgetTheme theme, double width, double height, bool showText)
    {
        var accent = WidgetVisuals.Accent(theme);
        var host = new Grid { Width = width, Height = height, HorizontalAlignment = HorizontalAlignment.Center };

        _area = WidgetVisuals.Area(width, height, accent);
        _areaBrush = (SolidColorBrush)_area.Fill;
        host.Children.Add(_area);

        _spark = WidgetVisuals.Spark(width, height, accent, 2.0);
        _sparkBrush = (SolidColorBrush)_spark.Stroke;
        host.Children.Add(_spark);

        if (showText)
        {
            _percentText = WidgetVisuals.Text(theme, height * 0.3, WidgetVisuals.Tint(theme), 100);
            _percentBrush = (SolidColorBrush)_percentText.Foreground;
            _percentText.HorizontalAlignment = HorizontalAlignment.Center;
            _percentText.VerticalAlignment = VerticalAlignment.Center;
            host.Children.Add(_percentText);
        }

        return host;
    }

    private StackPanel BuildVram(WidgetTheme theme, double width, double scale)
    {
        var stack = new StackPanel { Spacing = 3 * scale, Width = width, HorizontalAlignment = HorizontalAlignment.Center };

        var labelSize = 11 * scale;

        // "3.2 GB / 8.0 GB" beside the caption needs roughly 11em. Any narrower and the auto-sized
        // value takes the whole row, squeezing the caption down to "V." — so give each its own line.
        var stacked = width < WidgetVisuals.Size(theme, labelSize) * 11;

        var label = WidgetVisuals.Text(theme, labelSize, WidgetVisuals.Secondary(theme));
        label.Text = "VRAM";

        _vramText = WidgetVisuals.Text(theme, labelSize, WidgetVisuals.Tint(theme));

        if (stacked)
        {
            stack.Children.Add(label);
            stack.Children.Add(_vramText);
        }
        else
        {
            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            header.Children.Add(label);

            _vramText.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(_vramText, 1);
            header.Children.Add(_vramText);

            stack.Children.Add(header);
        }

        _vramBar = WidgetVisuals.Bar(width, Math.Max(5, 7 * scale), WidgetVisuals.Track(theme), WidgetVisuals.Accent(theme));
        stack.Children.Add(_vramBar);

        _vramRow = stack;
        return stack;
    }

    private void Update(SystemStats stats)
    {
        var theme = Context?.Theme;
        if (theme is null)
        {
            return;
        }

        try
        {
            if (!stats.HasGpu)
            {
                var muted = WidgetVisuals.Secondary(theme);
                SetGaugeFraction(0);
                ApplyGaugeColor(muted);

                if (_percentText is not null)
                {
                    _percentText.Text = "—";
                }

                PushHistory(0);

                if (_vramRow is not null)
                {
                    _vramRow.Visibility = Visibility.Collapsed;
                }

                if (_caption is not null)
                {
                    _caption.Text = "GPU情報を取得できません";
                    _caption.Foreground = new SolidColorBrush(muted);
                }

                return;
            }

            var fraction = Math.Clamp(stats.GpuPercent, 0, 100) / 100.0;
            var color = WidgetVisuals.LoadColor(theme, fraction, _colorByLoad);

            SetGaugeFraction(fraction);
            ApplyGaugeColor(color);

            if (_percentBrush is not null)
            {
                _percentBrush.Color = color;
            }

            if (_percentText is not null)
            {
                _percentText.Text = stats.GpuPercent.ToString("0", CultureInfo.InvariantCulture) + "%";
            }

            PushHistory(fraction);

            if (_vramRow is not null)
            {
                _vramRow.Visibility = _vramAllowed ? Visibility.Visible : Visibility.Collapsed;
                if (_vramAllowed)
                {
                    UpdateVram(stats);
                }
            }

            if (_caption is not null)
            {
                var name = _showGpuName && HardwareInfo.GpuName.Length > 0 ? HardwareInfo.GpuName : "GPU";

                _caption.Text = _showFps && stats.HasFrameRate
                    ? $"{name} ・ {stats.Fps:0} fps"
                    : name;

                _caption.Foreground = new SolidColorBrush(WidgetVisuals.Secondary(theme));
            }
        }
        catch (Exception ex)
        {
            Crash.Log(ex, "GpuMonitorWidget.Update");
        }
    }

    private void UpdateVram(SystemStats stats)
    {
        if (_vramText is null || _vramBar is null)
        {
            return;
        }

        if (stats.GpuMemTotalGb > 0)
        {
            _vramBar.Visibility = Visibility.Visible;
            var fraction = Math.Clamp(stats.GpuMemUsedGb / stats.GpuMemTotalGb, 0, 1);
            WidgetVisuals.SetBar(_vramBar, fraction);
            _vramText.Text = $"{WidgetVisuals.FormatGb(stats.GpuMemUsedGb)} / {WidgetVisuals.FormatGb(stats.GpuMemTotalGb)}";
        }
        else
        {
            _vramBar.Visibility = Visibility.Collapsed;
            _vramText.Text = WidgetVisuals.FormatGb(stats.GpuMemUsedGb);
        }
    }

    private void PushHistory(double fraction)
    {
        _history.Enqueue(fraction);
        while (_history.Count > HistoryCapacity)
        {
            _history.Dequeue();
        }

        if (_spark is not null)
        {
            WidgetVisuals.SetSpark(_spark, _history.ToArray(), HistoryCapacity);
        }

        if (_area is not null)
        {
            WidgetVisuals.SetArea(_area, _history.ToArray(), HistoryCapacity);
        }
    }

    private void ApplyGaugeColor(Color color)
    {
        if (_gaugeBrush is not null)
        {
            _gaugeBrush.Color = color;
        }

        if (_bar is not null)
        {
            WidgetVisuals.SetBarColor(_bar, color);
        }

        if (_areaBrush is not null)
        {
            _areaBrush.Color = ColorUtil.Fade(color, 0.28);
        }

        if (_sparkBrush is not null)
        {
            _sparkBrush.Color = color;
        }
    }

    private void SetGaugeFraction(double fraction)
    {
        if (_ring is not null)
        {
            WidgetVisuals.SetRing(_ring, _gaugeSize, _gaugeThickness, fraction);
        }

        if (_bar is not null)
        {
            WidgetVisuals.SetBar(_bar, fraction);
        }
    }

    private static SystemStats SampleStats() => new(
        42, 58, 19.5, 32.0, 47, 220, 476, 1840, 260, 78, false, true, TimeSpan.FromHours(9))
    {
        HasGpu = true,
        GpuPercent = 64,
        GpuMemUsedGb = 6.2,
        GpuMemTotalGb = 12.0,
    };
}
