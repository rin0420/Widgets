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

/// <summary>Capacity gauge for one drive, or a compact list of every fixed drive, plus optional I/O throughput.</summary>
public sealed partial class DiskMonitorWidget : WidgetViewBase
{
    private static readonly TimeSpan CapacityRefreshInterval = TimeSpan.FromSeconds(5);

    private bool _subscribed;
    private bool _showAllDrives;
    private bool _showDiskIo;
    private bool _colorByLoad;
    private bool _compactRows;

    // Single-drive mode.
    private Path? _ring;
    private Grid? _bar;
    private TextBlock? _percentText;
    private TextBlock? _capacityText;
    private TextBlock? _ioText;
    private Grid? _activeBar;
    private SolidColorBrush? _ringBrush;
    private double _gaugeSize;
    private double _gaugeThickness;

    // All-drives mode. Enumerated once in OnApply; capacities are re-read on a 5s throttle instead
    // of every tick so a removable/slow volume can't stall the once-a-second stats update.
    private readonly List<DriveRow> _driveRows = [];
    private DateTimeOffset _lastCapacityRefresh = DateTimeOffset.MinValue;

    private Panel? _contentPanel;
    private TextBlock? _errorText;

    public DiskMonitorWidget()
    {
        InitializeComponent();
    }

    protected override void OnApply(WidgetRenderContext context)
    {
        var theme = context.Theme;
        var style = context.GetString(WidgetSettingKeys.GaugeStyle, "Ring");
        var showText = context.GetBool(WidgetSettingKeys.ShowPercentageText, true);

        _showAllDrives = context.GetBool(WidgetSettingKeys.ShowAllDrives, false);
        _showDiskIo = context.GetBool(WidgetSettingKeys.ShowDiskIo, true);
        _colorByLoad = context.GetBool(WidgetSettingKeys.ColorByLoad, true);

        var driveLetter = context.GetString(WidgetSettingKeys.DriveLetter, "C");
        if (!_showAllDrives && !string.IsNullOrWhiteSpace(driveLetter))
        {
            AppServices.SystemStats.DriveLetter = driveLetter;
        }

        Root.Children.Clear();
        _ring = null;
        _bar = null;
        _percentText = null;
        _capacityText = null;
        _ioText = null;
        _activeBar = null;
        _ringBrush = null;
        _contentPanel = null;
        _errorText = null;
        _driveRows.Clear();

        _contentPanel = _showAllDrives
            ? BuildAllDrives(context, theme)
            : BuildSingleDrive(context, theme, style, showText);

        Root.Children.Add(_contentPanel);

        _errorText = WidgetVisuals.Text(theme, context.Size == WidgetSize.Small ? 12 : 14, WidgetVisuals.Secondary(theme));
        _errorText.HorizontalAlignment = HorizontalAlignment.Center;
        _errorText.VerticalAlignment = VerticalAlignment.Center;
        _errorText.TextAlignment = TextAlignment.Center;
        _errorText.TextWrapping = TextWrapping.Wrap;
        _errorText.MaxWidth = context.Width;
        _errorText.Visibility = Visibility.Collapsed;
        Root.Children.Add(_errorText);

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
        var theme = Context?.Theme;
        if (theme is null)
        {
            return;
        }

        if (_showAllDrives)
        {
            UpdateAllDrives(theme);
        }
        else
        {
            UpdateSingleDrive(stats, theme);
        }
    }

    // ---- single-drive mode --------------------------------------------------------

    private StackPanel BuildSingleDrive(WidgetRenderContext context, WidgetTheme theme, string style, bool showText)
    {
        var compact = context.Size == WidgetSize.Small;
        var roomy = context.Size is WidgetSize.Large or WidgetSize.Tall;

        var showIo = _showDiskIo && !compact;
        var showActive = _showDiskIo && roomy;

        var capacitySize = compact ? 12.0 : 13.0;
        const double ioSize = 11.0;
        const double activeLabelSize = 10.0;

        var spacing = compact ? 4.0 : 6.0;
        var rows = 1 + (showIo ? 1 : 0) + (showActive ? 1 : 0);
        var reserved =
            capacitySize * 1.6 +
            (showIo ? ioSize * 1.6 : 0) +
            (showActive ? (activeLabelSize + 10) * 1.5 : 0) +
            spacing * rows;

        var gaugeArea = Math.Max(32, context.Height - reserved);
        var gaugeSize = Math.Max(28, Math.Min(context.Width, gaugeArea) * 0.8);

        var stack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = spacing,
        };

        stack.Children.Add(style == "Bar"
            ? BuildBarGauge(theme, context.Width, showText)
            : BuildRingGauge(theme, gaugeSize, showText));

        _capacityText = WidgetVisuals.Text(theme, capacitySize, WidgetVisuals.Secondary(theme));
        _capacityText.HorizontalAlignment = HorizontalAlignment.Center;
        _capacityText.MaxWidth = context.Width;
        stack.Children.Add(_capacityText);

