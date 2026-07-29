namespace Widgets.App.Models;

/// <summary>Static description of a widget kind, used to populate the gallery.</summary>
public sealed class WidgetCatalogEntry
{
    public required WidgetKind Kind { get; init; }
    public required string DisplayName { get; init; }
    public required string Description { get; init; }

    /// <summary>Segoe Fluent Icons glyph shown on the gallery card.</summary>
    public required string Glyph { get; init; }

    public required string Category { get; init; }

    public required IReadOnlyList<WidgetSize> SupportedSizes { get; init; }

    /// <summary>Applied to <see cref="WidgetDefinition.Settings"/> when a new widget of this kind is created.</summary>
    public IReadOnlyDictionary<string, string> DefaultSettings { get; init; }
        = new Dictionary<string, string>();
}

/// <summary>
/// Setting keys understood by the widget renderers. Kept as constants in one place so the
/// editor and the renderers can never drift apart.
/// </summary>
public static class WidgetSettingKeys
{
    // Clock / world clock
    public const string TimeFormat = "timeFormat";           // "HH:mm", "h:mm tt", "HH:mm:ss"
    public const string ShowSeconds = "showSeconds";
    public const string TimeZoneId = "timeZoneId";
    public const string CityLabel = "cityLabel";
    public const string ClockFaceStyle = "clockFaceStyle";   // "Ticks", "Numbers", "Minimal"
    public const string ShowSecondHand = "showSecondHand";

    // Date / calendar
    public const string DateFormat = "dateFormat";           // custom .NET format string
    public const string FirstDayOfWeek = "firstDayOfWeek";   // 0 = Sunday
    public const string HighlightToday = "highlightToday";
    public const string ShowWeekNumbers = "showWeekNumbers";

    // Weather / astronomy
    public const string ShowForecast = "showForecast";
    public const string ForecastDays = "forecastDays";
    public const string ShowFeelsLike = "showFeelsLike";
    public const string AstronomyMode = "astronomyMode";     // "SunTimes", "MoonPhase", "Both"

    // Countdown
    public const string TargetDate = "targetDate";
    public const string CountdownTitle = "countdownTitle";
    public const string CountdownUnit = "countdownUnit";     // "Days", "Auto", "Full"
    public const string CountUp = "countUp";

    // System monitor / battery
    public const string Metrics = "metrics";                 // comma-separated: cpu,ram,disk,net,gpu,diskio,proc,uptime
    public const string DriveLetter = "driveLetter";
    public const string ShowPercentageText = "showPercentageText";
    public const string GaugeStyle = "gaugeStyle";           // "Bar", "Ring", "Sparkline"
    public const string ColorByLoad = "colorByLoad";         // recolor the gauge under high load

    // CPU monitor
    public const string CpuDisplayStyle = "cpuDisplayStyle"; // "Combined", "Cores", "Graph"
    public const string ShowCoreGrid = "showCoreGrid";
    public const string ShowCpuName = "showCpuName";
    public const string ShowCpuClock = "showCpuClock";
    public const string ShowProcessCount = "showProcessCount";

    // GPU monitor
    public const string ShowVram = "showVram";
    public const string ShowGpuName = "showGpuName";

    // Network monitor
    public const string NetworkUnit = "networkUnit";                 // "Bits", "Bytes"
    public const string NetworkScaleMode = "networkScaleMode";       // "Auto", "Fixed"
    public const string NetworkFullScaleMbps = "networkFullScaleMbps";
    public const string ShowNetworkTotals = "showNetworkTotals";

    // Disk monitor
    public const string ShowDiskIo = "showDiskIo";
    public const string ShowAllDrives = "showAllDrives";

    // Photo
    public const string PhotoPath = "photoPath";
    public const string PhotoFolder = "photoFolder";
    public const string PhotoIntervalSeconds = "photoIntervalSeconds";
    public const string PhotoStretch = "photoStretch";       // "UniformToFill", "Uniform"

    // Note
    public const string NoteText = "noteText";
    public const string NoteAlignment = "noteAlignment";     // "Left", "Center", "Right"
}

/// <summary>The gallery: every widget kind the app can create.</summary>
public static class WidgetCatalog
{
    private static readonly WidgetSize[] AllSizes =
    [
        WidgetSize.Small, WidgetSize.Medium, WidgetSize.Large, WidgetSize.Wide, WidgetSize.Tall
    ];

    private static readonly WidgetSize[] SquareSizes =
    [
        WidgetSize.Small, WidgetSize.Large
    ];

