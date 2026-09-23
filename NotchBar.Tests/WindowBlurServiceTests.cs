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
    public void ActiveStyle_AddsOnlyLayeredAfterTheNonLayeredProofContract()
    {
        const uint layered = 0x00080000;
        Assert.Equal(0u, BackdropWindowPolicy.ExtendedWindowStyle & layered);
        Assert.Equal(
            BackdropWindowPolicy.ExtendedWindowStyle | layered,
            BackdropWindowPolicy.ActiveExtendedWindowStyle);
    }

    [Fact]
    public void Lifecycle_RequiresClipFrostAndPointerThenSupportsSuppression()
    {
        var gate = new BackdropActivationGate();
        var generation = gate.BeginInitialization();
        Assert.True(gate.MarkClipCommitted(generation));
        Assert.True(gate.BeginProbe(generation, frostObserved: true));
        Assert.True(gate.CompleteProbe(generation, VerifiedEvidence()));
        Assert.True(gate.CanShow);

        gate.SetSuppressed(true);
        Assert.Equal(BackdropLifecycleState.Suppressed, gate.State);
        Assert.False(gate.CanShow);

        gate.SetSuppressed(false);
        Assert.Equal(BackdropLifecycleState.Active, gate.State);
        Assert.True(gate.CanShow);
    }

    [Fact]
    public void Lifecycle_RejectsPointerPassWhenFrostWasNotObserved()
    {
        var gate = new BackdropActivationGate();
        var generation = gate.BeginInitialization();
        Assert.True(gate.MarkClipCommitted(generation));
        Assert.True(gate.BeginProbe(generation, frostObserved: false));
        Assert.False(gate.CompleteProbe(generation, VerifiedEvidence()));
        Assert.Equal(BackdropLifecycleState.Fallback, gate.State);
        Assert.False(gate.CanShow);
    }

    [Fact]
    public void ImmediatePairedCommitFailure_CannotPublishAndCleansUpOnce()
    {
        var publication = new BackdropActivationPublicationGate();
        var companionStillValid = true;
        var cleanupCount = 0;

        var activated = publication.TryPublish(
            register: () =>
            {
                companionStillValid = false;
                publication.Fail(() => cleanupCount++);
                return false;
            },
            isStillValid: () => companionStillValid,
            rollback: () => cleanupCount++);

        Assert.False(activated);
        Assert.False(publication.IsPublished);
        Assert.Equal(1, cleanupCount);

        publication.Fail(() => cleanupCount++);
        Assert.Equal(1, cleanupCount);
    }

    [Fact]
    public void InvalidatedAfterNativeRegistration_CannotPublishAvailability()
    {
        var publication = new BackdropActivationPublicationGate();
        var cleanupCount = 0;

        var activated = publication.TryPublish(
            register: () => true,
            isStillValid: () => false,
            rollback: () => cleanupCount++);

        Assert.False(activated);
        Assert.False(publication.IsPublished);
        Assert.Equal(1, cleanupCount);
    }

    [Fact]
    public void SuccessfulInitialGeometryCommit_PublishesOnlyAfterValidityCheck()
    {
        var publication = new BackdropActivationPublicationGate();
        var validityChecked = false;

        var activated = publication.TryPublish(
            register: () => true,
            isStillValid: () => validityChecked = true,
            rollback: () => throw new InvalidOperationException("Rollback must not run."));

        Assert.True(activated);
        Assert.True(validityChecked);
        Assert.True(publication.IsPublished);
    }

    private static PointerProbeEvidence VerifiedEvidence() => new(
        DownReceived: true,
        UpReceived: true,
        ExpectedProcessId: 42,
        ActualProcessId: 42,
        ExpectedWindow: 101,
        ActualWindow: 101,
        ForegroundBefore: 11,
        ForegroundAfter: 11,
        FocusBefore: 22,
        FocusAfter: 22);
}
