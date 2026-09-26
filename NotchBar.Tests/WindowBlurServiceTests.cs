using NotchBar.Services;
using Xunit;

namespace NotchBar.Tests;

public sealed class WindowBlurServiceTests
{
    [Theory]
    [InlineData(true, false, true, 22000, BackdropActivationMode.Windows11HostBackdrop)]
    [InlineData(true, false, true, 19045, BackdropActivationMode.Windows10HostBackdrop)]
    [InlineData(true, false, true, 19041, BackdropActivationMode.Windows10HostBackdrop)]
    [InlineData(true, false, true, 19046, BackdropActivationMode.Fallback)]
    [InlineData(false, false, true, 22621, BackdropActivationMode.Fallback)]
    [InlineData(true, true, true, 22621, BackdropActivationMode.Fallback)]
    [InlineData(true, false, false, 22621, BackdropActivationMode.Fallback)]
    public void ActivationMode_UsesOnlySupportedCompositionBuilds(
        bool windows,
        bool highContrast,
        bool composition,
        int build,
        BackdropActivationMode expected)
    {
        Assert.Equal(
            expected,
            BackdropWindowPolicy.SelectActivationMode(windows, highContrast, composition, build));
    }

    [Fact]
    public void ActiveStyle_AddsLayeredOnlyAfterTheNonLayeredProofContract()
    {
        const uint layered = 0x00080000;
        Assert.Equal(0u, BackdropWindowPolicy.ExtendedWindowStyle & layered);
        Assert.Equal(
            BackdropWindowPolicy.ExtendedWindowStyle | layered,
            BackdropWindowPolicy.ActiveExtendedWindowStyle);
        Assert.True(BackdropWindowPolicy.HasTransparentInputContract(
            BackdropWindowPolicy.ActiveExtendedWindowStyle));
        Assert.False(BackdropWindowPolicy.HasTransparentInputContract(
            BackdropWindowPolicy.ActiveExtendedWindowStyle & ~0x00000020u));
    }

    [Theory]
    [InlineData(96, 1f)]
    [InlineData(120, 1.25f)]
    [InlineData(144, 1.5f)]
    public void MaskInsets_KeepTheSquareTopAndScaleRoundedBottomWithDpi(
        uint dpi,
        float expectedScale)
    {
        var insets = BackdropWindowPolicy.CalculateMaskInsets(dpi);

        Assert.Equal(0f, insets.TopPixels);
        Assert.Equal(20f, insets.BottomPixels);
        Assert.Equal(20f, insets.LeftPixels);
        Assert.Equal(20f, insets.RightPixels);
        Assert.Equal(expectedScale, insets.InsetScale);
    }

    [Fact]
    public void MaskInsets_DefaultUnknownDpiTo96()
    {
        var insets = BackdropWindowPolicy.CalculateMaskInsets(0);

        Assert.Equal(1f, insets.InsetScale);
    }

    [Fact]
    public void Lifecycle_RequiresCommittedRoundedClipAndVerifiedTransparentInput()
    {
        var gate = new BackdropActivationGate();
        var generation = gate.BeginInitialization();

        Assert.True(gate.MarkClipCommitted(generation));
        Assert.False(gate.CanShow);
        Assert.True(gate.MarkInputContractVerified(generation, verified: true));
        Assert.True(gate.Activate(generation));
        Assert.True(gate.CanShow);

        gate.SetSuppressed(true);
        Assert.Equal(BackdropLifecycleState.Suppressed, gate.State);
        Assert.False(gate.CanShow);

        gate.SetSuppressed(false);
        Assert.Equal(BackdropLifecycleState.Active, gate.State);
        Assert.True(gate.CanShow);
    }

    [Fact]
    public void Lifecycle_RejectsUnverifiedInputAndStaleInitialization()
    {
        var gate = new BackdropActivationGate();
        var staleGeneration = gate.BeginInitialization();
        var currentGeneration = gate.BeginInitialization();

        Assert.False(gate.MarkClipCommitted(staleGeneration));
        Assert.True(gate.MarkClipCommitted(currentGeneration));
        Assert.False(gate.MarkInputContractVerified(currentGeneration, verified: false));
        Assert.False(gate.Activate(currentGeneration));
        Assert.False(gate.CanShow);
    }

}
