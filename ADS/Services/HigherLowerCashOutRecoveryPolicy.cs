namespace ADS.Services;

internal static class HigherLowerCashOutRecoveryPolicy
{
    public static RecoveryDecision Evaluate(RecoveryState state)
    {
        if (state.TerminalProofAvailable && state.PendingOpenChest)
            return RecoveryDecision.ClearTerminal("terminal-proof");

        if (state.PendingOpenChest)
        {
            if (state.TimedOut)
                return RecoveryDecision.Timeout("cashout-surface-timeout");

            if (state.PendingPhase == PendingPhase.WaitingSurface)
            {
                return state.TreasureHighLowVisible
                    ? RecoveryDecision.SendOpenChest("treasure-highlow-visible")
                    : RecoveryDecision.WaitForSurface("waiting-surface");
            }

            return RecoveryDecision.None(true, "pending-openchest");
        }

        if (!state.CashOutDecision)
            return RecoveryDecision.None(false, "not-cashout");

        if (state.TreasureHighLowVisible)
            return RecoveryDecision.SendOpenChest("treasure-highlow-visible");

        if (state.HighLowTargetable)
            return RecoveryDecision.StartWaitingSurface("cashout-targetable-highlow");

        return RecoveryDecision.WaitForSurface("cashout-waiting-surface");
    }

    public readonly record struct RecoveryState(
        bool CashOutDecision,
        bool PendingOpenChest,
        PendingPhase PendingPhase,
        bool TreasureHighLowVisible,
        bool HighLowTargetable,
        bool TerminalProofAvailable,
        bool TimedOut);

    public readonly record struct RecoveryDecision(
        RecoveryAction Action,
        string Reason,
        bool SuppressDirectionSelection,
        bool ShouldBlock)
    {
        public static RecoveryDecision None(bool suppressDirectionSelection, string reason)
            => new(RecoveryAction.None, reason, suppressDirectionSelection, ShouldBlock: false);

        public static RecoveryDecision StartWaitingSurface(string reason)
            => new(RecoveryAction.StartWaitingSurface, reason, SuppressDirectionSelection: true, ShouldBlock: false);

        public static RecoveryDecision WaitForSurface(string reason)
            => new(RecoveryAction.WaitForSurface, reason, SuppressDirectionSelection: true, ShouldBlock: false);

        public static RecoveryDecision SendOpenChest(string reason)
            => new(RecoveryAction.SendOpenChest, reason, SuppressDirectionSelection: true, ShouldBlock: false);

        public static RecoveryDecision ClearTerminal(string reason)
            => new(RecoveryAction.ClearTerminal, reason, SuppressDirectionSelection: true, ShouldBlock: false);

        public static RecoveryDecision Timeout(string reason)
            => new(RecoveryAction.Timeout, reason, SuppressDirectionSelection: true, ShouldBlock: true);
    }

    public enum RecoveryAction
    {
        None = 0,
        StartWaitingSurface = 1,
        WaitForSurface = 2,
        SendOpenChest = 3,
        ClearTerminal = 4,
        Timeout = 5,
    }

    public enum PendingPhase
    {
        None = 0,
        WaitingSurface = 1,
        WaitingSelectYesno = 2,
        WaitingProof = 3,
        TimedOut = 4,
    }
}
