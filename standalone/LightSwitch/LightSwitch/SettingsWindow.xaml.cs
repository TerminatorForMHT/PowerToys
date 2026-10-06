using LightSwitch.Native;
using LightSwitch.Services;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI.ViewManagement;

namespace LightSwitch;

public sealed partial class SettingsWindow : Window
{
    private readonly UISettings _uiSettings = new();
    private HotkeyConfig _hotkey = new();

    public SettingsWindow()
    {
        InitializeComponent();

        Title = "LightSwitch 设置";

        // Windows 11 Mica backdrop
        if (MicaController.IsSupported())
            SystemBackdrop = new MicaBackdrop();

        var presenter = OverlappedPresenter.Create();
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        AppWindow.SetPresenter(presenter);
        AppWindow.Resize(new SizeInt32(520, 760));

        _uiSettings.ColorValuesChanged += OnColorValuesChanged;
        Closed += (_, _) => _uiSettings.ColorValuesChanged -= OnColorValuesChanged;

        ApplySystemTheme();
        LoadFromSettings();
    }

    private void OnColorValuesChanged(UISettings sender, object args)
    {
        DispatcherQueue.TryEnqueue(ApplySystemTheme);
    }

    private void ApplySystemTheme()
    {
        bool isLight = ThemeService.GetCurrentAppsTheme();
        RootGrid.RequestedTheme = isLight ? ElementTheme.Light : ElementTheme.Dark;
    }

    private void LoadFromSettings()
    {
        var cfg = SettingsService.Instance.Snapshot;

        ModeCombo.SelectedIndex = cfg.ScheduleMode switch
        {
            ScheduleMode.Off => 0,
            ScheduleMode.FixedHours => 1,
            ScheduleMode.SunsetToSunrise => 2,
            ScheduleMode.FollowNightLight => 3,
            _ => 0,
        };

        LightTimePicker.Time = TimeSpan.FromMinutes(((cfg.LightTime % 1440) + 1440) % 1440);
        DarkTimePicker.Time = TimeSpan.FromMinutes(((cfg.DarkTime % 1440) + 1440) % 1440);
        LatitudeBox.Text = cfg.Latitude;
        LongitudeBox.Text = cfg.Longitude;
        SunriseOffsetBox.Value = cfg.SunriseOffset;
        SunsetOffsetBox.Value = cfg.SunsetOffset;
        SystemToggle.IsOn = cfg.ChangeSystem;
        AppsToggle.IsOn = cfg.ChangeApps;
        _hotkey = cfg.Hotkey.Clone();

        UpdateHotkeyText();
        UpdateModePanelVisibility();
    }

