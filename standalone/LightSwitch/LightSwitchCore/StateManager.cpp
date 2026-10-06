#include "pch.h"
#include "StateManager.h"
#include "Logger.h"
#include "LightSwitchUtils.h"
#include "ThemeScheduler.h"
#include "ThemeHelper.h"

void ApplyTheme(bool shouldBeLight);

// Constructor
LightSwitchStateManager::LightSwitchStateManager()
{
    Logger::Info(L"[StateManager] Initialized");
}

// Called when settings.json changes
void LightSwitchStateManager::OnSettingsChanged()
{
    std::lock_guard<std::mutex> lock(_stateMutex);

    // If manual override was active, clear it so new settings take effect
    if (_state.isManualOverride)
    {
        _state.isManualOverride = false;
    }

    EvaluateAndApplyIfNeeded();
}

// Called once per minute
void LightSwitchStateManager::OnTick()
{
    std::lock_guard<std::mutex> lock(_stateMutex);
    if (_state.lastAppliedMode != ScheduleMode::FollowNightLight)
    {
        EvaluateAndApplyIfNeeded();
    }
}

// Called when manual override is triggered (via hotkey/tray)
void LightSwitchStateManager::OnManualOverride()
{
    std::lock_guard<std::mutex> lock(_stateMutex);
    Logger::Info(L"[StateManager] Manual override triggered");
    _state.isManualOverride = !_state.isManualOverride;

    // Caller has already flipped the Windows theme before signaling this event.
    // Sync cached state so the scheduler compares against the actual current theme
    // on the next evaluation.
    _state.isSystemLightActive = GetCurrentSystemTheme();
    _state.isAppsLightActive = GetCurrentAppsTheme();

    Logger::Debug(std::wstring(L"[StateManager] Synced internal theme state to current system theme (") +
                  (_state.isSystemLightActive ? L"light" : L"dark") +
                  L") and apps theme (" + (_state.isAppsLightActive ? L"light" : L"dark") + L").");

    EvaluateAndApplyIfNeeded();
}

// Runs when the registry observer detects a change in Night Light settings.
void LightSwitchStateManager::OnNightLightChange()
{
    std::lock_guard<std::mutex> lock(_stateMutex);

    bool newNightLightState = IsNightLightEnabled();

    // In Follow Night Light mode, treat a Night Light toggle as a boundary
    if (_state.lastAppliedMode == ScheduleMode::FollowNightLight && _state.isManualOverride)
    {
        Logger::Info(L"[StateManager] Night Light changed while manual override active; "
                     L"treating as a boundary and clearing manual override.");
        _state.isManualOverride = false;
    }

    if (newNightLightState != _state.isNightLightActive)
    {
        Logger::Info(std::wstring(L"[StateManager] Night Light toggled to ") +
                     (newNightLightState ? L"ON" : L"OFF"));

        _state.isNightLightActive = newNightLightState;
    }
    else
    {
        Logger::Debug(L"[StateManager] Night Light change event fired, but no actual change.");
    }

    EvaluateAndApplyIfNeeded();
}

// Helpers
bool LightSwitchStateManager::CoordinatesAreValid(const std::wstring& lat, const std::wstring& lon)
{
    try
    {
        double latVal = std::stod(lat);
        double lonVal = std::stod(lon);
        return !(latVal == 0 && lonVal == 0) && (latVal >= -90.0 && latVal <= 90.0) && (lonVal >= -180.0 && lonVal <= 180.0);
    }
    catch (...)
    {
        return false;
    }
}

void LightSwitchStateManager::SyncInitialThemeState()
{
    std::lock_guard<std::mutex> lock(_stateMutex);
    _state.isSystemLightActive = GetCurrentSystemTheme();
    _state.isAppsLightActive = GetCurrentAppsTheme();
    _state.isNightLightActive = IsNightLightEnabled();
    Logger::Debug(std::wstring(L"[StateManager] Synced initial state to current system theme (") +
                  (_state.isSystemLightActive ? L"light" : L"dark") + L")");
    Logger::Debug(std::wstring(L"[StateManager] Synced initial state to current apps theme (") +
                  (_state.isAppsLightActive ? L"light" : L"dark") + L")");

    // This will ensure that the theme is applied according to current settings at startup
    EvaluateAndApplyIfNeeded();
}

