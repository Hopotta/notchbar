using NotchBar.Services;
using Xunit;

namespace NotchBar.Tests;

public sealed class ThemeServiceTests
{
    [Theory]
    [InlineData(5, 59, true)]
    [InlineData(6, 0, false)]
    [InlineData(17, 59, false)]
    [InlineData(18, 0, true)]
    [InlineData(23, 59, true)]
    public void IsDarkHours_UsesLocalEveningAndNightWindow(int hour, int minute, bool expected)
    {
        var localTime = new DateTime(2026, 1, 1, hour, minute, 0);
        Assert.Equal(expected, ThemeService.IsDarkHours(localTime));
    }
}
