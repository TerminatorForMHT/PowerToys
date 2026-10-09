using Microsoft.Win32;
using Windows.ApplicationModel;

namespace LightSwitch.Services;

// Manages "run at Windows startup".
// Packaged (MSIX) builds MUST use the windows.startupTask manifest extension:
// direct HKCU\...\Run writes from a packaged app land in its private registry
// hive and never actually launch anything at login. Unpackaged runs fall back
// to the classic Run key.
public static class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "LightSwitch";
    private const string StartupTaskId = "LightSwitchStartup";

    // True when running with an identity package (installed MSIX).
    private static bool IsPackaged()
    {
        try
        {
            _ = Package.Current;
            return true;
        }
        catch
        {
            return false;
        }
    }

    // Enables or disables login autostart according to the setting.
    public static async Task ApplyAsync(bool enable)
    {
        if (IsPackaged())
            await ApplyPackagedAsync(enable);
        else
            ApplyUnpackaged(enable);
    }

    private static async Task ApplyPackagedAsync(bool enable)
    {
        try
        {
            var task = await StartupTask.GetAsync(StartupTaskId);

            if (enable)
            {
                switch (task.State)
                {
                    case StartupTaskState.Enabled:
                    case StartupTaskState.EnabledByPolicy:
                        Logger.Info("[Startup] StartupTask already enabled.");
                        break;
                    case StartupTaskState.DisabledByUser:
                        // The user turned it off in Task Manager; apps may not
                        // re-enable that programmatically.
                        Logger.Warn("[Startup] StartupTask disabled by user in Task Manager; not re-enabling.");
                        break;
                    default: // Disabled / DisabledByPolicy
                        var result = await task.RequestEnableAsync();
                        Logger.Info($"[Startup] StartupTask enable requested, result={result}.");
                        break;
                }
            }
            else
            {
                if (task.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy)
                {
                    task.Disable();
                    Logger.Info("[Startup] StartupTask disabled.");
                }
            }
        }
        catch (Exception e)
        {
            Logger.Error("[Startup] StartupTask exception: " + e.Message);
        }
    }

    private static void ApplyUnpackaged(bool enable)
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
}
