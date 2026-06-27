using ADS.Services;

namespace ADS.Tests;

public sealed class HigherLowerPendingDirectionRetryPolicyTests
{
    private static readonly DateTime StartedUtc = new(2026, 6, 27, 12, 36, 42, DateTimeKind.Utc);

    [Fact]
    public void DoesNotRetryBeforeFirstGrace()
    {
        var decision = Evaluate(StartedUtc + HigherLowerPendingDirectionRetryPolicy.FirstRetryGrace - TimeSpan.FromMilliseconds(1));

        Assert.False(decision.ShouldRetry);
        Assert.Equal("retry-cooldown", decision.Reason);
    }

    [Fact]
    public void RetriesWhenFirstGraceHasElapsed()
    {
        var decision = Evaluate(StartedUtc + HigherLowerPendingDirectionRetryPolicy.FirstRetryGrace);

        Assert.True(decision.ShouldRetry);
        Assert.Equal("retry-due", decision.Reason);
    }

    [Fact]
    public void RetryIntervalPreventsPerFrameSpam()
    {
        var retryUtc = StartedUtc + HigherLowerPendingDirectionRetryPolicy.FirstRetryGrace;
        var nextRetryUtc = HigherLowerPendingDirectionRetryPolicy.GetNextRetryUtc(retryUtc);
        var decision = Evaluate(
            retryUtc + TimeSpan.FromSeconds(1),
            interactAttempts: 2,
            nextRetryUtc: nextRetryUtc);

        Assert.False(decision.ShouldRetry);
        Assert.Equal("retry-cooldown", decision.Reason);
        Assert.Equal(nextRetryUtc, decision.NextRetryUtc);
    }

    [Fact]
    public void MaxAttemptsStopFurtherRetries()
    {
        var decision = Evaluate(
            StartedUtc + TimeSpan.FromSeconds(20),
            interactAttempts: HigherLowerPendingDirectionRetryPolicy.MaxInteractAttempts);

        Assert.False(decision.ShouldRetry);
        Assert.Equal("max-attempts", decision.Reason);
    }

    [Fact]
    public void DoesNotRetryOutsideWaitingSelectYesno()
    {
        var decision = Evaluate(
            StartedUtc + HigherLowerPendingDirectionRetryPolicy.FirstRetryGrace,
            waitingSelectYesno: false);

        Assert.False(decision.ShouldRetry);
        Assert.Equal("phase", decision.Reason);
    }

    [Fact]
    public void DoesNotRetryWhenTargetIsUnavailable()
    {
        var decision = Evaluate(
            StartedUtc + HigherLowerPendingDirectionRetryPolicy.FirstRetryGrace,
            targetAvailable: false,
            targetDistance: null);

        Assert.False(decision.ShouldRetry);
        Assert.Equal("target-missing", decision.Reason);
    }

    [Fact]
    public void DoesNotRetryWhenTargetIsOutOfRange()
    {
        var decision = Evaluate(
            StartedUtc + HigherLowerPendingDirectionRetryPolicy.FirstRetryGrace,
            targetDistance: 2.01f);

        Assert.False(decision.ShouldRetry);
        Assert.Equal("target-out-of-range", decision.Reason);
    }

    private static HigherLowerPendingDirectionRetryPolicy.RetryDecision Evaluate(
        DateTime now,
        bool waitingSelectYesno = true,
        int interactAttempts = 1,
        DateTime? nextRetryUtc = null,
        bool targetAvailable = true,
        float? targetDistance = 1.0f)
        => HigherLowerPendingDirectionRetryPolicy.Evaluate(new HigherLowerPendingDirectionRetryPolicy.RetryState(
            WaitingSelectYesno: waitingSelectYesno,
            HigherLowerPromptVisible: false,
            TrustedServerProofAvailable: false,
            PendingTimedOut: false,
            PendingStartedUtc: StartedUtc,
            NextRetryUtc: nextRetryUtc ?? HigherLowerPendingDirectionRetryPolicy.GetInitialNextRetryUtc(StartedUtc),
            InteractAttempts: interactAttempts,
            TargetAvailable: targetAvailable,
            TargetDistance: targetDistance,
            InteractRange: 2.0f,
            NowUtc: now));
}
