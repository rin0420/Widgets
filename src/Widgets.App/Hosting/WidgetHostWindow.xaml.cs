using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Widgets.App.Interop;
using Widgets.App.Models;
using Widgets.App.Services;
using Windows.Graphics;

namespace Widgets.App.Hosting;

/// <summary>
/// One borderless, non-activating window per placed widget. It owns geometry, z-order and
/// the drag/context-menu interaction; the visual content belongs to <c>WidgetSurface</c>.
/// </summary>
public sealed partial class WidgetHostWindow : Window
{
    private const int EdgeSnapThreshold = 12;

    /// <summary>Width of the grab band along each edge, in the window's own logical pixels.</summary>
    private const double ResizeBand = 8;

    private const double MinScale = 0.5;
    private const double MaxScale = 3.0;

    private static int _cascadeIndex;
    private static readonly Dictionary<InputSystemCursorShape, InputCursor> SharedCursors = new();

    private readonly IntPtr _hwnd;
    private readonly DispatcherTimer _zOrderTimer;

    /// <summary>Reused so a resize gesture allocates nothing per pointer sample.</summary>
    private readonly ScaleTransform _resizePreview = new();

    private bool _dragging;
    private int _dragOffsetX;
    private int _dragOffsetY;
    private bool _closed;
    private BackdropMode? _appliedBackdrop;

    private bool _resizing;
    private ResizeEdge _cursorEdge;
    private double _resizeStartScale;
    private double _resizeDpi;
    private double _resizeBaseWidth;
    private double _resizeBaseHeight;
    private double _resizeAnchorX;
    private double _resizeAnchorY;
    private double _resizeOriginFx;
    private double _resizeOriginFy;
    private double _resizeDirX;
    private double _resizeDirY;
    private double _resizeBias;

    public WidgetHostWindow(WidgetDefinition definition)
    {
        InitializeComponent();

        Definition = definition;
        _hwnd = WindowHelper.GetHwnd(this);

        ConfigureChrome();

        Root.PointerPressed += OnPointerPressed;
        Root.PointerMoved += OnPointerMoved;
        Root.PointerReleased += OnPointerReleased;
        Root.PointerCaptureLost += OnPointerCaptureLost;
        Root.RightTapped += OnRightTapped;

        Activated += OnActivated;
        Closed += OnClosed;

        ApplyDefinition(definition);

        _zOrderTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _zOrderTimer.Tick += OnZOrderTick;
        _zOrderTimer.Start();
    }

    public WidgetDefinition Definition { get; private set; }

    public IntPtr Hwnd => _hwnd;

    public event EventHandler<WidgetDefinition>? EditRequested;

    public event EventHandler<WidgetDefinition>? DuplicateRequested;

    public event EventHandler<WidgetDefinition>? DeleteRequested;

    public event EventHandler<WidgetDefinition>? DefinitionChanged;

    public event EventHandler<WidgetDefinition>? PositionChanged;

    public void ApplyDefinition(WidgetDefinition definition)
    {
        try
        {
            Definition = definition;
            Title = string.IsNullOrWhiteSpace(definition.Name) ? "Widget" : definition.Name;

            ApplyGeometry();

            // Swapping the backdrop object flickers, so only do it when the mode actually changes.
            if (_appliedBackdrop != definition.Theme.Backdrop)
            {
                _appliedBackdrop = definition.Theme.Backdrop;
                WindowHelper.ApplyBackdrop(this, definition.Theme.Backdrop);
            }

            WindowHelper.ApplyZOrder(_hwnd, definition.ZOrder);
            WindowHelper.SetClickThrough(_hwnd, definition.ClickThrough);

            // Acrylic and Mica derive their luminosity from the element theme, so a widget with
            // light text on a light system theme washes out to unreadable. Drive the theme from the
            // widget's own foreground rather than from Windows.
            var tint = Common.ColorUtil.Parse(definition.Theme.TintColor, Microsoft.UI.Colors.White);
            Root.RequestedTheme = Common.ColorUtil.Luminance(tint) > 0.5
                ? ElementTheme.Dark
                : ElementTheme.Light;

            Root.Opacity = Math.Clamp(definition.Theme.Opacity, 0.1, 1.0);
            Surface.SetDefinition(definition, false);
            Surface.SetScreenPosition(definition.X, definition.Y);
        }
        catch (Exception ex)
        {
            Crash.Log(ex, nameof(ApplyDefinition));
        }
    }

