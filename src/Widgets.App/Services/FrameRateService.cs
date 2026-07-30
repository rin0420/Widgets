using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using Widgets.App.Common;

namespace Widgets.App.Services;

/// <summary>Where a frame rate reading came from, which decides how it should be labelled.</summary>
public enum FrameRateSource
{
    None,

    /// <summary>Rate the desktop compositor is presenting at. A fallback — it tracks the refresh rate.</summary>
    Composition,

    /// <summary>Real present rate of the foreground application, i.e. the frame rate inside the game.</summary>
    Application,
}

public readonly record struct FrameRateReading(
    double Fps,
    double RefreshHz,
    FrameRateSource Source,
    string ProcessName);

/// <summary>
/// Frame rate of whatever is in the foreground.
///
/// The real, in-application rate is only observable through ETW: Windows raises a Present event
/// for every frame an application hands to the compositor, tagged with the process that presented
/// it. That is the same signal PresentMon uses. Opening a real-time ETW session needs elevation
/// (or membership in "Performance Log Users"), so when the session cannot be created this falls
/// back to the desktop composition rate, and <see cref="FrameRateReading.Source"/> says which one
/// the caller is looking at.
/// </summary>
public sealed class FrameRateService : IDisposable
{
    private const string SessionName = "WidgetsFrameRate";

    private const string DxgiProvider = "Microsoft-Windows-DXGI";

    /// <summary>DXGI's "Events" keyword, which is where Present lives.</summary>
    private const ulong DxgiEventsKeyword = 0x2;

    /// <summary>Presents older than this are dropped, so a rate always describes the recent past.</summary>
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(1);

    /// <summary>A process that has not presented for this long is forgotten.</summary>
    private static readonly TimeSpan Stale = TimeSpan.FromSeconds(5);

    private readonly Lock _gate = new();

    /// <summary>Timestamps of the recent presents made by each process.</summary>
    private readonly Dictionary<int, List<long>> _presents = new();

    private readonly Dictionary<int, string> _names = new();

    private TraceEventSession? _session;
    private Thread? _pump;
    private volatile bool _running;
    private double _compositionFps;

    /// <summary>Null until a session has been attempted; otherwise why the ETW path is unavailable.</summary>
    public string? UnavailableReason { get; private set; }

    public bool IsTracing => _running;

    public void Start()
    {
        lock (_gate)
        {
            if (_session is not null || _running)
            {
                return;
            }

            try
            {
                // A session left behind by a previous run would make this one fail with
                // "already exists" rather than start clean.
                TraceEventSession.GetActiveSession(SessionName)?.Stop();
            }
            catch (UnauthorizedAccessException)
            {
                // Enumerating sessions needs the same rights as creating one. Not worth logging —
                // the Start below reports the missing privilege properly.
            }
            catch (Exception ex)
            {
                Crash.Log(ex, "FrameRateService.StopStale");
            }

            try
            {
                _session = new TraceEventSession(SessionName) { StopOnDispose = true };

                // Keyword 0x2 is DXGI's "Events" — 0x1 is "Objects" and carries no Present at all.
                // Only DXGI is enabled: it covers D3D9/11/12, which is nearly every game, and is
                // low volume. DxgKrnl would also catch Vulkan and OpenGL, but its Present events
                // only come with the "Base" keyword, which is a firehose of every GPU packet —
                // far too much for something that runs all day in the background.
                _session.EnableProvider(DxgiProvider, TraceEventLevel.Verbose, DxgiEventsKeyword);

                _session.Source.AllEvents += OnEvent;

                _running = true;
                UnavailableReason = null;

                _pump = new Thread(Pump)
                {
                    IsBackground = true,
                    Name = "Widgets.FrameRate",
                };

                _pump.Start();
            }
            catch (UnauthorizedAccessException)
            {
                UnavailableReason = "管理者として実行するとアプリごとの実 FPS を計測できます";
                Dispose(disposing: true, keepReason: true);
            }
            catch (Exception ex)
            {
                Crash.Log(ex, "FrameRateService.Start");
                UnavailableReason = "ETW セッションを開始できませんでした";
                Dispose(disposing: true, keepReason: true);
            }
        }
    }

    private void Pump()
    {
        try
        {
            // Blocks until the session is stopped.
            _session?.Source.Process();
        }
        catch (Exception ex)
        {
            Crash.Log(ex, "FrameRateService.Pump");
        }
        finally
        {
            _running = false;
        }
    }

    private void OnEvent(TraceEvent data)
    {
        // Exactly one Present task per frame, counted on Start only — matching the whole task
        // family ("PresentHistory", …) or both Start and Stop would multiply every rate.
        if (data.Opcode != TraceEventOpcode.Start
            || !string.Equals(data.TaskName, "Present", StringComparison.Ordinal))
        {
            return;
        }

        var pid = data.ProcessID;
        if (pid <= 0)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();

        lock (_gate)
        {
            if (!_presents.TryGetValue(pid, out var times))
            {
                times = new List<long>(512);
                _presents[pid] = times;
            }

            times.Add(now);
        }
    }

