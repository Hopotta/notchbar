using System.Collections.Concurrent;

namespace NotchBar.Core;

public sealed class StatusStore : IDisposable
{
    private readonly ConcurrentDictionary<string, StatusItem> _items = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _expiryTimer;
    private bool _disposed;

    public StatusStore()
    {
        var now = DateTime.Now;
        _items["clock"] = new StatusItem
        {
            Id = "clock",
            Title = "Clock",
            Text = now.ToString("HH:mm"),
            SecondaryText = now.ToString("ddd, MMM d"),
            Detail = "Local time",
            Priority = 0,
            TtlSeconds = 0,
            UpdatedAt = DateTimeOffset.UtcNow,
            IsBuiltIn = true
        };

        _expiryTimer = new Timer(_ => RemoveExpired(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public event EventHandler<StatusStoreChangedEventArgs>? Changed;

    public IReadOnlyList<StatusItem> GetActiveItems()
    {
        var now = DateTimeOffset.UtcNow;
        return _items.Values
            .Where(item => !item.IsExpired(now))
            .OrderByDescending(item => item.Priority)
            .ThenByDescending(item => item.UpdatedAt)
            .Select(item => item with { })
            .ToArray();
    }

    public StatusItem? GetDisplayItem()
    {
        return GetActiveItems().FirstOrDefault();
    }

    public StatusItem Put(StatusItemRequest request, string id)
    {
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
            UpdatedAt = DateTimeOffset.UtcNow
        };

        _items[id] = item;
        Changed?.Invoke(this, new StatusStoreChangedEventArgs(item, item.WakeOnUpdate, false));
        return item;
    }

    public StatusItem AddNotification(NotificationRequest request)
    {
        var item = new StatusItem
        {
            Id = $"notification-{Guid.NewGuid():N}",
            Title = string.IsNullOrWhiteSpace(request.Title) ? "Notification" : request.Title.Trim(),
            Text = request.Text!.Trim(),
            Detail = NormalizeOptional(request.Detail),
            Priority = request.Priority ?? 60,
            TtlSeconds = request.TtlSeconds ?? 8,
            WakeOnUpdate = true,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        _items[item.Id] = item;
        Changed?.Invoke(this, new StatusStoreChangedEventArgs(item, true, false));
        return item;
    }

    public bool Delete(string id, out StatusItem? removed)
    {
        if (string.Equals(id, "clock", StringComparison.OrdinalIgnoreCase))
        {
            removed = null;
            return false;
        }

        var deleted = _items.TryRemove(id, out removed);
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
            Id = "clock",
            Title = "Clock",
            Text = now.ToString("HH:mm"),
            SecondaryText = now.ToString("ddd, MMM d"),
            Detail = TimeZoneInfo.Local.DisplayName,
            Priority = 0,
            TtlSeconds = 0,
            UpdatedAt = DateTimeOffset.UtcNow,
            IsBuiltIn = true
        };

        _items["clock"] = item;
        Changed?.Invoke(this, new StatusStoreChangedEventArgs(item, false, false));
    }

    private void RemoveExpired()
    {
        if (_disposed)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var pair in _items)
        {
            if (pair.Value.IsExpired(now) && _items.TryRemove(pair.Key, out var removed))
            {
                Changed?.Invoke(this, new StatusStoreChangedEventArgs(removed, false, true));
            }
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

public sealed class StatusStoreChangedEventArgs(StatusItem item, bool wakeOnUpdate, bool removed) : EventArgs
{
    public StatusItem Item { get; } = item;
    public bool WakeOnUpdate { get; } = wakeOnUpdate;
    public bool Removed { get; } = removed;
}
