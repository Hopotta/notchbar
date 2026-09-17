using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace NotchBar.Services;

public sealed class FullscreenSuppressionService : IDisposable
{
    private const uint MonitorDefaultToNearest = 0x00000002;
    private const uint MonitorInfoPrimary = 0x00000001;
    private const int BoundsTolerance = 2;

    private readonly DispatcherTimer _timer;
    private bool _isSuppressed;
    private bool _disposed;

    public FullscreenSuppressionService(TimeSpan? pollInterval = null)
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = pollInterval ?? TimeSpan.FromMilliseconds(500)
        };
        _timer.Tick += Timer_OnTick;
    }

    public bool IsSuppressed => _isSuppressed;

    public event EventHandler<FullscreenSuppressionChangedEventArgs>? Changed;

    public void Start()
    {
        if (_disposed || _timer.IsEnabled)
        {
            return;
        }

        Refresh();
        _timer.Start();
    }

    private void Timer_OnTick(object? sender, EventArgs e) => Refresh();

    private void Refresh()
    {
        if (_disposed)
        {
            return;
        }

        var next = IsForegroundFullscreenOnPrimaryMonitor();
        if (next == _isSuppressed)
        {
            return;
        }

        _isSuppressed = next;
        Changed?.Invoke(this, new FullscreenSuppressionChangedEventArgs(next));
    }

    private static bool IsForegroundFullscreenOnPrimaryMonitor()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero || !IsWindowVisible(window) || IsIconic(window) || IsDesktopShellWindow(window))
        {
            return false;
        }

        _ = GetWindowThreadProcessId(window, out var processId);
        if (processId == (uint)Environment.ProcessId)
        {
            return false;
        }

        var monitor = MonitorFromWindow(window, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return false;
        }

        var monitorInfo = new MonitorInfo
        {
            Size = (uint)Marshal.SizeOf<MonitorInfo>()
        };
        if (!GetMonitorInfo(monitor, ref monitorInfo) ||
            (monitorInfo.Flags & MonitorInfoPrimary) == 0 ||
            !GetWindowRect(window, out var windowRect))
        {
            return false;
        }

        return CoversBounds(windowRect, monitorInfo.Monitor, BoundsTolerance);
    }

    private static bool IsDesktopShellWindow(IntPtr window)
    {
        if (window == GetShellWindow())
        {
            return true;
        }

        var className = new StringBuilder(64);
        if (GetClassName(window, className, className.Capacity) == 0)
        {
            return false;
        }

        return className.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd";
    }

    private static bool CoversBounds(NativeRect window, NativeRect monitor, int tolerance)
    {
        return window.Left <= monitor.Left + tolerance &&
               window.Top <= monitor.Top + tolerance &&
               window.Right >= monitor.Right - tolerance &&
               window.Bottom >= monitor.Bottom - tolerance;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _timer.Tick -= Timer_OnTick;
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
    private struct MonitorInfo
    {
        public uint Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo monitorInfo);
}

public sealed class FullscreenSuppressionChangedEventArgs(bool isSuppressed) : EventArgs
{
    public bool IsSuppressed { get; } = isSuppressed;
}
