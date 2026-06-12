namespace ADS.Models;

public sealed class DutyContextSnapshot
{
    public required bool PluginEnabled { get; init; }
    public required bool IsLoggedIn { get; init; }
    public required bool BoundByDuty { get; init; }
    public required bool BoundByDuty56 { get; init; }
    public required bool BetweenAreas { get; init; }
    public required bool BetweenAreas51 { get; init; }
    public required bool Jumping { get; init; }
    public required bool Jumping61 { get; init; }
    public required bool Occupied33 { get; init; }
    public required bool OccupiedInQuestEvent { get; init; }
    public required bool OccupiedInEvent { get; init; }
    public required bool OccupiedInCutSceneEvent { get; init; }
    public required bool WatchingCutscene { get; init; }
    public required bool InCombat { get; init; }
    public required bool Mounted { get; init; }
    public required uint TerritoryTypeId { get; init; }
    public required uint MapId { get; init; }
    public required uint ContentFinderConditionId { get; init; }
    public required DutyCatalogEntry? CurrentDuty { get; init; }

    public bool InInstancedDuty
        => BoundByDuty || BoundByDuty56;

    public bool IsUnsafeTransition
        => BetweenAreas || BetweenAreas51;

    public bool IsInteractionOccupied
        => OccupiedInQuestEvent || OccupiedInEvent;

    public bool IsTreasureRouteTransitHold
        => BetweenAreas
           || BetweenAreas51
           || Jumping
           || Jumping61
           || Occupied33
           || OccupiedInCutSceneEvent
           || WatchingCutscene;

    public bool HasCatalogMetadata
        => CurrentDuty is not null;
}
