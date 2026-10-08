using System.Numerics;
using System.Globalization;
using System.Runtime.InteropServices;
using ADS.Models;
using ADS.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace ADS.Tests;

public sealed class ShopReliabilityExpansionTests
{
    [StructLayout(LayoutKind.Sequential)]
    private struct ClickReceiver
    {
        public AtkEventListener Listener;
        public int Calls;
        public int Parameter;
        public bool ValidEventData;
    }

    [UnmanagedCallersOnly]
    private static unsafe void ReceiveClick(AtkEventListener* listener, AtkEventType type, int parameter,
        AtkEvent* click, AtkEventData* data)
    {
        var receiver = (ClickReceiver*)listener;
        receiver->Calls++;
        receiver->Parameter = parameter;
        receiver->ValidEventData = data != null && click != null && click->Listener == listener &&
            type == AtkEventType.ButtonClick;
        if (click != null) click->Param = 999;
    }

    [Fact]
    public unsafe void ShopButtonDispatchUsesRegisteredReceiverAndOwnedEventData()
    {
        var vtable = new AtkEventListener.AtkEventListenerVirtualTable { ReceiveEvent = &ReceiveClick };
        var receiver = new ClickReceiver { Listener = new AtkEventListener { VirtualTable = &vtable } };
        var click = new AtkEvent { Listener = &receiver.Listener, Param = 3 };
        click.State.EventType = AtkEventType.ButtonClick;

        Assert.True(GameInteractionHelper.TryDispatchRegisteredClick(&click));
        Assert.Equal(1, receiver.Calls);
        Assert.Equal(3, receiver.Parameter);
        Assert.True(receiver.ValidEventData);
        Assert.Equal(3u, click.Param); // The native receiver cannot alter the registered event.

        click.State.StateFlags = AtkEventStateFlags.IsGlobalEvent;
        Assert.False(GameInteractionHelper.TryDispatchRegisteredClick(&click));
        click.State.StateFlags = 0;
        click.Listener = null;
        Assert.False(GameInteractionHelper.TryDispatchRegisteredClick(&click));
        Assert.False(GameInteractionHelper.TryDispatchRegisteredClick(null));
        Assert.Equal(1, receiver.Calls);
    }

    [Fact]
    public void LiveMenuResolverUsesSelectorSlotsAcrossNestedPath()
    {
        var firstStep = new ShopMenuPathStep(ShopMenuPathStepKind.ENpcData, 3, 3_276_827);

        var firstAccepted = ShopMenuRouteResolver.TryResolveSelectorIndex(
            1_001_276,
            1_001_276,
            firstStep,
            [
                new(8, 0, 0),
                new(9, 1, 1),
                new(3_276_827, 2, 3),
                new(10, 3, 4),
                new(11, 4, 5),
            ],
            out var firstCallback,
            out var firstDiagnostic);
        var nestedStep = new ShopMenuPathStep(ShopMenuPathStepKind.TopicSelectShop, 1, 262_191);
        var nestedAccepted = ShopMenuRouteResolver.TryResolveSelectorIndex(
            1_001_276,
            1_001_276,
            nestedStep,
            [
                new(20, 0, 0),
                new(21, 1, 1),
                new(22, 2, 2),
                new(23, 3, 3),
                new(262_191, 4, 1),
            ],
            out var nestedCallback,
            out _);

        Assert.True(firstAccepted);
        Assert.Equal(2, firstCallback);
        Assert.Contains("local index 3", firstDiagnostic, StringComparison.Ordinal);
        Assert.Contains("sheet index 3", firstDiagnostic, StringComparison.Ordinal);
        Assert.True(nestedAccepted);
        Assert.Equal(4, nestedCallback);
    }

    [Fact]
    public void LiveMenuResolverRejectsDuplicateHandlerTargetMismatchAndAbsentHandler()
    {
        var step = new ShopMenuPathStep(ShopMenuPathStepKind.ENpcData, 3, 3_276_827);
        Assert.False(ShopMenuRouteResolver.TryResolveSelectorIndex(
            1_001_276,
            1_001_276,
            step,
            [new(3_276_827, 0, 3), new(3_276_827, 1, 4)],
            out _,
            out _));
        Assert.False(ShopMenuRouteResolver.TryResolveSelectorIndex(
            1_001_276,
            1_000_238,
            step,
            [new(3_276_827, 0, 3)],
            out _,
            out _));
        Assert.False(ShopMenuRouteResolver.TryResolveSelectorIndex(
            1_001_276,
            1_001_276,
            step,
            [new(262_698, 1, 3)],
            out _,
            out _));
    }