    private void OnModeChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateModePanelVisibility();
    }

    private void UpdateModePanelVisibility()
    {
        int idx = ModeCombo.SelectedIndex;
        FixedHoursPanel.Visibility = idx == 1 ? Visibility.Visible : Visibility.Collapsed;
        SunPanel.Visibility = idx == 2 ? Visibility.Visible : Visibility.Collapsed;
        NightLightHint.Visibility = idx == 3 ? Visibility.Visible : Visibility.Collapsed;
    }

    // Capture the next key combination as the global hotkey.
    private void OnHotkeyKeyDown(object sender, KeyRoutedEventArgs e)
    {
        e.Handled = true;

        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            UpdateHotkeyText();
            return;
        }

        if (e.Key == Windows.System.VirtualKey.Back || e.Key == Windows.System.VirtualKey.Delete)
        {
            _hotkey = new HotkeyConfig();
            UpdateHotkeyText();
            return;
        }

        bool win = (NativeMethods.GetKeyState(NativeMethods.VK_LWIN) & 0x8000) != 0 ||
                   (NativeMethods.GetKeyState(NativeMethods.VK_RWIN) & 0x8000) != 0;
        bool ctrl = (NativeMethods.GetKeyState(NativeMethods.VK_CONTROL) & 0x8000) != 0;
        bool alt = (NativeMethods.GetKeyState(NativeMethods.VK_MENU) & 0x8000) != 0;
        bool shift = (NativeMethods.GetKeyState(NativeMethods.VK_SHIFT) & 0x8000) != 0;

        // Ignore bare modifier presses — show live preview
        if (e.Key is Windows.System.VirtualKey.Control or Windows.System.VirtualKey.Shift or
            Windows.System.VirtualKey.Menu or Windows.System.VirtualKey.LeftWindows or
            Windows.System.VirtualKey.RightWindows)
        {
            HotkeyCapture.Text = FormatHotkey(new HotkeyConfig { Win = win, Ctrl = ctrl, Alt = alt, Shift = shift, Key = 0 });
            return;
        }

        _hotkey = new HotkeyConfig { Win = win, Ctrl = ctrl, Alt = alt, Shift = shift, Key = (uint)e.Key };
        UpdateHotkeyText();
    }

    private void OnClearHotkeyClick(object sender, RoutedEventArgs e)
    {
        _hotkey = new HotkeyConfig();
        UpdateHotkeyText();
    }

    private void UpdateHotkeyText()
    {
        HotkeyCapture.Text = FormatHotkey(_hotkey);
    }

    private static string FormatHotkey(HotkeyConfig hk)
    {
        if (hk.Key == 0)
        {
            var mods = ModifierPreview(hk);
            return mods.Length == 0 ? "无" : mods + " + ...";
        }

        var parts = new List<string>();
        if (hk.Win) parts.Add("Win");
        if (hk.Ctrl) parts.Add("Ctrl");
        if (hk.Alt) parts.Add("Alt");
        if (hk.Shift) parts.Add("Shift");
        parts.Add(((Windows.System.VirtualKey)hk.Key).ToString());
        return string.Join(" + ", parts);
    }

    private static string ModifierPreview(HotkeyConfig hk)
    {
        var parts = new List<string>();
        if (hk.Win) parts.Add("Win");
        if (hk.Ctrl) parts.Add("Ctrl");
        if (hk.Alt) parts.Add("Alt");
        if (hk.Shift) parts.Add("Shift");
        return string.Join(" + ", parts);
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        var cfg = SettingsService.Instance.Snapshot;

        cfg.ScheduleMode = ModeCombo.SelectedIndex switch
        {
            1 => ScheduleMode.FixedHours,
            2 => ScheduleMode.SunsetToSunrise,
            3 => ScheduleMode.FollowNightLight,
            _ => ScheduleMode.Off,
        };

        cfg.LightTime = (int)LightTimePicker.Time.TotalMinutes;
        cfg.DarkTime = (int)DarkTimePicker.Time.TotalMinutes;
        cfg.SunriseOffset = double.IsNaN(SunriseOffsetBox.Value) ? 0 : (int)SunriseOffsetBox.Value;
        cfg.SunsetOffset = double.IsNaN(SunsetOffsetBox.Value) ? 0 : (int)SunsetOffsetBox.Value;
        cfg.ChangeSystem = SystemToggle.IsOn;
        cfg.ChangeApps = AppsToggle.IsOn;
        cfg.Hotkey = _hotkey;

        var latText = LatitudeBox.Text.Trim();
        var lonText = LongitudeBox.Text.Trim();

        if (cfg.ScheduleMode == ScheduleMode.SunsetToSunrise)
        {
            bool latOk = double.TryParse(latText, out double lat) && lat >= -90 && lat <= 90;
            bool lonOk = double.TryParse(lonText, out double lon) && lon >= -180 && lon <= 180;
            if (!latOk || !lonOk)
            {
                var dlg = new ContentDialog
                {
                    Title = "无效的经纬度",
                    Content = "请检查纬度和经度：纬度应在 -90 到 90 之间，经度应在 -180 到 180 之间。",
                    CloseButtonText = "确定",
                    XamlRoot = Content.XamlRoot,
                };
                await dlg.ShowAsync();
                return;
            }
        }

        cfg.Latitude = latText;
        cfg.Longitude = lonText;

        SettingsService.Instance.Update(cfg);
        Logger.Info("[SettingsWindow] Settings saved.");
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
