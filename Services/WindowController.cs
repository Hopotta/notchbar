using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using NotchBar.Core;

namespace NotchBar.Services;

public sealed class WindowController : IDisposable
{
    public const double DefaultWindowWidth = 424;
    public const double CompactHeight = 37;
    public const double DefaultExpandedHeight = 230;
    public const double HiddenTriggerHeight = 2;


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
    private readonly SpringMotion _widthMotion = new(response: 0.34);
    private readonly SpringMotion _heightMotion = new(response: 0.34);
    private bool _dimensionTransitionActive;
    private DateTime _dimensionTransitionLastFrame;
    private readonly SpringMotion _positionXMotion = new(response: 0.28);
    private readonly SpringMotion _positionYMotion = new(response: 0.28);
    private DateTime _positionTransitionLastFrame;
    private int _positionWidth;
    private int _positionHeight;
    private bool _positionTransitionActive;
    private bool _deferNativePosition;
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
        StopPositionTransition();
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

        var animatePosition = SystemParameters.ClientAreaAnimation && !_isSuppressed;

        if (state == NotchState.Hidden)
        {
            var shouldAnimateOut = animatePosition && previousState != NotchState.Hidden;
            _deferNativePosition = shouldAnimateOut;
            CancelTransitionAndCommit(targetWidth, CompactHeight);
            _deferNativePosition = false;

            if (shouldAnimateOut)
            {
                StartPositionTransition(NotchState.Hidden);
            }
            else
            {
                StopPositionTransition();
                PositionNative(state);
            }

            return;
        }

        if (previousState == NotchState.Hidden && animatePosition)
        {
            var currentWidth = GetEffectiveDimension(_window.ActualWidth, _window.Width, targetWidth);
            var currentHeight = GetEffectiveDimension(_window.ActualHeight, _window.Height, targetHeight);
            if (Math.Abs(currentWidth - targetWidth) <= 0.5
                && Math.Abs(currentHeight - targetHeight) <= 0.5)
            {
                StartPositionTransition(state);
                return;
            }
        }

        var animate = SystemParameters.ClientAreaAnimation && !_isSuppressed;
        StartDimensionTransition(targetWidth, targetHeight, animate);
    }

    private void StartPositionTransition(NotchState state)
    {
        if (_handle == IntPtr.Zero || _needsDpiHandshake || !GetWindowRect(_handle, out var rect))
        {
            PositionNative(state);
            return;
        }

        var dpi = GetDpiForWindow(_handle);
        if (dpi == 0)
        {
            dpi = 96;
        }

        var bounds = _monitor.Bounds;
        var compactPixels = Math.Max(1, (int)Math.Round(CompactHeight * dpi / 96d));
        var triggerPixels = Math.Max(1, (int)Math.Round(HiddenTriggerHeight * dpi / 96d));
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        var targetX = bounds.Left + Math.Max(0, (bounds.Width - width) / 2);
        var targetY = state == NotchState.Hidden
            ? bounds.Top - (compactPixels - triggerPixels)
            : bounds.Top;

        if (!_positionTransitionActive)
        {
            _positionXMotion.SetImmediate(rect.Left);
            _positionYMotion.SetImmediate(rect.Top);
            _positionWidth = width;
            _positionHeight = height;
        }

        _positionXMotion.SetTarget(targetX);
        _positionYMotion.SetTarget(targetY);
        _positionTransitionLastFrame = DateTime.UtcNow;
        _positionXMotion.Response = state == NotchState.Hidden ? 0.30 : 0.28;
        _positionYMotion.Response = state == NotchState.Hidden ? 0.30 : 0.28;
        _positionTransitionActive = true;

        CompositionTarget.Rendering -= PositionTransition_OnRendering;
        CompositionTarget.Rendering += PositionTransition_OnRendering;
    }

    private void PositionTransition_OnRendering(object? sender, EventArgs e)
    {
        if (!_positionTransitionActive || _disposed || _handle == IntPtr.Zero)
        {
            StopPositionTransition();
            return;
        }

        var now = DateTime.UtcNow;
        var elapsed = now - _positionTransitionLastFrame;
        _positionTransitionLastFrame = now;
        _positionXMotion.Step(elapsed);
        _positionYMotion.Step(elapsed);

        _ = SetWindowPos(
            _handle,
            IntPtr.Zero,
            (int)Math.Round(_positionXMotion.Value),
            (int)Math.Round(_positionYMotion.Value),
            _positionWidth,
            _positionHeight,
            SwpNoZOrder | SwpNoActivate);

        if (!_positionXMotion.IsSettled || !_positionYMotion.IsSettled)
        {
            return;
        }

        StopPositionTransition();
        PositionNative(_lastState);
    }
    private void StopPositionTransition()
    {
        if (_positionTransitionActive)
        {
            _positionTransitionActive = false;
        }

        CompositionTarget.Rendering -= PositionTransition_OnRendering;
    }
    private void StartDimensionTransition(double targetWidth, double targetHeight, bool animate)
    {
        var currentWidth = _dimensionTransitionActive
            ? _widthMotion.Value
            : GetEffectiveDimension(_window.ActualWidth, _window.Width, targetWidth);
        var currentHeight = _dimensionTransitionActive
            ? _heightMotion.Value
            : GetEffectiveDimension(_window.ActualHeight, _window.Height, targetHeight);

        if (!animate
            || (Math.Abs(currentWidth - targetWidth) <= 0.5
                && Math.Abs(currentHeight - targetHeight) <= 0.5))
        {
            StopDimensionTransition();
            SetBaseDimensions(targetWidth, targetHeight);
            PositionNative(_lastState);
            return;
        }

        if (!_dimensionTransitionActive)
        {
            _widthMotion.SetImmediate(currentWidth);
            _heightMotion.SetImmediate(currentHeight);
        }

        _widthMotion.SetTarget(targetWidth);
        _heightMotion.SetTarget(targetHeight);
        _dimensionTransitionActive = true;
        _dimensionTransitionLastFrame = DateTime.UtcNow;
        CompositionTarget.Rendering -= DimensionTransition_OnRendering;
        CompositionTarget.Rendering += DimensionTransition_OnRendering;
    }

    private void DimensionTransition_OnRendering(object? sender, EventArgs e)
    {
        if (_disposed || !_dimensionTransitionActive)
        {
            StopDimensionTransition();
            return;
        }

        var now = DateTime.UtcNow;
        var elapsed = now - _dimensionTransitionLastFrame;
        _dimensionTransitionLastFrame = now;
        _widthMotion.Step(elapsed);
        _heightMotion.Step(elapsed);

        _deferNativePosition = true;
        _window.Width = _widthMotion.Value;
        _window.Height = _heightMotion.Value;
        _deferNativePosition = false;
        PositionNative(_lastState);

        if (!_widthMotion.IsSettled || !_heightMotion.IsSettled)
        {
            return;
        }

        var targetWidth = _widthMotion.Target;
        var targetHeight = _heightMotion.Target;
        StopDimensionTransition();
        SetBaseDimensions(targetWidth, targetHeight);
        PositionNative(_lastState);
    }

    private void StopDimensionTransition()
    {
        _dimensionTransitionActive = false;
        CompositionTarget.Rendering -= DimensionTransition_OnRendering;
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

    private void CancelTransitionAndCommit(double width, double height)
    {
        StopDimensionTransition();
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
        if (_disposed || _handle == IntPtr.Zero || _deferNativePosition || _positionTransitionActive || _dimensionTransitionActive)
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
        StopPositionTransition();
        StopDimensionTransition();
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
