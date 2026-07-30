using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Widgets.App.Common;
using Widgets.App.Models;
using Widgets.App.Services;
using Widgets.App.Widgets;

namespace Widgets.App.Views;

public sealed partial class ThemesPage : Page
{
    public ThemesPage()
    {
        InitializeComponent();

        Loaded += (_, _) => Build();
    }

    private void Build()
    {
        CategoriesPanel.Children.Clear();

        foreach (var group in AppServices.Store.AllPresets.GroupBy(p => p.Category))
        {
            CategoriesPanel.Children.Add(new TextBlock
            {
                Text = group.Key,
                Style = (Style)Application.Current.Resources["SectionHeaderTextStyle"],
            });

            var grid = new GridView
            {
                SelectionMode = ListViewSelectionMode.None,
                IsItemClickEnabled = false,
                Padding = new Thickness(0),
                ItemContainerStyle = (Style)Resources["PresetItemStyle"],
            };

            foreach (var preset in group)
            {
                grid.Items.Add(BuildCard(preset));
            }

            CategoriesPanel.Children.Add(grid);
        }
    }

    private UIElement BuildCard(ThemePreset preset)
    {
        var previewFrame = new Border
        {
            Height = 176,
            CornerRadius = new CornerRadius(8),
            Background = (Brush)Application.Current.Resources["PreviewBackdropBrush"],
            Child = BuildSwatch(preset.Theme),
        };

        var title = new TextBlock
        {
            Text = preset.Name,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var caption = new TextBlock
        {
            Text = $"{(preset.IsBuiltIn ? "組み込み" : "カスタム")} ・ {BackdropName(preset.Theme.Backdrop)}",
            Style = (Style)Application.Current.Resources["CaptionTextStyle"],
        };

        var applyButton = new Button { Content = "テーマを適用", MinWidth = 152 };
        applyButton.Click += async (_, _) => await ApplyPresetAsync(preset);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 4, 0, 0),
        };

        actions.Children.Add(applyButton);

        if (!preset.IsBuiltIn)
        {
            var deleteButton = new Button
            {
                Content = new FontIcon { Glyph = "", FontSize = 14 },
                Width = 40,
            };

            ToolTipService.SetToolTip(deleteButton, "このプリセットを削除");
            deleteButton.Click += async (_, _) => await DeletePresetAsync(preset);
            actions.Children.Add(deleteButton);
        }

        var layout = new StackPanel { Spacing = 8 };
        layout.Children.Add(previewFrame);
        layout.Children.Add(title);
        layout.Children.Add(caption);
        layout.Children.Add(BuildPalette(preset.Theme));
        layout.Children.Add(actions);