    /// <summary>Reads the frame rate of the process that currently owns the foreground window.</summary>
    public FrameRateReading Sample()
    {
        var refreshHz = ReadRefreshHz();

        if (_running)
        {
            var pid = ForegroundProcessId();
            if (pid > 0)
            {
                var fps = Rate(pid);
                if (fps > 0)
                {
                    return new FrameRateReading(fps, refreshHz, FrameRateSource.Application, ProcessName(pid));
                }
            }
        }

        // Nothing is presenting (or ETW is unavailable): report what the desktop itself is doing.
        var composition = SampleComposition(refreshHz);
        return composition > 0
            ? new FrameRateReading(composition, refreshHz, FrameRateSource.Composition, string.Empty)
            : new FrameRateReading(0, refreshHz, FrameRateSource.None, string.Empty);
    }

    /// <summary>Presents per second for one process, over the last <see cref="Window"/>.</summary>
    private double Rate(int pid)
    {
        var now = Stopwatch.GetTimestamp();
        var cutoff = now - (long)(Window.TotalSeconds * Stopwatch.Frequency);
        var rate = 0.0;

        lock (_gate)
        {
            foreach (var key in _presents.Keys.ToList())
            {
                var times = _presents[key];
                times.RemoveAll(t => t < cutoff);

                if (times.Count == 0)
                {
                    // Nothing recent: drop the process so an idle machine keeps no state.
                    _presents.Remove(key);
                    _names.Remove(key);
                    continue;
                }

                if (key != pid || times.Count < 2)
                {
                    continue;
                }

                // Measured across the frames actually in the window rather than assuming a full
                // second, so the rate is right even just after a game starts presenting.
                var span = (times[^1] - times[0]) / (double)Stopwatch.Frequency;
                if (span > 0)
                {
                    rate = (times.Count - 1) / span;
                }
            }
        }

        return rate;
    }

    private string ProcessName(int pid)
    {
        lock (_gate)
        {
            if (_names.TryGetValue(pid, out var cached))
            {
                return cached;
            }
        }

        var name = string.Empty;
        try
        {
            using var process = Process.GetProcessById(pid);
            name = process.ProcessName;
        }
        catch (Exception)
        {
            // The process can exit between the present and this lookup; an empty label is fine.
        }

        lock (_gate)
        {
            _names[pid] = name;
        }

        return name;
    }

    // ---- Composition fallback --------------------------------------------------

    private const int FrameSamples = 4;

    /// <summary>
    /// Rate the compositor is presenting at, timed with DwmFlush (which blocks until the next
    /// frame is done). DwmGetCompositionTimingInfo would report it directly but fails on current
    /// Windows 11 builds with 0x88980090 for every hwnd, including NULL.
    /// </summary>
    private double SampleComposition(double refreshHz)
    {
        if (DwmIsCompositionEnabled(out var enabled) != 0 || !enabled)
        {
            return 0;
        }

        // The first flush only aligns to a frame boundary.
        if (DwmFlush() != 0)
        {
            return 0;
        }

        var start = Stopwatch.GetTimestamp();

        for (var i = 0; i < FrameSamples; i++)
        {
            if (DwmFlush() != 0)
            {
                return 0;
            }
        }

        var seconds = (Stopwatch.GetTimestamp() - start) / (double)Stopwatch.Frequency;
        if (seconds <= 0)
        {
            return _compositionFps;
        }

        var measured = FrameSamples / seconds;
        if (refreshHz > 0)
        {
            measured = Math.Min(measured, refreshHz);
        }

        _compositionFps = _compositionFps > 0 ? (_compositionFps * 0.6) + (measured * 0.4) : measured;
        return _compositionFps;
    }

    public static double ReadRefreshHz()
    {
        var hdc = GetDC(IntPtr.Zero);
        if (hdc == IntPtr.Zero)
        {
            return 0;
        }

        try
        {
            var hz = GetDeviceCaps(hdc, VREFRESH);

            // 0 and 1 both mean "hardware default" rather than a real rate.
            return hz > 1 ? hz : 0;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, hdc);
        }
    }

    private static int ForegroundProcessId()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
        {
            return 0;
        }

        _ = GetWindowThreadProcessId(hwnd, out var pid);
        return (int)pid;
    }

    public void Dispose() => Dispose(disposing: true, keepReason: false);

    private void Dispose(bool disposing, bool keepReason)
    {
        _running = false;

        try
        {
            _session?.Dispose();
        }
        catch (Exception ex)
        {
            Crash.Log(ex, "FrameRateService.Dispose");
        }

        _session = null;
        _pump = null;

        if (!keepReason)
        {
            UnavailableReason = null;
        }

        lock (_gate)
        {
            _presents.Clear();
            _names.Clear();
        }
    }

    private const int VREFRESH = 116;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern int GetDeviceCaps(IntPtr hdc, int nIndex);

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

    [DllImport("dwmapi.dll")]
    private static extern int DwmIsCompositionEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);
}
