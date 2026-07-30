using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Widgets.App.Common;
using Widgets.App.Models;
using Widgets.App.Services;
using Windows.UI;

namespace Widgets.App.Views;

public sealed partial class SettingsPage : Page
{
    private bool _loading;

    private static AppSettings Settings => AppServices.Store.Document.Settings;

    public SettingsPage()
    {
        InitializeComponent();

        Loaded += (_, _) => Load();
    }

    private void Load()
    {
        _loading = true;

        StartupToggle.IsOn = AppServices.Startup.IsEnabled;
        StartMinimizedToggle.IsOn = Settings.StartMinimized;

        AppThemeCombo.Items.Clear();
        AppThemeCombo.Items.Add(new ComboBoxItem { Content = "システムに合わせる", Tag = "Default" });
        AppThemeCombo.Items.Add(new ComboBoxItem { Content = "ライト", Tag = "Light" });
        AppThemeCombo.Items.Add(new ComboBoxItem { Content = "ダーク", Tag = "Dark" });
        AppThemeCombo.SelectedIndex = Settings.AppTheme switch
        {
            "Light" => 1,
            "Dark" => 2,
            _ => 0,
        };

        SnapCombo.Items.Clear();
        SnapCombo.Items.Add(new ComboBoxItem { Content = "なし", Tag = SnapMode.None });
        SnapCombo.Items.Add(new ComboBoxItem { Content = "グリッド", Tag = SnapMode.Grid });
        SnapCombo.Items.Add(new ComboBoxItem { Content = "画面の端", Tag = SnapMode.Edges });
        SnapCombo.SelectedIndex = (int)Settings.Snap;

        GridSizeSlider.Value = Settings.GridSize;
        UpdateGridSizeHeader();

        LockAllToggle.IsOn = Settings.LockAllWidgets;
        HideOnFullscreenToggle.IsOn = Settings.HideOnFullscreen;
        FollowAnimatedWallpaperToggle.IsOn = Settings.FollowAnimatedWallpaper;
        MetricToggle.IsOn = Settings.UseMetric;

        UpdateFrameRateStatus();
        UpdateLocationText();
        BuildSavedColors();

        DataPathText.Text = $"{Paths.DataDirectory}\n{Crash.LogPath}";
        CrashLogLink.IsEnabled = File.Exists(Crash.LogPath);

        _loading = false;
    }

    private void Commit()
    {
        if (_loading)
        {
            return;
        }

        AppServices.Store.NotifySettingsChanged();
        AppServices.Store.RequestSave();
    }

    private void ShowInfo(string message, InfoBarSeverity severity)
    {
        InfoBarControl.Severity = severity;
        InfoBarControl.Title = message;
        InfoBarControl.IsOpen = true;
    }

    private void UpdateFrameRateStatus()
    {
        var service = AppServices.FrameRate;

        if (service.IsTracing)
        {
            FrameRateStatusText.Text = "前面アプリの実 FPS を計測しています。";
            ElevateButton.Visibility = Visibility.Collapsed;
            return;
        }

        FrameRateStatusText.Text = service.UnavailableReason
            ?? "デスクトップ全体の描画レートを表示しています。";

        ElevateButton.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Relaunches through the shell with the "runas" verb so Windows shows the consent prompt.
    /// The current instance exits so the single-instance guard lets the elevated one through.
    /// </summary>
    private void OnElevateClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
            {
                ShowInfo("実行ファイルの場所を特定できませんでした。", InfoBarSeverity.Error);
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true,
                Verb = "runas",
            });

