using System.Windows.Threading;
using NotchBar.Core;

namespace NotchBar.Services;

public sealed class AutoHideService : IDisposable
{
    private readonly DispatcherTimer _timer;
    private readonly NotchStateMachine _stateMachine;
    private readonly TimeSpan _defaultDelay;
    private bool _isPointerOver;
    private bool _disposed;

    public AutoHideService(NotchStateMachine stateMachine, TimeSpan delay)
    {
        _stateMachine = stateMachine;
        _defaultDelay = delay;
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = delay
        };
        _timer.Tick += OnTimerTick;
    }

    public void OnMouseEnter()
    {
        if (_disposed)
        {
            return;
        }

        _isPointerOver = true;
        _timer.Stop();
    }

    public void OnMouseLeave(TimeSpan? delay = null)
    {
        if (_disposed)
        {
            return;
        }

        _isPointerOver = false;
        ScheduleHide(delay ?? _defaultDelay);
    }

    public void ResetPointerState()
    {
        if (_disposed)
        {
            return;
        }

        _isPointerOver = false;
        _timer.Stop();
    }

    public void ScheduleHide() => ScheduleHide(_defaultDelay);

    public void ScheduleHide(TimeSpan delay)
    {
        if (_disposed || _isPointerOver || _stateMachine.Current is NotchState.Hidden or NotchState.Pinned)
        {
            return;
        }

        _timer.Stop();
        _timer.Interval = delay > TimeSpan.Zero ? delay : TimeSpan.FromMilliseconds(1);
        _timer.Start();
    }

    public void Cancel() => _timer.Stop();

    private void OnTimerTick(object? sender, EventArgs e)
    {
        _timer.Stop();
        if (_stateMachine.Current is not NotchState.Pinned)
        {
            _stateMachine.Set(NotchState.Hidden);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
    }
}
