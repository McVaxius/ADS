using System.Numerics;

namespace ADS.Models;

public sealed class DungeonFrontierPoint
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required Vector3 Position { get; init; }
    public required uint LevelRowId { get; init; }
    public uint MapId { get; init; }
    public int Priority { get; init; } = 1000;
    public Vector2? MapCoordinates { get; init; }
    public bool UsePlayerYForNavigation { get; init; }
    public ManualDestinationKind ManualDestinationKind { get; init; }
    public bool IsManualDestination
        => ManualDestinationKind != ManualDestinationKind.None;
    public bool IsManualMapXzDestination
        => ManualDestinationKind == ManualDestinationKind.MapXz;
    public bool IsManualXyzDestination
        => ManualDestinationKind == ManualDestinationKind.Xyz;
    public bool AllowCombatBypass { get; init; }
    public float ArrivalRadiusXz { get; init; } = 8f;
    public float ArrivalRadius3d { get; init; }
}
