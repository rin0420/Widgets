using System.Text;

namespace Widgets.App;

/// <summary>
/// Best-effort crash log. Widgets run unattended on the desktop, so a swallowed exception
/// needs somewhere to surface; this writes to %LOCALAPPDATA%\Widgets\crash.log.
/// </summary>
public static class Crash
{
    private static readonly Lock Gate = new();

    public static string LogPath { get; } = Path.Combine(Paths.DataDirectory, "crash.log");

    public static void Log(Exception ex, string? context = null)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Paths.DataDirectory);

                var sb = new StringBuilder()
                    .AppendLine($"--- {DateTimeOffset.Now:O} ---");

                if (!string.IsNullOrEmpty(context))
                {
                    sb.AppendLine($"context: {context}");
                }

                sb.AppendLine(ex.ToString()).AppendLine();

                // Keep the log from growing without bound across long uptimes.
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 512 * 1024)
                {
                    File.Delete(LogPath);
                }

                File.AppendAllText(LogPath, sb.ToString());
            }
        }
        catch
        {
            // Logging must never throw.
        }
    }
}

/// <summary>Well-known locations for user data.</summary>
public static class Paths
{
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Widgets");

    public static string DocumentPath { get; } = Path.Combine(DataDirectory, "widgets.json");

    public static string PhotoCacheDirectory { get; } = Path.Combine(DataDirectory, "photos");

    public static void EnsureCreated() => Directory.CreateDirectory(DataDirectory);
}
