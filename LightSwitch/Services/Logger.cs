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
                var logPath = Path.Combine(logDir, appName + ".log");

                // Rotate: a tray app can run for months — keep one generation (.old)
                // and start fresh whenever the log exceeds ~1 MB.
                try
                {
                    if (File.Exists(logPath) && new FileInfo(logPath).Length > 1024 * 1024)
                    {
                        var oldPath = Path.Combine(logDir, appName + ".old.log");
                        File.Delete(oldPath);
                        File.Move(logPath, oldPath);
                    }
                }
                catch
                {
                    // Rotation is best-effort; fall through and keep appending.
                }

                _file = new StreamWriter(logPath, append: true, Encoding.UTF8)
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
