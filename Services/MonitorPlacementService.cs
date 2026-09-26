using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using WpfApplication = System.Windows.Application;

namespace NotchBar.Services;

public enum MonitorPlacementMode
{
    Primary,
    ActiveWindow
}

public sealed record MonitorTarget(string DeviceName, Rectangle Bounds, bool IsPrimary)
{
    // Lets the controller re-read the HWND's effective DPI when geometry is unchanged.
    public long DpiRefreshRevision { get; init; }
}

public sealed class MonitorPlacementService : IDisposable
{
    private readonly MonitorPlacementMode _mode;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer? _timer;
    private MonitorTarget _current;
    private volatile bool _started;
    private bool _displayEventsSubscribed;
    private int _refreshQueued;
    private long _dpiRefreshRevision;
    private volatile bool _disposed;

    public MonitorPlacementService(MonitorPlacementMode mode, TimeSpan? pollInterval = null)
    {
        _mode = mode;
        _dispatcher = WpfApplication.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _current = ResolveTarget(mode);
        if (mode == MonitorPlacementMode.ActiveWindow)
        {
            _timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
            {
                Interval = pollInterval ?? TimeSpan.FromMilliseconds(500)
            };
            _timer.Tick += Timer_OnTick;
        }
    }

    public MonitorTarget Current => _current;

    public event EventHandler<MonitorTargetChangedEventArgs>? Changed;

    public void Start()
    {
        if (_disposed || _started)
        {
            return;
        }

        _started = true;
        SubscribeToDisplayEvents();
        if (_mode == MonitorPlacementMode.ActiveWindow)
        {
            _timer?.Start();
        }

        Refresh();
    }

    private void Timer_OnTick(object? sender, EventArgs e) => Refresh();

    private void SubscribeToDisplayEvents()
    {
        if (_displayEventsSubscribed)
        {
            return;
        }

        SystemEvents.DisplaySettingsChanged += SystemEvents_DisplaySettingsChanged;
        SystemEvents.PowerModeChanged += SystemEvents_PowerModeChanged;
        SystemParameters.StaticPropertyChanged += SystemParameters_StaticPropertyChanged;
        _displayEventsSubscribed = true;
    }

    private void UnsubscribeFromDisplayEvents()
    {
        if (!_displayEventsSubscribed)
        {
            return;
        }

        SystemEvents.DisplaySettingsChanged -= SystemEvents_DisplaySettingsChanged;
        SystemEvents.PowerModeChanged -= SystemEvents_PowerModeChanged;
        SystemParameters.StaticPropertyChanged -= SystemParameters_StaticPropertyChanged;
        _displayEventsSubscribed = false;
    }

    private void SystemEvents_DisplaySettingsChanged(object? sender, EventArgs e) => QueueRefresh(dpiMayHaveChanged: true);

    private void SystemEvents_PowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            QueueRefresh(dpiMayHaveChanged: true);
        }
    }

    private void SystemParameters_StaticPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        var propertyName = e.PropertyName;
        var dpiMayHaveChanged = string.IsNullOrEmpty(propertyName) ||
            propertyName.Contains("Dpi", StringComparison.OrdinalIgnoreCase);
        if (dpiMayHaveChanged ||
            propertyName!.Contains("Screen", StringComparison.OrdinalIgnoreCase) ||
            propertyName.Contains("WorkArea", StringComparison.OrdinalIgnoreCase))
        {
            QueueRefresh(dpiMayHaveChanged);
        }
    }

    private void QueueRefresh(bool dpiMayHaveChanged = false)
    {
        if (_disposed || !_started || _dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return;
        }

        if (dpiMayHaveChanged)
        {
            Interlocked.Increment(ref _dpiRefreshRevision);
        }

        if (Interlocked.Exchange(ref _refreshQueued, 1) != 0)
        {
            return;
        }

        try
        {
            _dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)(() =>
            {
                Interlocked.Exchange(ref _refreshQueued, 0);
                Refresh();
            }));
        }
        catch (InvalidOperationException)
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
        }
    }

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

    private MonitorTarget ResolveTarget(MonitorPlacementMode mode)
    {
        Forms.Screen? screen = null;
        if (mode == MonitorPlacementMode.ActiveWindow)
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
            return new MonitorTarget("primary", new Rectangle(0, 0, 1, 1), true)
            {
                DpiRefreshRevision = Interlocked.Read(ref _dpiRefreshRevision)
            };
        }

        return new MonitorTarget(screen.DeviceName, screen.Bounds, screen.Primary)
        {
            DpiRefreshRevision = Interlocked.Read(ref _dpiRefreshRevision)
        };
    }

    private static bool SameMonitor(MonitorTarget left, MonitorTarget right)
    {
        return string.Equals(left.DeviceName, right.DeviceName, StringComparison.OrdinalIgnoreCase) &&
               left.Bounds == right.Bounds &&
               left.IsPrimary == right.IsPrimary &&
               left.DpiRefreshRevision == right.DpiRefreshRevision;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        UnsubscribeFromDisplayEvents();
        if (_timer is not null)
        {
            _timer.Stop();
            _timer.Tick -= Timer_OnTick;
        }
        Changed = null;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}

public sealed class MonitorTargetChangedEventArgs(MonitorTarget target) : EventArgs
{
    public MonitorTarget Target { get; } = target;
}
