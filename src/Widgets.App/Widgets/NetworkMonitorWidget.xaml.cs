using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;
using Widgets.App.Common;
using Widgets.App.Controls;
using Widgets.App.Models;
using Widgets.App.Services;
using Windows.UI;

namespace Widgets.App.Widgets;

/// <summary>Dual down/up throughput graph with adaptive vertical scale and running session totals.</summary>
public sealed partial class NetworkMonitorWidget : WidgetViewBase
{
    private const int HistoryCapacity = 60;

    /// <summary>Auto scale never shrinks below this, so idle traffic doesn't blow noise up to full height.</summary>
    private const double AutoScaleFloorKbps = 1000;

    /// <summary>How much of the gap to the new ceiling to close per sample when the scale is shrinking.</summary>
    private const double ScaleShrinkSmoothing = 0.12;

    private readonly Queue<double> _downHistory = new();
    private readonly Queue<double> _upHistory = new();

    private bool _subscribed;
    private string _scaleMode = "Auto";
    private double _fixedFullScaleKbps = 100_000;
    private double _autoFullScaleKbps = AutoScaleFloorKbps;
    private string _unit = "Bits";
    private bool _showTotals = true;

    private Polygon? _downArea;
    private Polygon? _upArea;
    private Polyline? _downSpark;
    private Polyline? _upSpark;
    private TextBlock? _downValueText;
    private TextBlock? _upValueText;
    private TextBlock? _scaleText;
    private TextBlock? _totalsText;
    private TextBlock? _adapterText;

    public NetworkMonitorWidget()
    {
        InitializeComponent();
    }

    protected override void OnApply(WidgetRenderContext context)
    {
        var theme = context.Theme;

        _unit = context.GetString(WidgetSettingKeys.NetworkUnit, "Bits");
        _scaleMode = context.GetString(WidgetSettingKeys.NetworkScaleMode, "Auto");
        _fixedFullScaleKbps = Math.Max(1, context.GetDouble(WidgetSettingKeys.NetworkFullScaleMbps, 100)) * 1000.0;
        _showTotals = context.GetBool(WidgetSettingKeys.ShowNetworkTotals, true);
        var showValue = context.GetBool(WidgetSettingKeys.ShowPercentageText, true);

        var stats = context.IsPreview ? SampleStats() : AppServices.SystemStats.Current;

        var downColor = WidgetVisuals.Accent(theme);
        var upColor = WidgetVisuals.Mix(downColor, WidgetVisuals.Tint(theme), 0.55);

        Root.Children.Clear();
        _downArea = null;
        _upArea = null;
        _downSpark = null;
        _upSpark = null;
        _downValueText = null;
        _upValueText = null;
        _scaleText = null;
        _totalsText = null;
        _adapterText = null;

        var compact = context.Size == WidgetSize.Small;
        var roomy = context.Size is WidgetSize.Large or WidgetSize.Tall;
        var narrow = context.Width < 260;

        var showScale = !compact;
        var hasAdapter = !string.IsNullOrWhiteSpace(stats.NetAdapterName);
        var showFooter = !compact && (_showTotals || hasAdapter);
        var footerLines = (_showTotals ? 1 : 0) + (hasAdapter ? 1 : 0);

        var valueSize = narrow ? (compact ? 15.0 : 17.0) : roomy ? 21.0 : 17.0;
        const double scaleSize = 10.0;
        const double footerSize = 10.0;
        var spacing = compact ? 4.0 : 6.0;

        var visibleRows = 1;
        if (showValue)
        {
            visibleRows++;
        }

        if (showScale)
        {
            visibleRows++;
        }

        if (showFooter)
        {
            visibleRows++;
        }

        var valueRowHeight = showValue ? valueSize * 1.35 * (narrow ? 2 : 1) + (narrow ? 3 : 0) : 0;
        var scaleRowHeight = showScale ? scaleSize * 1.5 : 0;
        var footerRowHeight = showFooter ? footerSize * 1.4 * Math.Max(1, footerLines) : 0;
        var reserved = valueRowHeight + scaleRowHeight + footerRowHeight + spacing * Math.Max(0, visibleRows - 1);

        var graphWidth = Math.Max(24, context.Width);
        var graphHeight = Math.Max(28, context.Height - reserved);

        var stack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = spacing,
        };

