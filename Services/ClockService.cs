using NotchBar.Core;

namespace NotchBar.Services;

public sealed class ClockService : IDisposable
{
    private readonly StatusStore _store;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _runTask;

    public ClockService(StatusStore store)
    {
        _store = store;
        _store.UpdateClock(DateTime.Now);
        _runTask = RunAsync(_cts.Token);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var now = DateTime.Now;
            var nextMinute = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, now.Kind).AddMinutes(1);
            var delay = nextMinute - DateTime.Now;
            if (delay < TimeSpan.Zero)
            {
                delay = TimeSpan.Zero;
            }

            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                _store.UpdateClock(DateTime.Now);
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _runTask.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }
        _cts.Dispose();
    }
}
