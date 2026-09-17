using NotchBar.Core;
using Xunit;

namespace NotchBar.Tests;

public sealed class RelativeTimeFormatterTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-17T09:00:00Z");

    [Theory]
    [InlineData(0, "Updated just now")]
    [InlineData(4, "Updated just now")]
    [InlineData(5, "Updated 5s ago")]
    [InlineData(59, "Updated 59s ago")]
    [InlineData(60, "Updated 1m ago")]
    [InlineData(3599, "Updated 59m ago")]
    [InlineData(3600, "Updated 1h ago")]
    [InlineData(86399, "Updated 23h ago")]
    [InlineData(86400, "Updated 1d ago")]
    public void FormatUpdated_UsesCompactRelativeUnits(int ageSeconds, string expected)
    {
        Assert.Equal(expected, RelativeTimeFormatter.FormatUpdated(Now.AddSeconds(-ageSeconds), Now));
    }

    [Fact]
    public void FormatUpdated_FutureTimestamp_IsJustNow()
    {
        Assert.Equal("Updated just now", RelativeTimeFormatter.FormatUpdated(Now.AddSeconds(10), Now));
    }
}
