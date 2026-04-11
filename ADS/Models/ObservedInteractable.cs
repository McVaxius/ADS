using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;

namespace ADS.Models;

public sealed class ObservedInteractable
{
    public required string Key { get; init; }
    public required ulong GameObjectId { get; init; }
    public required uint DataId { get; init; }
    public required uint MapId { get; init; }
    public required ObjectKind ObjectKind { get; init; }
    public required string Name { get; init; }
    public required Vector3 Position { get; init; }
    public required DateTime LastSeenUtc { get; set; }
    public required InteractableClass Classification { get; init; }
    public required GhostReason GhostReason { get; set; }
}
