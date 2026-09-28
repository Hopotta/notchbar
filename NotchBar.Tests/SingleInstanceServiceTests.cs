using NotchBar.Services;
using Xunit;

namespace NotchBar.Tests;

public sealed class SingleInstanceServiceTests
{
    [Fact]
    public void Dispose_ReleasesMutexEvenWhenActivationListenerFaults()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var mutexName = $@"Local\NotchBar.Tests.{suffix}.Mutex";
        var activationEventName = $@"Local\NotchBar.Tests.{suffix}.Activate";
        using var activationRaised = new ManualResetEventSlim();

        var primary = new SingleInstanceService(mutexName, activationEventName);
        Assert.True(primary.IsPrimary);
        primary.ActivationRequested += (_, _) =>
        {
            activationRaised.Set();
            throw new InvalidOperationException("Simulated listener failure.");
        };
        primary.StartListening();

        SingleInstanceService? secondaryInstance = null;
        var secondaryThread = new Thread(() =>
        {
            secondaryInstance = new SingleInstanceService(mutexName, activationEventName);
        });
        secondaryThread.Start();
        Assert.True(secondaryThread.Join(TimeSpan.FromSeconds(2)));
        var secondary = Assert.IsType<SingleInstanceService>(secondaryInstance);
        using (secondary)
        {
            Assert.False(secondary.IsPrimary);
            Assert.True(activationRaised.Wait(TimeSpan.FromSeconds(2)));
        }

        primary.Dispose();

        using var replacement = new SingleInstanceService(mutexName, activationEventName);
        Assert.True(replacement.IsPrimary);
    }
}
