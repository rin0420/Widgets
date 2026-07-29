using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;
using Widgets.App.Common;
using Widgets.App.Controls;
using Widgets.App.Models;
using Widgets.App.Services;
using Windows.UI;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace Widgets.App.Widgets;

/// <summary>CPU / memory / disk / network gauges driven by the shared stats service.</summary>
public sealed partial class SystemMonitorWidget : WidgetViewBase
{
    private const int HistoryCapacity = 60;

    /// <summary>Network throughput has no natural ceiling; this is the gauge's full-scale value.</summary>
    private const double NetworkFullScaleKbps = 10_000;

    private static readonly string[] KnownMetrics = ["cpu", "ram", "disk", "net"];

    private readonly List<Tile> _tiles = [];
    private readonly Dictionary<string, Queue<double>> _history = new();

    private bool _subscribed;

    public SystemMonitorWidget()
    {
        InitializeComponent();
    }

    protected override void OnApply(WidgetRenderContext context)
    {
        var theme = context.Theme;
        var style = context.GetString(WidgetSettingKeys.GaugeStyle, "Ring");
        var showText = context.GetBool(WidgetSettingKeys.ShowPercentageText, true);

        var drive = context.GetString(WidgetSettingKeys.DriveLetter, "C");
        if (!string.IsNullOrWhiteSpace(drive))
        {
            AppServices.SystemStats.DriveLetter = drive;
        }

        var metrics = context.GetString(WidgetSettingKeys.Metrics, "cpu,ram")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(m => m.ToLowerInvariant())
            .Where(m => KnownMetrics.Contains(m))
            .Distinct()
            .ToList();

        if (metrics.Count == 0)
        {
            metrics.Add("cpu");
        }

        var columns = context.Size switch
        {
            WidgetSize.Small => Math.Min(metrics.Count, 2),
            WidgetSize.Tall => 1,
            WidgetSize.Large => Math.Min(metrics.Count, 2),
            _ => metrics.Count,
        };

        var rows = (int)Math.Ceiling(metrics.Count / (double)columns);

        Tiles.Children.Clear();
        Tiles.RowDefinitions.Clear();
        Tiles.ColumnDefinitions.Clear();
        _tiles.Clear();

        for (var c = 0; c < columns; c++)
        {
            Tiles.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        for (var r = 0; r < rows; r++)
        {
            Tiles.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        }

        var tileWidth = Math.Max(24, (context.Width - (columns - 1) * 8) / columns);
        var tileHeight = Math.Max(24, (context.Height - (rows - 1) * 8) / rows);

        for (var i = 0; i < metrics.Count; i++)
        {
            var tile = style switch
            {
                "Bar" => BuildBar(theme, metrics[i], tileWidth, tileHeight, showText),
                "Sparkline" => BuildSpark(theme, metrics[i], tileWidth, tileHeight, showText),
                _ => BuildRing(theme, metrics[i], tileWidth, tileHeight, showText),
            };

            Grid.SetColumn(tile.Root, i % columns);
            Grid.SetRow(tile.Root, i / columns);
            Tiles.Children.Add(tile.Root);
            _tiles.Add(tile);
        }

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

    private void Update(SystemStats stats)
    {
        foreach (var tile in _tiles)
        {
            var fraction = Fraction(tile.Id, stats);
            Push(tile.Id, fraction);

            if (tile.Ring is not null)
            {
                WidgetVisuals.SetRing(tile.Ring, tile.GaugeSize, tile.GaugeThickness, fraction);
            }

            if (tile.Bar is not null)
            {
                WidgetVisuals.SetBar(tile.Bar, fraction);
            }

            if (tile.Spark is not null)
            {
                WidgetVisuals.SetSpark(tile.Spark, _history[tile.Id].ToArray(), HistoryCapacity);
            }

            if (tile.Value is not null)
            {
                tile.Value.Text = ValueText(tile.Id, stats);
            }
        }
    }

    private void Push(string id, double fraction)
    {
        if (!_history.TryGetValue(id, out var queue))
        {
            queue = new Queue<double>(HistoryCapacity);
            _history[id] = queue;
        }

        queue.Enqueue(fraction);
        while (queue.Count > HistoryCapacity)
        {
            queue.Dequeue();
        }
    }

    private Tile BuildRing(WidgetTheme theme, string id, double width, double height, bool showText)
    {
        var accent = WidgetVisuals.Accent(theme);
        var caption = LabelSize(height);
        var gauge = Math.Max(20, Math.Min(width, height - caption * 1.9) * 0.94);
        var thickness = Math.Max(3, gauge * 0.1);

        var stack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 2,
        };

        var dial = new Grid { Width = gauge, Height = gauge, HorizontalAlignment = HorizontalAlignment.Center };
        dial.Children.Add(WidgetVisuals.RingTrack(gauge, thickness, WidgetVisuals.Track(theme)));

        var ring = WidgetVisuals.Ring(gauge, thickness, accent);
        dial.Children.Add(ring);

        TextBlock? value = null;
        if (showText)
        {
            value = WidgetVisuals.Text(theme, gauge * 0.24, WidgetVisuals.Tint(theme), 100);
            value.HorizontalAlignment = HorizontalAlignment.Center;
            value.VerticalAlignment = VerticalAlignment.Center;
            dial.Children.Add(value);
        }

        stack.Children.Add(dial);
        stack.Children.Add(Caption(theme, id, caption));

        return new Tile(id, stack, value) { Ring = ring, GaugeSize = gauge, GaugeThickness = thickness };
    }

    private Tile BuildBar(WidgetTheme theme, string id, double width, double height, bool showText)
    {
        var caption = LabelSize(height);
        var barWidth = Math.Max(16, width);
        var barHeight = Math.Max(5, Math.Min(12, height * 0.14));

        var stack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 4,
        };

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        header.Children.Add(Caption(theme, id, caption, HorizontalAlignment.Left));

        TextBlock? value = null;
        if (showText)
        {
            value = WidgetVisuals.Text(theme, caption, WidgetVisuals.Tint(theme), 100);
            value.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(value, 1);
            header.Children.Add(value);
        }

        var bar = WidgetVisuals.Bar(barWidth, barHeight, WidgetVisuals.Track(theme), WidgetVisuals.Accent(theme));

        stack.Children.Add(header);
        stack.Children.Add(bar);

        return new Tile(id, stack, value) { Bar = bar };
    }