    [Fact]
    public void VathGilHandlerSelectionDoesNotUseBeastCurrencyMenuMetadata()
    {
        // The failed client inspection showed NPC 1016804 with ShopExchangeCurrency.
        // Reordered/filtered selector options need not share addon callback indexes.
        var gil = new ShopMenuPathStep(ShopMenuPathStepKind.ENpcData, 1, 262_698);
        Assert.True(ShopMenuRouteResolver.TryResolveSelectorIndex(
            1_016_804, 1_016_804, gil,
            [new(262_698, 1, 1), new(1_769_999, 0, 0)],
            out var selectorIndex, out _));
        Assert.Equal(0, selectorIndex);
        Assert.False(ShopMenuRouteResolver.TryResolveSelectorIndex(
            1_016_804, 1_016_804, gil,
            [new(1_769_999, 0, 0)], out _, out _));
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
    public void ConfirmationTokenUsesSharedTenSecondBoundaryAndRemainsExactSingleUse()
    {
        var evaluated = new EvaluatedShopOffer(
            Offer(false, ShopCurrencyKind.Item, 500) with
            {
                Currencies =
                [
                    new ShopCurrencyCost(ShopCurrencyKind.Item, 500, "Fixture Token", 1),
                    new ShopCurrencyCost(ShopCurrencyKind.Item, 501, "Other Token", 3),
                ],
            },
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
            [new(ShopCurrencyKind.Item, 501)] = 6,
        };

        Assert.False(token.TryConsumeStructured(100, 1, costs, created));
        Assert.False(token.TryConsumeStructured(101, 2, costs, created.AddSeconds(5)));
        Assert.False(token.TryConsumeStructured(
            100,
            2,
            new Dictionary<ShopCurrencyIdentity, long>
            {
                [new(ShopCurrencyKind.Item, 500)] = 2,
                [new(ShopCurrencyKind.Item, 501)] = 5,
            },
            created.AddSeconds(5)));
        Assert.True(token.TryConsumeStructured(100, 2, costs, created.AddSeconds(10)));
        Assert.False(token.TryConsumeStructured(100, 2, costs, created.AddSeconds(10)));

        var expired = new ShopConfirmationToken(evaluated, 2, created);
        Assert.False(expired.TryConsumeStructured(100, 2, costs, created.AddSeconds(10).AddTicks(1)));

        var prompt = new ShopConfirmationToken(evaluated, 2, created);
        Assert.True(prompt.TryConsumePrompt("Purchase 2 Fixture for 2 Fixture Token and 6 Other Token?", created.AddSeconds(5)));
        Assert.False(prompt.TryConsumePrompt("Purchase 2 Fixture for 2 Fixture Token and 6 Other Token?", created.AddSeconds(6)));

        var mismatchedPrompt = new ShopConfirmationToken(evaluated, 2, created);
        Assert.False(mismatchedPrompt.TryConsumePrompt("Purchase 20 Fixture for 20 Fixture Token and 60 Other Token?", created.AddSeconds(5)));

        var mismatchedItemPrompt = new ShopConfirmationToken(evaluated, 2, created);
        Assert.False(mismatchedItemPrompt.TryConsumePrompt(
            "Purchase 2 OtherFixture for costs 2 and 6?",
            created.AddSeconds(5)));
    }

    [Fact]
    public void ConfirmationTokenMatchesExactNinetyNineQuantityPromptAndRejectsMismatch()
    {
        var evaluated = new EvaluatedShopOffer(
            Offer(false, ShopCurrencyKind.Item, 500) with
            {
                Currencies =
                [
                    new ShopCurrencyCost(ShopCurrencyKind.Item, 500, "Fixture Token", 2),
                    new ShopCurrencyCost(ShopCurrencyKind.Item, 501, "Other Token", 6),
                ],
            },
            null,
            [],
            true,
            true,
            true,
            null);
        var created = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);
        const string expectedPrompt = "Purchase 99 Fixture for 198 Fixture Token and 594 Other Token?";
        var exact = new ShopConfirmationToken(evaluated, 99, created);

        Assert.True(exact.TryConsumePrompt(expectedPrompt, created.AddSeconds(5)));

        var mismatched = new ShopConfirmationToken(evaluated, 99, created);
        Assert.False(mismatched.TryConsumePrompt(
            "Purchase 99 Fixture for 198 Fixture Token and 593 Other Token?",
            created.AddSeconds(5)));
    }

