using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Widgets.App.Common;
using Widgets.App.Controls;
using Widgets.App.Models;
using Widgets.App.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using Windows.UI;

namespace Widgets.App.Views;

public sealed partial class EditorPage : Page
{
    private static readonly string[] FontFamilies =
    [
        "Segoe UI Variable Display", "Segoe UI Variable Text", "Segoe UI", "Segoe UI Emoji",
        "Yu Gothic UI", "Yu Gothic", "Yu Mincho", "Meiryo", "MS Gothic", "MS Mincho",
        "BIZ UDPGothic", "BIZ UDGothic", "Cascadia Mono", "Consolas", "Georgia", "Arial",
    ];

    private static readonly (int Value, string Label)[] FontWeightOptions =
    [
        (100, "極細"), (200, "細"), (300, "やや細"), (400, "標準"),
        (500, "中"), (600, "やや太"), (700, "太"), (800, "極太"), (900, "最太"),
    ];

    private static readonly string[] Palette =
    [
        "#FFFFFFFF", "#FFE8E8E8", "#FF9A9A9A", "#FF3A3A3A", "#FF000000", "#00000000",
        "#FFFF6B6B", "#FFFF9F43", "#FFFFD93D", "#FF6BCB77", "#FF4CC2FF", "#FF4D7CFE",
        "#FF9B5DE5", "#FFFF7AC6", "#FFE5C1A0", "#FF2B2D42", "#FF1B2A4A", "#FF6E4A6B",
    ];

    private readonly List<WidgetSurface> _presetSurfaces = new();

    private WidgetDefinition _definition = null!;
    private WidgetDefinition _snapshot = null!;
    private WidgetSurface? _surface;
    private Grid? _previewOuter;
    private Grid? _previewInner;
    /// <summary>
    /// Starts true so the control event handlers stay inert until <see cref="OnNavigatedTo"/> has
    /// supplied a definition — configuring the slider ranges in the constructor raises ValueChanged
    /// before there is anything to write to.
    /// </summary>
    private bool _loading = true;

    public EditorPage()
    {
        InitializeComponent();
        ConfigureSliderRanges();
    }

    /// <summary>
    /// Slider ranges are set here rather than in XAML: assigning a fractional Minimum from compiled
    /// XAML throws a XamlParseException on RangeBase.Minimum, which took the whole editor page down.
    /// Order matters — widen Maximum before narrowing Minimum so the range is never inverted.
    /// </summary>
    private void ConfigureSliderRanges()
    {
        static void Configure(Slider slider, double min, double max, double step)
        {
            slider.Maximum = Math.Max(max, slider.Minimum);
            slider.Minimum = min;
            slider.Maximum = max;
            slider.StepFrequency = step;
        }

        Configure(ScaleSlider, 0.5, 3.0, 0.05);
        Configure(FontScaleSlider, 0.5, 2.0, 0.05);
        Configure(OpacitySlider, 0.1, 1.0, 0.05);
        Configure(BackgroundImageOpacitySlider, 0.1, 1.0, 0.05);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _definition = (WidgetDefinition)e.Parameter;
        _snapshot = _definition.Clone();

        LoadAll();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _surface?.Cleanup();
        _surface = null;

        ReleasePresetSurfaces();
        AppServices.Store.Flush();
    }

    // ---- loading ---------------------------------------------------------------

    private void LoadAll()
    {
        _loading = true;

        var entry = WidgetCatalog.Get(_definition.Kind);
        HeaderText.Text = string.IsNullOrWhiteSpace(_definition.Name) ? entry.DisplayName : _definition.Name;

        LoadBasics(entry);
        LoadAppearance();
        BuildContentSection();
        BuildTimedSlots();
        BuildPresetStrip();

        _loading = false;

        RefreshPreview();
    }

    private void LoadBasics(WidgetCatalogEntry entry)
    {
        NameBox.Text = _definition.Name;

        SizeSelector.Items.Clear();
        SizeCombo.Items.Clear();

        foreach (var size in entry.SupportedSizes)
        {
            SizeSelector.Items.Add(new RadioButton { Content = WidgetMetrics.GetDisplayName(size), Tag = size });

            var (width, height) = WidgetMetrics.GetSize(size);
            SizeCombo.Items.Add(new ComboBoxItem
            {
                Content = $"{WidgetMetrics.GetDisplayName(size)}（{width:0}×{height:0}）",
                Tag = size,
            });
        }

        var index = Math.Max(0, entry.SupportedSizes.ToList().IndexOf(_definition.Size));
        SizeSelector.SelectedIndex = index;
        SizeCombo.SelectedIndex = index;

        ScaleSlider.Value = _definition.Scale;
        UpdateScaleHeader();

        ZOrderCombo.Items.Clear();
        ZOrderCombo.Items.Add(new ComboBoxItem { Content = "デスクトップに固定", Tag = ZOrderMode.Desktop });
        ZOrderCombo.Items.Add(new ComboBoxItem { Content = "通常のウィンドウ", Tag = ZOrderMode.Normal });
        ZOrderCombo.Items.Add(new ComboBoxItem { Content = "常に最前面", Tag = ZOrderMode.TopMost });
        ZOrderCombo.SelectedIndex = (int)_definition.ZOrder;

        ClickThroughToggle.IsOn = _definition.ClickThrough;
        LockToggle.IsOn = _definition.Locked;
        VisibleToggle.IsOn = _definition.IsVisible;
    }

