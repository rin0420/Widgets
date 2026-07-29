using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Widgets.App.Controls;
using Widgets.App.Models;
using Widgets.App.Services;

namespace Widgets.App.Views;

public sealed partial class ThemesPage : Page
{
    private readonly List<WidgetSurface> _surfaces = new();

    public ThemesPage()
    {
        InitializeComponent();

        Loaded += (_, _) => Build();
        Unloaded += (_, _) => ReleaseSurfaces();
    }

    private void ReleaseSurfaces()
    {
        foreach (var surface in _surfaces)
        {
            surface.Cleanup();
        }

        _surfaces.Clear();
    }

    private void Build()
    {
        ReleaseSurfaces();
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
        var sample = WidgetCatalog.CreateDefinition(WidgetKind.DigitalClock, WidgetSize.Small);
        sample.Theme = preset.Theme.Clone();

        var preview = PreviewBuilder.Create(sample, 176, 176, out var surface);
        _surfaces.Add(surface);

        var previewFrame = new Border
        {
            Height = 176,
            CornerRadius = new CornerRadius(8),
            Background = (Brush)Application.Current.Resources["PreviewBackdropBrush"],
            Child = preview,
        };

        var title = new TextBlock
        {
            Text = preset.Name,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var caption = new TextBlock
        {
            Text = preset.IsBuiltIn ? "組み込み" : "カスタム",
            Style = (Style)Application.Current.Resources["CaptionTextStyle"],
        };

        var applyButton = new Button { Content = "ウィジェットに適用", MinWidth = 152 };
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
                Content = new FontIcon { Glyph = "", FontSize = 14 },
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
        layout.Children.Add(actions);

        return new Border
        {
            Style = (Style)Application.Current.Resources["CardBorderStyle"],
            Width = 208,
            Child = layout,
        };
    }

    private async Task ApplyPresetAsync(ThemePreset preset)
    {
        var widgets = AppServices.Store.Document.Widgets;

        if (widgets.Count == 0)
        {
            ShowInfo("適用できるウィジェットがありません。先にギャラリーから追加してください。", InfoBarSeverity.Warning);
            return;
        }

        var list = new ListView { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 320 };

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

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"「{preset.Name}」を適用",
            Content = list,
            PrimaryButtonText = "適用",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        if (list.SelectedItem is not ListViewItem { Tag: WidgetDefinition target })
        {
            return;
        }

        target.Theme = preset.Theme.Clone();
        AppServices.Store.Update(target);

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
