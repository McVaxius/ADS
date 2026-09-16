using ADS.Services;
using System.Numerics;

namespace ADS.Tests;

public sealed class InteractionRangeTests
{
    [Theory]
    [InlineData(3f, 4f, 0.5f, 3f, 1.5f, true, true)]
    [InlineData(3f, 4f, 0.5f, 2.75f, 1.75f, true, false)]
    [InlineData(3f, 4f, 0.5f, 2.5f, 2f, true, false)]
    [InlineData(3f, 4f, 0.5f, 2.49f, 2.01f, false, false)]
    [InlineData(0f, 1.5f, 0f, 0f, 1.5f, true, true)]
    [InlineData(0f, 2f, 0f, 0f, 2f, true, false)]
    [InlineData(0f, 4f, 0f, 0f, 4f, false, false)]
    [InlineData(2.4f, 0f, 0.5f, 0f, 1.9f, true, false)]
    [InlineData(0f, 4f, 0f, 3f, 1f, true, true)]
    [InlineData(0f, 1f, 0.5f, 2.5f, 0f, true, true)]
    public void InteractionReachAccountsForHitboxesAndKeepsRetriesStricter(
        float horizontalDistance,
        float verticalDistance,
        float playerHitboxRadius,
        float targetHitboxRadius,
        float expectedReach,
        bool expectedInitialAttempt,
        bool expectedRetryAttempt)
    {
        var centerDistance = Vector3.Distance(Vector3.Zero, new Vector3(horizontalDistance, verticalDistance, 0f));
        var reach = ExecutionService.GetInteractionReach(centerDistance, playerHitboxRadius, targetHitboxRadius);
        var initialPolicy = ExecutionService.GetInteractionAttemptPolicy(closeRecoveryArmed: false);
        var retryPolicy = ExecutionService.GetInteractionAttemptPolicy(closeRecoveryArmed: true);

        Assert.Equal(expectedReach, reach, precision: 5);
        Assert.Equal(expectedInitialAttempt, reach <= initialPolicy.AttemptRange);
        Assert.Equal(expectedRetryAttempt, reach <= retryPolicy.AttemptRange);
    }

    [Fact]
    public void InitialAttemptUsesTwoYalmsAndAllowsCloseXzFallback()
    {
        var policy = ExecutionService.GetInteractionAttemptPolicy(closeRecoveryArmed: false);

        Assert.Equal(2.0f, policy.AttemptRange);
        Assert.True(policy.AllowCloseXzFallback);
    }

    [Fact]
    public void UnconfirmedRetryAllowsVnavStoppingToleranceAndDisablesCloseXzFallback()
    {
        var policy = ExecutionService.GetInteractionAttemptPolicy(closeRecoveryArmed: true);

        Assert.Equal(1.5f, policy.AttemptRange);
        Assert.False(policy.AllowCloseXzFallback);
    }

    [Fact]
    public void RetryIdentityRangeRemainsTwoYalms()
    {
        Assert.Equal(2.0f, ExecutionService.InteractableIdentityMatchRange);
    }
}
