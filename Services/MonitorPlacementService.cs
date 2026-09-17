using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace NotchBar.Services;

public enum MonitorMode
{
    Primary,
    ActiveWindow
}

public sealed record MonitorTarget(string DeviceName, Rectangle Bounds, bool IsPrimary);

public sealed class MonitorPlacementService : IDisposable
{
    private readonly MonitorMode _mode;
    private readonly DispatcherTimer _timer;
    private MonitorTarget _current;
    private bool _disposed;

    public MonitorPlacementService(MonitorMode mode, TimeSpan? pollInterval = null)
    {
        _mode = mode;
        _current = ResolveTarget(mode);
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = pollInterval ?? TimeSpan.FromMilliseconds(500)
        };
        _timer.Tick += Timer_OnTick;
    }

    public MonitorTarget Current => _current;

    public event EventHandler<MonitorTargetChangedEventArgs>? Changed;

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

        var next = ResolveTarget(_mode);
        if (SameMonitor(_current, next))
        {
            return;
        }

        _current = next;
        Changed?.Invoke(this, new MonitorTargetChangedEventArgs(next));
    }

    private static MonitorTarget ResolveTarget(MonitorMode mode)
    {
        Forms.Screen? screen = null;
        if (mode == MonitorMode.ActiveWindow)
        {
            var foreground = GetForegroundWindow();
            if (foreground != IntPtr.Zero)
            {
                screen = Forms.Screen.FromHandle(foreground);
            }
        }

        screen ??= Forms.Screen.PrimaryScreen ?? Forms.Screen.AllScreens.FirstOrDefault();
        if (screen is null)
        {
            return new MonitorTarget("primary", new Rectangle(0, 0, 1, 1), true);
        }

        return new MonitorTarget(screen.DeviceName, screen.Bounds, screen.Primary);
    }

    private static bool SameMonitor(MonitorTarget left, MonitorTarget right)
    {
        return string.Equals(left.DeviceName, right.DeviceName, StringComparison.OrdinalIgnoreCase) &&
               left.Bounds == right.Bounds &&
               left.IsPrimary == right.IsPrimary;
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

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}

public sealed class MonitorTargetChangedEventArgs(MonitorTarget target) : EventArgs
{
    public MonitorTarget Target { get; } = target;
}
