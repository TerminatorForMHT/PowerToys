using LightSwitch.Native;

namespace LightSwitch.Services;

// Registers a global hotkey via a hidden message-only window.
internal sealed class HotkeyService : IDisposable
{
    private const int HotkeyId = 1;
    private const string WindowClassName = "LightSwitchHotkeyWindow";

    // Keep the delegate alive for the lifetime of the window.
    private static readonly NativeMethods.WndProc s_proc = WndProcRouter;
    private static HotkeyService? s_instance;

    private IntPtr _hwnd;
    private ushort _classAtom;
    private IntPtr _hInstance;
    private HotkeyConfig _current = new();

    public event EventHandler? HotkeyPressed;

    public void Start()
    {
        if (_hwnd != IntPtr.Zero)
            return;

        s_instance = this;
        _hInstance = NativeMethods.GetModuleHandle(null);

        var wc = new NativeMethods.WNDCLASSEXW
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.WNDCLASSEXW>(),
            lpfnWndProc = System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(s_proc),
            hInstance = _hInstance,
            lpszClassName = WindowClassName,
        };

        _classAtom = NativeMethods.RegisterClassExW(ref wc);
        if (_classAtom == 0)
        {
            Logger.Error("[Hotkey] RegisterClassExW failed.");
            return;
        }

        _hwnd = NativeMethods.CreateWindowExW(0, new IntPtr(_classAtom), null, 0,
            0, 0, 0, 0, NativeMethods.HWND_MESSAGE, IntPtr.Zero, _hInstance, IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
            Logger.Error("[Hotkey] CreateWindowExW failed.");
    }

    // Re-register the hotkey from a new config.
    public void Apply(HotkeyConfig hotkey)
    {
        _current = hotkey.Clone();

        if (_hwnd == IntPtr.Zero)
            return;

        NativeMethods.UnregisterHotKey(_hwnd, HotkeyId);

        if (hotkey.Key == 0)
        {
            Logger.Info("[Hotkey] Hotkey disabled.");
            return;
        }

        uint mods = NativeMethods.MOD_NOREPEAT;
        if (hotkey.Win) mods |= NativeMethods.MOD_WIN;
        if (hotkey.Ctrl) mods |= NativeMethods.MOD_CONTROL;
        if (hotkey.Alt) mods |= NativeMethods.MOD_ALT;
        if (hotkey.Shift) mods |= NativeMethods.MOD_SHIFT;

        if (!NativeMethods.RegisterHotKey(_hwnd, HotkeyId, mods, hotkey.Key))
            Logger.Warn("[Hotkey] RegisterHotKey failed (already taken by another app?).");
        else
            Logger.Info("[Hotkey] Hotkey registered.");
    }

    private static IntPtr WndProcRouter(IntPtr hWnd, uint msg, UIntPtr wParam, IntPtr lParam)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam == (UIntPtr)HotkeyId)
        {
            try
            {
                s_instance?.HotkeyPressed?.Invoke(s_instance, EventArgs.Empty);
            }
            catch (Exception e)
            {
                Logger.Error("[Hotkey] Exception in hotkey handler: " + e.Message);
            }
            return IntPtr.Zero;
        }

        return NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.UnregisterHotKey(_hwnd, HotkeyId);
            NativeMethods.DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }

        if (_classAtom != 0)
        {
            NativeMethods.UnregisterClassW(new IntPtr(_classAtom), _hInstance);
            _classAtom = 0;
        }

        s_instance = null;
    }
}
