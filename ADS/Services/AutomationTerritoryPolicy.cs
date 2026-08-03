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
        => ExcludedTerritoryIds.Contains(territoryTypeId);
}
