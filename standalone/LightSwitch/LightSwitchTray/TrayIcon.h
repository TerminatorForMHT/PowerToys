#pragma once

#include <windows.h>
#include <shellapi.h>
#include <string>

// Wraps a system tray icon (Shell_NotifyIcon) bound to a hidden message window.
class TrayIcon
{
public:
    static constexpr UINT WM_TRAY_CALLBACK = WM_APP + 1;

    bool Create(HWND hwnd, HICON hIcon, const wchar_t* tip);
    void Remove();
    void UpdateTooltip(const wchar_t* tip);

    // Shows the tray context menu at the cursor position and returns the
    // selected command id (0 if cancelled). Mode items are radio-checked
    // according to the current settings mode.
    UINT ShowContextMenu(HWND hwnd);

private:
    NOTIFYICONDATAW m_nid{};
    bool m_created = false;
};
