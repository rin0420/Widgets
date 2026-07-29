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

namespace Widgets.App.Widgets;

/// <summary>Big CPU load number, a history graph, and a per-core grid, driven by the shared stats service.</summary>
public sealed partial class CpuMonitorWidget : WidgetViewBase
{
    private const int HistoryCapacity = 60;
    private const int MaxCoreTiles = 64;
    private const int CoreBarThreshold = 16;
    private const double EdgeMargin = 8;

    private readonly Queue<double> _history = new(HistoryCapacity);
    private readonly List<Grid> _coreBars = [];
    private readonly List<SolidColorBrush> _coreTileBrushes = [];

    private TextBlock? _percentText;
    private SolidColorBrush? _percentBrush;
    private Polygon? _area;
    private SolidColorBrush? _areaBrush;
    private Polyline? _spark;
    private SolidColorBrush? _sparkBrush;
    private TextBlock? _clockText;
    private TextBlock? _infoText;

    private bool _colorByLoad = true;
    private bool _clockShowMax;
    private bool _infoShowClock;
    private bool _infoShowProcess;
    private bool _infoShowName;
    private bool _subscribed;

    public CpuMonitorWidget()
    {
        InitializeComponent();
    }

    protected override void OnApply(WidgetRenderContext context)
    {
        var theme = context.Theme;
        var style = context.GetString(WidgetSettingKeys.CpuDisplayStyle, "Combined");
        _colorByLoad = context.GetBool(WidgetSettingKeys.ColorByLoad, true);

        var showPercentText = context.GetBool(WidgetSettingKeys.ShowPercentageText, true);
        var showCoreGridSetting = context.GetBool(WidgetSettingKeys.ShowCoreGrid, true);
        var showCpuName = context.GetBool(WidgetSettingKeys.ShowCpuName, true) && HardwareInfo.CpuName.Length > 0;
        var showCpuClock = context.GetBool(WidgetSettingKeys.ShowCpuClock, true);
        var showProcessCount = context.GetBool(WidgetSettingKeys.ShowProcessCount, false);

        Root.Children.Clear();
        _percentText = null;
        _percentBrush = null;
        _area = null;
        _areaBrush = null;
        _spark = null;
        _sparkBrush = null;
        _clockText = null;
        _infoText = null;
        _clockShowMax = false;
        _infoShowClock = false;
        _infoShowProcess = false;
        _infoShowName = false;
        _coreBars.Clear();
        _coreTileBrushes.Clear();

        var buildStats = context.IsPreview ? SampleStats() : AppServices.SystemStats.Current;

        // Sized from the core count, not from the sample: per-core usage needs two poll ticks to
        // exist, so a grid built off CoreUsage.Count would be empty here and never come back.
        var coreCount = showCoreGridSetting ? Math.Min(MaxCoreTiles, HardwareInfo.LogicalCores) : 0;

        var root = style switch
        {
            "Cores" => BuildCoresFocused(theme, context, coreCount, showPercentText),
            "Graph" => BuildGraphFocused(theme, context, showPercentText),
            _ => BuildCombined(theme, context, coreCount, showPercentText, showCpuName, showCpuClock, showProcessCount),
        };

        Root.Children.Add(root);

        if (!_subscribed)
        {
            AppServices.SystemStats.Updated += OnStatsUpdated;
            _subscribed = true;
        }

        Update(buildStats);
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

    // ---- Combined style: the per-size layouts described in the widget spec ----------------

    private FrameworkElement BuildCombined(
        WidgetTheme theme,
        WidgetRenderContext context,
        int coreCount,
        bool showPercentText,
        bool showCpuName,
        bool showCpuClock,
        bool showProcessCount) => context.Size switch
    {
        WidgetSize.Medium => BuildMediumCombined(theme, context, showPercentText, showCpuName, showCpuClock, showProcessCount),
        WidgetSize.Large => BuildLargeCombined(theme, context, coreCount, showPercentText, showCpuName, showCpuClock, showProcessCount),
        WidgetSize.Wide => BuildWideCombined(theme, context, coreCount, showPercentText, showCpuName, showCpuClock, showProcessCount),
        WidgetSize.Tall => BuildTallCombined(theme, context, coreCount, showPercentText, showCpuName, showCpuClock, showProcessCount),
        _ => BuildSmallCombined(theme, context, showPercentText),
    };

    private FrameworkElement BuildSmallCombined(WidgetTheme theme, WidgetRenderContext context, bool showPercentText)
    {
        var percentSize = Math.Clamp(context.Height * 0.30, 30, 54);
        const double spacing = 6.0;
        var headerH = showPercentText ? percentSize * 1.15 : 0;
        var graphH = Math.Max(24, context.Height - headerH - spacing - EdgeMargin);
        var graphW = Math.Max(24, context.Width - EdgeMargin * 2);

        var stack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = spacing,
        };

        if (showPercentText)
        {
            stack.Children.Add(BuildPercentText(theme, percentSize));
        }

        stack.Children.Add(BuildGraph(theme, graphW, graphH));
        return stack;
    }

