using NotchBar.Services;
using Xunit;

namespace NotchBar.Tests;

public sealed class ThemeScheduleTests
{
    [Theory]
    [InlineData(5, 59, 0, 6)]
    [InlineData(6, 0, 0, 18)]
    [InlineData(17, 59, 0, 18)]
    [InlineData(18, 0, 1, 6)]
    [InlineData(23, 59, 1, 6)]
    public void NextThemeBoundary_ReturnsNextStrictBoundary(
        int hour,
        int minute,
        int expectedDayOffset,
        int expectedHour)
    {
        var localTime = new DateTime(2026, 9, 26, hour, minute, 0, DateTimeKind.Unspecified);

        var boundary = ThemeService.NextThemeBoundary(localTime);

        Assert.Equal(new DateTime(2026, 9, 26, 0, 0, 0, DateTimeKind.Unspecified).AddDays(expectedDayOffset)
            .AddHours(expectedHour), boundary);
    }

    [Theory]
    [InlineData(5, true)]
    [InlineData(6, false)]
    [InlineData(17, false)]
    [InlineData(18, true)]
    [InlineData(23, true)]
    public void IsDarkHours_UsesSixAndEighteenHourBoundaries(int hour, bool expected)
    {
        Assert.Equal(expected, ThemeService.IsDarkHours(new DateTime(2026, 9, 26, hour, 0, 0)));
    }

    [Fact]
    public void GetDelayUntilNextThemeBoundary_UsesCurrentTimeZoneRules()
    {
        var utcNow = new DateTime(2026, 1, 14, 20, 30, 0, DateTimeKind.Utc);
        var tokyo = TimeZoneInfo.CreateCustomTimeZone(
            "UTC+09",
            TimeSpan.FromHours(9),
            "UTC+09",
            "UTC+09");
        var tokyoLocalTime = TimeZoneInfo.ConvertTimeFromUtc(utcNow, tokyo);
        var utcLocalTime = TimeZoneInfo.ConvertTimeFromUtc(utcNow, TimeZoneInfo.Utc);

        var tokyoDelay = ThemeService.GetDelayUntilNextThemeBoundary(tokyoLocalTime, utcNow, tokyo);
        var utcDelay = ThemeService.GetDelayUntilNextThemeBoundary(utcLocalTime, utcNow, TimeZoneInfo.Utc);

        Assert.Equal(TimeSpan.FromMinutes(30), tokyoDelay);
        Assert.Equal(TimeSpan.FromHours(9.5), utcDelay);
    }
}
