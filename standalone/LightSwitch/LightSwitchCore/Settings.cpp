#include "pch.h"
#include "Settings.h"
#include "Logger.h"
#include <shlobj.h>

LightSwitchSettings& LightSwitchSettings::instance()
{
    static LightSwitchSettings inst;
    return inst;
}

std::filesystem::path LightSwitchSettings::GetAppDataDir()
{
    wchar_t* localAppData = nullptr;
    if (SUCCEEDED(SHGetKnownFolderPath(FOLDERID_LocalAppData, 0, nullptr, &localAppData)))
    {
        std::filesystem::path p = std::filesystem::path(localAppData) / L"LightSwitch";
        CoTaskMemFree(localAppData);
        return p;
    }
    return std::filesystem::path(L".");
}

std::filesystem::path LightSwitchSettings::GetSettingsFilePath()
{
    return GetAppDataDir() / L"settings.json";
}

LightSwitchSettings::LightSwitchSettings()
{
    m_settingsChangedEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    LoadSettings();
}

LightSwitchSettings::~LightSwitchSettings()
{
    m_watcherRunning = false;
    if (m_watcherThread.joinable())
        m_watcherThread.join();
    if (m_settingsChangedEvent)
        CloseHandle(m_settingsChangedEvent);
}

void LightSwitchSettings::AddObserver(SettingsObserver& observer)
{
    std::lock_guard<std::mutex> lock(m_settingsMutex);
    m_observers.insert(&observer);
}

void LightSwitchSettings::RemoveObserver(SettingsObserver& observer)
{
    std::lock_guard<std::mutex> lock(m_settingsMutex);
    m_observers.erase(&observer);
}

void LightSwitchSettings::NotifyObservers(SettingId id) const
{
    for (auto observer : m_observers)
    {
        if (observer->WantsToBeNotified(id))
            observer->SettingsUpdate(id);
    }
}

void LightSwitchSettings::LoadSettings()
{
    std::lock_guard<std::mutex> guard(m_settingsMutex);

    auto path = GetSettingsFilePath();
    std::error_code ec;
    if (!std::filesystem::exists(path, ec))
    {
        // Use defaults; file will be created on first SaveSettings
        return;
    }

    try
    {
        std::ifstream f(path);
        if (!f.is_open())
            return;
        json j;
        f >> j;

        auto readString = [&](const char* key, std::wstring& target) {
            if (j.contains(key) && j[key].is_string())
            {
                std::string s = j[key].get<std::string>();
                target = std::wstring(s.begin(), s.end());
            }
        };

        auto readInt = [&](const char* key, int& target) {
            if (j.contains(key) && j[key].is_number_integer())
                target = j[key].get<int>();
        };

        auto readBool = [&](const char* key, bool& target) {
            if (j.contains(key) && j[key].is_boolean())
                target = j[key].get<bool>();
        };

        if (j.contains("scheduleMode") && j["scheduleMode"].is_string())
        {
            std::string s = j["scheduleMode"].get<std::string>();
            std::wstring ws(s.begin(), s.end());
            auto newMode = FromString(ws);
            if (m_settings.scheduleMode != newMode)
            {
                m_settings.scheduleMode = newMode;
                NotifyObservers(SettingId::ScheduleMode);
            }
        }

        readString("latitude", m_settings.latitude);
        readString("longitude", m_settings.longitude);
        readInt("lightTime", m_settings.lightTime);
        readInt("darkTime", m_settings.darkTime);
        readInt("sunrise_offset", m_settings.sunrise_offset);
        readInt("sunset_offset", m_settings.sunset_offset);
        readBool("changeSystem", m_settings.changeSystem);
        readBool("changeApps", m_settings.changeApps);

        if (j.contains("hotkey") && j["hotkey"].is_object())
        {
            auto& hk = j["hotkey"];
            if (hk.contains("win") && hk["win"].is_boolean()) m_settings.hotkey.win = hk["win"];
            if (hk.contains("ctrl") && hk["ctrl"].is_boolean()) m_settings.hotkey.ctrl = hk["ctrl"];
            if (hk.contains("alt") && hk["alt"].is_boolean()) m_settings.hotkey.alt = hk["alt"];
            if (hk.contains("shift") && hk["shift"].is_boolean()) m_settings.hotkey.shift = hk["shift"];
            if (hk.contains("key") && hk["key"].is_number_integer()) m_settings.hotkey.key = hk["key"];
        }
    }
    catch (const std::exception& e)
    {
        Logger::Error(L"[Settings] Failed to load settings: " + std::wstring(e.what(), e.what() + strlen(e.what())));
    }
}