        return new Border
        {
            Style = (Style)Application.Current.Resources["CardBorderStyle"],
            Width = 208,
            Child = layout,
        };
    }

    /// <summary>
    /// A pure type-and-color sample. This used to render a real DigitalClock widget, which made the
    /// card look like "apply = add a clock" — so nothing here instantiates a widget any more.
    /// </summary>
    private static UIElement BuildSwatch(WidgetTheme theme)
    {
        var body = new StackPanel
        {
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
        };

        body.Children.Add(new TextBlock
        {
            Text = "Aa",
            FontFamily = WidgetVisuals.Font(theme),
            FontWeight = WidgetVisuals.Weight(theme),
            FontSize = WidgetVisuals.Size(theme, 30),
            Foreground = WidgetVisuals.TintBrush(theme),
        });

        body.Children.Add(new Border
        {
            Width = 40,
            Height = 4,
            CornerRadius = new CornerRadius(2),
            Background = WidgetVisuals.AccentBrush(theme),
            HorizontalAlignment = HorizontalAlignment.Left,
        });

        body.Children.Add(new TextBlock
        {
            Text = "サブテキスト",
            FontFamily = WidgetVisuals.Font(theme),
            FontWeight = WidgetVisuals.Weight(theme),
            FontSize = WidgetVisuals.Size(theme, 11),
            Foreground = WidgetVisuals.SecondaryBrush(theme),
        });

        return new Border
        {
            Width = 148,
            Height = 124,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(SwatchFill(theme)),
            BorderBrush = ColorUtil.Brush(theme.BorderColor),
            BorderThickness = new Thickness(theme.BorderThickness),
            CornerRadius = new CornerRadius(theme.CornerRadius),
            Padding = new Thickness(theme.Padding),
            Opacity = Math.Clamp(theme.Opacity, 0.1, 1.0),
            Child = body,
        };
    }

    /// <summary>
    /// The swatch is a flat rectangle, so the translucent backdrops are approximated by alpha over
    /// the card's gradient rather than by really compositing acrylic/mica.
    /// </summary>
    private static Windows.UI.Color SwatchFill(WidgetTheme theme)
    {
        var background = WidgetVisuals.Background(theme);

        return theme.Backdrop switch
        {
            BackdropMode.Clear => ColorUtil.WithAlpha(background, 0x00),
            BackdropMode.Frosted => ColorUtil.WithAlpha(background, (byte)Math.Min((int)background.A, 0xB4)),
            BackdropMode.Mica => ColorUtil.WithAlpha(background, 0xE6),
            _ => background,
        };
    }

    private static UIElement BuildPalette(WidgetTheme theme)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };

        foreach (var color in new[]
                 {
                     WidgetVisuals.Tint(theme),
                     WidgetVisuals.Accent(theme),
                     WidgetVisuals.Secondary(theme),
                     ColorUtil.WithAlpha(WidgetVisuals.Background(theme), 0xFF),
                 })
        {
            row.Children.Add(new Border
            {
                Width = 18,
                Height = 18,
                CornerRadius = new CornerRadius(9),
                Background = new SolidColorBrush(ColorUtil.WithAlpha(color, 0xFF)),
                // A fixed neutral hairline rather than a ThemeResource lookup: these dots can be any
                // color, and the outline only has to separate a light swatch from a light card.
                BorderBrush = ColorUtil.Brush("#40808080"),
                BorderThickness = new Thickness(1),
            });
        }

        return row;
    }

    private static string BackdropName(BackdropMode mode) => mode switch
    {
        BackdropMode.Solid => "単色",
        BackdropMode.Frosted => "すりガラス",
        BackdropMode.Mica => "マイカ",
        BackdropMode.Clear => "透明",
        BackdropMode.Photo => "画像",
        _ => mode.ToString(),
    };

    private async Task ApplyPresetAsync(ThemePreset preset)
    {
        var widgets = AppServices.Store.Document.Widgets;

        if (widgets.Count == 0)
        {
            ShowInfo("適用できるウィジェットがありません。先にギャラリーから追加してください。", InfoBarSeverity.Warning);
            return;
        }

        var list = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            MaxHeight = 260,
            IsEnabled = false,
        };

        foreach (var definition in widgets)
        {
            var entry = WidgetCatalog.Get(definition.Kind);
            list.Items.Add(new ListViewItem
            {
                Content = $"{(string.IsNullOrWhiteSpace(definition.Name) ? entry.DisplayName : definition.Name)}"
                          + $"（{entry.DisplayName} ・ {WidgetMetrics.GetDisplayName(definition.Size)}）",
                Tag = definition,
            });
        }

        list.SelectedIndex = 0;

        var allOption = new RadioButton
        {
            Content = $"すべてのウィジェットに適用（{widgets.Count}個）",
            GroupName = "ApplyScope",
            IsChecked = true,
        };

        var oneOption = new RadioButton
        {
            Content = "選んだウィジェットだけに適用",
            GroupName = "ApplyScope",
        };

        // The list is only meaningful for the single-widget branch; greying it out keeps the
        // dialog from implying a selection matters when "all" is chosen.
        allOption.Checked += (_, _) => list.IsEnabled = false;
        oneOption.Checked += (_, _) => list.IsEnabled = true;

        var content = new StackPanel { Spacing = 8, Width = 380 };
        content.Children.Add(allOption);
        content.Children.Add(oneOption);
        content.Children.Add(list);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"「{preset.Name}」を適用",
            Content = content,
            PrimaryButtonText = "適用",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        if (allOption.IsChecked == true)
        {
            // ToList() because Update() writes back into Document.Widgets while we walk it.
            foreach (var target in widgets.ToList())
            {
                target.Theme = preset.Theme.Clone();
                AppServices.Store.Update(target);
            }

            ShowInfo($"「{preset.Name}」を{widgets.Count}個のウィジェットに適用しました。", InfoBarSeverity.Success);
            return;
        }

        if (list.SelectedItem is not ListViewItem { Tag: WidgetDefinition selected })
        {
            ShowInfo("ウィジェットを選んでください。", InfoBarSeverity.Warning);
            return;
        }

        selected.Theme = preset.Theme.Clone();
        AppServices.Store.Update(selected);

        ShowInfo($"「{preset.Name}」を適用しました。", InfoBarSeverity.Success);
    }

    private async Task DeletePresetAsync(ThemePreset preset)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "プリセットを削除",
            Content = $"「{preset.Name}」を削除します。この操作は元に戻せません。",
            PrimaryButtonText = "削除",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        AppServices.Store.DeleteCustomPreset(preset.Id);
        Build();
    }

    private void ShowInfo(string message, InfoBarSeverity severity)
    {
        InfoBarControl.Severity = severity;
        InfoBarControl.Title = message;
        InfoBarControl.IsOpen = true;
    }
}
