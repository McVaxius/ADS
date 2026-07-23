using System.Numerics;
using ADS.Models;
using ADS.Services;

namespace ADS.Tests;

public sealed class ShopReliabilityExpansionTests
{
    [Fact]
    public void LiveMenuResolverUsesUniqueHandlerAndIgnoresSheetIndex()
    {
        var step = new ShopMenuPathStep(ShopMenuPathStepKind.ENpcData, 99, 262_191);

        var accepted = ShopMenuRouteResolver.TryResolveVisibleIndex(
            1_001_276,
            1_001_276,
            step,
            [new(8, 0), new(262_191, 5)],
            out var liveIndex,
            out var diagnostic);

        Assert.True(accepted);
        Assert.Equal(5, liveIndex);
        Assert.Contains("diagnostic only", diagnostic, StringComparison.Ordinal);
        Assert.False(ShopMenuRouteResolver.TryResolveVisibleIndex(
            1_001_276,
            1_001_276,
            step,
            [new(262_191, 4), new(262_191, 5)],
            out _,
            out _));
        Assert.False(ShopMenuRouteResolver.TryResolveVisibleIndex(
            1_001_276,
            1_000_238,
            step,
            [new(262_191, 5)],
            out _,
            out _));
    }

    [Fact]
    public void VermaxionRegressionRowsResolveWithoutAdjacentIndexGuessing()
    {
        var greens = RegularGilShopRuntimeValidator.Validate(
            262_191,
            6,
            [
                new(1, 1, false), new(2, 2, false), new(3, 3, false),
                new(4, 4, false), new(5, 5, false), new(4_868, 36, false),
            ],
            6,
            [0, 1, 2, 3, 4, 5],
            262_191,
            4_868,
            36);
        var darkMatterItems = Enumerable.Range(0, 41)
            .Select(index => new ShopRuntimeGilItem(index == 40 ? 33_916u : (uint)(index + 1), index == 40 ? 280 : index + 1, false))
            .ToArray();
        var darkMatter = RegularGilShopRuntimeValidator.Validate(
            262_191,
            darkMatterItems.Length,
            darkMatterItems,
            darkMatterItems.Length,
            Enumerable.Range(0, darkMatterItems.Length).ToArray(),
            262_191,
            33_916,
            280);

        Assert.Equal(5, greens.RuntimeRow);
        Assert.Equal(40, darkMatter.RuntimeRow);
    }

    [Fact]
    public void RecursiveLinkBuilderCoversEveryDeterministicCarrierAndBreaksCycles()
    {
        var links = ShopNpcLinkBuilder.Build(
            new HashSet<uint> { 10 },
            new HashSet<uint> { 20, 21 },
            [new ShopNpcEventSheetRow(1_000, "Expanded Vendor", [
                new(ShopNpcEventKind.CustomTalk, 50),
                new(ShopNpcEventKind.FateShop, 60),
                new(ShopNpcEventKind.InclusionShop, 70),
                new(ShopNpcEventKind.GrandCompanyShop, 80),
                new(ShopNpcEventKind.FreeCompanyShop, 90),
                new(ShopNpcEventKind.CollectablesShop, 100),
                new(ShopNpcEventKind.DisposalShop, 101),
                new(ShopNpcEventKind.LotteryExchangeShop, 102),
            ])],
            [],
            [],
            new HashSet<uint> { 80 },
            new HashSet<uint> { 90 },
            [new ShopFateShopSheetRow(60, [20])],
            [new ShopInclusionRouteSheetRow(70, 21, 2, 3, 700)],
            [new ShopCustomTalkSheetRow(50, [new(ShopNpcEventKind.CustomTalk, 50), new(ShopNpcEventKind.GilShop, 10)])]);

        Assert.Equal(5, links.Count);
        Assert.Contains(links, link => link.LinkKind == ShopNpcLinkKind.CustomTalk && link.ShopId == 10);
        Assert.Contains(links, link => link.LinkKind == ShopNpcLinkKind.FateShop && link.ShopId == 20);
        var inclusion = Assert.Single(links, link => link.LinkKind == ShopNpcLinkKind.InclusionShop);
        Assert.Equal([2, 3], inclusion.CallbackPath.TakeLast(2).Select(step => step.Index));
        Assert.Contains(links, link => link.ShopKind == ShopSheetKind.GrandCompany);
        Assert.Contains(links, link => link.ShopKind == ShopSheetKind.FreeCompany);
    }

    [Fact]
    public void EveryAuditedVirtualCurrencyCodeResolvesAndUnknownCodeFailsClosed()
    {
        uint[] expected =
        [
            10309, 33913, 10311, 33914, 10307, 41784, 41785,
            21072, 21073, 21074, 21075, 21076, 21077, 21078, 21079, 21080, 21081,
            21172, 21173, 21935, 22525, 26533, 26807, 28063, 28186, 28187, 28188, 30341,
        ];
        for (uint code = 1; code <= expected.Length; code++)
        {
            Assert.True(ShopCurrencyResolver.TryConvertCurrencyId(
                1_770_637,
                code,
                16,
                new Dictionary<uint, ShopTomestoneSheetRow>(),
                out var itemId,
                out var tomestone));
            Assert.Equal(expected[code - 1], itemId);
            Assert.False(tomestone);
        }
        Assert.False(ShopCurrencyResolver.TryConvertCurrencyId(
            1_770_637,
            29,
            16,
            new Dictionary<uint, ShopTomestoneSheetRow>(),
            out _,
            out _));
    }

