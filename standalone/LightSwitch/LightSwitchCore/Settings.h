#pragma once

#include <windows.h>
#include <string>
#include <unordered_set>
#include <mutex>
#include <atomic>
#include <thread>
#include <chrono>
#include <filesystem>
#include "SettingsConstants.h"
#include "../vendor/nlohmann/json.hpp"

using json = nlohmann::json;

class SettingsObserver;

enum class ScheduleMode
{
    Off,
    FixedHours,
    SunsetToSunrise,
    FollowNightLight,
};

inline std::wstring ToString(ScheduleMode mode)
{
    switch (mode)
    {
    case ScheduleMode::FixedHours:      return L"FixedHours";
    case ScheduleMode::SunsetToSunrise: return L"SunsetToSunrise";
    case ScheduleMode::FollowNightLight:return L"FollowNightLight";
    default:                            return L"Off";
    }
}

inline ScheduleMode FromString(const std::wstring& str)
{
    if (str == L"SunsetToSunrise") return ScheduleMode::SunsetToSunrise;
    if (str == L"FixedHours")      return ScheduleMode::FixedHours;
    if (str == L"FollowNightLight")return ScheduleMode::FollowNightLight;
    return ScheduleMode::Off;
}

struct HotkeyConfig
{
    bool win = false;
    bool ctrl = false;
    bool alt = false;
    bool shift = false;
    unsigned int key = 0; // virtual-key code
};

struct LightSwitchConfig
{
    ScheduleMode scheduleMode = ScheduleMode::FixedHours;
    std::wstring latitude = L"0.0";
    std::wstring longitude = L"0.0";
    int lightTime = 8 * 60;
    int darkTime = 20 * 60;
    int sunrise_offset = 0;
    int sunset_offset = 0;
    bool changeSystem = false;
    bool changeApps = false;
    HotkeyConfig hotkey{ .win = true, .ctrl = true, .shift = true, .alt = false, .key = 'D' };
};

class LightSwitchSettings
{
public:
    static LightSwitchSettings& instance();
    static inline const LightSwitchConfig& settings() { return instance().m_settings; }

    void InitFileWatcher();
    static std::filesystem::path GetSettingsFilePath();
    static std::filesystem::path GetAppDataDir();

    void AddObserver(SettingsObserver& observer);
    void RemoveObserver(SettingsObserver& observer);

    void LoadSettings();
    void SaveSettings();
    // Replace current config with a new one, notify observers of changed fields.
    void UpdateSettings(const LightSwitchConfig& newConfig);

    HANDLE GetSettingsChangedEvent() const { return m_settingsChangedEvent; }

private:
    LightSwitchSettings();
    ~LightSwitchSettings();

    LightSwitchConfig m_settings;
    std::unordered_set<SettingsObserver*> m_observers;
    void NotifyObservers(SettingId id) const;

    HANDLE m_settingsChangedEvent = nullptr;
    mutable std::mutex m_settingsMutex;

    // Polling file watcher
    std::atomic_bool m_watcherRunning{ false };
    std::jthread m_watcherThread;
    std::filesystem::file_time_type m_lastWriteTime{};

    void RunFileWatcher(std::stop_token stop);
};

class SettingsObserver
{
public:
    SettingsObserver(std::unordered_set<SettingId> observedSettings) :
        m_observedSettings(std::move(observedSettings))
    {
        LightSwitchSettings::instance().AddObserver(*this);
    }

    virtual ~SettingsObserver()
    {
        LightSwitchSettings::instance().RemoveObserver(*this);
    }

    virtual void SettingsUpdate(SettingId) {}

    virtual bool WantsToBeNotified(SettingId type) const noexcept
    {
        return m_observedSettings.contains(type);
    }

protected:
    std::unordered_set<SettingId> m_observedSettings;
};
