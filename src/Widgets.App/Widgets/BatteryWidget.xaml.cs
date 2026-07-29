using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Widgets.App.Common;
using Widgets.App.Controls;
using Widgets.App.Models;
using Widgets.App.Services;
using Windows.UI;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace Widgets.App.Widgets;

/// <summary>Charge level as a ring or a battery bar, with charging and no-battery states.</summary>
public sealed partial class BatteryWidget : WidgetViewBase
{
    private Path? _ring;
    private Border? _fill;
    private TextBlock? _percentText;
    private TextBlock? _bolt;
    private TextBlock? _caption;
    private SolidColorBrush? _fillBrush;

    private double _gaugeSize;
    private double _gaugeThickness;
    private double _barWidth;
    private double _barInnerWidth;
    private bool _subscribed;

    public BatteryWidget()
    {
        InitializeComponent();
    }

    protected override void OnApply(WidgetRenderContext context)
    {
        var theme = context.Theme;
        var style = context.GetString(WidgetSettingKeys.GaugeStyle, "Ring");
        var showText = context.GetBool(WidgetSettingKeys.ShowPercentageText, true);

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
        _fill = null;
        _percentText = null;
        _bolt = null;

        var stack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 6 * scale,
        };

        if (style == "Bar")
        {
            stack.Children.Add(BuildBar(theme, context, scale, showText));
        }
        else
        {
            stack.Children.Add(BuildRing(theme, context, scale, showText));
        }

        _caption = WidgetVisuals.Text(theme, 12 * scale, WidgetVisuals.Secondary(theme));
        _caption.HorizontalAlignment = HorizontalAlignment.Center;
        _caption.MaxWidth = context.Width;
        stack.Children.Add(_caption);

        Root.Children.Add(stack);

        if (!_subscribed)
        {
            AppServices.SystemStats.Updated += OnStatsUpdated;
            _subscribed = true;
        }

        Update(context.IsPreview
            ? new SystemStats(0, 0, 0, 0, 0, 0, 0, 0, 0, 68, true, true, TimeSpan.FromHours(4))
            : AppServices.SystemStats.Current);
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

    private FrameworkElement BuildRing(WidgetTheme theme, WidgetRenderContext context, double scale, bool showText)
    {
        _gaugeSize = Math.Max(40, Math.Min(context.Width, context.Height * 0.72) * (context.Size == WidgetSize.Large ? 0.66 : 0.78));
        _gaugeThickness = Math.Max(4, _gaugeSize * 0.11);

        var dial = new Grid
        {
            Width = _gaugeSize,
            Height = _gaugeSize,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        dial.Children.Add(WidgetVisuals.RingTrack(_gaugeSize, _gaugeThickness, WidgetVisuals.Track(theme)));

        _fillBrush = new SolidColorBrush(WidgetVisuals.Accent(theme));
        _ring = WidgetVisuals.Ring(_gaugeSize, _gaugeThickness, WidgetVisuals.Accent(theme));
        _ring.Stroke = _fillBrush;
        dial.Children.Add(_ring);

        var center = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 0,
        };

        _bolt = WidgetVisuals.Glyph("", _gaugeSize * 0.2, WidgetVisuals.Accent(theme));
        _bolt.Visibility = Visibility.Collapsed;
        center.Children.Add(_bolt);

        if (showText)
        {
            _percentText = WidgetVisuals.Text(theme, _gaugeSize * 0.27, WidgetVisuals.Tint(theme), 100);
            _percentText.HorizontalAlignment = HorizontalAlignment.Center;
            center.Children.Add(_percentText);
        }

        dial.Children.Add(center);
        return dial;
    }

    private FrameworkElement BuildBar(WidgetTheme theme, WidgetRenderContext context, double scale, bool showText)
    {
        _barWidth = Math.Max(56, Math.Min(context.Width * 0.86, 200 * scale));
        var barHeight = _barWidth * 0.42;
        var radius = barHeight * 0.28;
        var edge = Math.Max(1.5, barHeight * 0.08);
        var inset = barHeight * 0.1;
        _barInnerWidth = Math.Max(1, _barWidth - 2 * (edge + inset));

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 8 * scale,
        };

