using ADS.Services;

namespace ADS.Tests;

public sealed class HigherLowerCashOutRecoveryPolicyTests
{
    [Fact]
    public void CashOutTargetableWithoutVisibleAddonStartsWaitingSurface()
    {
        var decision = Evaluate(
            cashOutDecision: true,
            highLowTargetable: true,
            treasureHighLowVisible: false);

        Assert.Equal(HigherLowerCashOutRecoveryPolicy.RecoveryAction.StartWaitingSurface, decision.Action);
        Assert.True(decision.SuppressDirectionSelection);
        Assert.False(decision.ShouldBlock);
        Assert.Equal("cashout-targetable-highlow", decision.Reason);
    }

    [Fact]
    public void WaitingSurfaceWithVisibleAddonSendsOpenChest()
    {
        var decision = Evaluate(
            pendingOpenChest: true,
            pendingPhase: HigherLowerCashOutRecoveryPolicy.PendingPhase.WaitingSurface,
            treasureHighLowVisible: true);

        Assert.Equal(HigherLowerCashOutRecoveryPolicy.RecoveryAction.SendOpenChest, decision.Action);
        Assert.True(decision.SuppressDirectionSelection);
        Assert.False(decision.ShouldBlock);
    }

    [Fact]
    public void WaitingSurfaceSuppressesHighLowDirectionSelection()
    {
        var decision = Evaluate(
            pendingOpenChest: true,
            pendingPhase: HigherLowerCashOutRecoveryPolicy.PendingPhase.WaitingSurface,
            highLowTargetable: true,
            treasureHighLowVisible: false);

        Assert.Equal(HigherLowerCashOutRecoveryPolicy.RecoveryAction.WaitForSurface, decision.Action);
        Assert.True(decision.SuppressDirectionSelection);
        Assert.False(decision.ShouldBlock);
    }

    [Fact]
    public void TerminalProofClearsPendingOpenChest()
    {
        var decision = Evaluate(
            pendingOpenChest: true,
            pendingPhase: HigherLowerCashOutRecoveryPolicy.PendingPhase.WaitingSurface,
            terminalProofAvailable: true,
            timedOut: true);

        Assert.Equal(HigherLowerCashOutRecoveryPolicy.RecoveryAction.ClearTerminal, decision.Action);
        Assert.True(decision.SuppressDirectionSelection);
        Assert.False(decision.ShouldBlock);
        Assert.Equal("terminal-proof", decision.Reason);
    }

    [Fact]
    public void WaitingSurfaceTimeoutAllowsOneHiddenOpenChestAttemptThenBlocks()
    {
        var firstDecision = Evaluate(
            pendingOpenChest: true,
            pendingPhase: HigherLowerCashOutRecoveryPolicy.PendingPhase.WaitingSurface,
            timedOut: true);

        Assert.Equal(HigherLowerCashOutRecoveryPolicy.RecoveryAction.SendHiddenOpenChest, firstDecision.Action);
        Assert.True(firstDecision.SuppressDirectionSelection);
        Assert.False(firstDecision.ShouldBlock);
        Assert.Equal("cashout-surface-timeout", firstDecision.Reason);

        var subsequentDecision = Evaluate(
            pendingOpenChest: true,
            pendingPhase: HigherLowerCashOutRecoveryPolicy.PendingPhase.TimedOut,
            timedOut: true);

        Assert.Equal(HigherLowerCashOutRecoveryPolicy.RecoveryAction.Timeout, subsequentDecision.Action);
        Assert.True(subsequentDecision.SuppressDirectionSelection);
        Assert.True(subsequentDecision.ShouldBlock);
    }

    private static HigherLowerCashOutRecoveryPolicy.RecoveryDecision Evaluate(
        bool cashOutDecision = false,
        bool pendingOpenChest = false,
        HigherLowerCashOutRecoveryPolicy.PendingPhase pendingPhase = HigherLowerCashOutRecoveryPolicy.PendingPhase.None,
        bool treasureHighLowVisible = false,
        bool highLowTargetable = false,
        bool terminalProofAvailable = false,
        bool timedOut = false)
        => HigherLowerCashOutRecoveryPolicy.Evaluate(new HigherLowerCashOutRecoveryPolicy.RecoveryState(
            CashOutDecision: cashOutDecision,
            PendingOpenChest: pendingOpenChest,
            PendingPhase: pendingPhase,
            TreasureHighLowVisible: treasureHighLowVisible,
            HighLowTargetable: highLowTargetable,
            TerminalProofAvailable: terminalProofAvailable,
            TimedOut: timedOut));
}
