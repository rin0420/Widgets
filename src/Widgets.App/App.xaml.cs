using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Widgets.App.Hosting;
using Widgets.App.Services;
using Widgets.App.Views;

namespace Widgets.App;

public partial class App : Application
{
    /// <summary>
    /// Passed by the logon entry the launch-at-startup toggle writes. Without it the app cannot
    /// tell a logon launch from a double-click, and <see cref="AppSettings.StartMinimized"/> would
    /// swallow the window in both cases — which looks exactly like the exe failing to start.
    /// </summary>
    public const string StartupArgument = "--startup";

    private const string InstanceMutexName = @"Local\Widgets.App.SingleInstance";
    private const string ActivateEventName = @"Local\Widgets.App.Activate";

    private static Mutex? _instanceMutex;

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
        // A second copy would draw every widget a second time on top of the first, and with
        // StartMinimized on there is nothing on screen to show that it happened. Hand the request
        // to the instance that already owns the desktop and get out of the way.
        if (!TryClaimSingleInstance())
        {
            SignalExistingInstance();
            Exit();
            return;
        }

        AppServices.UiDispatcher = DispatcherQueue.GetForCurrentThread();
        StartActivationListener();

        await AppServices.Store.LoadAsync();

        AppServices.Wallpaper.CaptureAnimated = AppServices.Store.Document.Settings.FollowAnimatedWallpaper;

        // Older installs registered the logon entry without the argument below; rewrite it so
        // StartMinimized starts behaving as documented.
        AppServices.Startup.RepairRegistration();

        // Falls back to the composition rate on its own when the ETW session cannot be opened,
        // so this never blocks startup.
        AppServices.FrameRate.Start();

        Hosts.Start();

        _mainWindow = new MainWindow();

        // StartMinimized is about the logon launch. A double-click always shows the window —
        // otherwise the app gives no sign at all that it started.
        if (!AppServices.Store.Document.Settings.StartMinimized || !LaunchedAtLogon())
        {
            _mainWindow.Activate();
        }
    }

    private static bool LaunchedAtLogon()
        => Environment.GetCommandLineArgs()
            .Skip(1)
            .Any(a => string.Equals(a, StartupArgument, StringComparison.OrdinalIgnoreCase));

    private static bool TryClaimSingleInstance()
    {
        try
        {
            _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out var created);
            return created;
        }
        catch (Exception ex)
        {
            // Failing open is the safer default: worst case the old duplicate behaviour returns,
            // where refusing to start would leave the user with no widgets at all.
            Crash.Log(ex, "App.TryClaimSingleInstance");
            return true;
        }
    }

    private static void SignalExistingInstance()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ActivateEventName, out var handle))
            {
                using (handle)
                {
                    handle.Set();
                }
            }
        }
        catch (Exception ex)
        {
            Crash.Log(ex, "App.SignalExistingInstance");
        }
    }

    /// <summary>Brings this instance forward whenever another copy is launched.</summary>
    private void StartActivationListener()
    {
        try
        {
            var handle = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);

            var thread = new Thread(() =>
            {
                while (handle.WaitOne())
                {
                    AppServices.OnUi(ShowManagerWindow);
                }
            })
            {
                IsBackground = true,
                Name = "Widgets.Activation",
            };

            thread.Start();
        }
        catch (Exception ex)
        {
            Crash.Log(ex, "App.StartActivationListener");
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
