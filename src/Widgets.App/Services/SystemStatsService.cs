using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Widgets.App.Interop;

namespace Widgets.App.Services;

public sealed record SystemStats(
    double CpuPercent,
    double RamPercent,
    double RamUsedGb,
    double RamTotalGb,
    double DiskPercent,
    double DiskUsedGb,
    double DiskTotalGb,
    double NetDownKbps,
    double NetUpKbps,
    int BatteryPercent,
    bool IsCharging,
    bool HasBattery,
    TimeSpan Uptime)
{
    /// <summary>Per logical core usage, 0-100. Empty until two samples have been taken.</summary>
    public IReadOnlyList<double> CoreUsage { get; init; } = [];

    public double CpuMhz { get; init; }

    public double CpuMaxMhz { get; init; }

    public int ProcessCount { get; init; }

    public int ThreadCount { get; init; }

    public double CommittedGb { get; init; }

    public double CommitLimitGb { get; init; }

    public bool HasGpu { get; init; }

    /// <summary>Sum of the 3D engines, 0-100.</summary>
    public double GpuPercent { get; init; }

    public double GpuMemUsedGb { get; init; }

    public double GpuMemTotalGb { get; init; }

    public double DiskReadBytesPerSec { get; init; }

    public double DiskWriteBytesPerSec { get; init; }

    public double DiskActivePercent { get; init; }

    public double NetDownPeakKbps { get; init; }

    public double NetUpPeakKbps { get; init; }

    public double NetTotalDownGb { get; init; }

    public double NetTotalUpGb { get; init; }

    public string NetAdapterName { get; init; } = string.Empty;

    /// <summary>Estimated minutes left on battery, or -1 when Windows cannot tell.</summary>
    public int BatteryMinutesRemaining { get; init; } = -1;
}

