using NotchBar.Services;
using Xunit;

namespace NotchBar.Tests;

public sealed class WindowBlurServiceTests
{
    [Theory]
    [InlineData(19045)]
    [InlineData(22621)]
    [InlineData(26100)]
    public void LayeredWindow_AlwaysSelectsAccentAcrylicOnSupportedWindows(int build)
    {
        var mode = WindowBlurService.SelectBackdropMode(
            isWindows: true,
            highContrast: false,
            isLayeredWindow: true,
            new Version(10, 0, build));

        Assert.Equal(WindowBackdropMode.AccentAcrylic, mode);
    }

    [Fact]
    public void NonLayeredWindows11Window_SelectsDocumentedSystemBackdrop()
    {
        var mode = WindowBlurService.SelectBackdropMode(
            isWindows: true,
            highContrast: false,
            isLayeredWindow: false,
            new Version(10, 0, 22621));

        Assert.Equal(WindowBackdropMode.SystemDesktopAcrylic, mode);
    }

    [Theory]
    [InlineData(false, false, 19045)]
    [InlineData(true, true, 19045)]
    [InlineData(true, false, 7601)]
    public void UnsupportedOrAccessibilityModes_SelectFallback(
        bool isWindows,
        bool highContrast,
        int build)
    {
        var major = build == 7601 ? 6 : 10;
        var mode = WindowBlurService.SelectBackdropMode(
            isWindows,
            highContrast,
            isLayeredWindow: true,
            new Version(major, major == 10 ? 0 : 1, build));

        Assert.Equal(WindowBackdropMode.Fallback, mode);
    }

    [Fact]
    public void AccentTint_IsPackedAsAabbggrr()
    {
        var packed = WindowBlurService.PackAccentColor(
            alpha: 0x1C,
            red: 0x12,
            green: 0x34,
            blue: 0x56);

        Assert.Equal(0x1C563412u, packed);
    }
}
