#include "pch.h"
#include "Worker.h"
#include "ThemeHelper.h"
#include "LightSwitchUtils.h"
#include "Settings.h"
#include "Logger.h"

// Applies the target theme to system/apps according to the current settings.
void ApplyTheme(bool shouldBeLight)
{
    const auto& s = LightSwitchSettings::settings();

    if (s.changeSystem)
    {
        bool isSystemCurrentlyLight = GetCurrentSystemTheme();
        if (shouldBeLight != isSystemCurrentlyLight)
        {
            SetSystemTheme(shouldBeLight);
            Logger::Info(std::wstring(L"[Worker] Changed system theme to ") + (shouldBeLight ? L"light" : L"dark"));
        }
    }

    if (s.changeApps)
    {
        bool isAppsCurrentlyLight = GetCurrentAppsTheme();
        if (shouldBeLight != isAppsCurrentlyLight)
        {
            SetAppsTheme(shouldBeLight);
            Logger::Info(std::wstring(L"[Worker] Changed apps theme to ") + (shouldBeLight ? L"light" : L"dark"));
        }
    }
}

Worker::Worker()
{
    m_stopEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    m_manualOverrideEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
}

Worker::~Worker()
{
    Stop();
    if (m_stopEvent)
        CloseHandle(m_stopEvent);
    if (m_manualOverrideEvent)
        CloseHandle(m_manualOverrideEvent);
}

bool Worker::Start()
{
    if (m_thread)
        return true;

    m_thread = CreateThread(nullptr, 0, ThreadProc, this, 0, nullptr);
    return m_thread != nullptr;
}

void Worker::Stop()
{
    if (!m_thread)
        return;

    Logger::Info(L"[Worker] Stop requested.");
    SetEvent(m_stopEvent);
    WaitForSingleObject(m_thread, 5000);
    CloseHandle(m_thread);
    m_thread = nullptr;
}

void Worker::ToggleThemeNow()
{
    const auto& s = LightSwitchSettings::settings();

    if (s.changeSystem)
        SetSystemTheme(!GetCurrentSystemTheme());
    if (s.changeApps)
        SetAppsTheme(!GetCurrentAppsTheme());

    Logger::Info(L"[Worker] Manual toggle requested.");
    SetEvent(m_manualOverrideEvent);
}

DWORD WINAPI Worker::ThreadProc(LPVOID param)
{
    static_cast<Worker*>(param)->Run();
    return 0;
}

void Worker::DetectAndHandleExternalThemeChange()
{
    const auto& s = LightSwitchSettings::settings();
    if (s.scheduleMode == ScheduleMode::Off)
        return;

    SYSTEMTIME st;
    GetLocalTime(&st);
    int nowMinutes = st.wHour * 60 + st.wMinute;

    int effectiveLight = s.lightTime;
    int effectiveDark = s.darkTime;

    if (s.scheduleMode == ScheduleMode::SunsetToSunrise)
    {
        effectiveLight = (s.lightTime + s.sunrise_offset) % 1440;
        effectiveDark = (s.darkTime + s.sunset_offset) % 1440;
    }

    bool shouldBeLight = false;
    if (s.scheduleMode == ScheduleMode::FollowNightLight)
    {
        shouldBeLight = !IsNightLightEnabled();
    }
    else
    {
        shouldBeLight = ShouldBeLight(nowMinutes, effectiveLight, effectiveDark);
    }

    bool currentSystemLight = GetCurrentSystemTheme();
    bool currentAppsLight = GetCurrentAppsTheme();

    bool systemMismatch = s.changeSystem && (currentSystemLight != shouldBeLight);
    bool appsMismatch = s.changeApps && (currentAppsLight != shouldBeLight);

    if ((systemMismatch || appsMismatch) && !m_stateManager.GetState().isManualOverride)
    {
        Logger::Info(L"[Worker] External theme change detected (Windows Settings). Entering manual override mode.");
        m_stateManager.OnManualOverride();
    }
}

void Worker::Run()
{
    Logger::Info(L"[Worker] Thread starting...");

    LightSwitchSettings::instance().InitFileWatcher();

    HANDLE hSettingsChanged = LightSwitchSettings::instance().GetSettingsChangedEvent();

    LightSwitchSettings::instance().LoadSettings();
    const auto& settings = LightSwitchSettings::instance().settings();

    bool nightLightNeeded = (settings.scheduleMode == ScheduleMode::FollowNightLight);
    if (nightLightNeeded && !m_nightLightWatcher)
    {
        Logger::Info(L"[Worker] Starting Night Light registry watcher...");
        m_nightLightWatcher = std::make_unique<NightLightRegistryObserver>(
            HKEY_CURRENT_USER,
            NIGHT_LIGHT_REGISTRY_PATH,
            [this]() { m_stateManager.OnNightLightChange(); });
    }

    SYSTEMTIME st;
    GetLocalTime(&st);
    Logger::Info(L"[Worker] Initialized.");

    m_stateManager.SyncInitialThemeState();

    for (;;)
    {
        HANDLE waits[3];
        DWORD count = 0;
        waits[count++] = m_stopEvent;
        waits[count++] = m_manualOverrideEvent;
        waits[count++] = hSettingsChanged;

        // Wait for one of these to trigger or for a new minute tick
        GetLocalTime(&st);
        int msToNextMinute = (60 - st.wSecond) * 1000 - st.wMilliseconds;
        if (msToNextMinute < 50)
            msToNextMinute = 50;

        DWORD wait = WaitForMultipleObjects(count, waits, FALSE, msToNextMinute);

        if (wait == WAIT_TIMEOUT)
        {
            // regular minute tick
            DetectAndHandleExternalThemeChange();
            m_stateManager.OnTick();
            continue;
        }

        if (wait == WAIT_OBJECT_0)
        {
            Logger::Info(L"[Worker] Stop event triggered — exiting.");
            break;
        }

        if (wait == WAIT_OBJECT_0 + 1)
        {
            Logger::Info(L"[Worker] Manual override event detected.");
            m_stateManager.OnManualOverride();
            ResetEvent(m_manualOverrideEvent);
            continue;
        }

        if (wait == WAIT_OBJECT_0 + 2)
        {
            ResetEvent(hSettingsChanged);
            LightSwitchSettings::instance().LoadSettings();
            m_stateManager.OnSettingsChanged();

            const auto& newSettings = LightSwitchSettings::instance().settings();
            bool nightLightNeededNow = (newSettings.scheduleMode == ScheduleMode::FollowNightLight);

            if (nightLightNeededNow && !m_nightLightWatcher)
            {
                Logger::Info(L"[Worker] Starting Night Light registry watcher...");
                m_nightLightWatcher = std::make_unique<NightLightRegistryObserver>(
                    HKEY_CURRENT_USER,
                    NIGHT_LIGHT_REGISTRY_PATH,
                    [this]() { m_stateManager.OnNightLightChange(); });

                m_stateManager.OnNightLightChange();
            }
            else if (!nightLightNeededNow && m_nightLightWatcher)
            {
                Logger::Info(L"[Worker] Stopping Night Light registry watcher...");
                m_nightLightWatcher->Stop();
                m_nightLightWatcher.reset();
            }

            continue;
        }
    }

    if (m_nightLightWatcher)
    {
        m_nightLightWatcher->Stop();
        m_nightLightWatcher.reset();
    }

    Logger::Info(L"[Worker] Thread exiting cleanly.");
}
