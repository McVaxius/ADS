namespace ADS.Services;

internal static class HigherLowerPendingDirectionRetryPolicy
{
    public static readonly TimeSpan FirstRetryGrace = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(3);
    public const int MaxInteractAttempts = 4;

    public static DateTime GetInitialNextRetryUtc(DateTime interactUtc)
        => interactUtc + FirstRetryGrace;

    public static DateTime GetNextRetryUtc(DateTime retryUtc)
        => retryUtc + RetryInterval;

    public static RetryDecision Evaluate(RetryState state)
    {
        var nextRetryUtc = state.NextRetryUtc == DateTime.MinValue
            ? GetInitialNextRetryUtc(state.PendingStartedUtc)
            : state.NextRetryUtc;

        if (!state.WaitingSelectYesno)
            return RetryDecision.Blocked("phase", nextRetryUtc);

        if (state.HigherLowerPromptVisible)
            return RetryDecision.Blocked("higherlower-prompt-visible", nextRetryUtc);

        if (state.TrustedServerProofAvailable)
            return RetryDecision.Blocked("trusted-server-proof", nextRetryUtc);

        if (state.PendingTimedOut)
            return RetryDecision.Blocked("pending-timeout", nextRetryUtc);

        if (state.InteractAttempts >= MaxInteractAttempts)
            return RetryDecision.Blocked("max-attempts", nextRetryUtc);

        if (state.NowUtc < nextRetryUtc)
            return RetryDecision.Blocked("retry-cooldown", nextRetryUtc);

        if (!state.TargetAvailable)
            return RetryDecision.Blocked("target-missing", nextRetryUtc);

        if (!state.TargetDistance.HasValue || !float.IsFinite(state.TargetDistance.Value))
            return RetryDecision.Blocked("target-distance-unavailable", nextRetryUtc);

        if (state.TargetDistance.Value > state.InteractRange)
            return RetryDecision.Blocked("target-out-of-range", nextRetryUtc);

        return new RetryDecision(true, "retry-due", nextRetryUtc);
    }

    public readonly record struct RetryState(
        bool WaitingSelectYesno,
        bool HigherLowerPromptVisible,
        bool TrustedServerProofAvailable,
        bool PendingTimedOut,
        DateTime PendingStartedUtc,
        DateTime NextRetryUtc,
        int InteractAttempts,
        bool TargetAvailable,
        float? TargetDistance,
        float InteractRange,
        DateTime NowUtc);

    public readonly record struct RetryDecision(bool ShouldRetry, string Reason, DateTime NextRetryUtc)
    {
        public static RetryDecision Blocked(string reason, DateTime nextRetryUtc)
            => new(false, reason, nextRetryUtc);
    }
}
