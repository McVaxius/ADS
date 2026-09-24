namespace ADS.Services;

internal static class AutomationTerritoryPolicy
{
    public const string InactiveStatus = "ADS is inactive in this activity.";

    private static readonly HashSet<uint> ExcludedTerritoryIds =
    [
        512, 514, 515, 624, 625, 656, 732, 763, 795, 827,
        900, 901, 920, 929, 939, 975, 1163, 1252, 1346,
    ];

    public static bool IsAutomationExcludedTerritory(uint territoryTypeId)
        => ExcludedTerritoryIds.Contains(territoryTypeId)
           // DDuck DeepDungeonCatalog floor sets and dedicated rest areas.
           || territoryTypeId is >= 561 and <= 565 or >= 593 and <= 607 or 570
               or >= 770 and <= 775 or >= 782 and <= 785 or 780
               or >= 1099 and <= 1108 or 1124
               or >= 1281 and <= 1290 or 1280;
}
