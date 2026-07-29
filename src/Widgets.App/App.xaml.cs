using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Widgets.App.Hosting;
using Widgets.App.Services;
using Widgets.App.Views;

namespace Widgets.App;

public partial class App : Application
{
    private MainWindow? _mainWindow;

    /// <summary>Owns one desktop window per visible widget.</summary>
    public static WidgetHostManager Hosts { get; } = new();

    public static App Instance { get; private set; } = null!;

    public App()
    {
        Instance = this;
        InitializeComponent();

        UnhandledException += (_, e) =>
        {
            // A single misbehaving widget must never take the whole desktop down with it.
            Crash.Log(e.Exception);
            e.Handled = true;
        };
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        AppServices.UiDispatcher = DispatcherQueue.GetForCurrentThread();

        await AppServices.Store.LoadAsync();

        AppServices.Wallpaper.CaptureAnimated = AppServices.Store.Document.Settings.FollowAnimatedWallpaper;

        Hosts.Start();

        _mainWindow = new MainWindow();

        if (!AppServices.Store.Document.Settings.StartMinimized)
        {
            _mainWindow.Activate();
        }
    }

    /// <summary>Brings the manager window forward, recreating it if the user closed it earlier.</summary>
    public void ShowManagerWindow()
    {
        if (_mainWindow is null)
        {
            _mainWindow = new MainWindow();
        }

        _mainWindow.Activate();
        _mainWindow.BringToFront();
    }

    public void ExitApp()
    {
        Hosts.Stop();
        AppServices.Shutdown();
        Exit();
    }
}
