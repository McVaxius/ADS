using ADS.Services;

namespace ADS.Tests;

public sealed class InteractionRangeTests
{
    [Fact]
    public void DirectInteractionAndRetryIdentityRangesAreTwoYalms()
    {
        Assert.Equal(2.0f, ExecutionService.DirectInteractAttemptRange);
        Assert.Equal(2.0f, ExecutionService.InteractableIdentityMatchRange);
    }
}
