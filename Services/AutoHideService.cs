using System.Windows.Threading;
using NotchBar.Core;

namespace NotchBar.Services;

public sealed class AutoHideService : IDisposable
{
    private readonly DispatcherTimer _timer;
    private readonly NotchStateMachine _stateMachine;
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

    public void OnMouseEnter() => _timer.Stop();

    public void OnMouseLeave() => ScheduleHide();

    public void ScheduleHide()
    {
        if (_disposed || _stateMachine.Current is NotchState.Hidden or NotchState.Pinned)
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
