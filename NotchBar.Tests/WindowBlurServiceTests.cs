using NotchBar.Services;
using Xunit;

namespace NotchBar.Tests;

public sealed class WindowBlurServiceTests
{
    [Theory]
    [InlineData(22000, BackdropActivationMode.Windows11HostBackdrop)]
    [InlineData(22621, BackdropActivationMode.Windows11HostBackdrop)]
    [InlineData(19041, BackdropActivationMode.Windows10HostBackdrop)]
    [InlineData(19045, BackdropActivationMode.Windows10HostBackdrop)]
    [InlineData(19040, BackdropActivationMode.Fallback)]
    [InlineData(19046, BackdropActivationMode.Fallback)]
    public void ActivationMatrix_SelectsOnlySupportedSamplingEnablers(
        int build,
        BackdropActivationMode expected)
    {
        var selected = CompositionBackdropHost.SelectActivationMode(
            isWindows: true,
            highContrast: false,
            compositionEnabled: true,
            build);

        Assert.Equal(expected, selected);
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    public void ActivationMatrix_RejectsUnsupportedRuntimeState(
        bool isWindows,
        bool highContrast,
        bool compositionEnabled)
    {
        Assert.Equal(
            BackdropActivationMode.Fallback,
            CompositionBackdropHost.SelectActivationMode(
                isWindows,
                highContrast,
                compositionEnabled,
                windowsBuild: 22621));
    }

    [Theory]
    [InlineData(96, 20)]
    [InlineData(120, 25)]
    [InlineData(144, 30)]
    public void Metrics_ScaleBottomRadiusAndKeepSquareTop(uint dpi, float expectedRadius)
    {
        var metrics = CompositionBackdropHost.CalculateMetrics(424, 148, dpi);

        Assert.Equal(424, metrics.VisualSizePixels.X);
        Assert.Equal(148, metrics.VisualSizePixels.Y);
        Assert.Equal(0, metrics.TopLeftRadiusPixels);
        Assert.Equal(0, metrics.TopRightRadiusPixels);
        Assert.Equal(expectedRadius, metrics.BottomLeftRadiusPixels);
        Assert.Equal(expectedRadius, metrics.BottomRightRadiusPixels);
    }

    [Theory]
    [InlineData(96, 18.5)]
    [InlineData(120, 18.5)]
    [InlineData(144, 18.5)]
    public void Metrics_ClampRadiusForCompactHeight(uint dpi, float expectedRadius)
    {
        var metrics = CompositionBackdropHost.CalculateMetrics(286, 37, dpi);

        Assert.Equal(expectedRadius, metrics.BottomLeftRadiusPixels);
        Assert.Equal(expectedRadius, metrics.BottomRightRadiusPixels);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void MaskedRoot_RemainsDetachedUntilEveryGateSucceeds(
        bool proofCommitted,
        bool maskLoaded,
        bool generationCurrent)
    {
        Assert.False(CompositionBackdropHost.CanAttachMaskedRoot(
            proofCommitted,
            maskLoaded,
            generationCurrent));
    }

    [Fact]
    public void MaskedRoot_AttachesOnlyAfterProofAndMaskForCurrentGeneration()
    {
        Assert.True(CompositionBackdropHost.CanAttachMaskedRoot(
            proofCommitSucceeded: true,
            maskLoadSucceeded: true,
            generationCurrent: true));
    }
}
