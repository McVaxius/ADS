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
    [InlineData(153u)] // South Shroud: entrance/vendor
    [InlineData(613u)] // Ruby Sea: entrance/vendor
    [InlineData(156u)] // Mor Dhona: entrance/vendor
    [InlineData(816u)] // Il Mheg: entrance/vendor
    [InlineData(1069u)] // Sil'dihn Subterrane
    [InlineData(1075u)] // Another Sil'dihn Subterrane
    [InlineData(1076u)] // Savage
    public void KeepsRegularDutiesEntrancesAndVariantCriterionEnabled(uint territoryTypeId)
        => Assert.False(AutomationTerritoryPolicy.IsAutomationExcludedTerritory(territoryTypeId));

    [Theory]
    [InlineData(900u, 900u, true, false)]
    [InlineData(1163u, 1163u, true, false)]
    [InlineData(900u, 1163u, true, false)]
    [InlineData(900u, 900u, false, true)]
    [InlineData(1163u, 1163u, false, true)]
    [InlineData(129u, 900u, false, false)]
    [InlineData(129u, 1163u, false, false)]
    [InlineData(129u, 1163u, true, false)]
    [InlineData(129u, 129u, false, false)]
    [InlineData(0u, 900u, true, true)]
    [InlineData(512u, 900u, true, true)]
    [InlineData(900u, 561u, true, true)]
    [InlineData(129u, 561u, false, true)]
    public void NpcSaleExceptionIsLocalOnlyAboardAndIgnoresOnlyStaleOceanContext(
        uint liveTerritory, uint observedTerritory, bool localOnly, bool excluded)
        => Assert.Equal(excluded, AutomationTerritoryPolicy.IsNpcSaleExcludedTerritory(liveTerritory, observedTerritory, localOnly));

    public static IEnumerable<object[]> DeepDungeonTerritories()
        => Enumerable.Range(561, 5).Concat(Enumerable.Range(593, 15)).Append(570)
            .Concat(Enumerable.Range(770, 6)).Concat(Enumerable.Range(782, 4)).Append(780)
            .Concat(Enumerable.Range(1099, 10)).Append(1124)
            .Concat(Enumerable.Range(1281, 10)).Append(1280)
            .Select(id => new object[] { (uint)id });

    [Theory]
    [MemberData(nameof(DeepDungeonTerritories))]
    public void ExcludesEveryDeepDungeonFloorAndRestArea(uint territoryTypeId)
    {
        Assert.True(AutomationTerritoryPolicy.IsAutomationExcludedTerritory(territoryTypeId));
        Assert.True(AutomationTerritoryPolicy.IsNpcSaleExcludedTerritory(territoryTypeId, 900, true));
        Assert.True(AutomationTerritoryPolicy.IsNpcSaleExcludedTerritory(129, territoryTypeId, false));
    }
}
