using System.Diagnostics;
using System.Text;

namespace LightSwitch.Services;

// Minimal logger: writes to debugger output and a log file under
// %LocalAppData%\LightSwitch\logs\.
internal static class Logger
{
    private static readonly object Sync = new();
    private static StreamWriter? _file;
    private static bool _initialized;

    public static void Init(string appName)
    {
        lock (Sync)
        {
            if (_initialized)
                return;

            try
            {
                var logDir = Path.Combine(SettingsService.AppDataDir, "logs");
                Directory.CreateDirectory(logDir);
                _file = new StreamWriter(Path.Combine(logDir, appName + ".log"), append: true, Encoding.UTF8)
                {
                    AutoFlush = true,
                };
            }
            catch
            {
                // Logging must never crash the app.
            }

            _initialized = true;
        }
    }

    public static void Shutdown()
    {
        lock (Sync)
        {
            _file?.Dispose();
            _file = null;
            _initialized = false;
        }
    }

    private static void Write(string level, string msg)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {msg}";
        lock (Sync)
        {
            System.Diagnostics.Debug.WriteLine(line);
            try
            {
                _file?.WriteLine(line);
            }
            catch
            {
                // ignored
            }
        }
    }

    public static void Info(string msg) => Write("INFO", msg);

    public static void Warn(string msg) => Write("WARN", msg);

    public static void Error(string msg) => Write("ERROR", msg);

    public static void Debug(string msg) => Write("DEBUG", msg);
}
