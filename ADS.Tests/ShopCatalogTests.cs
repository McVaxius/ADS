using System.Numerics;
using System.Text.Json;
using ADS.Models;
using ADS.Services;

namespace ADS.Tests;

public sealed class ShopCatalogTests
{
    [Fact]
    public void MapMarkerCoordinatesConvertToWorldCoordinates()
    {
        var position = ShopAetheryteRouteBuilder.ConvertMarkerToWorld(
            markerX: 1124,
            markerY: 824,
            sizeFactor: 200,
            offsetX: 10,
            offsetY: -20);

        Assert.Equal(new Vector3(40, 0, -80), position);
    }

    [Fact]
    public void AetheryteRoutesRequireExactMapTerritoryAndUniqueMarker()
    {
        ShopAetheryteSourceRow[] aetherytes = [new(8, "Limsa Lominsa", 129, 12, true)];
        ShopMapMarkerSheetRow marker = new(72, 4, 3, 8, 860, 1023);

        var valid = ShopAetheryteRouteBuilder.Build(
            aetherytes,
            [new ShopMapSheetRow(12, 129, 72, 200, 0, 0)],
            [new ShopMapMarkerSheetRow(999, 1, 3, 8, 1, 1), marker]);
        var wrongTerritory = ShopAetheryteRouteBuilder.Build(
            aetherytes,
            [new ShopMapSheetRow(12, 130, 72, 200, 0, 0)],
            [marker]);
        var duplicateMarker = ShopAetheryteRouteBuilder.Build(
            aetherytes,
            [new ShopMapSheetRow(12, 129, 72, 200, 0, 0)],
            [marker, marker with { SubrowId = 5 }]);

        var route = Assert.Single(valid.Routes);
        Assert.Equal(new Vector3(-82, 0, -0.5f), route.Position);
        Assert.Equal((1, 0, 0), (valid.MappedCount, valid.RejectedCount, valid.MissingCount));
        Assert.Empty(wrongTerritory.Routes);
        Assert.Equal((0, 1, 0), (wrongTerritory.MappedCount, wrongTerritory.RejectedCount, wrongTerritory.MissingCount));
        Assert.Empty(duplicateMarker.Routes);
        Assert.Equal((0, 1, 0), (duplicateMarker.MappedCount, duplicateMarker.RejectedCount, duplicateMarker.MissingCount));
    }

    [Fact]
    public void AetheryteRouteBuilderCountsMissingDummyAndMissingMarker()
    {
        var result = ShopAetheryteRouteBuilder.Build(
            [
                new ShopAetheryteSourceRow(1, "Aethernet shard", 1, 0, true),
                new ShopAetheryteSourceRow(8, "Limsa Lominsa", 129, 12, true),
            ],
            [new ShopMapSheetRow(12, 129, 72, 200, 0, 0)],
            []);

        Assert.Empty(result.Routes);
        Assert.Equal((0, 0, 2), (result.MappedCount, result.RejectedCount, result.MissingCount));
    }

