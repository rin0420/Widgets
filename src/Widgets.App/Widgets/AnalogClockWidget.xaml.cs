using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Widgets.App.Common;
using Widgets.App.Controls;
using Widgets.App.Models;
using Windows.UI;

namespace Widgets.App.Widgets;

/// <summary>
/// A hand-drawn clock face. Everything is laid out on a fixed 200×200 canvas and scaled by a
/// Viewbox, so the geometry is resolution independent and never needs recomputing on resize.
/// </summary>
public sealed partial class AnalogClockWidget : WidgetViewBase
{
    private const double C = 100.0;

    private readonly Line _hourHand = new();
    private readonly Line _minuteHand = new();
    private readonly Line _secondHand = new();
    private readonly Ellipse _cap = new();
    private readonly Ellipse _capInner = new();

    private readonly RotateTransform _hourRotation = new() { CenterX = C, CenterY = C };
    private readonly RotateTransform _minuteRotation = new() { CenterX = C, CenterY = C };
    private readonly RotateTransform _secondRotation = new() { CenterX = C, CenterY = C };

    private bool _showSecondHand = true;

    public AnalogClockWidget()
    {
        InitializeComponent();

        _hourHand.RenderTransform = _hourRotation;
        _minuteHand.RenderTransform = _minuteRotation;
        _secondHand.RenderTransform = _secondRotation;
    }

    public override TimeSpan TickInterval
        => _showSecondHand ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(10);

    protected override void OnApply(WidgetRenderContext context)
    {
        var theme = context.Theme;
        var style = context.GetString(WidgetSettingKeys.ClockFaceStyle, "Ticks");
        _showSecondHand = context.GetBool(WidgetSettingKeys.ShowSecondHand, true);

        var tint = WidgetVisuals.Tint(theme);
        var secondary = WidgetVisuals.Secondary(theme);
        var accent = WidgetVisuals.Accent(theme);

        Face.Children.Clear();

        Face.Children.Add(new Ellipse
        {
            Width = 196,
            Height = 196,
            Stroke = new SolidColorBrush(ColorUtil.Fade(secondary, 0.5)),
            StrokeThickness = 1.5,
            Margin = new Thickness(2),
        });

        switch (style)
        {
            case "Numbers":
                BuildNumbers(theme, tint, secondary);
                break;
            case "Minimal":
                BuildMinimal(tint);
                break;
            default:
                BuildTicks(tint, secondary);
                break;
        }

        StyleHand(_hourHand, 52, 6.5, tint);
        StyleHand(_minuteHand, 76, 4.5, tint);
        StyleHand(_secondHand, 84, 1.8, accent, tail: 20);

        _cap.Width = _cap.Height = 11;
        _cap.Fill = new SolidColorBrush(tint);
        Canvas.SetLeft(_cap, C - 5.5);
        Canvas.SetTop(_cap, C - 5.5);

        _capInner.Width = _capInner.Height = 4.5;
        _capInner.Fill = new SolidColorBrush(accent);
        Canvas.SetLeft(_capInner, C - 2.25);
        Canvas.SetTop(_capInner, C - 2.25);

        Face.Children.Add(_hourHand);
        Face.Children.Add(_minuteHand);
        Face.Children.Add(_secondHand);
        Face.Children.Add(_cap);
        Face.Children.Add(_capInner);

        _secondHand.Visibility = _showSecondHand ? Visibility.Visible : Visibility.Collapsed;
        _capInner.Visibility = _showSecondHand ? Visibility.Visible : Visibility.Collapsed;

        OnTick(DateTimeOffset.Now);
    }

    public override void OnTick(DateTimeOffset now)
    {
        var t = now.LocalDateTime;
        var seconds = t.Second + t.Millisecond / 1000.0;
        var minutes = t.Minute + seconds / 60.0;
        var hours = t.Hour % 12 + minutes / 60.0;

        _hourRotation.Angle = hours * 30.0;
        _minuteRotation.Angle = minutes * 6.0;
        _secondRotation.Angle = Math.Floor(seconds) * 6.0;
    }

    private static void StyleHand(Line hand, double length, double thickness, Color color, double tail = 12)
    {
        hand.X1 = C;
        hand.Y1 = C + tail;
        hand.X2 = C;
        hand.Y2 = C - length;
        hand.Stroke = new SolidColorBrush(color);
        hand.StrokeThickness = thickness;
        hand.StrokeStartLineCap = PenLineCap.Round;
        hand.StrokeEndLineCap = PenLineCap.Round;
    }

    private void BuildTicks(Color tint, Color secondary)
    {
        for (var i = 0; i < 60; i++)
        {
            var major = i % 5 == 0;
            var angle = i * 6.0 * Math.PI / 180.0;
            var outer = 90.0;
            var inner = major ? 78.0 : 85.0;

            Face.Children.Add(new Line
            {
                X1 = C + Math.Sin(angle) * inner,
                Y1 = C - Math.Cos(angle) * inner,
                X2 = C + Math.Sin(angle) * outer,
                Y2 = C - Math.Cos(angle) * outer,
                Stroke = new SolidColorBrush(major ? tint : ColorUtil.Fade(secondary, 0.7)),
                StrokeThickness = major ? 3.0 : 1.2,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
            });
        }
    }

    private void BuildMinimal(Color tint)
    {
        for (var i = 0; i < 12; i += 3)
        {
            var angle = i * 30.0 * Math.PI / 180.0;

            Face.Children.Add(new Line
            {
                X1 = C + Math.Sin(angle) * 76,
                Y1 = C - Math.Cos(angle) * 76,
                X2 = C + Math.Sin(angle) * 90,
                Y2 = C - Math.Cos(angle) * 90,
                Stroke = new SolidColorBrush(tint),
                StrokeThickness = 3.5,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
            });
        }
    }

    private void BuildNumbers(WidgetTheme theme, Color tint, Color secondary)
    {
        for (var i = 1; i <= 12; i++)
        {
            var angle = i * 30.0 * Math.PI / 180.0;
            var x = C + Math.Sin(angle) * 76;
            var y = C - Math.Cos(angle) * 76;

            var host = new Border
            {
                Width = 34,
                Height = 28,
                Child = new TextBlock
                {
                    Text = i.ToString(CultureInfo.InvariantCulture),
                    FontFamily = WidgetVisuals.Font(theme),
                    FontSize = 19,
                    FontWeight = WidgetVisuals.Weight(theme, i % 3 == 0 ? 200 : 0),
                    Foreground = new SolidColorBrush(i % 3 == 0 ? tint : ColorUtil.Fade(secondary, 0.85)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };

            Canvas.SetLeft(host, x - 17);
            Canvas.SetTop(host, y - 14);
            Face.Children.Add(host);
        }
    }
}
