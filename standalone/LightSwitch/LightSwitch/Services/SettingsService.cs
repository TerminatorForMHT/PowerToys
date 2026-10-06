using System.Text.Json;
using System.Text.Json.Nodes;

namespace LightSwitch.Services;

public enum ScheduleMode
{
    Off,
    FixedHours,
    SunsetToSunrise,
    FollowNightLight,
}

public static class ScheduleModeNames
{
    public static string ToName(ScheduleMode mode) => mode switch
    {
        ScheduleMode.FixedHours => "FixedHours",
        ScheduleMode.SunsetToSunrise => "SunsetToSunrise",
        ScheduleMode.FollowNightLight => "FollowNightLight",
        _ => "Off",
    };

    public static ScheduleMode FromName(string? name) => name switch
    {
        "FixedHours" => ScheduleMode.FixedHours,
        "SunsetToSunrise" => ScheduleMode.SunsetToSunrise,
        "FollowNightLight" => ScheduleMode.FollowNightLight,
        _ => ScheduleMode.Off,
    };
}

public sealed class HotkeyConfig
{
    public bool Win { get; set; }
    public bool Ctrl { get; set; }
    public bool Alt { get; set; }
    public bool Shift { get; set; }
    public uint Key { get; set; } // virtual-key code, 0 = disabled

    public HotkeyConfig Clone() => (HotkeyConfig)MemberwiseClone();

    public bool EqualsTo(HotkeyConfig other) =>
        Win == other.Win && Ctrl == other.Ctrl && Alt == other.Alt && Shift == other.Shift && Key == other.Key;
}

public sealed class LightSwitchConfig
{
    public ScheduleMode ScheduleMode { get; set; } = ScheduleMode.FixedHours;
    public string Latitude { get; set; } = "0.0";
    public string Longitude { get; set; } = "0.0";
    public int LightTime { get; set; } = 8 * 60;  // minutes since midnight
    public int DarkTime { get; set; } = 20 * 60;  // minutes since midnight
    public int SunriseOffset { get; set; }
    public int SunsetOffset { get; set; }
    public bool ChangeSystem { get; set; } = true;
    public bool ChangeApps { get; set; } = true;
    public HotkeyConfig Hotkey { get; set; } = new() { Win = true, Ctrl = true, Shift = true, Alt = false, Key = 'D' };

    public LightSwitchConfig Clone()
    {
        var clone = (LightSwitchConfig)MemberwiseClone();
        clone.Hotkey = Hotkey.Clone();
        return clone;
    }
}

// Owns settings.json under %LocalAppData%\LightSwitch\.
// The JSON schema matches the previous C++ build so existing files keep working.
public sealed class SettingsService : IDisposable
{
    private static readonly Lazy<SettingsService> LazyInstance = new(() => new SettingsService());
    public static SettingsService Instance => LazyInstance.Value;

    public static string AppDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LightSwitch");

    public static string SettingsFilePath => Path.Combine(AppDataDir, "settings.json");

    private readonly object _sync = new();
    private LightSwitchConfig _config = new();

    private FileSystemWatcher? _watcher;
    private System.Threading.Timer? _debounceTimer;
    private long _lastWriteTicksUtc;

    // Fired after settings were reloaded from disk (file watcher) or replaced via Update().
    public event EventHandler? Changed;

    private SettingsService()
    {
        Load();
    }

    public LightSwitchConfig Snapshot
    {
        get
        {
            lock (_sync)
            {
                return _config.Clone();
            }
        }
    }

    public void Load()
    {
        var path = SettingsFilePath;
        if (!File.Exists(path))
        {
            // Use defaults; file will be created on first Save()
            return;
        }

        try
        {
            var text = File.ReadAllText(path);
            var j = JsonNode.Parse(text) as JsonObject;
            if (j == null)
                return;

            lock (_sync)
            {
                var cfg = _config.Clone();

                if (j["scheduleMode"] is JsonValue modeValue && modeValue.TryGetValue<string>(out var mode))
                    cfg.ScheduleMode = ScheduleModeNames.FromName(mode);

                cfg.Latitude = ReadString(j, "latitude", cfg.Latitude);
                cfg.Longitude = ReadString(j, "longitude", cfg.Longitude);
                cfg.LightTime = ReadInt(j, "lightTime", cfg.LightTime);
                cfg.DarkTime = ReadInt(j, "darkTime", cfg.DarkTime);
                cfg.SunriseOffset = ReadInt(j, "sunrise_offset", cfg.SunriseOffset);
                cfg.SunsetOffset = ReadInt(j, "sunset_offset", cfg.SunsetOffset);
                cfg.ChangeSystem = ReadBool(j, "changeSystem", cfg.ChangeSystem);
                cfg.ChangeApps = ReadBool(j, "changeApps", cfg.ChangeApps);

                if (j["hotkey"] is JsonObject hk)
                {
                    cfg.Hotkey.Win = ReadBool(hk, "win", cfg.Hotkey.Win);
                    cfg.Hotkey.Ctrl = ReadBool(hk, "ctrl", cfg.Hotkey.Ctrl);
                    cfg.Hotkey.Alt = ReadBool(hk, "alt", cfg.Hotkey.Alt);
                    cfg.Hotkey.Shift = ReadBool(hk, "shift", cfg.Hotkey.Shift);
                    cfg.Hotkey.Key = (uint)ReadInt(hk, "key", (int)cfg.Hotkey.Key);
                }

                _config = cfg;
            }
        }
        catch (Exception e)
        {
            Logger.Error("[Settings] Failed to load settings: " + e.Message);
        }
    }

