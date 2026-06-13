namespace ADS.Models;

public enum TreasureDungeonRole
{
    MapOpener = 0,
    Follower = 1,
    Regular = 2,
}

public sealed record TreasureDungeonRoleInference(
    TreasureDungeonRole Role,
    string Source,
    string Detail,
    string CharacterKey,
    bool FrenRiderLoaded,
    bool FrenRiderEnabled,
    bool LootGoblinLoaded,
    bool LootGoblinEnabled,
    bool LootGoblinAdsSolverEnabled)
{
    public const string MapSeekerDisplayName = "Map Seeker";

    public string DisplayName
        => Role switch
        {
            TreasureDungeonRole.Follower => "Follower",
            TreasureDungeonRole.Regular => "Regular",
            TreasureDungeonRole.MapOpener when LootGoblinLoaded && LootGoblinEnabled && LootGoblinAdsSolverEnabled => "MapOpener",
            _ => MapSeekerDisplayName,
        };

    public bool AllowsOutsideBmraiFollow
        => Role == TreasureDungeonRole.Follower
           || string.Equals(DisplayName, MapSeekerDisplayName, StringComparison.Ordinal);

    public static bool IsStableRegularDuty(DutyContextSnapshot context, bool supportedTreasureTerritory)
        => context.IsLoggedIn
           && context.InInstancedDuty
           && !context.IsUnsafeTransition
           && context.CurrentDuty is { Category: not DutyCategory.TreasureDungeon }
           && !supportedTreasureTerritory;

    public static TreasureDungeonRoleInference SegmentForDuty(
        TreasureDungeonRoleInference inferred,
        DutyContextSnapshot context,
        bool supportedTreasureTerritory)
    {
        if (!IsStableRegularDuty(context, supportedTreasureTerritory))
            return inferred;

        var duty = context.CurrentDuty!;
        return inferred with
        {
            Role = TreasureDungeonRole.Regular,
            Source = "DutyCatalog",
            Detail = $"Stable instanced duty '{duty.EnglishName}' is categorized as {duty.Category}; treasure follow is disabled. Outside/treasure inference was {inferred.DisplayName} from {inferred.Source}.",
        };
    }

    public static TreasureDungeonRoleInference Default(string detail = "No external treasure-role source was active; ADS shows Map Seeker outside treasure duties.")
        => new(
            TreasureDungeonRole.MapOpener,
            "Default",
            detail,
            string.Empty,
            FrenRiderLoaded: false,
            FrenRiderEnabled: false,
            LootGoblinLoaded: false,
            LootGoblinEnabled: false,
            LootGoblinAdsSolverEnabled: false);
}
