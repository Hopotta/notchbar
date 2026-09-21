using NotchBar.Core;
using Xunit;

namespace NotchBar.Tests;

public sealed class NotchStateMachineTests
{
    [Fact]
    public void WakeExpandCollapse_FollowsExpectedVisualStates()
    {
        var state = new NotchStateMachine();

        Assert.Equal(NotchState.Hidden, state.Current);

        state.Wake();
        Assert.Equal(NotchState.Compact, state.Current);

        state.Expand();
        Assert.Equal(NotchState.Expanded, state.Current);

        state.Collapse();
        Assert.Equal(NotchState.Compact, state.Current);
    }

    [Fact]
    public void Pinning_PreservesAndChangesVisualStateIndependently()
    {
        var state = new NotchStateMachine();
        state.Wake();
        state.Expand();

        state.TogglePinned();
        Assert.True(state.IsPinned);
        Assert.Equal(NotchState.Expanded, state.VisualState);

        state.Collapse();
        Assert.True(state.IsPinned);
        Assert.Equal(NotchState.Compact, state.VisualState);

        state.TogglePinned();
        Assert.False(state.IsPinned);
        Assert.Equal(NotchState.Compact, state.Current);
    }
}

public sealed class StatusItemValidationTests
{
    [Fact]
    public void Validate_RejectsInvalidTtlAndProgress()
    {
        var badTtl = ValidRequest() with { TtlSeconds = 0 };
        Assert.Contains("ttlSeconds", StatusItemValidation.Validate(badTtl, "demo"));

        var badProgress = ValidRequest() with { Progress = 1.01 };
        Assert.Contains("progress", StatusItemValidation.Validate(badProgress, "demo"));
    }

    [Theory]
    [InlineData("good-id")]
    [InlineData("good.id_2")]
    public void ValidateId_AcceptsSupportedCharacters(string id)
    {
        Assert.Null(StatusItemValidation.ValidateId(id));
    }

    [Fact]
    public void ValidateId_RejectsUnsupportedCharacters()
    {
        Assert.NotNull(StatusItemValidation.ValidateId("bad/id"));
        Assert.NotNull(StatusItemValidation.ValidateId("bad id"));
    }

    private static StatusItemRequest ValidRequest() => new()
    {
        Title = "Agent",
        Text = "Working",
        TtlSeconds = 10
    };
}

public sealed class StatusStoreTests
{
    [Fact]
    public void HigherPriorityItem_IsSelectedForDisplay()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-09-17T00:00:00Z"));
        using var store = new StatusStore(time);

        store.Put(Request("Low", priority: 10), "low");
        time.Advance(TimeSpan.FromSeconds(1));
        store.Put(Request("High", priority: 90), "high");

