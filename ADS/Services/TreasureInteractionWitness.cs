namespace ADS.Services;

public sealed record TreasureInteractionWitness(
    ulong Sequence,
    string EventKind,
    string Source,
    string ActorName,
    ulong? ActorContentId,
    ulong? ActorGameObjectId,
    ulong? ActorEntityId,
    bool AllowActorNameResolution,
    string TargetName,
    ulong? TargetGameObjectId,
    ulong? TargetEntityId,
    uint TargetBaseId,
    string TargetKind,
    DateTime CapturedUtc);