            App.Instance.ExitApp();
        }
        catch (Exception ex)
        {
            // Cancelling the UAC prompt lands here; nothing is broken, so say so plainly.
            Crash.Log(ex, "SettingsPage.OnElevateClick");
            ShowInfo("管理者としての再起動をキャンセルしました。", InfoBarSeverity.Informational);
        }
    }

    private void UpdateGridSizeHeader() => GridSizeSlider.Header = $"グリッドの間隔 {GridSizeSlider.Value:0} px";

    private void UpdateLocationText()
        => LocationText.Text = string.IsNullOrEmpty(Settings.WeatherLocationName)
            ? "場所が未設定です"
            : $"現在の場所: {Settings.WeatherLocationName}（{Settings.WeatherLatitude:0.##}, {Settings.WeatherLongitude:0.##}）";

    // ---- 全般 -------------------------------------------------------------------

    private void OnStartupToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        try
        {
            AppServices.Startup.IsEnabled = StartupToggle.IsOn;
            Settings.LaunchAtStartup = StartupToggle.IsOn;
            Commit();
        }
        catch (Exception ex)
        {
            Crash.Log(ex, "SettingsPage.Startup");
            ShowInfo("スタートアップ設定を変更できませんでした。", InfoBarSeverity.Error);
        }
    }

    private void OnStartMinimizedToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        Settings.StartMinimized = StartMinimizedToggle.IsOn;
        Commit();
    }

    private void OnAppThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || AppThemeCombo.SelectedItem is not ComboBoxItem { Tag: string theme })
        {
            return;
        }

        Settings.AppTheme = theme;
        Commit();
    }

    // ---- 配置 -------------------------------------------------------------------

    private void OnSnapChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || SnapCombo.SelectedItem is not ComboBoxItem { Tag: SnapMode mode })
        {
            return;
        }

        Settings.Snap = mode;
        Commit();
    }

    private void OnGridSizeChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        UpdateGridSizeHeader();

        if (_loading)
        {
            return;
        }

        Settings.GridSize = (int)e.NewValue;
        Commit();
    }

    private void OnLockAllToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        Settings.LockAllWidgets = LockAllToggle.IsOn;
        Commit();
    }

    private void OnHideOnFullscreenToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        Settings.HideOnFullscreen = HideOnFullscreenToggle.IsOn;
        Commit();
    }

    private void OnReloadWidgetsClick(object sender, RoutedEventArgs e)
    {
        foreach (var definition in AppServices.Store.Document.Widgets)
        {
            App.Hosts.Refresh(definition);
        }

        ShowInfo("すべてのウィジェットを再読み込みしました。", InfoBarSeverity.Success);
    }

    // ---- デスクトップの背景 -----------------------------------------------------------

    private async void OnRefreshWallpaperClick(object sender, RoutedEventArgs e)
    {
        WallpaperRefreshButton.IsEnabled = false;

        try
        {
            await AppServices.Wallpaper.RefreshAsync();
            ShowInfo("デスクトップの背景を再取得しました。", InfoBarSeverity.Success);
        }
        finally
        {
            WallpaperRefreshButton.IsEnabled = true;
        }
    }

    private void OnFollowAnimatedWallpaperToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        Settings.FollowAnimatedWallpaper = FollowAnimatedWallpaperToggle.IsOn;
        AppServices.Wallpaper.CaptureAnimated = FollowAnimatedWallpaperToggle.IsOn;
        Commit();
    }

    // ---- 天気と場所 ----------------------------------------------------------------

    private void OnMetricToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        Settings.UseMetric = MetricToggle.IsOn;
        Commit();
    }

    private void OnLocationQueryKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            OnLocationSearchClick(sender, e);
        }
    }

    private async void OnLocationSearchClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(LocationQueryBox.Text))
        {
            return;
        }

        LocationSearchButton.IsEnabled = false;

        try
        {
            var found = await AppServices.Weather.SearchLocationAsync(LocationQueryBox.Text.Trim());

            LocationResults.Items.Clear();

            foreach (var geo in found)
            {
                LocationResults.Items.Add(new ListViewItem
                {
                    Content = $"{geo.Name} — {geo.Admin} {geo.Country}".Trim(),
                    Tag = geo,
                });
            }

            LocationResults.Visibility = found.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

            if (found.Count == 0)
            {
                ShowInfo("該当する場所が見つかりませんでした。", InfoBarSeverity.Warning);
            }
        }
        catch (Exception ex)
        {
            Crash.Log(ex, "SettingsPage.SearchLocation");
            ShowInfo("場所の検索に失敗しました。ネットワークを確認してください。", InfoBarSeverity.Error);
        }
        finally
        {
            LocationSearchButton.IsEnabled = true;
        }
    }

    private async void OnLocationDetectClick(object sender, RoutedEventArgs e)
    {
        LocationDetectButton.IsEnabled = false;

        try
        {
            var geo = await AppServices.Weather.DetectLocationAsync();

            if (geo is null)
            {
                ShowInfo("現在地を特定できませんでした。", InfoBarSeverity.Warning);
                return;
            }

            ApplyLocation(geo);
            ShowInfo($"現在地を「{geo.Name}」に設定しました。", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            Crash.Log(ex, "SettingsPage.DetectLocation");
            ShowInfo("現在地の取得に失敗しました。", InfoBarSeverity.Error);
        }
        finally
        {
            LocationDetectButton.IsEnabled = true;
        }
    }

    private void OnLocationSelected(object sender, SelectionChangedEventArgs e)
    {
        if (LocationResults.SelectedItem is not ListViewItem { Tag: GeoResult geo })
        {
            return;
        }

        ApplyLocation(geo);
        LocationResults.Visibility = Visibility.Collapsed;
    }

    private void ApplyLocation(GeoResult geo)
    {
        Settings.WeatherLocationName = geo.Name;
        Settings.WeatherLatitude = geo.Latitude;
        Settings.WeatherLongitude = geo.Longitude;

        UpdateLocationText();
        AppServices.Store.NotifySettingsChanged();
        AppServices.Store.RequestSave();
    }

    // ---- 保存した色 ----------------------------------------------------------------

    private void BuildSavedColors()
    {
        SavedColorsPanel.Children.Clear();

        if (Settings.SavedColors.Count == 0)
        {
            SavedColorsPanel.Children.Add(new TextBlock
            {
                Text = "保存した色はまだありません。編集画面の色パレットから保存できます。",
                Style = (Style)Application.Current.Resources["CaptionTextStyle"],
            });

            return;
        }

        foreach (var hex in Settings.SavedColors.ToList())
        {
            var color = ColorUtil.Parse(hex);

            var swatch = new Border
            {
                Width = 56,
                Height = 40,
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(80, 128, 128, 128)),
                Background = new SolidColorBrush(color),
            };

            ToolTipService.SetToolTip(swatch, hex);
            SavedColorsPanel.Children.Add(swatch);
        }
    }

    private void OnClearSavedColorsClick(object sender, RoutedEventArgs e)
    {
        Settings.SavedColors.Clear();
        BuildSavedColors();

        AppServices.Store.NotifySettingsChanged();
        AppServices.Store.RequestSave();
    }

    // ---- データと診断 ---------------------------------------------------------------

    private void OnOpenDataFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Paths.EnsureCreated();
            Process.Start(new ProcessStartInfo(Paths.DataDirectory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Crash.Log(ex, "SettingsPage.OpenDataFolder");
            ShowInfo("フォルダーを開けませんでした。", InfoBarSeverity.Error);
        }
    }

    private void OnOpenCrashLogClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Crash.LogPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Crash.Log(ex, "SettingsPage.OpenCrashLog");
            ShowInfo("クラッシュログを開けませんでした。", InfoBarSeverity.Error);
        }
    }

    // ---- 終了 -------------------------------------------------------------------

    private async void OnExitClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "アプリを終了",
            Content = "デスクトップ上のウィジェットもすべて閉じられます。",
            PrimaryButtonText = "終了",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        MainWindow.IsExiting = true;
        App.Instance.ExitApp();
    }
}
