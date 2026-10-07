using Microsoft.Win32;

namespace LightSwitch.Services;

// Manages the "Run at Windows startup" registry entry under
// HKCU\Software\Microsoft\Windows\CurrentVersion\Run (per-user, no admin needed).
public static class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "LightSwitch";

    // Registers the app to launch at login, or removes the entry when disabled.
    public static void Apply(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key == null)
            {
                Logger.Error("[Startup] Failed to open Run registry key.");
                return;
            }

            if (enable)
            {
                // For a packaged (MSIX) app the exe lives under Program Files\WindowsApps.
                // Environment.ProcessPath gives the full path to the running exe.
                var exePath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exePath))
                {
                    Logger.Error("[Startup] Could not determine exe path.");
                    return;
                }
                key.SetValue(AppName, $"\"{exePath}\"");
                Logger.Info("[Startup] Registered to run at login: " + exePath);
            }
            else
            {
                key.DeleteValue(AppName, throwOnMissingValue: false);
                Logger.Info("[Startup] Removed from login startup.");
            }
        }
        catch (Exception e)
        {
            Logger.Error("[Startup] Exception: " + e.Message);
        }
    }

    // Checks whether the app is currently registered to run at login.
    public static bool IsRegistered()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(AppName) != null;
        }
        catch
        {
            return false;
        }
    }
}