static std::pair<int, int> update_sun_times(const LightSwitchConfig& settings)
{
    double latitude = std::stod(settings.latitude);
    double longitude = std::stod(settings.longitude);

    SYSTEMTIME st;
    GetLocalTime(&st);

    SunTimes newTimes = CalculateSunriseSunset(latitude, longitude, st.wYear, st.wMonth, st.wDay);

    int newLightTime = newTimes.sunriseHour * 60 + newTimes.sunriseMinute;
    int newDarkTime = newTimes.sunsetHour * 60 + newTimes.sunsetMinute;

    Logger::Info(L"[StateManager] Updated sun times from coordinates.");

    return { newLightTime, newDarkTime };
}

// Internal: decide what should happen now
void LightSwitchStateManager::EvaluateAndApplyIfNeeded()
{
    LightSwitchSettings::instance().LoadSettings();
    const auto& _currentSettings = LightSwitchSettings::settings();
    auto now = GetNowMinutes();

    // Early exit: OFF mode just pauses activity
    if (_currentSettings.scheduleMode == ScheduleMode::Off)
    {
        _state.lastTickMinutes = now;
        return;
    }

    bool coordsValid = CoordinatesAreValid(_currentSettings.latitude, _currentSettings.longitude);

    // Handle Sun Mode recalculation
    if (_currentSettings.scheduleMode == ScheduleMode::SunsetToSunrise && coordsValid)
    {
        SYSTEMTIME st;
        GetLocalTime(&st);
        bool newDay = (_state.lastEvaluatedDay != st.wDay);
        bool modeChangedToSun = (_state.lastAppliedMode != ScheduleMode::SunsetToSunrise &&
                                 _currentSettings.scheduleMode == ScheduleMode::SunsetToSunrise);

        if (newDay || modeChangedToSun)
        {
            auto [newLightTime, newDarkTime] = update_sun_times(_currentSettings);
            _state.lastEvaluatedDay = st.wDay;
            _state.effectiveLightMinutes = newLightTime + _currentSettings.sunrise_offset;
            _state.effectiveDarkMinutes = newDarkTime + _currentSettings.sunset_offset;
        }
        else
        {
            _state.effectiveLightMinutes = _currentSettings.lightTime + _currentSettings.sunrise_offset;
            _state.effectiveDarkMinutes = _currentSettings.darkTime + _currentSettings.sunset_offset;
        }
    }
    else if (_currentSettings.scheduleMode == ScheduleMode::FixedHours)
    {
        _state.effectiveLightMinutes = _currentSettings.lightTime;
        _state.effectiveDarkMinutes = _currentSettings.darkTime;
    }

    // Handle manual override logic
    if (_state.isManualOverride)
    {
        bool crossedBoundary = false;
        if (_state.lastTickMinutes != -1)
        {
            int prev = _state.lastTickMinutes;

            // Handle midnight wraparound safely
            if (now < prev)
            {
                crossedBoundary =
                    (prev <= _state.effectiveLightMinutes || now >= _state.effectiveLightMinutes) ||
                    (prev <= _state.effectiveDarkMinutes || now >= _state.effectiveDarkMinutes);
            }
            else
            {
                crossedBoundary =
                    (prev < _state.effectiveLightMinutes && now >= _state.effectiveLightMinutes) ||
                    (prev < _state.effectiveDarkMinutes && now >= _state.effectiveDarkMinutes);
            }
        }

        if (crossedBoundary)
        {
            _state.isManualOverride = false;
        }
        else
        {
            _state.lastTickMinutes = now;
            return;
        }
    }

    _state.lastAppliedMode = _currentSettings.scheduleMode;

    bool shouldBeLight = false;
    if (_currentSettings.scheduleMode == ScheduleMode::FollowNightLight)
    {
        shouldBeLight = !_state.isNightLightActive;
    }
    else
    {
        shouldBeLight = ShouldBeLight(now, _state.effectiveLightMinutes, _state.effectiveDarkMinutes);
    }

    bool appsNeedsToChange = _currentSettings.changeApps && (_state.isAppsLightActive != shouldBeLight);
    bool systemNeedsToChange = _currentSettings.changeSystem && (_state.isSystemLightActive != shouldBeLight);

    // Only apply theme if there's a change or no override active
    if (!_state.isManualOverride && (appsNeedsToChange || systemNeedsToChange))
    {
        Logger::Info(std::wstring(L"[StateManager] Applying ") + (shouldBeLight ? L"light" : L"dark") + L" theme");
        ApplyTheme(shouldBeLight);

        _state.isSystemLightActive = GetCurrentSystemTheme();
        _state.isAppsLightActive = GetCurrentAppsTheme();
    }

    _state.lastTickMinutes = now;
}
