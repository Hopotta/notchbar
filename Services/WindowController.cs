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
    private const uint SwpShowWindow = 0x0040;
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
    private IntPtr _companionHandle;
    private Action<WindowEnvelopeGeometry, uint>? _companionGeometryChanged;
    private Action? _companionCommitFailed;
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
        if (message != WmNcHitTest || _isSuppressed)
        {
            return IntPtr.Zero;
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
        var islandScreenX = monitorBounds.Left + Math.Max(0, (monitorBounds.Width - islandWidth) / 2);
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
