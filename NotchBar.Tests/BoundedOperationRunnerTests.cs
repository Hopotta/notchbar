using NotchBar.Services;
using Xunit;

namespace NotchBar.Tests;

public sealed class BoundedOperationRunnerTests
{
    [Fact]
    public void TryRun_ReturnsTrueWhenOperationCompletes()
    {
        var completed = BoundedOperationRunner.TryRun(_ => Task.CompletedTask, TimeSpan.FromSeconds(1));

        Assert.True(completed);
    }

    [Fact]
    public void TryRun_CancelsAndReturnsFalseWhenOperationExceedsDeadline()
    {
        using var started = new ManualResetEventSlim();

        var completed = BoundedOperationRunner.TryRun(
            cancellationToken =>
            {
                started.Set();
                return Task.Delay(Timeout.Infinite, cancellationToken);
            },
            TimeSpan.FromMilliseconds(50));

        Assert.True(started.IsSet);
        Assert.False(completed);
    }
}