    public static IReadOnlyList<WidgetCatalogEntry> Entries { get; } =
    [
        new()
        {
            Kind = WidgetKind.DigitalClock,
            DisplayName = "デジタル時計",
            Description = "現在時刻を大きな文字で表示します。",
            Glyph = "",
            Category = "時間",
            SupportedSizes = AllSizes,
            DefaultSettings = new Dictionary<string, string>
            {
                [WidgetSettingKeys.TimeFormat] = "HH:mm",
                [WidgetSettingKeys.ShowSeconds] = "false",
                [WidgetSettingKeys.DateFormat] = "M月d日 dddd",
            },
        },
        new()
        {
            Kind = WidgetKind.AnalogClock,
            DisplayName = "アナログ時計",
            Description = "針で時刻を表示する文字盤。",
            Glyph = "",
            Category = "時間",
            SupportedSizes = SquareSizes,
            DefaultSettings = new Dictionary<string, string>
            {
                [WidgetSettingKeys.ClockFaceStyle] = "Ticks",
                [WidgetSettingKeys.ShowSecondHand] = "true",
            },
        },
        new()
        {
            Kind = WidgetKind.WorldClock,
            DisplayName = "世界時計",
            Description = "指定したタイムゾーンの現地時刻。",
            Glyph = "",
            Category = "時間",
            SupportedSizes = AllSizes,
            DefaultSettings = new Dictionary<string, string>
            {
                [WidgetSettingKeys.TimeZoneId] = "Tokyo Standard Time",
                [WidgetSettingKeys.CityLabel] = "東京",
                [WidgetSettingKeys.TimeFormat] = "HH:mm",
            },
        },
        new()
        {
            Kind = WidgetKind.Date,
            DisplayName = "日付",
            Description = "今日の日付と曜日。",
            Glyph = "",
            Category = "時間",
            SupportedSizes = AllSizes,
            DefaultSettings = new Dictionary<string, string>
            {
                [WidgetSettingKeys.DateFormat] = "d",
            },
        },
        new()
        {
            Kind = WidgetKind.Calendar,
            DisplayName = "カレンダー",
            Description = "今月のカレンダーを表示します。",
            Glyph = "",
            Category = "時間",
            SupportedSizes = [WidgetSize.Medium, WidgetSize.Large, WidgetSize.Wide],
            DefaultSettings = new Dictionary<string, string>
            {
                [WidgetSettingKeys.FirstDayOfWeek] = "0",
                [WidgetSettingKeys.HighlightToday] = "true",
                [WidgetSettingKeys.ShowWeekNumbers] = "false",
            },
        },
        new()
        {
            Kind = WidgetKind.Weather,
            DisplayName = "天気",
            Description = "現在の天気と数日先の予報。",
            Glyph = "",
            Category = "情報",
            SupportedSizes = AllSizes,
            DefaultSettings = new Dictionary<string, string>
            {
                [WidgetSettingKeys.ShowForecast] = "true",
                [WidgetSettingKeys.ForecastDays] = "4",
                [WidgetSettingKeys.ShowFeelsLike] = "true",
            },
        },
        new()
        {
            Kind = WidgetKind.Astronomy,
            DisplayName = "天体",
            Description = "日の出・日の入りと月齢。",
            Glyph = "",
            Category = "情報",
            SupportedSizes = AllSizes,
            DefaultSettings = new Dictionary<string, string>
            {
                [WidgetSettingKeys.AstronomyMode] = "Both",
            },
        },
        new()
        {
            Kind = WidgetKind.Countdown,
            DisplayName = "カウントダウン",
            Description = "指定した日までの残り時間。",
            Glyph = "",
            Category = "情報",
            SupportedSizes = AllSizes,
            DefaultSettings = new Dictionary<string, string>
            {
                [WidgetSettingKeys.CountdownTitle] = "イベント",
                [WidgetSettingKeys.CountdownUnit] = "Days",
                [WidgetSettingKeys.CountUp] = "false",
            },
        },
        new()
        {
            Kind = WidgetKind.SystemMonitor,
            DisplayName = "システムモニター",
            Description = "CPU・メモリ・ディスク・ネットワークの使用状況。",
            Glyph = "",
            Category = "システム",
            SupportedSizes = AllSizes,
            DefaultSettings = new Dictionary<string, string>
            {
                [WidgetSettingKeys.Metrics] = "cpu,ram",
                [WidgetSettingKeys.GaugeStyle] = "Ring",
                [WidgetSettingKeys.ShowPercentageText] = "true",
                [WidgetSettingKeys.DriveLetter] = "C",
                [WidgetSettingKeys.ColorByLoad] = "true",
            },
        },
        new()
        {
            Kind = WidgetKind.Battery,
            DisplayName = "バッテリー",
            Description = "バッテリー残量と充電状態。",
            Glyph = "",
            Category = "システム",
            SupportedSizes = AllSizes,
            DefaultSettings = new Dictionary<string, string>
            {
                [WidgetSettingKeys.GaugeStyle] = "Ring",
                [WidgetSettingKeys.ShowPercentageText] = "true",
            },
        },
        new()
        {
            Kind = WidgetKind.CpuMonitor,
            DisplayName = "CPU",
            Description = "CPU の使用率をコア別・履歴つきで表示します。",
            Glyph = "",
            Category = "システム",
            SupportedSizes = AllSizes,
            DefaultSettings = new Dictionary<string, string>
            {
                [WidgetSettingKeys.CpuDisplayStyle] = "Combined",
                [WidgetSettingKeys.ShowCoreGrid] = "true",
                [WidgetSettingKeys.ShowCpuName] = "true",
                [WidgetSettingKeys.ShowCpuClock] = "true",
                [WidgetSettingKeys.ShowProcessCount] = "false",
                [WidgetSettingKeys.ShowPercentageText] = "true",
                [WidgetSettingKeys.ColorByLoad] = "true",
            },
        },
        new()
        {
            Kind = WidgetKind.GpuMonitor,
            DisplayName = "GPU",
            Description = "GPU の使用率と VRAM の使用量。",
            Glyph = "",
            Category = "システム",
            SupportedSizes = AllSizes,
            DefaultSettings = new Dictionary<string, string>
            {
                [WidgetSettingKeys.GaugeStyle] = "Ring",
                [WidgetSettingKeys.ShowVram] = "true",
                [WidgetSettingKeys.ShowGpuName] = "true",
                [WidgetSettingKeys.ShowPercentageText] = "true",
                [WidgetSettingKeys.ColorByLoad] = "true",
            },
        },
        new()
        {
            Kind = WidgetKind.NetworkMonitor,
            DisplayName = "ネットワーク",
            Description = "通信速度の推移と累計転送量。",
            Glyph = "",
            Category = "システム",
            SupportedSizes = AllSizes,
            DefaultSettings = new Dictionary<string, string>
            {
                [WidgetSettingKeys.NetworkUnit] = "Bits",
                [WidgetSettingKeys.NetworkScaleMode] = "Auto",
                [WidgetSettingKeys.NetworkFullScaleMbps] = "100",
                [WidgetSettingKeys.ShowNetworkTotals] = "true",
                [WidgetSettingKeys.ShowPercentageText] = "true",
            },
        },
        new()
        {
            Kind = WidgetKind.DiskMonitor,
            DisplayName = "ディスク",
            Description = "ディスクの空き容量と読み書き速度。",
            Glyph = "",
            Category = "システム",
            SupportedSizes = AllSizes,
            DefaultSettings = new Dictionary<string, string>
            {
                [WidgetSettingKeys.DriveLetter] = "C",
                [WidgetSettingKeys.GaugeStyle] = "Ring",
                [WidgetSettingKeys.ShowDiskIo] = "true",
                [WidgetSettingKeys.ShowAllDrives] = "false",
                [WidgetSettingKeys.ShowPercentageText] = "true",
                [WidgetSettingKeys.ColorByLoad] = "true",
            },
        },
        new()
        {
            Kind = WidgetKind.Photo,
            DisplayName = "写真",
            Description = "お気に入りの写真、またはフォルダーのスライドショー。",
            Glyph = "",
            Category = "パーソナル",
            SupportedSizes = AllSizes,
            DefaultSettings = new Dictionary<string, string>
            {
                [WidgetSettingKeys.PhotoStretch] = "UniformToFill",
                [WidgetSettingKeys.PhotoIntervalSeconds] = "60",
            },
        },
        new()
        {
            Kind = WidgetKind.Note,
            DisplayName = "メモ",
            Description = "自由なテキストを表示します。",
            Glyph = "",
            Category = "パーソナル",
            SupportedSizes = AllSizes,
            DefaultSettings = new Dictionary<string, string>
            {
                [WidgetSettingKeys.NoteText] = "メモを入力",
                [WidgetSettingKeys.NoteAlignment] = "Center",
            },
        },
    ];

