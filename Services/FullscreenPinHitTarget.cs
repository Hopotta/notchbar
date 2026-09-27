using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;

namespace NotchBar.Services;

/// <summary>
/// A nearly invisible, non-activating HWND that owns only the fullscreen pin
/// button's physical-pixel rectangle. The main island can remain click-through
/// while this small target receives the pin click directly.
/// </summary>
internal sealed class FullscreenPinHitTarget : IDisposable
{
    private const string ClassName = "NotchBar.FullscreenPinHitTarget";
    private const uint WsPopup = 0x80000000;
    private const uint WsExToolWindow = 0x00000080;
    private const uint WsExNoActivate = 0x08000000;
    private const uint WsExLayered = 0x00080000;
    private const uint LwaAlpha = 0x00000002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const int SwHide = 0;
    private const int WmNcHitTest = 0x0084;
    private const int WmMouseActivate = 0x0021;
    private const int WmSetCursor = 0x0020;
    private const int WmMouseMove = 0x0200;
    private const int WmMouseLeave = 0x02A3;
    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonUp = 0x0202;
    private const int WmCaptureChanged = 0x0215;
    private const int WmCancelMode = 0x001F;
    private const int WmEraseBackground = 0x0014;
    private const int WmPaint = 0x000F;
    private const int HtClient = 1;
    private const int MaNoActivate = 3;
    private const int IdcHand = 32649;
    private const uint TmeLeave = 0x00000002;
    private const byte MinimumVisibleAlpha = 1;
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly object ClassLock = new();
    private static readonly WindowProcedure WindowProcedureDelegate = WindowProc;
    private static readonly Dictionary<IntPtr, FullscreenPinHitTarget> Instances = [];
    private static ushort _windowClassAtom;

    private Rectangle _bounds;
    private bool _visible;
    private bool _pressed;
    private bool _hovered;
    private bool _trackingMouseLeave;
    private bool _disposed;
    private readonly bool _useHandCursor;

    public FullscreenPinHitTarget(IntPtr owner, bool useHandCursor = true)
    {
        if (owner == IntPtr.Zero)
        {
            throw new ArgumentException("A valid owner window is required.", nameof(owner));
        }

        _useHandCursor = useHandCursor;
        EnsureClassRegistered();
        Handle = CreateWindowEx(
            WsExLayered | WsExNoActivate | WsExToolWindow,
            ClassName,
            string.Empty,
            WsPopup,
            0,
            0,
            1,
            1,
            owner,
            IntPtr.Zero,
            GetModuleHandle(null),
            IntPtr.Zero);
        if (Handle == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The fullscreen pin hit target could not be created.");
        }

        Instances[Handle] = this;
        if (!SetLayeredWindowAttributes(Handle, 0, MinimumVisibleAlpha, LwaAlpha))
        {
            var error = Marshal.GetLastWin32Error();
            _ = DestroyWindow(Handle);
            Instances.Remove(Handle);
            Handle = IntPtr.Zero;
            throw new Win32Exception(error, "The fullscreen pin hit target could not be made transparent.");
        }
    }

    public IntPtr Handle { get; private set; }

    public event EventHandler? Clicked;

    public event EventHandler? MouseEntered;

    public event EventHandler? MouseLeft;

    /// <summary>
    /// Moves and shows the target only when its integer screen rectangle
    /// changes. A cached rectangle avoids repeated native calls during frames
    /// that quantize to the same physical pixels.
    /// </summary>
    public bool ShowAt(Rectangle bounds)
    {
        if (_disposed || Handle == IntPtr.Zero || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return false;
        }

        if (_visible && _bounds == bounds)
        {
            return true;
        }

        var flags = SwpNoActivate | SwpShowWindow;
        var insertAfter = IntPtr.Zero;
        if (!_visible)
        {
            // The main island was raised immediately before the first show;
            // put this owned target above it in the topmost group.
            insertAfter = HwndTopmost;
        }
        else
        {
            flags |= SwpNoZOrder;
        }

        if (!SetWindowPos(Handle, insertAfter, bounds.X, bounds.Y, bounds.Width, bounds.Height, flags))
        {
            return false;
        }

        _bounds = bounds;
        _visible = true;
        RefreshHoverFromCursor();
        return true;
    }

