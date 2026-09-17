using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;

namespace NotchBar.Services;

public sealed class HotkeyService : IDisposable
{
    private const int WmHotKey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const int HotkeyId = 0x4E42;

    private HwndSource? _source;
    private IntPtr _handle;
    private bool _registered;

    public event EventHandler? Pressed;
    public event EventHandler? RegistrationFailed;

    public void Attach(Window window, ModifierKeys modifiers, Key key)
    {
        var helper = new WindowInteropHelper(window);
        _handle = helper.EnsureHandle();
        _source = HwndSource.FromHwnd(_handle);
        _source?.AddHook(WndProc);

        var nativeModifiers = ToNativeModifiers(modifiers);
        var virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        _registered = RegisterHotKey(_handle, HotkeyId, nativeModifiers, virtualKey);
        if (!_registered)
        {
            RegistrationFailed?.Invoke(this, EventArgs.Empty);
        }
    }

    internal static uint ToNativeModifiers(ModifierKeys modifiers)
    {
        var nativeModifiers = 0u;
        if (modifiers.HasFlag(ModifierKeys.Alt)) nativeModifiers |= ModAlt;
        if (modifiers.HasFlag(ModifierKeys.Control)) nativeModifiers |= ModControl;
        if (modifiers.HasFlag(ModifierKeys.Shift)) nativeModifiers |= ModShift;
        if (modifiers.HasFlag(ModifierKeys.Windows)) nativeModifiers |= ModWin;
        return nativeModifiers;
    }

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmHotKey && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            Pressed?.Invoke(this, EventArgs.Empty);
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_registered && _handle != IntPtr.Zero)
        {
            UnregisterHotKey(_handle, HotkeyId);
        }

        if (_source is not null)
        {
            _source.RemoveHook(WndProc);
        }

        _registered = false;
        _handle = IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
