using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using NotchBar.Core;

namespace NotchBar.Services;

public sealed class WindowController
{
    public const double WindowWidth = 424;
    public const double CompactHeight = 44;
    public const double ExpandedHeight = 230;
    public const double HiddenTriggerHeight = 2;

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    private readonly Window _window;
    private MonitorTarget _monitor;
    private IntPtr _handle;
    private NotchState _lastState = NotchState.Hidden;
    private bool _isSuppressed;

    public WindowController(Window window, MonitorTarget monitor)
    {
        _window = window;
        _monitor = monitor;
        _window.Width = WindowWidth;
        _window.Height = CompactHeight;
        ApplyFallbackPosition(NotchState.Hidden);
    }

    public void Attach()
    {
        if (_handle != IntPtr.Zero)
        {
            return;
        }

        _handle = new WindowInteropHelper(_window).EnsureHandle();
        Apply(_lastState);
    }

    public void SetMonitor(MonitorTarget monitor)
    {
        _monitor = monitor;
        Apply(_lastState);
    }

    public void SetSuppressed(bool suppressed)
    {
        _isSuppressed = suppressed;
    }

    public void Apply(NotchState state)
    {
        _lastState = state;
        var targetHeight = state is NotchState.Expanded or NotchState.Pinned ? ExpandedHeight : CompactHeight;

        if (state == NotchState.Hidden)
        {
            _window.BeginAnimation(FrameworkElement.HeightProperty, null);
            _window.Height = CompactHeight;
        }
        else
        {
            _window.BeginAnimation(FrameworkElement.HeightProperty, new DoubleAnimation(targetHeight, new Duration(TimeSpan.FromMilliseconds(180)))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            });
        }

        if (_handle == IntPtr.Zero)
        {
            ApplyFallbackPosition(state);
            return;
        }

        PositionNative(state);
    }

    private void PositionNative(NotchState state)
    {
        var bounds = _monitor.Bounds;

        // Move first so PerMonitorV2 can update the HWND DPI before centering.
        _ = SetWindowPos(
            _handle,
            IntPtr.Zero,
            bounds.Left,
            bounds.Top,
            0,
            0,
            SwpNoSize | SwpNoZOrder | SwpNoActivate);

        if (!GetWindowRect(_handle, out var rect))
        {
            return;
        }

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        var dpi = GetDpiForWindow(_handle);
        if (dpi == 0)
        {
            dpi = 96;
        }

        var triggerPixels = Math.Max(1, (int)Math.Round(HiddenTriggerHeight * dpi / 96d));
        var x = bounds.Left + Math.Max(0, (bounds.Width - width) / 2);
        int y;
        var flags = SwpNoZOrder | SwpNoActivate | SwpNoSize;

        if (_isSuppressed)
        {
            y = bounds.Top - height;
        }
        else if (state == NotchState.Hidden)
        {
            var compactPixels = Math.Max(1, (int)Math.Round(CompactHeight * dpi / 96d));
            y = bounds.Top - (compactPixels - triggerPixels);
            flags = SwpNoZOrder | SwpNoActivate;
            height = compactPixels;
        }
        else
        {
            y = bounds.Top;
        }

        _ = SetWindowPos(_handle, IntPtr.Zero, x, y, width, height, flags);
    }

    private void ApplyFallbackPosition(NotchState state)
    {
        var targetHeight = state is NotchState.Expanded or NotchState.Pinned ? ExpandedHeight : CompactHeight;
        _window.Left = Math.Max(0, (SystemParameters.PrimaryScreenWidth - WindowWidth) / 2);
        _window.Top = _isSuppressed
            ? -targetHeight
            : state == NotchState.Hidden
                ? -(targetHeight - HiddenTriggerHeight)
                : 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);
}
