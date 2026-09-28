using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
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
    public const double FullscreenBadgeWidth = 32;
    public const double DefaultExpandedHeight = 230;
    public const double HiddenTriggerHeight = 2;
    private const double MinCompactWidth = 286;
    private const double MaxCompactWidth = 520;
    private const double MinExpandedWidth = 360;
    private const double MaxExpandedWidth = 560;
    private const double MinExpandedHeight = 118;
    private const double MaxExpandedHeight = 300;

    public const double EnvelopeWidth = MaxExpandedWidth;
    public const double EnvelopeHeight = MaxExpandedHeight + CompactHeight - HiddenTriggerHeight;

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpFrameChanged = 0x0020;
    private const uint SwpShowWindow = 0x0040;
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x00000020L;
    private const int SwHide = 0;
    private const int WmNcHitTest = 0x0084;
    private const int HtTransparent = -1;
    private static readonly IntPtr HwndTopmost = new(-1);

    private readonly Window _window;
    private readonly WindowTransitionMotion _motion = new();
    private MonitorTarget _monitor;
    private IntPtr _handle;
    private TimeSpan? _lastRenderingTime;
    private bool _transitionActive;
    private bool _committingGeometry;
    private bool _needsDpiHandshake = true;
    private bool _isSuppressed;
    private bool _fullscreenBadgeActive;
    private bool _fullscreenBadgeContentVisible = true;
    private bool _fullscreenPassthrough;
    private NotchState _requestedState = NotchState.Hidden;
    private IntPtr _companionHandle;
    private Action<WindowEnvelopeGeometry, uint>? _companionGeometryChanged;
    private Action? _companionCommitFailed;
    private FullscreenPinHitTarget? _fullscreenPinHitTarget;
    private Rectangle? _fullscreenPinHitBounds;
    private FullscreenPinHitTarget? _fullscreenWakeHitTarget;
    private Rectangle? _fullscreenWakeHitBounds;
    private bool _companionActive;
    private WindowPixelGeometry? _lastCommittedGeometry;
    private bool _lastCommitIncludedCompanion;
    private IntPtr _lastCommittedCompanionHandle;
    private WindowEnvelopeGeometry _currentGeometry;
    private uint _currentDpi = 96;
    private HwndSource? _source;
    private bool _disposed;

    public WindowController(Window window, MonitorTarget monitor)
    {
        _window = window;
        _monitor = monitor;
        _window.Width = EnvelopeWidth;
        _window.Height = EnvelopeHeight;
        _window.SizeChanged += Window_OnSizeChanged;
        _currentGeometry = WindowEnvelopeGeometry.Calculate(
            _monitor.Bounds,
            _currentDpi,
            _motion.Current,
            isSuppressed: false);
        ApplyEnvelopeWpfSize(_currentGeometry, _currentDpi);
        ApplyFallbackPosition();
    }

    public event EventHandler<WindowMotionFrameChangedEventArgs>? MotionFrameChanged;

    public event EventHandler? FullscreenPinClicked;

    public event EventHandler? FullscreenPinMouseEntered;

    public event EventHandler? FullscreenPinMouseLeft;

    public event EventHandler? FullscreenWakeMouseEntered;

    public event EventHandler? FullscreenWakeMouseLeft;

    public WindowMotionFrame CurrentFrame => _motion.Current;

    public WindowEnvelopeGeometry CurrentGeometry => _currentGeometry;

    public uint CurrentDpi => _currentDpi;

    public bool IsTransitionActive => _transitionActive;

    public bool RegisterCompanion(
        IntPtr handle,
        Action<WindowEnvelopeGeometry, uint> geometryChanged,
        Action commitFailed)
    {
        if (_disposed || handle == IntPtr.Zero)
        {
            return false;
        }

        _companionHandle = handle;
        _companionGeometryChanged = geometryChanged;
        _companionCommitFailed = commitFailed;
        _companionActive = true;
        InvalidateCommittedGeometry();
        CommitFrame(_motion.Current);
        return _companionHandle != IntPtr.Zero && _companionActive;
    }

    public void UnregisterCompanion()
    {
        InvalidateCommittedGeometry();
        if (_companionHandle != IntPtr.Zero)
        {
            _ = ShowWindow(_companionHandle, SwHide);
        }

        _companionActive = false;
        _companionHandle = IntPtr.Zero;
        _companionGeometryChanged = null;
        _companionCommitFailed = null;
    }

    public void SetCompanionActive(bool active)
    {
        var companionActive = active && _companionHandle != IntPtr.Zero;
        if (_companionActive != companionActive)
        {
            InvalidateCommittedGeometry();
        }

        _companionActive = companionActive;
        CommitFrame(_motion.Current);
    }

    public void Attach()
    {
        if (_handle != IntPtr.Zero || _disposed)
        {
            return;
        }

        _handle = new WindowInteropHelper(_window).EnsureHandle();
        _source = HwndSource.FromHwnd(_handle);
        _source?.AddHook(WindowMessageHook);
        ApplyFullscreenPassthroughStyle();
        try
        {
            _fullscreenPinHitTarget = new FullscreenPinHitTarget(_handle);
            _fullscreenPinHitTarget.Clicked += FullscreenPinHitTarget_OnClicked;
            _fullscreenPinHitTarget.MouseEntered += FullscreenPinHitTarget_OnMouseEntered;
            _fullscreenPinHitTarget.MouseLeft += FullscreenPinHitTarget_OnMouseLeft;
        }
        catch (Win32Exception exception)
        {
            Debug.WriteLine($"NotchBar fullscreen pin hit target is unavailable: {exception.Message}");
        }
        try
        {
            _fullscreenWakeHitTarget = new FullscreenPinHitTarget(
                _handle,
                useHandCursor: false,
                useRawMouseInput: true);
            _fullscreenWakeHitTarget.MouseEntered += FullscreenWakeHitTarget_OnMouseEntered;
            _fullscreenWakeHitTarget.MouseLeft += FullscreenWakeHitTarget_OnMouseLeft;
        }
        catch (Win32Exception exception)
        {
            Debug.WriteLine($"NotchBar fullscreen wake hit target is unavailable: {exception.Message}");
        }
        _needsDpiHandshake = true;
        InvalidateCommittedGeometry();
        CommitFrame(_motion.Current);
        NotifyFrame();
    }

    public void SetMonitor(MonitorTarget monitor)
    {
        _monitor = monitor;
        _needsDpiHandshake = true;
        InvalidateCommittedGeometry();
        CommitFrame(_motion.Current);
    }

    public void SetPreferredSize(
        double compactWidth,
        double expandedWidth,
        double expandedHeight,
        bool applyCurrentState = true)
    {
        compactWidth = Math.Clamp(compactWidth, MinCompactWidth, MaxCompactWidth);
        expandedWidth = Math.Clamp(expandedWidth, MinExpandedWidth, MaxExpandedWidth);
        expandedHeight = Math.Clamp(expandedHeight, MinExpandedHeight, MaxExpandedHeight);

        var animate = applyCurrentState &&
            SystemParameters.ClientAreaAnimation &&
            !_isSuppressed &&
            _handle != IntPtr.Zero;
        _motion.SetPreferredSize(compactWidth, expandedWidth, expandedHeight, animate);

        if (!applyCurrentState)
        {
            return;
        }

        ContinueOrCommit(animate);
    }

    public void SetSuppressed(bool suppressed)
    {
        if (_isSuppressed != suppressed)
        {
            InvalidateCommittedGeometry();
        }

        _isSuppressed = suppressed;
        RefreshFullscreenPinHitTarget();
        RefreshFullscreenWakeHitTarget();
    }

    public void SetFullscreenBadgeActive(bool active)
    {
        if (_fullscreenBadgeActive == active || _disposed)
        {
            return;
        }

        _fullscreenBadgeActive = active;
        var animate = SystemParameters.ClientAreaAnimation &&
            !_isSuppressed &&
            _handle != IntPtr.Zero;
        _motion.SetFullscreenBadge(active, animate);
        _motion.Retarget(_requestedState, animate);
        ContinueOrCommit(animate);
        RefreshFullscreenPinHitTarget();
        RefreshFullscreenWakeHitTarget();
    }

    public void SetFullscreenPassthrough(bool enabled)
    {
        if (_fullscreenPassthrough == enabled)
        {
            return;
        }

        _fullscreenPassthrough = enabled;
        ApplyFullscreenPassthroughStyle();
        RefreshFullscreenPinHitTarget();
        RefreshFullscreenWakeHitTarget();
    }

    public void SetFullscreenPinHitTarget(Rectangle? screenBounds)
    {
        if (_disposed)
        {
            return;
        }

        var normalizedBounds = screenBounds is { Width: > 0, Height: > 0 } ? screenBounds : null;
        if (_fullscreenPinHitBounds == normalizedBounds)
        {
            return;
        }

        _fullscreenPinHitBounds = normalizedBounds;
        RefreshFullscreenPinHitTarget();
    }

    public void SetFullscreenWakeHitTarget(Rectangle? screenBounds)
    {
        if (_disposed)
        {
            return;
        }

        var normalizedBounds = screenBounds is { Width: > 0, Height: > 0 } ? screenBounds : null;
        if (_fullscreenWakeHitBounds == normalizedBounds)
        {
            return;
        }

        _fullscreenWakeHitBounds = normalizedBounds;
        RefreshFullscreenWakeHitTarget();
    }

    /// <summary>
    /// Restores the passive overlay above a newly foregrounded full-screen app
    /// without changing its fixed envelope, rendered mask, or current opacity.
    /// This is intentionally called only for full-screen identity transitions,
    /// never from the per-frame motion path.
    /// </summary>
    public bool ReassertFullscreenZOrder()
    {
        if (_disposed || _handle == IntPtr.Zero || _isSuppressed || !_fullscreenPassthrough)
        {
            return false;
        }

        var flags = SwpNoMove | SwpNoSize | SwpNoActivate;
        var companionRaised = true;
        if (_companionActive && _companionHandle != IntPtr.Zero)
        {
            // Raise the backdrop first, then the main layered HWND so the
            // interactive/pass-through island remains visually above it.
            companionRaised = SetWindowPos(_companionHandle, HwndTopmost, 0, 0, 0, 0, flags);
        }

        // Keep the main HWND above the companion even if the latter was
        // destroyed during the brief transition between context snapshots.
        var mainRaised = SetWindowPos(_handle, HwndTopmost, 0, 0, 0, 0, flags);
        var pinRaised = mainRaised && (_fullscreenPinHitTarget?.RaiseAboveOwner() ?? true);
        var wakeRaised = mainRaised && (_fullscreenWakeHitTarget?.RaiseAboveOwner() ?? true);
        return companionRaised && mainRaised && pinRaised && wakeRaised;
    }

    public void SetFullscreenBadgeContentVisible(bool visible, bool animate = true)
    {
        if (_disposed || _fullscreenBadgeContentVisible == visible)
        {
            return;
        }

        _fullscreenBadgeContentVisible = visible;
        animate = animate && SystemParameters.ClientAreaAnimation &&
            !_isSuppressed &&
            _handle != IntPtr.Zero;
        _motion.SetBadgeContentVisible(visible, animate);
        ContinueOrCommit(animate);
    }

    public void RefreshDpiGeometry()
    {
        if (_disposed || _handle == IntPtr.Zero)
        {
            return;
        }

        InvalidateCommittedGeometry();
        CommitFrame(_motion.Current);
        NotifyFrame();
    }

    public void Apply(NotchState state)
    {
        _requestedState = state;
        var animate = SystemParameters.ClientAreaAnimation &&
            !_isSuppressed &&
            _handle != IntPtr.Zero;
        _motion.Retarget(state, animate);
        ContinueOrCommit(animate);
    }

    private void ContinueOrCommit(bool animate)
    {
        if (animate && !_motion.IsSettled)
        {
            StartRendering();
            CommitFrame(_motion.Current);
            NotifyFrame();
            return;
        }

        StopRendering();
        CommitSettledFrame(_motion.Current);
        NotifyFrame();
    }

    private void StartRendering()
    {
        if (_transitionActive)
        {
            return;
        }

        _transitionActive = true;
        _lastRenderingTime = null;
        CompositionTarget.Rendering += CompositionTarget_OnRendering;
    }

    private void CompositionTarget_OnRendering(object? sender, EventArgs e)
    {
        if (_disposed || !_transitionActive)
        {
            StopRendering();
            return;
        }

        if (e is not RenderingEventArgs renderingEventArgs)
        {
            return;
        }

        var renderingTime = renderingEventArgs.RenderingTime;
        if (_lastRenderingTime is not { } previousRenderingTime)
        {
            _lastRenderingTime = renderingTime;
            return;
        }

        var elapsed = renderingTime - previousRenderingTime;
        if (elapsed <= TimeSpan.Zero)
        {
            return;
        }

        _lastRenderingTime = renderingTime;
        _motion.Step(elapsed);
        var frame = _motion.Current;
        CommitFrame(frame);
        NotifyFrame();

        if (!frame.IsSettled)
        {
            return;
        }

        StopRendering();
        CommitSettledFrame(frame);
        NotifyFrame();
    }

    private void StopRendering()
    {
        _transitionActive = false;
        _lastRenderingTime = null;
        CompositionTarget.Rendering -= CompositionTarget_OnRendering;
    }

    private void CommitSettledFrame(WindowMotionFrame frame)
    {
        CommitFrame(frame);
    }

    private void CommitFrame(WindowMotionFrame frame)
    {
        if (_handle == IntPtr.Zero)
        {
            _currentDpi = 96;
            _currentGeometry = WindowEnvelopeGeometry.Calculate(
                _monitor.Bounds,
                _currentDpi,
                frame,
                _isSuppressed);
            _committingGeometry = true;
            try
            {
                ApplyEnvelopeWpfSize(_currentGeometry, _currentDpi);
            }
            finally
            {
                _committingGeometry = false;
            }

            ApplyFallbackPosition();
            return;
        }

        var bounds = _monitor.Bounds;
        if (_needsDpiHandshake)
        {
            // The handshake deliberately moves the HWND to the target monitor
            // before its DPI is queried, so a previously committed rectangle
            // cannot be considered current after this operation.
            InvalidateCommittedGeometry();
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

        var dpi = GetDpiForWindow(_handle);
        if (dpi == 0)
        {
            dpi = 96;
        }

        var geometry = WindowEnvelopeGeometry.Calculate(bounds, dpi, frame, _isSuppressed);
        _currentDpi = dpi;
        _currentGeometry = geometry;

        _committingGeometry = true;
        try
        {
            ApplyEnvelopeWpfSize(geometry, dpi);
            var companionShouldShow = _companionActive && _companionHandle != IntPtr.Zero && !_isSuppressed;
            if (companionShouldShow)
            {
                var committed = false;
                try
                {
                    _companionGeometryChanged?.Invoke(geometry, dpi);
                    committed = CommitPairedGeometry(geometry.Envelope);
                }
                catch (Exception exception)
                {
                    System.Diagnostics.Debug.WriteLine($"NotchBar paired backdrop commit failed: {exception}");
                }

                if (!committed)
                {
                    FailCompanionCommit();
                    _ = CommitMainGeometry(geometry.Envelope);
                }
            }
            else
            {
                if (_companionHandle != IntPtr.Zero)
                {
                    _ = ShowWindow(_companionHandle, SwHide);
                }

                _ = CommitMainGeometry(geometry.Envelope);
            }
        }
        finally
        {
            _committingGeometry = false;
        }
    }

    private void NotifyFrame()
    {
        MotionFrameChanged?.Invoke(
            this,
            new WindowMotionFrameChangedEventArgs(
                _motion.Current,
                _transitionActive,
                _currentGeometry,
                _currentDpi));
    }

    private void Window_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_disposed || _handle == IntPtr.Zero || _committingGeometry)
        {
            return;
        }

        // A size change outside our native commit path means the HWND may no
        // longer match the cached pixel rectangle.
        InvalidateCommittedGeometry();
        if (_transitionActive)
        {
            return;
        }

        CommitFrame(_motion.Current);
    }

    private void ApplyFallbackPosition()
    {
        _window.Left = Math.Max(0, (SystemParameters.PrimaryScreenWidth - EnvelopeWidth) / 2);
        _window.Top = _isSuppressed ? -EnvelopeHeight : -(CompactHeight - HiddenTriggerHeight);
    }

    private void ApplyEnvelopeWpfSize(WindowEnvelopeGeometry geometry, uint dpi)
    {
        var scale = (dpi == 0 ? 96 : dpi) / 96d;
        var widthDip = geometry.Envelope.Width / scale;
        var heightDip = geometry.Envelope.Height / scale;
        if (Math.Abs(_window.Width - widthDip) > 0.001)
        {
            _window.Width = widthDip;
        }

        if (Math.Abs(_window.Height - heightDip) > 0.001)
        {
            _window.Height = heightDip;
        }
    }

    private IntPtr WindowMessageHook(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message != WmNcHitTest)
        {
            return IntPtr.Zero;
        }

        if (_isSuppressed || _fullscreenPassthrough)
        {
            handled = true;
            return new IntPtr(HtTransparent);
        }

        // WM_NCHITTEST packs signed screen coordinates in lParam. Do not use
        // GetCursorPos here: WindowFromPoint and accessibility hit-tests can
        // query a point that is not the current physical cursor position.
        var packedPoint = unchecked((int)lParam.ToInt64());
        var screenPoint = new NativePoint
        {
            X = unchecked((short)(packedPoint & 0xFFFF)),
            Y = unchecked((short)((packedPoint >> 16) & 0xFFFF))
        };

        var envelope = _currentGeometry.Envelope;
        var island = _currentGeometry.Island;
        var left = envelope.X + island.X;
        var top = envelope.Y + island.Y;
        if (screenPoint.X >= left && screenPoint.X < left + island.Width &&
            screenPoint.Y >= top && screenPoint.Y < top + island.Height)
        {
            return IntPtr.Zero;
        }

        handled = true;
        return new IntPtr(HtTransparent);
    }

    private void ApplyFullscreenPassthroughStyle()
    {
        if (_handle == IntPtr.Zero)
        {
            return;
        }

        var currentStyle = GetWindowLongPtr(_handle, GwlExStyle).ToInt64();
        var nextStyle = _fullscreenPassthrough
            ? currentStyle | WsExTransparent
            : currentStyle & ~WsExTransparent;
        if (nextStyle == currentStyle)
        {
            return;
        }

        _ = SetWindowLongPtr(_handle, GwlExStyle, new IntPtr(nextStyle));
        _ = SetWindowPos(
            _handle,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
    }

    private void RefreshFullscreenPinHitTarget()
    {
        if (_fullscreenPinHitTarget is null)
        {
            return;
        }

        if (_fullscreenBadgeActive &&
            _fullscreenPassthrough &&
            !_isSuppressed &&
            _fullscreenPinHitBounds is { Width: > 0, Height: > 0 } bounds)
        {
            _ = _fullscreenPinHitTarget.ShowAt(bounds);
        }
        else
        {
            _fullscreenPinHitTarget.Hide();
        }
    }

    private void RefreshFullscreenWakeHitTarget()
    {
        if (_fullscreenWakeHitTarget is null)
        {
            return;
        }

        if (_fullscreenPassthrough &&
            !_isSuppressed &&
            !_fullscreenBadgeActive &&
            _fullscreenWakeHitBounds is { Width: > 0, Height: > 0 } bounds)
        {
            _ = _fullscreenWakeHitTarget.ShowAt(bounds);
        }
        else
        {
            _fullscreenWakeHitTarget.Hide();
        }
    }

    private void FullscreenPinHitTarget_OnClicked(object? sender, EventArgs e) =>
        FullscreenPinClicked?.Invoke(this, EventArgs.Empty);

    private void FullscreenPinHitTarget_OnMouseEntered(object? sender, EventArgs e) =>
        FullscreenPinMouseEntered?.Invoke(this, EventArgs.Empty);

    private void FullscreenPinHitTarget_OnMouseLeft(object? sender, EventArgs e) =>
        FullscreenPinMouseLeft?.Invoke(this, EventArgs.Empty);

    private void FullscreenWakeHitTarget_OnMouseEntered(object? sender, EventArgs e) =>
        FullscreenWakeMouseEntered?.Invoke(this, EventArgs.Empty);

    private void FullscreenWakeHitTarget_OnMouseLeft(object? sender, EventArgs e) =>
        FullscreenWakeMouseLeft?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopRendering();
        _source?.RemoveHook(WindowMessageHook);
        _source = null;
        if (_fullscreenPinHitTarget is not null)
        {
            _fullscreenPinHitTarget.Clicked -= FullscreenPinHitTarget_OnClicked;
            _fullscreenPinHitTarget.MouseEntered -= FullscreenPinHitTarget_OnMouseEntered;
            _fullscreenPinHitTarget.MouseLeft -= FullscreenPinHitTarget_OnMouseLeft;
            _fullscreenPinHitTarget.Dispose();
            _fullscreenPinHitTarget = null;
        }
        if (_fullscreenWakeHitTarget is not null)
        {
            _fullscreenWakeHitTarget.MouseEntered -= FullscreenWakeHitTarget_OnMouseEntered;
            _fullscreenWakeHitTarget.MouseLeft -= FullscreenWakeHitTarget_OnMouseLeft;
            _fullscreenWakeHitTarget.Dispose();
            _fullscreenWakeHitTarget = null;
        }
        _window.SizeChanged -= Window_OnSizeChanged;
        UnregisterCompanion();
    }

    private bool CommitPairedGeometry(WindowPixelGeometry geometry)
    {
        if (HasCommittedGeometry(geometry, includesCompanion: true))
        {
            return true;
        }

        var deferred = BeginDeferWindowPos(2);
        if (deferred == IntPtr.Zero)
        {
            InvalidateCommittedGeometry();
            return false;
        }

        var next = DeferWindowPos(
            deferred,
            _handle,
            HwndTopmost,
            geometry.X,
            geometry.Y,
            geometry.Width,
            geometry.Height,
            SwpNoActivate | SwpShowWindow);
        if (next == IntPtr.Zero)
        {
            InvalidateCommittedGeometry();
            _ = ShowWindow(_companionHandle, SwHide); // The failed DeferWindowPos invalidates its HDWP.
            return false;
        }

        next = DeferWindowPos(
            next,
            _companionHandle,
            _handle,
            geometry.X,
            geometry.Y,
            geometry.Width,
            geometry.Height,
            SwpNoActivate | SwpShowWindow);
        if (next == IntPtr.Zero)
        {
            InvalidateCommittedGeometry();
            _ = ShowWindow(_companionHandle, SwHide); // The failed DeferWindowPos invalidates its HDWP.
            return false;
        }

        var committed = EndDeferWindowPos(next);
        if (!committed)
        {
            InvalidateCommittedGeometry();
            _ = ShowWindow(_companionHandle, SwHide);
            return false;
        }

        RecordCommittedGeometry(geometry, includesCompanion: true);
        return true;
    }

    private bool CommitMainGeometry(WindowPixelGeometry geometry)
    {
        if (HasCommittedGeometry(geometry, includesCompanion: false))
        {
            return true;
        }

        var committed = SetWindowPos(
            _handle,
            IntPtr.Zero,
            geometry.X,
            geometry.Y,
            geometry.Width,
            geometry.Height,
            SwpNoZOrder | SwpNoActivate);
        if (committed)
        {
            RecordCommittedGeometry(geometry, includesCompanion: false);
        }
        else
        {
            InvalidateCommittedGeometry();
        }

        return committed;
    }

    private bool HasCommittedGeometry(WindowPixelGeometry geometry, bool includesCompanion) =>
        _lastCommittedGeometry == geometry &&
        _lastCommitIncludedCompanion == includesCompanion &&
        _lastCommittedCompanionHandle == (includesCompanion ? _companionHandle : IntPtr.Zero);

    private void RecordCommittedGeometry(WindowPixelGeometry geometry, bool includesCompanion)
    {
        _lastCommittedGeometry = geometry;
        _lastCommitIncludedCompanion = includesCompanion;
        _lastCommittedCompanionHandle = includesCompanion ? _companionHandle : IntPtr.Zero;
    }

    private void InvalidateCommittedGeometry()
    {
        _lastCommittedGeometry = null;
        _lastCommitIncludedCompanion = false;
        _lastCommittedCompanionHandle = IntPtr.Zero;
    }

    private void FailCompanionCommit()
    {
        var failure = _companionCommitFailed;
        UnregisterCompanion();
        failure?.Invoke();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr BeginDeferWindowPos(int numberOfWindows);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr DeferWindowPos(
        IntPtr deferred,
        IntPtr hwnd,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EndDeferWindowPos(IntPtr deferred);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

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
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr newValue);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }
}

