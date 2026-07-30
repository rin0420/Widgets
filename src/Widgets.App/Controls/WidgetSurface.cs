using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Widgets.App.Common;
using Widgets.App.Models;
using Widgets.App.Services;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI;

namespace Widgets.App.Controls;

/// <summary>
/// The chrome every widget is drawn in: backdrop, border, corner radius, padding and the
/// tick timer. Both the desktop host window and the editor's live preview host one of these,
/// so a widget looks identical wherever it is shown.
/// </summary>
public sealed class WidgetSurface : ContentControl
{
    private readonly Grid _root = new();
    private readonly Border _shadowReceiver = new();
    private readonly Border _chrome = new();
    private readonly Grid _layers = new();
    private readonly Image _wallpaperLayer = new();
    private readonly Border _photoLayer = new();
    private readonly Border _scrim = new();
    private readonly Border _contentHost = new();
    private readonly DispatcherTimer _tick = new();
    private readonly DispatcherTimer _slotWatch = new();

    private WidgetViewBase? _view;
    private WidgetKind? _viewKind;
    private string? _activeSlotId;
    private bool _isPreview;
    private int _screenX;
    private int _screenY;
    private bool _wallpaperWanted;
    private bool _wallpaperBlurred;
    private int _wallpaperToken;

    public WidgetSurface()
    {
        IsTabStop = false;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        Padding = new Thickness(0);
        BorderThickness = new Thickness(0);

        _shadowReceiver.Background = new SolidColorBrush(Colors.Transparent);
        _shadowReceiver.IsHitTestVisible = false;

        // Stretch.Fill with an explicit logical size is what pins one wallpaper pixel to one screen
        // pixel; Stretch.None would render the bitmap at 1px-per-DIP and drift on scaled displays.
        _wallpaperLayer.Stretch = Stretch.Fill;
        _wallpaperLayer.HorizontalAlignment = HorizontalAlignment.Left;
        _wallpaperLayer.VerticalAlignment = VerticalAlignment.Top;
        _wallpaperLayer.IsHitTestVisible = false;
        _wallpaperLayer.Visibility = Visibility.Collapsed;

        _photoLayer.IsHitTestVisible = false;
        _photoLayer.Visibility = Visibility.Collapsed;

        _scrim.IsHitTestVisible = false;
        _scrim.Visibility = Visibility.Collapsed;

        _layers.Children.Add(_wallpaperLayer);
        _layers.Children.Add(_photoLayer);
        _layers.Children.Add(_scrim);
        _layers.Children.Add(_contentHost);

        _chrome.Child = _layers;
        _chrome.BackgroundSizing = BackgroundSizing.InnerBorderEdge;

        _root.Children.Add(_shadowReceiver);
        _root.Children.Add(_chrome);

        base.Content = _root;

        _tick.Tick += OnTickTimer;
        _slotWatch.Interval = TimeSpan.FromMinutes(1);
        _slotWatch.Tick += OnSlotWatch;

        AppServices.Wallpaper.Changed += OnWallpaperChanged;

        Loaded += (_, _) =>
        {
            Resume();
            UpdateWallpaperLayout();
        };
        Unloaded += (_, _) => Suspend();
    }

    public WidgetDefinition? Definition { get; private set; }

    /// <summary>Applies a definition, rebuilding the chrome and (when the kind changed) the renderer.</summary>
    public void SetDefinition(WidgetDefinition definition, bool isPreview)
    {
        Definition = definition;
        _isPreview = isPreview;
        Rebuild();
    }

    /// <summary>
    /// Re-lays the content out at the definition's current footprint, skipping the chrome and the
    /// tick timers. This is the live-resize path: a custom-size drag calls it on every pointer
    /// sample, where rebuilding acrylic brushes and restarting timers would stutter.
    /// </summary>
    public void Relayout() => Rebuild(rebuildChrome: false);

    /// <summary>Moves the wallpaper layer to keep it registered with the screen. Cheap enough to call while dragging.</summary>
    public void SetScreenPosition(int physicalX, int physicalY)
    {
        _screenX = physicalX;
        _screenY = physicalY;
        UpdateWallpaperLayout();
    }

    /// <summary>Stops the timers and releases the renderer. Safe to call more than once.</summary>
    public void Cleanup()
    {
        Suspend();

        AppServices.Wallpaper.Changed -= OnWallpaperChanged;
        _wallpaperToken++;
        _wallpaperWanted = false;
        _wallpaperLayer.Source = null;
        _wallpaperLayer.Visibility = Visibility.Collapsed;

        try
        {
            _view?.Cleanup();
        }
        catch (Exception ex)
        {
            Crash.Log(ex, "WidgetSurface.Cleanup");
        }

        _contentHost.Child = null;
        _view = null;
        _viewKind = null;
    }