    private FrameworkElement BuildMediumCombined(
        WidgetTheme theme,
        WidgetRenderContext context,
        bool showPercentText,
        bool showCpuName,
        bool showCpuClock,
        bool showProcessCount)
    {
        _infoShowClock = showCpuClock;
        _infoShowProcess = showProcessCount;
        _infoShowName = showCpuName;
        var infoEligible = _infoShowClock || _infoShowProcess || _infoShowName;

        const double infoSize = 12.0;
        const double spacing = 6.0;
        var infoH = infoEligible ? infoSize * 1.6 : 0;
        var mainH = Math.Max(24, context.Height - infoH - (infoEligible ? spacing : 0) - EdgeMargin);

        var leftW = Math.Max(60, context.Width * 0.36);
        var rightW = Math.Max(40, context.Width - leftW - 16);
        var percentSize = Math.Clamp(mainH * 0.55, 28, 64);

        var root = new Grid { RowSpacing = spacing };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        if (infoEligible)
        {
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        var mainRow = new Grid { ColumnSpacing = 16 };
        mainRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(leftW) });
        mainRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var percentHost = new Grid { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        if (showPercentText)
        {
            percentHost.Children.Add(BuildPercentText(theme, percentSize));
        }

        mainRow.Children.Add(percentHost);

        var graph = BuildGraph(theme, rightW, Math.Max(24, mainH * 0.86));
        graph.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(graph, 1);
        mainRow.Children.Add(graph);

        root.Children.Add(mainRow);

        if (infoEligible)
        {
            _infoText = WidgetVisuals.Text(theme, infoSize, WidgetVisuals.Secondary(theme));
            _infoText.HorizontalAlignment = HorizontalAlignment.Left;
            _infoText.MaxWidth = context.Width;
            Grid.SetRow(_infoText, 1);
            root.Children.Add(_infoText);
        }

        return root;
    }

    private FrameworkElement BuildLargeCombined(
        WidgetTheme theme,
        WidgetRenderContext context,
        int coreCount,
        bool showPercentText,
        bool showCpuName,
        bool showCpuClock,
        bool showProcessCount)
    {
        _infoShowProcess = showProcessCount;
        _infoShowName = showCpuName;
        var infoEligible = _infoShowProcess || _infoShowName;
        _clockShowMax = showCpuClock;

        var percentSize = Math.Clamp(context.Height * 0.16, 34, 64);
        const double clockSize = 15.0;
        const double infoSize = 13.0;
        const double spacing = 8.0;

        var topH = (showPercentText ? percentSize * 1.15 : 0) + (showCpuClock ? clockSize * 1.4 : 0);
        var infoH = infoEligible ? infoSize * 1.6 : 0;
        var gapCount = 1 + (coreCount > 0 ? 1 : 0) + (infoEligible ? 1 : 0);
        var remaining = Math.Max(40, context.Height - topH - infoH - gapCount * spacing - EdgeMargin);

        var graphH = coreCount > 0 ? remaining * 0.56 : remaining;
        var coreH = remaining - graphH;
        var width = Math.Max(40, context.Width - EdgeMargin * 2);

        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Spacing = spacing };

