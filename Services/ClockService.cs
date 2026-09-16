using NotchBar.Core;

namespace NotchBar.Services;

public sealed class ClockService : IDisposable
{
    private readonly Timer _timer;

    public ClockService(StatusStore store)
    {
        _timer = new Timer(_ => store.UpdateClock(DateTime.Now), null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
    }

    public void Dispose() => _timer.Dispose();
}
