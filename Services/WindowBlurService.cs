using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace NotchBar.Services;

/// <summary>
/// Applies a bounded DWM blur region while leaving the visible rounded
/// silhouette to WPF, whose vector rendering provides smoother anti-aliased
/// corners than a pixel-based window region.
/// </summary>
public sealed class WindowBlurService : IDisposable
{
    private const uint DwmBbEnable = 0x00000001;
    private const uint DwmBbBlurRegion = 0x00000002;
    private const uint DwmNcRenderingPolicy = 6;
    private const uint DwmWindowCornerPreference = 33;
    private const int DwmNcRenderingDisabled = 2;
    private const int DwmCornerDoNotRound = 1;
    private const int RegionOr = 2;

    private readonly Window _window;
    private IntPtr _hwnd;
    private IntPtr _blurRegion;
    private DispatcherOperation? _pendingRegionUpdate;
    private int _appliedClientWidth = -1;
    private int _appliedClientHeight = -1;
    private uint _appliedDpi;
    private bool _isApplied;
    private bool _disposed;

    public WindowBlurService(Window window)
    {
        _window = window;
    }

    public bool TryApply()
    {
        if (_disposed || !OperatingSystem.IsWindows() || SystemParameters.HighContrast)
        {
            return false;
        }

        if (_isApplied)
        {
            QueueBackdropRegionUpdate();
            return true;
        }

        try
        {
            _hwnd = new WindowInteropHelper(_window).Handle;
            if (_hwnd == IntPtr.Zero)
            {
                return false;
            }

            SetWindowAttribute(DwmNcRenderingPolicy, DwmNcRenderingDisabled);
            SetWindowAttribute(DwmWindowCornerPreference, DwmCornerDoNotRound);

            // Apply the initial region synchronously so the first presented
            // frame already has the correct glass silhouette.
            UpdateBackdropRegion();
            _window.SizeChanged += Window_OnSizeChanged;
            _window.DpiChanged += Window_OnDpiChanged;
            _isApplied = true;
            return true;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    private void Window_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        QueueBackdropRegionUpdate();
    }

    private void Window_OnDpiChanged(object sender, System.Windows.DpiChangedEventArgs e)
    {
        QueueBackdropRegionUpdate();
    }

    private void QueueBackdropRegionUpdate()
    {
        if (_disposed || _hwnd == IntPtr.Zero || _pendingRegionUpdate is not null)
        {
            return;
        }

        // Window size animation can produce several SizeChanged notifications
        // in one dispatcher pass. Rebuild the native region only once, just
        // before WPF renders the resulting size.
        _pendingRegionUpdate = _window.Dispatcher.BeginInvoke(
            DispatcherPriority.Render,
            new Action(ProcessPendingBackdropRegionUpdate));
    }

    private void ProcessPendingBackdropRegionUpdate()
    {
        _pendingRegionUpdate = null;
        if (_disposed)
        {
            return;
        }

        try
        {
            UpdateBackdropRegion();
        }
        catch (DllNotFoundException)
        {
            // DWM is optional. Keep the WPF glass fallback rather than
            // allowing a late resize callback to tear down the UI thread.
        }
        catch (EntryPointNotFoundException)
        {
            // Older Windows builds may not expose every API used here.
        }
    }

    private void UpdateBackdropRegion()
    {
        if (_hwnd == IntPtr.Zero || _disposed)
        {
            return;
        }

        if (!GetClientRect(_hwnd, out var clientRect))
        {
            return;
        }

        var width = Math.Max(1, clientRect.Right - clientRect.Left);
        var height = Math.Max(1, clientRect.Bottom - clientRect.Top);
        var dpi = GetDpi(_hwnd);
        if (width == _appliedClientWidth && height == _appliedClientHeight && dpi == _appliedDpi)
        {
            return;
        }

        var radius = Math.Clamp((int)Math.Round(20d * dpi / 96d), 1, Math.Min(width, height) / 2);

        var replacementRegion = CreateIslandRegion(width, height, radius);
        if (replacementRegion == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var blurBehind = new DwmBlurBehind
            {
                Flags = DwmBbEnable | DwmBbBlurRegion,
                Enable = true,
                BlurRegion = replacementRegion,
                TransitionOnMaximized = false
            };

            if (DwmEnableBlurBehindWindow(_hwnd, ref blurBehind) != 0)
            {
                return;
            }

            // DWM accepted the replacement before the previous HRGN is
            // released. This avoids a one-frame unblurred rectangle while a
            // transparent window is being resized.
            var previousRegion = _blurRegion;
            _blurRegion = replacementRegion;
            replacementRegion = IntPtr.Zero;
            _appliedClientWidth = width;
            _appliedClientHeight = height;
            _appliedDpi = dpi;

            if (previousRegion != IntPtr.Zero)
            {
                DeleteObject(previousRegion);
            }
        }
        finally
        {
            if (replacementRegion != IntPtr.Zero)
            {
                DeleteObject(replacementRegion);
            }
        }
    }

    private static IntPtr CreateIslandRegion(int width, int height, int radius)
    {
        var rounded = CreateRoundRectRgn(0, 0, width + 1, height + 1, radius * 2, radius * 2);
        if (rounded == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        // The island has rounded bottom corners but a square top edge that
        // meets the top of the screen when it is visible.
        var topHeight = Math.Min(radius, height);
        var top = CreateRectRgn(0, 0, width + 1, topHeight + 1);
        if (top != IntPtr.Zero)
        {
            CombineRgn(rounded, rounded, top, RegionOr);
            DeleteObject(top);
        }

        return rounded;
    }

    private void SetWindowAttribute(uint attribute, int value)
    {
        _ = DwmSetWindowAttribute(_hwnd, attribute, ref value, sizeof(int));
    }

    private void DisableBlurRegion()
    {
        var blurRegion = _blurRegion;
        _blurRegion = IntPtr.Zero;
        _appliedClientWidth = -1;
        _appliedClientHeight = -1;
        _appliedDpi = 0;

        if (blurRegion == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var blurBehind = new DwmBlurBehind
            {
                Flags = DwmBbEnable,
                Enable = false,
                BlurRegion = IntPtr.Zero,
                TransitionOnMaximized = false
            };

            if (_hwnd != IntPtr.Zero)
            {
                _ = DwmEnableBlurBehindWindow(_hwnd, ref blurBehind);
            }
        }
        finally
        {
            DeleteObject(blurRegion);
        }
    }

    private static uint GetDpi(IntPtr hwnd)
    {
        try
        {
            var dpi = GetDpiForWindow(hwnd);
            return dpi == 0 ? 96u : dpi;
        }
        catch (EntryPointNotFoundException)
        {
            return 96u;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _pendingRegionUpdate?.Abort();
        _pendingRegionUpdate = null;
        _window.SizeChanged -= Window_OnSizeChanged;
        _window.DpiChanged -= Window_OnDpiChanged;
        _isApplied = false;
        DisableBlurRegion();
        GC.SuppressFinalize(this);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DwmBlurBehind
    {
        public uint Flags;

        [MarshalAs(UnmanagedType.Bool)]
        public bool Enable;

        public IntPtr BlurRegion;

        [MarshalAs(UnmanagedType.Bool)]
        public bool TransitionOnMaximized;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmEnableBlurBehindWindow(IntPtr hwnd, ref DwmBlurBehind blurBehind);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, uint attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    private static extern int CombineRgn(IntPtr destination, IntPtr source1, IntPtr source2, int mode);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr objectHandle);
}
