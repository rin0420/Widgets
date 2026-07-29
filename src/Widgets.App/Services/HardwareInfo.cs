using System.Globalization;
using Microsoft.Win32;

namespace Widgets.App.Services;

/// <summary>
/// Machine identity that never changes while the app runs, read once on first use. Every probe is
/// best effort: these values are cosmetic, and a static initializer that throws would take the
/// whole app down, so failures fall back to empty/zero.
/// </summary>
public static class HardwareInfo
{
    private const string CpuKeyPath = @"HARDWARE\DESCRIPTION\System\CentralProcessor\0";

    // The display adapter class GUID; adapters live in the numbered subkeys below it.
    private const string DisplayClassKeyPath =
        @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    private const double BytesPerGb = 1024.0 * 1024 * 1024;

    /// <summary>Marketing name of the CPU, e.g. "AMD Ryzen 9 7950X". Empty when unknown.</summary>
    public static string CpuName { get; }

    public static int LogicalCores { get; }

    /// <summary>Name of the adapter with the most dedicated memory. Empty when unknown.</summary>
    public static string GpuName { get; }

    public static double GpuMemoryTotalGb { get; }

    /// <summary>e.g. "Windows 11 26200".</summary>
    public static string OsCaption { get; }

    static HardwareInfo()
    {
        CpuName = ReadCpuName();
        LogicalCores = Math.Max(1, Environment.ProcessorCount);
        (GpuName, GpuMemoryTotalGb) = ReadGpu();
        OsCaption = ReadOsCaption();
    }

    private static string ReadCpuName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(CpuKeyPath);
            return (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? string.Empty;
        }
        catch (Exception ex)
        {
            Crash.Log(ex, "HardwareInfo.ReadCpuName");
            return string.Empty;
        }
    }

    private static (string Name, double MemoryGb) ReadGpu()
    {
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(DisplayClassKeyPath);
            if (root is null)
            {
                return (string.Empty, 0);
            }

            var name = string.Empty;
            ulong memory = 0;

            foreach (var subKeyName in root.GetSubKeyNames())
            {
                // Adapters are "0000", "0001", ...; siblings like "Configuration" are not adapters.
                if (subKeyName.Length != 4 || !subKeyName.All(char.IsAsciiDigit))
                {
                    continue;
                }

                using var adapter = root.OpenSubKey(subKeyName);
                if (adapter is null)
                {
                    continue;
                }

                var adapterMemory = ReadQword(adapter, "HardwareInformation.qwMemorySize");
                if (adapterMemory <= memory && name.Length > 0)
                {
                    continue;
                }

                name = adapter.GetValue("DriverDesc") as string ?? string.Empty;
                memory = adapterMemory;
            }

            return (name.Trim(), memory / BytesPerGb);
        }
        catch (Exception ex)
        {
            Crash.Log(ex, "HardwareInfo.ReadGpu");
            return (string.Empty, 0);
        }
    }

    /// <summary>Some drivers store the VRAM size as REG_BINARY instead of REG_QWORD.</summary>
    private static ulong ReadQword(RegistryKey key, string valueName) => key.GetValue(valueName) switch
    {
        long value => (ulong)value,
        int value => (uint)value,
        byte[] bytes when bytes.Length >= 8 => BitConverter.ToUInt64(bytes, 0),
        _ => 0,
    };

    private static string ReadOsCaption()
    {
        try
        {
            var build = Environment.OSVersion.Version.Build;
            var family = build >= 22000 ? "Windows 11" : "Windows 10";
            return $"{family} {build.ToString(CultureInfo.InvariantCulture)}";
        }
        catch (Exception ex)
        {
            Crash.Log(ex, "HardwareInfo.ReadOsCaption");
            return string.Empty;
        }
    }
}