    public bool RaiseAboveOwner()
    {
        if (_disposed || !_visible || Handle == IntPtr.Zero)
        {
            return true;
        }

        return SetWindowPos(
            Handle,
            HwndTopmost,
            0,
            0,
            0,
            0,
            SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow);
    }

    public void Hide()
    {
        if (Handle != IntPtr.Zero && _visible)
        {
            if (_pressed)
            {
                _ = ReleaseCapture();
            }

            _ = ShowWindow(Handle, SwHide);
            _visible = false;
            _pressed = false;
            _trackingMouseLeave = false;
            SetHovered(false);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var handle = Handle;
        Handle = IntPtr.Zero;
        _visible = false;
        _pressed = false;
        _trackingMouseLeave = false;
        _hovered = false;
        if (handle != IntPtr.Zero)
        {
            Instances.Remove(handle);
            _ = ShowWindow(handle, SwHide);
            _ = DestroyWindow(handle);
        }

        Clicked = null;
        MouseEntered = null;
        MouseLeft = null;
        GC.SuppressFinalize(this);
    }

    private static void EnsureClassRegistered()
    {
        if (_windowClassAtom != 0)
        {
            return;
        }

        lock (ClassLock)
        {
            if (_windowClassAtom != 0)
            {
                return;
            }

            var windowClass = new WindowClassEx
            {
                Size = (uint)Marshal.SizeOf<WindowClassEx>(),
                WindowProcedure = WindowProcedureDelegate,
                Instance = GetModuleHandle(null),
                ClassName = ClassName
            };
            _windowClassAtom = RegisterClassEx(ref windowClass);
            if (_windowClassAtom == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "The fullscreen pin hit target class could not be registered.");
            }
        }
    }