    public static WidgetCatalogEntry Get(WidgetKind kind)
        => Entries.First(e => e.Kind == kind);

    /// <summary>Creates a definition pre-populated with the catalog defaults for <paramref name="kind"/>.</summary>
    public static WidgetDefinition CreateDefinition(WidgetKind kind, WidgetSize size)
    {
        var entry = Get(kind);
        var def = new WidgetDefinition
        {
            Kind = kind,
            Name = entry.DisplayName,
            Size = entry.SupportedSizes.Contains(size) ? size : entry.SupportedSizes[0],
            Settings = new Dictionary<string, string>(entry.DefaultSettings),
        };

        if (kind == WidgetKind.Countdown)
        {
            def.Set(WidgetSettingKeys.TargetDate, DateTimeOffset.Now.AddDays(30));
        }

        return def;
    }
}

/// <summary>Logical (96 DPI) pixel footprints for each <see cref="WidgetSize"/>.</summary>
public static class WidgetMetrics
{
    public static (double Width, double Height) GetSize(WidgetSize size) => size switch
    {
        WidgetSize.Small => (176, 176),
        WidgetSize.Medium => (368, 176),
        WidgetSize.Large => (368, 368),
        WidgetSize.Wide => (560, 176),
        WidgetSize.Tall => (176, 368),
        _ => (176, 176),
    };

    public static string GetDisplayName(WidgetSize size) => size switch
    {
        WidgetSize.Small => "小",
        WidgetSize.Medium => "中",
        WidgetSize.Large => "大",
        WidgetSize.Wide => "横長",
        WidgetSize.Tall => "縦長",
        _ => size.ToString(),
    };
}