    private void Rebuild(bool rebuildChrome = true)
    {
        var definition = Definition;
        if (definition is null)
        {
            return;
        }

        try
        {
            var slot = ActiveSlot(definition);
            _activeSlotId = slot?.Id;

            var kind = slot?.Kind ?? definition.Kind;
            var settings = MergeSettings(definition, slot);
            var theme = definition.Theme;

            var (width, height) = WidgetMetrics.GetSize(definition);
            _root.Width = width;
            _root.Height = height;

            // A free-form footprint has no preset to switch on, so it borrows the layout of the
            // preset it most resembles and folds the leftover difference into the type scale. That
            // is what makes a dragged widget reflow (columns, rows, which lines survive) instead of
            // just being the small layout stretched over a bigger box.
            var custom = definition.Size == WidgetSize.Custom;
            var layoutSize = custom ? WidgetMetrics.NearestPreset(width, height) : definition.Size;

            // Cloned, not mutated: every renderer already routes its font sizes through
            // WidgetVisuals.Size(theme, …), so folding the factor into FontScale scales typography
            // everywhere without touching all sixteen renderers. The chrome keeps the real theme —
            // padding and corner radius should not grow with the type.
            var contentTheme = theme;
            if (custom)
            {
                contentTheme = theme.Clone();
                contentTheme.FontScale = theme.FontScale
                    * WidgetMetrics.TypeScaleFor(width, height, layoutSize);
            }

            // The host sizes the window to logical * Scale, so the content has to be scaled to match
            // or it renders at 1x inside an oversized window. A render transform keeps text and
            // vector gauges crisp instead of resampling a bitmap. The editor preview stays at 1x —
            // it fits the design into its own box and would overflow if scaled twice.
            var contentScale = Math.Clamp(definition.Scale, 0.5, 3.0);
            var scaling = !_isPreview && Math.Abs(contentScale - 1.0) >= 0.001;

            // A render transform does not affect layout, so the fixed-size root would still be
            // centred in the larger window and then scale away from that centre, landing offset and
            // clipped. Pinning it to the top-left makes the scaled box fill the window exactly.
            _root.HorizontalAlignment = scaling ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
            _root.VerticalAlignment = scaling ? VerticalAlignment.Top : VerticalAlignment.Stretch;

            _root.RenderTransformOrigin = new Windows.Foundation.Point(0, 0);
            _root.RenderTransform = scaling
                ? new ScaleTransform { ScaleX = contentScale, ScaleY = contentScale }
                : null;

            if (rebuildChrome)
            {
                ApplyChrome(theme);
            }

            var pad = Math.Max(0, theme.Padding);
            var edge = Math.Max(0, theme.BorderThickness);
            _contentHost.Padding = new Thickness(pad);

            if (_view is null || _viewKind != kind)
            {
                _view?.Cleanup();
                _view = WidgetViewFactory.Create(kind);
                _viewKind = kind;
                _contentHost.Child = _view;
            }

            _view.Apply(new WidgetRenderContext
            {
                Definition = definition,
                EffectiveKind = kind,
                Settings = settings,
                Theme = contentTheme,
                Size = layoutSize,
                IsCustom = custom,
                Width = Math.Max(1, width - 2 * (pad + edge)),
                Height = Math.Max(1, height - 2 * (pad + edge)),
                IsPreview = _isPreview,
            });

            if (rebuildChrome)
            {
                Resume();
            }
        }
        catch (Exception ex)
        {
            Crash.Log(ex, "WidgetSurface.Rebuild");
            ShowFailure();
        }
    }

    private static TimedSlot? ActiveSlot(WidgetDefinition definition)
    {
        var now = DateTime.Now.TimeOfDay;
        return definition.TimedSlots.FirstOrDefault(s => s.Contains(now));
    }

    private static IReadOnlyDictionary<string, string> MergeSettings(WidgetDefinition definition, TimedSlot? slot)
    {
        if (slot is null)
        {
            return definition.Settings;
        }

        var merged = new Dictionary<string, string>(definition.Settings);
        foreach (var pair in slot.Settings)
        {
            merged[pair.Key] = pair.Value;
        }

        return merged;
    }

    private void ApplyChrome(WidgetTheme theme)
    {
        var radius = new CornerRadius(Math.Max(0, theme.CornerRadius));
        _chrome.CornerRadius = radius;
        _photoLayer.CornerRadius = radius;
        _scrim.CornerRadius = radius;
        _shadowReceiver.CornerRadius = radius;

        _chrome.BorderThickness = new Thickness(Math.Max(0, theme.BorderThickness));
        _chrome.BorderBrush = new SolidColorBrush(ColorUtil.Parse(theme.BorderColor, Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)));

        _root.Opacity = Math.Clamp(theme.Opacity, 0.1, 1.0);

