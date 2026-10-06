namespace LightSwitch.Services;

// Central scheduler that evaluates theme boundaries and reacts to settings
// changes, manual overrides and time ticks.  Merges the original C++ Worker
// and StateManager into a single background-threaded C# service.
internal sealed class SchedulerService : IDisposable
{
    // Thread-signal events
    private readonly ManualResetEvent _stopEvent = new(false);
    private readonly ManualResetEvent _manualOverrideEvent = new(false);
    private readonly ManualResetEvent _settingsChangedEvent = new(false);
    private Thread? _thread;

    // Runtime state (protected by _stateMutex)
    private readonly object _stateMutex = new();
    private ScheduleMode _lastAppliedMode = ScheduleMode.Off;
    private bool _isManualOverride;
    private bool _isSystemLightActive;
    private bool _isAppsLightActive;
    private bool _isNightLightActive;
    private int _lastEvaluatedDay = -1;
    private int _lastTickMinutes = -1;
    private int _effectiveLightMinutes;
    private int _effectiveDarkMinutes;

    private NightLightWatcher? _nightLightWatcher;
    private bool _running;

    public void Start()
    {
        if (_running)
            return;

        _running = true;
        SettingsService.Instance.Changed += OnSettingsServiceChanged;
        _thread = new Thread(Run) { IsBackground = true, Name = "SchedulerService" };
        _thread.Start();
    }

    public void Dispose()
    {
        _stopEvent.Set();
        _thread?.Join(5000);
        _thread = null;
        _nightLightWatcher?.Dispose();
        _stopEvent.Dispose();
        _manualOverrideEvent.Dispose();
        _settingsChangedEvent.Dispose();
    }

    private void OnSettingsServiceChanged(object? sender, EventArgs e)
    {
        _settingsChangedEvent.Set();
    }

    // Toggle the current theme immediately (tray/hotkey action).
    public void ToggleThemeNow()
    {
        var snap = SettingsService.Instance.Snapshot;

        if (snap.ChangeSystem)
        {
            bool current = ThemeService.GetCurrentSystemTheme();
            ThemeService.SetSystemTheme(!current);
            Logger.Info($"[Scheduler] Manual toggle: system theme to {(current ? "dark" : "light")}.");
        }
        if (snap.ChangeApps)
        {
            bool current = ThemeService.GetCurrentAppsTheme();
            ThemeService.SetAppsTheme(!current);
            Logger.Info($"[Scheduler] Manual toggle: apps theme to {(current ? "dark" : "light")}.");
        }

        _manualOverrideEvent.Set();
    }

    private void Run()
    {
        Logger.Info("[Scheduler] Thread starting...");
        SettingsService.Instance.StartWatcher();

        var snap = SettingsService.Instance.Snapshot;
        if (snap.ScheduleMode == ScheduleMode.FollowNightLight)
            StartNightLightWatcher();

        SyncInitialThemeState();

        while (true)
        {
            int msToNextMinute = ComputeMsToNextMinute();
            int idx = WaitHandle.WaitAny(new WaitHandle[] { _stopEvent, _manualOverrideEvent, _settingsChangedEvent }, msToNextMinute);

            if (idx == 0) // stop
            {
                Logger.Info("[Scheduler] Stop event triggered — exiting.");
                break;
            }

            if (idx == 1) // manual override
            {
                Logger.Info("[Scheduler] Manual override event detected.");
                OnManualOverride();
                _manualOverrideEvent.Reset();
                continue;
            }

            if (idx == 2) // settings changed
            {
                Logger.Info("[Scheduler] Settings changed event detected.");
                _settingsChangedEvent.Reset();
                OnSettingsChanged();
                continue;
            }

            // Timeout = regular minute tick
            DetectAndHandleExternalThemeChange();
            OnTick();
        }

        lock (_stateMutex)
        {
            _nightLightWatcher?.Dispose();
            _nightLightWatcher = null;
        }

        Logger.Info("[Scheduler] Thread exiting cleanly.");
    }

    private static int ComputeMsToNextMinute()
    {
        var now = DateTime.Now;
        int ms = (60 - now.Second) * 1000 - now.Millisecond;
        return ms < 50 ? 50 : ms;
    }

    private static int GetNowMinutes()
    {
        var now = DateTime.Now;
        return now.Hour * 60 + now.Minute;
    }

    private static bool ShouldBeLight(int nowMinutes, int lightTime, int darkTime)
    {
        int nLight = ((lightTime % 1440) + 1440) % 1440;
        int nDark = ((darkTime % 1440) + 1440) % 1440;
        int nNow = ((nowMinutes % 1440) + 1440) % 1440;

        if (nLight < nDark)
            return nNow >= nLight && nNow < nDark;

        return nNow >= nLight || nNow < nDark;
    }

