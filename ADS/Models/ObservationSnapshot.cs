namespace ADS.Models;

public sealed class ObservationSnapshot
{
    public static ObservationSnapshot Empty { get; } = new()
    {
        LiveMonsters = [],
        MonsterGhosts = [],
        LiveInteractables = [],
        InteractableGhosts = [],
    };

    public required IReadOnlyList<ObservedMonster> LiveMonsters { get; init; }
    public required IReadOnlyList<ObservedMonster> MonsterGhosts { get; init; }
    public required IReadOnlyList<ObservedInteractable> LiveInteractables { get; init; }
    public required IReadOnlyList<ObservedInteractable> InteractableGhosts { get; init; }
}
