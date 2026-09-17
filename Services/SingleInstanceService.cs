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
        _mutex = new Mutex(initiallyOwned: false, MutexName);
        try
        {
            _ownsMutex = _mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            // The previous owner exited without releasing the mutex. Ownership is
            // transferred to this process, so it can safely become the primary.
            _ownsMutex = true;
        }

        IsPrimary = _ownsMutex;
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