/// <summary>
/// Polls system metrics once a second while something is listening. P/Invokes are declared privately
/// here (rather than in the shared Interop file) so this can be built independently.
/// </summary>
public sealed class SystemStatsService : IDisposable
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(
        out System.Runtime.InteropServices.ComTypes.FILETIME lpIdleTime,
        out System.Runtime.InteropServices.ComTypes.FILETIME lpKernelTime,
        out System.Runtime.InteropServices.ComTypes.FILETIME lpUserTime);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);

    private const int SystemProcessorPerformanceInformation = 8;

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION
    {
        public long IdleTime;
        public long KernelTime;    // includes IdleTime
        public long UserTime;
        public long DpcTime;
        public long InterruptTime;
        public uint InterruptCount;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(
        int SystemInformationClass, IntPtr SystemInformation, int SystemInformationLength, out int ReturnLength);

    private const int ProcessorInformation = 11;

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESSOR_POWER_INFORMATION
    {
        public uint Number;
        public uint MaxMhz;
        public uint CurrentMhz;
        public uint MhzLimit;
        public uint MaxIdleState;
        public uint CurrentIdleState;
    }

    [DllImport("powrprof.dll")]
    private static extern uint CallNtPowerInformation(
        int InformationLevel, IntPtr lpInputBuffer, uint nInputBufferSize, IntPtr lpOutputBuffer,
        uint nOutputBufferSize);

    private const double BytesPerGb = 1024.0 * 1024 * 1024;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private const string GpuEngineKey = "gpuEngine";
    private const string GpuMemoryKey = "gpuMemory";
    private const string DiskReadKey = "diskRead";
    private const string DiskWriteKey = "diskWrite";
    private const string DiskTimeKey = "diskTime";
    private const string ProcessesKey = "processes";
    private const string ThreadsKey = "threads";

    private readonly Lock _gate = new();
    private readonly HashSet<string> _logged = new(StringComparer.Ordinal);
    private readonly Stopwatch _netStopwatch = new();

    private EventHandler<SystemStats>? _updated;
    private Timer? _timer;
    private PdhQuery? _pdh;

    private long _prevIdle;
    private long _prevKernel;
    private long _prevUser;
    private bool _hasPrevCpu;

    private double[] _coreUsage = [];
    private SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION[] _prevCoreTimes = [];
    private bool _hasPrevCoreTimes;

    private long _prevNetRecv;
    private long _prevNetSent;
    private bool _hasPrevNet;
    private double _netDownPeakKbps;
    private double _netUpPeakKbps;
    private long _netTotalDownBytes;
    private long _netTotalUpBytes;

    public string DriveLetter { get; set; } = "C";

    public SystemStats Current { get; private set; }

    /// <summary>
    /// Fired on the UI thread once a second. The poll timer only runs while at least one handler is
    /// attached — the PDH counters behind these stats are not free enough to sample for nobody.
    /// </summary>
    public event EventHandler<SystemStats>? Updated
    {
        add
        {
            lock (_gate)
            {
                _updated += value;
                _timer ??= new Timer(_ => Tick(), null, PollInterval, PollInterval);
            }
        }
        remove
        {
            lock (_gate)
            {
                _updated -= value;
                if (_updated is null)
                {
                    StopPolling();
                }
            }
        }
    }

    public SystemStatsService()
    {
        _netStopwatch.Start();

        // PDH is deliberately left alone here: constructing the service must not be able to fail,
        // and the counters are only worth opening once someone subscribes.
        Current = Sample();
    }

    /// <summary>Takes a sample immediately, for callers that want a value before subscribing.</summary>
    public void Refresh()
    {
        lock (_gate)
        {
            try
            {
                EnsurePdh();
                Current = Sample();
            }
            catch (Exception ex)
            {
                LogOnce("Refresh", ex);
            }
        }
    }

    private void Tick()
    {
        SystemStats stats;

        lock (_gate)
        {
            // The last subscriber may have left while this callback was already queued.
            if (_timer is null)
            {
                return;
            }

            try
            {
                EnsurePdh();
                stats = Sample();
                Current = stats;
            }
            catch (Exception ex)
            {
                LogOnce("Tick", ex);
                return;
            }
        }

        AppServices.OnUi(() => _updated?.Invoke(this, stats));
    }

    private void StopPolling()
    {
        _timer?.Dispose();
        _timer = null;

        // Rate counters and byte deltas are all measured against the previous sample, so drop every
        // baseline: resuming later must not report a whole idle period as one enormous spike.
        _pdh?.Dispose();
        _pdh = null;
        _hasPrevCpu = false;
        _hasPrevCoreTimes = false;
        _hasPrevNet = false;
    }

    private void EnsurePdh()
    {
        if (_pdh is not null)
        {
            return;
        }

        var pdh = new PdhQuery();
        pdh.TryAddWildcard(GpuEngineKey, @"\GPU Engine(*)\Utilization Percentage");
        pdh.TryAddWildcard(GpuMemoryKey, @"\GPU Process Memory(*)\Dedicated Usage");
        pdh.TryAddCounter(DiskReadKey, @"\PhysicalDisk(_Total)\Disk Read Bytes/sec");
        pdh.TryAddCounter(DiskWriteKey, @"\PhysicalDisk(_Total)\Disk Write Bytes/sec");
        pdh.TryAddCounter(DiskTimeKey, @"\PhysicalDisk(_Total)\% Disk Time", allowOver100: true);
        pdh.TryAddCounter(ProcessesKey, @"\System\Processes");
        pdh.TryAddCounter(ThreadsKey, @"\System\Threads");
        _pdh = pdh;
    }

    private SystemStats Sample()
    {
        var cpu = SampleCpu();
        var cores = SampleCoreUsage();
        var (cpuMhz, cpuMaxMhz) = SampleCpuClock();
        var ram = SampleRam();
        var (diskPercent, diskUsed, diskTotal) = SampleDisk();
        var net = SampleNetwork();
        var battery = SampleBattery();
        var pdh = SamplePdh();
        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);

        return new SystemStats(
            cpu, ram.Percent, ram.UsedGb, ram.TotalGb,
            diskPercent, diskUsed, diskTotal,
            net.DownKbps, net.UpKbps,
            battery.Percent, battery.Charging, battery.HasBattery,
            uptime)
        {
            CoreUsage = cores,
            CpuMhz = cpuMhz,
            CpuMaxMhz = cpuMaxMhz,
            ProcessCount = pdh.ProcessCount,
            ThreadCount = pdh.ThreadCount,
            CommittedGb = ram.CommittedGb,
            CommitLimitGb = ram.CommitLimitGb,
            HasGpu = pdh.HasGpu,
            GpuPercent = pdh.GpuPercent,
            GpuMemUsedGb = pdh.GpuMemUsedGb,
            GpuMemTotalGb = HardwareInfo.GpuMemoryTotalGb,
            DiskReadBytesPerSec = pdh.DiskReadBytesPerSec,
            DiskWriteBytesPerSec = pdh.DiskWriteBytesPerSec,
            DiskActivePercent = pdh.DiskActivePercent,
            NetDownPeakKbps = _netDownPeakKbps,
            NetUpPeakKbps = _netUpPeakKbps,
            NetTotalDownGb = _netTotalDownBytes / BytesPerGb,
            NetTotalUpGb = _netTotalUpBytes / BytesPerGb,
            NetAdapterName = net.AdapterName,
            BatteryMinutesRemaining = battery.MinutesRemaining,
        };
    }

    private double SampleCpu()
    {
        if (!GetSystemTimes(out var idleFt, out var kernelFt, out var userFt))
        {
            return 0;
        }

        var idle = ToLong(idleFt);
        var kernel = ToLong(kernelFt);
        var user = ToLong(userFt);

        var result = 0.0;
        if (_hasPrevCpu)
        {
            var idleDelta = idle - _prevIdle;
            var kernelDelta = kernel - _prevKernel;
            var userDelta = user - _prevUser;
            var total = kernelDelta + userDelta;
            if (total > 0)
            {
                result = Math.Clamp((total - idleDelta) * 100.0 / total, 0, 100);
            }
        }

        _prevIdle = idle;
        _prevKernel = kernel;
        _prevUser = user;
        _hasPrevCpu = true;
        return result;
    }

    private static long ToLong(System.Runtime.InteropServices.ComTypes.FILETIME ft)
        => ((long)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;

    private IReadOnlyList<double> SampleCoreUsage()
    {
        var size = Marshal.SizeOf<SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION>();
        var count = Math.Max(1, Environment.ProcessorCount);
        var buffer = Marshal.AllocHGlobal(size * count);

        try
        {
            if (NtQuerySystemInformation(
                    SystemProcessorPerformanceInformation, buffer, size * count, out var returned) != 0)
            {
                return _coreUsage[..];
            }

            // A machine with several processor groups only reports the caller's group, so trust the
            // returned length rather than ProcessorCount and show whatever came back.
            var reported = Math.Min(count, returned / size);
            if (reported <= 0)
            {
                return [];
            }

            if (_coreUsage.Length != reported)
            {
                _coreUsage = new double[reported];
                _prevCoreTimes = new SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION[reported];
                _hasPrevCoreTimes = false;
            }

            var hadPrevious = _hasPrevCoreTimes;
            for (var i = 0; i < reported; i++)
            {
                var info = Marshal.PtrToStructure<SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION>(
                    IntPtr.Add(buffer, i * size));

                if (hadPrevious)
                {
                    var previous = _prevCoreTimes[i];
                    var idleDelta = info.IdleTime - previous.IdleTime;
                    var total = info.KernelTime - previous.KernelTime + (info.UserTime - previous.UserTime);

                    // An empty window keeps the previous reading; zeroing it would flicker the bars.
                    if (total > 0)
                    {
                        _coreUsage[i] = Math.Clamp((1.0 - (double)idleDelta / total) * 100.0, 0, 100);
                    }
                }

                _prevCoreTimes[i] = info;
            }

            _hasPrevCoreTimes = true;
            return hadPrevious ? _coreUsage[..] : [];
        }
        catch (Exception ex)
        {
            LogOnce("SampleCoreUsage", ex);
            return [];
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private (double Mhz, double MaxMhz) SampleCpuClock()
    {
        var size = Marshal.SizeOf<PROCESSOR_POWER_INFORMATION>();
        var count = Math.Max(1, Environment.ProcessorCount);
        var buffer = Marshal.AllocHGlobal(size * count);

        try
        {
            if (CallNtPowerInformation(
                    ProcessorInformation, IntPtr.Zero, 0, buffer, (uint)(size * count)) != 0)
            {
                return (0, 0);
            }

            var sum = 0.0;
            var maxMhz = 0.0;
            for (var i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<PROCESSOR_POWER_INFORMATION>(IntPtr.Add(buffer, i * size));
                sum += info.CurrentMhz;
                if (i == 0)
                {
                    maxMhz = info.MaxMhz;
                }
            }

            return (sum / count, maxMhz);
        }
        catch (Exception ex)
        {
            LogOnce("SampleCpuClock", ex);
            return (0, 0);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static (double Percent, double UsedGb, double TotalGb, double CommittedGb, double CommitLimitGb) SampleRam()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref status))
        {
            return (0, 0, 0, 0, 0);
        }

        var totalGb = status.ullTotalPhys / BytesPerGb;
        var usedGb = (status.ullTotalPhys - status.ullAvailPhys) / BytesPerGb;

        // ullTotalPageFile is the commit limit (RAM + page file), not the page file on its own.
        var commitLimitGb = status.ullTotalPageFile / BytesPerGb;
        var committedGb = (status.ullTotalPageFile - status.ullAvailPageFile) / BytesPerGb;
        return (status.dwMemoryLoad, usedGb, totalGb, committedGb, commitLimitGb);
    }

    private (double Percent, double UsedGb, double TotalGb) SampleDisk()
    {
        try
        {
            var drive = new DriveInfo(DriveLetter);
            if (!drive.IsReady)
            {
                return (0, 0, 0);
            }

            var totalGb = drive.TotalSize / BytesPerGb;
            var freeGb = drive.AvailableFreeSpace / BytesPerGb;
            var usedGb = totalGb - freeGb;
            var percent = totalGb > 0 ? usedGb / totalGb * 100.0 : 0;
            return (percent, usedGb, totalGb);
        }
        catch (Exception ex)
        {
            LogOnce("SampleDisk", ex);
            return (0, 0, 0);
        }
    }

    private (double DownKbps, double UpKbps, string AdapterName) SampleNetwork()
    {
        try
        {
            long recv = 0;
            long sent = 0;
            long busiest = -1;
            var adapterName = string.Empty;

            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                var stats = nic.GetIPStatistics();
                recv += stats.BytesReceived;
                sent += stats.BytesSent;

                if (stats.BytesReceived > busiest)
                {
                    busiest = stats.BytesReceived;
                    adapterName = nic.Name;
                }
            }

            var elapsed = _netStopwatch.Elapsed.TotalSeconds;
            _netStopwatch.Restart();

            var down = 0.0;
            var up = 0.0;
            if (_hasPrevNet && elapsed > 0)
            {
                // These counters wrap at 32 bits and reset when an adapter goes down, so a negative
                // delta is noise: report nothing for this window and leave the totals alone.
                var recvDelta = recv - _prevNetRecv;
                if (recvDelta > 0)
                {
                    down = recvDelta * 8.0 / 1000.0 / elapsed;
                    _netTotalDownBytes += recvDelta;
                }

                var sentDelta = sent - _prevNetSent;
                if (sentDelta > 0)
                {
                    up = sentDelta * 8.0 / 1000.0 / elapsed;
                    _netTotalUpBytes += sentDelta;
                }

                _netDownPeakKbps = Math.Max(_netDownPeakKbps, down);
                _netUpPeakKbps = Math.Max(_netUpPeakKbps, up);
            }

            _prevNetRecv = recv;
            _prevNetSent = sent;
            _hasPrevNet = true;
            return (down, up, adapterName);
        }
        catch (Exception ex)
        {
            LogOnce("SampleNetwork", ex);
            return (0, 0, string.Empty);
        }
    }

    private static (int Percent, bool Charging, bool HasBattery, int MinutesRemaining) SampleBattery()
    {
        if (!GetSystemPowerStatus(out var status))
        {
            return (0, false, false, -1);
        }

        const byte noBattery = 128;
        const byte unknown = 255;
        var hasBattery = status.BatteryFlag != noBattery && status.BatteryFlag != unknown;
        var charging = (status.BatteryFlag & 0x08) != 0;
        var percent = status.BatteryLifePercent == unknown ? 0 : status.BatteryLifePercent;
        var minutes = status.BatteryLifeTime < 0 ? -1 : status.BatteryLifeTime / 60;
        return (percent, charging, hasBattery, minutes);
    }

    private readonly record struct PdhSample(
        bool HasGpu,
        double GpuPercent,
        double GpuMemUsedGb,
        double DiskReadBytesPerSec,
        double DiskWriteBytesPerSec,
        double DiskActivePercent,
        int ProcessCount,
        int ThreadCount);

    private PdhSample SamplePdh()
    {
        if (_pdh is null)
        {
            return default;
        }

        try
        {
            _pdh.Collect();

            // Every engine of every process shows up here; only the 3D engines make up what task
            // manager calls GPU utilisation.
            var hasUsage = _pdh.TryGetSum(
                GpuEngineKey,
                out var gpuPercent,
                static name => name.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase));

            _pdh.TryGetSum(GpuMemoryKey, out var gpuMemoryBytes);
            var hasGpu = _pdh.IsAvailable(GpuEngineKey) && (HardwareInfo.GpuName.Length > 0 || hasUsage);

            _pdh.TryGetValue(DiskReadKey, out var read);
            _pdh.TryGetValue(DiskWriteKey, out var write);
            _pdh.TryGetValue(DiskTimeKey, out var diskTime);
            _pdh.TryGetValue(ProcessesKey, out var processes);
            _pdh.TryGetValue(ThreadsKey, out var threads);

            return new PdhSample(
                hasGpu,
                Math.Clamp(gpuPercent, 0, 100),
                gpuMemoryBytes / BytesPerGb,
                read,
                write,
                Math.Clamp(diskTime, 0, 100),
                (int)processes,
                (int)threads);
        }
        catch (Exception ex)
        {
            LogOnce("SamplePdh", ex);
            return default;
        }
    }

    /// <summary>
    /// Records a failure the first time it happens. Sampling runs every second, so an unconditional
    /// log here would bury the crash log in identical lines.
    /// </summary>
    private void LogOnce(string context, Exception ex)
    {
        if (_logged.Add(context))
        {
            Crash.Log(ex, $"SystemStatsService.{context}");
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            StopPolling();
        }
    }
}