        var shell = new Border
        {
            Width = _barWidth,
            Height = barHeight,
            CornerRadius = new CornerRadius(radius),
            BorderThickness = new Thickness(edge),
            BorderBrush = new SolidColorBrush(ColorUtil.Fade(WidgetVisuals.Secondary(theme), 0.8)),
            Padding = new Thickness(inset),
        };

        var track = new Grid();
        _fillBrush = new SolidColorBrush(WidgetVisuals.Accent(theme));
        _fill = new Border
        {
            Background = _fillBrush,
            CornerRadius = new CornerRadius(radius * 0.75),
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = 0,
        };

        track.Children.Add(_fill);

        _bolt = WidgetVisuals.Glyph("", barHeight * 0.5, WidgetVisuals.Accent(theme));
        _bolt.Visibility = Visibility.Collapsed;
        track.Children.Add(_bolt);

        shell.Child = track;

        var cap = new Border
        {
            Width = Math.Max(3, barHeight * 0.1),
            Height = barHeight * 0.38,
            VerticalAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(1.5),
            Background = new SolidColorBrush(ColorUtil.Fade(WidgetVisuals.Secondary(theme), 0.8)),
            Margin = new Thickness(2, 0, 0, 0),
        };

        var battery = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        battery.Children.Add(shell);
        battery.Children.Add(cap);

        row.Children.Add(battery);

        if (showText)
        {
            _percentText = WidgetVisuals.Text(theme, 22 * scale, WidgetVisuals.Tint(theme), 100);
            _percentText.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(_percentText);
        }

        return row;
    }

    private void Update(SystemStats stats)
    {
        var theme = Context?.Theme;
        if (theme is null || _caption is null)
        {
            return;
        }

        if (!stats.HasBattery)
        {
            _caption.Text = "バッテリー非搭載";
            _caption.Foreground = new SolidColorBrush(WidgetVisuals.Secondary(theme));
            SetLevel(0);
            if (_percentText is not null)
            {
                _percentText.Text = "—";
            }

            if (_bolt is not null)
            {
                _bolt.Visibility = Visibility.Collapsed;
            }

            return;
        }

        var percent = Math.Clamp(stats.BatteryPercent, 0, 100);

        var color = percent switch
        {
            < 10 => WidgetVisuals.DangerColor,
            < 20 => WidgetVisuals.WarningColor,
            _ => WidgetVisuals.Accent(theme),
        };

        if (_fillBrush is not null)
        {
            _fillBrush.Color = color;
        }

        if (_percentText is not null)
        {
            _percentText.Text = percent.ToString(CultureInfo.InvariantCulture) + "%";
            _percentText.Foreground = new SolidColorBrush(percent < 20 ? color : WidgetVisuals.Tint(theme));
        }

        if (_bolt is not null)
        {
            _bolt.Visibility = stats.IsCharging ? Visibility.Visible : Visibility.Collapsed;
            _bolt.Foreground = new SolidColorBrush(WidgetVisuals.Accent(theme));
        }

        _caption.Text = stats.IsCharging ? "電源に接続" : percent < 20 ? "残量わずか" : "バッテリー";
        _caption.Foreground = new SolidColorBrush(percent < 20 && !stats.IsCharging ? color : WidgetVisuals.Secondary(theme));

        SetLevel(percent / 100.0);
    }

    private void SetLevel(double fraction)
    {
        if (_ring is not null)
        {
            WidgetVisuals.SetRing(_ring, _gaugeSize, _gaugeThickness, fraction);
        }

        if (_fill is not null)
        {
            _fill.Width = Math.Max(0, _barInnerWidth * Math.Clamp(fraction, 0, 1));
        }
    }
}
