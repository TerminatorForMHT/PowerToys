#include "pch.h"
#include "TrayIcon.h"
#include "resource.h"
#include "Settings.h"

bool TrayIcon::Create(HWND hwnd, HICON hIcon, const wchar_t* tip)
{
    m_nid.cbSize = sizeof(m_nid);
    m_nid.hWnd = hwnd;
    m_nid.uID = 1;
    m_nid.uFlags = NIF_ICON | NIF_MESSAGE | NIF_TIP;
    m_nid.uCallbackMessage = WM_TRAY_CALLBACK;
    m_nid.hIcon = hIcon;
    wcsncpy_s(m_nid.szTip, tip, _TRUNCATE);

    m_created = Shell_NotifyIconW(NIM_ADD, &m_nid) != FALSE;
    return m_created;
}

void TrayIcon::Remove()
{
    if (m_created)
    {
        Shell_NotifyIconW(NIM_DELETE, &m_nid);
        m_created = false;
    }
}

void TrayIcon::UpdateTooltip(const wchar_t* tip)
{
    if (!m_created)
        return;
    wcsncpy_s(m_nid.szTip, tip, _TRUNCATE);
    m_nid.uFlags = NIF_TIP;
    Shell_NotifyIconW(NIM_MODIFY, &m_nid);
}

UINT TrayIcon::ShowContextMenu(HWND hwnd)
{
    HMENU hMenu = LoadMenuW(GetModuleHandleW(nullptr), MAKEINTRESOURCEW(IDR_TRAY_MENU));
    if (!hMenu)
        return 0;

    HMENU hSub = GetSubMenu(hMenu, 0);
    if (hSub)
    {
        // Radio-check the active mode in the Mode submenu
        HMENU hModeMenu = GetSubMenu(hSub, 1);
        if (hModeMenu)
        {
            UINT modeId = IDM_MODE_OFF;
            switch (LightSwitchSettings::settings().scheduleMode)
            {
            case ScheduleMode::FixedHours:       modeId = IDM_MODE_FIXED; break;
            case ScheduleMode::SunsetToSunrise:  modeId = IDM_MODE_SUN; break;
            case ScheduleMode::FollowNightLight: modeId = IDM_MODE_NIGHTLIGHT; break;
            default:                             modeId = IDM_MODE_OFF; break;
            }
            CheckMenuRadioItem(hModeMenu, IDM_MODE_OFF, IDM_MODE_NIGHTLIGHT, modeId, MF_BYCOMMAND);
        }

        // Required so the menu dismisses correctly when clicking elsewhere
        SetForegroundWindow(hwnd);

        POINT pt;
        GetCursorPos(&pt);
        UINT cmd = TrackPopupMenu(hSub, TPM_RETURNCMD | TPM_NONOTIFY | TPM_RIGHTBUTTON,
                                  pt.x, pt.y, 0, hwnd, nullptr);
        DestroyMenu(hMenu);
        return cmd;
    }

    DestroyMenu(hMenu);
    return 0;
}
