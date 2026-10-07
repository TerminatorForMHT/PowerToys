using Microsoft.Win32;

namespace LightSwitch.Services;

// Sets the desktop wallpaper per theme (light / dark), the same mechanism
// Auto Dark Mode uses: registry style + SystemParametersInfoW(SPI_SETDESKWALLPAPER)
// with SPIF_UPDATEINIFILE | SPIF_SENDWININICHANGE so the shell picks it up live.
public static class WallpaperService
{
    private const uint SPI_SETDESKWALLPAPER = 0x0014;
    private const uint SPIF_UPDATEINIFILE = 0x01;
    private const uint SPIF_SENDWININICHANGE = 0x02;

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SystemParametersInfoW(uint uiAction, uint uiParam, string pvParam, uint fWinIni);

    // Returns the current desktop wallpaper path, or empty string on failure.
    public static string GetCurrentWallpaper()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            return key?.GetValue("Wallpaper") as string ?? string.Empty;
        }
        catch (Exception e)
        {
            Logger.Error("[Wallpaper] Failed to read current wallpaper: " + e.Message);
            return string.Empty;
        }
    }

    // Sets the desktop wallpaper. style: 0=centered, 2=stretched, 6=fit, 10=fill (Windows default).
    public static bool SetWallpaper(string path, int style = 10)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            Logger.Warn($"[Wallpaper] Skipping: file not found: {path}");
            return false;
        }

        try
        {
            // 1. Persist the style under Control Panel\Desktop so Windows
            //    renders the image with the requested fit on next apply.
            using (var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop", writable: true))
            {
                if (key == null)
                    return false;
                key.SetValue("WallpaperStyle", style.ToString());
                key.SetValue("TileWallpaper", "0");
            }

            // 2. Broadcast the change. SPIF_UPDATEINIFILE persists it,
            //    SPIF_SENDWININICHANGE tells explorer/shell to repaint now.
            bool ok = SystemParametersInfoW(SPI_SETDESKWALLPAPER, 0, path, SPIF_UPDATEINIFILE | SPIF_SENDWININICHANGE);
            if (ok)
                Logger.Info($"[Wallpaper] Set wallpaper: {path}");
            else
                Logger.Error($"[Wallpaper] SystemParametersInfoW failed: {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
            return ok;
        }
        catch (Exception e)
        {
            Logger.Error("[Wallpaper] Exception: " + e.Message);
            return false;
        }
    }

    // Applies the wallpaper for the target theme if the user enabled switching
    // and picked an image for that theme. No-op otherwise.
    public static void ApplyForTheme(bool isLight, LightSwitchConfig cfg)
    {
        if (!cfg.ChangeWallpaper)
            return;

        var target = isLight ? cfg.LightWallpaper : cfg.DarkWallpaper;
        if (string.IsNullOrWhiteSpace(target))
            return;

        var current = GetCurrentWallpaper();
        if (string.Equals(current, target, StringComparison.OrdinalIgnoreCase))
            return; // already correct

        SetWallpaper(target);
    }
}
