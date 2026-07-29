using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Widgets.App.Common;
using Widgets.App.Controls;
using Widgets.App.Models;
using Windows.UI;

namespace Widgets.App.Widgets;

/// <summary>A month grid with the weekend tinted and today wearing an accent pill.</summary>
public sealed partial class CalendarWidget : WidgetViewBase
{
    private const int Rows = 6;

    private DateTime _builtFor = DateTime.MinValue;

    public CalendarWidget()
    {
        InitializeComponent();
    }

    public override TimeSpan TickInterval => TimeSpan.FromSeconds(60);

    protected override void OnApply(WidgetRenderContext context)
    {
        Build(context, DateTime.Today);
    }

    public override void OnTick(DateTimeOffset now)
    {
        if (Context is { } context && now.LocalDateTime.Date != _builtFor)
        {
            Build(context, now.LocalDateTime.Date);
        }
    }

    private void Build(WidgetRenderContext context, DateTime today)
    {
        _builtFor = today;

        var theme = context.Theme;
        var first = (DayOfWeek)Math.Clamp(context.GetInt(WidgetSettingKeys.FirstDayOfWeek), 0, 6);
        var highlight = context.GetBool(WidgetSettingKeys.HighlightToday, true);
        var showWeeks = context.GetBool(WidgetSettingKeys.ShowWeekNumbers);

        var tint = WidgetVisuals.Tint(theme);
        var secondary = WidgetVisuals.Secondary(theme);
        var accent = WidgetVisuals.Accent(theme);

        var titleSize = context.Size switch
        {
            WidgetSize.Large => 20.0,
            WidgetSize.Wide => 16.0,
            _ => 14.0,
        };

        WidgetVisuals.Style(TitleText, theme, titleSize, tint, 200);
        TitleText.Text = today.ToString("yyyy年M月", CultureInfo.CurrentCulture);

        var gridHeight = Math.Max(1, context.Height - WidgetVisuals.Size(theme, titleSize) * 1.5);
        var columns = showWeeks ? 8 : 7;
        var cellHeight = gridHeight / (Rows + 1);
        var cellWidth = context.Width / columns;
        var cellSize = Math.Min(cellWidth, cellHeight);
        var dayFont = Math.Max(7.0, Math.Min(cellHeight * 0.56, cellWidth * 0.52) * Math.Clamp(theme.FontScale, 0.5, 2.0));

        Days.Children.Clear();
        Days.RowDefinitions.Clear();
        Days.ColumnDefinitions.Clear();

        for (var r = 0; r <= Rows; r++)
        {
            Days.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        }

        if (showWeeks)
        {
            Days.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.7, GridUnitType.Star) });
        }

        for (var c = 0; c < 7; c++)
        {
            Days.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        var dayOffset = showWeeks ? 1 : 0;

        for (var c = 0; c < 7; c++)
        {
            var day = (DayOfWeek)(((int)first + c) % 7);
            Days.Children.Add(Cell(
                WidgetVisuals.WeekdayJa(day),
                dayFont * 0.82,
                WeekendColor(day, secondary, 0.75, ColorUtil.Fade(secondary, 0.9)),
                theme,
                0,
                c + dayOffset,
                weightDelta: 100));
        }

        var monthStart = new DateTime(today.Year, today.Month, 1);
        var lead = ((int)monthStart.DayOfWeek - (int)first + 7) % 7;
        var cursor = monthStart.AddDays(-lead);

        for (var r = 0; r < Rows; r++)
        {
            if (showWeeks)
            {
                Days.Children.Add(Cell(
                    ISOWeek.GetWeekOfYear(cursor).ToString(CultureInfo.InvariantCulture),
                    dayFont * 0.7,
                    ColorUtil.Fade(secondary, 0.6),
                    theme,
                    r + 1,
                    0));
            }

            for (var c = 0; c < 7; c++)
            {
                var inMonth = cursor.Month == today.Month;
                var isToday = cursor.Date == today.Date;

                Color color;
                if (!inMonth)
                {
                    color = ColorUtil.Fade(secondary, 0.4);
                }
                else if (isToday && highlight)
                {
                    color = ColorUtil.ReadableForeground(accent);
                }
                else
                {
                    color = WeekendColor(cursor.DayOfWeek, tint, 0.55, tint);
                }

                var cell = Cell(
                    cursor.Day.ToString(CultureInfo.InvariantCulture),
                    dayFont,
                    color,
                    theme,
                    r + 1,
                    c + dayOffset,
                    weightDelta: isToday ? 200 : 0);

                if (isToday && highlight)
                {
                    cell.Background = new SolidColorBrush(accent);
                    cell.CornerRadius = new CornerRadius(cellSize * 0.5);
                    cell.Width = cellSize * 0.94;
                    cell.Height = cellSize * 0.94;
                }
                else if (isToday)
                {
                    cell.BorderBrush = new SolidColorBrush(accent);
                    cell.BorderThickness = new Thickness(1.5);
                    cell.CornerRadius = new CornerRadius(cellSize * 0.5);
                    cell.Width = cellSize * 0.94;
                    cell.Height = cellSize * 0.94;
                }

                Days.Children.Add(cell);
                cursor = cursor.AddDays(1);
            }
        }
    }

    private static Color WeekendColor(DayOfWeek day, Color baseColor, double strength, Color weekdayColor) => day switch
    {
        DayOfWeek.Sunday => WidgetVisuals.Mix(baseColor, WidgetVisuals.SundayColor, strength),
        DayOfWeek.Saturday => WidgetVisuals.Mix(baseColor, WidgetVisuals.SaturdayColor, strength),
        _ => weekdayColor,
    };

    private static Border Cell(
        string text,
        double fontSize,
        Color color,
        WidgetTheme theme,
        int row,
        int column,
        int weightDelta = 0)
    {
        var border = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = text,
                FontFamily = WidgetVisuals.Font(theme),
                FontSize = Math.Max(6, fontSize),
                FontWeight = WidgetVisuals.Weight(theme, weightDelta),
                Foreground = new SolidColorBrush(color),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
            },
        };

        Grid.SetRow(border, row);
        Grid.SetColumn(border, column);
        return border;
    }
}
