using System.ComponentModel;
using System.Runtime.InteropServices;

namespace NotchBar.Services;

/// <summary>
/// An unowned, non-activating composition surface that always sits immediately
/// behind the WPF island. It is created non-layered so HostBackdrop can commit,
/// then becomes a verified layered-transparent window before the first show. It
/// has no non-client rendering, class brush, region, or shadow.
/// </summary>
public sealed class BackdropHostWindow : IDisposable
{
    private const string ClassName = "NotchBar.CompositionBackdropHost";
    private const uint WmNcHitTest = 0x0084;
    private const uint WmMouseActivate = 0x0021;
    private const uint WmEraseBackground = 0x0014;
    private const uint WmNcPaint = 0x0085;
    private const uint WmPaint = 0x000F;
    private const int HtTransparent = -1;
    private const int MaNoActivate = 3;
    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const uint GwOwner = 4;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpFrameChanged = 0x0020;
    private const uint SwpShowWindow = 0x0040;
    private const int SwHide = 0;
    private const uint DwmNcRenderingPolicy = 6;
    private const uint DwmWindowCornerPreference = 33;
    private const int DwmNcRenderingDisabled = 2;
    private const int DwmCornerDoNotRound = 1;

    private static readonly object RegistrationLock = new();
    private static readonly WndProc WindowProcedure = WindowProc;
    private static ushort _classAtom;

    private bool _disposed;

    public BackdropHostWindow()
    {
        EnsureClassRegistered();
        Handle = CreateWindowEx(
            BackdropWindowPolicy.ExtendedWindowStyle,
            ClassName,
            string.Empty,
            BackdropWindowPolicy.WindowStyle,
            0,
            0,
            1,
            1,
            IntPtr.Zero,
            IntPtr.Zero,
            GetModuleHandle(null),
            IntPtr.Zero);
        if (Handle == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The composition backdrop window could not be created.");
        }

        var ncPolicy = DwmNcRenderingDisabled;
        if (DwmSetWindowAttribute(Handle, DwmNcRenderingPolicy, ref ncPolicy, sizeof(int)) != 0)
        {
            Dispose();
            throw new InvalidOperationException("DWM non-client rendering could not be disabled for the backdrop host.");
        }

        var corners = DwmCornerDoNotRound;
        if (Environment.OSVersion.Version.Build >= 22000 &&
            DwmSetWindowAttribute(Handle, DwmWindowCornerPreference, ref corners, sizeof(int)) != 0)
        {
            Dispose();
            throw new InvalidOperationException("DWM rounded-window policy could not be disabled for the backdrop host.");
        }

        if (!HasExpectedNativeContract())
        {
            Dispose();
            throw new InvalidOperationException("The composition backdrop window did not retain its required unowned, non-layered styles.");
        }
    }

    public IntPtr Handle { get; private set; }

    public bool HasExpectedNativeContract()
    {
        if (Handle == IntPtr.Zero)
        {
            return false;
        }

        var style = unchecked((uint)GetWindowLongPtr(Handle, GwlStyle).ToInt64());
        var exStyle = unchecked((uint)GetWindowLongPtr(Handle, GwlExStyle).ToInt64());
        const uint layered = 0x00080000;
        return (style & BackdropWindowPolicy.WindowStyle) == BackdropWindowPolicy.WindowStyle &&
            (exStyle & BackdropWindowPolicy.ExtendedWindowStyle) == BackdropWindowPolicy.ExtendedWindowStyle &&
            (exStyle & layered) == 0 &&
            GetWindow(Handle, GwOwner) == IntPtr.Zero;
    }

    public bool HasTransparentInputContract()
    {
        if (Handle == IntPtr.Zero)
        {
            return false;
        }

        var exStyle = unchecked((uint)GetWindowLongPtr(Handle, GwlExStyle).ToInt64());
        return BackdropWindowPolicy.HasTransparentInputContract(exStyle) &&
            GetWindow(Handle, GwOwner) == IntPtr.Zero;
    }
    public bool EnableLayeredTransparency()
    {
        if (_disposed || Handle == IntPtr.Zero)
        {
            return false;
        }

        Marshal.SetLastPInvokeError(0);
        var current = GetWindowLongPtr(Handle, GwlExStyle);
        var previous = SetWindowLongPtr(
            Handle,
            GwlExStyle,
            new IntPtr(current.ToInt64() | BackdropWindowPolicy.ActiveExtendedWindowStyle));
        if (previous == IntPtr.Zero && Marshal.GetLastPInvokeError() != 0)
        {
            return false;
        }

        var frameChanged = SetWindowPos(
            Handle,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            SwpNoSize | SwpNoMove | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
        return frameChanged && HasTransparentInputContract();
    }

    public void Hide()
    {
        if (Handle != IntPtr.Zero)
        {
            _ = ShowWindow(Handle, SwHide);
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
        if (handle != IntPtr.Zero)
        {
            _ = ShowWindow(handle, SwHide);
            _ = DestroyWindow(handle);
        }

        GC.SuppressFinalize(this);
    }

    private static void EnsureClassRegistered()
    {
        if (_classAtom != 0)
        {
            return;
        }

        lock (RegistrationLock)
        {
            if (_classAtom != 0)
            {
                return;
            }

            var windowClass = new WindowClassEx
            {
                Size = (uint)Marshal.SizeOf<WindowClassEx>(),
                WindowProcedure = WindowProcedure,
                Instance = GetModuleHandle(null),
                ClassName = ClassName
            };
            _classAtom = RegisterClassEx(ref windowClass);
            if (_classAtom == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "The composition backdrop window class could not be registered.");
            }
        }
    }

    private static IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case WmNcHitTest:
                return new IntPtr(HtTransparent);
            case WmMouseActivate:
                return new IntPtr(MaNoActivate);
            case WmEraseBackground:
                return new IntPtr(1);
            case WmNcPaint:
                return IntPtr.Zero;
            case WmPaint:
                _ = BeginPaint(hwnd, out var paint);
                _ = EndPaint(hwnd, ref paint);
                return IntPtr.Zero;
            default:
                return DefWindowProc(hwnd, message, wParam, lParam);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClassEx
    {
        public uint Size;
        public uint Style;
        public WndProc WindowProcedure;
        public int ClassExtra;
        public int WindowExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public string? MenuName;
        public string ClassName;
        public IntPtr SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PaintStruct
    {
        public IntPtr DeviceContext;
        [MarshalAs(UnmanagedType.Bool)] public bool Erase;
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
        [MarshalAs(UnmanagedType.Bool)] public bool Restore;
        [MarshalAs(UnmanagedType.Bool)] public bool IncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] Reserved;
    }

    private delegate IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WindowClassEx windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint command);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    private static extern IntPtr BeginPaint(IntPtr hwnd, out PaintStruct paint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EndPaint(IntPtr hwnd, ref PaintStruct paint);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, uint attribute, ref int value, int size);
}
