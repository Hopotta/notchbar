using System.Text.Json.Serialization;

namespace NotchBar.Core;

public sealed record StatusItem
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Text { get; init; }
    public string? SecondaryText { get; init; }
    public string? Detail { get; init; }
    public double? Progress { get; init; }
    public int Priority { get; init; }
    public int TtlSeconds { get; init; }
    public bool WakeOnUpdate { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }

    [JsonIgnore]
    public bool IsBuiltIn { get; init; }

    [JsonIgnore]
    public bool IsNotification { get; init; }

    public bool IsExpired(DateTimeOffset now)
    {
        return !IsBuiltIn && TtlSeconds > 0 && UpdatedAt.AddSeconds(TtlSeconds) <= now;
    }
}

public sealed record StatusItemRequest
{
    public string? Title { get; init; }
    public string? Text { get; init; }
    public string? SecondaryText { get; init; }
    public string? Detail { get; init; }
    public double? Progress { get; init; }
    public int? Priority { get; init; }
    public int? TtlSeconds { get; init; }
    public bool? WakeOnUpdate { get; init; }
}

public sealed record NotificationRequest
{
    public string? Title { get; init; }
    public string? Text { get; init; }
    public string? Detail { get; init; }
    public int? TtlSeconds { get; init; }
    public int? Priority { get; init; }
}

public static class StatusItemValidation
{
    public const int MaxIdLength = 64;
    public const int MaxTitleLength = 64;
    public const int MaxTextLength = 160;
    public const int MaxDetailLength = 1000;
    public const int MaxTtlSeconds = 24 * 60 * 60;

    public static string? ValidateId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > MaxIdLength)
        {
            return $"id must be 1-{MaxIdLength} characters";
        }

        if (id.Any(character => !(char.IsLetterOrDigit(character) || character is '-' or '_' or '.')))
        {
            return "id may contain only letters, digits, '-', '_' and '.'";
        }

        return null;
    }

    public static string? Validate(StatusItemRequest request, string id)
    {
        return ValidateFields(
            id,
            request.Title,
            request.Text,
            request.SecondaryText,
            request.Detail,
            request.Progress,
            request.Priority,
            request.TtlSeconds);
    }

    public static string? ValidateNotification(NotificationRequest request)
    {
        return ValidateFields(
            "notification",
            request.Title ?? "Notification",
            request.Text,
            null,
            request.Detail,
            null,
            request.Priority ?? 60,
            request.TtlSeconds ?? 8);
    }

    private static string? ValidateFields(
        string id,
        string? title,
        string? text,
        string? secondaryText,
        string? detail,
        double? progress,
        int? priority,
        int? ttlSeconds)
    {
        var idError = ValidateId(id);
        if (idError is not null)
        {
            return idError;
        }

        if (string.IsNullOrWhiteSpace(title) || title.Length > MaxTitleLength)
        {
            return $"title is required and must be at most {MaxTitleLength} characters";
        }

        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxTextLength)
        {
            return $"text is required and must be at most {MaxTextLength} characters";
        }

        if (secondaryText is not null && secondaryText.Length > MaxTextLength)
        {
            return $"secondaryText must be at most {MaxTextLength} characters";
        }

        if (detail is not null && detail.Length > MaxDetailLength)
        {
            return $"detail must be at most {MaxDetailLength} characters";
        }

        if (progress is not null && (double.IsNaN(progress.Value) || double.IsInfinity(progress.Value) || progress.Value < 0 || progress.Value > 1))
        {
            return "progress must be between 0 and 1";
        }

        if (priority is not null && (priority.Value < -100 || priority.Value > 1000))
        {
            return "priority must be between -100 and 1000";
        }

        if (ttlSeconds is null || ttlSeconds.Value <= 0 || ttlSeconds.Value > MaxTtlSeconds)
        {
            return $"ttlSeconds must be between 1 and {MaxTtlSeconds}";
        }

        if (ContainsUnsafeControlCharacter(title) || ContainsUnsafeControlCharacter(text) ||
            ContainsUnsafeControlCharacter(secondaryText) || ContainsUnsafeControlCharacter(detail))
        {
            return "text fields may not contain control characters";
        }

        return null;
    }

    private static bool ContainsUnsafeControlCharacter(string? value)
    {
        return value?.Any(character => char.IsControl(character) && character is not '\r' and not '\n' and not '\t') == true;
    }
}