        var background = ColorUtil.Parse(theme.BackgroundColor, Color.FromArgb(0xE5, 0x20, 0x20, 0x20));
        _photoLayer.Visibility = Visibility.Collapsed;
        _scrim.Visibility = Visibility.Collapsed;

        switch (theme.Backdrop)
        {
            case BackdropMode.Frosted:
                // The acrylic underneath is only the fallback for when the wallpaper cache is missing.
                _chrome.Background = CreateAcrylic(background);
                _scrim.Background = new SolidColorBrush(background);
                _scrim.Visibility = Visibility.Visible;
                break;

            case BackdropMode.Mica:
                // Mica is wallpaper tinted by the surface colour, so the tint has to be opaque and
                // the blurred wallpaper rides on top of it at partial strength.
                _chrome.Background = new SolidColorBrush(ColorUtil.WithAlpha(background, 0xFF));
                break;

            case BackdropMode.Clear:
                // Text sits straight on the wallpaper here, so contrast cannot be guaranteed by the
                // theme alone. A faint scrim in the opposite luminance keeps it legible on any
                // wallpaper while still reading as transparent.
                _chrome.Background = new SolidColorBrush(Colors.Transparent);
                _scrim.Background = new SolidColorBrush(
                    ColorUtil.Luminance(ColorUtil.Parse(theme.TintColor, Colors.White)) > 0.5
                        ? Color.FromArgb(0x4D, 0, 0, 0)
                        : Color.FromArgb(0x4D, 0xFF, 0xFF, 0xFF));
                _scrim.Visibility = Visibility.Visible;
                break;

            case BackdropMode.Photo when TryCreatePhotoBrush(theme, out var photo):
                _chrome.Background = new SolidColorBrush(ColorUtil.WithAlpha(background, 0xFF));
                _photoLayer.Background = photo;
                _photoLayer.Opacity = Math.Clamp(theme.BackgroundImageOpacity, 0, 1);
                _photoLayer.Visibility = Visibility.Visible;
                _scrim.Background = CreateScrim(background);
                _scrim.Visibility = Visibility.Visible;
                break;

            default:
                _chrome.Background = new SolidColorBrush(background);
                break;
        }

