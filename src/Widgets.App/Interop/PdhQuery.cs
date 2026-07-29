using System.Globalization;
using System.Runtime.InteropServices;

namespace Widgets.App.Interop;

/// <summary>
/// Thin wrapper over the PDH performance counter API. Counters are addressed by a caller-supplied
/// key so a counter that does not exist on this machine (GPU counters need a WDDM driver, for
/// instance) degrades to "unavailable" instead of throwing. Nothing here is thread-safe; the
/// owner is expected to poll from a single thread.
/// </summary>
internal sealed class PdhQuery : IDisposable
{
    private const uint ErrorSuccess = 0;
    private const uint PdhMoreData = 0x800007D2;

    // Per-item status returned inside PDH_FMT_COUNTERVALUE.
    private const uint PdhCstatusValidData = 0x00000000;
    private const uint PdhCstatusNewData = 0x00000001;

    private const uint PdhFmtDouble = 0x00000200;
    private const uint PdhFmtNoCap100 = 0x00008000;

    [StructLayout(LayoutKind.Sequential)]
    private struct PDH_FMT_COUNTERVALUE
    {
        public uint CStatus;
        public double doubleValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PDH_FMT_COUNTERVALUE_ITEM_W
    {
        public IntPtr szName;
        public PDH_FMT_COUNTERVALUE FmtValue;
    }

