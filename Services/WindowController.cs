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

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    private readonly Window _window;
    private readonly WindowTransitionMotion _motion = new();
    private MonitorTarget _monitor;
    private IntPtr _handle;
    private TimeSpan? _lastRenderingTime;
    private bool _transitionActive;
    private bool _committingGeometry;
    private bool _needsDpiHandshake = true;
    private bool _isSuppressed;
    private bool _disposed;

    public WindowController(Window window, MonitorTarget monitor)
    {
        _window = window;
        _monitor = monitor;
        _window.Width = DefaultWindowWidth;
        _window.Height = CompactHeight;
        _window.SizeChanged += Window_OnSizeChanged;
        ApplyFallbackPosition(_motion.Current);
    }

    public event EventHandler<WindowMotionFrameChangedEventArgs>? MotionFrameChanged;

    public WindowMotionFrame CurrentFrame => _motion.Current;

    public bool IsTransitionActive => _transitionActive;

    public void Attach()
    {
        if (_handle != IntPtr.Zero || _disposed)
        {
            return;
        }

        _handle = new WindowInteropHelper(_window).EnsureHandle();
        _needsDpiHandshake = true;
        CommitFrame(_motion.Current);
        NotifyFrame();
    }

    public void SetMonitor(MonitorTarget monitor)
    {
        _monitor = monitor;
        _needsDpiHandshake = true;
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
        _isSuppressed = suppressed;
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
        _committingGeometry = true;
        try
        {
            _window.BeginAnimation(FrameworkElement.WidthProperty, null);
            _window.BeginAnimation(FrameworkElement.HeightProperty, null);
            _window.Width = frame.Width;
            _window.Height = frame.Height;
        }
        finally
        {
            _committingGeometry = false;
        }

        CommitFrame(frame);
    }

    private void CommitFrame(WindowMotionFrame frame)
    {
        if (_handle == IntPtr.Zero)
        {
            if (frame.IsSettled)
            {
                _window.Width = frame.Width;
                _window.Height = frame.Height;
            }

            ApplyFallbackPosition(frame);
            return;
        }

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

        var dpi = GetDpiForWindow(_handle);
        if (dpi == 0)
        {
            dpi = 96;
        }

        var scale = dpi / 96d;
        var width = Math.Max(1, (int)Math.Round(frame.Width * scale));
        var height = Math.Max(1, (int)Math.Round(frame.Height * scale));
        var x = bounds.Left + Math.Max(0, (bounds.Width - width) / 2);
        var y = _isSuppressed
            ? bounds.Top - height
            : bounds.Top + (int)Math.Round(frame.TopOffset * scale);

        _committingGeometry = true;
        try
        {
            _ = SetWindowPos(
                _handle,
                IntPtr.Zero,
                x,
                y,
                width,
                height,
                SwpNoZOrder | SwpNoActivate);
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
            new WindowMotionFrameChangedEventArgs(_motion.Current, _transitionActive));
    }

    private void Window_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_disposed || _handle == IntPtr.Zero || _committingGeometry || _transitionActive)
        {
            return;
        }

        CommitFrame(_motion.Current);
    }

    private void ApplyFallbackPosition(WindowMotionFrame frame)
    {
        _window.Left = Math.Max(0, (SystemParameters.PrimaryScreenWidth - frame.Width) / 2);
        _window.Top = _isSuppressed ? -frame.Height : frame.TopOffset;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopRendering();
        _window.BeginAnimation(FrameworkElement.WidthProperty, null);
        _window.BeginAnimation(FrameworkElement.HeightProperty, null);
        _window.SizeChanged -= Window_OnSizeChanged;
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
    private static extern uint GetDpiForWindow(IntPtr hWnd);
}

public sealed class WindowMotionFrameChangedEventArgs(
    WindowMotionFrame frame,
    bool isTransitionActive) : EventArgs
{
    public WindowMotionFrame Frame { get; } = frame;

    public bool IsTransitionActive { get; } = isTransitionActive;
}
