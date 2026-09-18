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

    private static readonly TimeSpan ExpandDuration = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan CollapseDuration = TimeSpan.FromMilliseconds(240);
    private static readonly TimeSpan ContentResizeDuration = TimeSpan.FromMilliseconds(180);

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
    private long _dimensionTransitionVersion;
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

    public void SetPreferredSize(
        double compactWidth,
        double expandedWidth,
        double expandedHeight,
        bool applyCurrentState = true)
    {
        _compactWidth = Math.Clamp(compactWidth, MinCompactWidth, MaxCompactWidth);
        _expandedWidth = Math.Clamp(expandedWidth, MinExpandedWidth, MaxExpandedWidth);
        _expandedHeight = Math.Clamp(expandedHeight, MinExpandedHeight, MaxExpandedHeight);

        if (applyCurrentState)
        {
            Apply(_lastState);
        }
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
            CancelTransitionAndCommit(targetWidth, state == NotchState.Hidden ? CompactHeight : targetHeight);
            ApplyFallbackPosition(state);
            return;
        }

        if (state == NotchState.Hidden)
        {
            CancelTransitionAndCommit(targetWidth, CompactHeight);
            PositionNative(state);
            return;
        }

        var isStateMorph = previousState != state && previousState != NotchState.Hidden;
        var baseDuration = isStateMorph
            ? state is NotchState.Expanded or NotchState.Pinned ? ExpandDuration : CollapseDuration
            : ContentResizeDuration;
        var easing = CreateMorphEasing(state, isStateMorph);
        var animate = SystemParameters.ClientAreaAnimation && !_isSuppressed;

        StartDimensionTransition(targetWidth, targetHeight, baseDuration, easing, animate);
    }

    private void StartDimensionTransition(
        double targetWidth,
        double targetHeight,
        TimeSpan baseDuration,
        IEasingFunction easing,
        bool animate)
    {
        var currentWidth = GetEffectiveDimension(_window.ActualWidth, _window.Width, targetWidth);
        var currentHeight = GetEffectiveDimension(_window.ActualHeight, _window.Height, targetHeight);
        var transitionVersion = ++_dimensionTransitionVersion;

        CommitDimensions(currentWidth, currentHeight);

        if (!animate
            || (Math.Abs(currentWidth - targetWidth) <= 0.5
                && Math.Abs(currentHeight - targetHeight) <= 0.5))
        {
            CommitDimensions(targetWidth, targetHeight);
            PositionNative(_lastState);
            return;
        }

        var duration = ScaleDurationForRemainingDistance(
            baseDuration,
            currentWidth,
            currentHeight,
            targetWidth,
            targetHeight);

        var widthAnimation = new DoubleAnimation(currentWidth, targetWidth, new Duration(duration))
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.HoldEnd
        };
        var heightAnimation = new DoubleAnimation(currentHeight, targetHeight, new Duration(duration))
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.HoldEnd
        };

        heightAnimation.Completed += (_, _) =>
        {
            if (_disposed || transitionVersion != _dimensionTransitionVersion)
            {
                return;
            }

            CommitDimensions(targetWidth, targetHeight);
            PositionNative(_lastState);
        };

        _window.BeginAnimation(
            FrameworkElement.WidthProperty,
            widthAnimation,
            HandoffBehavior.SnapshotAndReplace);
        _window.BeginAnimation(
            FrameworkElement.HeightProperty,
            heightAnimation,
            HandoffBehavior.SnapshotAndReplace);
    }

    private static double GetEffectiveDimension(double actual, double configured, double fallback)
    {
        if (double.IsFinite(actual) && actual > 0)
        {
            return actual;
        }

        if (double.IsFinite(configured) && configured > 0)
        {
            return configured;
        }

        return fallback;
    }

    private TimeSpan ScaleDurationForRemainingDistance(
        TimeSpan baseDuration,
        double currentWidth,
        double currentHeight,
        double targetWidth,
        double targetHeight)
    {
        var fullWidthDistance = Math.Max(1, Math.Abs(_expandedWidth - _compactWidth));
        var fullHeightDistance = Math.Max(1, Math.Abs(_expandedHeight - CompactHeight));
        var widthFraction = Math.Min(1, Math.Abs(targetWidth - currentWidth) / fullWidthDistance);
        var heightFraction = Math.Min(1, Math.Abs(targetHeight - currentHeight) / fullHeightDistance);
        var remainingFraction = Math.Max(widthFraction, heightFraction);

        // Interrupted reversals should keep moving instead of restarting a full-length transition.
        var scale = 0.48 + (0.52 * Math.Sqrt(Math.Max(0.05, remainingFraction)));
        return TimeSpan.FromMilliseconds(Math.Clamp(baseDuration.TotalMilliseconds * scale, 105, baseDuration.TotalMilliseconds));
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

    private void CancelTransitionAndCommit(double width, double height)
    {
        ++_dimensionTransitionVersion;
        SetBaseDimensions(width, height);
    }

    private void SetBaseDimensions(double width, double height)
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
        ++_dimensionTransitionVersion;
        _window.BeginAnimation(FrameworkElement.WidthProperty, null);
        _window.BeginAnimation(FrameworkElement.HeightProperty, null);
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
