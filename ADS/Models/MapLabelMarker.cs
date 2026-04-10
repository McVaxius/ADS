using System.Numerics;

namespace ADS.Models;

public sealed class MapLabelMarker
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required uint MapId { get; init; }
    public required uint MarkerRangeId { get; init; }
    public required string MapName { get; init; }
    public required ushort SubrowId { get; init; }
    public required byte DataType { get; init; }
    public required ushort Icon { get; init; }
    public required short TextureX { get; init; }
    public required short TextureY { get; init; }
    public required Vector3 WorldPosition { get; init; }
    public required Vector2 MapCoordinates { get; init; }
}
