using ADS.Services;
using Xunit;

namespace ADS.Tests;

public sealed class KeyboundActivityExclusionTests
{
    [Theory]
    [InlineData(1359u)]
    [InlineData(1360u)]
    public void KeyboundArenaAndSquareExcludeAllDutyAndMenuAutomation(uint territory)
    {
        Assert.Equal(ActivityOwnershipPolicy.Excluded, AutomationTerritoryPolicy.GetPolicy(territory, 0));
        Assert.Equal(ActivityOwnershipPolicy.Excluded, AutomationTerritoryPolicy.GetPolicy(territory, 23));
        Assert.True(AutomationTerritoryPolicy.IsNpcSaleExcludedTerritory(territory, territory, true));
        Assert.True(AutomationTerritoryPolicy.IsNpcSaleExcludedTerritory(129, territory, false));
    }

    [Theory]
    [InlineData(1044u)]
    [InlineData(144u)]
    public void OrdinaryDutiesAndGoldSaucerRemainAvailable(uint territory)
        => Assert.False(AutomationTerritoryPolicy.IsAutomationExcludedTerritory(territory));
}