        var top = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Spacing = 2 };
        if (showPercentText)
        {
            top.Children.Add(BuildPercentText(theme, percentSize));
        }

        if (showCpuClock)
        {
            _clockText = WidgetVisuals.Text(theme, clockSize, WidgetVisuals.Secondary(theme));
            _clockText.HorizontalAlignment = HorizontalAlignment.Center;
            top.Children.Add(_clockText);
        }

        stack.Children.Add(top);
        stack.Children.Add(BuildGraph(theme, width, graphH));

        if (coreCount > 0 && BuildCoreGrid(theme, width, coreH, coreCount) is { } coreGrid)
        {
            stack.Children.Add(coreGrid);
        }

        if (infoEligible)
        {
            _infoText = WidgetVisuals.Text(theme, infoSize, WidgetVisuals.Secondary(theme));
            _infoText.HorizontalAlignment = HorizontalAlignment.Center;
            _infoText.MaxWidth = width;
            stack.Children.Add(_infoText);
        }

        return stack;
    }

    private FrameworkElement BuildWideCombined(
        WidgetTheme theme,
        WidgetRenderContext context,
        int coreCount,
        bool showPercentText,
        bool showCpuName,
        bool showCpuClock,
        bool showProcessCount)
    {
        var showCoreGrid = coreCount > 0;
        if (!showCoreGrid)
        {
            _infoShowClock = showCpuClock;
            _infoShowProcess = showProcessCount;
            _infoShowName = showCpuName;
        }

        var percentSize = Math.Clamp(context.Height * 0.42, 34, 64);
        const double spacing = 16.0;

        var leftW = Math.Max(70, context.Width * 0.2);
        var rightW = Math.Max(90, context.Width * 0.26);
        var centerW = Math.Max(60, context.Width - leftW - rightW - spacing * 2);

        var root = new Grid { ColumnSpacing = spacing };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(leftW) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(rightW) });

        var percentHost = new Grid { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        if (showPercentText)
        {
            percentHost.Children.Add(BuildPercentText(theme, percentSize));
        }

        root.Children.Add(percentHost);

        var graphH = Math.Max(30, context.Height - EdgeMargin * 2);
        var graph = BuildGraph(theme, centerW, graphH);
        graph.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(graph, 1);
        root.Children.Add(graph);

        FrameworkElement rightContent;
        if (showCoreGrid)
        {
            rightContent = BuildCoreGrid(theme, rightW, Math.Max(30, context.Height - EdgeMargin * 2), coreCount) ?? new Grid();
        }
        else
        {
            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            if (_infoShowClock || _infoShowProcess || _infoShowName)
            {
                _infoText = WidgetVisuals.Text(theme, 13, WidgetVisuals.Secondary(theme));
                _infoText.MaxWidth = rightW;
                info.Children.Add(_infoText);
            }

            rightContent = info;
        }

        rightContent.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(rightContent, 2);
        root.Children.Add(rightContent);

        return root;
    }

    private FrameworkElement BuildTallCombined(
        WidgetTheme theme,
        WidgetRenderContext context,
        int coreCount,
        bool showPercentText,
        bool showCpuName,
        bool showCpuClock,
        bool showProcessCount)
    {
        _infoShowClock = showCpuClock;
        _infoShowProcess = showProcessCount;
        _infoShowName = showCpuName;
        var infoEligible = _infoShowClock || _infoShowProcess || _infoShowName;

        var percentSize = Math.Clamp(context.Height * 0.15, 30, 50);
        const double infoSize = 12.0;
        const double spacing = 8.0;

        var headerH = showPercentText ? percentSize * 1.15 : 0;
        var infoH = infoEligible ? infoSize * 1.6 : 0;
        var gapCount = 1 + (coreCount > 0 ? 1 : 0) + (infoEligible ? 1 : 0);
        var remaining = Math.Max(40, context.Height - headerH - infoH - gapCount * spacing - EdgeMargin);

        var graphH = coreCount > 0 ? remaining * 0.52 : remaining;
        var coreH = remaining - graphH;
        var width = Math.Max(30, context.Width - EdgeMargin * 1.5);

        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Spacing = spacing };

        if (showPercentText)
        {
            stack.Children.Add(BuildPercentText(theme, percentSize));
        }

        stack.Children.Add(BuildGraph(theme, width, graphH));

        if (coreCount > 0 && BuildCoreGrid(theme, width, coreH, coreCount) is { } coreGrid)
        {
            stack.Children.Add(coreGrid);
        }

        if (infoEligible)
        {
            _infoText = WidgetVisuals.Text(theme, infoSize, WidgetVisuals.Secondary(theme));
            _infoText.HorizontalAlignment = HorizontalAlignment.Center;
            _infoText.MaxWidth = width;
            stack.Children.Add(_infoText);
        }

        return stack;
    }

    // ---- Cores / Graph styles: one visual maximized across every size --------------------

    private FrameworkElement BuildCoresFocused(WidgetTheme theme, WidgetRenderContext context, int coreCount, bool showPercentText)
    {
        if (coreCount <= 0)
        {
            var percentSize = Math.Clamp(Math.Min(context.Width, context.Height) * 0.34, 30, 84);
            var center = new Grid { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            if (showPercentText)
            {
                center.Children.Add(BuildPercentText(theme, percentSize));
            }

            return center;
        }

        var stripSize = Math.Clamp(Math.Min(context.Width, context.Height) * 0.14, 14, 30);
        const double spacing = 6.0;
        var stripH = showPercentText ? stripSize * 1.4 : 0;

        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Spacing = spacing };

        if (showPercentText)
        {
            var host = new Grid { HorizontalAlignment = HorizontalAlignment.Center };
            host.Children.Add(BuildPercentText(theme, stripSize));
            stack.Children.Add(host);
        }

        var gridW = Math.Max(40, context.Width - EdgeMargin * 2);
        var gridH = Math.Max(40, context.Height - stripH - (showPercentText ? spacing : 0) - EdgeMargin);
        if (BuildCoreGrid(theme, gridW, gridH, coreCount) is { } coreGrid)
        {
            stack.Children.Add(coreGrid);
        }

        return stack;
    }

    private FrameworkElement BuildGraphFocused(WidgetTheme theme, WidgetRenderContext context, bool showPercentText)
    {
        var width = Math.Max(40, context.Width - EdgeMargin * 1.5);
        var height = Math.Max(30, context.Height - EdgeMargin * 1.5);

        var host = new Grid { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        host.Children.Add(BuildGraph(theme, width, height));

        if (showPercentText)
        {
            var percentSize = Math.Clamp(Math.Min(width, height) * 0.34, 28, 72);
            var overlay = BuildPercentText(theme, percentSize);
            overlay.VerticalAlignment = VerticalAlignment.Center;
            host.Children.Add(overlay);
        }

        return host;
    }

    // ---- Shared building blocks ------------------------------------------------------------

    private TextBlock BuildPercentText(WidgetTheme theme, double fontSize)
    {
        _percentText = WidgetVisuals.Text(theme, fontSize, WidgetVisuals.Accent(theme), 100);
        _percentText.HorizontalAlignment = HorizontalAlignment.Center;
        _percentBrush = (SolidColorBrush)_percentText.Foreground;
        return _percentText;
    }

    private FrameworkElement BuildGraph(WidgetTheme theme, double width, double height)
    {
        width = Math.Max(20, width);
        height = Math.Max(16, height);

        var accent = WidgetVisuals.Accent(theme);
        var host = new Grid { Width = width, Height = height, HorizontalAlignment = HorizontalAlignment.Center };

        _area = WidgetVisuals.Area(width, height, accent);
        _areaBrush = (SolidColorBrush)_area.Fill;
        host.Children.Add(_area);

        _spark = WidgetVisuals.Spark(width, height, accent, 2.0);
        _sparkBrush = (SolidColorBrush)_spark.Stroke;
        host.Children.Add(_spark);

        return host;
    }

    private FrameworkElement? BuildCoreGrid(WidgetTheme theme, double width, double height, int coreCount)
    {
        if (coreCount <= 0)
        {
            return null;
        }

        width = Math.Max(20, width);
        height = Math.Max(14, height);
        var track = WidgetVisuals.Track(theme);
        var accent = WidgetVisuals.Accent(theme);

        if (coreCount <= CoreBarThreshold)
        {
            var spacing = Math.Max(2, width * 0.012);
            var panel = new Grid
            {
                Width = width,
                Height = height,
                ColumnSpacing = spacing,
                HorizontalAlignment = HorizontalAlignment.Center,
            };

            for (var i = 0; i < coreCount; i++)
            {
                panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            }

            var barWidth = Math.Max(3, (width - (coreCount - 1) * spacing) / coreCount);
            for (var i = 0; i < coreCount; i++)
            {
                var bar = WidgetVisuals.VBar(barWidth, height, track, accent);
                Grid.SetColumn(bar, i);
                panel.Children.Add(bar);
                _coreBars.Add(bar);
            }

            return panel;
        }

        // Many-core CPUs get small tiles instead of bars; pick a column count that keeps them roughly square.
        var count = Math.Min(coreCount, MaxCoreTiles);
        var columns = Math.Clamp((int)Math.Round(Math.Sqrt(count * width / height)), 1, count);
        var rows = (int)Math.Ceiling(count / (double)columns);

        var grid = new Grid
        {
            Width = width,
            Height = height,
            ColumnSpacing = 2,
            RowSpacing = 2,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        for (var c = 0; c < columns; c++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        for (var r = 0; r < rows; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        }

        for (var i = 0; i < count; i++)
        {
            var brush = new SolidColorBrush(track);
            var tile = new Border { Background = brush, CornerRadius = new CornerRadius(2) };
            Grid.SetColumn(tile, i % columns);
            Grid.SetRow(tile, i / columns);
            grid.Children.Add(tile);
            _coreTileBrushes.Add(brush);
        }

        return grid;
    }

    // ---- Per-tick updates --------------------------------------------------------------------

    private void Update(SystemStats stats)
    {
        var theme = Context?.Theme;
        if (theme is null)
        {
            return;
        }

        try
        {
            var fraction = Math.Clamp(stats.CpuPercent, 0, 100) / 100.0;
            var color = WidgetVisuals.LoadColor(theme, fraction, _colorByLoad);

            if (_percentBrush is not null)
            {
                _percentBrush.Color = color;
            }

            if (_percentText is not null)
            {
                _percentText.Text = stats.CpuPercent.ToString("0", CultureInfo.InvariantCulture) + "%";
            }

            if (_areaBrush is not null)
            {
                _areaBrush.Color = ColorUtil.Fade(color, 0.28);
            }

            if (_sparkBrush is not null)
            {
                _sparkBrush.Color = color;
            }

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

            if (_clockText is not null)
            {
                _clockText.Text = _clockShowMax && stats.CpuMaxMhz > 0
                    ? $"{WidgetVisuals.FormatClock(stats.CpuMhz)} / 定格 {WidgetVisuals.FormatClock(stats.CpuMaxMhz)}"
                    : WidgetVisuals.FormatClock(stats.CpuMhz);
            }

            if (_infoText is not null)
            {
                _infoText.Text = BuildInfoLine(stats);
            }

            UpdateCoreGrid(theme, stats.CoreUsage);
        }
        catch (Exception ex)
        {
            Crash.Log(ex, "CpuMonitorWidget.Update");
        }
    }

    private string BuildInfoLine(SystemStats stats)
    {
        var parts = new List<string>();

        if (_infoShowClock)
        {
            parts.Add(WidgetVisuals.FormatClock(stats.CpuMhz));
        }

        if (_infoShowProcess)
        {
            parts.Add(
                "プロセス " + stats.ProcessCount.ToString("N0", CultureInfo.InvariantCulture) +
                " / スレッド " + stats.ThreadCount.ToString("N0", CultureInfo.InvariantCulture));
        }

        if (_infoShowName && HardwareInfo.CpuName.Length > 0)
        {
            parts.Add(HardwareInfo.CpuName);
        }

        return string.Join("   ・   ", parts);
    }

    private void UpdateCoreGrid(WidgetTheme theme, IReadOnlyList<double> usage)
    {
        if (_coreBars.Count > 0)
        {
            for (var i = 0; i < _coreBars.Count; i++)
            {
                var fraction = i < usage.Count ? Math.Clamp(usage[i], 0, 100) / 100.0 : 0;
                WidgetVisuals.SetVBar(_coreBars[i], fraction);
                WidgetVisuals.SetVBarColor(_coreBars[i], WidgetVisuals.LoadColor(theme, fraction, _colorByLoad));
            }

            return;
        }

        if (_coreTileBrushes.Count == 0)
        {
            return;
        }

        var track = WidgetVisuals.Track(theme);
        for (var i = 0; i < _coreTileBrushes.Count; i++)
        {
            var fraction = i < usage.Count ? Math.Clamp(usage[i], 0, 100) / 100.0 : 0;
            var loadColor = WidgetVisuals.LoadColor(theme, fraction, _colorByLoad);
            _coreTileBrushes[i].Color = WidgetVisuals.Mix(track, loadColor, fraction);
        }
    }

    private static SystemStats SampleStats() => new(
        42, 58, 19.5, 32.0, 47, 220, 476, 1840, 260, 78, false, true, TimeSpan.FromHours(9))
    {
        // One value per real core, so the editor preview lays out like the placed widget will.
        CoreUsage = [.. Enumerable.Range(0, HardwareInfo.LogicalCores).Select(i => 18.0 + i * 37 % 71)],
        CpuMhz = 3850,
        CpuMaxMhz = 3400,
        ProcessCount = 312,
        ThreadCount = 4208,
    };
}
