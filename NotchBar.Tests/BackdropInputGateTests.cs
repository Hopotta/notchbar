using NotchBar.Services;
using Xunit;

namespace NotchBar.Tests;

public sealed class BackdropInputGateTests
{
    [Fact]
    public void CandidateStyles_ArePopupNoRedirectionToolNoActivateTransparent()
    {
        Assert.Equal(0x80000000u, BackdropWindowPolicy.WindowStyle);
        Assert.Equal(0x082000A0u, BackdropWindowPolicy.ExtendedWindowStyle);
        Assert.Equal(0x082800A0u, BackdropWindowPolicy.ActiveExtendedWindowStyle);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void PointerGate_RejectsIncompleteOrChangedFocus(
        bool down,
        bool up,
        bool sameFocus)
    {
        var evidence = Evidence(down, up) with
        {
            FocusAfter = sameFocus ? 22 : 23
        };
        Assert.False(evidence.IsVerifiedPass);
    }

    [Fact]
    public void PointerGate_RejectsWrongProcessWindowTimeoutAndStaleGeneration()
    {
        var gate = ReadyGate(out var generation);
        Assert.False(gate.CompleteProbe(generation, Evidence(true, true) with { ActualProcessId = 99 }));
        Assert.Equal(BackdropLifecycleState.Fallback, gate.State);
        Assert.False(gate.CompleteProbe(generation, Evidence(true, true)));

        gate = ReadyGate(out generation);
        Assert.False(gate.CompleteProbe(generation, Evidence(true, true) with { ActualWindow = 999 }));

        gate = ReadyGate(out generation);
        Assert.False(gate.CompleteProbe(generation, Evidence(true, true) with { TimedOut = true }));
    }

    [Fact]
    public void OnlySameGenerationVerifiedEvidenceActivates()
    {
        var gate = ReadyGate(out var generation);
        Assert.True(gate.CompleteProbe(generation, Evidence(true, true)));
        Assert.True(gate.CanShow);
        Assert.Equal(PointerPassThroughGateState.Passed, gate.PointerState);
    }

    [Fact]
    public void DestroyIsIdempotentAndMakesLaterSuccessInert()
    {
        var gate = ReadyGate(out var generation);
        gate.Destroy();
        gate.Destroy();
        Assert.Equal(BackdropLifecycleState.Destroyed, gate.State);
        Assert.False(gate.CompleteProbe(generation, Evidence(true, true)));
        Assert.False(gate.CanShow);
    }

    private static BackdropActivationGate ReadyGate(out long generation)
    {
        var gate = new BackdropActivationGate();
        generation = gate.BeginInitialization();
        Assert.True(gate.MarkClipCommitted(generation));
        Assert.True(gate.BeginProbe(generation, frostObserved: true));
        return gate;
    }

    private static PointerProbeEvidence Evidence(bool down, bool up) => new(
        down,
        up,
        ExpectedProcessId: 42,
        ActualProcessId: 42,
        ExpectedWindow: 101,
        ActualWindow: 101,
        ForegroundBefore: 11,
        ForegroundAfter: 11,
        FocusBefore: 22,
        FocusAfter: 22);
}
