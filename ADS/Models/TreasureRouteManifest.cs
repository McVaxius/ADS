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
    public TreasureRoutePointDefinition? EntryPoint { get; set; }
    public List<TreasureRoutePointDefinition> Doors { get; set; } = [];
}

public sealed class TreasureRoutePointDefinition
{
    public string Label { get; set; } = string.Empty;
    public int Room { get; set; }
    public TreasureRoutePointSlot Slot { get; set; }
    public float? X { get; set; }
    public float? Y { get; set; }
    public float? Z { get; set; }
}

public enum TreasureRoutePointSlot
{
    Unknown = 0,
    Start,
    Left,
    Centre,
    Right,
    Single,
}
