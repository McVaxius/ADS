using System.Collections;
using System.Numerics;
using System.Resources;
using System.Text.Json;
using ADS.Localization;
using ADS.Models;
using ADS.Services;

namespace ADS.Tests;

public sealed class PoeticsGearCatalogTests
{
    public static IEnumerable<object[]> ExpansionCases()
        => Enumerable.Range(0, 5).Select(index => new object[] { index });

    [Theory]
    [MemberData(nameof(ExpansionCases))]
    public void EveryJobTemplateUsesCurrentGearOffersAndTheExactPoeticsMenu(int expansionIndex)
    {
        using var fixture = ReadFixture();
        var metadata = fixture.RootElement.GetProperty("items").EnumerateArray()
            .ToDictionary(item => item.GetProperty("id").GetUInt32());
        var snapshot = CreateSnapshot();
        var catalog = new ShopCatalogService(new FixedSheets(snapshot));
        var expansion = ShopListExamples.GearExpansions[expansionIndex];
        var expectedLevel = 50 + expansionIndex * 10;
        var expectedIl = new[] { 130, 270, 400, 530, 660 }[expansionIndex];
        Assert.Equal(21, expansion.Jobs.Count);
        for (var jobIndex = 0; jobIndex < expansion.Jobs.Count; jobIndex++)
        {
            var job = ShopListExamples.GearJobs[jobIndex];
            var example = expansion.Jobs[jobIndex];
            Assert.EndsWith(" - " + job, example.Name);
            Assert.InRange(example.Items.Count, 9, 11);
            Assert.Equal(example.Items.Count, example.Items.Select(item => item.ItemId).Distinct().Count());
            var slots = new HashSet<string>();
            foreach (var item in example.Items)
            {
                var nativeItem = metadata[item.ItemId];
                Assert.Equal(expectedLevel, nativeItem.GetProperty("level").GetInt32());
                Assert.Equal(expectedIl, nativeItem.GetProperty("il").GetInt32());
                Assert.Equal(1, item.Quantity);
                Assert.Contains(job, nativeItem.GetProperty("jobs").EnumerateArray().Select(value => value.GetString()));
                Assert.True(slots.Add(nativeItem.GetProperty("slot").GetString()!), "One piece per slot, including one unique ring.");
                var row = Assert.Single(snapshot.SpecialShopRows, row => row.ReceiveItems.Any(output => output.ItemId == item.ItemId));
                var offer = Assert.Single(catalog.Resolve(item.ItemId, 1).Offers);
                Assert.Equal(ShopOfferKind.SpecialShopTomestone, offer.Kind);
                Assert.Equal(row.ShopId, offer.ShopId);
                Assert.Equal(row.RowIndex, offer.SheetRowIndex);
                Assert.Equal(1u, offer.ReceiveCount);
                var cost = Assert.Single(offer.Currencies);
                Assert.Equal(ShopCurrencyKind.Tomestone, cost.Kind);
                Assert.Equal(28u, cost.ItemId);
                Assert.Equal(row.Costs.Single(cost => cost.Amount > 0).Amount, cost.AmountPerTransaction);
                Assert.NotEmpty(offer.AetheryteRoutes);
                var npc = Assert.Single(snapshot.NpcLinks, link => link.ShopId == row.ShopId);
                Assert.Equal(npc.NpcId, offer.NpcId);
                Assert.Equal(npc.MenuPath, offer.MenuPath);
                Assert.Equal(row.ShopId, offer.CallbackPath[^1].HandlerId);
            }
            Assert.All(new[] { "head", "body", "hands", "legs", "feet", "ears", "neck", "wrists", "ring" },
                slot => Assert.Contains(slot, slots));
            Assert.Equal(job == "PLD", slots.Contains("shield"));
        }
    }

    [Fact]
    public void GearExampleCaptionsResolveFromEveryCompiledLanguageCatalog()
    {
        var keys = new[] { "Job", "Add gear example", ShopListExamples.GearDescription }
            .Concat(ShopListExamples.GearExpansions.Select(expansion => expansion.Name)).ToArray();
        foreach (var language in Enum.GetValues<UiLanguage>())
        {
            var suffix = language == UiLanguage.English ? "" : "." + Ui.CultureFor(language).Name;
            using var stream = typeof(Plugin).Assembly.GetManifestResourceStream($"ADS.Localization.Strings{suffix}.resources");
            Assert.NotNull(stream);
            using var reader = new ResourceReader(stream);
            var values = reader.Cast<DictionaryEntry>().ToDictionary(entry => (string)entry.Key, entry => (string)entry.Value!);
            foreach (var key in keys)
            {
                Assert.True(values.TryGetValue(key, out var value), $"{language}: missing {key}");
                Assert.False(string.IsNullOrWhiteSpace(value));
                Assert.Equal(value, Ui.T(language, key));
            }
        }
    }

