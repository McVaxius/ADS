namespace ADS.Models;

public sealed class PlannerSnapshot
{
    public required PlannerMode Mode { get; init; }
    public required PlannerObjectiveKind ObjectiveKind { get; init; }
    public required string Objective { get; init; }
    public required string Explanation { get; init; }
    public string? TargetName { get; init; }
    public float? TargetDistance { get; init; }
    public float? TargetVerticalDelta { get; init; }
    public required DateTime CapturedAtUtc { get; init; }
}