    [Fact]
    public void DeferredGateAndUnknownFcBalanceRemainLowerPriorityCandidates()
    {
        var deferred = Offer(hasUnknownGate: true, ShopCurrencyKind.FreeCompanyCredit, 0);
        var allowed = Offer(hasUnknownGate: false, ShopCurrencyKind.Gil, 1) with { ShopId = 11 };
        var resolution = new ShopCatalogResolution(100, "Fixture", 99, false, 1, [deferred, allowed], 0, 0, 0);
        var context = new ShopSelectionContext(
            1,
            Vector3.Zero,
            _ => true,
            _ => true,
            currency => currency.Kind == ShopCurrencyKind.FreeCompanyCredit ? -1 : 1_000,
            _ => 0,
            (_, _) => 999);

        var result = ShopOfferSelector.Select(resolution, context);

        Assert.Equal((uint)11, result.Selected?.Offer.ShopId);
        var deferredEvaluation = Assert.Single(result.Alternatives, candidate => candidate.Offer.ShopId == 10);
        Assert.Equal(ShopAvailability.Deferred, deferredEvaluation.Availability);
        Assert.False(deferredEvaluation.BalanceKnown);
        Assert.True(deferredEvaluation.Affordable);
    }

    [Fact]
    public void MultiOutputExchangeRequiresCapacityForEveryCoproduct()
    {
        var snapshot = new ShopCatalogSnapshot(
            new Dictionary<uint, ShopItemSheetRow>
            {
                [100] = new(100, "Target", 99, 0, false),
                [200] = new(200, "Coproduct", 99, 0, false),
                [500] = new(500, "Token", 999, 0, false),
            },
            [],
            [new SpecialShopSheetRow(20, "Bundle Shop", 0, [(100u, 2u, false), (200u, 3u, false)], [new(500, 1, 0, 0)], 0, [], false)],
            [new ShopNpcSheetLink(ShopSheetKind.Special, 20, 1_000, "Vendor", [], ShopNpcLinkKind.DirectShop, [], false)],
            [new ShopNpcPlacementSheetRow(1_000, 1, "Fixture", Vector3.Zero, 1)],
            [],
            []);
        var resolution = ShopCatalogBuilder.Resolve(snapshot, 100, 4);
        var offer = Assert.Single(resolution.Offers);
        Assert.Equal(2, offer.TransactionsRequired);
        Assert.Equal(2, offer.AllOutputs.Count);
        var result = ShopOfferSelector.Select(
            resolution,
            new ShopSelectionContext(
                1,
                Vector3.Zero,
                _ => true,
                _ => true,
                _ => 999,
                _ => 0,
                (itemId, _) => itemId == 200 ? 5 : 999));
        Assert.Equal(ShopPurchaseFailureCodes.InventoryCapacity, result.FailureCode);
    }

    [Fact]
    public void ConfirmationTokenIsImmediateSingleUseAndExact()
    {
        var evaluated = new EvaluatedShopOffer(
            Offer(false, ShopCurrencyKind.Item, 500),
            null,
            [],
            true,
            true,
            true,
            null);
        var created = new DateTime(2026, 7, 22, 12, 0, 0, DateTimeKind.Utc);
        var token = new ShopConfirmationToken(evaluated, 2, created);
        var costs = new Dictionary<ShopCurrencyIdentity, long>
        {
            [new(ShopCurrencyKind.Item, 500)] = 2,
        };

        Assert.False(token.TryConsumeStructured(100, 1, costs, created));
        Assert.False(token.TryConsumeStructured(100, 2, costs, created.AddSeconds(6)));
        Assert.True(token.TryConsumeStructured(100, 2, costs, created.AddMilliseconds(10)));
        Assert.False(token.TryConsumeStructured(100, 2, costs, created.AddMilliseconds(20)));
    }

    private static ShopOffer Offer(bool hasUnknownGate, ShopCurrencyKind currencyKind, uint currencyItemId)
        => new(
            ShopOfferKind.GilShop,
            10,
            "Fixture Shop",
            0,
            1_000,
            "Vendor",
            1,
            "Fixture",
            Vector3.Zero,
            [],
            ShopNpcLinkKind.DirectShop,
            ShopNpcPlacementSource.Level,
            0,
            1,
            "Level",
            100,
            "Fixture",
            1,
            1,
            [new ShopCurrencyCost(currencyKind, currencyItemId, "Currency", 1)],
            [],
            hasUnknownGate,
            [],
            false,
            [new ShopOfferOutput(100, "Fixture", 1, 99, false)]);
}
