using System.Numerics;

namespace ADS.Models;

public sealed class ObservedMonster
{
    public required string Key { get; init; }
    public required ulong GameObjectId { get; init; }
    public required uint DataId { get; init; }
    public required string Name { get; init; }
    public required Vector3 Position { get; init; }
    public required DateTime LastSeenUtc { get; set; }
}
