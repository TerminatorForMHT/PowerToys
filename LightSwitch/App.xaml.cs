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
    private SettingsWindow? _settingsWindow;
    private SchedulerService? _scheduler;
    private HotkeyService? _hotkey;

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

        _scheduler = new SchedulerService();
        _scheduler.Start();

        _hotkey = new HotkeyService();
        _hotkey.HotkeyPressed += (_, _) => _scheduler.ToggleThemeNow();
        _hotkey.Start();
        _hotkey.Apply(SettingsService.Instance.Snapshot.Hotkey);

        SettingsService.Instance.Changed += OnSettingsChanged;

        HookUpCommand("ToggleThemeCommand", (_, _) => _scheduler.ToggleThemeNow());
        HookUpCommand("ModeOffCommand", (_, _) => SetMode(ScheduleMode.Off));
        HookUpCommand("ModeFixedCommand", (_, _) => SetMode(ScheduleMode.FixedHours));
        HookUpCommand("ModeSunCommand", (_, _) => SetMode(ScheduleMode.SunsetToSunrise));
        HookUpCommand("ModeNightLightCommand", (_, _) => SetMode(ScheduleMode.FollowNightLight));
        HookUpCommand("OpenSettingsCommand", (_, _) => ShowSettingsWindow());
        HookUpCommand("ExitCommand", (_, _) => ExitApp());

        _trayIcon = (TaskbarIcon)Resources["TrayIcon"];
        if (_trayIcon.ContextFlyout is MenuFlyout mf)
            mf.Opening += OnTrayMenuOpening;
        _trayIcon.ForceCreate(false);
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
                _hotkey?.Apply(SettingsService.Instance.Snapshot.Hotkey);
                UpdateTrayModeChecks();
            });
    }

    // Sync the checked radio item in the tray menu with current settings.
    private void UpdateTrayModeChecks()
    {
        if (_trayIcon?.ContextFlyout is not MenuFlyout flyout || flyout.Items.Count < 2)
            return;

        var mode = SettingsService.Instance.Snapshot.ScheduleMode;

        if (flyout.Items[1] is MenuFlyoutSubItem modeSubItem)
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
        if (_settingsWindow == null)
        {
            _settingsWindow = new SettingsWindow();
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
