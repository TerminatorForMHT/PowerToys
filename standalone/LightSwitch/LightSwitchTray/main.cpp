#include "pch.h"
#include "resource.h"
#include "TrayIcon.h"
#include "Worker.h"
#include "SettingsDialog.h"
#include "Settings.h"
#include "Logger.h"
#include <commctrl.h>

namespace
{
    constexpr wchar_t kWindowClassName[] = L"LightSwitchTrayWindow";
    constexpr wchar_t kAppName[] = L"LightSwitch";
    constexpr int kHotkeyId = 1;

    HINSTANCE g_hInstance = nullptr;
    TrayIcon g_trayIcon;
    Worker g_worker;
    HWND g_hwnd = nullptr;

    UINT HotkeyModifiers(const HotkeyConfig& hk)
    {
        UINT mods = MOD_NOREPEAT;
        if (hk.win)   mods |= MOD_WIN;
        if (hk.ctrl)  mods |= MOD_CONTROL;
        if (hk.alt)   mods |= MOD_ALT;
        if (hk.shift) mods |= MOD_SHIFT;
        return mods;
    }

    void RegisterToggleHotkey()
    {
        UnregisterHotKey(g_hwnd, kHotkeyId);

        const auto& hk = LightSwitchSettings::settings().hotkey;
        if (hk.key == 0)
            return;

        if (!RegisterHotKey(g_hwnd, kHotkeyId, HotkeyModifiers(hk), hk.key))
        {
            Logger::Warn(L"[Main] Failed to register toggle hotkey.");
        }
    }

    void SetMode(ScheduleMode mode)
    {
        LightSwitchConfig cfg = LightSwitchSettings::settings();
        cfg.scheduleMode = mode;
        LightSwitchSettings::instance().UpdateSettings(cfg);
        SetEvent(LightSwitchSettings::instance().GetSettingsChangedEvent());
    }

    void HandleTrayCommand(UINT cmd)
    {
        switch (cmd)
        {
        case IDM_TOGGLE:
            g_worker.ToggleThemeNow();
            break;
        case IDM_MODE_OFF:
            SetMode(ScheduleMode::Off);
            break;
        case IDM_MODE_FIXED:
            SetMode(ScheduleMode::FixedHours);
            break;
        case IDM_MODE_SUN:
            SetMode(ScheduleMode::SunsetToSunrise);
            break;
        case IDM_MODE_NIGHTLIGHT:
            SetMode(ScheduleMode::FollowNightLight);
            break;
        case IDM_SETTINGS:
            if (ShowSettingsDialog(g_hwnd))
            {
                // Apply immediately and pick up a possible hotkey change
                SetEvent(LightSwitchSettings::instance().GetSettingsChangedEvent());
                RegisterToggleHotkey();
            }
            break;
        case IDM_EXIT:
            DestroyWindow(g_hwnd);
            break;
        }
    }

    LRESULT CALLBACK WndProc(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam)
    {
        switch (msg)
        {
        case WM_CREATE:
            return 0;

        case TrayIcon::WM_TRAY_CALLBACK:
            switch (LOWORD(lParam))
            {
            case WM_CONTEXTMENU:
            case WM_RBUTTONUP:
            {
                UINT cmd = g_trayIcon.ShowContextMenu(hwnd);
                if (cmd != 0)
                    HandleTrayCommand(cmd);
                return 0;
            }
            case WM_LBUTTONDBLCLK:
                g_worker.ToggleThemeNow();
                return 0;
            }
            return 0;

        case WM_HOTKEY:
            if (wParam == kHotkeyId)
            {
                Logger::Info(L"[Main] Toggle hotkey pressed.");
                g_worker.ToggleThemeNow();
            }
            return 0;

        case WM_DESTROY:
            UnregisterHotKey(hwnd, kHotkeyId);
            g_trayIcon.Remove();
            g_worker.Stop();
            PostQuitMessage(0);
            return 0;
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }
}

int APIENTRY wWinMain(HINSTANCE hInstance, HINSTANCE, PWSTR, int)
{
    g_hInstance = hInstance;

    // Single instance guard
    HANDLE mutex = CreateMutexW(nullptr, TRUE, L"Local\\LightSwitchStandalone_{d8dc2f29-8c94-4ca1-8c5f-3e2b1e3c4f5a}");
    if (GetLastError() == ERROR_ALREADY_EXISTS)
    {
        MessageBoxW(nullptr, L"LightSwitch is already running.", kAppName, MB_ICONINFORMATION);
        if (mutex)
            CloseHandle(mutex);
        return 0;
    }

    Logger::Init(kAppName);
    Logger::Info(L"[Main] LightSwitch starting.");

    INITCOMMONCONTROLSEX icc{ sizeof(icc), ICC_HOTKEY_CLASS };
    InitCommonControlsEx(&icc);

    HICON hIcon = static_cast<HICON>(LoadImageW(hInstance, MAKEINTRESOURCEW(IDI_LIGHTSWITCH),
                                                IMAGE_ICON, 32, 32, LR_DEFAULTCOLOR));
    if (!hIcon)
        hIcon = LoadIconW(nullptr, IDI_APPLICATION);

    WNDCLASSEXW wc{};
    wc.cbSize = sizeof(wc);
    wc.lpfnWndProc = WndProc;
    wc.hInstance = hInstance;
    wc.lpszClassName = kWindowClassName;
    wc.hIcon = hIcon;
    if (!RegisterClassExW(&wc))
    {
        Logger::Error(L"[Main] Failed to register window class.");
        if (mutex)
            CloseHandle(mutex);
        return 1;
    }

    g_hwnd = CreateWindowExW(0, kWindowClassName, kAppName, 0,
                             0, 0, 0, 0, HWND_MESSAGE, nullptr, hInstance, nullptr);
    if (!g_hwnd)
    {
        Logger::Error(L"[Main] Failed to create message window.");
        if (mutex)
            CloseHandle(mutex);
        return 1;
    }

    // Ensure the settings file exists so first-run users have something to edit
    LightSwitchSettings::instance().SaveSettings();

    if (!g_trayIcon.Create(g_hwnd, hIcon, L"LightSwitch - Theme Scheduler"))
    {
        Logger::Error(L"[Main] Failed to create tray icon.");
        DestroyWindow(g_hwnd);
        if (mutex)
            CloseHandle(mutex);
        return 1;
    }

    if (!g_worker.Start())
    {
        Logger::Error(L"[Main] Failed to start worker thread.");
        DestroyWindow(g_hwnd);
        if (mutex)
            CloseHandle(mutex);
        return 1;
    }

    RegisterToggleHotkey();

    MSG msg;
    while (GetMessageW(&msg, nullptr, 0, 0))
    {
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }

    Logger::Info(L"[Main] LightSwitch exiting.");
    Logger::Shutdown();

    if (mutex)
        CloseHandle(mutex);
    return static_cast<int>(msg.wParam);
}