    /// <summary>Shows the window without taking focus away from whatever the user is doing.</summary>
    public void ShowWidget()
    {
        try
        {
            Activate();
            Win32.ShowWindow(_hwnd, Win32.SW_SHOWNOACTIVATE);
            WindowHelper.ApplyZOrder(_hwnd, Definition.ZOrder);
        }
        catch (Exception ex)
        {
            Crash.Log(ex, nameof(ShowWidget));
        }
    }

    public void SetHidden(bool hidden)
    {
        try
        {
            Win32.ShowWindow(_hwnd, hidden ? Win32.SW_HIDE : Win32.SW_SHOWNOACTIVATE);
            if (!hidden)
            {
                WindowHelper.ApplyZOrder(_hwnd, Definition.ZOrder);
            }
        }
        catch (Exception ex)
        {
            Crash.Log(ex, nameof(SetHidden));
        }
    }

    private bool IsLocked
        => Definition.Locked || AppServices.Store.Document.Settings.LockAllWidgets;

    private void ConfigureChrome()
    {
        try
        {
            var appWindow = AppWindow;
            appWindow.IsShownInSwitchers = false;

            if (appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsResizable = false;
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = false;
                presenter.SetBorderAndTitleBar(false, false);
            }

            WindowHelper.ApplyWidgetChrome(_hwnd);
            WindowHelper.SetTransparentBackdrop(this);
        }
        catch (Exception ex)
        {
            Crash.Log(ex, nameof(ConfigureChrome));
        }
    }

    private void ApplyGeometry()
    {
        var scale = WindowHelper.GetScale(_hwnd);
        var (width, height) = MeasurePhysicalSize(scale);

        // A freshly created definition has no position yet; cascade so widgets never stack exactly.
        if (Definition.X == 0 && Definition.Y == 0)
        {
            var area = WindowHelper.GetPrimaryWorkArea();
            var step = (int)Math.Round(36 * scale);
            var offset = step * (1 + (_cascadeIndex++ % 8));
            Definition.X = Math.Min(area.X + offset, Math.Max(area.X, area.X + area.Width - width));
            Definition.Y = Math.Min(area.Y + offset, Math.Max(area.Y, area.Y + area.Height - height));
        }

        PlaceWindow(Definition.X, Definition.Y, width, height, scale);
    }

    /// <summary>Window size in physical pixels for the current footprint and scale.</summary>
    private (int Width, int Height) MeasurePhysicalSize(double dpiScale)
    {
        var (logicalWidth, logicalHeight) = WidgetMetrics.GetSize(Definition.Size);

        // Clamped exactly like WidgetSurface clamps the content transform, or a hand-edited scale
        // would size the window and its contents differently.
        var scale = Math.Clamp(Definition.Scale, MinScale, MaxScale);
        return ((int)Math.Round(logicalWidth * scale * dpiScale), (int)Math.Round(logicalHeight * scale * dpiScale));
    }

