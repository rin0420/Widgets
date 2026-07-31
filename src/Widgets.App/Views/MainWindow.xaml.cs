using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Widgets.App.Common;
using Widgets.App.Models;
using Widgets.App.Services;
using Windows.Graphics;

namespace Widgets.App.Views;

public sealed partial class MainWindow : Window
{
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr hWnd);

    private bool _suppressNavigation;

    /// <summary>The manager window currently alive, used by pages for dialogs and file pickers.</summary>
    public static MainWindow? Instance { get; private set; }

    /// <summary>Set before <c>App.ExitApp</c> so the close handler stops hiding the window instead.</summary>
    public static bool IsExiting { get; set; }

    public IntPtr Hwnd { get; }

    public MainWindow()
    {
        InitializeComponent();

        Instance = this;
        Hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        Title = "Widgets";

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        ApplyBackdrop();
        ConfigureAppWindow();
        ApplyAppTheme();

        NavView.SelectedItem = NavView.MenuItems[0];

        App.Hosts.EditRequested += OnEditRequested;
        AppServices.Store.SettingsChanged += OnSettingsChanged;
        AppWindow.Changed += OnAppWindowChanged;
        Closed += OnClosed;
    }

    /// <summary>
    /// Rebuilds the page after <see cref="ReleaseContent"/> dropped it.
    ///
    /// This hangs off AppWindow.Changed rather than Window.VisibilityChanged: the window is put
    /// away with AppWindow.Hide, and the Window-level event does not fire for that at all — a
    /// manager that reappears blank is worse than one that never released anything.
    /// </summary>
    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidVisibilityChange || !sender.IsVisible || ContentFrame.Content is not null)
        {
            return;
        }

        var tag = (NavView.SelectedItem as NavigationViewItem)?.Tag as string;
        ContentFrame.Navigate(PageFor(tag), null, new EntranceNavigationTransitionInfo());
    }

    private void ApplyBackdrop()
    {
        try
        {
            SystemBackdrop = MicaController.IsSupported()
                ? new MicaBackdrop()
                : new DesktopAcrylicBackdrop();
        }
        catch (Exception ex)
        {
            Crash.Log(ex, "MainWindow.ApplyBackdrop");
        }
    }

    private void ConfigureAppWindow()
    {
        var appWindow = AppWindow;

        appWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        appWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;

        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 1000;
            presenter.PreferredMinimumHeight = 680;
        }

        appWindow.Resize(new SizeInt32(1180, 800));
        appWindow.Closing += OnAppWindowClosing;
    }

    private void ApplyAppTheme()
    {
        if (RootGrid is null)
        {
            return;
        }

        RootGrid.RequestedTheme = AppServices.Store.Document.Settings.AppTheme switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
    }

    private void OnSettingsChanged(object? sender, EventArgs e) => AppServices.OnUi(ApplyAppTheme);

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        // Closing the manager only puts it away; the desktop widgets keep running.
        if (IsExiting)
        {
            return;
        }

        args.Cancel = true;
        sender.Hide();
        ReleaseContent();
    }

    /// <summary>
    /// Drops the page the manager was showing and gives the memory back.
    ///
    /// Hiding a window keeps its whole visual tree alive. The gallery in particular holds sixteen
    /// live widget surfaces with running timers, and none of that is worth keeping while the app
    /// sits in the tray. <see cref="BringToFront"/> navigates again when the window comes back.
    /// </summary>
    private void ReleaseContent()
    {
        try
        {
            // Navigating to null runs the pages' Unloaded handlers, which is what releases the
            // preview surfaces; clearing Content alone would not.
            ContentFrame.Content = null;
            ContentFrame.BackStack.Clear();
            ContentFrame.ForwardStack.Clear();
        }
        catch (Exception ex)
        {
            Crash.Log(ex, "MainWindow.ReleaseContent");
        }

        MemoryTrim.Release();
    }

    private static Type PageFor(string? tag) => tag switch
    {
        "gallery" => typeof(GalleryPage),
        "themes" => typeof(ThemesPage),
        "settings" => typeof(SettingsPage),
        _ => typeof(MyWidgetsPage),
    };

    private void OnClosed(object sender, WindowEventArgs args)
    {
        App.Hosts.EditRequested -= OnEditRequested;
        AppServices.Store.SettingsChanged -= OnSettingsChanged;
        AppWindow.Changed -= OnAppWindowChanged;

        if (ReferenceEquals(Instance, this))
        {
            Instance = null;
        }
    }

    private void OnEditRequested(object? sender, WidgetDefinition definition)
        => AppServices.OnUi(() =>
        {
            BringToFront();
            NavigateToEditor(definition);
        });

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_suppressNavigation || args.SelectedItem is not NavigationViewItem item)
        {
            return;
        }

        var target = PageFor(item.Tag as string);

        if (ContentFrame.CurrentSourcePageType != target)
        {
            ContentFrame.Navigate(target, null, new EntranceNavigationTransitionInfo());
        }
    }

    /// <summary>Restores, shows and focuses the manager window.</summary>
    public void BringToFront()
    {
        AppWindow.Show();

        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
        {
            presenter.Restore();
        }

        Activate();
        SetForegroundWindow(Hwnd);
    }

    /// <summary>Opens the deep-customization editor for <paramref name="definition"/>.</summary>
    public void NavigateToEditor(WidgetDefinition definition)
    {
        _suppressNavigation = true;
        NavView.SelectedItem = null;
        _suppressNavigation = false;

        ContentFrame.Navigate(typeof(EditorPage), definition, new DrillInNavigationTransitionInfo());
    }

    /// <summary>Selects a top-level section by its navigation tag ("my", "gallery", "themes", "settings").</summary>
    public void NavigateTo(string tag)
    {
        foreach (var menuItem in NavView.MenuItems)
        {
            if (menuItem is NavigationViewItem item && (string?)item.Tag == tag)
            {
                NavView.SelectedItem = item;
                return;
            }
        }
    }
}
