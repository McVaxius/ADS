using ADS.Services;

namespace ADS.Tests;

public sealed class AutomationTerritoryPolicyTests
{
    [Theory]
    [InlineData(512u)]
    [InlineData(514u)]
    [InlineData(515u)]
    [InlineData(624u)]
    [InlineData(625u)]
    [InlineData(656u)]
    [InlineData(732u)]
    [InlineData(763u)]
    [InlineData(795u)]
    [InlineData(827u)]
    [InlineData(900u)]
    [InlineData(901u)]
    [InlineData(920u)]
    [InlineData(929u)]
    [InlineData(939u)]
    [InlineData(975u)]
    [InlineData(1163u)]
    [InlineData(1252u)]
    [InlineData(1346u)]
    public void ExcludesCrowdedActivityTerritories(uint territoryTypeId)
        => Assert.True(AutomationTerritoryPolicy.IsAutomationExcludedTerritory(territoryTypeId));

    [Theory]
    [InlineData(1044u)] // The Praetorium
    [InlineData(1099u)] // Eureka Orthos
    [InlineData(1100u)] // Eureka Orthos
    public void KeepsRegularDutiesAndEurekaOrthosEnabled(uint territoryTypeId)
        => Assert.False(AutomationTerritoryPolicy.IsAutomationExcludedTerritory(territoryTypeId));
}
