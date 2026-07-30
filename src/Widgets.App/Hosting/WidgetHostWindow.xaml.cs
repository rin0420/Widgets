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

    private bool _dragging;
    private int _dragOffsetX;
    private int _dragOffsetY;
    private bool _closed;
    private BackdropMode? _appliedBackdrop;

    private bool _resizing;
    private ResizeEdge _cursorEdge;
    private ResizeEdge _resizeEdge;
    private double _resizeDpi;
    private double _resizeScale;
    private double _resizeStartWidth;
    private double _resizeStartHeight;
    private double _resizeAnchorX;
    private double _resizeAnchorY;
    private double _resizeOriginFx;
    private double _resizeOriginFy;
    private double _resizeBiasX;
    private double _resizeBiasY;

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
        var (logicalWidth, logicalHeight) = WidgetMetrics.GetSize(Definition);

        // Clamped exactly like WidgetSurface clamps the content transform, or a hand-edited scale
        // would size the window and its contents differently.
        var scale = Math.Clamp(Definition.Scale, MinScale, MaxScale);
        return ((int)Math.Round(logicalWidth * scale * dpiScale), (int)Math.Round(logicalHeight * scale * dpiScale));
    }

    /// <param name="applyRegion">
    /// False while a resize gesture is in flight. Reshaping the region builds a GDI object and
    /// forces a full redraw through SetWindowRgn, which is far too much to repeat on every pointer
    /// sample; the resize clears the region up front and restores it once the gesture ends.
    /// </param>
    private void PlaceWindow(int x, int y, int width, int height, double dpiScale, bool applyRegion = true)
    {
        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));

        if (applyRegion)
        {
            ApplyRoundedRegion(width, height, dpiScale);
        }
    }

    /// <summary>
    /// The corner radius is authored in logical pixels against the unscaled widget, so it has to
    /// track both the per-widget scale and the monitor DPI to line up with what XAML draws.
    /// </summary>
    private void ApplyRoundedRegion(int width, int height, double dpiScale)
        => WindowHelper.ApplyRoundedRegion(
            _hwnd,
            width,
            height,
            Definition.Theme.CornerRadius * Math.Clamp(Definition.Scale, MinScale, MaxScale) * dpiScale);

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
        // Presets are fixed footprints by definition — only a custom-sized widget can be dragged.
        // Returning None here also stops the resize cursors from advertising a gesture that would
        // do nothing.
        if (Definition.Size != WidgetSize.Custom)
        {
            return ResizeEdge.None;
        }

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
        var (logicalWidth, logicalHeight) = WidgetMetrics.GetSize(Definition);

        _resizeEdge = edge;
        _resizeScale = Math.Clamp(Definition.Scale, MinScale, MaxScale);

        // The DPI is frozen for the whole gesture: growing a widget can move its centre onto a
        // neighbouring display, and re-reading the DPI mid-drag would resize the window under the
        // cursor. ApplyDefinition picks up the real DPI again when the gesture ends.
        _resizeDpi = WindowHelper.GetScale(_hwnd);
        _resizeStartWidth = logicalWidth;
        _resizeStartHeight = logicalHeight;

        // The opposite edge is what must not move, so the origin is expressed as a fixed anchor
        // point minus a fraction of the (changing) window size. Grabbing a plain edge leaves the
        // perpendicular axis centred on the anchor.
        _resizeOriginFx = (edge & ResizeEdge.Left) != 0 ? 1 : (edge & ResizeEdge.Right) != 0 ? 0 : 0.5;
        _resizeOriginFy = (edge & ResizeEdge.Top) != 0 ? 1 : (edge & ResizeEdge.Bottom) != 0 ? 0 : 0.5;
        _resizeAnchorX = position.X + (_resizeOriginFx * size.Width);
        _resizeAnchorY = position.Y + (_resizeOriginFy * size.Height);

        // The grab lands a few pixels inside the edge; biasing by that makes the widget start from
        // its current size instead of snapping the edge onto the cursor.
        var rawWidth = PointerWidth(cursorX);
        var rawHeight = PointerHeight(cursorY);
        _resizeBiasX = double.IsNaN(rawWidth) ? 0 : size.Width - rawWidth;
        _resizeBiasY = double.IsNaN(rawHeight) ? 0 : size.Height - rawHeight;

        // Dropped for the duration of the gesture so the window is neither clipped to the shape it
        // started at nor re-regioned on every pointer sample. EndResize puts it back.
        WindowHelper.ClearWindowRegion(_hwnd);
    }

    /// <summary>
    /// Window width in physical pixels implied by the cursor, or NaN when this gesture does not
    /// move the horizontal edges. Each axis is independent, so a corner drag reshapes freely.
    /// </summary>
    private double PointerWidth(int cursorX)
    {
        if ((_resizeEdge & ResizeEdge.Right) != 0)
        {
            return cursorX - _resizeAnchorX;
        }

        return (_resizeEdge & ResizeEdge.Left) != 0 ? _resizeAnchorX - cursorX : double.NaN;
    }

    private double PointerHeight(int cursorY)
    {
        if ((_resizeEdge & ResizeEdge.Bottom) != 0)
        {
            return cursorY - _resizeAnchorY;
        }

        return (_resizeEdge & ResizeEdge.Top) != 0 ? _resizeAnchorY - cursorY : double.NaN;
    }

    private void UpdateResize(int cursorX, int cursorY)
    {
        // Physical pixels per logical widget pixel. Scale stays fixed for the gesture — the drag
        // changes the footprint itself, not the zoom.
        var density = _resizeDpi * _resizeScale;
        if (density <= 0)
        {
            return;
        }

        // Derived from the absolute cursor position every time, never accumulated, so sitting on a
        // clamp end and dragging further leaves the geometry exactly where it was.
        var rawWidth = PointerWidth(cursorX);
        var rawHeight = PointerHeight(cursorY);

        var logicalWidth = double.IsNaN(rawWidth)
            ? _resizeStartWidth
            : Math.Round(WidgetMetrics.ClampCustom((rawWidth + _resizeBiasX) / density));

        var logicalHeight = double.IsNaN(rawHeight)
            ? _resizeStartHeight
            : Math.Round(WidgetMetrics.ClampCustom((rawHeight + _resizeBiasY) / density));

        if (Math.Abs(logicalWidth - Definition.CustomWidth) < 0.5
            && Math.Abs(logicalHeight - Definition.CustomHeight) < 0.5)
        {
            return;
        }

        Definition.CustomWidth = logicalWidth;
        Definition.CustomHeight = logicalHeight;

        var width = (int)Math.Round(logicalWidth * density);
        var height = (int)Math.Round(logicalHeight * density);
        var (x, y) = ClampToWorkArea(
            (int)Math.Round(_resizeAnchorX - (_resizeOriginFx * width)),
            (int)Math.Round(_resizeAnchorY - (_resizeOriginFy * height)),
            width,
            height);

        Definition.X = x;
        Definition.Y = y;

        // A real re-layout rather than a render transform: the content is supposed to respond to
        // the new shape, which a uniform zoom cannot do. Relayout skips the chrome and the timers,
        // so this stays cheap enough for every pointer sample.
        Surface.Relayout();

        PlaceWindow(x, y, width, height, _resizeDpi, applyRegion: false);
        Surface.SetScreenPosition(x, y);
    }

    private void EndResize()
    {
        if (!_resizing)
        {
            return;
        }

        _resizing = false;

        if (Math.Abs(Definition.CustomWidth - _resizeStartWidth) < 0.5
            && Math.Abs(Definition.CustomHeight - _resizeStartHeight) < 0.5)
        {
            // Nothing to persist, but the rounded corners BeginResize dropped still have to
            // come back — otherwise a grab that went nowhere leaves the widget square.
            var size = AppWindow.Size;
            ApplyRoundedRegion(size.Width, size.Height, _resizeDpi);
            return;
        }

        // Rebuilds the content at the final footprint and re-reads the monitor DPI the gesture
        // froze. ApplyGeometry restores the region as part of placing the window.
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
        foreach (var size in WidgetCatalog.Get(Definition.Kind).SupportedSizes.Append(WidgetSize.Custom))
        {
            var captured = size;
            var item = new RadioMenuFlyoutItem
            {
                Text = size == WidgetSize.Custom
                    ? $"{WidgetMetrics.GetDisplayName(size)}（端をドラッグ）"
                    : WidgetMetrics.GetDisplayName(size),
                GroupName = $"size_{Definition.Id}",
                IsChecked = Definition.Size == size,
            };

            item.Click += (_, _) => Mutate(d =>
            {
                // Seed the free-form footprint from whatever was showing, so picking カスタム
                // never makes the widget jump — it only unlocks the edges.
                if (captured == WidgetSize.Custom && d.Size != WidgetSize.Custom)
                {
                    var (w, h) = WidgetMetrics.GetSize(d.Size);
                    d.CustomWidth = WidgetMetrics.ClampCustom(w);
                    d.CustomHeight = WidgetMetrics.ClampCustom(h);
                }

                d.Size = captured;
            });

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
