using Microsoft.Win32;

namespace Widgets.App.Services;

/// <summary>Launch-at-login toggle backed by the classic per-user HKCU Run key.</summary>
public sealed class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Widgets";

    public bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
                return key?.GetValue(ValueName) is string;
            }
            catch (Exception ex)
            {
                Crash.Log(ex, "StartupService.IsEnabled.get");
                return false;
            }
        }
        set
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                    ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);

                if (value)
                {
                    Write(key);
                }
                else
                {
                    key.DeleteValue(ValueName, throwOnMissingValue: false);
                }
            }
            catch (Exception ex)
            {
                Crash.Log(ex, "StartupService.IsEnabled.set");
            }
        }
    }

    /// <summary>
    /// Rewrites the logon entry if it is stale — either pointing at a path the app has since moved
    /// away from, or missing the startup argument that older builds did not pass.
    /// </summary>
    public void RepairRegistration()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key?.GetValue(ValueName) is not string current)
            {
                return;
            }

            if (!string.Equals(current, Command(), StringComparison.OrdinalIgnoreCase))
            {
                Write(key);
            }
        }
        catch (Exception ex)
        {
            Crash.Log(ex, "StartupService.RepairRegistration");
        }
    }

    private static void Write(RegistryKey key)
    {
        var command = Command();
        if (command.Length > 0)
        {
            key.SetValue(ValueName, command);
        }
    }

    private static string Command()
    {
        var exePath = Environment.ProcessPath;
        return string.IsNullOrEmpty(exePath) ? string.Empty : $"\"{exePath}\" {App.StartupArgument}";
    }
}
