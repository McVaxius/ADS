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
    public static TreasureDungeonRoleInference Default(string detail = "No external treasure-role source was active; ADS keeps map-opener behavior.")
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
