namespace NotchBar.Core;

public static class RelativeTimeFormatter
{
    public static string FormatUpdated(DateTimeOffset updatedAt, DateTimeOffset now)
    {
        var age = now - updatedAt;
        if (age <= TimeSpan.FromSeconds(4))
        {
            return "Updated just now";
        }

        if (age < TimeSpan.FromMinutes(1))
        {
            return $"Updated {Math.Max(1, (int)age.TotalSeconds)}s ago";
        }

        if (age < TimeSpan.FromHours(1))
        {
            return $"Updated {Math.Max(1, (int)age.TotalMinutes)}m ago";
        }

        if (age < TimeSpan.FromDays(1))
        {
            return $"Updated {Math.Max(1, (int)age.TotalHours)}h ago";
        }

        return $"Updated {Math.Max(1, (int)age.TotalDays)}d ago";
    }
}