/// <summary>Physical-pixel window rectangle with a screen-absolute origin.</summary>
public readonly record struct WindowPixelGeometry(int X, int Y, int Width, int Height);

/// <summary>
/// Island bounds in physical pixels, relative to the fixed envelope's client
/// origin. These are shared with the composition companion.
/// </summary>
public readonly record struct WindowIslandPixelRect(int X, int Y, int Width, int Height);

/// <summary>
/// Fixed HWND envelope bounds plus the changing island bounds inside it. The
/// envelope origin is screen-absolute; island coordinates are envelope-local.
/// </summary>
public readonly record struct WindowEnvelopeGeometry(
    WindowPixelGeometry Envelope,
    WindowIslandPixelRect Island)
{
    private const double HiddenTopInset = WindowController.CompactHeight - WindowController.HiddenTriggerHeight;
    private const double MaxIslandWidth = WindowController.EnvelopeWidth;
    private const double MaxIslandHeight = 300;

    public static WindowEnvelopeGeometry Calculate(
        Rectangle monitorBounds,
        uint dpi,
        WindowMotionFrame frame,
        bool isSuppressed)
    {
        var scale = (dpi == 0 ? 96 : dpi) / 96d;
        var envelopeWidth = Math.Max(
            1,
            Math.Min(ScaleToPixels(WindowController.EnvelopeWidth, scale), monitorBounds.Width));
        var envelopeHeight = ScaleToPixels(WindowController.EnvelopeHeight, scale);
        var islandWidth = Math.Clamp(
            ScaleToPixels(Math.Clamp(frame.Width, 1, MaxIslandWidth), scale),
            1,
            envelopeWidth);
        var islandHeight = Math.Clamp(
            ScaleToPixels(Math.Clamp(frame.Height, 1, MaxIslandHeight), scale),
            1,
            envelopeHeight);
        var envelopeX = monitorBounds.Left + Math.Max(0, (monitorBounds.Width - envelopeWidth) / 2);
        var badgeProgress = Math.Clamp(frame.BadgeProgress, 0d, 1d);
        var compactBodyWidth = Math.Max(1, frame.Width - WindowController.FullscreenBadgeWidth * badgeProgress);
        var compactBodyWidthPixels = Math.Clamp(
            ScaleToPixels(compactBodyWidth, scale),
            1,
            envelopeWidth);
        var centeredIslandX = monitorBounds.Left + Math.Max(0, (monitorBounds.Width - compactBodyWidthPixels) / 2);
        var anchorWidthPixels = ScaleToPixels(Math.Max(1, frame.BadgeAnchorWidth), scale);
        var anchoredIslandX = monitorBounds.Left + Math.Max(0, (monitorBounds.Width - anchorWidthPixels) / 2);
        var anchorProgress = Math.Clamp(frame.BadgeAnchorProgress, 0d, 1d);
        var islandScreenX = (int)Math.Round(
            centeredIslandX + ((anchoredIslandX - centeredIslandX) * anchorProgress),
            MidpointRounding.AwayFromZero);
        var islandX = Math.Clamp(islandScreenX - envelopeX, 0, envelopeWidth - islandWidth);
        var hiddenTopInsetPixels = ScaleToPixels(HiddenTopInset, scale);
        var islandTopOffsetPixels = ScaleToPixels(frame.TopOffset, scale);
        var envelopeY = isSuppressed
            ? monitorBounds.Top - envelopeHeight
            : monitorBounds.Top - hiddenTopInsetPixels;
        var islandY = Math.Clamp(
            islandTopOffsetPixels + hiddenTopInsetPixels,
            0,
            Math.Max(0, envelopeHeight - islandHeight));

        return new WindowEnvelopeGeometry(
            new WindowPixelGeometry(envelopeX, envelopeY, envelopeWidth, envelopeHeight),
            new WindowIslandPixelRect(islandX, islandY, islandWidth, islandHeight));
    }

    private static int ScaleToPixels(double value, double scale) =>
        (int)Math.Round(value * scale, MidpointRounding.AwayFromZero);
}

