using ADS.Models;

namespace ADS.Services;

internal enum ActivityOwnershipPolicy { Automatic, ExplicitOnly, Excluded }

internal static class AutomationTerritoryPolicy
{
    public const string InactiveStatus = "ADS is inactive in this activity.";

    private static readonly HashSet<uint> ExcludedTerritoryIds =
    [
        512, 514, 515, 579, 624, 625, 656, 732, 763, 795, 827, 940, 941,
        900, 901, 920, 929, 939, 975, 1163, 1252, 1346,
    ];

    public static bool IsAutomationExcludedTerritory(uint territoryTypeId)
        => ExcludedTerritoryIds.Contains(territoryTypeId)
           // DDuck DeepDungeonCatalog floor sets and dedicated rest areas.
           || territoryTypeId is >= 561 and <= 565 or >= 593 and <= 607 or 570
               or >= 770 and <= 775 or >= 782 and <= 785 or 780
               or >= 1099 and <= 1108 or 1124
               or >= 1281 and <= 1290 or 1280;

    public static bool IsOceanFishingTerritory(uint territoryTypeId)
        => territoryTypeId is 900 or 1163;

    public static ActivityOwnershipPolicy GetPolicy(uint territoryTypeId, uint contentTypeId)
        => IsAutomationExcludedTerritory(territoryTypeId) ? ActivityOwnershipPolicy.Excluded
            // Native content types: PvP, Gold Saucer, Hall of the Novice, seasonal events.
            : contentTypeId is 6 or 19 or 20 or 22 ? ActivityOwnershipPolicy.ExplicitOnly
            : ActivityOwnershipPolicy.Automatic;

    public static ActivityOwnershipPolicy GetPolicy(DutyContextSnapshot context)
        => GetPolicy(context.TerritoryTypeId, context.ContentTypeRowId != 0
            ? context.ContentTypeRowId : context.CurrentDuty?.ContentTypeRowId ?? 0);

    public static string GetReason(ActivityOwnershipPolicy policy)
        => policy switch
        {
            ActivityOwnershipPolicy.Excluded => InactiveStatus,
            ActivityOwnershipPolicy.ExplicitOnly => "This activity requires Start Inside or Resume in the current instance; automatic carry-over is blocked.",
            _ => "Ordinary duty ownership rules apply.",
        };

    public static bool IsNpcSaleExcludedTerritory(uint liveTerritory, uint observedTerritory, bool localOnly)
        => liveTerritory == 0
           || (IsAutomationExcludedTerritory(liveTerritory)
               && (!IsOceanFishingTerritory(liveTerritory) || !localOnly))
           || (IsAutomationExcludedTerritory(observedTerritory)
               && !IsOceanFishingTerritory(observedTerritory));
}
