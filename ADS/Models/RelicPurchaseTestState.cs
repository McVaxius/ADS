namespace ADS.Models;

public sealed class RelicPurchaseTestState
{
    public string? SelectedTask { get; set; }
    public ulong CharacterId { get; set; }
    public HashSet<uint> CompletedItems { get; set; } = [];
    public RelicPurchaseInterruption? InterruptedPurchase { get; set; }
}

public sealed record RelicPurchaseInterruption(uint ItemId, long ItemCountBefore, long PoeticsBefore, long Cost);