    private void PlaceWindow(int x, int y, int width, int height, double dpiScale)
    {
        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));

        // The corner radius is authored in logical pixels against the unscaled widget, so it has to
        // track both the per-widget scale and the monitor DPI to line up with what XAML draws.
        WindowHelper.ApplyRoundedRegion(
            _hwnd, width, height, Definition.Theme.CornerRadius * Math.Clamp(Definition.Scale, MinScale, MaxScale) * dpiScale);
    }

    // ---- Dragging --------------------------------------------------------------

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        try
        {
            var point = e.GetCurrentPoint(Root);
            if (IsLocked || !point.Properties.IsLeftButtonPressed)
            {
                return;
            }

            if (!WindowHelper.TryGetCursorPosition(out var cursorX, out var cursorY))
            {
                return;
            }

            // The edge bands resize, everything inside them moves, so the two gestures can never
            // start together.
            var edge = HitTestEdge(point.Position);
            if (edge != ResizeEdge.None)
            {
                // Captured first: BeginResize re-lays out the surface, and only EndResize undoes
                // that, which never runs for a gesture that failed to start.
                if (Root.CapturePointer(e.Pointer))
                {
                    _resizing = true;
                    BeginResize(edge, cursorX, cursorY);
                    e.Handled = true;
                }

                return;
            }

            var position = AppWindow.Position;
            _dragOffsetX = cursorX - position.X;
            _dragOffsetY = cursorY - position.Y;
            _dragging = Root.CapturePointer(e.Pointer);
            e.Handled = _dragging;
        }
        catch (Exception ex)
        {
            Crash.Log(ex, nameof(OnPointerPressed));
        }
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        try
        {
            if (!_dragging && !_resizing)
            {
                ApplyCursor(IsLocked ? ResizeEdge.None : HitTestEdge(e.GetCurrentPoint(Root).Position));
                return;
            }

            if (!WindowHelper.TryGetCursorPosition(out var cursorX, out var cursorY))
            {
                return;
            }

            if (_resizing)
            {
                UpdateResize(cursorX, cursorY);
            }
            else
            {
                var size = AppWindow.Size;
                var (x, y) = Snap(cursorX - _dragOffsetX, cursorY - _dragOffsetY, size.Width, size.Height);
                AppWindow.Move(new PointInt32(x, y));
                Surface.SetScreenPosition(x, y);
            }

            e.Handled = true;
        }
        catch (Exception ex)
        {
            Crash.Log(ex, nameof(OnPointerMoved));
        }
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        try
        {
            if (!_dragging && !_resizing)
            {
                return;
            }

            Root.ReleasePointerCapture(e.Pointer);
            EndDrag();
            EndResize();
            e.Handled = true;
        }
        catch (Exception ex)
        {
            Crash.Log(ex, nameof(OnPointerReleased));
        }
    }

    private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        try
        {
            EndDrag();
            EndResize();
        }
        catch (Exception ex)
        {
            Crash.Log(ex, nameof(OnPointerCaptureLost));
        }
    }

    private void EndDrag()
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;

        var position = AppWindow.Position;
        if (position.X == Definition.X && position.Y == Definition.Y)
        {
            return;
        }

        Definition.X = position.X;
        Definition.Y = position.Y;
        PositionChanged?.Invoke(this, Definition);
    }

    private (int X, int Y) Snap(int x, int y, int width, int height)
    {
        var settings = AppServices.Store.Document.Settings;
        var scale = WindowHelper.GetScale(_hwnd);

        switch (settings.Snap)
        {
            case SnapMode.Grid:
                var pitch = Math.Max(1, (int)Math.Round(settings.GridSize * scale));
                return ((int)(Math.Round((double)x / pitch) * pitch),
                        (int)(Math.Round((double)y / pitch) * pitch));

            case SnapMode.Edges:
                var area = WindowHelper.GetWorkAreaAt(x + (width / 2), y + (height / 2));
                var threshold = (int)Math.Round(EdgeSnapThreshold * scale);

                if (Math.Abs(x - area.X) <= threshold)
                {
                    x = area.X;
                }
                else if (Math.Abs(area.X + area.Width - (x + width)) <= threshold)
                {
                    x = area.X + area.Width - width;
                }

                if (Math.Abs(y - area.Y) <= threshold)
                {
                    y = area.Y;
                }
                else if (Math.Abs(area.Y + area.Height - (y + height)) <= threshold)
                {
                    y = area.Y + area.Height - height;
                }

                return (x, y);

            default:
                return (x, y);
        }
    }

    // ---- Resizing --------------------------------------------------------------

    [Flags]
    private enum ResizeEdge
    {
        None = 0,
        Left = 1,
        Right = 2,
        Top = 4,
        Bottom = 8,
    }

    /// <summary>
    /// Which grab band the pointer sits in. The point comes in logical pixels, so the band is the
    /// same physical width on every monitor without any DPI arithmetic.
    /// </summary>
    private ResizeEdge HitTestEdge(Windows.Foundation.Point point)
    {
        var width = Root.ActualWidth;
        var height = Root.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return ResizeEdge.None;
        }

        // A widget at scale 0.5 is only 88 logical pixels wide, so the band has to give way rather
        // than swallow the whole surface and make the widget undraggable.
        var bandX = Math.Min(ResizeBand, width / 3);
        var bandY = Math.Min(ResizeBand, height / 3);

        var edge = ResizeEdge.None;

        if (point.X <= bandX)
        {
            edge |= ResizeEdge.Left;
        }
        else if (point.X >= width - bandX)
        {
            edge |= ResizeEdge.Right;
        }

        if (point.Y <= bandY)
        {
            edge |= ResizeEdge.Top;
        }
        else if (point.Y >= height - bandY)
        {
            edge |= ResizeEdge.Bottom;
        }

        return edge;
    }

    private void BeginResize(ResizeEdge edge, int cursorX, int cursorY)
    {
        var position = AppWindow.Position;
        var size = AppWindow.Size;
        var (logicalWidth, logicalHeight) = WidgetMetrics.GetSize(Definition.Size);

        _resizeStartScale = Math.Clamp(Definition.Scale, MinScale, MaxScale);

        // The DPI is frozen for the whole gesture: growing a widget can move its centre onto a
        // neighbouring display, and re-reading the DPI mid-drag would resize the window under the
        // cursor. ApplyDefinition picks up the real DPI again when the gesture ends.
        _resizeDpi = WindowHelper.GetScale(_hwnd);
        _resizeBaseWidth = logicalWidth * _resizeDpi;
        _resizeBaseHeight = logicalHeight * _resizeDpi;

        // The opposite edge is what must not move, so the origin is expressed as a fixed anchor
        // point minus a fraction of the (changing) window size. Grabbing a plain edge leaves the
        // perpendicular axis centred on the anchor.
        _resizeOriginFx = (edge & ResizeEdge.Left) != 0 ? 1 : (edge & ResizeEdge.Right) != 0 ? 0 : 0.5;
        _resizeOriginFy = (edge & ResizeEdge.Top) != 0 ? 1 : (edge & ResizeEdge.Bottom) != 0 ? 0 : 0.5;
        _resizeAnchorX = position.X + (_resizeOriginFx * size.Width);
        _resizeAnchorY = position.Y + (_resizeOriginFy * size.Height);

        // Growth direction in physical pixels per 1.0 of Scale. Zeroing the axis that was not
        // grabbed makes the projection below collapse to a plain one-dimensional ratio.
        _resizeDirX = (edge & ResizeEdge.Right) != 0 ? _resizeBaseWidth
            : (edge & ResizeEdge.Left) != 0 ? -_resizeBaseWidth : 0;
        _resizeDirY = (edge & ResizeEdge.Bottom) != 0 ? _resizeBaseHeight
            : (edge & ResizeEdge.Top) != 0 ? -_resizeBaseHeight : 0;

        // The grab lands a few pixels inside the edge; biasing by that makes the widget start from
        // its current scale instead of jumping on the first move.
        _resizeBias = _resizeStartScale - ProjectScale(cursorX, cursorY);

        // The content follows with a render transform rather than a rebuild per pointer sample.
        // The surface has to be pinned to the window origin at its current size first: it is
        // normally stretched, and the widget's fixed-size root would drift to the centre of the
        // growing window and then scale away from there.
        Surface.HorizontalAlignment = HorizontalAlignment.Left;
        Surface.VerticalAlignment = VerticalAlignment.Top;
        Surface.Width = logicalWidth * _resizeStartScale;
        Surface.Height = logicalHeight * _resizeStartScale;
        _resizePreview.ScaleX = 1;
        _resizePreview.ScaleY = 1;
        Surface.RenderTransformOrigin = new Windows.Foundation.Point(0, 0);
        Surface.RenderTransform = _resizePreview;
    }

    /// <summary>
    /// Least-squares projection of the cursor onto the growth direction: the scale closest to the
    /// pointer that still keeps the footprint's aspect ratio. Everything here is physical pixels.
    /// </summary>
    private double ProjectScale(int cursorX, int cursorY)
    {
        var dx = cursorX - _resizeAnchorX;
        var dy = cursorY - _resizeAnchorY;
        return ((dx * _resizeDirX) + (dy * _resizeDirY))
            / ((_resizeDirX * _resizeDirX) + (_resizeDirY * _resizeDirY));
    }

    private void UpdateResize(int cursorX, int cursorY)
    {
        // Scale is derived from the absolute cursor position every time, never accumulated, so
        // sitting on a clamp end and dragging further leaves the geometry exactly where it was.
        // Quantising keeps a fast drag from rebuilding the window region on every pointer sample.
        var scale = Math.Clamp(Math.Round(ProjectScale(cursorX, cursorY) + _resizeBias, 2), MinScale, MaxScale);
        if (Math.Abs(scale - Definition.Scale) < 0.001)
        {
            return;
        }

        Definition.Scale = scale;

        var width = (int)Math.Round(_resizeBaseWidth * scale);
        var height = (int)Math.Round(_resizeBaseHeight * scale);
        var (x, y) = ClampToWorkArea(
            (int)Math.Round(_resizeAnchorX - (_resizeOriginFx * width)),
            (int)Math.Round(_resizeAnchorY - (_resizeOriginFy * height)),
            width,
            height);

        Definition.X = x;
        Definition.Y = y;

        var factor = scale / _resizeStartScale;
        _resizePreview.ScaleX = factor;
        _resizePreview.ScaleY = factor;

        PlaceWindow(x, y, width, height, _resizeDpi);

        // Definition.Scale is already the live value, so the wallpaper layer stays registered with
        // the screen while the widget grows.
        Surface.SetScreenPosition(x, y);
    }

    private void EndResize()
    {
        if (!_resizing)
        {
            return;
        }

        _resizing = false;

        Surface.RenderTransform = null;
        Surface.Width = double.NaN;
        Surface.Height = double.NaN;
        Surface.HorizontalAlignment = HorizontalAlignment.Stretch;
        Surface.VerticalAlignment = VerticalAlignment.Stretch;

        if (Math.Abs(Definition.Scale - _resizeStartScale) < 0.001)
        {
            return;
        }

        // Rebuilds the content at the final scale and re-reads the monitor DPI the gesture froze.
        ApplyDefinition(Definition);
        DefinitionChanged?.Invoke(this, Definition);
    }

    /// <summary>Keeps a resized widget on screen, along whichever axes it still fits.</summary>
    private static (int X, int Y) ClampToWorkArea(int x, int y, int width, int height)
    {
        var area = WindowHelper.GetWorkAreaAt(x + (width / 2), y + (height / 2));

        if (width <= area.Width)
        {
            x = Math.Clamp(x, area.X, area.X + area.Width - width);
        }

        if (height <= area.Height)
        {
            y = Math.Clamp(y, area.Y, area.Y + area.Height - height);
        }

        return (x, y);
    }

    private void ApplyCursor(ResizeEdge edge)
    {
        if (edge == _cursorEdge)
        {
            return;
        }

        _cursorEdge = edge;
        Root.SetCursor(ResizeCursor(edge));
    }

    private static InputCursor? ResizeCursor(ResizeEdge edge)
    {
        InputSystemCursorShape shape;

        switch (edge)
        {
            case ResizeEdge.Left | ResizeEdge.Top:
            case ResizeEdge.Right | ResizeEdge.Bottom:
                shape = InputSystemCursorShape.SizeNorthwestSoutheast;
                break;

            case ResizeEdge.Right | ResizeEdge.Top:
            case ResizeEdge.Left | ResizeEdge.Bottom:
                shape = InputSystemCursorShape.SizeNortheastSouthwest;
                break;

            case ResizeEdge.Left:
            case ResizeEdge.Right:
                shape = InputSystemCursorShape.SizeWestEast;
                break;

            case ResizeEdge.Top:
            case ResizeEdge.Bottom:
                shape = InputSystemCursorShape.SizeNorthSouth;
                break;

            default:
                // Null restores the inherited arrow rather than drawing no cursor at all.
                return null;
        }

        // System cursors are immutable and shared by every widget window, so they are created once.
        if (!SharedCursors.TryGetValue(shape, out var cursor))
        {
            cursor = InputSystemCursor.Create(shape);
            SharedCursors[shape] = cursor;
        }

        return cursor;
    }

    // ---- Context menu ----------------------------------------------------------

    private void OnRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        try
        {
            var flyout = BuildMenu();
            flyout.ShowAt(Root, new FlyoutShowOptions { Position = e.GetPosition(Root) });
            e.Handled = true;
        }
        catch (Exception ex)
        {
            Crash.Log(ex, nameof(OnRightTapped));
        }
    }

    private MenuFlyout BuildMenu()
    {
        // A widget window is only ~176px wide, so the menu has to be free to spill outside it.
        var menu = new MenuFlyout { ShouldConstrainToRootBounds = false };

        var edit = new MenuFlyoutItem { Text = "編集" };
        edit.Click += (_, _) => Raise(EditRequested);
        menu.Items.Add(edit);

        var duplicate = new MenuFlyoutItem { Text = "複製" };
        duplicate.Click += (_, _) => Raise(DuplicateRequested);
        menu.Items.Add(duplicate);

        menu.Items.Add(new MenuFlyoutSeparator());

        var sizes = new MenuFlyoutSubItem { Text = "サイズ" };
        foreach (var size in WidgetCatalog.Get(Definition.Kind).SupportedSizes)
        {
            var captured = size;
            var item = new RadioMenuFlyoutItem
            {
                Text = WidgetMetrics.GetDisplayName(size),
                GroupName = $"size_{Definition.Id}",
                IsChecked = Definition.Size == size,
            };
            item.Click += (_, _) => Mutate(d => d.Size = captured);
            sizes.Items.Add(item);
        }

        menu.Items.Add(sizes);

        var order = new MenuFlyoutSubItem { Text = "表示順" };
        foreach (var (mode, label) in new[]
                 {
                     (ZOrderMode.TopMost, "最前面"),
                     (ZOrderMode.Desktop, "デスクトップ"),
                     (ZOrderMode.Normal, "通常"),
                 })
        {
            var captured = mode;
            var item = new RadioMenuFlyoutItem
            {
                Text = label,
                GroupName = $"z_{Definition.Id}",
                IsChecked = Definition.ZOrder == mode,
            };
            item.Click += (_, _) => Mutate(d => d.ZOrder = captured);
            order.Items.Add(item);
        }

        menu.Items.Add(order);

        var clickThrough = new ToggleMenuFlyoutItem { Text = "クリックスルー", IsChecked = Definition.ClickThrough };
        clickThrough.Click += (s, _) => Mutate(d => d.ClickThrough = ((ToggleMenuFlyoutItem)s).IsChecked);
        menu.Items.Add(clickThrough);

        var locked = new ToggleMenuFlyoutItem { Text = "ロック", IsChecked = Definition.Locked };
        locked.Click += (s, _) => Mutate(d => d.Locked = ((ToggleMenuFlyoutItem)s).IsChecked);
        menu.Items.Add(locked);

        menu.Items.Add(new MenuFlyoutSeparator());

        var delete = new MenuFlyoutItem { Text = "削除" };
        delete.Click += (_, _) => Raise(DeleteRequested);
        menu.Items.Add(delete);

        return menu;
    }

    private void Raise(EventHandler<WidgetDefinition>? handler)
    {
        try
        {
            handler?.Invoke(this, Definition);
        }
        catch (Exception ex)
        {
            Crash.Log(ex, nameof(Raise));
        }
    }

    private void Mutate(Action<WidgetDefinition> change)
    {
        try
        {
            change(Definition);
            ApplyDefinition(Definition);
            DefinitionChanged?.Invoke(this, Definition);
        }
        catch (Exception ex)
        {
            Crash.Log(ex, nameof(Mutate));
        }
    }

    // ---- Z-order upkeep --------------------------------------------------------

    private void OnZOrderTick(object? sender, object e)
    {
        try
        {
            if (!_closed && Definition.ZOrder == ZOrderMode.Desktop)
            {
                WindowHelper.ApplyZOrder(_hwnd, ZOrderMode.Desktop);
            }
        }
        catch (Exception ex)
        {
            Crash.Log(ex, nameof(OnZOrderTick));
        }
    }

    private void OnActivated(object sender, WindowActivatedEventArgs e)
    {
        try
        {
            // Clicking a desktop-pinned widget raises it; push it straight back down.
            if (Definition.ZOrder == ZOrderMode.Desktop)
            {
                WindowHelper.ApplyZOrder(_hwnd, ZOrderMode.Desktop);
            }
        }
        catch (Exception ex)
        {
            Crash.Log(ex, nameof(OnActivated));
        }
    }

    private void OnClosed(object sender, WindowEventArgs e)
    {
        try
        {
            _closed = true;
            _zOrderTimer.Stop();
            _zOrderTimer.Tick -= OnZOrderTick;
            Activated -= OnActivated;
            Closed -= OnClosed;
        }
        catch (Exception ex)
        {
            Crash.Log(ex, nameof(OnClosed));
        }
    }
}

/// <summary>
/// The host window's root panel. It exists only because <c>ProtectedCursor</c> is protected on
/// <c>UIElement</c>, so the resize cursors cannot be set from the window that owns the panel.
/// </summary>
public sealed class WidgetRootPanel : Grid
{
    public void SetCursor(InputCursor? cursor) => ProtectedCursor = cursor;
}
