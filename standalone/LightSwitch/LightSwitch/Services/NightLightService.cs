using LightSwitch.Native;
using Microsoft.Win32;

namespace LightSwitch.Services;

internal static class NightLightService
{
    public const string NightLightRegistryPath =
        @"Software\Microsoft\Windows\CurrentVersion\CloudStore\Store\DefaultAccount\Current\default$windows.data.bluelightreduction.bluelightreductionstate\windows.data.bluelightreduction.bluelightreductionstate";

    public static bool IsNightLightEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(NightLightRegistryPath);
            // We need bytes 23 and 24, so the blob must be at least 25 bytes
            if (key?.GetValue("Data") is byte[] data && data.Length >= 25)
                return data[23] == 0x10 && data[24] == 0x00;
        }
        catch
        {
            // fall through
        }

        return false;
    }
}

// Watches the Night Light registry key via RegNotifyChangeKeyValue on a background thread.
internal sealed class NightLightWatcher : IDisposable
{
    private readonly string _subkey;
    private readonly Action _callback;
    private readonly object _sync = new();

    private Thread? _thread;
    private volatile bool _stop;
    private IntPtr _hKey;
    private ManualResetEvent? _event;

    public NightLightWatcher(string subkey, Action callback)
    {
        _subkey = subkey;
        _callback = callback;
        _thread = new Thread(Run) { IsBackground = true, Name = "NightLightWatcher" };
        _thread.Start();
    }

    public void Dispose()
    {
        _stop = true;

        lock (_sync)
        {
            _event?.Set();
        }

        if (_thread != null)
        {
            _thread.Join(5000);
            _thread = null;
        }

        CleanupHandles();
    }

    private void Run()
    {
        lock (_sync)
        {
            if (NativeMethods.RegOpenKeyExW(NativeMethods.HKEY_CURRENT_USER, _subkey, 0,
                    NativeMethods.KEY_NOTIFY, out _hKey) != 0)
                return;

            _event = new ManualResetEvent(false);
        }

        while (true)
        {
            IntPtr hKeyLocal;
            IntPtr eventLocal;

            lock (_sync)
            {
                if (_stop)
                    break;

                hKeyLocal = _hKey;
                eventLocal = _event?.SafeWaitHandle.DangerousGetHandle() ?? IntPtr.Zero;
            }

            if (hKeyLocal == IntPtr.Zero || eventLocal == IntPtr.Zero)
                break;

            if (_stop)
                break;

            if (NativeMethods.RegNotifyChangeKeyValue(hKeyLocal, false,
                    NativeMethods.REG_NOTIFY_CHANGE_LAST_SET, eventLocal, true) != 0)
                break;

            uint wait = NativeMethods.WaitForSingleObject(eventLocal, NativeMethods.INFINITE);
            if (_stop || wait == NativeMethods.WAIT_FAILED)
                break;

            lock (_sync)
            {
                _event?.Reset();
            }

            if (_stop)
                break;

            try
            {
                _callback();
            }
            catch
            {
                // ignored
            }
        }

        CleanupHandles();
    }

    private void CleanupHandles()
    {
        lock (_sync)
        {
            if (_hKey != IntPtr.Zero)
            {
                NativeMethods.RegCloseKey(_hKey);
                _hKey = IntPtr.Zero;
            }

            if (_event != null)
            {
                _event.Dispose();
                _event = null;
            }
        }
    }
}