    [Fact]
    public void TemplatesCoverAll373CurrentPiecesAndKeepLegacyAccessoriesOnTheirIntendedRole()
    {
        using var fixture = ReadFixture();
        var expected = fixture.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("id").GetUInt32()).Order().ToArray();
        var actual = ShopListExamples.GearExpansions.SelectMany(expansion => expansion.Jobs)
            .SelectMany(example => example.Items).Select(item => item.ItemId).Distinct().Order().ToArray();
        Assert.Equal(373, expected.Length);
        Assert.Equal(expected, actual);
        foreach (var (index, aiming, fending, slaying) in new[] { (0, 8909u, 8883u, 8899u), (1, 16385u, 16383u, 16384u) })
        {
            var examples = ShopListExamples.GearExpansions[index].Jobs;
            var ninja = examples[Array.IndexOf(ShopListExamples.GearJobs, "NIN")].Items.Select(item => item.ItemId).ToArray();
            Assert.Contains(aiming, ninja);
            Assert.DoesNotContain(fending, ninja);
            Assert.DoesNotContain(slaying, ninja);
        }
        var endwalker = ShopListExamples.GearExpansions[4].Jobs;
        Assert.Contains(endwalker[0].Items, item => item.ItemId == 40090); // Individually named Polaris Point.
        Assert.Contains(endwalker[Array.IndexOf(ShopListExamples.GearJobs, "VPR")].Items, item => item.ItemId == 43632);
        Assert.Contains(endwalker[Array.IndexOf(ShopListExamples.GearJobs, "PCT")].Items, item => item.ItemId == 43653);
    }

    internal static ShopCatalogSnapshot CreateSnapshot()
    {
        using var fixture = ReadFixture();
        var root = fixture.RootElement;
        var items = root.GetProperty("items").EnumerateArray().ToDictionary(item => item.GetProperty("id").GetUInt32(),
            item => new ShopItemSheetRow(item.GetProperty("id").GetUInt32(), item.GetProperty("name").GetString()!, 1, 0, true));
        items[28] = new(28, "Allagan Tomestones of Poetics", 2000, 0, false);
        var shops = new List<SpecialShopSheetRow>();
        foreach (var shop in root.GetProperty("shops").EnumerateArray())
        foreach (var row in shop.GetProperty("rows").EnumerateArray())
        {
            var receives = row.GetProperty("items").EnumerateArray().Select(value => value.GetUInt32()).ToArray();
            var counts = row.GetProperty("counts").EnumerateArray().Select(value => value.GetUInt32()).ToArray();
            var hq = row.GetProperty("hq").EnumerateArray().Select(value => value.GetBoolean()).ToArray();
            var costs = row.GetProperty("costs").EnumerateArray().Select(value => value.GetUInt32()).ToArray();
            var prices = row.GetProperty("prices").EnumerateArray().Select(value => value.GetUInt32()).ToArray();
            var types = row.GetProperty("types").EnumerateArray().Select(value => value.GetByte()).ToArray();
            var collectability = row.GetProperty("collectability").EnumerateArray().Select(value => value.GetUInt16()).ToArray();
            shops.Add(new(shop.GetProperty("id").GetUInt32(), shop.GetProperty("name").GetString()!,
                row.GetProperty("row").GetInt32(), receives.Select((id, index) => (id, counts[index], hq[index])).ToArray(),
                costs.Select((id, index) => new SpecialShopCostSheetRow(id, prices[index], collectability[index], types[index])).ToArray(),
                shop.GetProperty("currency").GetByte(), [], false));
        }
        var npcs = root.GetProperty("npcs").EnumerateArray().Select(npc => new ShopNpcEventSheetRow(
            npc.GetProperty("id").GetUInt32(), npc.GetProperty("name").GetString()!,
            npc.GetProperty("events").EnumerateArray().Select(entry => new ShopNpcEventReference(
                entry.GetProperty("kind").GetString() == "SpecialShop" ? ShopNpcEventKind.SpecialShop : ShopNpcEventKind.Unsupported,
                entry.GetProperty("id").GetUInt32())).ToArray())).ToArray();
        var links = ShopNpcLinkBuilder.Build(new HashSet<uint>(), shops.Select(shop => shop.ShopId).ToHashSet(), npcs, [], []);
        // Territory, NPC and aetheryte identities are captured; coordinates model arrival only.
        var placements = root.GetProperty("npcs").EnumerateArray().Select(npc => new ShopNpcPlacementSheetRow(
            npc.GetProperty("id").GetUInt32(), npc.GetProperty("territory").GetUInt32(),
            npc.GetProperty("territoryName").GetString()!, Vector3.One, 1)).ToArray();
        var aetherytes = root.GetProperty("npcs").EnumerateArray().GroupBy(npc => npc.GetProperty("aetheryte").GetUInt32())
            .Select(group => new ShopAetheryteSheetRow(group.Key, group.First().GetProperty("territoryName").GetString()!,
                group.First().GetProperty("territory").GetUInt32(), Vector3.Zero)).ToArray();
        return new(items, [], shops, links, placements, aetherytes, [new(1, 28, "Allagan Tomestones of Poetics")]);
    }

    private static JsonDocument ReadFixture()
    {
        using var stream = typeof(PoeticsGearCatalogTests).Assembly.GetManifestResourceStream("ADS.Tests.PoeticsGearOffers.json")!;
        return JsonDocument.Parse(stream);
    }

    private sealed class FixedSheets(ShopCatalogSnapshot snapshot) : IShopSheetSource
    {
        public ShopCatalogSnapshot BuildSnapshot() => snapshot;
    }
}
