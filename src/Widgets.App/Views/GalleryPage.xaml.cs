using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Widgets.App.Controls;
using Widgets.App.Models;
using Widgets.App.Services;

namespace Widgets.App.Views;

public sealed partial class GalleryPage : Page
{
    private readonly List<WidgetSurface> _surfaces = new();

    public GalleryPage()
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

        foreach (var group in WidgetCatalog.Entries.GroupBy(e => e.Category))
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
                ItemContainerStyle = (Style)Resources["GalleryItemStyle"],
            };

            foreach (var entry in group)
            {
                grid.Items.Add(BuildCard(entry));
            }

            CategoriesPanel.Children.Add(grid);
        }
    }

    private UIElement BuildCard(WidgetCatalogEntry entry)
    {
        var sample = WidgetCatalog.CreateDefinition(entry.Kind, entry.SupportedSizes[0]);
        var preview = PreviewBuilder.Create(sample, 268, 150, out var surface);
        _surfaces.Add(surface);

        var previewFrame = new Border
        {
            Height = 150,
            CornerRadius = new CornerRadius(8),
            Background = (Brush)Application.Current.Resources["PreviewBackdropBrush"],
            Child = preview,
        };

        var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        heading.Children.Add(new FontIcon { Glyph = entry.Glyph, FontSize = 18 });
        heading.Children.Add(new TextBlock
        {
            Text = entry.DisplayName,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (var size in entry.SupportedSizes)
        {
            chips.Children.Add(new Border
            {
                Style = (Style)Application.Current.Resources["ChipBorderStyle"],
                Child = new TextBlock { Text = WidgetMetrics.GetDisplayName(size), FontSize = 11 },
            });
        }

        var layout = new StackPanel { Spacing = 8 };
        layout.Children.Add(previewFrame);
        layout.Children.Add(heading);
        layout.Children.Add(new TextBlock
        {
            Text = entry.Description,
            TextWrapping = TextWrapping.Wrap,
            Height = 34,
            Style = (Style)Application.Current.Resources["CaptionTextStyle"],
        });
        layout.Children.Add(chips);

        var card = new Button
        {
            Width = 300,
            Padding = new Thickness(16),
            CornerRadius = new CornerRadius(8),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = layout,
        };

        card.Click += async (_, _) => await CreateWidgetAsync(entry);
        return card;
    }

    private async Task CreateWidgetAsync(WidgetCatalogEntry entry)
    {
        var selector = new RadioButtons { MaxColumns = 3 };

        foreach (var option in entry.SupportedSizes)
        {
            var (width, height) = WidgetMetrics.GetSize(option);
            selector.Items.Add(new RadioButton
            {
                Content = $"{WidgetMetrics.GetDisplayName(option)}（{width:0}×{height:0}）",
                Tag = option,
            });
        }

        selector.SelectedIndex = 0;

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"{entry.DisplayName} のサイズ",
            Content = selector,
            PrimaryButtonText = "追加",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var size = selector.SelectedItem is RadioButton { Tag: WidgetSize picked } ? picked : entry.SupportedSizes[0];
        var definition = WidgetCatalog.CreateDefinition(entry.Kind, size);

        AppServices.Store.Add(definition);
        MainWindow.Instance?.NavigateToEditor(definition);
    }
}