        if (showIo)
        {
            _ioText = WidgetVisuals.Text(theme, ioSize, WidgetVisuals.Secondary(theme));
            _ioText.HorizontalAlignment = HorizontalAlignment.Center;
            _ioText.MaxWidth = context.Width;
            stack.Children.Add(_ioText);
        }

        if (showActive)
        {
            var activeRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                HorizontalAlignment = HorizontalAlignment.Center,
            };

            var label = WidgetVisuals.Text(theme, activeLabelSize, WidgetVisuals.Secondary(theme));
            label.Text = "アクティブ";
            label.VerticalAlignment = VerticalAlignment.Center;

            var barWidth = Math.Max(40, Math.Min(context.Width * 0.6, 120));
            _activeBar = WidgetVisuals.Bar(barWidth, 5, WidgetVisuals.Track(theme), WidgetVisuals.Accent(theme));
            _activeBar.VerticalAlignment = VerticalAlignment.Center;

            activeRow.Children.Add(label);
            activeRow.Children.Add(_activeBar);
            stack.Children.Add(activeRow);
        }

        return stack;
    }

    private FrameworkElement BuildRingGauge(WidgetTheme theme, double size, bool showText)
    {
        _gaugeSize = size;
        _gaugeThickness = Math.Max(3, size * 0.1);

        var dial = new Grid { Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Center };
        dial.Children.Add(WidgetVisuals.RingTrack(size, _gaugeThickness, WidgetVisuals.Track(theme)));

        _ringBrush = new SolidColorBrush(WidgetVisuals.Accent(theme));
        _ring = WidgetVisuals.Ring(size, _gaugeThickness, WidgetVisuals.Accent(theme));
        _ring.Stroke = _ringBrush;
        dial.Children.Add(_ring);

        if (showText)
        {
            _percentText = WidgetVisuals.Text(theme, size * 0.22, WidgetVisuals.Tint(theme), 100);
            _percentText.HorizontalAlignment = HorizontalAlignment.Center;
            _percentText.VerticalAlignment = VerticalAlignment.Center;
            dial.Children.Add(_percentText);
        }

        return dial;
    }

    private FrameworkElement BuildBarGauge(WidgetTheme theme, double width, bool showText)
    {
        var barWidth = Math.Max(60, Math.Min(width * 0.82, 260));
        var barHeight = Math.Max(10, Math.Min(18, barWidth * 0.14));

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        _bar = WidgetVisuals.Bar(barWidth, barHeight, WidgetVisuals.Track(theme), WidgetVisuals.Accent(theme));
        row.Children.Add(_bar);

        if (showText)
        {
            _percentText = WidgetVisuals.Text(theme, barHeight * 1.1, WidgetVisuals.Tint(theme), 100);
            _percentText.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(_percentText);
        }

        return row;
    }

    private void UpdateSingleDrive(SystemStats stats, WidgetTheme theme)
    {
        if (stats.DiskTotalGb <= 0)
        {
            ShowError("ドライブを取得できません");
            return;
        }

        HideError();

        var fraction = Math.Clamp(stats.DiskPercent / 100.0, 0, 1);
        var color = WidgetVisuals.LoadColor(theme, fraction, _colorByLoad);

        if (_ring is not null)
        {
            WidgetVisuals.SetRing(_ring, _gaugeSize, _gaugeThickness, fraction);
        }

        if (_ringBrush is not null)
        {
            _ringBrush.Color = color;
        }

        if (_bar is not null)
        {
            WidgetVisuals.SetBar(_bar, fraction);
            WidgetVisuals.SetBarColor(_bar, color);
        }

        if (_percentText is not null)
        {
            _percentText.Text = stats.DiskPercent.ToString("0", CultureInfo.InvariantCulture) + "%";
        }

        if (_capacityText is not null)
        {
            var free = Math.Max(0, stats.DiskTotalGb - stats.DiskUsedGb);
            _capacityText.Text = WidgetVisuals.FormatGb(stats.DiskUsedGb) + " / " + WidgetVisuals.FormatGb(stats.DiskTotalGb)
                + " ・ 空き " + WidgetVisuals.FormatGb(free);
        }

        if (_ioText is not null)
        {
            _ioText.Text = "読み " + WidgetVisuals.FormatByteRate(stats.DiskReadBytesPerSec)
                + " ・ 書き " + WidgetVisuals.FormatByteRate(stats.DiskWriteBytesPerSec);
        }

        if (_activeBar is not null)
        {
            WidgetVisuals.SetBar(_activeBar, Math.Clamp(stats.DiskActivePercent / 100.0, 0, 1));
        }
    }

    // ---- all-drives mode -----------------------------------------------------------

    private StackPanel BuildAllDrives(WidgetRenderContext context, WidgetTheme theme)
    {
        var compact = context.Size == WidgetSize.Small;
        _compactRows = compact;

        var rowHeight = compact ? 18.0 : 22.0;
        var spacing = compact ? 3.0 : 5.0;
        var maxRows = Math.Max(1, (int)((context.Height + spacing) / (rowHeight + spacing)));

        var letterSize = compact ? 11.0 : 12.0;
        var textSize = compact ? 10.0 : 11.0;
        var letterColumnWidth = compact ? 22.0 : 28.0;
        var textColumnWidth = compact ? 40.0 : 108.0;
        var barHeight = compact ? 7.0 : 9.0;
        var barWidth = Math.Max(20, context.Width - letterColumnWidth - textColumnWidth - 16);

        var list = new StackPanel { Spacing = spacing, VerticalAlignment = VerticalAlignment.Center };

        foreach (var drive in EnumerateFixedDrives().Take(maxRows))
        {
            var rowGrid = new Grid { ColumnSpacing = 8, Height = rowHeight };
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(letterColumnWidth) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(textColumnWidth) });

            var letterText = WidgetVisuals.Text(theme, letterSize, WidgetVisuals.Tint(theme), 200);
            letterText.Text = drive.Name[..1];
            letterText.VerticalAlignment = VerticalAlignment.Center;

            var bar = WidgetVisuals.Bar(barWidth, barHeight, WidgetVisuals.Track(theme), WidgetVisuals.Accent(theme));
            bar.VerticalAlignment = VerticalAlignment.Center;

            var text = WidgetVisuals.Text(theme, textSize, WidgetVisuals.Secondary(theme));
            text.VerticalAlignment = VerticalAlignment.Center;
            text.HorizontalAlignment = HorizontalAlignment.Right;

            Grid.SetColumn(bar, 1);
            Grid.SetColumn(text, 2);

            rowGrid.Children.Add(letterText);
            rowGrid.Children.Add(bar);
            rowGrid.Children.Add(text);

            list.Children.Add(rowGrid);

            TryReadCapacity(drive, out var used, out var total);
            _driveRows.Add(new DriveRow(drive, bar, text) { UsedGb = used, TotalGb = total });
        }

        _lastCapacityRefresh = DateTimeOffset.Now;
        return list;
    }

    private void UpdateAllDrives(WidgetTheme theme)
    {
        if (_driveRows.Count == 0)
        {
            ShowError("ドライブを取得できません");
            return;
        }

        HideError();

        var now = DateTimeOffset.Now;
        if (now - _lastCapacityRefresh >= CapacityRefreshInterval)
        {
            _lastCapacityRefresh = now;

            foreach (var row in _driveRows)
            {
                if (TryReadCapacity(row.Drive, out var used, out var total))
                {
                    row.UsedGb = used;
                    row.TotalGb = total;
                }
            }
        }

        foreach (var row in _driveRows)
        {
            var fraction = row.TotalGb > 0 ? Math.Clamp(row.UsedGb / row.TotalGb, 0, 1) : 0;
            WidgetVisuals.SetBar(row.Bar, fraction);
            WidgetVisuals.SetBarColor(row.Bar, WidgetVisuals.LoadColor(theme, fraction, _colorByLoad));

            var percentText = (fraction * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
            row.Text.Text = _compactRows ? percentText : percentText + " ・ " + WidgetVisuals.FormatGb(row.TotalGb);
        }
    }

    private static List<DriveInfo> EnumerateFixedDrives()
    {
        try
        {
            return DriveInfo.GetDrives()
                .Where(d => d.IsReady && d.DriveType == DriveType.Fixed)
                .ToList();
        }
        catch (Exception ex)
        {
            Crash.Log(ex, "DiskMonitorWidget.EnumerateFixedDrives");
            return [];
        }
    }

    private static bool TryReadCapacity(DriveInfo drive, out double usedGb, out double totalGb)
    {
        try
        {
            const double bytesPerGb = 1024.0 * 1024 * 1024;
            totalGb = drive.TotalSize / bytesPerGb;
            usedGb = totalGb - drive.AvailableFreeSpace / bytesPerGb;
            return true;
        }
        catch (Exception ex)
        {
            Crash.Log(ex, "DiskMonitorWidget.TryReadCapacity");
            usedGb = 0;
            totalGb = 0;
            return false;
        }
    }

    // ---- shared ----------------------------------------------------------------------

    private void ShowError(string message)
    {
        if (_contentPanel is not null)
        {
            _contentPanel.Visibility = Visibility.Collapsed;
        }

        if (_errorText is not null)
        {
            _errorText.Text = message;
            _errorText.Visibility = Visibility.Visible;
        }
    }

    private void HideError()
    {
        if (_contentPanel is not null)
        {
            _contentPanel.Visibility = Visibility.Visible;
        }

        if (_errorText is not null)
        {
            _errorText.Visibility = Visibility.Collapsed;
        }
    }

    private static SystemStats SampleStats()
        => new(38, 61, 19.5, 32.0, 47, 220, 476, 1840, 260, 0, false, false, TimeSpan.FromHours(9))
        {
            DiskReadBytesPerSec = 13_000_000,
            DiskWriteBytesPerSec = 3_200_000,
            DiskActivePercent = 22,
        };

    private sealed class DriveRow(DriveInfo drive, Grid bar, TextBlock text)
    {
        public DriveInfo Drive { get; } = drive;

        public Grid Bar { get; } = bar;

        public TextBlock Text { get; } = text;

        public double UsedGb { get; set; }

        public double TotalGb { get; set; }
    }
}
