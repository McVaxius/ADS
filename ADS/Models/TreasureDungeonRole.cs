namespace ADS.Models;

public enum TreasureDungeonRole
{
    MapOpener = 0,
    Follower = 1,
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
            TreasureDungeonRole.MapOpener when LootGoblinLoaded && LootGoblinEnabled && LootGoblinAdsSolverEnabled => "MapOpener",
            _ => MapSeekerDisplayName,
        };

    public bool AllowsOutsideBmraiFollow
        => Role == TreasureDungeonRole.Follower
           || string.Equals(DisplayName, MapSeekerDisplayName, StringComparison.Ordinal);

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
