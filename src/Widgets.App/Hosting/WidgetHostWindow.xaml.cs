using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
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

    private static int _cascadeIndex;

    private readonly IntPtr _hwnd;
    private readonly DispatcherTimer _zOrderTimer;

    private bool _dragging;
    private int _dragOffsetX;
    private int _dragOffsetY;
    private bool _closed;
    private BackdropMode? _appliedBackdrop;

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
        var (logicalWidth, logicalHeight) = WidgetMetrics.GetSize(Definition.Size);
        var width = (int)Math.Round(logicalWidth * Definition.Scale * scale);
        var height = (int)Math.Round(logicalHeight * Definition.Scale * scale);

        // A freshly created definition has no position yet; cascade so widgets never stack exactly.
        if (Definition.X == 0 && Definition.Y == 0)
        {
            var area = WindowHelper.GetPrimaryWorkArea();
            var step = (int)Math.Round(36 * scale);
            var offset = step * (1 + (_cascadeIndex++ % 8));
            Definition.X = Math.Min(area.X + offset, Math.Max(area.X, area.X + area.Width - width));
            Definition.Y = Math.Min(area.Y + offset, Math.Max(area.Y, area.Y + area.Height - height));
        }

        AppWindow.MoveAndResize(new RectInt32(Definition.X, Definition.Y, width, height));

        // The corner radius is authored in logical pixels against the unscaled widget, so it has to
        // track both the per-widget scale and the monitor DPI to line up with what XAML draws.
        WindowHelper.ApplyRoundedRegion(
            _hwnd, width, height, Definition.Theme.CornerRadius * Definition.Scale * scale);
    }

    // ---- Dragging --------------------------------------------------------------

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        try
        {
            if (IsLocked || !e.GetCurrentPoint(Root).Properties.IsLeftButtonPressed)
            {
                return;
            }

            if (!WindowHelper.TryGetCursorPosition(out var cursorX, out var cursorY))
            {
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
            if (!_dragging || !WindowHelper.TryGetCursorPosition(out var cursorX, out var cursorY))
            {
                return;
            }

            var size = AppWindow.Size;
            var (x, y) = Snap(cursorX - _dragOffsetX, cursorY - _dragOffsetY, size.Width, size.Height);
            AppWindow.Move(new PointInt32(x, y));
            Surface.SetScreenPosition(x, y);
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
            if (!_dragging)
            {
                return;
            }

            Root.ReleasePointerCapture(e.Pointer);
            EndDrag();
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