    [Fact]
    public void ConfirmationTokenAcceptsPluralisedItemNameInPrompt()
    {
        // The game pluralises the item name in the purchase confirmation while the sheet name stays
        // singular, e.g. token 'Ragworm' against "Purchase 2 ragworms for 16 gil?". Exact whole-word
        // matching rejected those, so ADS refused to confirm its own purchase and failed ui-mismatch.
        var created = new DateTime(2026, 8, 2, 12, 0, 0, DateTimeKind.Utc);

        static EvaluatedShopOffer Gil(string itemName)
            => new(
                Offer(false, ShopCurrencyKind.Gil, 1) with
                {
                    ReceiveItemName = itemName,
                    Currencies = [new ShopCurrencyCost(ShopCurrencyKind.Gil, 1, "Gil", 8)],
                },
                null,
                [],
                true,
                true,
                true,
                null);

        Assert.True(new ShopConfirmationToken(Gil("Ragworm"), 2, created)
            .TryConsumePrompt("Purchase 2 ragworms for 16 gil?", created.AddSeconds(1)));
        Assert.True(new ShopConfirmationToken(Gil("Plump Worm"), 2, created)
            .TryConsumePrompt("Purchase 2 plump worms for 16 gil?", created.AddSeconds(1)));
        Assert.True(new ShopConfirmationToken(Gil("Anchovy"), 2, created)
            .TryConsumePrompt("Purchase 2 anchovies for 16 gil?", created.AddSeconds(1)));

        // Singular prompts must keep working -- Krill is its own plural, which is why the bug hid.
        Assert.True(new ShopConfirmationToken(Gil("Krill"), 2, created)
            .TryConsumePrompt("Purchase 2 krill for 16 gil?", created.AddSeconds(1)));

        // Loosening applies to the item NAME only: quantity and every currency amount stay exact, and
        // an unrelated item is still rejected even in plural form.
        Assert.False(new ShopConfirmationToken(Gil("Ragworm"), 2, created)
            .TryConsumePrompt("Purchase 3 ragworms for 16 gil?", created.AddSeconds(1)));
        Assert.False(new ShopConfirmationToken(Gil("Ragworm"), 2, created)
            .TryConsumePrompt("Purchase 2 ragworms for 15 gil?", created.AddSeconds(1)));
        Assert.False(new ShopConfirmationToken(Gil("Ragworm"), 2, created)
            .TryConsumePrompt("Purchase 2 plump worms for 16 gil?", created.AddSeconds(1)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    public void ConfirmationNumbersMatchWholeInvariantAmountsAcrossCultures(string culture)
    {
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var gil = new EvaluatedShopOffer(
                Offer(false, ShopCurrencyKind.Gil, 1) with
                {
                    ReceiveItemId = 6005,
                    ReceiveItemName = "Wayward Hatchling",
                    Currencies = [new(ShopCurrencyKind.Gil, 1, "Gil", 2400)],
                }, null, [], true, true, true, null);
            var exchange = gil with
            {
                Offer = gil.Offer with
                {
                    Kind = ShopOfferKind.SpecialShopMgp,
                    Currencies = [new(ShopCurrencyKind.Mgp, 29, "MGP", 2400)],
                },
            };
            var created = new DateTime(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);

            foreach (var price in new[] { "2400", "2,400", "2.400", "2\u00A0400", "2\u202F400" })
            {
                var token = new ShopConfirmationToken(gil, 1, created);
                var prompt = $"Purchase 1 wayward hatchling for {price} gil?";
                Assert.True(token.TryConsumePrompt(prompt, created.AddSeconds(10)), prompt);
                Assert.False(token.TryConsumePrompt(prompt, created.AddSeconds(10)));
                Assert.False(new ShopConfirmationToken(gil, 1, created)
                    .TryConsumePrompt(prompt, created.AddSeconds(10).AddTicks(1)));
                Assert.True(new ShopConfirmationToken(exchange, 1, created)
                    .TryConsumeCurrencyPrompt(6005, $"Exchange {price} MGP for the following item?", created));
            }

            var rejected = new ShopConfirmationToken(gil, 1, created);
            foreach (var wrongPrice in new[] { "240", "2401", "24000", "12,400", "12.400", "24,00", "24.00", "2,400.0", "2.400,0", "2,400,000", "2.400.000" })
            {
                Assert.False(rejected.TryConsumePrompt($"Purchase 1 wayward hatchling for {wrongPrice} gil?", created));
                Assert.False(new ShopConfirmationToken(exchange, 1, created)
                    .TryConsumeCurrencyPrompt(6005, $"Exchange {wrongPrice} MGP for the following item?", created));
            }
            foreach (var wrongQuantity in new[] { "2", "11", "1,000", "1.000", "1,0", "1.0", "0,1", "0.1" })
                Assert.False(rejected.TryConsumePrompt($"Purchase {wrongQuantity} wayward hatchlings for 2,400 gil?", created));
            Assert.False(rejected.TryConsumePrompt("Purchase 1 wind-up airship for 2,400 gil?", created));
            Assert.False(rejected.IsConsumed);
            Assert.True(rejected.TryConsumePrompt("Purchase 1 wayward hatchling for 2,400 gil?", created));

            var batch = new ShopConfirmationToken(gil, 515, created);
            Assert.False(batch.TryConsumePrompt("Purchase 515 wayward hatchlings for 1,236.000 gil?", created));
            Assert.True(batch.TryConsumePrompt("Purchase 515 wayward hatchlings for 1.236.000 gil?", created));
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Fact]
    public void TomestonePreviewMatchesFourBonesFor600PoeticsAndKeepsSingleUseTimeout()
    {
        var offer = new EvaluatedShopOffer(
            Offer(false, ShopCurrencyKind.Tomestone, 28) with
            {
                Kind = ShopOfferKind.SpecialShopTomestone,
                ReceiveItemId = 13582,
                ReceiveItemName = "Unidentifiable Bone",
                TransactionsRequired = 4,
                Currencies = [new ShopCurrencyCost(ShopCurrencyKind.Tomestone, 28, "Allagan Tomestone of Poetics", 150)],
            },
            null, [], true, true, true, null);
        var created = new DateTime(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc);
        const string prompt = "Exchange 600 Allagan tomestones of poetics for the following item?";
        var token = new ShopConfirmationToken(offer, 4, created);

        Assert.Equal(4, token.Quantity);
        Assert.False(token.TryConsumePrompt(prompt, created));
        Assert.False(token.TryConsumeCurrencyPrompt(0, prompt, created));
        Assert.False(token.TryConsumeCurrencyPrompt(13583, prompt, created));
        Assert.False(token.TryConsumeCurrencyPrompt(13582, null, created));
        foreach (var wrongCost in new[] { "150", "599", "601", "1600", "1,600", "600.0", "600 and 4" })
            Assert.False(token.TryConsumeCurrencyPrompt(13582, prompt.Replace("600", wrongCost), created));
        Assert.False(token.IsConsumed);
        Assert.True(token.TryConsumeCurrencyPrompt(13582, prompt, created.AddSeconds(10)));
        Assert.False(token.TryConsumeCurrencyPrompt(13582, prompt, created.AddSeconds(10)));
        Assert.False(token.TryConsumeStructured(13582, 4,
            new Dictionary<ShopCurrencyIdentity, long> { [new(ShopCurrencyKind.Tomestone, 28)] = 600 }, created));

        Assert.False(new ShopConfirmationToken(offer, 4, created)
            .TryConsumeCurrencyPrompt(13582, prompt, created.AddSeconds(10).AddTicks(1)));
        Assert.False(new ShopConfirmationToken(offer, 1, created)
            .TryConsumeCurrencyPrompt(13582, prompt, created));
        Assert.True(new ShopConfirmationToken(offer, 99, created)
            .TryConsumeCurrencyPrompt(13582, prompt.Replace("600", "14,850"), created));
        Assert.False(new ShopConfirmationToken(offer with { Offer = offer.Offer with { Kind = ShopOfferKind.GilShop } }, 4, created)
            .TryConsumeCurrencyPrompt(13582, prompt, created));
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