    private static bool CoordinatesAreValid(string lat, string lon)
    {
        try
        {
            double latVal = double.Parse(lat);
            double lonVal = double.Parse(lon);
            return !(latVal == 0 && lonVal == 0) && latVal >= -90.0 && latVal <= 90.0 && lonVal >= -180.0 && lonVal <= 180.0;
        }
        catch
        {
            return false;
        }
    }

    private void SyncInitialThemeState()
    {
        lock (_stateMutex)
        {
            _isSystemLightActive = ThemeService.GetCurrentSystemTheme();
            _isAppsLightActive = ThemeService.GetCurrentAppsTheme();
            _isNightLightActive = NightLightService.IsNightLightEnabled();
            Logger.Info($"[Scheduler] Synced initial state: system={(_isSystemLightActive ? "light" : "dark")}, apps={(_isAppsLightActive ? "light" : "dark")}, nightLight={(_isNightLightActive ? "ON" : "OFF")}.");
            EvaluateAndApplyIfNeeded();
        }
    }

    private void OnTick()
    {
        lock (_stateMutex)
        {
            if (_lastAppliedMode != ScheduleMode.FollowNightLight)
                EvaluateAndApplyIfNeeded();
        }
    }

    private void OnManualOverride()
    {
        lock (_stateMutex)
        {
            Logger.Info("[Scheduler] Manual override triggered.");
            _isManualOverride = !_isManualOverride;
            _isSystemLightActive = ThemeService.GetCurrentSystemTheme();
            _isAppsLightActive = ThemeService.GetCurrentAppsTheme();
            EvaluateAndApplyIfNeeded();
        }
    }

    private void OnNightLightChange()
    {
        lock (_stateMutex)
        {
            bool newState = NightLightService.IsNightLightEnabled();

            if (_lastAppliedMode == ScheduleMode.FollowNightLight && _isManualOverride)
            {
                Logger.Info("[Scheduler] Night Light changed while manual override active; treating as boundary and clearing manual override.");
                _isManualOverride = false;
            }

            if (newState != _isNightLightActive)
            {
                Logger.Info($"[Scheduler] Night Light toggled to {(newState ? "ON" : "OFF")}.");
                _isNightLightActive = newState;
            }
            else
            {
                Logger.Debug("[Scheduler] Night Light event fired, but no actual change.");
            }

            EvaluateAndApplyIfNeeded();
        }
    }

    private void OnSettingsChanged()
    {
        lock (_stateMutex)
        {
            if (_isManualOverride)
                _isManualOverride = false;

            var snap = SettingsService.Instance.Snapshot;
            bool needNightLight = snap.ScheduleMode == ScheduleMode.FollowNightLight;

            if (needNightLight && _nightLightWatcher == null)
                StartNightLightWatcher();
            else if (!needNightLight && _nightLightWatcher != null)
            {
                _nightLightWatcher.Dispose();
                _nightLightWatcher = null;
            }

            EvaluateAndApplyIfNeeded();
        }
    }

    private void StartNightLightWatcher()
    {
        _nightLightWatcher?.Dispose();
        _nightLightWatcher = new NightLightWatcher(NightLightService.NightLightRegistryPath, OnNightLightChange);
    }

    private void DetectAndHandleExternalThemeChange()
    {
        lock (_stateMutex)
        {
            var snap = SettingsService.Instance.Snapshot;
            if (snap.ScheduleMode == ScheduleMode.Off)
                return;

            int now = GetNowMinutes();
            int effectiveLight = snap.LightTime;
            int effectiveDark = snap.DarkTime;

            if (snap.ScheduleMode == ScheduleMode.SunsetToSunrise)
            {
                effectiveLight = (snap.LightTime + snap.SunriseOffset) % 1440;
                effectiveDark = (snap.DarkTime + snap.SunsetOffset) % 1440;
            }

            bool shouldBeLight = snap.ScheduleMode == ScheduleMode.FollowNightLight
                ? !NightLightService.IsNightLightEnabled()
                : ShouldBeLight(now, effectiveLight, effectiveDark);

            bool currentSystem = ThemeService.GetCurrentSystemTheme();
            bool currentApps = ThemeService.GetCurrentAppsTheme();

            bool sysMismatch = snap.ChangeSystem && currentSystem != shouldBeLight;
            bool appMismatch = snap.ChangeApps && currentApps != shouldBeLight;

            if ((sysMismatch || appMismatch) && !_isManualOverride)
            {
                Logger.Info("[Scheduler] External theme change detected (Windows Settings). Entering manual override mode.");
                _isManualOverride = !_isManualOverride;
                _isSystemLightActive = currentSystem;
                _isAppsLightActive = currentApps;
                EvaluateAndApplyIfNeeded();
            }
        }
    }

