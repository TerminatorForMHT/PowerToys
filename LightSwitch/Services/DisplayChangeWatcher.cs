using LightSwitch.Native;

namespace LightSwitch.Services;

// Watches for display topology changes (WM_DISPLAYCHANGE broadcast) via a
// hidden message-only window, so the settings UI can refresh its per-monitor
// wallpaper rows while the window is open. Same pattern as HotkeyService.
internal sealed class DisplayChangeWatcher : IDisposable
{
    private const uint WM_DISPLAYCHANGE = 0x007E;
    private const string WindowClassName = "LightSwitchDisplayWindow";

    // Keep the delegate alive for the lifetime of the window.
    private static readonly NativeMethods.WndProc s_proc = WndProcRouter;
    private static DisplayChangeWatcher? s_instance;

    private IntPtr _hwnd;
    private ushort _classAtom;
    private IntPtr _hInstance;

    // Raised whenever the monitor topology changes. The message-only window is
    // created on the UI thread, so handlers run on the UI thread.
    public event EventHandler? DisplayChanged;

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
            Logger.Error("[Display] RegisterClassExW failed.");
            return;
        }

        _hwnd = NativeMethods.CreateWindowExW(0, new IntPtr(_classAtom), null, 0,
            0, 0, 0, 0, NativeMethods.HWND_MESSAGE, IntPtr.Zero, _hInstance, IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
            Logger.Error("[Display] CreateWindowExW failed.");
    }

    private static IntPtr WndProcRouter(IntPtr hWnd, uint msg, UIntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_DISPLAYCHANGE)
        {
            try
            {
                s_instance?.DisplayChanged?.Invoke(s_instance, EventArgs.Empty);
            }
            catch (Exception e)
            {
                Logger.Error("[Display] Exception in handler: " + e.Message);
            }
        }

        return NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero)
        {
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