    [DllImport("pdh.dll", EntryPoint = "PdhOpenQueryW", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQuery(string? szDataSource, IntPtr dwUserData, out IntPtr phQuery);

    // English variant on purpose: counter names are localised, so "\PhysicalDisk(_Total)\% Disk Time"
    // does not resolve at all on a Japanese Windows through PdhAddCounterW.
    [DllImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounter(
        IntPtr hQuery, string szFullCounterPath, IntPtr dwUserData, out IntPtr phCounter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr hQuery);

    [DllImport("pdh.dll")]
    private static extern uint PdhGetFormattedCounterValue(
        IntPtr hCounter, uint dwFormat, out uint lpdwType, out PDH_FMT_COUNTERVALUE pValue);

    [DllImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArray(
        IntPtr hCounter, uint dwFormat, ref uint lpdwBufferSize, out uint lpdwItemCount, IntPtr ItemBuffer);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr hQuery);

    private sealed class Counter
    {
        public IntPtr Handle;
        public uint Format;
        public bool Wildcard;
        public bool HasData;
        public double Value;
        public readonly List<(string Name, double Value)> Instances = [];
    }

    private readonly Dictionary<string, Counter> _counters = new(StringComparer.Ordinal);
    private readonly HashSet<string> _logged = new(StringComparer.Ordinal);
    private IntPtr _query;
    private int _collected;

    public PdhQuery()
    {
        try
        {
            if (PdhOpenQuery(null, IntPtr.Zero, out var query) == ErrorSuccess)
            {
                _query = query;
            }
            else
            {
                LogOnce("open", "PdhOpenQueryW failed");
            }
        }
        catch (Exception ex)
        {
            Crash.Log(ex, "PdhQuery.ctor");
        }
    }

    public bool TryAddCounter(string key, string path, bool allowOver100 = false)
        => TryAdd(key, path, allowOver100, wildcard: false);

    /// <summary>Adds a counter whose instance is a wildcard, e.g. <c>\GPU Engine(*)\...</c>.</summary>
    public bool TryAddWildcard(string key, string path, bool allowOver100 = false)
        => TryAdd(key, path, allowOver100, wildcard: true);

    private bool TryAdd(string key, string path, bool allowOver100, bool wildcard)
    {
        if (_query == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var status = PdhAddEnglishCounter(_query, path, IntPtr.Zero, out var handle);
            if (status != ErrorSuccess)
            {
                LogOnce($"add:{key}", $"PdhAddEnglishCounterW({path}) failed");
                return false;
            }

            _counters[key] = new Counter
            {
                Handle = handle,
                Format = allowOver100 ? PdhFmtDouble | PdhFmtNoCap100 : PdhFmtDouble,
                Wildcard = wildcard,
            };
            return true;
        }
        catch (Exception ex)
        {
            Crash.Log(ex, "PdhQuery.TryAdd");
            return false;
        }
    }

    /// <summary>
    /// Takes one sample of every counter. Rate counters have no value until the second call, so the
    /// first pass is expected to leave everything unavailable.
    /// </summary>
    public void Collect()
    {
        if (_query == IntPtr.Zero)
        {
            return;
        }

        var status = PdhCollectQueryData(_query);
        if (status != ErrorSuccess)
        {
            LogOnce("collect", $"PdhCollectQueryData failed (0x{Hex(status)})");
            return;
        }

        _collected++;

        foreach (var (key, counter) in _counters)
        {
            if (counter.Wildcard)
            {
                ReadArray(key, counter);
            }
            else
            {
                ReadSingle(key, counter);
            }
        }
    }

    /// <summary>True when the counter path resolved on this machine.</summary>
    public bool IsAvailable(string key) => _counters.ContainsKey(key);

    public bool TryGetValue(string key, out double value)
    {
        if (_counters.TryGetValue(key, out var counter) && !counter.Wildcard && counter.HasData)
        {
            value = counter.Value;
            return true;
        }

        value = 0;
        return false;
    }

    public bool TryGetSum(string key, out double sum, Func<string, bool>? instanceFilter = null)
    {
        sum = 0;
        if (!_counters.TryGetValue(key, out var counter) || !counter.Wildcard || !counter.HasData)
        {
            return false;
        }

        foreach (var (name, value) in counter.Instances)
        {
            if (instanceFilter is null || instanceFilter(name))
            {
                sum += value;
            }
        }

        return true;
    }

    private void ReadSingle(string key, Counter counter)
    {
        var status = PdhGetFormattedCounterValue(counter.Handle, counter.Format, out _, out var value);
        if (status != ErrorSuccess || value.CStatus is not (PdhCstatusValidData or PdhCstatusNewData))
        {
            ReportReadFailure(key, status);
            return;
        }

        counter.Value = value.doubleValue;
        counter.HasData = true;
    }

    private void ReadArray(string key, Counter counter)
    {
        // Size is only known by asking for it first; the instance set can change between the two
        // calls, in which case this sample is skipped and the previous values stay in place.
        uint bufferSize = 0;
        var status = PdhGetFormattedCounterArray(
            counter.Handle, counter.Format, ref bufferSize, out _, IntPtr.Zero);

        if (status == ErrorSuccess)
        {
            // Resolved, but nothing is instanced right now.
            counter.Instances.Clear();
            counter.HasData = true;
            return;
        }

        if (status != PdhMoreData || bufferSize == 0)
        {
            ReportReadFailure(key, status);
            return;
        }

        var buffer = Marshal.AllocHGlobal((int)bufferSize);
        try
        {
            status = PdhGetFormattedCounterArray(
                counter.Handle, counter.Format, ref bufferSize, out var itemCount, buffer);
            if (status != ErrorSuccess)
            {
                ReportReadFailure(key, status);
                return;
            }

            var itemSize = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM_W>();
            counter.Instances.Clear();

            for (var i = 0; i < itemCount; i++)
            {
                var item = Marshal.PtrToStructure<PDH_FMT_COUNTERVALUE_ITEM_W>(IntPtr.Add(buffer, i * itemSize));
                if (item.FmtValue.CStatus is not (PdhCstatusValidData or PdhCstatusNewData))
                {
                    continue;
                }

                var name = item.szName == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUni(item.szName);
                counter.Instances.Add((name ?? string.Empty, item.FmtValue.doubleValue));
            }

            counter.HasData = true;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private void ReportReadFailure(string key, uint status)
    {
        // The first collect never has enough history for a rate counter, so that failure is normal.
        if (_collected > 1)
        {
            LogOnce($"read:{key}", $"reading '{key}' failed (0x{Hex(status)})");
        }
    }

    private void LogOnce(string key, string message)
    {
        // Callers poll once a second; without this the crash log would fill up with the same line.
        if (_logged.Add(key))
        {
            Crash.Log(new InvalidOperationException(message), "PdhQuery");
        }
    }

    private static string Hex(uint status) => status.ToString("X8", CultureInfo.InvariantCulture);

    public void Dispose()
    {
        if (_query == IntPtr.Zero)
        {
            return;
        }

        // Closing the query releases the counter handles with it.
        var query = _query;
        _query = IntPtr.Zero;
        _counters.Clear();

        try
        {
            PdhCloseQuery(query);
        }
        catch (Exception ex)
        {
            Crash.Log(ex, "PdhQuery.Dispose");
        }
    }
}