    public void Save()
    {
        lock (_sync)
        {
            try
            {
                Directory.CreateDirectory(AppDataDir);

                var cfg = _config;
                var j = new JsonObject
                {
                    ["scheduleMode"] = ScheduleModeNames.ToName(cfg.ScheduleMode),
                    ["latitude"] = cfg.Latitude,
                    ["longitude"] = cfg.Longitude,
                    ["lightTime"] = cfg.LightTime,
                    ["darkTime"] = cfg.DarkTime,
                    ["sunrise_offset"] = cfg.SunriseOffset,
                    ["sunset_offset"] = cfg.SunsetOffset,
                    ["changeSystem"] = cfg.ChangeSystem,
                    ["changeApps"] = cfg.ChangeApps,
                    ["hotkey"] = new JsonObject
                    {
                        ["win"] = cfg.Hotkey.Win,
                        ["ctrl"] = cfg.Hotkey.Ctrl,
                        ["alt"] = cfg.Hotkey.Alt,
                        ["shift"] = cfg.Hotkey.Shift,
                        ["key"] = cfg.Hotkey.Key,
                    },
                };

                var path = SettingsFilePath;
                File.WriteAllText(path, j.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                _lastWriteTicksUtc = File.GetLastWriteTimeUtc(path).Ticks;
            }
            catch (Exception e)
            {
                Logger.Error("[Settings] Failed to save settings: " + e.Message);
            }
        }
    }

    // Replace current config with a new one and persist it.
    public void Update(LightSwitchConfig newConfig)
    {
        lock (_sync)
        {
            _config = newConfig.Clone();
        }

        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // Watch settings.json for external edits and reload with a 1s debounce.
    public void StartWatcher()
    {
        lock (_sync)
        {
            if (_watcher != null)
                return;

            Directory.CreateDirectory(AppDataDir);
            if (File.Exists(SettingsFilePath))
                _lastWriteTicksUtc = File.GetLastWriteTimeUtc(SettingsFilePath).Ticks;

            _debounceTimer = new System.Threading.Timer(OnDebounceElapsed, null, Timeout.Infinite, Timeout.Infinite);

            _watcher = new FileSystemWatcher(AppDataDir, "settings.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += OnFileEvent;
            _watcher.Created += OnFileEvent;
            _watcher.Renamed += OnFileEvent;
        }
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        // Debounce: wait 1 second after the last change
        _debounceTimer?.Change(1000, Timeout.Infinite);
    }

    private void OnDebounceElapsed(object? state)
    {
        try
        {
            var path = SettingsFilePath;
            if (!File.Exists(path))
                return;

            var writeTicks = File.GetLastWriteTimeUtc(path).Ticks;

            lock (_sync)
            {
                // Ignore events caused by our own Save()
                if (writeTicks == _lastWriteTicksUtc)
                    return;
                _lastWriteTicksUtc = writeTicks;
            }

            Load();
            Logger.Info("[Settings] File change detected and settings reloaded.");
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception e)
        {
            Logger.Error("[Settings] Exception during reload: " + e.Message);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_watcher != null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Changed -= OnFileEvent;
                _watcher.Created -= OnFileEvent;
                _watcher.Renamed -= OnFileEvent;
                _watcher.Dispose();
                _watcher = null;
            }

            _debounceTimer?.Dispose();
            _debounceTimer = null;
        }
    }

    private static string ReadString(JsonObject j, string key, string fallback)
    {
        return j[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : fallback;
    }

    private static int ReadInt(JsonObject j, string key, int fallback)
    {
        return j[key] is JsonValue v && v.TryGetValue<int>(out var i) ? i : fallback;
    }

    private static bool ReadBool(JsonObject j, string key, bool fallback)
    {
        return j[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : fallback;
    }
}
