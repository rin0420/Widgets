using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Widgets.App.Controls;
using Widgets.App.Models;
using Widgets.App.Services;

namespace Widgets.App.Views;

public sealed partial class MyWidgetsPage : Page
{
    private readonly List<WidgetSurface> _surfaces = new();

    public ObservableCollection<UIElement> Cards { get; } = new();

    public MyWidgetsPage()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var store = AppServices.Store;
        store.WidgetAdded += OnWidgetsChanged;
        store.WidgetChanged += OnWidgetsChanged;
        store.WidgetRemoved += OnWidgetRemoved;

        Rebuild();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        var store = AppServices.Store;
        store.WidgetAdded -= OnWidgetsChanged;
        store.WidgetChanged -= OnWidgetsChanged;
        store.WidgetRemoved -= OnWidgetRemoved;

        ReleaseSurfaces();
    }

    private void OnWidgetsChanged(object? sender, WidgetDefinition definition) => AppServices.OnUi(Rebuild);

    private void OnWidgetRemoved(object? sender, string id) => AppServices.OnUi(Rebuild);

    private void ReleaseSurfaces()
    {
        foreach (var surface in _surfaces)
        {
            surface.Cleanup();
        }

        _surfaces.Clear();
    }

    private void Rebuild()
    {
        ReleaseSurfaces();
        Cards.Clear();

        var widgets = AppServices.Store.Document.Widgets;

        foreach (var definition in widgets)
        {
            Cards.Add(BuildCard(definition));
        }

        SubtitleText.Text = $"{widgets.Count} 個のウィジェット";
        EmptyState.Visibility = widgets.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CardsView.Visibility = widgets.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private UIElement BuildCard(WidgetDefinition definition)
    {
        var entry = WidgetCatalog.Get(definition.Kind);

        var preview = PreviewBuilder.Create(definition, 268, 168, out var surface);
        _surfaces.Add(surface);

        var previewFrame = new Border
        {
            Height = 168,
            CornerRadius = new CornerRadius(8),
            Background = (Brush)Application.Current.Resources["PreviewBackdropBrush"],
            Child = preview,
        };

        var title = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(definition.Name) ? entry.DisplayName : definition.Name,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var caption = new TextBlock
        {
            Text = $"{entry.DisplayName} ・ {WidgetMetrics.GetDisplayName(definition.Size)}"
                   + (definition.IsVisible ? string.Empty : " ・ 非表示"),
            Style = (Style)Application.Current.Resources["CaptionTextStyle"],
        };

        var editButton = new Button { Content = "編集", MinWidth = 96 };
        editButton.Click += (_, _) => MainWindow.Instance?.NavigateToEditor(definition);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 4, 0, 0),
        };

        actions.Children.Add(editButton);
        actions.Children.Add(IconButton("", "複製", (_, _) => Duplicate(definition)));
        actions.Children.Add(IconButton("", definition.IsVisible ? "非表示にする" : "表示する",
            (_, _) => ToggleVisibility(definition)));
        actions.Children.Add(IconButton("", "削除", async (_, _) => await ConfirmDeleteAsync(definition)));

        var layout = new StackPanel { Spacing = 8, Opacity = definition.IsVisible ? 1.0 : 0.55 };
        layout.Children.Add(previewFrame);
        layout.Children.Add(title);
        layout.Children.Add(caption);
        layout.Children.Add(actions);

        return new Border
        {
            Style = (Style)Application.Current.Resources["CardBorderStyle"],
            Width = 300,
            Child = layout,
        };
    }

    private static Button IconButton(string glyph, string tooltip, RoutedEventHandler onClick)
    {
        var button = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = 14 },
            Width = 40,
        };

        ToolTipService.SetToolTip(button, tooltip);
        button.Click += onClick;
        return button;
    }

    private void Duplicate(WidgetDefinition definition)
    {
        var copy = definition.Clone();
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Name = $"{(string.IsNullOrWhiteSpace(definition.Name) ? WidgetCatalog.Get(definition.Kind).DisplayName : definition.Name)} のコピー";
        copy.X += 32;
        copy.Y += 32;

        foreach (var slot in copy.TimedSlots)
        {
            slot.Id = Guid.NewGuid().ToString("N");
        }

        AppServices.Store.Add(copy);
    }

    private void ToggleVisibility(WidgetDefinition definition)
    {
        definition.IsVisible = !definition.IsVisible;
        AppServices.Store.Update(definition);
    }

    private async Task ConfirmDeleteAsync(WidgetDefinition definition)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "ウィジェットを削除",
            Content = $"「{(string.IsNullOrWhiteSpace(definition.Name) ? WidgetCatalog.Get(definition.Kind).DisplayName : definition.Name)}」を削除します。この操作は元に戻せません。",
            PrimaryButtonText = "削除",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            AppServices.Store.Remove(definition.Id);
        }
    }

    private void OnAddClick(object sender, RoutedEventArgs e) => MainWindow.Instance?.NavigateTo("gallery");
}

/// <summary>Builds a non-interactive <see cref="WidgetSurface"/> scaled down to fit a fixed box.</summary>
internal static class PreviewBuilder
{
    public static UIElement Create(WidgetDefinition definition, double boxWidth, double boxHeight, out WidgetSurface surface)
    {
        var (width, height) = WidgetMetrics.GetSize(definition);
        var scale = Math.Min(Math.Min(boxWidth / width, boxHeight / height), 1.0);

        surface = new WidgetSurface { Width = width, Height = height };
        surface.SetDefinition(definition, true);

        var inner = new Grid
        {
            Width = width,
            Height = height,
            RenderTransform = new ScaleTransform { ScaleX = scale, ScaleY = scale },
        };

        inner.Children.Add(surface);

        return new Grid
        {
            Width = width * scale,
            Height = height * scale,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { inner },
        };
    }
}