void LightSwitchSettings::UpdateSettings(const LightSwitchConfig& newConfig)
{
    {
        std::lock_guard<std::mutex> guard(m_settingsMutex);

        if (m_settings.scheduleMode != newConfig.scheduleMode)
        {
            m_settings.scheduleMode = newConfig.scheduleMode;
            NotifyObservers(SettingId::ScheduleMode);
        }
        if (m_settings.latitude != newConfig.latitude)
        {
            m_settings.latitude = newConfig.latitude;
            NotifyObservers(SettingId::Latitude);
        }
        if (m_settings.longitude != newConfig.longitude)
        {
            m_settings.longitude = newConfig.longitude;
            NotifyObservers(SettingId::Longitude);
        }
        if (m_settings.lightTime != newConfig.lightTime)
        {
            m_settings.lightTime = newConfig.lightTime;
            NotifyObservers(SettingId::LightTime);
        }
        if (m_settings.darkTime != newConfig.darkTime)
        {
            m_settings.darkTime = newConfig.darkTime;
            NotifyObservers(SettingId::DarkTime);
        }
        if (m_settings.sunrise_offset != newConfig.sunrise_offset)
        {
            m_settings.sunrise_offset = newConfig.sunrise_offset;
            NotifyObservers(SettingId::Sunrise_Offset);
        }
        if (m_settings.sunset_offset != newConfig.sunset_offset)
        {
            m_settings.sunset_offset = newConfig.sunset_offset;
            NotifyObservers(SettingId::Sunset_Offset);
        }
        if (m_settings.changeSystem != newConfig.changeSystem)
        {
            m_settings.changeSystem = newConfig.changeSystem;
            NotifyObservers(SettingId::ChangeSystem);
        }
        if (m_settings.changeApps != newConfig.changeApps)
        {
            m_settings.changeApps = newConfig.changeApps;
            NotifyObservers(SettingId::ChangeApps);
        }
        if (m_settings.hotkey.win != newConfig.hotkey.win || m_settings.hotkey.ctrl != newConfig.hotkey.ctrl ||
            m_settings.hotkey.alt != newConfig.hotkey.alt || m_settings.hotkey.shift != newConfig.hotkey.shift ||
            m_settings.hotkey.key != newConfig.hotkey.key)
        {
            m_settings.hotkey = newConfig.hotkey;
            NotifyObservers(SettingId::Hotkey);
        }
    }
    SaveSettings();
}

void LightSwitchSettings::SaveSettings()
{
    std::lock_guard<std::mutex> guard(m_settingsMutex);

    try
    {
        auto dir = GetAppDataDir();
        std::error_code ec;
        std::filesystem::create_directories(dir, ec);

        json j;
        {
            std::string s(ToString(m_settings.scheduleMode).begin(), ToString(m_settings.scheduleMode).end());
            j["scheduleMode"] = s;
        }
        j["latitude"] = std::string(m_settings.latitude.begin(), m_settings.latitude.end());
        j["longitude"] = std::string(m_settings.longitude.begin(), m_settings.longitude.end());
        j["lightTime"] = m_settings.lightTime;
        j["darkTime"] = m_settings.darkTime;
        j["sunrise_offset"] = m_settings.sunrise_offset;
        j["sunset_offset"] = m_settings.sunset_offset;
        j["changeSystem"] = m_settings.changeSystem;
        j["changeApps"] = m_settings.changeApps;

        json hk;
        hk["win"] = m_settings.hotkey.win;
        hk["ctrl"] = m_settings.hotkey.ctrl;
        hk["alt"] = m_settings.hotkey.alt;
        hk["shift"] = m_settings.hotkey.shift;
        hk["key"] = m_settings.hotkey.key;
        j["hotkey"] = hk;

        std::ofstream f(GetSettingsFilePath());
        f << j.dump(4);
    }
    catch (const std::exception& e)
    {
        Logger::Error(L"[Settings] Failed to save settings: " + std::wstring(e.what(), e.what() + strlen(e.what())));
    }
}

void LightSwitchSettings::InitFileWatcher()
{
    if (m_watcherRunning)
        return;

    m_watcherRunning = true;
    m_watcherThread = std::jthread([this](std::stop_token stop) { RunFileWatcher(stop); });
}

void LightSwitchSettings::RunFileWatcher(std::stop_token stop)
{
    auto path = GetSettingsFilePath();
    std::error_code ec;
    if (std::filesystem::exists(path, ec))
        m_lastWriteTime = std::filesystem::last_write_time(path, ec);

    while (!stop.stop_requested())
    {
        std::this_thread::sleep_for(std::chrono::seconds(2));
        if (stop.stop_requested())
            break;

        if (!std::filesystem::exists(path, ec))
            continue;

        auto currentWriteTime = std::filesystem::last_write_time(path, ec);
        if (currentWriteTime != m_lastWriteTime)
        {
            m_lastWriteTime = currentWriteTime;

            // Debounce: wait 1 second after last change
            std::this_thread::sleep_for(std::chrono::seconds(1));
            if (stop.stop_requested())
                break;

            try
            {
                LoadSettings();
                SetEvent(m_settingsChangedEvent);
                Logger::Info(L"[Settings] File change detected and settings reloaded.");
            }
            catch (const std::exception& e)
            {
                Logger::Error(L"[Settings] Exception during reload: " + std::wstring(e.what(), e.what() + strlen(e.what())));
            }
        }
    }
}
