using System.Runtime;
using System.Runtime.InteropServices;

namespace Widgets.App.Common;

/// <summary>
/// Hands memory back to the OS when the manager window is put away.
///
/// The widgets themselves are tiny; the manager window is not. It carries a NavigationView, the
/// gallery's live preview surfaces and the editor, and closing it only hides it — so all of that
/// stays resident for the rest of the session. Collecting and then trimming the working set turns
/// that into memory the OS can reuse, faulted back in only if the window is opened again.
/// </summary>
internal static class MemoryTrim
{
    /// <summary>
    /// Passing -1 for both bounds tells Windows to trim the working set as far as it can. Nothing
    /// is lost — the pages fault back in on next use.
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr minimum, IntPtr maximum);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    public static void Release()
    {
        try
        {
            // Compacting matters here: the wallpaper bitmaps are large-object-heap sized, and
            // without it the freed space stays reserved by the process.
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);

            SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1);
        }
        catch (Exception ex)
        {
            Crash.Log(ex, "MemoryTrim.Release");
        }
    }
}