/// <summary>Compact content and badge widths constrained to the visible island bounds.</summary>
public readonly record struct FullscreenBadgeLayout(double CompactBodyWidth, double BadgeWidth)
{
    public static FullscreenBadgeLayout Calculate(double availableWidth, WindowMotionFrame frame)
    {
        availableWidth = Math.Max(1d, availableWidth);
        var requestedBadgeWidth = WindowController.FullscreenBadgeWidth * Math.Clamp(frame.BadgeProgress, 0d, 1d);
        var badgeWidth = Math.Min(requestedBadgeWidth, Math.Max(0d, availableWidth - 1d));
        var bodyWidth = Math.Min(
            Math.Max(1d, frame.Width - requestedBadgeWidth),
            Math.Max(1d, availableWidth - badgeWidth));
        return new FullscreenBadgeLayout(bodyWidth, badgeWidth);
    }
}

public sealed class WindowMotionFrameChangedEventArgs(
    WindowMotionFrame frame,
    bool isTransitionActive,
    WindowEnvelopeGeometry geometry,
    uint dpi) : EventArgs
{
    public WindowMotionFrame Frame { get; } = frame;

    public bool IsTransitionActive { get; } = isTransitionActive;

    public WindowEnvelopeGeometry Geometry { get; } = geometry;

    public uint Dpi { get; } = dpi;
}
