using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

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
    TimeSpan Uptime);

/// <summary>
/// Polls system metrics once a second on a background timer. P/Invokes are declared privately
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

    private readonly Timer _timer;
    private readonly Stopwatch _netStopwatch = new();

    private long _prevIdle;
    private long _prevKernel;
    private long _prevUser;
    private bool _hasPrevCpu;

    private long _prevNetRecv;
    private long _prevNetSent;
    private bool _hasPrevNet;

    public string DriveLetter { get; set; } = "C";

    public SystemStats Current { get; private set; }

    public event EventHandler<SystemStats>? Updated;

    public SystemStatsService()
    {
        _netStopwatch.Start();
        Current = Sample();
        _timer = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    private void Tick()
    {
        try
        {
            var stats = Sample();
            Current = stats;
            AppServices.OnUi(() => Updated?.Invoke(this, stats));
        }
        catch (Exception ex)
        {
            Crash.Log(ex, "SystemStatsService.Tick");
        }
    }

    private SystemStats Sample()
    {
        var cpu = SampleCpu();
        var (ramPercent, ramUsed, ramTotal) = SampleRam();
        var (diskPercent, diskUsed, diskTotal) = SampleDisk();
        var (netDown, netUp) = SampleNetwork();
        var (batteryPercent, charging, hasBattery) = SampleBattery();
        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);

        return new SystemStats(
            cpu, ramPercent, ramUsed, ramTotal,
            diskPercent, diskUsed, diskTotal,
            netDown, netUp,
            batteryPercent, charging, hasBattery,
            uptime);
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

    private static (double Percent, double UsedGb, double TotalGb) SampleRam()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref status))
        {
            return (0, 0, 0);
        }

        const double bytesPerGb = 1024.0 * 1024 * 1024;
        var totalGb = status.ullTotalPhys / bytesPerGb;
        var usedGb = (status.ullTotalPhys - status.ullAvailPhys) / bytesPerGb;
        return (status.dwMemoryLoad, usedGb, totalGb);
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

            const double bytesPerGb = 1024.0 * 1024 * 1024;
            var totalGb = drive.TotalSize / bytesPerGb;
            var freeGb = drive.AvailableFreeSpace / bytesPerGb;
            var usedGb = totalGb - freeGb;
            var percent = totalGb > 0 ? usedGb / totalGb * 100.0 : 0;
            return (percent, usedGb, totalGb);
        }
        catch (Exception ex)
        {
            Crash.Log(ex, "SystemStatsService.SampleDisk");
            return (0, 0, 0);
        }
    }

    private (double DownKbps, double UpKbps) SampleNetwork()
    {
        long recv = 0;
        long sent = 0;

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            var stats = nic.GetIPStatistics();
            recv += stats.BytesReceived;
            sent += stats.BytesSent;
        }

        var elapsed = _netStopwatch.Elapsed.TotalSeconds;
        _netStopwatch.Restart();

        var down = 0.0;
        var up = 0.0;
        if (_hasPrevNet && elapsed > 0)
        {
            down = Math.Max(0, (recv - _prevNetRecv) * 8.0 / 1000.0 / elapsed);
            up = Math.Max(0, (sent - _prevNetSent) * 8.0 / 1000.0 / elapsed);
        }

        _prevNetRecv = recv;
        _prevNetSent = sent;
        _hasPrevNet = true;
        return (down, up);
    }

    private static (int Percent, bool Charging, bool HasBattery) SampleBattery()
    {
        if (!GetSystemPowerStatus(out var status))
        {
            return (0, false, false);
        }

        const byte noBattery = 128;
        const byte unknown = 255;
        var hasBattery = status.BatteryFlag != noBattery && status.BatteryFlag != unknown;
        var charging = (status.BatteryFlag & 0x08) != 0;
        var percent = status.BatteryLifePercent == unknown ? 0 : status.BatteryLifePercent;
        return (percent, charging, hasBattery);
    }

    public void Dispose() => _timer.Dispose();
}