    private Tile BuildSpark(WidgetTheme theme, string id, double width, double height, bool showText)
    {
        var caption = LabelSize(height);

        var stack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 4,
        };

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(Caption(theme, id, caption, HorizontalAlignment.Left));

        TextBlock? value = null;
        if (showText)
        {
            value = WidgetVisuals.Text(theme, caption, WidgetVisuals.Tint(theme), 100);
            value.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(value, 1);
            header.Children.Add(value);
        }

        var spark = WidgetVisuals.Spark(Math.Max(16, width), Math.Max(14, height - caption * 2.4), WidgetVisuals.Accent(theme));

        stack.Children.Add(header);
        stack.Children.Add(spark);

        return new Tile(id, stack, value) { Spark = spark };
    }

    private static TextBlock Caption(
        WidgetTheme theme,
        string id,
        double size,
        HorizontalAlignment alignment = HorizontalAlignment.Center)
    {
        var text = WidgetVisuals.Text(theme, size, WidgetVisuals.Secondary(theme));
        text.Text = Label(id);
        text.HorizontalAlignment = alignment;
        return text;
    }

    private static double LabelSize(double tileHeight) => Math.Clamp(tileHeight * 0.13, 9, 16);

    private static string Label(string id) => id switch
    {
        "cpu" => "CPU",
        "ram" => "メモリ",
        "disk" => "ディスク",
        "net" => "ネット",
        _ => id.ToUpperInvariant(),
    };

    private static double Fraction(string id, SystemStats stats) => id switch
    {
        "cpu" => stats.CpuPercent / 100.0,
        "ram" => stats.RamPercent / 100.0,
        "disk" => stats.DiskPercent / 100.0,
        "net" => Math.Min(1.0, (stats.NetDownKbps + stats.NetUpKbps) / NetworkFullScaleKbps),
        _ => 0,
    };

    private static string ValueText(string id, SystemStats stats) => id switch
    {
        "cpu" => $"{stats.CpuPercent:0}%",
        "ram" => $"{stats.RamPercent:0}%",
        "disk" => $"{stats.DiskPercent:0}%",
        "net" => $"↓{Rate(stats.NetDownKbps)} ↑{Rate(stats.NetUpKbps)}",
        _ => string.Empty,
    };

    private static string Rate(double kbps)
        => kbps >= 1000
            ? (kbps / 1000).ToString("0.0", CultureInfo.InvariantCulture) + "M"
            : kbps.ToString("0", CultureInfo.InvariantCulture) + "K";

    private static SystemStats SampleStats()
        => new(38, 61, 19.5, 32.0, 47, 220, 476, 1840, 260, 78, false, true, TimeSpan.FromHours(9));

    private sealed class Tile(string id, FrameworkElement root, TextBlock? value)
    {
        public string Id { get; } = id;

        public FrameworkElement Root { get; } = root;

        public TextBlock? Value { get; } = value;

        public Path? Ring { get; init; }

        public Grid? Bar { get; init; }

        public Polyline? Spark { get; init; }

        public double GaugeSize { get; init; }

        public double GaugeThickness { get; init; }
    }
}
