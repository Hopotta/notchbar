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
    [InlineData("title", "Line\nBreak")]
    [InlineData("title", "Column\tBreak")]
    [InlineData("text", "Line\nBreak")]
    [InlineData("text", "Column\tBreak")]
    [InlineData("secondaryText", "Line\nBreak")]
    [InlineData("secondaryText", "Column\tBreak")]
    public void Validate_RejectsControlsInSingleLineFields(string fieldName, string value)
    {
        var request = fieldName switch
        {
            "title" => ValidRequest() with { Title = value },
            "text" => ValidRequest() with { Text = value },
            "secondaryText" => ValidRequest() with { SecondaryText = value },
            _ => throw new ArgumentOutOfRangeException(nameof(fieldName))
        };

        var error = StatusItemValidation.Validate(request, "demo");

        Assert.NotNull(error);
        Assert.StartsWith(fieldName, error);
    }

    [Theory]
    [InlineData("title", "Line\nBreak")]
    [InlineData("text", "Column\tBreak")]
    public void ValidateNotification_RejectsControlsInSingleLineFields(string fieldName, string value)
    {
        var request = fieldName switch
        {
            "title" => new NotificationRequest { Title = value, Text = "Body" },
            "text" => new NotificationRequest { Title = "Notice", Text = value },
            _ => throw new ArgumentOutOfRangeException(nameof(fieldName))
        };

        var error = StatusItemValidation.ValidateNotification(request);

        Assert.NotNull(error);
        Assert.StartsWith(fieldName, error);
    }

    [Fact]
    public void Validate_AllowsTabsAndLineBreaksInDetail()
    {
        const string detail = "First line\r\nSecond line\tvalue";

        Assert.Null(StatusItemValidation.Validate(ValidRequest() with { Detail = detail }, "demo"));
        Assert.Null(StatusItemValidation.ValidateNotification(new NotificationRequest
        {
            Text = "Body",
            Detail = detail
        }));
    }

    [Fact]
    public void Validate_RejectsUnsafeDetailControlCharacters()
    {
        const string detail = "Unsafe\u0001detail";

        var itemError = StatusItemValidation.Validate(ValidRequest() with { Detail = detail }, "demo");
        var notificationError = StatusItemValidation.ValidateNotification(new NotificationRequest
        {
            Text = "Body",
            Detail = detail
        });

        Assert.StartsWith("detail", itemError);
        Assert.StartsWith("detail", notificationError);
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
    public void EqualPriorityItems_UseMostRecentUpdateForDisplay()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-09-17T00:00:00Z"));
        using var store = new StatusStore(time);

        store.Put(Request("First", priority: 50), "first");
        time.Advance(TimeSpan.FromSeconds(1));
        store.Put(Request("Second", priority: 50), "second");

        Assert.Equal("second", store.GetDisplayItem()?.Id);
    }

    [Fact]
    public void ItemAtExactExpiryBoundary_FallsBackToBuiltInClock()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-09-17T00:00:00Z"));
        using var store = new StatusStore(time);

        store.Put(Request("Temporary", priority: 100, ttlSeconds: 5), "temporary");
        Assert.Equal("temporary", store.GetDisplayItem()?.Id);

        time.Advance(TimeSpan.FromSeconds(5));

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

    [Fact]
    public void ExpiryTimer_RearmsToNearestExpiryAndCancelsWhenNoExpiringItemsRemain()
    {
        var start = DateTimeOffset.Parse("2026-09-17T00:00:00Z");
        var time = new ManualTimerTimeProvider(start);
        using var store = new StatusStore(time);
        var removedIds = new List<string>();
        store.Changed += (_, args) =>
        {
            if (args.Removed)
            {
                removedIds.Add(args.Item.Id);
            }
        };

        Assert.Equal(0, time.ActiveTimerCount);

        store.Put(Request("Long lived", ttlSeconds: 10), "long-lived");
        Assert.Equal(start.AddSeconds(10), time.NextTimerDue);

        time.Advance(TimeSpan.FromSeconds(3));
        store.Put(Request("Earlier expiry", ttlSeconds: 4), "earlier");
        Assert.Equal(start.AddSeconds(7), time.NextTimerDue);

        time.Advance(TimeSpan.FromSeconds(4));

        Assert.Equal(new[] { "earlier" }, removedIds);
        Assert.Equal(start.AddSeconds(10), time.NextTimerDue);
        Assert.Contains(store.GetActiveItems(), item => item.Id == "long-lived");

        Assert.True(store.Delete("long-lived", out _));
        Assert.Equal(0, time.ActiveTimerCount);
        Assert.Null(time.NextTimerDue);
    }

    [Fact]
    public void ExpiryEvents_ArriveInExpiryOrder()
    {
        var time = new ManualTimerTimeProvider(DateTimeOffset.Parse("2026-09-17T00:00:00Z"));
        using var store = new StatusStore(time);
        var removedIds = new List<string>();
        store.Changed += (_, args) =>
        {
            if (args.Removed)
            {
                removedIds.Add(args.Item.Id);
            }
        };

        store.Put(Request("Expires later", ttlSeconds: 5), "later");
        time.Advance(TimeSpan.FromSeconds(1));
        store.Put(Request("Expires first", ttlSeconds: 1), "first");

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(new[] { "first" }, removedIds);
        Assert.Contains(store.GetActiveItems(), item => item.Id == "later");

        time.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(new[] { "first", "later" }, removedIds);
    }

    [Fact]
    public void ConcurrentMutationsAndExpiry_StaySafeWithoutWallClockWaits()
    {
        var time = new ManualTimerTimeProvider(DateTimeOffset.Parse("2026-09-17T00:00:00Z"));
        using var store = new StatusStore(time);

        Parallel.For(0, 300, index =>
        {
            var id = $"entry-{index % 24}";
            store.Put(Request($"Update {index}", ttlSeconds: index % 30 + 1), id);
            if (index % 7 == 0)
            {
                store.Delete(id, out _);
            }
        });

        time.Advance(TimeSpan.FromDays(2));

        Assert.Single(store.GetActiveItems(), item => item.IsBuiltIn);
        Assert.Equal(0, time.ActiveTimerCount);
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

internal sealed class ManualTimerTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _utcNow = utcNow;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _utcNow;
        }
    }

    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (_gate)
        {
            _timers.Add(timer);
            timer.Change(dueTime, period);
        }

        return timer;
    }

    public int ActiveTimerCount
    {
        get
        {
            lock (_gate)
            {
                return _timers.Count(timer => timer.IsScheduled);
            }
        }
    }

    public DateTimeOffset? NextTimerDue
    {
        get
        {
            lock (_gate)
            {
                return _timers
                    .Where(timer => timer.DueAt is not null)
                    .Select(timer => timer.DueAt)
                    .Min();
            }
        }
    }

    public void Advance(TimeSpan duration)
    {
        ManualTimer[] dueTimers;
        lock (_gate)
        {
            _utcNow = _utcNow.Add(duration);
            dueTimers = _timers.Where(timer => timer.MarkDue(_utcNow)).ToArray();
        }

        foreach (var timer in dueTimers)
        {
            timer.Invoke();
        }
    }

    private sealed class ManualTimer(
        ManualTimerTimeProvider owner,
        TimerCallback callback,
        object? state) : ITimer
    {
        private bool _disposed;
        private DateTimeOffset? _dueAt;

        public DateTimeOffset? DueAt => _dueAt;
        public bool IsScheduled => !_disposed && _dueAt is not null;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                if (_disposed)
                {
                    return false;
                }

                _dueAt = dueTime == Timeout.InfiniteTimeSpan
                    ? null
                    : owner._utcNow.Add(dueTime);
                return true;
            }
        }

        public void Dispose()
        {
            lock (owner._gate)
            {
                _disposed = true;
                _dueAt = null;
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        public bool MarkDue(DateTimeOffset now)
        {
            if (_disposed || _dueAt is null || _dueAt.Value > now)
            {
                return false;
            }

            _dueAt = null;
            return true;
        }

        public void Invoke() => callback(state);
    }
}
