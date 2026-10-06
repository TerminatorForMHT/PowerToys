#include "pch.h"
#include "SettingsDialog.h"
#include "resource.h"
#include "Settings.h"
#include <commctrl.h>
#include <string>

namespace
{
    std::wstring MinutesToTimeString(int minutes)
    {
        minutes = ((minutes % 1440) + 1440) % 1440;
        wchar_t buf[8];
        swprintf_s(buf, L"%02d:%02d", minutes / 60, minutes % 60);
        return buf;
    }

    // Parses "HH:MM" into minutes since midnight. Returns false on invalid input.
    bool TimeStringToMinutes(const std::wstring& text, int& outMinutes)
    {
        int h = 0, m = 0;
        if (swscanf_s(text.c_str(), L"%d:%d", &h, &m) != 2)
            return false;
        if (h < 0 || h > 23 || m < 0 || m > 59)
            return false;
        outMinutes = h * 60 + m;
        return true;
    }

    std::wstring GetEditText(HWND dlg, int controlId)
    {
        wchar_t buf[64] = {};
        GetDlgItemTextW(dlg, controlId, buf, _countof(buf));
        return buf;
    }

    void PopulateControls(HWND dlg, const LightSwitchConfig& cfg)
    {
        HWND combo = GetDlgItem(dlg, IDC_MODE_COMBO);
        SendMessageW(combo, CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(L"Off"));
        SendMessageW(combo, CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(L"Fixed Hours"));
        SendMessageW(combo, CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(L"Sunset to Sunrise"));
        SendMessageW(combo, CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(L"Follow Night Light"));

        int modeIndex = 0;
        switch (cfg.scheduleMode)
        {
        case ScheduleMode::FixedHours:       modeIndex = 1; break;
        case ScheduleMode::SunsetToSunrise:  modeIndex = 2; break;
        case ScheduleMode::FollowNightLight: modeIndex = 3; break;
        default:                             modeIndex = 0; break;
        }
        SendMessageW(combo, CB_SETCURSEL, modeIndex, 0);

        SetDlgItemTextW(dlg, IDC_LIGHT_TIME, MinutesToTimeString(cfg.lightTime).c_str());
        SetDlgItemTextW(dlg, IDC_DARK_TIME, MinutesToTimeString(cfg.darkTime).c_str());
        SetDlgItemInt(dlg, IDC_SUNRISE_OFFSET, static_cast<UINT>(cfg.sunrise_offset), TRUE);
        SetDlgItemInt(dlg, IDC_SUNSET_OFFSET, static_cast<UINT>(cfg.sunset_offset), TRUE);
        SetDlgItemTextW(dlg, IDC_LATITUDE, cfg.latitude.c_str());
        SetDlgItemTextW(dlg, IDC_LONGITUDE, cfg.longitude.c_str());

        CheckDlgButton(dlg, IDC_CHANGE_SYSTEM, cfg.changeSystem ? BST_CHECKED : BST_UNCHECKED);
        CheckDlgButton(dlg, IDC_CHANGE_APPS, cfg.changeApps ? BST_CHECKED : BST_UNCHECKED);

        WORD modifiers = 0;
        if (cfg.hotkey.alt)   modifiers |= HOTKEYF_ALT;
        if (cfg.hotkey.ctrl)  modifiers |= HOTKEYF_CONTROL;
        if (cfg.hotkey.shift) modifiers |= HOTKEYF_SHIFT;
        SendMessageW(GetDlgItem(dlg, IDC_HOTKEY), HKM_SETHOTKEY, MAKEWORD(cfg.hotkey.key, modifiers), 0);
    }

    bool ReadControls(HWND dlg, LightSwitchConfig& out)
    {
        LightSwitchConfig cfg = LightSwitchSettings::settings(); // start from current (keeps hotkey.win)

        int sel = static_cast<int>(SendMessageW(GetDlgItem(dlg, IDC_MODE_COMBO), CB_GETCURSEL, 0, 0));
        switch (sel)
        {
        case 1:  cfg.scheduleMode = ScheduleMode::FixedHours; break;
        case 2:  cfg.scheduleMode = ScheduleMode::SunsetToSunrise; break;
        case 3:  cfg.scheduleMode = ScheduleMode::FollowNightLight; break;
        default: cfg.scheduleMode = ScheduleMode::Off; break;
        }

        if (!TimeStringToMinutes(GetEditText(dlg, IDC_LIGHT_TIME), cfg.lightTime))
        {
            MessageBoxW(dlg, L"Light time must be in HH:MM format (00:00 - 23:59).", L"LightSwitch", MB_ICONWARNING);
            return false;
        }
        if (!TimeStringToMinutes(GetEditText(dlg, IDC_DARK_TIME), cfg.darkTime))
        {
            MessageBoxW(dlg, L"Dark time must be in HH:MM format (00:00 - 23:59).", L"LightSwitch", MB_ICONWARNING);
            return false;
        }

        BOOL translated = FALSE;
        UINT sunriseOffset = GetDlgItemInt(dlg, IDC_SUNRISE_OFFSET, &translated, TRUE);
        cfg.sunrise_offset = translated ? static_cast<int>(sunriseOffset) : 0;
        UINT sunsetOffset = GetDlgItemInt(dlg, IDC_SUNSET_OFFSET, &translated, TRUE);
        cfg.sunset_offset = translated ? static_cast<int>(sunsetOffset) : 0;

        cfg.latitude = GetEditText(dlg, IDC_LATITUDE);
        cfg.longitude = GetEditText(dlg, IDC_LONGITUDE);

        cfg.changeSystem = IsDlgButtonChecked(dlg, IDC_CHANGE_SYSTEM) == BST_CHECKED;
        cfg.changeApps = IsDlgButtonChecked(dlg, IDC_CHANGE_APPS) == BST_CHECKED;

        DWORD hk = static_cast<DWORD>(SendMessageW(GetDlgItem(dlg, IDC_HOTKEY), HKM_GETHOTKEY, 0, 0));
        BYTE vk = LOBYTE(hk);
        BYTE mods = HIBYTE(hk);
        if (vk != 0)
        {
            cfg.hotkey.key = vk;
            cfg.hotkey.alt = (mods & HOTKEYF_ALT) != 0;
            cfg.hotkey.ctrl = (mods & HOTKEYF_CONTROL) != 0;
            cfg.hotkey.shift = (mods & HOTKEYF_SHIFT) != 0;
            // cfg.hotkey.win preserved from current settings (hotkey control can't capture Win)
        }

        out = cfg;
        return true;
    }

    INT_PTR CALLBACK SettingsDialogProc(HWND dlg, UINT msg, WPARAM wParam, LPARAM)
    {
        switch (msg)
        {
        case WM_INITDIALOG:
            PopulateControls(dlg, LightSwitchSettings::settings());
            return TRUE;

        case WM_COMMAND:
            switch (LOWORD(wParam))
            {
            case IDOK:
            {
                LightSwitchConfig cfg;
                if (ReadControls(dlg, cfg))
                {
                    LightSwitchSettings::instance().UpdateSettings(cfg);
                    EndDialog(dlg, IDOK);
                }
                return TRUE;
            }
            case IDCANCEL:
                EndDialog(dlg, IDCANCEL);
                return TRUE;
            }
            break;
        }
        return FALSE;
    }
}

bool ShowSettingsDialog(HWND parent)
{
    INT_PTR result = DialogBoxParamW(GetModuleHandleW(nullptr),
                                     MAKEINTRESOURCEW(IDD_SETTINGS_DIALOG),
                                     parent,
                                     SettingsDialogProc,
                                     0);
    return result == IDOK;
}
