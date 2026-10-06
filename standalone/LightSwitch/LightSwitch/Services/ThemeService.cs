using LightSwitch.Native;
using Microsoft.Win32;

namespace LightSwitch.Services;

// Controls switching the Windows system/app themes via the Personalize registry key
// plus a broadcast so running apps pick the change up immediately.
internal static class ThemeService
{
    public const string PersonalizationRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private static void BroadcastThemeChange(bool includeThemeChanged)
    {
        NativeMethods.SendMessageTimeout(NativeMethods.HWND_BROADCAST, NativeMethods.WM_SETTINGCHANGE,
            UIntPtr.Zero, "ImmersiveColorSet", NativeMethods.SMTO_ABORTIFHUNG, 5000, out _);

        if (includeThemeChanged)
        {
            NativeMethods.SendMessageTimeout(NativeMethods.HWND_BROADCAST, NativeMethods.WM_THEMECHANGED,
                UIntPtr.Zero, IntPtr.Zero, NativeMethods.SMTO_ABORTIFHUNG, 5000, out _);
        }
    }

    private static void ResetColorPrevalence()
    {
        using var key = Registry.CurrentUser.OpenSubKey(PersonalizationRegistryPath, writable: true);
        if (key == null)
            return;

        key.SetValue("ColorPrevalence", 0, RegistryValueKind.DWord); // back to default value
        BroadcastThemeChange(includeThemeChanged: true);

        NativeMethods.SendMessageTimeout(NativeMethods.HWND_BROADCAST, NativeMethods.WM_DWMCOLORIZATIONCOLORCHANGED,
            UIntPtr.Zero, IntPtr.Zero, NativeMethods.SMTO_ABORTIFHUNG, 5000, out _);
    }

    public static void SetAppsTheme(bool isLight)
    {
        using var key = Registry.CurrentUser.OpenSubKey(PersonalizationRegistryPath, writable: true);
        if (key == null)
            return;

        key.SetValue("AppsUseLightTheme", isLight ? 1 : 0, RegistryValueKind.DWord);
        BroadcastThemeChange(includeThemeChanged: true);
    }

    public static void SetSystemTheme(bool isLight)
    {
        using var key = Registry.CurrentUser.OpenSubKey(PersonalizationRegistryPath, writable: true);
        if (key == null)
            return;

        key.SetValue("SystemUsesLightTheme", isLight ? 1 : 0, RegistryValueKind.DWord);

        if (isLight) // if we are changing to light mode
        {
            ResetColorPrevalence();
            Logger.Info("[ThemeService] Reset ColorPrevalence to default when switching to light mode.");
        }

        BroadcastThemeChange(includeThemeChanged: true);
    }

    // true = light, false = dark
    public static bool GetCurrentSystemTheme() => GetThemeValue("SystemUsesLightTheme");

    public static bool GetCurrentAppsTheme() => GetThemeValue("AppsUseLightTheme");

    private static bool GetThemeValue(string valueName)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizationRegistryPath);
            if (key?.GetValue(valueName) is int value)
                return value == 1;
        }
        catch
        {
            // fall through to default
        }

        return true; // default = light
    }
}