    private void EvaluateAndApplyIfNeeded()
    {
        var snap = SettingsService.Instance.Snapshot;
        int now = GetNowMinutes();

        if (snap.ScheduleMode == ScheduleMode.Off)
        {
            _lastTickMinutes = now;
            return;
        }

        bool coordsValid = CoordinatesAreValid(snap.Latitude, snap.Longitude);

        if (snap.ScheduleMode == ScheduleMode.SunsetToSunrise && coordsValid)
        {
            int today = DateTime.Now.Day;
            bool newDay = _lastEvaluatedDay != today;
            bool modeChangedToSun = _lastAppliedMode != ScheduleMode.SunsetToSunrise && snap.ScheduleMode == ScheduleMode.SunsetToSunrise;

            if (newDay || modeChangedToSun)
            {
                double lat = double.Parse(snap.Latitude);
                double lon = double.Parse(snap.Longitude);
                var dt = DateTime.Now;
                var times = SunCalculator.Calculate(lat, lon, dt.Year, dt.Month, dt.Day);
                _effectiveLightMinutes = times.SunriseHour * 60 + times.SunriseMinute + snap.SunriseOffset;
                _effectiveDarkMinutes = times.SunsetHour * 60 + times.SunsetMinute + snap.SunsetOffset;
                _lastEvaluatedDay = today;
                Logger.Info("[Scheduler] Updated sun times from coordinates.");
            }
            else
            {
                _effectiveLightMinutes = snap.LightTime + snap.SunriseOffset;
                _effectiveDarkMinutes = snap.DarkTime + snap.SunsetOffset;
            }
        }
        else if (snap.ScheduleMode == ScheduleMode.FixedHours)
        {
            _effectiveLightMinutes = snap.LightTime;
            _effectiveDarkMinutes = snap.DarkTime;
        }

        if (_isManualOverride)
        {
            bool crossedBoundary = false;
            if (_lastTickMinutes != -1)
            {
                int prev = _lastTickMinutes;
                if (now < prev)
                {
                    crossedBoundary =
                        (prev <= _effectiveLightMinutes || now >= _effectiveLightMinutes) ||
                        (prev <= _effectiveDarkMinutes || now >= _effectiveDarkMinutes);
                }
                else
                {
                    crossedBoundary =
                        (prev < _effectiveLightMinutes && now >= _effectiveLightMinutes) ||
                        (prev < _effectiveDarkMinutes && now >= _effectiveDarkMinutes);
                }
            }

            if (crossedBoundary)
                _isManualOverride = false;
            else
            {
                _lastTickMinutes = now;
                return;
            }
        }

        _lastAppliedMode = snap.ScheduleMode;

        bool shouldBeLight;
        if (snap.ScheduleMode == ScheduleMode.FollowNightLight)
            shouldBeLight = !_isNightLightActive;
        else
            shouldBeLight = ShouldBeLight(now, _effectiveLightMinutes, _effectiveDarkMinutes);

        bool appsNeedChange = snap.ChangeApps && _isAppsLightActive != shouldBeLight;
        bool systemNeedChange = snap.ChangeSystem && _isSystemLightActive != shouldBeLight;

        if (!_isManualOverride && (appsNeedChange || systemNeedChange))
        {
            Logger.Info($"[Scheduler] Applying {(shouldBeLight ? "light" : "dark")} theme.");
            ApplyTheme(shouldBeLight, snap);
            _isSystemLightActive = ThemeService.GetCurrentSystemTheme();
            _isAppsLightActive = ThemeService.GetCurrentAppsTheme();
        }

        _lastTickMinutes = now;
    }

    private static void ApplyTheme(bool shouldBeLight, LightSwitchConfig settings)
    {
        if (settings.ChangeSystem)
        {
            bool current = ThemeService.GetCurrentSystemTheme();
            if (shouldBeLight != current)
                ThemeService.SetSystemTheme(shouldBeLight);
        }
        if (settings.ChangeApps)
        {
            bool current = ThemeService.GetCurrentAppsTheme();
            if (shouldBeLight != current)
                ThemeService.SetAppsTheme(shouldBeLight);
        }
    }
}
