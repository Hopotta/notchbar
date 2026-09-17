using NotchBar.Core;
using Xunit;

namespace NotchBar.Tests;

public sealed class NotificationDisplayTests
{
    [Fact]
    public void ActiveNotification_PreemptsHigherPriorityRegularItem()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-09-17T09:00:00Z"));
        using var store = new StatusStore(time);

        store.Put(Status("Critical work", priority: 900), "agent");
        var notification = store.AddNotification(Notification("Build finished", priority: 10));

        Assert.Equal(notification.Id, store.GetDisplayItem()?.Id);
        Assert.True(store.GetDisplayItem()?.IsNotification);
    }

    [Fact]
    public void NotificationPriority_OrdersConcurrentNotifications()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-09-17T09:00:00Z"));
        using var store = new StatusStore(time);

        var important = store.AddNotification(Notification("Important", priority: 100));
        time.Advance(TimeSpan.FromSeconds(1));
        _ = store.AddNotification(Notification("Newer but lower priority", priority: 20));

        Assert.Equal(important.Id, store.GetDisplayItem()?.Id);
    }

    [Fact]
    public void ExpiredNotification_FallsBackToRegularStatus()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-09-17T09:00:00Z"));
        using var store = new StatusStore(time);

        store.Put(Status("Working", priority: 80), "agent");
        _ = store.AddNotification(Notification("Done", priority: 60, ttlSeconds: 5));
        Assert.True(store.GetDisplayItem()?.IsNotification);

        time.Advance(TimeSpan.FromSeconds(6));

        Assert.Equal("agent", store.GetDisplayItem()?.Id);
        Assert.False(store.GetDisplayItem()?.IsNotification);
    }

    private static StatusItemRequest Status(string text, int priority) => new()
    {
        Title = "Agent",
        Text = text,
        Priority = priority,
        TtlSeconds = 30
    };

    private static NotificationRequest Notification(string text, int priority, int ttlSeconds = 8) => new()
    {
        Title = "Notice",
        Text = text,
        Priority = priority,
        TtlSeconds = ttlSeconds
    };

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow = _utcNow.Add(duration);
    }
}
