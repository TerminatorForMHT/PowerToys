#pragma once

#include <windows.h>
#include <memory>
#include "StateManager.h"
#include "NightLightRegistryObserver.h"

// Owns the scheduling worker thread: minute ticks, settings reload events,
// manual-override events and the night-light registry watcher.
class Worker
{
public:
    Worker();
    ~Worker();

    bool Start();
    void Stop();

    // Flips the current theme (per settings targets) and enters manual override.
    void ToggleThemeNow();

private:
    static DWORD WINAPI ThreadProc(LPVOID param);
    void Run();
    void DetectAndHandleExternalThemeChange();

    HANDLE m_stopEvent = nullptr;             // signaled on app exit
    HANDLE m_manualOverrideEvent = nullptr;   // signaled by tray/hotkey toggle
    HANDLE m_thread = nullptr;

    LightSwitchStateManager m_stateManager;
    std::unique_ptr<NightLightRegistryObserver> m_nightLightWatcher;
};
