using System.Windows.Threading;
using NotchBar.Core;

namespace NotchBar.Services;

public sealed class AutoHideService : IDisposable
{
    private readonly DispatcherTimer _timer;
    private readonly NotchStateMachine _stateMachine;
    private bool _isPointerOver;
    private bool _disposed;

    public AutoHideService(NotchStateMachine stateMachine, TimeSpan delay)
    {
        _stateMachine = stateMachine;
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

    public void OnMouseLeave()
    {
        if (_disposed)
        {
            return;
        }

        _isPointerOver = false;
        ScheduleHide();
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

    public void ScheduleHide()
    {
        if (_disposed || _isPointerOver || _stateMachine.Current is NotchState.Hidden or NotchState.Pinned)
        {
            return;
        }

        _timer.Stop();
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
