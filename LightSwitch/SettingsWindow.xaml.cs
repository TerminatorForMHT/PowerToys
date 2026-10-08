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
    private System.IntPtr _hwnd;

    // Guard: control mutations during LoadFromSettings must not re-persist settings.
    private bool _loading;

    // Per-monitor wallpaper text boxes (monitors 2..3), created in the ctor.
    private readonly List<TextBox> _extraLightBoxes = new();
    private readonly List<TextBox> _extraDarkBoxes = new();

    public SettingsWindow()
    {
        InitializeComponent();

        Title = "LightSwitch";

        // Windows 11 Mica backdrop
        if (MicaController.IsSupported())
            SystemBackdrop = new MicaBackdrop();

        // Fluent custom title bar: extend content and register the drag area
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarGrid);

        // Resizable like other Fluent settings windows; the NavigationView adapts
        // the layout when resized or maximized.
        var presenter = OverlappedPresenter.Create();
        presenter.IsResizable = true;
        AppWindow.SetPresenter(presenter);

        // Re-assert after replacing the presenter: creating a new OverlappedPresenter
        // resets AppWindow.TitleBar, whose standard (light) title bar would otherwise
        // peek out as a white line across the top when the window is maximized.
        AppWindow.TitleBar.ExtendsContentIntoTitleBar = true;

        // Scale the window by the display's DPI (e.g. 125% → 850×900 on a 2K screen).
        // Sized for the fixed 200px nav pane + paged content, like Auto Dark Mode.
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var dpi = GetDpiForWindow(_hwnd);
        double scale = dpi / 96.0;
        AppWindow.Resize(new SizeInt32((int)(680 * scale), (int)(720 * scale)));

        var titleBar = AppWindow.TitleBar;
        titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;

        _uiSettings.ColorValuesChanged += OnColorValuesChanged;
        Closed += OnWindowClosed;

        CreateMonitorWallpaperRows();
        ApplySystemTheme();
        LoadFromSettings();

        // Select the first nav item (fires OnNavSelectionChanged once all controls exist).
        NavView.SelectedItem = NavView.MenuItems[0];
    }

    // P/Invoke: DPI of the window's display (needed to scale the AppWindow size
    // correctly — AppWindow.Resize works in physical pixels, not DIPs).
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(System.IntPtr hwnd);

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _uiSettings.ColorValuesChanged -= OnColorValuesChanged;
    }

    // Win11 Settings pattern: the left nav pane switches the visible section.
    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item)
            return;

        var tag = item.Tag as string;
        SectionMode.Visibility = tag == "mode" ? Visibility.Visible : Visibility.Collapsed;
        SectionScope.Visibility = tag == "scope" ? Visibility.Visible : Visibility.Collapsed;
        SectionWallpaper.Visibility = tag == "wallpaper" ? Visibility.Visible : Visibility.Collapsed;
        SectionGeneral.Visibility = tag == "general" ? Visibility.Visible : Visibility.Collapsed;
        PageTitle.Text = item.Content as string ?? string.Empty;
    }

    private void OnColorValuesChanged(UISettings sender, object args)
    {
        DispatcherQueue.TryEnqueue(ApplySystemTheme);
    }

    private void ApplySystemTheme()
    {
        bool isLight = ThemeService.GetCurrentAppsTheme();
        RootGrid.RequestedTheme = isLight ? ElementTheme.Light : ElementTheme.Dark;

        // Theme the caption buttons (min/max/close) to match
        var foreground = isLight ? Windows.UI.Color.FromArgb(255, 27, 27, 27) : Windows.UI.Color.FromArgb(255, 255, 255, 255);
        var hoverBackground = isLight ? Windows.UI.Color.FromArgb(25, 0, 0, 0) : Windows.UI.Color.FromArgb(25, 255, 255, 255);
        var pressedBackground = isLight ? Windows.UI.Color.FromArgb(51, 0, 0, 0) : Windows.UI.Color.FromArgb(51, 255, 255, 255);

        var titleBar = AppWindow.TitleBar;
        titleBar.ButtonForegroundColor = foreground;
        titleBar.ButtonHoverForegroundColor = foreground;
        titleBar.ButtonPressedForegroundColor = foreground;
        titleBar.ButtonHoverBackgroundColor = hoverBackground;
        titleBar.ButtonPressedBackgroundColor = pressedBackground;
    }

    private void LoadFromSettings()
    {
        _loading = true;
        try
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
            // "0.0"/"0.0" is the persisted sentinel for "use system location".
            LatitudeBox.Text = cfg.Latitude == "0.0" ? string.Empty : cfg.Latitude;
            LongitudeBox.Text = cfg.Longitude == "0.0" ? string.Empty : cfg.Longitude;
            SunriseOffsetBox.Value = cfg.SunriseOffset;
            SunsetOffsetBox.Value = cfg.SunsetOffset;
            SystemToggle.IsOn = cfg.ChangeSystem;
            AppsToggle.IsOn = cfg.ChangeApps;
            WallpaperToggle.IsOn = cfg.ChangeWallpaper;
            LightWallpaperBox.Text = cfg.LightWallpaper;
            DarkWallpaperBox.Text = cfg.DarkWallpaper;
            for (int i = 0; i < _extraLightBoxes.Count; i++)
            {
                var l = i == 0 ? cfg.LightWallpaper2 : cfg.LightWallpaper3;
                var d = i == 0 ? cfg.DarkWallpaper2 : cfg.DarkWallpaper3;
                _extraLightBoxes[i].Text = l;
                _extraDarkBoxes[i].Text = d;
            }
            StartupToggle.IsOn = cfg.StartWithWindows;
            NotificationsToggle.IsOn = cfg.ShowNotifications;
            TrayDoubleClickCombo.SelectedIndex = cfg.TrayDoubleClickAction == "settings" ? 1 : 0;
            _hotkey = cfg.Hotkey.Clone();

            UpdateHotkeyText();
            UpdateModePanelVisibility();
            UpdateSunTimesDisplay();
            WallpaperPanel.Visibility = WallpaperToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            _loading = false;
        }
    }

    // One row (light + dark pickers) per additional monitor, up to 3 monitors.
    private void CreateMonitorWallpaperRows()
    {
        int count = Math.Min(Math.Max(WallpaperService.GetMonitorCount(), 1), 3);
        for (int m = 1; m < count; m++)
        {
            var lightBox = new TextBox { Header = $"显示器 {m + 1} · 浅色壁纸", PlaceholderText = "留空则沿用显示器 1 的图片", IsReadOnly = true };
            var darkBox = new TextBox { Header = $"显示器 {m + 1} · 深色壁纸", PlaceholderText = "留空则沿用显示器 1 的图片", IsReadOnly = true };
            var lightButton = new Button { Content = "浏览...", VerticalAlignment = VerticalAlignment.Bottom };
            var darkButton = new Button { Content = "浏览...", VerticalAlignment = VerticalAlignment.Bottom };

            lightButton.Click += async (_, _) =>
            {
                var path = await PickImageFileAsync();
                if (path != null)
                {
                    lightBox.Text = path;
                    ApplyChanges();
                }
            };
            darkButton.Click += async (_, _) =>
            {
                var path = await PickImageFileAsync();
                if (path != null)
                {
                    darkBox.Text = path;
                    ApplyChanges();
                }
            };

            MonitorWallpaperPanel.Children.Add(WrapWithBrowse(lightBox, lightButton));
            MonitorWallpaperPanel.Children.Add(WrapWithBrowse(darkBox, darkButton));
            _extraLightBoxes.Add(lightBox);
            _extraDarkBoxes.Add(darkBox);
        }
    }

    private static Grid WrapWithBrowse(TextBox box, Button button)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(button, 2);
        grid.Children.Add(box);
        grid.Children.Add(button);
        return grid;
    }

    // Collects the whole UI state and persists it — settings apply instantly,
    // Win11-Settings style (no Save button).
    private void ApplyChanges()
    {
        if (_loading)
            return;

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
        cfg.ChangeWallpaper = WallpaperToggle.IsOn;
        cfg.LightWallpaper = LightWallpaperBox.Text.Trim();
        cfg.DarkWallpaper = DarkWallpaperBox.Text.Trim();
        if (_extraLightBoxes.Count > 0) cfg.LightWallpaper2 = _extraLightBoxes[0].Text.Trim();
        if (_extraLightBoxes.Count > 1) cfg.LightWallpaper3 = _extraLightBoxes[1].Text.Trim();
        if (_extraDarkBoxes.Count > 0) cfg.DarkWallpaper2 = _extraDarkBoxes[0].Text.Trim();
        if (_extraDarkBoxes.Count > 1) cfg.DarkWallpaper3 = _extraDarkBoxes[1].Text.Trim();
        cfg.StartWithWindows = StartupToggle.IsOn;
        cfg.ShowNotifications = NotificationsToggle.IsOn;
        cfg.TrayDoubleClickAction = TrayDoubleClickCombo.SelectedIndex == 1 ? "settings" : "toggle";
        cfg.Hotkey = _hotkey.Clone();

        // Lat/lon: persist only valid or intentionally-empty values; the "0.0"
        // sentinel means "use system location".
        var latText = LatitudeBox.Text.Trim();
        var lonText = LongitudeBox.Text.Trim();
        bool latEmpty = latText.Length == 0;
        bool lonEmpty = lonText.Length == 0;

        bool latOk = double.TryParse(latText, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double lat) && lat >= -90 && lat <= 90;
        bool lonOk = double.TryParse(lonText, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double lon) && lon >= -180 && lon <= 180;

        if (latEmpty && lonEmpty)
        {
            cfg.Latitude = "0.0";
            cfg.Longitude = "0.0";
        }
        else if (!latEmpty && !lonEmpty && latOk && lonOk)
        {
            cfg.Latitude = latText;
            cfg.Longitude = lonText;
        }
        // otherwise keep the previously stored values

        SettingsService.Instance.Update(cfg);
        StartupService.Apply(cfg.StartWithWindows);
    }

    private void OnModeChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateModePanelVisibility();
        ApplyChanges();
    }

    private void UpdateModePanelVisibility()
    {
        int idx = ModeCombo.SelectedIndex;
        FixedHoursPanel.Visibility = idx == 1 ? Visibility.Visible : Visibility.Collapsed;
        SunPanel.Visibility = idx == 2 ? Visibility.Visible : Visibility.Collapsed;
        NightLightHint.Visibility = idx == 3 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnScheduleChanged(object sender, TimePickerValueChangedEventArgs e) => ApplyChanges();

    private void OnSettingToggled(object sender, RoutedEventArgs e) => ApplyChanges();

    private void OnTrayActionChanged(object sender, SelectionChangedEventArgs e) => ApplyChanges();

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
            ApplyChanges();
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
        ApplyChanges();
    }

    private async void OnGetLocationClick(object sender, RoutedEventArgs e)
    {
        GetLocationButton.IsEnabled = false;
        try
        {
            var loc = await LocationService.TryGetLocationAsync();
            if (loc is { } l)
            {
                LatitudeBox.Text = l.Latitude.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);
                LongitudeBox.Text = l.Longitude.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);
                ApplyChanges();
            }
            else
            {
                var dlg = new ContentDialog
                {
                    Title = "无法获取位置",
                    Content = "请在 设置 → 隐私和安全性 → 位置 中开启定位服务，或手动填写经纬度。",
                    CloseButtonText = "确定",
                    XamlRoot = Content.XamlRoot,
                };
                await dlg.ShowAsync();
            }
        }
        finally
        {
            GetLocationButton.IsEnabled = true;
        }
    }

    private void OnSunInputsChanged(object sender, TextChangedEventArgs e) => UpdateSunTimesDisplay();

    private void OnSunOffsetChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        UpdateSunTimesDisplay();
        ApplyChanges();
    }

    // Commit latitude/longitude on focus loss with validation feedback.
    private async void OnLatLonCommitted(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;

        var latText = LatitudeBox.Text.Trim();
        var lonText = LongitudeBox.Text.Trim();
        bool latEmpty = latText.Length == 0;
        bool lonEmpty = lonText.Length == 0;

        if (latEmpty != lonEmpty)
        {
            var dlg = new ContentDialog
            {
                Title = "经纬度不完整",
                Content = "纬度和经度需要同时填写，或同时留空以使用系统定位。",
                CloseButtonText = "确定",
                XamlRoot = Content.XamlRoot,
            };
            await dlg.ShowAsync();
            return;
        }

        if (!latEmpty)
        {
            bool latOk = double.TryParse(latText, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double lat) && lat >= -90 && lat <= 90;
            bool lonOk = double.TryParse(lonText, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double lon) && lon >= -180 && lon <= 180;
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

        ApplyChanges();
    }

    // Show today's computed sunrise/sunset (and the effective times after offsets).
    private void UpdateSunTimesDisplay()
    {
        if (SunTimesText == null)
            return;

        var latText = LatitudeBox.Text.Trim();
        var lonText = LongitudeBox.Text.Trim();

        if (latText.Length == 0 || lonText.Length == 0)
        {
            SunTimesText.Text = "经纬度留空：将自动使用系统定位计算。";
            return;
        }

        bool latOk = double.TryParse(latText, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double lat) && lat >= -90 && lat <= 90;
        bool lonOk = double.TryParse(lonText, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double lon) && lon >= -180 && lon <= 180;

        if (!latOk || !lonOk)
        {
            SunTimesText.Text = "经纬度无效，无法计算日出日落时间。";
            return;
        }

        var now = DateTime.Now;
        var t = SunCalculator.Calculate(lat, lon, now.Year, now.Month, now.Day);
        int rise = t.SunriseHour * 60 + t.SunriseMinute;
        int set = t.SunsetHour * 60 + t.SunsetMinute;
        int riseEff = rise + (double.IsNaN(SunriseOffsetBox.Value) ? 0 : (int)SunriseOffsetBox.Value);
        int setEff = set + (double.IsNaN(SunsetOffsetBox.Value) ? 0 : (int)SunsetOffsetBox.Value);

        SunTimesText.Text = riseEff == rise && setEff == set
            ? $"今日日出 {Fmt(rise)}，日落 {Fmt(set)}"
            : $"今日日出 {Fmt(rise)}，日落 {Fmt(set)}（含偏移：{Fmt(riseEff)} / {Fmt(setEff)}）";

        static string Fmt(int m)
        {
            m = ((m % 1440) + 1440) % 1440;
            return $"{m / 60:D2}:{m % 60:D2}";
        }
    }

    private void OnWallpaperToggled(object sender, RoutedEventArgs e)
    {
        WallpaperPanel.Visibility = WallpaperToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
        ApplyChanges();
    }

    private async void OnPickLightWallpaper(object sender, RoutedEventArgs e)
    {
        var path = await PickImageFileAsync();
        if (path != null)
        {
            LightWallpaperBox.Text = path;
            ApplyChanges();
        }
    }

    private async void OnPickDarkWallpaper(object sender, RoutedEventArgs e)
    {
        var path = await PickImageFileAsync();
        if (path != null)
        {
            DarkWallpaperBox.Text = path;
            ApplyChanges();
        }
    }

    // FileOpenPicker needs a window handle to show up in a WinUI 3 desktop app.
    private async System.Threading.Tasks.Task<string?> PickImageFileAsync()
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        picker.ViewMode = Windows.Storage.Pickers.PickerViewMode.Thumbnail;
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        picker.FileTypeFilter.Add(".png");
        picker.FileTypeFilter.Add(".bmp");

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    private void OnClearHotkeyClick(object sender, RoutedEventArgs e)
    {
        _hotkey = new HotkeyConfig();
        UpdateHotkeyText();
        ApplyChanges();
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
}