    [Theory]
    [InlineData("ffxiv/sea_s1/twn/s1t2/level/s1t2", "bg/ffxiv/sea_s1/twn/s1t2/level/planevent.lgb")]
    [InlineData("bg/ex1/02_dra_d2/twn/d2t1/level/d2t1", "bg/ex1/02_dra_d2/twn/d2t1/level/planevent.lgb")]
    [InlineData("ex4\\02_mid_m5\\twn\\m5t1\\level\\m5t1", "bg/ex4/02_mid_m5/twn/m5t1/level/planevent.lgb")]
    public void PlaneventPathIsDerivedFromTerritoryBackground(string background, string expected)
    {
        Assert.True(ShopLgbPathBuilder.TryDerivePlaneventPath(background, out var actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ffxiv/sea_s1/twn/s1t2")]
    [InlineData("/level/only")]
    public void PlaneventPathRejectsMalformedTerritoryBackground(string background)
        => Assert.False(ShopLgbPathBuilder.TryDerivePlaneventPath(background, out _));

    [Fact]
    public void LgbExtractionKeepsOnlyEventNpcBaseIdsAndPreservesXyz()
    {
        var territory = new ShopTerritorySheetRow(77, "Fixture Territory", 88, "fixture/level/f00");
        var position = new Vector3(12.25f, -3.5f, 98.75f);

        var placements = ShopNpcPlacementBuilder.ExtractLgbPlacements(
            territory,
            "bg/fixture/level/planevent.lgb",
            [
                new ShopLgbInstanceObjectSheetRow(false, 700, new Vector3(1, 2, 3)),
                new ShopLgbInstanceObjectSheetRow(true, 0, new Vector3(4, 5, 6)),
                new ShopLgbInstanceObjectSheetRow(true, 701, position),
            ]);

        var placement = Assert.Single(placements);
        Assert.Equal((uint)701, placement.NpcId);
        Assert.Equal(position, placement.Position);
        Assert.Equal(ShopNpcPlacementSource.Lgb, placement.Source);
        Assert.Equal((uint)88, placement.MapId);
        Assert.Equal("bg/fixture/level/planevent.lgb", placement.SourcePath);
    }

    [Fact]
    public void PlacementMergeFiltersMappedLgbAndUnlinkedNpcsWhileKeepingLevelFallbacks()
    {
        ShopNpcPlacementSheetRow[] lgb =
        [
            Placement(10, 1, new Vector3(1, 2, 3), ShopNpcPlacementSource.Lgb, "mapped-a"),
            Placement(10, 2, new Vector3(20, 2, 3), ShopNpcPlacementSource.Lgb, "unmapped"),
            Placement(99, 1, new Vector3(30, 2, 3), ShopNpcPlacementSource.Lgb, "unlinked"),
        ];
        ShopNpcPlacementSheetRow[] levels =
        [
            Placement(10, 1, new Vector3(1.5f, 2, 3), ShopNpcPlacementSource.Level, "Level", 100),
            Placement(10, 1, new Vector3(5, 2, 3), ShopNpcPlacementSource.Level, "Level", 101),
            Placement(10, 3, new Vector3(7, 2, 3), ShopNpcPlacementSource.Level, "Level", 102),
            Placement(99, 1, new Vector3(40, 2, 3), ShopNpcPlacementSource.Level, "Level", 103),
        ];

        var result = ShopNpcPlacementBuilder.Merge(lgb, levels, new HashSet<uint> { 1 }, new HashSet<uint> { 10 });

        Assert.Equal(3, result.Placements.Count);
        Assert.Contains(result.Placements, row => row.Source == ShopNpcPlacementSource.Lgb && row.Position == new Vector3(1, 2, 3));
        Assert.DoesNotContain(result.Placements, row => row.Position == new Vector3(1.5f, 2, 3));
        Assert.Contains(result.Placements, row => row.Position == new Vector3(5, 2, 3));
        Assert.Contains(result.Placements, row => row.TerritoryId == 3 && row.Source == ShopNpcPlacementSource.Level);
        Assert.Equal((2, 4, 1, 2, 1), (
            result.LgbCollectedCount,
            result.LevelCollectedCount,
            result.UnmappedLgbCount,
            result.UnlinkedNpcCount,
            result.NearDuplicateCount));
    }

    [Fact]
    public void OfflineCatalogIsFallbackUnlessCorrectionExplicitlyReplacesPrimarySources()
    {
        var lgb = Placement(10, 1, new Vector3(1, 2, 3), ShopNpcPlacementSource.Lgb, "lgb");
        var normalOffline = new ShopNpcPlacementSheetRow(
            10,
            1,
            "Territory 1",
            new Vector3(20, 0, 20),
            0,
            ShopNpcPlacementSource.OfflineCatalog,
            1,
            "offline-catalog:garland",
            RequiresFloorResolution: true);
        var missingOffline = normalOffline with { NpcId = 11, Position = new Vector3(30, 0, 30) };
        var replacement = normalOffline with
        {
            NpcId = 12,
            Position = new Vector3(40, 0, 40),
            SourcePath = "offline-catalog:correction",
            ReplacesExisting = true,
        };
        var invalidLevel = Placement(12, 1, new Vector3(99, 9, 99), ShopNpcPlacementSource.Level, "Level", 5);

        var result = ShopNpcPlacementBuilder.Merge(
            [lgb],
            [invalidLevel],
            new HashSet<uint> { 1 },
            new HashSet<uint> { 10, 11, 12 },
            [normalOffline, missingOffline, replacement]);

        Assert.Contains(lgb, result.Placements);
        Assert.DoesNotContain(normalOffline, result.Placements);
        Assert.Contains(missingOffline, result.Placements);
        Assert.Contains(replacement, result.Placements);
        Assert.DoesNotContain(invalidLevel, result.Placements);
        Assert.Equal(2, result.OfflineCatalogCount);
        Assert.Equal(1, result.ReplacementCount);
    }

    [Fact]
    public void OfflineFloorlessPlacementsDeduplicateNearbyXzAndKeepDistinctLocations()
    {
        var first = new ShopNpcPlacementSheetRow(
            10,
            1,
            "Territory 1",
            new Vector3(1, 0, 1),
            0,
            ShopNpcPlacementSource.OfflineCatalog,
            1,
            "offline-catalog:first",
            RequiresFloorResolution: true);
        var nearby = first with { Position = new Vector3(1.5f, 0, 1.5f), SourcePath = "offline-catalog:nearby" };
        var distinct = first with { Position = new Vector3(10, 0, 10), SourcePath = "offline-catalog:distinct" };

        var result = ShopNpcPlacementBuilder.Merge(
            [],
            [],
            new HashSet<uint> { 1 },
            new HashSet<uint> { 10 },
            [nearby, distinct, first]);

        Assert.Equal(2, result.Placements.Count);
        Assert.Equal(1, result.NearDuplicateCount);
        Assert.Contains(result.Placements, row => row.Position == first.Position);
        Assert.Contains(result.Placements, row => row.Position == distinct.Position);
    }

    [Fact]
    public void EmbeddedOfflineCatalogContainsVersatileLureLimsaCorrection()
    {
        var placement = Assert.Single(
            ShopOfflineNpcPlacementCatalog.LoadEmbedded(),
            row => row.NpcId == 1_005_422);

        Assert.Equal((uint)129, placement.TerritoryId);
        Assert.Equal((uint)12, placement.MapId);
        Assert.Equal(-397.6349f, placement.X);
        Assert.Equal(80.979614f, placement.Z);
        Assert.Equal("item-vendor-location-correction", placement.Source);
        Assert.False(placement.ReplaceExisting);
        Assert.True(placement.ToSheetRow().RequiresFloorResolution);
    }

    [Fact]
    public void LinkBuilderEnumeratesEverySupportedDirectTopicAndPreHandlerPath()
    {
        var links = ShopNpcLinkBuilder.Build(
            new HashSet<uint> { 10 },
            new HashSet<uint> { 20 },
            [
                new ShopNpcEventSheetRow(100, "Vendor", [
                    new(ShopNpcEventKind.GilShop, 10),
                    new(ShopNpcEventKind.TopicSelect, 500),
                    new(ShopNpcEventKind.PreHandler, 600),
                ]),
            ],
            [
                new ShopTopicSelectSheetRow(500, [
                    new(ShopNpcEventKind.SpecialShop, 20),
                    new(ShopNpcEventKind.PreHandler, 601),
                    new(ShopNpcEventKind.Unsupported, 999),
                ]),
            ],
            [
                new ShopPreHandlerSheetRow(600, new(ShopNpcEventKind.SpecialShop, 20), 700, false),
                new ShopPreHandlerSheetRow(601, new(ShopNpcEventKind.GilShop, 10), 701, false),
            ]);

        Assert.Equal(4, links.Count);
        Assert.Contains(links, link => link.LinkKind == ShopNpcLinkKind.DirectShop && link.ShopId == 10 && link.MenuPath.SequenceEqual([0]));
        Assert.Contains(links, link => link.LinkKind == ShopNpcLinkKind.TopicSelectShop && link.ShopId == 20 && link.MenuPath.SequenceEqual([1, 0]));
        Assert.Contains(links, link => link.LinkKind == ShopNpcLinkKind.DirectPreHandler && link.ShopId == 20
            && link.MenuPath.SequenceEqual([2]) && link.RequiredQuestIds.SequenceEqual([700u]));
        Assert.Contains(links, link => link.LinkKind == ShopNpcLinkKind.TopicSelectPreHandler && link.ShopId == 10
            && link.MenuPath.SequenceEqual([1, 1]) && link.RequiredQuestIds.SequenceEqual([701u]));
    }

    [Theory]
    [InlineData("shop 38420 3", true)]
    [InlineData("SHOP 38420 9999", true)]
    [InlineData("shop 0 1", false)]
    [InlineData("shop 38420 0", false)]
    [InlineData("shop 38420 10000", false)]
    [InlineData("shop 38420", false)]
    [InlineData("shop 38420 3 extra", false)]
    [InlineData("shop +38420 3", false)]
    [InlineData("shop 0x9614 3", false)]
    public void CommandValidationIsExactAndDecimal(string arguments, bool expected)
        => Assert.Equal(expected, ShopPurchaseRequest.TryParseCommand(arguments, out _, out _));

    [Theory]
    [InlineData("{\"itemId\":38420,\"quantity\":3}", true)]
    [InlineData("{\"itemId\":38420,\"quantity\":0}", false)]
    [InlineData("{\"itemId\":38420}", false)]
    [InlineData("{\"itemId\":\"38420\",\"quantity\":3}", false)]
    public void GenericActionPayloadValidationIsStrict(string json, bool expected)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Equal(expected, ShopPurchaseRequest.TryParseJson(document.RootElement, out _, out _));
    }

    [Fact]
    public void MandervilleFixtureMapsJubrunnahAndPoetics()
    {
        var snapshot = SpecialSnapshot(
            itemId: 38_420,
            itemName: "Manderium Meteorite",
            shopId: 1_770_624,
            shopName: "Manderville Weapon Materials Exchange",
            npcId: 1_043_892,
            npcName: "Jubrunnah",
            territoryId: 963,
            currencyCost: 500) with
        {
            NpcPlacements =
            [
                new ShopNpcPlacementSheetRow(1_043_892, 963, "Radz-at-Han", new Vector3(48.3303f, 0, -12.7965f), 9_333_912),
            ],
            Aetherytes = FixtureAetheryteRoutes(
                (183, "Radz-at-Han", 963, 694, 476, 200, 0, 0, 1076, 967)),
        };

        var resolution = ShopCatalogBuilder.Resolve(snapshot, 38_420, 3);

        var offer = Assert.Single(resolution.Offers);
        Assert.Equal(ShopOfferKind.SpecialShopTomestone, offer.Kind);
        Assert.Equal((uint)1_770_624, offer.ShopId);
        Assert.Equal((uint)1_043_892, offer.NpcId);
        Assert.Equal(3, offer.TransactionsRequired);
        var currency = Assert.Single(offer.Currencies);
        Assert.Equal(ShopCurrencyKind.Tomestone, currency.Kind);
        Assert.Equal((uint)28, currency.ItemId);
        Assert.Equal((uint)500, currency.AmountPerTransaction);
        var route = Assert.Single(offer.AetheryteRoutes);
        Assert.Equal((uint)183, route.AetheryteId);
        Assert.Equal(new Vector3(26, 0, -28.5f), route.Position);
    }

    [Fact]
    public void AnimaFixtureResolvesDirectAndTopicSelectNpcLinks()
    {
        var baseSnapshot = SpecialSnapshot(
            itemId: 13_582,
            itemName: "Unidentifiable Bone",
            shopId: 1_769_781,
            shopName: "Allagan Tomestones of Poetics (Special Arms)",
            npcId: 1_008_119,
            npcName: "Auriana",
            territoryId: 156,
            currencyCost: 150);
        var snapshot = baseSnapshot with
        {
            NpcLinks =
            [
                Link(ShopSheetKind.Special, 1_769_781, 1_008_119, "Auriana", false, 2),
                Link(ShopSheetKind.Special, 1_769_781, 1_012_228, "Hismena", true, 6, 1),
            ],
            NpcPlacements =
            [
                new ShopNpcPlacementSheetRow(1_008_119, 156, "Mor Dhona", new Vector3(62.3635f, 31.288f, -739.956f), 4_374_264),
                new ShopNpcPlacementSheetRow(1_012_228, 478, "Idyllshire", new Vector3(-17.4196f, 211.033f, -41.7179f), 5_881_190),
            ],
            Aetherytes =
                FixtureAetheryteRoutes(
                    (24, "Revenant's Toll", 156, 25, 108, 100, 0, 0, 1064, 356),
                    (75, "Idyllshire", 478, 257, 182, 400, 0, 0, 1312, 948)),
        };

        var resolution = ShopCatalogBuilder.Resolve(snapshot, 13_582, 2);

        Assert.Equal(2, resolution.Offers.Count);
        Assert.Contains(resolution.Offers, offer => offer.NpcName == "Auriana" && !offer.ViaTopicSelect && offer.MenuPath.SequenceEqual([2]));
        Assert.Contains(resolution.Offers, offer => offer.NpcName == "Hismena" && offer.ViaTopicSelect && offer.MenuPath.SequenceEqual([6, 1]));
        Assert.All(resolution.Offers, offer => Assert.Equal((uint)28, Assert.Single(offer.Currencies).ItemId));
        var aurianaRoute = Assert.Single(Assert.Single(resolution.Offers, offer => offer.NpcName == "Auriana").AetheryteRoutes);
        var hismenaRoute = Assert.Single(Assert.Single(resolution.Offers, offer => offer.NpcName == "Hismena").AetheryteRoutes);
        Assert.Equal((uint)24, aurianaRoute.AetheryteId);
        Assert.Equal(new Vector3(40, 0, -668), aurianaRoute.Position);
        Assert.InRange(aurianaRoute.DistanceToNpc, 75f, 76f);
        Assert.Equal((uint)75, hismenaRoute.AetheryteId);
        Assert.Equal(new Vector3(72, 0, -19), hismenaRoute.Position);
        Assert.InRange(hismenaRoute.DistanceToNpc, 92f, 93f);
    }

    [Fact]
    public void GradeEightDarkMatterFixtureRoutesUnsynraelThroughLimsaAetheryte()
    {
        var snapshot = new ShopCatalogSnapshot(
            new Dictionary<uint, ShopItemSheetRow>
            {
                [33_916] = new(33_916, "Grade 8 Dark Matter", 999, 280, false),
            },
            [new GilShopSheetRow(262_191, "Unsynrael's Shop", 40, 33_916, false, [], false)],
            [],
            [Link(ShopSheetKind.Gil, 262_191, 1_001_207, "Unsynrael", false, 0)],
            [new ShopNpcPlacementSheetRow(1_001_207, 129, "Limsa Lominsa Lower Decks", new Vector3(-258.137f, 16.1898f, 54.0626f), 2_073_881)],
            FixtureAetheryteRoutes(
                (8, "Limsa Lominsa Lower Decks", 129, 12, 72, 200, 0, 0, 860, 1023)),
            []);

        var resolution = ShopCatalogBuilder.Resolve(snapshot, 33_916, 1);
        var offer = Assert.Single(resolution.Offers);
        var route = Assert.Single(offer.AetheryteRoutes);

        Assert.Equal((uint)262_191, offer.ShopId);
        Assert.Equal((uint)1_001_207, offer.NpcId);
        Assert.Equal("Unsynrael", offer.NpcName);
        Assert.Equal((uint)8, route.AetheryteId);
        Assert.Equal(new Vector3(-82, 0, -0.5f), route.Position);
        Assert.InRange(route.DistanceToNpc, 184f, 185f);
    }

    [Fact]
    public void RegularGilFixtureUsesItemPriceAndRequestedUnits()
    {
        var snapshot = new ShopCatalogSnapshot(
            new Dictionary<uint, ShopItemSheetRow>
            {
                [29_717] = new(29_717, "Versatile Lure", 999, 300, false),
            },
            [new GilShopSheetRow(263_015, "Tackle Shop", 3, 29_717, false, [], false)],
            [],
            [Link(ShopSheetKind.Gil, 263_015, 1_000_001, "Merchant", false, 0)],
            [new ShopNpcPlacementSheetRow(1_000_001, 129, "Limsa Lominsa", Vector3.Zero, 1)],
            [new ShopAetheryteSheetRow(8, "Limsa Lominsa", 129, Vector3.Zero)],
            []);

        var offer = Assert.Single(ShopCatalogBuilder.Resolve(snapshot, 29_717, 4).Offers);

        Assert.Equal(ShopOfferKind.GilShop, offer.Kind);
        Assert.Equal(4, offer.TransactionsRequired);
        var gil = Assert.Single(offer.Currencies);
        Assert.Equal(ShopCurrencyKind.Gil, gil.Kind);
        Assert.Equal((uint)300, gil.AmountPerTransaction);
    }

    [Fact]
    public void VersatileLureFixtureResolvesSheetLinkToOfflineLimsaVendor()
    {
        const uint itemId = 29_717;
        const uint shopId = 263_015;
        const uint limsaNpcId = 1_005_422;
        const uint otherNpcId = 1_032_822;
        var links = ShopNpcLinkBuilder.Build(
            new HashSet<uint> { shopId },
            new HashSet<uint>(),
            [
                new ShopNpcEventSheetRow(limsaNpcId, "Merchant & Mender", [new(ShopNpcEventKind.GilShop, shopId)]),
                new ShopNpcEventSheetRow(otherNpcId, "Merchant & Mender", [new(ShopNpcEventKind.GilShop, shopId)]),
            ],
            [],
            []);
        var offlinePlacement = Assert.Single(
                ShopOfflineNpcPlacementCatalog.LoadEmbedded(),
                row => row.NpcId == limsaNpcId)
            .ToSheetRow();
        var placements = ShopNpcPlacementBuilder.Merge(
            [],
            [],
            new HashSet<uint> { 129 },
            links.Select(link => link.NpcId).ToHashSet(),
            [offlinePlacement]);
        var snapshot = new ShopCatalogSnapshot(
            new Dictionary<uint, ShopItemSheetRow>
            {
                [itemId] = new(itemId, "Versatile Lure", 999, 300, false),
            },
            [new GilShopSheetRow(shopId, "Purchase Items", 3, itemId, false, [], false)],
            [],
            links,
            placements.Placements,
            [new ShopAetheryteSheetRow(8, "Limsa Lominsa Lower Decks", 129, new Vector3(-82, 0, -0.5f))],
            []);

        var resolution = ShopCatalogBuilder.Resolve(snapshot, itemId, 2);

        var offer = Assert.Single(resolution.Offers);
        Assert.Equal(limsaNpcId, offer.NpcId);
        Assert.Equal(ShopNpcPlacementSource.OfflineCatalog, offer.PlacementSource);
        Assert.Equal("offline-catalog:item-vendor-location-correction", offer.PlacementSourcePath);
        Assert.True(offer.RequiresFloorResolution);
        Assert.Single(offer.AetheryteRoutes);
        Assert.Equal(1, resolution.UnresolvedRouteCount);
        Assert.DoesNotContain(resolution.Offers, candidate => candidate.NpcId == otherNpcId);
    }

    [Fact]
    public void CatalogResolvesUnrelatedRegularAndExchangeItemsAcrossHandlerPaths()
    {
        var links = ShopNpcLinkBuilder.Build(
            new HashSet<uint> { 100 },
            new HashSet<uint> { 200, 201 },
            [
                new ShopNpcEventSheetRow(1_000, "Direct Gil Vendor", [new(ShopNpcEventKind.GilShop, 100)]),
                new ShopNpcEventSheetRow(1_001, "Topic Exchange Vendor", [new(ShopNpcEventKind.TopicSelect, 800)]),
                new ShopNpcEventSheetRow(1_002, "PreHandler Exchange Vendor", [new(ShopNpcEventKind.PreHandler, 900)]),
            ],
            [new ShopTopicSelectSheetRow(800, [new(ShopNpcEventKind.SpecialShop, 200)])],
            [new ShopPreHandlerSheetRow(900, new(ShopNpcEventKind.SpecialShop, 201), 777, false)]);
        var items = new Dictionary<uint, ShopItemSheetRow>
        {
            [501] = new(501, "Regular Fixture", 99, 25, false),
            [502] = new(502, "Topic Fixture", 99, 0, false),
            [503] = new(503, "PreHandler Fixture", 99, 0, false),
            [600] = new(600, "Fixture Token", 999, 0, false),
        };
        var snapshot = new ShopCatalogSnapshot(
            items,
            [new GilShopSheetRow(100, "Regular Shop", 0, 501, false, [], false)],
            [
                new SpecialShopSheetRow(200, "Topic Exchange", 0, [(502u, 1u, false)], [new(600, 2, 0, 0)], 0, [], false),
                new SpecialShopSheetRow(201, "PreHandler Exchange", 0, [(503u, 1u, false)], [new(600, 3, 0, 0)], 0, [], false),
            ],
            links,
            [
                Placement(1_000, 50, new Vector3(1, 0, 1), ShopNpcPlacementSource.Lgb, "gil"),
                Placement(1_001, 50, new Vector3(2, 0, 2), ShopNpcPlacementSource.Lgb, "topic"),
                Placement(1_002, 50, new Vector3(3, 0, 3), ShopNpcPlacementSource.Level, "Level", 99),
            ],
            [new ShopAetheryteSheetRow(5, "Fixture Aetheryte", 50, Vector3.Zero)],
            []);

        var regular = Assert.Single(ShopCatalogBuilder.Resolve(snapshot, 501, 1).Offers);
        var topic = Assert.Single(ShopCatalogBuilder.Resolve(snapshot, 502, 1).Offers);
        var preHandler = Assert.Single(ShopCatalogBuilder.Resolve(snapshot, 503, 1).Offers);

        Assert.Equal((uint)1_000, regular.NpcId);
        Assert.Equal(ShopNpcLinkKind.DirectShop, regular.LinkKind);
        Assert.Equal((uint)1_001, topic.NpcId);
        Assert.Equal(ShopNpcLinkKind.TopicSelectShop, topic.LinkKind);
        Assert.Equal((uint)1_002, preHandler.NpcId);
        Assert.Equal(ShopNpcLinkKind.DirectPreHandler, preHandler.LinkKind);
        Assert.Contains((uint)777, preHandler.RequiredQuestIds);
    }

    [Theory]
    [InlineData(42u)]
    [InlineData(4_000_000u)]
    public void CatalogResolutionIsIndependentOfRegressionItemIds(uint itemId)
    {
        var snapshot = new ShopCatalogSnapshot(
            new Dictionary<uint, ShopItemSheetRow>
            {
                [itemId] = new(itemId, "Arbitrary Fixture", 99, 17, false),
            },
            [new GilShopSheetRow(70, "Arbitrary Shop", 0, itemId, false, [], false)],
            [],
            [Link(ShopSheetKind.Gil, 70, 71, "Arbitrary NPC", false, 0)],
            [Placement(71, 72, Vector3.Zero, ShopNpcPlacementSource.Lgb, "arbitrary")],
            [new ShopAetheryteSheetRow(73, "Arbitrary Aetheryte", 72, Vector3.Zero)],
            []);

        var offer = Assert.Single(ShopCatalogBuilder.Resolve(snapshot, itemId, 3).Offers);

        Assert.Equal(itemId, offer.ReceiveItemId);
        Assert.Equal(3, offer.TransactionsRequired);
    }

    [Fact]
    public void CatalogKeepsEveryNpcLinkAndDistinctPlacementForTheSameShop()
    {
        var snapshot = new ShopCatalogSnapshot(
            new Dictionary<uint, ShopItemSheetRow>
            {
                [800] = new(800, "Fixture", 99, 10, false),
            },
            [new GilShopSheetRow(801, "Shared Shop", 0, 800, false, [], false)],
            [],
            [
                Link(ShopSheetKind.Gil, 801, 802, "Vendor", false, 0),
                Link(ShopSheetKind.Gil, 801, 802, "Vendor", true, 1, 2),
            ],
            [
                Placement(802, 803, new Vector3(0, 0, 0), ShopNpcPlacementSource.Lgb, "first"),
                Placement(802, 803, new Vector3(10, 0, 0), ShopNpcPlacementSource.Lgb, "second"),
            ],
            [new ShopAetheryteSheetRow(804, "Aetheryte", 803, Vector3.Zero)],
            []);

        var resolution = ShopCatalogBuilder.Resolve(snapshot, 800, 1);

        Assert.Equal(4, resolution.Offers.Count);
        Assert.All(resolution.Offers, offer => Assert.Equal((uint)801, offer.ShopId));
        Assert.Equal(2, resolution.Offers.Select(offer => offer.CallbackPath).Distinct().Count());
        Assert.Equal(2, resolution.Offers.Select(offer => offer.NpcPosition).Distinct().Count());
    }

    [Fact]
    public void ExactBundleArithmeticRejectsNonDivisibleQuantity()
    {
        var snapshot = SpecialSnapshot(100, "Bundle", 200, "Exchange", 300, "Vendor", 400, 10) with
        {
            SpecialShopRows =
            [
                new SpecialShopSheetRow(
                    200,
                    "Exchange",
                    0,
                    [(100u, 2u, false)],
                    [new SpecialShopCostSheetRow(1, 10, 0, 2)],
                    4,
                    [],
                    false),
            ],
        };

        var resolution = ShopCatalogBuilder.Resolve(snapshot, 100, 3);

        Assert.Empty(resolution.Offers);
        Assert.Equal(1, resolution.NonDivisibleOfferCount);
    }

    [Fact]
    public void UnknownAndMixedCostFamiliesFailClosed()
    {
        var snapshot = SpecialSnapshot(100, "Item", 200, "Exchange", 300, "Vendor", 400, 10) with
        {
            Items = new Dictionary<uint, ShopItemSheetRow>
            {
                [100] = new(100, "Item", 99, 0, false),
                [500] = new(500, "Token", 999, 0, false),
            },
            SpecialShopRows =
            [
                new SpecialShopSheetRow(200, "Unknown", 0, [(100u, 1u, false)], [new(500, 1, 0, 9)], 0, [], false),
                new SpecialShopSheetRow(201, "Mixed", 0, [(100u, 1u, false)], [new(500, 1, 0, 0), new(1, 10, 0, 2)], 4, [], false),
            ],
            NpcLinks =
            [
                Link(ShopSheetKind.Special, 200, 300, "Vendor", false, 0),
                Link(ShopSheetKind.Special, 201, 300, "Vendor", false, 1),
            ],
        };

        var resolution = ShopCatalogBuilder.Resolve(snapshot, 100, 1);

        Assert.Empty(resolution.Offers);
        Assert.Equal(2, resolution.UnsupportedOfferCount);
    }

    [Fact]
    public void UnresolvedNpcPlacementReturnsNoRoute()
    {
        var snapshot = SpecialSnapshot(100, "Item", 200, "Exchange", 300, "Vendor", 400, 10) with
        {
            NpcPlacements = [],
        };
        var resolution = ShopCatalogBuilder.Resolve(snapshot, 100, 1);
        var selected = ShopOfferSelector.Select(resolution, Context(400, new Dictionary<ShopCurrencyIdentity, long>
        {
            [new(ShopCurrencyKind.Tomestone, 28)] = 100,
        }));

        Assert.Null(selected.Selected);
        Assert.Equal(ShopPurchaseFailureCodes.NoRoute, selected.FailureCode);
    }

    [Fact]
    public void SelectionChecksCapacityAndAffordabilityBeforeTravel()
    {
        var resolution = ShopCatalogBuilder.Resolve(
            SpecialSnapshot(100, "Item", 200, "Exchange", 300, "Vendor", 400, 10),
            100,
            3);
        var currency = new Dictionary<ShopCurrencyIdentity, long>
        {
            [new(ShopCurrencyKind.Tomestone, 28)] = 20,
        };

        var noCapacity = ShopOfferSelector.Select(resolution, Context(400, currency, capacity: 2));
        var noCurrency = ShopOfferSelector.Select(resolution, Context(400, currency, capacity: 99));

        Assert.Equal(ShopPurchaseFailureCodes.InventoryCapacity, noCapacity.FailureCode);
        Assert.Equal(ShopPurchaseFailureCodes.InsufficientCurrency, noCurrency.FailureCode);
    }

    [Fact]
    public void UniqueItemCapacityAllowsOnlyOneWhenNotAlreadyOwned()
    {
        var offer = Offer(10, 100, ShopCurrencyKind.Gil, 1, 10);
        var resolution = new ShopCatalogResolution(100, "Unique Item", 1, true, 2, [offer with { TransactionsRequired = 2 }], 0, 0, 0);
        var result = ShopOfferSelector.Select(resolution, Context(1, new Dictionary<ShopCurrencyIdentity, long>
        {
            [new(ShopCurrencyKind.Gil, 1)] = 1_000,
        }));

        Assert.Equal(ShopPurchaseFailureCodes.InventoryCapacity, result.FailureCode);
    }

    [Fact]
    public void DifferentAffordableCurrenciesAreAmbiguous()
    {
        var resolution = Resolution(
            1,
            Offer(10, 100, ShopCurrencyKind.Gil, 1, 10),
            Offer(11, 101, ShopCurrencyKind.Item, 500, 1));
        var result = ShopOfferSelector.Select(resolution, Context(1, new Dictionary<ShopCurrencyIdentity, long>
        {
            [new(ShopCurrencyKind.Gil, 1)] = 1_000,
            [new(ShopCurrencyKind.Item, 500)] = 100,
        }));

        Assert.Equal(ShopPurchaseFailureCodes.AmbiguousCurrency, result.FailureCode);
    }

    [Fact]
    public void IncomparableSameCurrencySetIsAmbiguous()
    {
        var first = Offer(10, 100, [(ShopCurrencyKind.Item, 500u, 1u), (ShopCurrencyKind.Item, 501u, 2u)]);
        var second = Offer(11, 101, [(ShopCurrencyKind.Item, 500u, 2u), (ShopCurrencyKind.Item, 501u, 1u)]);
        var result = ShopOfferSelector.Select(Resolution(1, first, second), Context(1, new Dictionary<ShopCurrencyIdentity, long>
        {
            [new(ShopCurrencyKind.Item, 500)] = 100,
            [new(ShopCurrencyKind.Item, 501)] = 100,
        }));

        Assert.Equal(ShopPurchaseFailureCodes.AmbiguousCurrency, result.FailureCode);
    }

    [Fact]
    public void ExactCostTiePrefersCurrentTerritoryThenStableIds()
    {
        var other = Offer(9, 90, ShopCurrencyKind.Gil, 1, 10, territoryId: 2);
        var currentHigherId = Offer(11, 101, ShopCurrencyKind.Gil, 1, 10, territoryId: 1);
        var currentLowerId = Offer(10, 100, ShopCurrencyKind.Gil, 1, 10, territoryId: 1);
        var result = ShopOfferSelector.Select(Resolution(1, other, currentHigherId, currentLowerId), Context(1, new Dictionary<ShopCurrencyIdentity, long>
        {
            [new(ShopCurrencyKind.Gil, 1)] = 1_000,
        }));

        Assert.NotNull(result.Selected);
        Assert.Equal((uint)10, result.Selected.Offer.ShopId);
        Assert.Equal(2, result.IdenticalCostFallbacks.Count);
    }

    private static ShopCatalogSnapshot SpecialSnapshot(
        uint itemId,
        string itemName,
        uint shopId,
        string shopName,
        uint npcId,
        string npcName,
        uint territoryId,
        uint currencyCost)
        => new(
            new Dictionary<uint, ShopItemSheetRow>
            {
                [itemId] = new(itemId, itemName, 999, 0, false),
            },
            [],
            [
                new SpecialShopSheetRow(
                    shopId,
                    shopName,
                    0,
                    [(itemId, 1u, false)],
                    [new SpecialShopCostSheetRow(1, currencyCost, 0, 2)],
                    4,
                    [],
                    false),
            ],
            [Link(ShopSheetKind.Special, shopId, npcId, npcName, false, 2)],
            [new ShopNpcPlacementSheetRow(npcId, territoryId, $"Territory {territoryId}", Vector3.Zero, 1)],
            [new ShopAetheryteSheetRow(50, "Aetheryte", territoryId, Vector3.Zero)],
            [new ShopTomestoneSheetRow(1, 28, "Allagan Tomestones of Poetics")]);

    private static IReadOnlyList<ShopAetheryteSheetRow> FixtureAetheryteRoutes(
        params (uint AetheryteId, string Name, uint TerritoryId, uint MapId, ushort MarkerRange, ushort SizeFactor,
            short OffsetX, short OffsetY, short MarkerX, short MarkerY)[] fixtures)
    {
        var result = ShopAetheryteRouteBuilder.Build(
            fixtures.Select(fixture => new ShopAetheryteSourceRow(
                fixture.AetheryteId,
                fixture.Name,
                fixture.TerritoryId,
                fixture.MapId,
                true)).ToArray(),
            fixtures.Select(fixture => new ShopMapSheetRow(
                fixture.MapId,
                fixture.TerritoryId,
                fixture.MarkerRange,
                fixture.SizeFactor,
                fixture.OffsetX,
                fixture.OffsetY)).ToArray(),
            fixtures.Select((fixture, index) => new ShopMapMarkerSheetRow(
                fixture.MarkerRange,
                (ushort)index,
                3,
                fixture.AetheryteId,
                fixture.MarkerX,
                fixture.MarkerY)).ToArray());
        Assert.Equal(fixtures.Length, result.MappedCount);
        Assert.Equal(0, result.RejectedCount);
        Assert.Equal(0, result.MissingCount);
        return result.Routes;
    }

    private static ShopSelectionContext Context(
        uint territoryId,
        IReadOnlyDictionary<ShopCurrencyIdentity, long> currency,
        long capacity = 999)
        => new(
            territoryId,
            Vector3.Zero,
            _ => true,
            _ => true,
            cost => currency.TryGetValue(cost.Identity, out var value) ? value : 0,
            _ => 0,
            (_, _) => capacity);

    private static ShopCatalogResolution Resolution(int quantity, params ShopOffer[] offers)
        => new(100, "Item", 999, false, quantity, offers, 0, 0, 0);

    private static ShopNpcPlacementSheetRow Placement(
        uint npcId,
        uint territoryId,
        Vector3 position,
        ShopNpcPlacementSource source,
        string sourcePath,
        uint levelId = 0)
        => new(
            npcId,
            territoryId,
            $"Territory {territoryId}",
            position,
            levelId,
            source,
            territoryId + 1_000,
            sourcePath);

    private static ShopNpcSheetLink Link(
        ShopSheetKind shopKind,
        uint shopId,
        uint npcId,
        string npcName,
        bool viaTopicSelect,
        params int[] menuPath)
        => new(
            shopKind,
            shopId,
            npcId,
            npcName,
            menuPath.Select((index, position) => new ShopMenuPathStep(
                position == 0 ? ShopMenuPathStepKind.ENpcData : ShopMenuPathStepKind.TopicSelectShop,
                index,
                position == menuPath.Length - 1 ? shopId : 900_000u + (uint)position)).ToArray(),
            viaTopicSelect ? ShopNpcLinkKind.TopicSelectShop : ShopNpcLinkKind.DirectShop,
            [],
            false);

    private static ShopOffer Offer(
        uint shopId,
        uint npcId,
        ShopCurrencyKind kind,
        uint currencyItemId,
        uint amount,
        uint territoryId = 1)
        => Offer(shopId, npcId, [(kind, currencyItemId, amount)], territoryId);

    private static ShopOffer Offer(
        uint shopId,
        uint npcId,
        IReadOnlyList<(ShopCurrencyKind Kind, uint ItemId, uint Amount)> costs,
        uint territoryId = 1)
        => new(
            costs[0].Kind == ShopCurrencyKind.Gil ? ShopOfferKind.GilShop : ShopOfferKind.SpecialShopItem,
            shopId,
            $"Shop {shopId}",
            0,
            npcId,
            $"NPC {npcId}",
            territoryId,
            $"Territory {territoryId}",
            Vector3.Zero,
            [new ShopMenuPathStep(ShopMenuPathStepKind.ENpcData, 0, shopId)],
            ShopNpcLinkKind.DirectShop,
            ShopNpcPlacementSource.Level,
            0,
            1,
            "Level",
            100,
            "Item",
            1,
            1,
            costs.Select(cost => new ShopCurrencyCost(cost.Kind, cost.ItemId, $"Currency {cost.ItemId}", cost.Amount)).ToArray(),
            [],
            false,
            [new ShopRouteCandidate(50 + territoryId, "Aetheryte", Vector3.Zero, 10)]);
}
