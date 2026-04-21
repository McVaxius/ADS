namespace ADS.Models;

public sealed class DutyContextSnapshot
{
    public required bool PluginEnabled { get; init; }
    public required bool IsLoggedIn { get; init; }
    public required bool BoundByDuty { get; init; }
    public required bool BoundByDuty56 { get; init; }
    public required bool BetweenAreas { get; init; }
    public required bool BetweenAreas51 { get; init; }
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

    public bool HasCatalogMetadata
        => CurrentDuty is not null;
}