    private void LoadAppearance()
    {
        var theme = _definition.Theme;

        FontCombo.Items.Clear();
        foreach (var family in FontFamilies)
        {
            FontCombo.Items.Add(new ComboBoxItem
            {
                Content = family,
                Tag = family,
                FontFamily = new FontFamily(family),
            });
        }

        var fontIndex = Array.IndexOf(FontFamilies, theme.FontFamily);
        FontCombo.SelectedIndex = fontIndex >= 0 ? fontIndex : 0;

        FontScaleSlider.Value = theme.FontScale;

        FontWeightCombo.Items.Clear();
        foreach (var (value, label) in FontWeightOptions)
        {
            FontWeightCombo.Items.Add(new ComboBoxItem { Content = $"{label} ({value})", Tag = value });
        }

        var weightIndex = Array.FindIndex(FontWeightOptions, o => o.Value == theme.FontWeight);
        FontWeightCombo.SelectedIndex = weightIndex >= 0 ? weightIndex : 3;

        BuildColorRows();

        BorderThicknessSlider.Value = theme.BorderThickness;
        CornerRadiusSlider.Value = theme.CornerRadius;
        PaddingSlider.Value = theme.Padding;
        OpacitySlider.Value = theme.Opacity;

        BackdropCombo.Items.Clear();
        BackdropCombo.Items.Add(new ComboBoxItem { Content = "単色", Tag = BackdropMode.Solid });
        BackdropCombo.Items.Add(new ComboBoxItem { Content = "すりガラス", Tag = BackdropMode.Frosted });
        BackdropCombo.Items.Add(new ComboBoxItem { Content = "マイカ", Tag = BackdropMode.Mica });
        BackdropCombo.Items.Add(new ComboBoxItem { Content = "透明", Tag = BackdropMode.Clear });
        BackdropCombo.Items.Add(new ComboBoxItem { Content = "写真", Tag = BackdropMode.Photo });
        BackdropCombo.SelectedIndex = (int)theme.Backdrop;

        BackgroundImageOpacitySlider.Value = theme.BackgroundImageOpacity;
        UpdateBackgroundImageRow();

        ShadowToggle.IsOn = theme.DropShadow;
    }

    private void UpdateBackgroundImageRow()
    {
        var theme = _definition.Theme;

        BackgroundImageRow.Visibility = theme.Backdrop == BackdropMode.Photo
            ? Visibility.Visible
            : Visibility.Collapsed;

        BackgroundImageText.Text = string.IsNullOrEmpty(theme.BackgroundImagePath)
            ? "画像が選択されていません"
            : theme.BackgroundImagePath;
    }

    private void UpdateScaleHeader() => ScaleSlider.Header = $"拡大率 ×{ScaleSlider.Value:0.00}";

    // ---- live preview ----------------------------------------------------------

    private void Apply()
    {
        if (_loading)
        {
            return;
        }

        AppServices.Store.Update(_definition);
        RefreshPreview();
    }

    private void RefreshPreview()
    {
        if (_definition is null)
        {
            return;
        }

        if (_surface is null)
        {
            _surface = new WidgetSurface();
            _previewInner = new Grid { RenderTransformOrigin = new Windows.Foundation.Point(0, 0) };
            _previewInner.Children.Add(_surface);

            _previewOuter = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };

            _previewOuter.Children.Add(_previewInner);
            PreviewHost.Children.Add(_previewOuter);
        }

        var (width, height) = WidgetMetrics.GetSize(_definition.Size);

        var availableWidth = Math.Max(160, PreviewBackdrop.ActualWidth - 64);
        var availableHeight = Math.Max(160, PreviewBackdrop.ActualHeight - 64);
        var scale = Math.Min(_definition.Scale, Math.Min(availableWidth / width, availableHeight / height));

        _surface.Width = width;
        _surface.Height = height;
        _previewInner!.Width = width;
        _previewInner.Height = height;
        _previewInner.RenderTransform = new ScaleTransform { ScaleX = scale, ScaleY = scale };
        _previewOuter!.Width = width * scale;
        _previewOuter.Height = height * scale;

