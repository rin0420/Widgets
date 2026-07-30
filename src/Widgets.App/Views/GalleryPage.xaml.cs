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

        card.Click += (_, _) => CreateWidget(entry);
        return card;
    }

    /// <summary>
    /// Adds the widget at its default size and goes straight to the editor. There used to be a
    /// size dialog here, but the editor already has a size picker at the top of the preview and
    /// a drag-resizable frame, so asking up front was one modal in the way of every add.
    /// </summary>
    private void CreateWidget(WidgetCatalogEntry entry)
    {
        var definition = WidgetCatalog.CreateDefinition(entry.Kind, entry.SupportedSizes[0]);

        AppServices.Store.Add(definition);
        MainWindow.Instance?.NavigateToEditor(definition);
    }
}