        Assert.Equal("high", store.GetDisplayItem()?.Id);
    }

    [Fact]
    public void ExpiredItem_FallsBackToBuiltInClock()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-09-17T00:00:00Z"));
        using var store = new StatusStore(time);

        store.Put(Request("Temporary", priority: 100, ttlSeconds: 5), "temporary");
        Assert.Equal("temporary", store.GetDisplayItem()?.Id);

        time.Advance(TimeSpan.FromSeconds(6));

        Assert.Equal(StatusStore.ClockId, store.GetDisplayItem()?.Id);
    }

    [Fact]
    public void ClockId_IsReservedAndCannotBeOverwritten()
    {
        using var store = new StatusStore();

        Assert.True(StatusStore.IsReservedId("clock"));
        Assert.True(StatusStore.IsReservedId("CLOCK"));
        Assert.Throws<InvalidOperationException>(() => store.Put(Request("Fake clock"), "clock"));
    }

    [Fact]
    public void RemainingNotificationLifetime_TracksLatestActiveExpiry()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-09-17T00:00:00Z"));
        using var store = new StatusStore(time);

        store.AddNotification(new NotificationRequest
        {
            Text = "First",
            Priority = 100,
            TtlSeconds = 4
        });

        time.Advance(TimeSpan.FromSeconds(1));
        store.AddNotification(new NotificationRequest
        {
            Text = "Second",
            Priority = 10,
            TtlSeconds = 10
        });

        Assert.Equal(TimeSpan.FromSeconds(10), store.GetRemainingNotificationLifetime());

        time.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal(TimeSpan.FromSeconds(6), store.GetRemainingNotificationLifetime());

        time.Advance(TimeSpan.FromSeconds(7));
        Assert.Null(store.GetRemainingNotificationLifetime());
    }

    [Fact]
    public void RegularItemCapacity_RejectsNewIdsBeyondLimit()
    {
        using var store = new StatusStore();

        for (var index = 0; index < StatusStore.MaxRegularItems; index++)
        {
            store.Put(Request($"Item {index}"), $"item-{index}");
        }

        var exception = Assert.Throws<StatusStoreCapacityException>(
            () => store.Put(Request("Overflow"), "overflow"));

        Assert.Equal("regular item", exception.EntryKind);
        Assert.Equal(StatusStore.MaxRegularItems, exception.Capacity);
        Assert.Equal(
            StatusStore.MaxRegularItems,
            store.GetActiveItems().Count(item => !item.IsBuiltIn && !item.IsNotification));
    }

    [Fact]
    public void RegularItemCapacity_AllowsExistingIdUpdateAtLimit()
    {
        using var store = new StatusStore();

        for (var index = 0; index < StatusStore.MaxRegularItems; index++)
        {
            store.Put(Request($"Item {index}"), $"item-{index}");
        }

        var updated = store.Put(Request("Updated"), "item-0");

        Assert.Equal("Updated", updated.Text);
        Assert.Equal(
            "Updated",
            store.GetActiveItems().Single(item => item.Id == "item-0").Text);
    }

    [Fact]
    public void RegularItemCapacity_ExpiredItemsReleaseSlotsImmediately()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-09-17T00:00:00Z"));
        using var store = new StatusStore(time);

        for (var index = 0; index < StatusStore.MaxRegularItems; index++)
        {
            store.Put(Request($"Item {index}", ttlSeconds: 1), $"item-{index}");
        }

        time.Advance(TimeSpan.FromSeconds(2));

        var replacement = store.Put(Request("Replacement"), "replacement");

        Assert.Equal("replacement", replacement.Id);
        Assert.Single(store.GetActiveItems(), item => !item.IsBuiltIn);
    }

    [Fact]
    public void NotificationCapacity_RejectsNotificationsBeyondLimit()
    {
        using var store = new StatusStore();

        for (var index = 0; index < StatusStore.MaxNotifications; index++)
        {
            store.AddNotification(Notification($"Notification {index}"));
        }

        var exception = Assert.Throws<StatusStoreCapacityException>(
            () => store.AddNotification(Notification("Overflow")));

        Assert.Equal("notification", exception.EntryKind);
        Assert.Equal(StatusStore.MaxNotifications, exception.Capacity);
        Assert.Equal(
            StatusStore.MaxNotifications,
            store.GetActiveItems().Count(item => item.IsNotification));
    }

    [Fact]
    public void ConcurrentRegularAdmissions_NeverExceedCapacity()
    {
        using var store = new StatusStore();
        var admitted = 0;
        var rejected = 0;
        var attempts = StatusStore.MaxRegularItems + 16;

        Parallel.For(0, attempts, index =>
        {
            try
            {
                store.Put(Request($"Item {index}"), $"item-{index}");
                Interlocked.Increment(ref admitted);
            }
            catch (StatusStoreCapacityException)
            {
                Interlocked.Increment(ref rejected);
            }
        });

        Assert.Equal(StatusStore.MaxRegularItems, admitted);
        Assert.Equal(attempts - StatusStore.MaxRegularItems, rejected);
        Assert.Equal(
            StatusStore.MaxRegularItems,
            store.GetActiveItems().Count(item => !item.IsBuiltIn && !item.IsNotification));
    }

    private static NotificationRequest Notification(string text, int ttlSeconds = 10) => new()
    {
        Text = text,
        TtlSeconds = ttlSeconds
    };

    private static StatusItemRequest Request(string text, int priority = 50, int ttlSeconds = 10) => new()
    {
        Title = "Test",
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
