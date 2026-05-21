namespace ADS.Models;

public sealed class TreasureRouteManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string Description { get; set; } = string.Empty;
    public List<TreasureRouteDefinition> Routes { get; set; } = [];
}

public sealed class TreasureRouteDefinition
{
    public bool Enabled { get; set; } = true;
    public uint TerritoryTypeId { get; set; }
    public string DutyName { get; set; } = string.Empty;
    public TreasureRouteKind RouteKind { get; set; }
    public TreasureRouteCoordinate? EntryPoint { get; set; }
    public List<TreasureRouteRoomDefinition> Rooms { get; set; } = [];
}

public sealed class TreasureRouteRoomDefinition
{
    public int Room { get; set; }
    public TreasureRouteCoordinate? Left { get; set; }
    public TreasureRouteCoordinate? Middle { get; set; }
    public TreasureRouteCoordinate? Right { get; set; }
}

public sealed class TreasureRouteCoordinate
{
    public float? X { get; set; }
    public float? Y { get; set; }
    public float? Z { get; set; }
}

public enum TreasureRouteKind
{
    Unknown = 0,
    Treasure,
    Thief,
}
