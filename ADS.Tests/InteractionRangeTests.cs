using ADS.Services;

namespace ADS.Tests;

public sealed class InteractionRangeTests
{
    [Fact]
    public void InitialAttemptUsesTwoYalmsAndAllowsCloseXzFallback()
    {
        var policy = ExecutionService.GetInteractionAttemptPolicy(closeRecoveryArmed: false);

        Assert.Equal(2.0f, policy.AttemptRange);
        Assert.True(policy.AllowCloseXzFallback);
    }

    [Fact]
    public void UnconfirmedRetryUsesPointEightYalmsAndDisablesCloseXzFallback()
    {
        var policy = ExecutionService.GetInteractionAttemptPolicy(closeRecoveryArmed: true);

        Assert.Equal(0.8f, policy.AttemptRange);
        Assert.False(policy.AllowCloseXzFallback);
    }

    [Fact]
    public void RetryIdentityRangeRemainsTwoYalms()
    {
        Assert.Equal(2.0f, ExecutionService.InteractableIdentityMatchRange);
    }
}