        ApplyWallpaper(theme.Backdrop);
        ApplyShadow(theme.DropShadow);
    }

    // ---- Wallpaper layer -------------------------------------------------------

    /// <summary>
    /// Widget windows are non-activating, which makes Windows refuse them acrylic and Mica. The
    /// transparent modes therefore paint the desktop wallpaper themselves — sound, because a widget
    /// sits at the bottom of the z-order and has nothing but the wallpaper behind it.
    /// </summary>
    private void ApplyWallpaper(BackdropMode mode)
    {
        // The editor preview draws its own backdrop and is not registered with the screen.
        var wanted = !_isPreview && mode is BackdropMode.Clear or BackdropMode.Frosted or BackdropMode.Mica;
        if (!wanted)
        {
            _wallpaperToken++;
            _wallpaperWanted = false;
            _wallpaperLayer.Source = null;
            _wallpaperLayer.Visibility = Visibility.Collapsed;
            return;
        }

        var blurred = mode != BackdropMode.Clear;
        var reload = !_wallpaperWanted || _wallpaperBlurred != blurred || _wallpaperLayer.Source is null;

        _wallpaperWanted = true;
        _wallpaperBlurred = blurred;
        _wallpaperLayer.Opacity = mode == BackdropMode.Mica ? 0.75 : 1.0;
        UpdateWallpaperLayout();

        if (reload)
        {
            _ = LoadWallpaperAsync();
        }
    }

    private void UpdateWallpaperLayout()
    {
        if (!_wallpaperWanted)
        {
            return;
        }

        try
        {
            var screen = AppServices.Wallpaper.VirtualScreen;

            // The layer lives inside the surface's own render transform, so both the monitor DPI and
            // the per-widget scale sit between logical units here and physical screen pixels.
            var raster = XamlRoot?.RasterizationScale ?? 1.0;
            if (raster <= 0)
            {
                raster = 1.0;
            }

            var content = Math.Clamp(Definition?.Scale ?? 1.0, 0.5, 3.0);
            var scale = raster * content;

            // The host positions the outer window but the XAML island only covers the client area;
            // without the invisible frame between them the wallpaper sits off register by a few pixels.
            var island = XamlRoot?.Size.Width ?? 0;
            var frame = island > 0 && !double.IsNaN(_root.Width)
                ? Math.Max(0, ((_root.Width * content) - island) / 2 * raster)
                : 0;

            _wallpaperLayer.Width = screen.Width / scale;
            _wallpaperLayer.Height = screen.Height / scale;
            _wallpaperLayer.Margin = new Thickness(
                -(_screenX + frame - screen.X) / scale,
                -(_screenY + frame - screen.Y) / scale,
                0,
                0);
        }
        catch (Exception ex)
        {
            Crash.Log(ex, "WidgetSurface.UpdateWallpaperLayout");
        }
    }

    private async Task LoadWallpaperAsync()
    {
        var token = ++_wallpaperToken;
        var blurred = _wallpaperBlurred;

        try
        {
            var path = await AppServices.Wallpaper.GetDesktopImageAsync(blurred);
            if (path is null || token != _wallpaperToken || !_wallpaperWanted)
            {
                return;
            }

            var image = new BitmapImage();
            using (var stream = await FileRandomAccessStream.OpenAsync(path, FileAccessMode.Read))
            {
                await image.SetSourceAsync(stream);
            }

            if (token != _wallpaperToken || !_wallpaperWanted)
            {
                return;
            }

            _wallpaperLayer.Source = image;
            _wallpaperLayer.Visibility = Visibility.Visible;
            UpdateWallpaperLayout();
        }
        catch (Exception ex)
        {
            Crash.Log(ex, "WidgetSurface.LoadWallpaper");
        }
    }

    private void OnWallpaperChanged(object? sender, EventArgs e)
    {
        if (_wallpaperWanted)
        {
            _ = LoadWallpaperAsync();
        }
    }

    private static Brush CreateAcrylic(Color background)
    {
        try
        {
            return new AcrylicBrush
            {
                TintColor = background,
                TintOpacity = 0.4,
                TintLuminosityOpacity = 0.85,
                FallbackColor = ColorUtil.Fade(background, 0.9),
            };
        }
        catch (Exception ex)
        {
            Crash.Log(ex, "WidgetSurface.CreateAcrylic");
            return new SolidColorBrush(ColorUtil.Fade(background, 0.75));
        }
    }

    /// <summary>A bottom-weighted wash so text stays legible over a bright photo.</summary>
    private static Brush CreateScrim(Color background) => new LinearGradientBrush
    {
        StartPoint = new Windows.Foundation.Point(0, 0),
        EndPoint = new Windows.Foundation.Point(0, 1),
        GradientStops =
        {
            new GradientStop { Offset = 0.0, Color = ColorUtil.Fade(background, 0.18) },
            new GradientStop { Offset = 0.55, Color = ColorUtil.Fade(background, 0.30) },
            new GradientStop { Offset = 1.0, Color = ColorUtil.Fade(background, 0.70) },
        },
    };

    private static bool TryCreatePhotoBrush(WidgetTheme theme, out ImageBrush brush)
    {
        brush = null!;

        try
        {
            var path = theme.BackgroundImagePath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return false;
            }

            brush = new ImageBrush
            {
                ImageSource = new BitmapImage(new Uri(path)),
                Stretch = Stretch.UniformToFill,
            };

            return true;
        }
        catch (Exception ex)
        {
            Crash.Log(ex, "WidgetSurface.TryCreatePhotoBrush");
            return false;
        }
    }

    private void ApplyShadow(bool enabled)
    {
        try
        {
            if (enabled)
            {
                var shadow = new ThemeShadow();
                shadow.Receivers.Add(_shadowReceiver);
                _chrome.Shadow = shadow;
                _chrome.Translation = new Vector3(0, 0, 20);
            }
            else
            {
                _chrome.Shadow = null;
                _chrome.Translation = Vector3.Zero;
            }
        }
        catch (Exception ex)
        {
            Crash.Log(ex, "WidgetSurface.ApplyShadow");
        }
    }

    private void ShowFailure()
    {
        _tick.Stop();
        _view = null;
        _viewKind = null;
        _contentHost.Child = new TextBlock
        {
            Text = "!",
            FontSize = 28,
            Opacity = 0.6,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Colors.White),
        };
    }

    private void Resume()
    {
        _tick.Stop();
        _slotWatch.Stop();

        if (Definition is null)
        {
            return;
        }

        var interval = _view?.TickInterval ?? TimeSpan.Zero;
        if (interval > TimeSpan.Zero)
        {
            _tick.Interval = interval;
            _tick.Start();
            OnTickTimer(null, null);
        }

        if (Definition.TimedSlots.Count > 0)
        {
            _slotWatch.Start();
        }
    }

    private void Suspend()
    {
        _tick.Stop();
        _slotWatch.Stop();
    }

    private void OnTickTimer(object? sender, object? e)
    {
        try
        {
            _view?.OnTick(DateTimeOffset.Now);
        }
        catch (Exception ex)
        {
            Crash.Log(ex, $"WidgetSurface.OnTick({_viewKind})");
        }
    }

    private void OnSlotWatch(object? sender, object? e)
    {
        var definition = Definition;
        if (definition is null)
        {
            return;
        }

        if (ActiveSlot(definition)?.Id != _activeSlotId)
        {
            Rebuild();
        }
    }
}
