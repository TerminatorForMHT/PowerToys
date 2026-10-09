using H.NotifyIcon;
using LightSwitch.Native;
using LightSwitch.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;

namespace LightSwitch;

public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Local\LightSwitchStandalone_{d8dc2f29-8c94-4ca1-8c5f-3e2b1e3c4f5a}";

    private Mutex? _singleInstanceMutex;
    private TaskbarIcon? _trayIcon;
    private SettingsUi? _settingsWindow;
    private SchedulerService? _scheduler;
    private HotkeyService? _hotkey;
    private DisplayChangeWatcher? _displayWatcher;

    // RegisterHotKey/UnregisterHotKey must run on the thread that created the
    // hidden hotkey window, so settings-change handlers marshal back here.
    private Microsoft.UI.Dispatching.DispatcherQueue? _uiDispatcher;

    public App()
    {
        InitializeComponent();
        UnhandledException += (s, e) =>
        {
            Logger.Error("[App] Unhandled exception: " + e.Message);
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out bool createdNew);
        if (!createdNew)
        {
            NativeMethods.MessageBox(IntPtr.Zero, "LightSwitch 已在运行。", "LightSwitch", NativeMethods.MB_ICONINFORMATION);
            Environment.Exit(0);
            return;
        }

        Logger.Init("LightSwitch");
        Logger.Info("[App] LightSwitch starting...");

        _uiDispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        // Ensure settings.json exists so the file watcher has something to watch.
        SettingsService.Instance.Save();

        // Align the login-startup state with the saved setting. Also self-heals
        // after a package upgrade where the old startup entry pointed at a
        // removed versioned exe path.
        _ = StartupService.ApplyAsync(SettingsService.Instance.Snapshot.StartWithWindows);

        _scheduler = new SchedulerService();
        _scheduler.Start();

        _hotkey = new HotkeyService();
        _hotkey.HotkeyPressed += (_, _) => _scheduler.ToggleThemeNow();
        _hotkey.Start();
        ApplyHotkeyWithFeedback(SettingsService.Instance.Snapshot.Hotkey);

        // Refresh the settings window's per-monitor rows on display changes.
        _displayWatcher = new DisplayChangeWatcher();
        _displayWatcher.DisplayChanged += (_, _) =>
            _uiDispatcher.TryEnqueue(() => _settingsWindow?.OnMonitorsChanged());
        _displayWatcher.Start();

        SettingsService.Instance.Changed += OnSettingsChanged;

        // Tray flyout commands execute while the menu is tearing down; creating
        // or showing a window in that context races the flyout close and can
        // crash. Dispatch instead so the work runs after the menu is gone.
        HookUpCommand("ToggleThemeCommand", (_, _) => RunOnUi(() => _scheduler.ToggleThemeNow()));
        HookUpCommand("ModeOffCommand", (_, _) => RunOnUi(() => SetMode(ScheduleMode.Off)));
        HookUpCommand("ModeFixedCommand", (_, _) => RunOnUi(() => SetMode(ScheduleMode.FixedHours)));
        HookUpCommand("ModeSunCommand", (_, _) => RunOnUi(() => SetMode(ScheduleMode.SunsetToSunrise)));
        HookUpCommand("ModeNightLightCommand", (_, _) => RunOnUi(() => SetMode(ScheduleMode.FollowNightLight)));
        HookUpCommand("TrayDoubleClickCommand", (_, _) => RunOnUi(OnTrayDoubleClick));
        HookUpCommand("OpenSettingsCommand", (_, _) => RunOnUi(ShowSettingsWindow));
        HookUpCommand("ExitCommand", (_, _) => RunOnUi(ExitApp));

        _trayIcon = (TaskbarIcon)Resources["TrayIcon"];
        if (_trayIcon.ContextFlyout is MenuFlyout mf)
            mf.Opening += OnTrayMenuOpening;
        _trayIcon.ForceCreate(false);

        // SecondWindow mode: Opening may not fire on first open — sync immediately.
        UpdateTrayModeChecks();
    }

    // Register the hotkey and surface conflicts to the user via toast.
    private void ApplyHotkeyWithFeedback(HotkeyConfig hotkey)
    {
        if (_hotkey == null)
            return;

        bool ok = _hotkey.Apply(hotkey);
        if (!ok && hotkey.Key != 0)
        {
            var mods = new List<string>();
            if (hotkey.Win) mods.Add("Win");
            if (hotkey.Ctrl) mods.Add("Ctrl");
            if (hotkey.Alt) mods.Add("Alt");
            if (hotkey.Shift) mods.Add("Shift");
            mods.Add(((Windows.System.VirtualKey)hotkey.Key).ToString());
            NotificationService.ShowError($"快捷键 {string.Join(" + ", mods)} 注册失败，可能已被其他程序占用。");
        }
    }

    // Double-click on the tray icon: toggle theme (default) or open settings.
    private void OnTrayDoubleClick()
    {
        if (SettingsService.Instance.Snapshot.TrayDoubleClickAction == "settings")
            ShowSettingsWindow();
        else
            _scheduler?.ToggleThemeNow();
    }

    private void HookUpCommand(string resourceKey, TypedEventHandler<XamlUICommand, ExecuteRequestedEventArgs> handler)
    {
        if (Resources[resourceKey] is XamlUICommand command)
            command.ExecuteRequested += handler;
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        // Changed may fire on a timer-pool thread (file watcher) — hop to the UI thread.
        // _uiDispatcher is captured on the UI thread in OnLaunched.
        if (_uiDispatcher is { } dq)
            dq.TryEnqueue(() =>
            {
                ApplyHotkeyWithFeedback(SettingsService.Instance.Snapshot.Hotkey);
                UpdateTrayModeChecks();
            });
    }

    // Sync the tray menu with current settings and theme state.
    private void UpdateTrayModeChecks()
    {
        if (_trayIcon?.ContextFlyout is not MenuFlyout flyout)
            return;

        var mode = SettingsService.Instance.Snapshot.ScheduleMode;
        // x:Name inside a ResourceDictionary-defined TaskbarIcon does not generate
        // a code field and the item is not in the app-level Resources map (lookup
        // would throw), so locate the state item positionally (first flyout item).
        if (flyout.Items.FirstOrDefault() is MenuFlyoutItem stateItem)
            stateItem.Text = "当前主题：" + (ThemeService.GetCurrentAppsTheme() ? "浅色" : "深色");

        var modeSubItem = flyout.Items.OfType<MenuFlyoutSubItem>().FirstOrDefault();
        if (modeSubItem != null)
        {
            foreach (var item in modeSubItem.Items)
            {
                if (item is RadioMenuFlyoutItem radio)
                {
                    radio.IsChecked = radio.Name switch
                    {
                        "TrayModeOff" => mode == ScheduleMode.Off,
                        "TrayModeFixed" => mode == ScheduleMode.FixedHours,
                        "TrayModeSun" => mode == ScheduleMode.SunsetToSunrise,
                        "TrayModeNightLight" => mode == ScheduleMode.FollowNightLight,
                        _ => false,
                    };
                }
            }
        }
    }

    // Queues work onto the UI thread (always asynchronous, so queued handlers
    // run after a tray flyout has finished closing).
    private void RunOnUi(Action action) => _uiDispatcher?.TryEnqueue(() => action());

    private void OnTrayMenuOpening(object sender, object e) => UpdateTrayModeChecks();

    private static void SetMode(ScheduleMode mode)
    {
        var cfg = SettingsService.Instance.Snapshot;
        cfg.ScheduleMode = mode;
        SettingsService.Instance.Update(cfg);
        Logger.Info($"[App] Schedule mode set to {ScheduleModeNames.ToName(mode)} via tray.");
    }

    private void ShowSettingsWindow()
    {
        UpdateTrayModeChecks(); // re-sync radio state in case file was edited externally

        if (_settingsWindow == null)
        {
            _settingsWindow = new SettingsUi();
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Activate();
        }
        else
        {
            _settingsWindow.AppWindow.Show();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_settingsWindow);
            NativeMethods.SetForegroundWindow(hwnd);
        }
    }

    private void ExitApp()
    {
        Logger.Info("[App] Exiting...");

        SettingsService.Instance.Changed -= OnSettingsChanged;

        _hotkey?.Dispose();
        _hotkey = null;

        _displayWatcher?.Dispose();
        _displayWatcher = null;

        _scheduler?.Dispose();
        _scheduler = null;

        _settingsWindow?.Close();
        _settingsWindow = null;

        _trayIcon?.Dispose();
        _trayIcon = null;

        Logger.Shutdown();

        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;

        Environment.Exit(0);
    }
}