    private static IntPtr WindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam)
    {
        if (!Instances.TryGetValue(hwnd, out var target))
        {
            return DefWindowProc(hwnd, message, wParam, lParam);
        }

        switch (message)
        {
            case WmNcHitTest:
                return new IntPtr(HtClient);
            case WmMouseActivate:
#if DEBUG
                Debug.WriteLine($"FullscreenHitTarget {hwnd}: WM_MOUSEACTIVATE -> MA_NOACTIVATE");
#endif
                return new IntPtr(MaNoActivate);
            case WmSetCursor:
                if ((unchecked((int)lParam.ToInt64()) & 0xFFFF) == HtClient)
                {
                    if (target._useHandCursor)
                    {
                        _ = SetCursor(LoadCursor(IntPtr.Zero, new IntPtr(IdcHand)));
                        return new IntPtr(1);
                    }
                }

                break;
            case WmMouseMove:
                target.BeginMouseLeaveTracking(hwnd);
                target.SetHovered(true);
                return IntPtr.Zero;
            case WmMouseLeave:
                target._trackingMouseLeave = false;
                target.SetHovered(false);
                return IntPtr.Zero;
            case WmLButtonDown:
#if DEBUG
                Debug.WriteLine($"FullscreenHitTarget {hwnd}: WM_LBUTTONDOWN bounds={target._bounds} cursor={GetCursorPositionForDebug()} captureBefore={GetCapture()}");
#endif
                target.BeginMouseLeaveTracking(hwnd);
                target.SetHovered(true);
                target._pressed = true;
                _ = SetCapture(hwnd);
#if DEBUG
                Debug.WriteLine($"FullscreenHitTarget {hwnd}: captureAfter={GetCapture()}");
#endif
                return IntPtr.Zero;
            case WmLButtonUp:
#if DEBUG
                Debug.WriteLine($"FullscreenHitTarget {hwnd}: WM_LBUTTONUP pressed={target._pressed} bounds={target._bounds} point=({unchecked((short)(unchecked((int)lParam.ToInt64()) & 0xFFFF))},{unchecked((short)((unchecked((int)lParam.ToInt64()) >> 16) & 0xFFFF))}) capture={GetCapture()}");
#endif
                if (target._pressed)
                {
                    target._pressed = false;
                    _ = ReleaseCapture();
                    var packed = unchecked((int)lParam.ToInt64());
                    var x = unchecked((short)(packed & 0xFFFF));
                    var y = unchecked((short)((packed >> 16) & 0xFFFF));
                    if (x >= 0 && y >= 0 && x < target._bounds.Width && y < target._bounds.Height)
                    {
#if DEBUG
                        Debug.WriteLine($"FullscreenHitTarget {hwnd}: invoking Clicked");
#endif
                        target.Clicked?.Invoke(target, EventArgs.Empty);
                    }

                    return IntPtr.Zero;
                }

                return IntPtr.Zero;
            case WmCaptureChanged:
            case WmCancelMode:
#if DEBUG
                Debug.WriteLine($"FullscreenHitTarget {hwnd}: capture/mode cancellation message={message}");
#endif
                target._pressed = false;
                break;
            case WmEraseBackground:
                return new IntPtr(1);
            case WmPaint:
                _ = BeginPaint(hwnd, out var paint);
                _ = EndPaint(hwnd, ref paint);
                return IntPtr.Zero;
        }

        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    private void RefreshHoverFromCursor()
    {
        if (!_visible || Handle == IntPtr.Zero || !GetCursorPos(out var point))
        {
            return;
        }

        if (_bounds.Contains(point.X, point.Y))
        {
            BeginMouseLeaveTracking(Handle);
            SetHovered(true);
        }
        else
        {
            SetHovered(false);
        }
    }

    private void BeginMouseLeaveTracking(IntPtr hwnd)
    {
        if (_trackingMouseLeave)
        {
            return;
        }

        var tracking = new TrackMouseEventData
        {
            Size = (uint)Marshal.SizeOf<TrackMouseEventData>(),
            Flags = TmeLeave,
            Window = hwnd
        };
        _trackingMouseLeave = TrackMouseEvent(ref tracking);
    }

    private void SetHovered(bool hovered)
    {
        if (_hovered == hovered)
        {
            return;
        }

        _hovered = hovered;
#if DEBUG
        Debug.WriteLine($"FullscreenHitTarget {Handle}: hover={(hovered ? "enter" : "leave")}, bounds={_bounds}");
#endif
        if (hovered)
        {
            MouseEntered?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            MouseLeft?.Invoke(this, EventArgs.Empty);
        }
    }

#if DEBUG
    private static string GetCursorPositionForDebug() =>
        GetCursorPos(out var point) ? $"({point.X},{point.Y})" : "<unavailable>";
#endif

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WindowProcedure(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClassEx
    {
        public uint Size;
        public uint Style;
        [MarshalAs(UnmanagedType.FunctionPtr)]
        public WindowProcedure? WindowProcedure;
        public int ClassExtra;
        public int WindowExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? MenuName;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? ClassName;
        public IntPtr SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PaintStruct
    {
        public IntPtr DeviceContext;
        [MarshalAs(UnmanagedType.Bool)]
        public bool Erase;
        public NativeRect PaintRectangle;
        [MarshalAs(UnmanagedType.Bool)]
        public bool Restore;
        [MarshalAs(UnmanagedType.Bool)]
        public bool IncrementalUpdate;
        public uint Reserved0;
        public uint Reserved1;
        public uint Reserved2;
        public uint Reserved3;
        public uint Reserved4;
        public uint Reserved5;
        public uint Reserved6;
        public uint Reserved7;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrackMouseEventData
    {
        public uint Size;
        public uint Flags;
        public IntPtr Window;
        public uint HoverTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WindowClassEx windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr parameter);

    [DllImport("user32.dll", EntryPoint = "SetLayeredWindowAttributes", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint colorKey, byte alpha, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SetCapture(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TrackMouseEvent(ref TrackMouseEventData tracking);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll", EntryPoint = "LoadCursorW", SetLastError = true)]
    private static extern IntPtr LoadCursor(IntPtr instance, IntPtr cursorName);

    [DllImport("user32.dll")]
    private static extern IntPtr SetCursor(IntPtr cursor);

    [DllImport("user32.dll")]
    private static extern IntPtr GetCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr BeginPaint(IntPtr hwnd, out PaintStruct paint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EndPaint(IntPtr hwnd, ref PaintStruct paint);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
