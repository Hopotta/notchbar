namespace NotchBar.Services;

public sealed class SingleInstanceService : IDisposable
{
    private const string MutexName = @"Local\NotchBar.SingleInstance";
    private const string ActivationEventName = @"Local\NotchBar.Activate";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activationEvent;
    private readonly ManualResetEvent _shutdownEvent = new(false);
    private Task? _listenTask;
    private bool _disposed;
    private bool _ownsMutex;

    public SingleInstanceService()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        IsPrimary = createdNew;
        _ownsMutex = createdNew;
        _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName);

        if (!IsPrimary)
        {
            _activationEvent.Set();
        }
    }

    public bool IsPrimary { get; }

    public event EventHandler? ActivationRequested;

    public void StartListening()
    {
        if (_disposed || !IsPrimary || _listenTask is not null)
        {
            return;
        }

        _listenTask = Task.Run(ListenLoop);
    }

    private void ListenLoop()
    {
        var handles = new WaitHandle[] { _activationEvent, _shutdownEvent };
        while (!_disposed)
        {
            var signaled = WaitHandle.WaitAny(handles);
            if (signaled == 1 || _disposed)
            {
                break;
            }

            ActivationRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _shutdownEvent.Set();
        _listenTask?.GetAwaiter().GetResult();
        _shutdownEvent.Dispose();
        _activationEvent.Dispose();

        if (_ownsMutex)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // The OS already released ownership during teardown.
            }
            _ownsMutex = false;
        }

        _mutex.Dispose();
    }
}
