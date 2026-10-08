using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace LightSwitch.Services;

// Sets the desktop wallpaper per theme (light / dark).
// Uses the IDesktopWallpaper COM API (Windows 8+) so each monitor can get
// its own image — the same mechanism Auto Dark Mode uses. Falls back to
// the registry-style SystemParametersInfoW path if COM is unavailable.
public static class WallpaperService
{
    private const uint SPI_SETDESKWALLPAPER = 0x0014;
    private const uint SPIF_UPDATEINIFILE = 0x01;
    private const uint SPIF_SENDWININICHANGE = 0x02;

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfoW(uint uiAction, uint uiParam, string pvParam, uint fWinIni);

    #region IDesktopWallpaper COM

    [ComImport]
    [Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDesktopWallpaper
    {
        // vtable order matters — all methods must be declared in order.
        void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorID, [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);
        [return: MarshalAs(UnmanagedType.LPWStr)]
        string GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorID);
        [return: MarshalAs(UnmanagedType.LPWStr)]
        string GetMonitorDevicePathAt(uint monitorIndex);
        [return: MarshalAs(UnmanagedType.U4)]
        uint GetMonitorDevicePathCount();
        void GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitorID, out WinRect rect);
        void SetBackgroundColor([MarshalAs(UnmanagedType.U4)] uint color);
        [return: MarshalAs(UnmanagedType.U4)]
        uint GetBackgroundColor();
        void SetPosition(DesktopWallpaperPosition position);
        [return: MarshalAs(UnmanagedType.I4)]
        DesktopWallpaperPosition GetPosition();
        void SetSlideshow([MarshalAs(UnmanagedType.Interface)] object items);
        [return: MarshalAs(UnmanagedType.Interface)]
        object GetSlideshow();
        void SetSlideshowOptions(DesktopWallpaperSlideshowDirection direction, uint slideshowTick);
        void GetSlideshowOptions(out DesktopWallpaperSlideshowDirection direction, out uint slideshowTick);
        void AdvanceSlideshow([MarshalAs(UnmanagedType.LPWStr)] string? monitorID, DesktopWallpaperSlideshowDirection direction);
        DesktopWallpaperStatus GetStatus();
        void Enable([MarshalAs(UnmanagedType.Bool)] bool enable);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    public enum DesktopWallpaperPosition
    {
        Center = 0,
        Tile = 1,
        Stretch = 2,
        Fit = 3,
        Fill = 4,
        Span = 5,
    }

    public enum DesktopWallpaperSlideshowDirection
    {
        Forward = 0,
        Backward = 1,
    }

    [Flags]
    public enum DesktopWallpaperStatus : uint
    {
        None = 0,
        Slideshow = 1,
        DisabledByRemoteSession = 2,
        DisabledByPolicy = 4,
        InTransition = 8,
    }

    [ComImport]
    [Guid("C2CF3110-460E-4FC1-B9D0-8A1C0C9CC4BD")]
    private class DesktopWallpaperClass
    {
    }

    #endregion

    // Returns the current (primary monitor) desktop wallpaper path, or empty on failure.
    public static string GetCurrentWallpaper()
    {
        try
        {
            var wallpaper = (IDesktopWallpaper)new DesktopWallpaperClass();
            return wallpaper.GetWallpaper(null);
        }
        catch (Exception e)
        {
            Logger.Error("[Wallpaper] IDesktopWallpaper failed: " + e.Message);
        }

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

    // Number of monitors as reported by the shell.
    public static int GetMonitorCount()
    {
        try
        {
            var wallpaper = (IDesktopWallpaper)new DesktopWallpaperClass();
            return checked((int)wallpaper.GetMonitorDevicePathCount());
        }
        catch
        {
            return 1;
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
            var wallpaper = (IDesktopWallpaper)new DesktopWallpaperClass();
            wallpaper.SetPosition(style switch
            {
                0 => DesktopWallpaperPosition.Center,
                1 => DesktopWallpaperPosition.Tile,
                2 => DesktopWallpaperPosition.Stretch,
                6 => DesktopWallpaperPosition.Fit,
                5 => DesktopWallpaperPosition.Span,
                _ => DesktopWallpaperPosition.Fill,
            });
            wallpaper.SetWallpaper(null, path); // null monitor = all monitors
            Logger.Info($"[Wallpaper] Set wallpaper on all monitors: {path}");
            return true;
        }
        catch (Exception e)
        {
            Logger.Error("[Wallpaper] IDesktopWallpaper SetWallpaper failed: " + e.Message);
            return SetWallpaperLegacy(path, style);
        }
    }

    private static bool SetWallpaperLegacy(string path, int style)
    {
        try
        {
            using (var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop", writable: true))
            {
                if (key == null)
                    return false;
                key.SetValue("WallpaperStyle", style.ToString());
                key.SetValue("TileWallpaper", "0");
            }

            bool ok = SystemParametersInfoW(SPI_SETDESKWALLPAPER, 0, path, SPIF_UPDATEINIFILE | SPIF_SENDWININICHANGE);
            if (ok)
                Logger.Info($"[Wallpaper] Set wallpaper (legacy): {path}");
            else
                Logger.Error($"[Wallpaper] SystemParametersInfoW failed: {Marshal.GetLastWin32Error()}");
            return ok;
        }
        catch (Exception e)
        {
            Logger.Error("[Wallpaper] Exception: " + e.Message);
            return false;
        }
    }

    // Applies the wallpaper for the target theme. Monitor 1 uses Light/DarkWallpaper;
    // monitors 2/3 use their own fields, falling back to monitor 1's image when empty.
    public static void ApplyForTheme(bool isLight, LightSwitchConfig cfg)
    {
        if (!cfg.ChangeWallpaper)
            return;

        string primary = isLight ? cfg.LightWallpaper : cfg.DarkWallpaper;
        if (string.IsNullOrWhiteSpace(primary))
            return;

        var targets = new[] { primary,
            isLight ? cfg.LightWallpaper2 : cfg.DarkWallpaper2,
            isLight ? cfg.LightWallpaper3 : cfg.DarkWallpaper3 };

        try
        {
            var wallpaper = (IDesktopWallpaper)new DesktopWallpaperClass();
            uint count = wallpaper.GetMonitorDevicePathCount();
            bool anyChanged = false;

            for (uint i = 0; i < count && i < 3; i++)
            {
                var target = string.IsNullOrWhiteSpace(targets[i]) ? primary : targets[i];
                if (!File.Exists(target))
                    continue;

                string monitorId = wallpaper.GetMonitorDevicePathAt(i);
                string current = SafeGetWallpaper(wallpaper, monitorId);
                if (!string.Equals(current, target, StringComparison.OrdinalIgnoreCase))
                {
                    wallpaper.SetWallpaper(monitorId, target);
                    anyChanged = true;
                }
            }

            if (anyChanged)
                Logger.Info($"[Wallpaper] Applied {(isLight ? "light" : "dark")} wallpaper to {count} monitor(s).");
        }
        catch (Exception e)
        {
            Logger.Error("[Wallpaper] Per-monitor apply failed: " + e.Message);
            SetWallpaper(primary);
        }
    }

    private static string SafeGetWallpaper(IDesktopWallpaper wallpaper, string monitorId)
    {
        try
        {
            return wallpaper.GetWallpaper(monitorId);
        }
        catch
        {
            return string.Empty;
        }
    }
}
