using System.Collections.Concurrent;
using System.Globalization;
using Timer = System.Threading.Timer;

namespace NotchBar.Core;

public sealed class StatusStore : IDisposable
{
    public const string ClockId = "clock";
    public const int MaxRegularItems = 64;
    public const int MaxNotifications = 32;

    private readonly object _mutationGate = new();
    private readonly ConcurrentDictionary<string, StatusItem> _items = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _timeProvider;
    private readonly Timer _expiryTimer;
    private bool _disposed;

    public StatusStore(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        var now = DateTime.Now;
        _items[ClockId] = new StatusItem
        {
            Id = ClockId,
            Title = "Clock",
            Text = now.ToString("HH:mm", CultureInfo.InvariantCulture),
            SecondaryText = now.ToString("ddd, MMM d", CultureInfo.InvariantCulture),
            Detail = "Local time",
            Priority = 0,
            TtlSeconds = 0,
            UpdatedAt = _timeProvider.GetUtcNow(),
            IsBuiltIn = true
        };

        _expiryTimer = new Timer(_ => RemoveExpired(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public event EventHandler<StatusStoreChangedEventArgs>? Changed;

    public static bool IsReservedId(string? id) =>
        string.Equals(id, ClockId, StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<StatusItem> GetActiveItems()
    {
        var now = _timeProvider.GetUtcNow();
        return _items.Values
            .Where(item => !item.IsExpired(now))
            .OrderByDescending(item => item.Priority)
            .ThenByDescending(item => item.UpdatedAt)
            .Select(item => item with { })
            .ToArray();
    }

    public StatusItem? GetDisplayItem()
    {
        var active = GetActiveItems();
        return active.FirstOrDefault(item => item.IsNotification) ?? active.FirstOrDefault();
    }

    public TimeSpan? GetRemainingNotificationLifetime()
    {
        var now = _timeProvider.GetUtcNow();
        DateTimeOffset? latestExpiry = null;

        foreach (var item in _items.Values)
        {
            if (!item.IsNotification || item.IsExpired(now) || item.TtlSeconds <= 0)
            {
                continue;
            }

            var expiry = item.UpdatedAt.AddSeconds(item.TtlSeconds);
            if (latestExpiry is null || expiry > latestExpiry.Value)
            {
                latestExpiry = expiry;
            }
        }

        return latestExpiry is null ? null : latestExpiry.Value - now;
    }

    public StatusItem Put(StatusItemRequest request, string id)
    {
        if (IsReservedId(id))
        {
            throw new InvalidOperationException($"'{id}' is reserved for a built-in item");
        }

        var now = _timeProvider.GetUtcNow();
        var item = new StatusItem
        {
            Id = id,
            Title = request.Title!.Trim(),
            Text = request.Text!.Trim(),
            SecondaryText = NormalizeOptional(request.SecondaryText),
            Detail = NormalizeOptional(request.Detail),
            Progress = request.Progress,
            Priority = request.Priority ?? 50,
            TtlSeconds = request.TtlSeconds!.Value,
            WakeOnUpdate = request.WakeOnUpdate ?? false,
            UpdatedAt = now
        };

        lock (_mutationGate)
        {
            var updatesActiveRegularItem =
                _items.TryGetValue(id, out var existing) &&
                !existing.IsBuiltIn &&
                !existing.IsNotification &&
                !existing.IsExpired(now);
            if (!updatesActiveRegularItem && CountActiveItems(now, notifications: false) >= MaxRegularItems)
            {
                throw new StatusStoreCapacityException("regular item", MaxRegularItems);
            }

            _items[id] = item;
        }

        Changed?.Invoke(this, new StatusStoreChangedEventArgs(item, item.WakeOnUpdate, false));
        return item;
    }

    public StatusItem AddNotification(NotificationRequest request)
    {
        var now = _timeProvider.GetUtcNow();
        var item = new StatusItem
        {
            Id = $"notification-{Guid.NewGuid():N}",
            Title = string.IsNullOrWhiteSpace(request.Title) ? "Notification" : request.Title.Trim(),
            Text = request.Text!.Trim(),
            Detail = NormalizeOptional(request.Detail),
            Priority = request.Priority ?? 60,
            TtlSeconds = request.TtlSeconds ?? 8,
            WakeOnUpdate = true,
            UpdatedAt = now,
            IsNotification = true
        };

        lock (_mutationGate)
        {
            if (CountActiveItems(now, notifications: true) >= MaxNotifications)
            {
                throw new StatusStoreCapacityException("notification", MaxNotifications);
            }

            _items[item.Id] = item;
        }

        Changed?.Invoke(this, new StatusStoreChangedEventArgs(item, true, false));
        return item;
    }

    public bool Delete(string id, out StatusItem? removed)
    {
        if (IsReservedId(id))
        {
            removed = null;
            return false;
        }

        bool deleted;
        lock (_mutationGate)
        {
            deleted = _items.TryRemove(id, out removed);
        }

        if (deleted && removed is not null)
        {
            Changed?.Invoke(this, new StatusStoreChangedEventArgs(removed, false, true));
        }

        return deleted;
    }

    public void UpdateClock(DateTime now)
    {
        var item = new StatusItem
        {
            Id = ClockId,
            Title = "Clock",
            Text = now.ToString("HH:mm", CultureInfo.InvariantCulture),
            SecondaryText = now.ToString("ddd, MMM d", CultureInfo.InvariantCulture),
            Detail = "Local time",
            Priority = 0,
            TtlSeconds = 0,
            UpdatedAt = _timeProvider.GetUtcNow(),
            IsBuiltIn = true
        };

        lock (_mutationGate)
        {
            _items[ClockId] = item;
        }

        Changed?.Invoke(this, new StatusStoreChangedEventArgs(item, false, false));
    }

    private int CountActiveItems(DateTimeOffset now, bool notifications) =>
        _items.Values.Count(item =>
            !item.IsBuiltIn && item.IsNotification == notifications && !item.IsExpired(now));

    private void RemoveExpired()
    {
        if (_disposed)
        {
            return;
        }

        var now = _timeProvider.GetUtcNow();
        List<StatusItem>? removedItems = null;
        lock (_mutationGate)
        {
            var collection = (ICollection<KeyValuePair<string, StatusItem>>)_items;
            foreach (var pair in _items)
            {
                if (pair.Value.IsExpired(now) && collection.Remove(pair))
                {
                    (removedItems ??= []).Add(pair.Value);
                }
            }
        }

        if (removedItems is null)
        {
            return;
        }

        foreach (var removedItem in removedItems)
        {
            Changed?.Invoke(this, new StatusStoreChangedEventArgs(removedItem, false, true));
        }
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    public void Dispose()
    {
        _disposed = true;
        _expiryTimer.Dispose();
    }
}

public sealed class StatusStoreCapacityException(string entryKind, int capacity)
    : InvalidOperationException($"Active {entryKind} limit of {capacity} has been reached")
{
    public string EntryKind { get; } = entryKind;

    public int Capacity { get; } = capacity;
}

public sealed class StatusStoreChangedEventArgs(StatusItem item, bool wakeOnUpdate, bool removed) : EventArgs
{
    public StatusItem Item { get; } = item;
    public bool WakeOnUpdate { get; } = wakeOnUpdate;
    public bool Removed { get; } = removed;
}
