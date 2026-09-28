namespace NotchBar.Services;

internal static class BoundedOperationRunner
{
    public static bool TryRun(Func<CancellationToken, Task> operation, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            operation(cancellation.Token)
                .WaitAsync(cancellation.Token)
                .GetAwaiter()
                .GetResult();
            return true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return false;
        }
    }
}