        if (showValue)
        {
            _downValueText = WidgetVisuals.Text(theme, valueSize, downColor, 200);
            _upValueText = WidgetVisuals.Text(theme, valueSize, upColor, 200);
            _downValueText.HorizontalAlignment = HorizontalAlignment.Center;
            _upValueText.HorizontalAlignment = HorizontalAlignment.Center;

            Panel valueRow;
            if (narrow)
            {
                _downValueText.MaxWidth = context.Width;
                _upValueText.MaxWidth = context.Width;
                valueRow = new StackPanel { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Center };
            }
            else
            {
                _downValueText.MaxWidth = context.Width / 2 - 8;
                _upValueText.MaxWidth = context.Width / 2 - 8;
                valueRow = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 16,
                    HorizontalAlignment = HorizontalAlignment.Center,
                };
            }

            valueRow.Children.Add(_downValueText);
            valueRow.Children.Add(_upValueText);
            stack.Children.Add(valueRow);
        }

        var graph = new Grid { Width = graphWidth, Height = graphHeight };
        _downArea = WidgetVisuals.Area(graphWidth, graphHeight, downColor);
        _upArea = WidgetVisuals.Area(graphWidth, graphHeight, upColor);
        _downSpark = WidgetVisuals.Spark(graphWidth, graphHeight, downColor);
        _upSpark = WidgetVisuals.Spark(graphWidth, graphHeight, upColor);
        graph.Children.Add(_downArea);
        graph.Children.Add(_upArea);
        graph.Children.Add(_downSpark);
        graph.Children.Add(_upSpark);
        stack.Children.Add(graph);

        if (showScale)
        {
            _scaleText = WidgetVisuals.Text(theme, scaleSize, WidgetVisuals.Secondary(theme));
            _scaleText.HorizontalAlignment = HorizontalAlignment.Center;
            _scaleText.MaxWidth = context.Width;
            stack.Children.Add(_scaleText);
        }

        if (showFooter)
        {
            var footer = new StackPanel { Spacing = 1, HorizontalAlignment = HorizontalAlignment.Center };

            if (_showTotals)
            {
                _totalsText = WidgetVisuals.Text(theme, footerSize, WidgetVisuals.Secondary(theme));
                _totalsText.HorizontalAlignment = HorizontalAlignment.Center;
                _totalsText.MaxWidth = context.Width;
                footer.Children.Add(_totalsText);
            }

            if (hasAdapter)
            {
                _adapterText = WidgetVisuals.Text(theme, footerSize, WidgetVisuals.Secondary(theme));
                _adapterText.HorizontalAlignment = HorizontalAlignment.Center;
                _adapterText.MaxWidth = context.Width;
                footer.Children.Add(_adapterText);
            }

            stack.Children.Add(footer);
        }

        Root.Children.Add(stack);

        if (!_subscribed)
        {
            AppServices.SystemStats.Updated += OnStatsUpdated;
            _subscribed = true;
        }

        Update(stats);
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
        _downHistory.Enqueue(stats.NetDownKbps);
        while (_downHistory.Count > HistoryCapacity)
        {
            _downHistory.Dequeue();
        }

        _upHistory.Enqueue(stats.NetUpKbps);
        while (_upHistory.Count > HistoryCapacity)
        {
            _upHistory.Dequeue();
        }

        var downRaw = _downHistory.ToArray();
        var upRaw = _upHistory.ToArray();

        var rawMax = 0.0;
        foreach (var v in downRaw)
        {
            rawMax = Math.Max(rawMax, v);
        }

        foreach (var v in upRaw)
        {
            rawMax = Math.Max(rawMax, v);
        }

        var fullScale = ResolveFullScaleKbps(rawMax);

        if (_downArea is not null)
        {
            WidgetVisuals.SetArea(_downArea, ToFractions(downRaw, fullScale), HistoryCapacity);
        }

        if (_upArea is not null)
        {
            WidgetVisuals.SetArea(_upArea, ToFractions(upRaw, fullScale), HistoryCapacity);
        }

        if (_downSpark is not null)
        {
            WidgetVisuals.SetSpark(_downSpark, ToFractions(downRaw, fullScale), HistoryCapacity);
        }

        if (_upSpark is not null)
        {
            WidgetVisuals.SetSpark(_upSpark, ToFractions(upRaw, fullScale), HistoryCapacity);
        }

        if (_downValueText is not null)
        {
            _downValueText.Text = "↓ " + FormatRate(stats.NetDownKbps);
        }

        if (_upValueText is not null)
        {
            _upValueText.Text = "↑ " + FormatRate(stats.NetUpKbps);
        }

        if (_scaleText is not null)
        {
            _scaleText.Text = "最大 " + WidgetVisuals.FormatBitrate(fullScale);
        }

        if (_totalsText is not null)
        {
            _totalsText.Text = $"累計 ↓ {WidgetVisuals.FormatGb(stats.NetTotalDownGb)} / ↑ {WidgetVisuals.FormatGb(stats.NetTotalUpGb)}";
        }

        if (_adapterText is not null)
        {
            _adapterText.Text = stats.NetAdapterName;
        }
    }

    /// <summary>
    /// Resolves the graph's vertical ceiling. Fixed mode is just the user's setting; auto mode tracks
    /// the window's peak, snapping up immediately so the trace never clips but easing down slowly so
    /// the scale doesn't visibly jitter once traffic settles.
    /// </summary>
    private double ResolveFullScaleKbps(double rawMax)
    {
        if (_scaleMode == "Fixed")
        {
            return _fixedFullScaleKbps;
        }

        var target = Math.Max(AutoScaleFloorKbps, NiceCeiling(rawMax));

        if (_autoFullScaleKbps <= 0 || target >= _autoFullScaleKbps)
        {
            _autoFullScaleKbps = target;
        }
        else
        {
            var eased = _autoFullScaleKbps + (target - _autoFullScaleKbps) * ScaleShrinkSmoothing;
            _autoFullScaleKbps = eased - target < Math.Max(1.0, target * 0.01) ? target : eased;
        }

        return _autoFullScaleKbps;
    }

    /// <summary>Rounds up to a "nice" 1/2/5 × 10^n ceiling so the scale label reads like "20 Mbps", not "18.4".</summary>
    private static double NiceCeiling(double raw)
    {
        if (raw <= 0)
        {
            return AutoScaleFloorKbps;
        }

        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var normalized = raw / magnitude;
        var step = normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10;
        return step * magnitude;
    }

    private static double[] ToFractions(IReadOnlyList<double> raw, double fullScale)
    {
        var scale = Math.Max(1, fullScale);
        var result = new double[raw.Count];
        for (var i = 0; i < raw.Count; i++)
        {
            result[i] = Math.Clamp(raw[i] / scale, 0, 1);
        }

        return result;
    }

    private string FormatRate(double kbps)
        => _unit == "Bytes" ? WidgetVisuals.FormatByteRate(kbps * 125.0) : WidgetVisuals.FormatBitrate(kbps);

    private static SystemStats SampleStats()
        => new(38, 61, 19.5, 32.0, 47, 220, 476, 12_400, 820, 0, false, false, TimeSpan.FromHours(9))
        {
            NetDownPeakKbps = 42_000,
            NetUpPeakKbps = 6_200,
            NetTotalDownGb = 4.2,
            NetTotalUpGb = 0.6,
            NetAdapterName = "Wi-Fi",
        };
}
