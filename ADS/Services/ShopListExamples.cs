namespace ADS.Services;

internal sealed record ShopListExample(
    string Name,
    string Description,
    IReadOnlyList<(uint ItemId, int Quantity)> Items);

internal static class ShopListExamples
{
    // Step quantities: https://ffxiv.consolegameswiki.com/wiki/Zodiac_Weapons
    // and https://ffxiv.consolegameswiki.com/wiki/Anima_Weapons.
    internal static IReadOnlyList<ShopListExample> RelicSteps { get; } =
    [
        new("ARR - A Relic Reborn - Quenching Oil",
            "1 Radz-at-Han Quenching Oil for the base relic hand-in.", [(6267, 1)]),
        new("ARR - Zenith - Thavnairian Mist",
            "3 Thavnairian Mist for the Zenith upgrade.", [(6268, 3)]),
        new("ARR - Novus - Sphere Scroll Ink",
            "3 Superior Enchanted Ink for a sphere scroll. Mysterious Map and Alexandrite farming belong to Loot Goblin.", [(7885, 3)]),
        new("ARR - Zodiac Braves - Sacred Spring Water",
            "4 Sacred Spring Water: one for each Zodiac Braves quest. Other quest materials are acquired separately.", [(9540, 4)]),
        new("HW - Coming into Its Own - Unidentifiable Items",
            "10 each of Unidentifiable Bone, Shell, Ore, and Seeds. The full set costs 6,000 Poetics; earn more and rerun for remaining targets. Crafted turn-ins are acquired separately.",
            [(13582, 10), (13584, 10), (13586, 10), (13588, 10)]),
        new("HW - Finding Your Voice - Aether Oil",
            "5 Aether Oil for the Hyperconductive Anima upgrade.", [(14899, 5)]),
        new("HW - A Dream Fulfilled - Umbrite Batch (20)",
            "An adjustable batch of 20 Umbrite. The step usually needs 57-60 total; set the target to what you still need. Crystal Sand is acquired separately.", [(15840, 20)]),
        new("HW - Future Proof - Singing Clusters",
            "50 Singing Clusters for the Sharpened Anima upgrade. Clusters already in inventory reduce purchases.", [(16064, 50)]),
        new("HW - Some Assembly Required - Pneumite",
            "15 Pneumite for the Newborn Soulstone.", [(16933, 15)]),
        new("HW - Best Friends Forever - Archaic Ink",
            "1 Archaic Enchanted Ink for the final Lux Anima hand-in.", [(16934, 1)]),
    ];
}
