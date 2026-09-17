using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using NotchBar.Core;

namespace NotchBar.Services;

public sealed class WindowController : IDisposable
{
    public const double DefaultWindowWidth = 424;
    public const double CompactHeight = 44;
    public const double DefaultExpandedHeight = 230;
    public const double HiddenTriggerHeight = 2;

    private static readonly Duration ExpandDuration = new(TimeSpan.FromMilliseconds(280));
    private static readonly Duration CollapseDuration = new(TimeSpan.FromMilliseconds(220));
    private static readonly Duration ContentResizeDuration = new(TimeSpan.FromMilliseconds(180));

    private const double MinCompactWidth = 286;
    private const double MaxCompactWidth = 520;
    private const double MinExpandedWidth = 360;
    private const double MaxExpandedWidth = 560;
    private const double MinExpandedHeight = 148;
    private const double MaxExpandedHeight = 300;

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    private readonly Window _window;
    private MonitorTarget _monitor;
    private IntPtr _handle;
    private NotchState _lastState = NotchState.Hidden;
    private double _compactWidth = DefaultWindowWidth;
    private double _expandedWidth = DefaultWindowWidth;
    private double _expandedHeight = DefaultExpandedHeight;
    private bool _needsDpiHandshake = true;
    private bool _isSuppressed;
    private bool _disposed;

    public WindowController(Window window, MonitorTarget monitor)
    {
        _window = window;
        _monitor = monitor;
        _window.Width = _compactWidth;
        _window.Height = CompactHeight;
        _window.SizeChanged += Window_OnSizeChanged;
        ApplyFallbackPosition(NotchState.Hidden);
    }

    public void Attach()
    {
        if (_handle != IntPtr.Zero)
        {
            return;
        }

        _handle = new WindowInteropHelper(_window).EnsureHandle();
        _needsDpiHandshake = true;
        Apply(_lastState);
    }

    public void SetMonitor(MonitorTarget monitor)
    {
        _monitor = monitor;
        _needsDpiHandshake = true;
        Apply(_lastState);
    }

    public void SetPreferredSize(double compactWidth, double expandedWidth, double expandedHeight)
    {
        _compactWidth = Math.Clamp(compactWidth, MinCompactWidth, MaxCompactWidth);
        _expandedWidth = Math.Clamp(expandedWidth, MinExpandedWidth, MaxExpandedWidth);
        _expandedHeight = Math.Clamp(expandedHeight, MinExpandedHeight, MaxExpandedHeight);
        Apply(_lastState);
    }

    public void SetSuppressed(bool suppressed)
    {
        _isSuppressed = suppressed;
    }

    public void Apply(NotchState state)
    {
        var previousState = _lastState;
        _lastState = state;

        var targetWidth = GetTargetWidth(state);
        var targetHeight = GetTargetHeight(state);

        if (_handle == IntPtr.Zero)
        {
            SetDimensionsImmediate(targetWidth, state == NotchState.Hidden ? CompactHeight : targetHeight);
            ApplyFallbackPosition(state);
            return;
        }

        if (state == NotchState.Hidden)
        {
            SetDimensionsImmediate(targetWidth, CompactHeight);
            PositionNative(state);
            return;
        }

        var isStateMorph = previousState != state && previousState != NotchState.Hidden;
        var duration = isStateMorph
            ? state is NotchState.Expanded or NotchState.Pinned ? ExpandDuration : CollapseDuration
            : ContentResizeDuration;
        var easing = CreateMorphEasing(state, isStateMorph);
        var animate = SystemParameters.ClientAreaAnimation && !_isSuppressed;

        AnimateDimension(FrameworkElement.WidthProperty, _window.ActualWidth, targetWidth, duration, easing, animate);
        AnimateDimension(FrameworkElement.HeightProperty, _window.ActualHeight, targetHeight, duration, easing, animate);
        PositionNative(state);
    }

    private static IEasingFunction CreateMorphEasing(NotchState state, bool isStateMorph)
    {
        if (!isStateMorph)
        {
            return new CubicEase { EasingMode = EasingMode.EaseOut };
        }

        return state is NotchState.Expanded or NotchState.Pinned
            ? new QuinticEase { EasingMode = EasingMode.EaseOut }
            : new CubicEase { EasingMode = EasingMode.EaseInOut };
    }

    private void AnimateDimension(
        DependencyProperty property,
        double current,
        double target,
        Duration duration,
        IEasingFunction easing,
        bool animate)
    {
        if (!double.IsFinite(current) || current <= 0)
        {
            current = target;
        }

        _window.BeginAnimation(property, null);
        _window.SetValue(property, current);

        if (!animate || Math.Abs(current - target) <= 0.5)
        {
            _window.SetValue(property, target);
            return;
        }

        var animation = new DoubleAnimation(current, target, duration)
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.Stop
        };

        _window.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
        _window.SetValue(property, target);
    }

    private void SetDimensionsImmediate(double width, double height)
    {
        _window.BeginAnimation(FrameworkElement.WidthProperty, null);
        _window.BeginAnimation(FrameworkElement.HeightProperty, null);
        _window.Width = width;
        _window.Height = height;
    }

    private double GetTargetWidth(NotchState state) =>
        state is NotchState.Expanded or NotchState.Pinned ? _expandedWidth : _compactWidth;

    private double GetTargetHeight(NotchState state) =>
        state is NotchState.Expanded or NotchState.Pinned ? _expandedHeight : CompactHeight;

    private void Window_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_disposed || _handle == IntPtr.Zero)
        {
            return;
        }

        PositionNative(_lastState);
    }

    private void PositionNative(NotchState state)
    {
        var bounds = _monitor.Bounds;

        if (_needsDpiHandshake)
        {
            _ = SetWindowPos(
                _handle,
                IntPtr.Zero,
                bounds.Left,
                bounds.Top,
                0,
                0,
                SwpNoSize | SwpNoZOrder | SwpNoActivate);
            _needsDpiHandshake = false;
        }

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
        var targetWidth = GetTargetWidth(state);
        var targetHeight = GetTargetHeight(state);
        _window.Left = Math.Max(0, (SystemParameters.PrimaryScreenWidth - targetWidth) / 2);
        _window.Top = _isSuppressed
            ? -targetHeight
            : state == NotchState.Hidden
                ? -(CompactHeight - HiddenTriggerHeight)
                : 0;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _window.SizeChanged -= Window_OnSizeChanged;
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
