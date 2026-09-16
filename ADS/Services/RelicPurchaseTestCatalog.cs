namespace ADS.Services;

internal sealed record RelicPurchaseTestItem(uint ItemId, string Name);

internal static class RelicPurchaseTestCatalog
{
    internal static IReadOnlyList<RelicPurchaseTestItem> Items { get; } =
    [
        new(6267, "Radz-at-Han Quenching Oil"),
        new(6268, "Thavnairian Mist"),
        new(7885, "Superior Enchanted Ink"),
        new(9540, "Sacred Spring Water"),
        new(13582, "Unidentifiable Bone"),
        new(13584, "Unidentifiable Shell"),
        new(13586, "Unidentifiable Ore"),
        new(13588, "Unidentifiable Seeds"),
        new(14899, "Aether Oil"),
        new(15840, "Umbrite"),
        new(16064, "Singing Cluster"),
        new(16933, "Pneumite"),
        new(16934, "Archaic Enchanted Ink"),
    ];
}