        _surface.SetDefinition(_definition, true);
    }

    private void OnPreviewBackdropSizeChanged(object sender, SizeChangedEventArgs e) => RefreshPreview();

    private void ShowError(string message)
    {
        ErrorBar.Title = message;
        ErrorBar.IsOpen = true;
    }

    // ---- 基本 -------------------------------------------------------------------

    private void OnNameChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _definition.Name = NameBox.Text;
        HeaderText.Text = string.IsNullOrWhiteSpace(_definition.Name)
            ? WidgetCatalog.Get(_definition.Kind).DisplayName
            : _definition.Name;

        Apply();
    }

    private void OnSizeSelectorChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || SizeSelector.SelectedItem is not RadioButton { Tag: WidgetSize size })
        {
            return;
        }

        _loading = true;
        SizeCombo.SelectedIndex = SizeSelector.SelectedIndex;
        _loading = false;

        _definition.Size = size;
        Apply();
    }

    private void OnSizeComboChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || SizeCombo.SelectedItem is not ComboBoxItem { Tag: WidgetSize size })
        {
            return;
        }

        _loading = true;
        SizeSelector.SelectedIndex = SizeCombo.SelectedIndex;
        _loading = false;

        _definition.Size = size;
        Apply();
    }

    private void OnScaleChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        UpdateScaleHeader();

        if (_loading)
        {
            return;
        }

        _definition.Scale = e.NewValue;
        Apply();
    }

    private void OnZOrderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ZOrderCombo.SelectedItem is not ComboBoxItem { Tag: ZOrderMode mode })
        {
            return;
        }

        _definition.ZOrder = mode;
        Apply();
    }

    private void OnClickThroughToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _definition.ClickThrough = ClickThroughToggle.IsOn;
        Apply();
    }

    private void OnLockToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _definition.Locked = LockToggle.IsOn;
        Apply();
    }

    private void OnVisibleToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _definition.IsVisible = VisibleToggle.IsOn;
        Apply();
    }

    // ---- 外観 -------------------------------------------------------------------

    private void OnFontChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || FontCombo.SelectedItem is not ComboBoxItem { Tag: string family })
        {
            return;
        }

        _definition.Theme.FontFamily = family;
        Apply();
    }

    private void OnFontScaleChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _definition.Theme.FontScale = e.NewValue;
        Apply();
    }

    private void OnFontWeightChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || FontWeightCombo.SelectedItem is not ComboBoxItem { Tag: int weight })
        {
            return;
        }

        _definition.Theme.FontWeight = weight;
        Apply();
    }

    private void OnBorderThicknessChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _definition.Theme.BorderThickness = e.NewValue;
        Apply();
    }

    private void OnCornerRadiusChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _definition.Theme.CornerRadius = e.NewValue;
        Apply();
    }

    private void OnPaddingChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _definition.Theme.Padding = e.NewValue;
        Apply();
    }

    private void OnOpacityChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _definition.Theme.Opacity = e.NewValue;
        Apply();
    }

    private void OnBackdropChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || BackdropCombo.SelectedItem is not ComboBoxItem { Tag: BackdropMode mode })
        {
            return;
        }

        _definition.Theme.Backdrop = mode;
        UpdateBackgroundImageRow();
        Apply();
    }

    private void OnBackgroundImageOpacityChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _definition.Theme.BackgroundImageOpacity = e.NewValue;
        Apply();
    }

    private async void OnPickBackgroundImageClick(object sender, RoutedEventArgs e)
    {
        var path = await PickImageAsync();
        if (path is null)
        {
            return;
        }

        _definition.Theme.BackgroundImagePath = path;
        UpdateBackgroundImageRow();
        Apply();
    }

    private void OnClearBackgroundImageClick(object sender, RoutedEventArgs e)
    {
        _definition.Theme.BackgroundImagePath = null;
        UpdateBackgroundImageRow();
        Apply();
    }

    private void OnShadowToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _definition.Theme.DropShadow = ShadowToggle.IsOn;
        Apply();
    }

    // ---- 色 ---------------------------------------------------------------------

    private void BuildColorRows()
    {
        var theme = _definition.Theme;

        ColorRowsPanel.Children.Clear();
        ColorRowsPanel.Children.Add(ColorRow("文字", () => theme.TintColor, v => theme.TintColor = v));
        ColorRowsPanel.Children.Add(ColorRow("アクセント", () => theme.AccentColor, v => theme.AccentColor = v));
        ColorRowsPanel.Children.Add(ColorRow("補助テキスト", () => theme.SecondaryColor, v => theme.SecondaryColor = v));
        ColorRowsPanel.Children.Add(ColorRow("背景", () => theme.BackgroundColor, v => theme.BackgroundColor = v));
        ColorRowsPanel.Children.Add(ColorRow("枠線", () => theme.BorderColor, v => theme.BorderColor = v));
    }

    private FrameworkElement ColorRow(string label, Func<string> get, Action<string> set)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var text = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var button = BuildColorButton(get, set);
        Grid.SetColumn(button, 1);

        grid.Children.Add(text);
        grid.Children.Add(button);
        return grid;
    }

    private Button BuildColorButton(Func<string> get, Action<string> set)
    {
        var swatch = new Border
        {
            Width = 26,
            Height = 20,
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(80, 128, 128, 128)),
            Background = new SolidColorBrush(ColorUtil.Parse(get())),
        };

        var hexText = new TextBlock
        {
            Text = get(),
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = new FontFamily("Consolas"),
        };

        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        content.Children.Add(swatch);
        content.Children.Add(hexText);

        var picker = new ColorPicker
        {
            IsAlphaEnabled = true,
            IsHexInputVisible = true,
            IsColorChannelTextInputVisible = false,
            IsAlphaTextInputVisible = false,
            Width = 288,
            Color = ColorUtil.Parse(get()),
        };

        var savedPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var updating = false;

        void Commit(Color color)
        {
            var hex = ColorUtil.ToHex(color);
            set(hex);

            swatch.Background = new SolidColorBrush(color);
            hexText.Text = hex;

            Apply();
        }

        picker.ColorChanged += (_, args) =>
        {
            if (!updating)
            {
                Commit(args.NewColor);
            }
        };

        void Pick(Color color)
        {
            updating = true;
            picker.Color = color;
            updating = false;
            Commit(color);
        }

        var paletteRows = new StackPanel { Spacing = 6 };
        foreach (var chunk in Palette.Chunk(6))
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };

            foreach (var hex in chunk)
            {
                row.Children.Add(Swatch(hex, Pick));
            }

            paletteRows.Children.Add(row);
        }

        var saveButton = new Button { Content = "保存", HorizontalAlignment = HorizontalAlignment.Stretch };
        saveButton.Click += (_, _) =>
        {
            AppServices.Store.AddSavedColor(ColorUtil.ToHex(picker.Color));
            RefreshSavedColors(savedPanel, Pick);
        };

        var panel = new StackPanel { Spacing = 12, Width = 288 };
        panel.Children.Add(paletteRows);
        panel.Children.Add(new TextBlock
        {
            Text = "保存した色",
            Style = (Style)Application.Current.Resources["FieldLabelTextStyle"],
        });
        panel.Children.Add(savedPanel);
        panel.Children.Add(picker);
        panel.Children.Add(saveButton);

        var flyout = new Flyout { Content = new ScrollViewer { Content = panel, MaxHeight = 620 } };
        flyout.Opening += (_, _) =>
        {
            updating = true;
            picker.Color = ColorUtil.Parse(get());
            updating = false;
            RefreshSavedColors(savedPanel, Pick);
        };

        return new Button
        {
            Content = content,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Flyout = flyout,
        };
    }

    private static void RefreshSavedColors(StackPanel panel, Action<Color> pick)
    {
        panel.Children.Clear();

        var saved = AppServices.Store.Document.Settings.SavedColors;

        if (saved.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "まだありません",
                Style = (Style)Application.Current.Resources["CaptionTextStyle"],
            });

            return;
        }

        foreach (var hex in saved)
        {
            panel.Children.Add(Swatch(hex, pick));
        }
    }

    private static Button Swatch(string hex, Action<Color> pick)
    {
        var color = ColorUtil.Parse(hex);

        var button = new Button
        {
            Width = 40,
            Height = 32,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(color),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(80, 128, 128, 128)),
        };

        ToolTipService.SetToolTip(button, hex);
        button.Click += (_, _) => pick(color);
        return button;
    }

    // ---- コンテンツ ---------------------------------------------------------------

    private void BuildContentSection()
    {
        ContentPanel.Children.Clear();

        switch (_definition.Kind)
        {
            case WidgetKind.DigitalClock:
                ContentPanel.Children.Add(TimeFormatCombo());
                ContentPanel.Children.Add(SettingToggle("秒を表示", WidgetSettingKeys.ShowSeconds, false));
                ContentPanel.Children.Add(SettingTextBox("日付の書式", WidgetSettingKeys.DateFormat, "M月d日 dddd"));
                break;

            case WidgetKind.AnalogClock:
                ContentPanel.Children.Add(SettingCombo("文字盤", WidgetSettingKeys.ClockFaceStyle,
                    [("Ticks", "目盛り"), ("Numbers", "数字"), ("Minimal", "ミニマル")], "Ticks"));
                ContentPanel.Children.Add(SettingToggle("秒針を表示", WidgetSettingKeys.ShowSecondHand, true));
                break;

            case WidgetKind.WorldClock:
                ContentPanel.Children.Add(TimeZoneCombo());
                ContentPanel.Children.Add(SettingTextBox("都市名", WidgetSettingKeys.CityLabel, "東京"));
                ContentPanel.Children.Add(TimeFormatCombo());
                break;

            case WidgetKind.Date:
                ContentPanel.Children.Add(SettingTextBox("日付の書式", WidgetSettingKeys.DateFormat, "d"));
                ContentPanel.Children.Add(new TextBlock
                {
                    Text = "例: yyyy年M月d日、dddd、M/d",
                    Style = (Style)Application.Current.Resources["CaptionTextStyle"],
                });
                break;

            case WidgetKind.Calendar:
                ContentPanel.Children.Add(SettingCombo("週の開始曜日", WidgetSettingKeys.FirstDayOfWeek,
                    [("0", "日曜日"), ("1", "月曜日")], "0"));
                ContentPanel.Children.Add(SettingToggle("今日を強調", WidgetSettingKeys.HighlightToday, true));
                ContentPanel.Children.Add(SettingToggle("週番号を表示", WidgetSettingKeys.ShowWeekNumbers, false));
                break;

            case WidgetKind.Weather:
                ContentPanel.Children.Add(SettingToggle("予報を表示", WidgetSettingKeys.ShowForecast, true));
                ContentPanel.Children.Add(SettingSlider("予報の日数", WidgetSettingKeys.ForecastDays, 1, 7, 1, 4));
                ContentPanel.Children.Add(SettingToggle("体感温度を表示", WidgetSettingKeys.ShowFeelsLike, true));
                ContentPanel.Children.Add(BuildLocationPicker());
                break;

            case WidgetKind.Astronomy:
                ContentPanel.Children.Add(SettingCombo("表示内容", WidgetSettingKeys.AstronomyMode,
                    [("SunTimes", "日の出・日の入り"), ("MoonPhase", "月齢"), ("Both", "両方")], "Both"));
                ContentPanel.Children.Add(BuildLocationPicker());
                break;

            case WidgetKind.Countdown:
                ContentPanel.Children.Add(SettingTextBox("タイトル", WidgetSettingKeys.CountdownTitle, "イベント"));
                ContentPanel.Children.Add(BuildTargetDatePicker());
                ContentPanel.Children.Add(SettingCombo("表示単位", WidgetSettingKeys.CountdownUnit,
                    [("Days", "日数のみ"), ("Auto", "自動"), ("Full", "日・時・分")], "Days"));
                ContentPanel.Children.Add(SettingToggle("経過時間を数える", WidgetSettingKeys.CountUp, false));
                break;

            case WidgetKind.SystemMonitor:
                ContentPanel.Children.Add(BuildMetricChecks());
                ContentPanel.Children.Add(GaugeStyleCombo());
                ContentPanel.Children.Add(BuildDriveCombo());
                ContentPanel.Children.Add(SettingToggle("数値を表示", WidgetSettingKeys.ShowPercentageText, true));
                ContentPanel.Children.Add(SettingToggle("負荷に応じて色を変える", WidgetSettingKeys.ColorByLoad, true));
                break;

            case WidgetKind.Battery:
                ContentPanel.Children.Add(GaugeStyleCombo());
                ContentPanel.Children.Add(SettingToggle("数値を表示", WidgetSettingKeys.ShowPercentageText, true));
                break;

            case WidgetKind.CpuMonitor:
                ContentPanel.Children.Add(SettingCombo("表示スタイル", WidgetSettingKeys.CpuDisplayStyle,
                    [("Combined", "使用率＋グラフ"), ("Cores", "コア別"), ("Graph", "履歴グラフ")], "Combined"));
                ContentPanel.Children.Add(SettingToggle("コア別の負荷を表示", WidgetSettingKeys.ShowCoreGrid, true));
                ContentPanel.Children.Add(SettingToggle("CPU 名を表示", WidgetSettingKeys.ShowCpuName, true));
                ContentPanel.Children.Add(SettingToggle("クロックを表示", WidgetSettingKeys.ShowCpuClock, true));
                ContentPanel.Children.Add(SettingToggle("プロセス数を表示", WidgetSettingKeys.ShowProcessCount, false));
                ContentPanel.Children.Add(SettingToggle("数値を表示", WidgetSettingKeys.ShowPercentageText, true));
                ContentPanel.Children.Add(SettingToggle("負荷に応じて色を変える", WidgetSettingKeys.ColorByLoad, true));
                break;

            case WidgetKind.GpuMonitor:
                ContentPanel.Children.Add(GaugeStyleCombo());
                ContentPanel.Children.Add(SettingToggle("VRAM を表示", WidgetSettingKeys.ShowVram, true));
                ContentPanel.Children.Add(SettingToggle("GPU 名を表示", WidgetSettingKeys.ShowGpuName, true));
                ContentPanel.Children.Add(SettingToggle("数値を表示", WidgetSettingKeys.ShowPercentageText, true));
                ContentPanel.Children.Add(SettingToggle("負荷に応じて色を変える", WidgetSettingKeys.ColorByLoad, true));
                break;

            case WidgetKind.NetworkMonitor:
                ContentPanel.Children.Add(SettingCombo("単位", WidgetSettingKeys.NetworkUnit,
                    [("Bits", "ビット毎秒 (Mbps)"), ("Bytes", "バイト毎秒 (MB/s)")], "Bits"));
                ContentPanel.Children.Add(SettingCombo("縦軸のスケール", WidgetSettingKeys.NetworkScaleMode,
                    [("Auto", "自動"), ("Fixed", "固定")], "Auto"));
                ContentPanel.Children.Add(SettingSlider("スケールの上限（Mbps）", WidgetSettingKeys.NetworkFullScaleMbps, 1, 1000, 1, 100));
                ContentPanel.Children.Add(SettingToggle("累計転送量を表示", WidgetSettingKeys.ShowNetworkTotals, true));
                ContentPanel.Children.Add(SettingToggle("数値を表示", WidgetSettingKeys.ShowPercentageText, true));
                break;

            case WidgetKind.DiskMonitor:
                ContentPanel.Children.Add(SettingToggle("すべてのドライブを表示", WidgetSettingKeys.ShowAllDrives, false));
                ContentPanel.Children.Add(BuildDriveCombo());
                ContentPanel.Children.Add(GaugeStyleCombo());
                ContentPanel.Children.Add(SettingToggle("読み書き速度を表示", WidgetSettingKeys.ShowDiskIo, true));
                ContentPanel.Children.Add(SettingToggle("数値を表示", WidgetSettingKeys.ShowPercentageText, true));
                ContentPanel.Children.Add(SettingToggle("負荷に応じて色を変える", WidgetSettingKeys.ColorByLoad, true));
                break;

            case WidgetKind.Photo:
                ContentPanel.Children.Add(BuildPhotoPickers());
                ContentPanel.Children.Add(SettingSlider("切り替え間隔（秒）", WidgetSettingKeys.PhotoIntervalSeconds,
                    5, 600, 5, 60));
                ContentPanel.Children.Add(SettingCombo("表示方法", WidgetSettingKeys.PhotoStretch,
                    [("UniformToFill", "全体を埋める"), ("Uniform", "全体を表示")], "UniformToFill"));
                break;

            case WidgetKind.Note:
                ContentPanel.Children.Add(SettingTextBox("メモ", WidgetSettingKeys.NoteText, "メモを入力", multiline: true));
                ContentPanel.Children.Add(SettingCombo("配置", WidgetSettingKeys.NoteAlignment,
                    [("Left", "左寄せ"), ("Center", "中央"), ("Right", "右寄せ")], "Center"));
                break;
        }
    }

    private ComboBox TimeFormatCombo() => SettingCombo("時刻の書式", WidgetSettingKeys.TimeFormat,
        [("HH:mm", "24時間 (13:05)"), ("h:mm tt", "12時間 (1:05 PM)"), ("HH:mm:ss", "24時間・秒あり (13:05:09)")],
        "HH:mm");

    private ComboBox GaugeStyleCombo() => SettingCombo("ゲージの形", WidgetSettingKeys.GaugeStyle,
        [("Bar", "バー"), ("Ring", "リング"), ("Sparkline", "折れ線")], "Ring");

    private ComboBox TimeZoneCombo()
    {
        var combo = new ComboBox { Header = "タイムゾーン", HorizontalAlignment = HorizontalAlignment.Stretch };
        var zones = TimeZoneInfo.GetSystemTimeZones();
        var current = _definition.GetString(WidgetSettingKeys.TimeZoneId, TimeZoneInfo.Local.Id);

        for (var i = 0; i < zones.Count; i++)
        {
            combo.Items.Add(new ComboBoxItem { Content = zones[i].DisplayName, Tag = zones[i].Id });

            if (zones[i].Id == current)
            {
                combo.SelectedIndex = i;
            }
        }

        if (combo.SelectedIndex < 0 && zones.Count > 0)
        {
            combo.SelectedIndex = 0;
        }

        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem { Tag: string id })
            {
                _definition.Set(WidgetSettingKeys.TimeZoneId, id);
                Apply();
            }
        };

        return combo;
    }

    private FrameworkElement BuildTargetDatePicker()
    {
        var target = _definition.GetDate(WidgetSettingKeys.TargetDate) ?? DateTimeOffset.Now.AddDays(30);

        var datePicker = new CalendarDatePicker
        {
            Header = "対象の日付",
            Date = target,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        var timePicker = new TimePicker
        {
            Header = "時刻",
            SelectedTime = target.TimeOfDay,
            ClockIdentifier = "24HourClock",
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        void Commit()
        {
            var date = datePicker.Date ?? DateTimeOffset.Now;
            var time = timePicker.SelectedTime ?? TimeSpan.Zero;

            _definition.Set(WidgetSettingKeys.TargetDate,
                new DateTimeOffset(date.Year, date.Month, date.Day, time.Hours, time.Minutes, 0, date.Offset));

            Apply();
        }

        datePicker.DateChanged += (_, _) => Commit();
        timePicker.SelectedTimeChanged += (_, _) => Commit();

        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(datePicker);
        panel.Children.Add(timePicker);
        return panel;
    }

    private FrameworkElement BuildMetricChecks()
    {
        (string Key, string Label)[] metrics =
        [
            ("cpu", "CPU"), ("ram", "メモリ"), ("disk", "ディスク"), ("net", "ネットワーク"),
            ("gpu", "GPU"), ("diskio", "ディスク I/O"), ("proc", "プロセス数"), ("uptime", "稼働時間"),
        ];

        // Order matters here: this list *is* the Metrics setting, in display order.
        var selectedKeys = _definition.GetString(WidgetSettingKeys.Metrics, "cpu,ram")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(k => metrics.Any(m => m.Key == k))
            .Distinct()
            .ToList();

        var selectedList = new ListView
        {
            SelectionMode = ListViewSelectionMode.None,
            CanDragItems = true,
            AllowDrop = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };

        var availableList = new ListView
        {
            SelectionMode = ListViewSelectionMode.None,
            CanDragItems = true,
            AllowDrop = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };

        var emptyHint = new TextBlock
        {
            Text = "1 つ以上選んでください（未選択の場合は CPU が表示されます）",
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.Resources["CaptionTextStyle"],
            Visibility = Visibility.Collapsed,
        };

        // A drop can carry anything the desktop lets the user drag — text selected in another app,
        // a file, a link. Letting an unknown key through would poison the Metrics setting and then
        // throw out of RebuildLists, which clears the list before it rebuilds it.
        bool IsKnownMetric(string? key) => key is not null && metrics.Any(m => m.Key == key);

        // Refuse foreign payloads while they are still over the list, so the pointer never shows a
        // "move here" cursor for something the drop handler is only going to throw away.
        void AcceptMetricDrag(DragEventArgs args)
            => args.AcceptedOperation = args.DataView.Contains(StandardDataFormats.Text)
                ? DataPackageOperation.Move
                : DataPackageOperation.None;

        void Commit()
        {
            _definition.Set(WidgetSettingKeys.Metrics, string.Join(',', selectedKeys));
            Apply();
        }

        // Every move — reorder within the selected list, or arriving from the available list —
        // goes through here: remove the key wherever it currently sits, then insert it at the
        // (index-adjusted) target. This keeps the two lists from ever disagreeing about who owns a key.
        void MoveSelected(string key, int index)
        {
            var existingIndex = selectedKeys.IndexOf(key);
            if (existingIndex >= 0)
            {
                selectedKeys.RemoveAt(existingIndex);
                if (existingIndex < index)
                {
                    index--;
                }
            }

            index = Math.Clamp(index, 0, selectedKeys.Count);
            selectedKeys.Insert(index, key);

            RebuildLists();
            Commit();
        }

        void RemoveFromSelected(string key)
        {
            if (selectedKeys.Remove(key))
            {
                RebuildLists();
                Commit();
            }
        }

        async void HandleRowDrop(DragEventArgs e, ListViewItem row)
        {
            // Stop this from bubbling up to the ListView-level Drop handler below — otherwise a
            // drop on a specific row would be processed twice (once here, once as "append at end").
            e.Handled = true;
            var deferral = e.GetDeferral();

            try
            {
                var key = await e.DataView.GetTextAsync();
                if (!IsKnownMetric(key))
                {
                    return;
                }

                var targetKey = (string)row.Tag!;
                var targetIndex = selectedKeys.IndexOf(targetKey);
                if (targetIndex < 0)
                {
                    return;
                }

                var before = e.GetPosition(row).Y < row.ActualHeight / 2;
                MoveSelected(key, before ? targetIndex : targetIndex + 1);
            }
            catch (Exception ex)
            {
                Crash.Log(ex, "EditorPage.MetricRowDrop");
            }
            finally
            {
                deferral.Complete();
            }
        }

        void RebuildLists()
        {
            selectedList.Items.Clear();

            foreach (var key in selectedKeys)
            {
                var metric = metrics.First(m => m.Key == key);

                var handle = new TextBlock
                {
                    Text = "⠿",
                    Opacity = 0.6,
                    VerticalAlignment = VerticalAlignment.Center,
                };

                var label = new TextBlock { Text = metric.Label, VerticalAlignment = VerticalAlignment.Center };

                var removeButton = new Button { Content = "✕", Padding = new Thickness(8, 4, 8, 4) };
                ToolTipService.SetToolTip(removeButton, "削除");
                removeButton.Click += (_, _) => RemoveFromSelected(key);

                var grid = new Grid { ColumnSpacing = 8 };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                Grid.SetColumn(label, 1);
                Grid.SetColumn(removeButton, 2);
                grid.Children.Add(handle);
                grid.Children.Add(label);
                grid.Children.Add(removeButton);

                var row = new ListViewItem
                {
                    Content = grid,
                    Tag = key,
                    AllowDrop = true,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                };

                row.DragOver += (_, args) => AcceptMetricDrag(args);
                row.Drop += (_, args) => HandleRowDrop(args, row);

                selectedList.Items.Add(row);
            }

            availableList.Items.Clear();

            foreach (var (key, label) in metrics.Where(m => !selectedKeys.Contains(m.Key)))
            {
                var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };

                var addButton = new Button { Content = "＋", Padding = new Thickness(8, 4, 8, 4) };
                ToolTipService.SetToolTip(addButton, "追加");
                addButton.Click += (_, _) =>
                {
                    selectedKeys.Add(key);
                    RebuildLists();
                    Commit();
                };

                var grid = new Grid { ColumnSpacing = 8 };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                Grid.SetColumn(addButton, 1);
                grid.Children.Add(text);
                grid.Children.Add(addButton);

                availableList.Items.Add(new ListViewItem
                {
                    Content = grid,
                    Tag = key,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                });
            }

            emptyHint.Visibility = selectedKeys.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        void OnDragItemsStarting(object sender, DragItemsStartingEventArgs e)
        {
            if (e.Items.Count > 0 && e.Items[0] is ListViewItem { Tag: string key })
            {
                e.Data.SetText(key);
                e.Data.RequestedOperation = DataPackageOperation.Move;
            }
        }

        selectedList.DragItemsStarting += OnDragItemsStarting;
        availableList.DragItemsStarting += OnDragItemsStarting;

        // List-level DragOver/Drop cover the space a per-row handler doesn't: dropping below the
        // last row, or into a list that has no rows at all (e.g. all 8 metrics already selected).
        selectedList.DragOver += (_, e) => AcceptMetricDrag(e);
        selectedList.Drop += async (_, e) =>
        {
            var deferral = e.GetDeferral();

            try
            {
                var key = await e.DataView.GetTextAsync();
                if (IsKnownMetric(key))
                {
                    MoveSelected(key, selectedKeys.Count);
                }
            }
            catch (Exception ex)
            {
                Crash.Log(ex, "EditorPage.MetricListDrop");
            }
            finally
            {
                deferral.Complete();
            }
        };

        availableList.DragOver += (_, e) => AcceptMetricDrag(e);
        availableList.Drop += async (_, e) =>
        {
            var deferral = e.GetDeferral();

            try
            {
                var key = await e.DataView.GetTextAsync();
                if (IsKnownMetric(key))
                {
                    RemoveFromSelected(key);
                }
            }
            catch (Exception ex)
            {
                Crash.Log(ex, "EditorPage.MetricListDrop");
            }
            finally
            {
                deferral.Complete();
            }
        };

        RebuildLists();

        var selectedColumn = new StackPanel { Spacing = 6 };
        selectedColumn.Children.Add(new TextBlock
        {
            Text = "表示する項目（ドラッグで並べ替え）",
            Style = (Style)Application.Current.Resources["FieldLabelTextStyle"],
        });
        selectedColumn.Children.Add(selectedList);
        selectedColumn.Children.Add(emptyHint);

        var availableColumn = new StackPanel { Spacing = 6 };
        availableColumn.Children.Add(new TextBlock
        {
            Text = "使わない項目",
            Style = (Style)Application.Current.Resources["FieldLabelTextStyle"],
        });
        availableColumn.Children.Add(availableList);

        var layout = new Grid { ColumnSpacing = 12 };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(availableColumn, 1);
        layout.Children.Add(selectedColumn);
        layout.Children.Add(availableColumn);

        return layout;
    }

    private ComboBox BuildDriveCombo()
    {
        var combo = new ComboBox { Header = "対象ドライブ", HorizontalAlignment = HorizontalAlignment.Stretch };
        var current = _definition.GetString(WidgetSettingKeys.DriveLetter, "C");

        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
        {
            var letter = drive.Name[..1];
            combo.Items.Add(new ComboBoxItem { Content = $"{letter}:", Tag = letter });

            if (string.Equals(letter, current, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedIndex = combo.Items.Count - 1;
            }
        }

        if (combo.SelectedIndex < 0 && combo.Items.Count > 0)
        {
            combo.SelectedIndex = 0;
        }

        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem { Tag: string letter })
            {
                _definition.Set(WidgetSettingKeys.DriveLetter, letter);
                Apply();
            }
        };

        return combo;
    }

    private FrameworkElement BuildPhotoPickers()
    {
        var pathText = new TextBlock
        {
            TextTrimming = TextTrimming.CharacterEllipsis,
            Style = (Style)Application.Current.Resources["CaptionTextStyle"],
        };

        void UpdateText()
        {
            var file = _definition.GetString(WidgetSettingKeys.PhotoPath);
            var folder = _definition.GetString(WidgetSettingKeys.PhotoFolder);

            pathText.Text = !string.IsNullOrEmpty(folder) ? $"フォルダー: {folder}"
                : !string.IsNullOrEmpty(file) ? $"画像: {file}"
                : "画像が選択されていません";
        }

        UpdateText();

        var fileButton = new Button { Content = "画像を選ぶ" };
        fileButton.Click += async (_, _) =>
        {
            var path = await PickImageAsync();
            if (path is null)
            {
                return;
            }

            _definition.Set(WidgetSettingKeys.PhotoPath, path);
            _definition.Set(WidgetSettingKeys.PhotoFolder, string.Empty);
            UpdateText();
            Apply();
        };

        var folderButton = new Button { Content = "フォルダーを選ぶ" };
        folderButton.Click += async (_, _) =>
        {
            var path = await PickFolderAsync();
            if (path is null)
            {
                return;
            }

            _definition.Set(WidgetSettingKeys.PhotoFolder, path);
            _definition.Set(WidgetSettingKeys.PhotoPath, string.Empty);
            UpdateText();
            Apply();
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(fileButton);
        buttons.Children.Add(folderButton);

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = "写真",
            Style = (Style)Application.Current.Resources["FieldLabelTextStyle"],
        });
        panel.Children.Add(buttons);
        panel.Children.Add(pathText);
        return panel;
    }

    private FrameworkElement BuildLocationPicker()
    {
        var settings = AppServices.Store.Document.Settings;

        var currentText = new TextBlock
        {
            Style = (Style)Application.Current.Resources["CaptionTextStyle"],
            Text = string.IsNullOrEmpty(settings.WeatherLocationName)
                ? "場所が未設定です"
                : $"現在の場所: {settings.WeatherLocationName}",
        };

        var queryBox = new TextBox { Header = "場所を検索", PlaceholderText = "都市名を入力" };
        var searchButton = new Button { Content = "検索" };
        var results = new ListView
        {
            MaxHeight = 168,
            Visibility = Visibility.Collapsed,
            SelectionMode = ListViewSelectionMode.Single,
        };

        results.SelectionChanged += (_, _) =>
        {
            if (results.SelectedItem is not ListViewItem { Tag: GeoResult geo })
            {
                return;
            }

            settings.WeatherLocationName = geo.Name;
            settings.WeatherLatitude = geo.Latitude;
            settings.WeatherLongitude = geo.Longitude;

            AppServices.Store.NotifySettingsChanged();
            currentText.Text = $"現在の場所: {geo.Name}";
            results.Visibility = Visibility.Collapsed;

            Apply();
        };

        async void Search()
        {
            if (string.IsNullOrWhiteSpace(queryBox.Text))
            {
                return;
            }

            searchButton.IsEnabled = false;

            try
            {
                var found = await AppServices.Weather.SearchLocationAsync(queryBox.Text.Trim());

                results.Items.Clear();

                foreach (var geo in found)
                {
                    results.Items.Add(new ListViewItem
                    {
                        Content = $"{geo.Name} — {geo.Admin} {geo.Country}".Trim(),
                        Tag = geo,
                    });
                }

                results.Visibility = found.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

                if (found.Count == 0)
                {
                    ShowError("該当する場所が見つかりませんでした。");
                }
            }
            catch (Exception ex)
            {
                Crash.Log(ex, "EditorPage.SearchLocation");
                ShowError("場所の検索に失敗しました。ネットワークを確認してください。");
            }
            finally
            {
                searchButton.IsEnabled = true;
            }
        }

        searchButton.Click += (_, _) => Search();
        queryBox.KeyDown += (_, args) =>
        {
            if (args.Key == Windows.System.VirtualKey.Enter)
            {
                Search();
            }
        };

        var searchRow = new Grid();
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        searchButton.Margin = new Thickness(8, 0, 0, 0);
        searchButton.VerticalAlignment = VerticalAlignment.Bottom;
        Grid.SetColumn(searchButton, 1);
        searchRow.Children.Add(queryBox);
        searchRow.Children.Add(searchButton);

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(currentText);
        panel.Children.Add(searchRow);
        panel.Children.Add(results);
        return panel;
    }

    // ---- setting control helpers ------------------------------------------------

    private ComboBox SettingCombo(string header, string key, (string Value, string Label)[] options, string fallback)
    {
        var combo = new ComboBox { Header = header, HorizontalAlignment = HorizontalAlignment.Stretch };

        foreach (var (value, label) in options)
        {
            combo.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        }

        var current = _definition.GetString(key, fallback);
        combo.SelectedIndex = Math.Max(0, Array.FindIndex(options, o => o.Value == current));

        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem { Tag: string value })
            {
                _definition.Set(key, value);
                Apply();
            }
        };

        return combo;
    }

    private ToggleSwitch SettingToggle(string header, string key, bool fallback)
    {
        var toggle = new ToggleSwitch
        {
            Header = header,
            OnContent = "オン",
            OffContent = "オフ",
            IsOn = _definition.GetBool(key, fallback),
        };

        toggle.Toggled += (_, _) =>
        {
            _definition.Set(key, toggle.IsOn);
            Apply();
        };

        return toggle;
    }

    private Slider SettingSlider(string header, string key, double minimum, double maximum, double step, double fallback)
    {
        var slider = new Slider
        {
            Header = header,
            Minimum = minimum,
            Maximum = maximum,
            StepFrequency = step,
            Value = _definition.GetDouble(key, fallback),
        };

        slider.ValueChanged += (_, args) =>
        {
            _definition.Set(key, args.NewValue);
            Apply();
        };

        return slider;
    }

    private TextBox SettingTextBox(string header, string key, string fallback, bool multiline = false)
    {
        var box = new TextBox
        {
            Header = header,
            Text = _definition.GetString(key, fallback),
            AcceptsReturn = multiline,
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            Height = multiline ? 110 : double.NaN,
        };

        box.TextChanged += (_, _) =>
        {
            _definition.Set(key, box.Text);
            Apply();
        };

        return box;
    }

    // ---- 時間帯ウィジェット ---------------------------------------------------------

    private void BuildTimedSlots()
    {
        TimedPanel.Children.Clear();

        foreach (var slot in _definition.TimedSlots)
        {
            TimedPanel.Children.Add(BuildTimedSlotRow(slot));
        }
    }

    private FrameworkElement BuildTimedSlotRow(TimedSlot slot)
    {
        var start = new TimePicker
        {
            Header = "開始",
            SelectedTime = slot.Start,
            ClockIdentifier = "24HourClock",
        };

        start.SelectedTimeChanged += (_, _) =>
        {
            slot.StartMinutes = (int)(start.SelectedTime ?? TimeSpan.Zero).TotalMinutes;
            Apply();
        };

        var end = new TimePicker
        {
            Header = "終了",
            SelectedTime = slot.End,
            ClockIdentifier = "24HourClock",
        };

        end.SelectedTimeChanged += (_, _) =>
        {
            slot.EndMinutes = (int)(end.SelectedTime ?? TimeSpan.Zero).TotalMinutes;
            Apply();
        };

        var times = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        times.Children.Add(start);
        times.Children.Add(end);

        var kindCombo = new ComboBox
        {
            Header = "この時間帯に表示するウィジェット",
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        foreach (var entry in WidgetCatalog.Entries)
        {
            kindCombo.Items.Add(new ComboBoxItem { Content = entry.DisplayName, Tag = entry.Kind });

            if (entry.Kind == slot.Kind)
            {
                kindCombo.SelectedIndex = kindCombo.Items.Count - 1;
            }
        }

        kindCombo.SelectionChanged += (_, _) =>
        {
            if (kindCombo.SelectedItem is ComboBoxItem { Tag: WidgetKind kind })
            {
                slot.Kind = kind;
                slot.Settings = new Dictionary<string, string>(WidgetCatalog.Get(kind).DefaultSettings);
                Apply();
            }
        };

        var removeButton = new Button { Content = "この時間帯を削除" };
        removeButton.Click += (_, _) =>
        {
            _definition.TimedSlots.Remove(slot);
            BuildTimedSlots();
            Apply();
        };

        var layout = new StackPanel { Spacing = 10 };
        layout.Children.Add(times);
        layout.Children.Add(kindCombo);
        layout.Children.Add(removeButton);

        return new Border
        {
            Style = (Style)Application.Current.Resources["CardBorderStyle"],
            Child = layout,
        };
    }

    private void OnAddTimedSlotClick(object sender, RoutedEventArgs e)
    {
        _definition.TimedSlots.Add(new TimedSlot
        {
            StartMinutes = 22 * 60,
            EndMinutes = 6 * 60,
            Kind = _definition.Kind,
            Settings = new Dictionary<string, string>(WidgetCatalog.Get(_definition.Kind).DefaultSettings),
        });

        BuildTimedSlots();
        Apply();
    }

    // ---- プリセット ----------------------------------------------------------------

    private void ReleasePresetSurfaces()
    {
        foreach (var surface in _presetSurfaces)
        {
            surface.Cleanup();
        }

        _presetSurfaces.Clear();
    }

    private void BuildPresetStrip()
    {
        ReleasePresetSurfaces();
        PresetStrip.Children.Clear();

        foreach (var preset in AppServices.Store.AllPresets)
        {
            // Keep the widget's own size. The preview is scaled down to fit the chip anyway, and
            // forcing Small laid the content out for a footprint the widget does not have — which
            // is what made the chips show bar gauges for a widget drawing rings.
            var sample = _definition.Clone();
            sample.Scale = 1.0;
            sample.Theme = preset.Theme.Clone();

            var preview = PreviewBuilder.Create(sample, 108, 108, out var surface);
            _presetSurfaces.Add(surface);

            var frame = new Border
            {
                Width = 116,
                Height = 116,
                CornerRadius = new CornerRadius(8),
                Background = (Brush)Application.Current.Resources["PreviewBackdropBrush"],
                Child = preview,
            };

            var layout = new StackPanel { Spacing = 6, Width = 116 };
            layout.Children.Add(frame);
            layout.Children.Add(new TextBlock
            {
                Text = preset.Name,
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Style = (Style)Application.Current.Resources["CaptionTextStyle"],
            });

            var button = new Button
            {
                Padding = new Thickness(6),
                CornerRadius = new CornerRadius(8),
                Content = layout,
            };

            ToolTipService.SetToolTip(button, $"{preset.Category} / {preset.Name}");

            var captured = preset;
            button.Click += (_, _) =>
            {
                _definition.Theme = captured.Theme.Clone();

                _loading = true;
                LoadAppearance();
                _loading = false;

                Apply();
            };

            PresetStrip.Children.Add(button);
        }
    }

    private async void OnSavePresetClick(object sender, RoutedEventArgs e)
    {
        var nameBox = new TextBox
        {
            Header = "プリセット名",
            Text = string.IsNullOrWhiteSpace(_definition.Name)
                ? WidgetCatalog.Get(_definition.Kind).DisplayName
                : _definition.Name,
        };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "現在のデザインを保存",
            Content = nameBox,
            PrimaryButtonText = "保存",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(nameBox.Text))
        {
            return;
        }

        AppServices.Store.SaveCustomPreset(nameBox.Text.Trim(), _definition.Theme.Clone());
        BuildPresetStrip();
    }

    // ---- footer -----------------------------------------------------------------

    private void OnRevertClick(object sender, RoutedEventArgs e)
    {
        CopyInto(_snapshot, _definition);
        AppServices.Store.Update(_definition);
        LoadAll();
    }

    private void OnDoneClick(object sender, RoutedEventArgs e)
    {
        AppServices.Store.Flush();
        MainWindow.Instance?.NavigateTo("my");
    }

    private void OnBackClick(object sender, RoutedEventArgs e) => MainWindow.Instance?.NavigateTo("my");

    private static void CopyInto(WidgetDefinition source, WidgetDefinition target)
    {
        target.Name = source.Name;
        target.Size = source.Size;
        target.Scale = source.Scale;
        target.Locked = source.Locked;
        target.IsVisible = source.IsVisible;
        target.ZOrder = source.ZOrder;
        target.ClickThrough = source.ClickThrough;
        target.Theme = source.Theme.Clone();
        target.Settings = new Dictionary<string, string>(source.Settings);
        target.TimedSlots = source.TimedSlots.Select(s => s.Clone()).ToList();
    }

    // ---- pickers ----------------------------------------------------------------

    private async Task<string?> PickImageAsync()
    {
        var hwnd = MainWindow.Instance?.Hwnd ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero)
        {
            return null;
        }

        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        foreach (var extension in new[] { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp" })
        {
            picker.FileTypeFilter.Add(extension);
        }

        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    private async Task<string?> PickFolderAsync()
    {
        var hwnd = MainWindow.Instance?.Hwnd ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero)
        {
            return null;
        }

        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        picker.FileTypeFilter.Add("*");

        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }
}
