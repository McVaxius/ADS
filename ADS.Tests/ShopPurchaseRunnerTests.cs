using System.Numerics;
using ADS.Models;
using ADS.Services;

namespace ADS.Tests;

public sealed class ShopPurchaseRunnerTests
{
    [Fact]
    public void FiniteOrderCreditsOnlyVerifiedPurchasesAcrossCurrencyLimitedPassesAndReload()
    {
        using var temp = new TempDirectory();
        var store = new ShopListPresetStore(temp.Path);
        Assert.True(store.Create("Order test", out _));
        Assert.True(store.ConfigureActive(ShopListMode.FillOrderOverMultipleRuns, ShopCurrencyKind.Tomestone, 28, 0, out _));
        Assert.True(store.SetItem(100, 60, 60, false, ShopListOwnershipScope.InventoryOnly, out _));
        var preset = store.ActivePreset;
        var row = preset.Items.Single();
        var runtime = new FakeRuntime { ApplyItemDelta = true, ApplyCurrencyDelta = true, ItemCount = 2 };
        var clock = new FakeClock();
        var log = System.Reflection.DispatchProxy.Create<Dalamud.Plugin.Services.IPluginLog, ForceMarchLockTests.NoOpProxy>();
        var utility = new UtilityAutomationService(new ShopCatalogService(new BatchSheets()), runtime, clock, log);
        var service = new ShopListService(store, null!, utility, _ => throw new InvalidOperationException("Inventory-only order queried retainers"), log);
        Dictionary<Guid, long>? progress = null;
        for (var pass = 0; pass < 4; pass++)
        {
            runtime.SetCurrency(Poetics, 2_000);
            var response = System.Text.Json.JsonDocument.Parse(service.StartShopListPresetJson(System.Text.Json.JsonSerializer.Serialize(new
            {
                version = 1, operationId = $"pass-{pass}", presetId = preset.PresetId,
                supportsFiniteOrderProgress = true, creditedQuantities = progress,
            })));
            Assert.True(response.RootElement.GetProperty("accepted").GetBoolean());
            for (var tick = 0; tick < 200 && utility.IsRunning; tick++) utility.Update();
            Assert.False(utility.IsRunning);
            var status = utility.ShopListBatchStatus;
            Assert.True(status.Succeeded, status.FailureMessage);
            progress = new(status.CreditedQuantities!);
            Assert.Equal(Math.Min(60, 2 + (pass + 1) * 20), progress[row.RowId]);
            Assert.Equal(pass == 2 ? "fulfilled" : pass < 2 ? "partial" : "fulfilled", status.Disposition);
            Assert.False(runtime.ExpectedShopVisible);
            Assert.True(store.SaveOrderProgress(1, preset.PresetId, progress, out _));
            store.Reload();
            Assert.Equal(progress[row.RowId], store.GetOrderProgress(1, preset.PresetId)[row.RowId]);
            Assert.Empty(store.GetOrderProgress(2, preset.PresetId));
            runtime.ItemCount = 0; // Consume or move everything. Credited progress must remain.
            if (pass == 2) break;
        }
        var callbacks = runtime.SubmitCount;
        var completed = System.Text.Json.JsonDocument.Parse(service.StartShopListPresetJson(System.Text.Json.JsonSerializer.Serialize(new
        {
            version = 1, operationId = "completed-after-reload", presetId = preset.PresetId,
            supportsFiniteOrderProgress = true, creditedQuantities = progress,
        })));
        Assert.Equal("fulfilled", completed.RootElement.GetProperty("disposition").GetString());
        Assert.Equal(callbacks, runtime.SubmitCount);
        Assert.True(store.SaveOrderProgress(1, preset.PresetId, new Dictionary<Guid, long>(), out _));
        Assert.Empty(store.GetOrderProgress(1, preset.PresetId));
        var oldCaller = service.StartShopListPresetJson(System.Text.Json.JsonSerializer.Serialize(new
        { version = 1, operationId = "old-caller", presetId = preset.PresetId }));
        Assert.Contains("Update DAD", oldCaller);
    }

    [Theory]
    [InlineData(ShopListMode.TargetedRefill, false, 5)]
    [InlineData(ShopListMode.SpendUntilCurrencyOrCapacity, true, 20)]
    [InlineData(ShopListMode.FillOrderOverMultipleRuns, false, 5)]
    public void PurchaseTypesRetainTheirSeparateQuotas(ShopListMode mode, bool repeatable, int expected)
    {
        var runtime = new FakeRuntime { ApplyItemDelta = true, ApplyCurrencyDelta = true };
        runtime.SetCurrency(Poetics, 2_000);
        var log = System.Reflection.DispatchProxy.Create<Dalamud.Plugin.Services.IPluginLog, ForceMarchLockTests.NoOpProxy>();
        var utility = new UtilityAutomationService(new ShopCatalogService(new BatchSheets()), runtime, new FakeClock(), log);
        var row = Guid.NewGuid();
        var definition = new ShopListBatchDefinition("types", Guid.NewGuid(), mode, Poetics, 0, [], [],
            [new(row, 100, "Fixture", 5, 5, repeatable, ShopListOwnershipScope.InventoryOnly, 0)])
        { CreditedQuantities = mode == ShopListMode.FillOrderOverMultipleRuns ? new Dictionary<Guid, long> { [row] = 0 } : null };
        Assert.True(utility.StartShopListBatch(definition));
        for (var tick = 0; tick < 200 && utility.IsRunning; tick++) utility.Update();
        Assert.True(utility.ShopListBatchStatus.Succeeded, utility.ShopListBatchStatus.FailureMessage);
        Assert.Equal(expected, runtime.ItemCount);
        Assert.Equal(mode == ShopListMode.FillOrderOverMultipleRuns, utility.ShopListBatchStatus.CreditedQuantities != null);
    }

    [Fact]
    public void FiniteOrderCancellationPreservesVerifiedBundlesButDoesNotCreditUnverifiedDeltas()
    {
        var clock = new FakeClock();
        var runtime = new FakeRuntime { ApplyItemDelta = true, ApplyCurrencyDelta = true };
        runtime.SetCurrency(Poetics, 100_000);
        var log = System.Reflection.DispatchProxy.Create<Dalamud.Plugin.Services.IPluginLog, ForceMarchLockTests.NoOpProxy>();
        var utility = new UtilityAutomationService(new ShopCatalogService(new BatchSheets()), runtime, clock, log);
        var row = Guid.NewGuid();
        Dictionary<Guid, long> saved = [];
        Assert.True(utility.StartShopListBatch(new("cancel-order", Guid.NewGuid(), ShopListMode.FillOrderOverMultipleRuns,
            Poetics, 0, [], [], [new(row, 100, "Fixture", 120, 120, false, ShopListOwnershipScope.InventoryOnly, 0)])
        { CreditedQuantities = new Dictionary<Guid, long> { [row] = 0 }, SaveProgress = totals => saved = new(totals) }));
        for (var tick = 0; tick < 200 && saved.GetValueOrDefault(row) == 0; tick++) utility.Update();
        Assert.Equal(99, saved[row]);
        runtime.ApplyCurrencyDelta = false;
        for (var tick = 0; tick < 100 && runtime.SubmitCount < 2; tick++) utility.Update();
        utility.Cancel("synthetic cancellation");
        Assert.Equal(99, utility.ShopListBatchStatus.CreditedQuantities![row]);
        Assert.Equal(99, saved[row]);
        Assert.Equal("cancelled", utility.ShopListBatchStatus.Disposition);
        Assert.Empty(utility.ShopListBatchStatus.CompletedNonRepeatableRowIds);
    }

    [Theory]
    [InlineData(1u, 0, 1000, "partial", 0)]
    [InlineData(1u, 2000, 0, "partial", 0)]
    [InlineData(3u, 100, 1000, "partial", 3)]
    [InlineData(3u, 2000, 1000, "fulfilled", 6)]
    public void FiniteOrdersHonorCapacityAffordableBundlesAndRounding(uint bundle, long balance, long capacity, string disposition, long purchased)
    {
        var runtime = new FakeRuntime { ApplyItemDelta = true, ApplyCurrencyDelta = true, Capacity = capacity };
        runtime.SetCurrency(Poetics, balance);
        var log = System.Reflection.DispatchProxy.Create<Dalamud.Plugin.Services.IPluginLog, ForceMarchLockTests.NoOpProxy>();
        var utility = new UtilityAutomationService(new ShopCatalogService(new BatchSheets(bundle)), runtime, new FakeClock(), log);
        var row = Guid.NewGuid();
        Assert.True(utility.StartShopListBatch(new("bundles", Guid.NewGuid(), ShopListMode.FillOrderOverMultipleRuns,
            Poetics, 0, [], [], [new(row, 100, "Fixture", 5, 5, false, ShopListOwnershipScope.InventoryOnly, 0)])
        { CreditedQuantities = new Dictionary<Guid, long> { [row] = 0 } }));
        for (var tick = 0; tick < 200 && utility.IsRunning; tick++) utility.Update();
        Assert.Equal(disposition, utility.ShopListBatchStatus.Disposition);
        Assert.Equal(purchased, utility.ShopListBatchStatus.CreditedQuantities![row]);
        Assert.Equal(purchased, runtime.ItemCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FiniteOrderGenuineFailuresRemainFailures(bool failedWrite)
    {
        var runtime = new FakeRuntime { ApplyItemDelta = true, ApplyCurrencyDelta = true };
        runtime.SetCurrency(Poetics, 100_000);
        if (!failedWrite) runtime.Validation = ShopUiValidationResult.Mismatch("Wrong vendor");
        var log = System.Reflection.DispatchProxy.Create<Dalamud.Plugin.Services.IPluginLog, ForceMarchLockTests.NoOpProxy>();
        var utility = new UtilityAutomationService(new ShopCatalogService(new BatchSheets()), runtime, new FakeClock(), log);
        var row = Guid.NewGuid();
        Assert.True(utility.StartShopListBatch(new("fail", Guid.NewGuid(), ShopListMode.FillOrderOverMultipleRuns,
            Poetics, 0, [], [], [new(row, 100, "Fixture", 120, 120, false, ShopListOwnershipScope.InventoryOnly, 0)])
        { CreditedQuantities = new Dictionary<Guid, long> { [row] = 0 }, SaveProgress = _ => throw new IOException("Cannot save") }));
        for (var tick = 0; tick < 200 && utility.IsRunning; tick++) utility.Update();
        Assert.False(utility.ShopListBatchStatus.Succeeded);
        Assert.Equal(failedWrite ? 99 : 0, utility.ShopListBatchStatus.CreditedQuantities![row]);
        Assert.Equal(failedWrite ? 1 : 0, runtime.SubmitCount);
        Assert.Empty(utility.ShopListBatchStatus.CompletedNonRepeatableRowIds);
    }

    [Fact]
    public void StandaloneOrderPersistsOnlyOnRunAndRequiresExplicitResetWhileIpcRemainsSeparate()
    {
        using var temp = new TempDirectory();
        var store = new ShopListPresetStore(temp.Path);
        Assert.True(store.Create("Standalone", out _));
        Assert.True(store.ConfigureActive(ShopListMode.FillOrderOverMultipleRuns, ShopCurrencyKind.Tomestone, 28, 0, out _));
        Assert.True(store.SetItem(100, 5, 5, false, ShopListOwnershipScope.InventoryOnly, out _));
        var presetId = store.ActivePresetId;
        var rowId = store.ActivePreset.Items[0].RowId;
        var runtime = new FakeRuntime { ApplyItemDelta = true, ApplyCurrencyDelta = true };
        var log = System.Reflection.DispatchProxy.Create<Dalamud.Plugin.Services.IPluginLog, ForceMarchLockTests.NoOpProxy>();
        var utility = new UtilityAutomationService(new ShopCatalogService(new BatchSheets()), runtime, new FakeClock(), log);
        var service = new ShopListService(store, null!, utility, _ => throw new InvalidOperationException(), log);
        runtime.SetCurrency(Poetics, 200);
        var before = File.ReadAllText(store.ConfigPath);
        Assert.Equal("ready", service.PreviewActivePreset().Disposition);
        Assert.Equal(before, File.ReadAllText(store.ConfigPath));
        Assert.False(service.StartNewOrder(out _));
        Assert.True(service.TryStartBatch(out _));
        for (var tick = 0; tick < 200 && utility.IsRunning; tick++) utility.Update();
        Assert.Equal(2, store.GetOrderProgress(1, presetId)[rowId]);
        runtime.ItemCount = 0;
        store.Reload();
        runtime.SetCurrency(Poetics, 300);
        Assert.True(service.TryStartBatch(out _));
        for (var tick = 0; tick < 200 && utility.IsRunning; tick++) utility.Update();
        Assert.True(service.IsStandaloneOrderComplete);
        runtime.ItemCount = 0;
        store.Reload();
        var callbacks = runtime.SubmitCount;
        Assert.Equal("fulfilled", service.PreviewActivePreset().Disposition);
        Assert.False(service.TryStartBatch(out _));
        Assert.Equal(callbacks, runtime.SubmitCount);
        runtime.CharacterId = 2;
        Assert.False(service.IsStandaloneOrderComplete);
        Assert.Empty(store.GetOrderProgress(2, presetId));
        runtime.CharacterId = 1;
        runtime.SetCurrency(Poetics, 200);
        var separate = System.Text.Json.JsonDocument.Parse(service.StartShopListPresetJson(System.Text.Json.JsonSerializer.Serialize(new
        { version = 1, operationId = "separate-association", presetId, supportsFiniteOrderProgress = true })));
        Assert.True(separate.RootElement.GetProperty("accepted").GetBoolean());
        for (var tick = 0; tick < 200 && utility.IsRunning; tick++) utility.Update();
        Assert.Equal(2, utility.ShopListBatchStatus.CreditedQuantities![rowId]);
        Assert.Equal(5, store.GetOrderProgress(1, presetId)[rowId]);
        Assert.True(service.StartNewOrder(out _));
        Assert.Empty(store.GetOrderProgress(1, presetId));
    }

    [Fact]
    public void FiniteOrderDoesNotPublishTerminalSuccessUntilCleanupIsProven()
    {
        var runtime = new FakeRuntime { ApplyItemDelta = true, ApplyCurrencyDelta = true, CleanupBlocked = true };
        var log = System.Reflection.DispatchProxy.Create<Dalamud.Plugin.Services.IPluginLog, ForceMarchLockTests.NoOpProxy>();
        var utility = new UtilityAutomationService(new ShopCatalogService(new BatchSheets()), runtime, new FakeClock(), log);
        var row = Guid.NewGuid();
        Assert.True(utility.StartShopListBatch(new("cleanup", Guid.NewGuid(), ShopListMode.FillOrderOverMultipleRuns,
            Poetics, 0, [], [], [new(row, 100, "Fixture", 1, 1, false, ShopListOwnershipScope.InventoryOnly, 0)])
        { CreditedQuantities = new Dictionary<Guid, long> { [row] = 0 } }));
        for (var tick = 0; tick < 80; tick++) utility.Update();
        Assert.True(utility.IsRunning);
        Assert.False(utility.ShopListBatchStatus.Done);
        Assert.Equal(1, utility.ShopListBatchStatus.CreditedQuantities![row]);
        runtime.CleanupBlocked = false;
        utility.Update();
        Assert.True(utility.ShopListBatchStatus.Succeeded);
        runtime.SetCurrency(Poetics, -1);
        Assert.False(utility.StartShopListBatch(new("unknown-balance", Guid.NewGuid(), ShopListMode.FillOrderOverMultipleRuns,
            Poetics, 0, [], [], [new(row, 100, "Fixture", 1, 1, false, ShopListOwnershipScope.InventoryOnly, 0)])));
        Assert.Contains("unavailable", utility.StatusMessage);
    }

    private sealed class BatchSheets(uint bundle = 1) : IShopSheetSource
    {
        public ShopCatalogSnapshot BuildSnapshot() => new(
            new Dictionary<uint, ShopItemSheetRow> { [100] = new(100, "Fixture", 999, 0, false), [28] = new(28, "Poetics", 2000, 0, false) },
            [], [new(10, "Fixture shop", 0, [(100u, bundle, false)], [new(1, 100, 0, 1)], 2, [], false)],
            [new(ShopSheetKind.Special, 10, 100, "Fixture vendor", [], ShopNpcLinkKind.DirectShop, [], false)],
            [new(100, 1, "Fixture territory", Vector3.Zero, 1)], [], [new(1, 28, "Poetics")]);
    }

    [Fact]
    public void RegularGilValidationReturnsTheUniqueVisibleCallbackRow()
    {
        ShopRuntimeGilItem[] items =
        [
            new(33_916, 280, false),
            new(1, 5, false),
        ];

        var result = RegularGilShopRuntimeValidator.Validate(
            262_191,
            items.Length,
            items,
            2,
            [1, 0],
            262_191,
            33_916,
            280);

        Assert.Equal(ShopUiValidationState.Valid, result.State);
        Assert.Equal(1, result.RuntimeRow);
    }

    [Fact]
    public void RegularGilValidationRejectsWrongShopItemPriceAndHqState()
    {
        Assert.Equal(
            ShopUiValidationState.Mismatch,
            ValidateGil(activeShopId: 262_192, items: [new(33_916, 280, false)], visible: [0]).State);
        Assert.Equal(
            ShopUiValidationState.Mismatch,
            ValidateGil(items: [new(1, 280, false)], visible: [0]).State);
        Assert.Equal(
            ShopUiValidationState.Mismatch,
            ValidateGil(items: [new(33_916, 281, false)], visible: [0]).State);
        Assert.Equal(
            ShopUiValidationState.Mismatch,
            ValidateGil(items: [new(33_916, 280, true)], visible: [0]).State);
    }

    [Fact]
    public void RegularGilValidationRejectsDuplicateAndMalformedRuntimeRows()
    {
        Assert.Equal(
            ShopUiValidationState.Mismatch,
            ValidateGil(items: [new(33_916, 280, false), new(33_916, 280, false)], visible: [0, 1]).State);
        Assert.Equal(
            ShopUiValidationState.Mismatch,
            ValidateGil(items: [new(33_916, 280, false)], visible: [0, 0]).State);
        Assert.Equal(
            ShopUiValidationState.Mismatch,
            ValidateGil(items: [new(33_916, 280, false)], visible: [1]).State);

        ShopRuntimeGilItem[] items = [new(33_916, 280, false)];
        Assert.Equal(
            ShopUiValidationState.Mismatch,
            RegularGilShopRuntimeValidator.Validate(262_191, 2, items, 1, [0], 262_191, 33_916, 280).State);
        Assert.Equal(
            ShopUiValidationState.Mismatch,
            RegularGilShopRuntimeValidator.Validate(262_191, 1, items, 2, [0], 262_191, 33_916, 280).State);
    }

    [Fact]
    public void RuntimeCostMatcherAcceptsThreeSlotsAndRequiresExactPopulatedCosts()
    {
        ShopRuntimeCostValue[] runtimeCosts = [new(500, 2), new(0, 0), new(0, 0)];
        ShopCurrencyCost[] expected = [new(ShopCurrencyKind.Item, 500, "Token", 2)];

        Assert.True(ShopRuntimeCostMatcher.Matches(runtimeCosts, expected));
        Assert.False(ShopRuntimeCostMatcher.Matches(
            [new ShopRuntimeCostValue(500, 2), new ShopRuntimeCostValue(501, 1), new ShopRuntimeCostValue(0, 0)],
            expected));
        Assert.False(ShopRuntimeCostMatcher.Matches(
            [new ShopRuntimeCostValue(500, 3), new ShopRuntimeCostValue(0, 0), new ShopRuntimeCostValue(0, 0)],
            expected));
    }

    [Fact]
    public void ExchangeValidationRequiresExactShopItemBundleCostsAndUniqueRow()
    {
        ShopCurrencyCost[] expectedCosts = [new(ShopCurrencyKind.Item, 500, "Token", 2)];
        ShopRuntimeExchangeItem[] receive = [new(29_717, 1)];
        ShopRuntimeCostValue[] costs = [new(500, 2), new(0, 0), new(0, 0)];

        var valid = ExchangeShopRuntimeValidator.Validate(
            "Fixture Exchange",
            receive,
            costs,
            "Fixture Exchange",
            29_717,
            1,
            expectedCosts);

        Assert.Equal(ShopUiValidationState.Valid, valid.State);
        Assert.Equal(0, valid.RuntimeRow);
        Assert.Equal(
            ShopUiValidationState.Mismatch,
            ExchangeShopRuntimeValidator.Validate("Wrong Exchange", receive, costs, "Fixture Exchange", 29_717, 1, expectedCosts).State);
        Assert.Equal(
            ShopUiValidationState.Mismatch,
            ExchangeShopRuntimeValidator.Validate("Fixture Exchange", [new(1_029_717, 1)], costs, "Fixture Exchange", 29_717, 1, expectedCosts).State);
        Assert.Equal(
            ShopUiValidationState.Mismatch,
            ExchangeShopRuntimeValidator.Validate("Fixture Exchange", [new(29_717, 2)], costs, "Fixture Exchange", 29_717, 1, expectedCosts).State);
        Assert.Equal(
            ShopUiValidationState.Mismatch,
            ExchangeShopRuntimeValidator.Validate("Fixture Exchange", receive, [new(500, 3), new(0, 0), new(0, 0)], "Fixture Exchange", 29_717, 1, expectedCosts).State);
        Assert.Equal(
            ShopUiValidationState.Mismatch,
            ExchangeShopRuntimeValidator.Validate(
                "Fixture Exchange",
                [new(29_717, 1), new(29_717, 1)],
                [new(500, 2), new(500, 2)],
                "Fixture Exchange",
                29_717,
                1,
                expectedCosts).State);
        Assert.Equal(
            ShopUiValidationState.Mismatch,
            ExchangeShopRuntimeValidator.Validate(
                "Fixture Exchange",
                [new(29_717, 1), new(1, 1)],
                [new(500, 2), new(0, 0), new(0, 0)],
                "Fixture Exchange",
                29_717,
                1,
                expectedCosts).State);
    }

    [Fact]
    public void CurrencyExchangeUsesDisplayedPricesAndCallbackIndicesWithIndependentItemChecks()
    {
        uint[] ids = [16934, 16933, 16064, 15840, 14899, 13582, 13584, 13586, 13588, 9540, 7885, 6267, 6268, 7777];
        uint[] prices = [500, 100, 40, 75, 350, 150, 150, 150, 150, 200, 25, 15, 20, 250];
        var receives = ids.Select(id => new ShopRuntimeExchangeItem(id, 1)).ToArray();
        var values = Enumerable.Repeat(-1L, 1432).ToArray();
        values[4] = ids.Length;
        for (var row = 0; row < ids.Length; row++)
        {
            values[456 + row] = prices[row];
            values[1066 + row] = ids[row];
            values[1310 + row] = ids.Length - row - 1;
        }
        ShopUiValidationResult Validate(uint currencyId = 28) => ExchangeShopRuntimeValidator.ValidateCurrency(
            values, "Poetics", receives, currencyId, "Poetics", 6267, 1,
            [new(ShopCurrencyKind.Tomestone, 28, "Poetics", 15)]);

        Assert.Equal(ShopUiValidationState.Valid, Validate().State);
        Assert.Equal(2, Validate().RuntimeRow);
        Assert.Equal(ShopUiValidationState.Mismatch, Validate(29).State);
        Assert.Equal(ShopUiValidationState.Mismatch, Validate(0).State);
        values[467] = 20;
        Assert.Equal(ShopUiValidationState.Mismatch, Validate().State);
        values[467] = 15;
        values[1077] = 6268;
        Assert.Equal(ShopUiValidationState.Mismatch, Validate().State);
        values[1077] = 6267;
        values[1321] = values[1322];
        Assert.Equal(ShopUiValidationState.Mismatch, Validate().State);
        values[1321] = -1;
        Assert.Equal(ShopUiValidationState.Mismatch, Validate().State);
        values[4] = 123;
        Assert.Equal(ShopUiValidationState.Mismatch, Validate().State);
        values[4] = 0;
        Assert.Equal(ShopUiValidationState.NotReady, Validate().State);
    }

    [Fact]
    public void ExchangeAllocatedButEmptyCostsWaitWithoutAcceptingTheRow()
    {
        var result = ExchangeShopRuntimeValidator.Validate("Poetics", [new(6267, 1)],
            [new(0, 0), new(0, 0), new(0, 0)], "Poetics", 6267, 1,
            [new(ShopCurrencyKind.Tomestone, 28, "Poetics", 15)]);
        Assert.Equal(ShopUiValidationState.NotReady, result.State);
        Assert.Equal(-1, result.RuntimeRow);
    }

    [Fact]
    public void UiMismatchNeverSubmitsPurchase()
    {
        var clock = new FakeClock();
        var runtime = new FakeRuntime
        {
            Validation = ShopUiValidationResult.Mismatch("fixture mismatch"),
        };
        var runner = CreateRunner(3, runtime, clock);

        Assert.True(runner.Start(new ShopPurchaseRequest(100, 3)));
        Drive(runner);

        Assert.Equal(0, runtime.SubmitCount);
        Assert.Equal(ShopPurchaseFailureCodes.UiMismatch, runner.Status.FailureCode);
        Assert.Equal(0, runner.Status.AcquiredQuantity);
    }

    [Fact]
    public void ValidationMismatchAdvancesToNextIdenticalCostNpc()
    {
        var clock = new FakeClock();
        var runtime = new FakeRuntime { ApplyItemDelta = true, ApplyCurrencyDelta = true };
        runtime.NpcValidations[100] = ShopUiValidationResult.Mismatch("wrong first shop");
        runtime.NpcValidations[101] = ShopUiValidationResult.Valid(0);
        var runner = new ShopPurchaseRunner(
            new FakeCatalog(Resolution(1, [Offer(10, 100, 1), Offer(11, 101, 1)])),
            runtime,
            clock);

        Assert.True(runner.Start(new ShopPurchaseRequest(100, 1)));
        Drive(runner);

        Assert.True(runner.Status.Succeeded);
        Assert.Equal((uint)101, runner.Status.SelectedOffer?.NpcId);
        Assert.Equal([100u, 101u], runtime.InteractedNpcIds);
        Assert.Equal([101u], runtime.SubmittedNpcIds);
        Assert.True(runtime.CloseUiCount >= 1);
        Assert.True(runtime.Events.IndexOf("close-ui") < runtime.Events.IndexOf("interact:101"));
    }

    [Fact]
    public void ValidationTimeoutAdvancesToNextIdenticalCostNpc()
    {
        var clock = new FakeClock();
        var runtime = new FakeRuntime { ApplyItemDelta = true, ApplyCurrencyDelta = true };
        runtime.NpcValidations[100] = ShopUiValidationResult.NotReady("first runtime surface unavailable");
        runtime.NpcValidations[101] = ShopUiValidationResult.Valid(0);
        var runner = new ShopPurchaseRunner(
            new FakeCatalog(Resolution(1, [Offer(10, 100, 1), Offer(11, 101, 1)])),
            runtime,
            clock);
        Assert.True(runner.Start(new ShopPurchaseRequest(100, 1)));
        DriveUntil(runner, () => runner.Status.Phase == "validating-ui");

        runner.Update();
        clock.Advance(TimeSpan.FromSeconds(21));
        runner.Update();
        Drive(runner);

        Assert.True(runner.Status.Succeeded);
        Assert.Equal([101u], runtime.SubmittedNpcIds);
        Assert.Contains(runtime.Events, entry => entry == "close-ui");
    }

    [Fact]
    public void InteractionFailureAdvancesToNextIdenticalCostNpc()
    {
        var clock = new FakeClock();
        var runtime = new FakeRuntime { ApplyItemDelta = true, ApplyCurrencyDelta = true };
        runtime.InteractionResults[100] = false;
        var runner = new ShopPurchaseRunner(
            new FakeCatalog(Resolution(1, [Offer(10, 100, 1), Offer(11, 101, 1)])),
            runtime,
            clock);

        Assert.True(runner.Start(new ShopPurchaseRequest(100, 1)));
        DriveWithTime(runner, clock);

        Assert.True(runner.Status.Succeeded);
        Assert.Equal(3, runtime.InteractedNpcIds.Count(id => id == 100));
        Assert.Contains((uint)101, runtime.InteractedNpcIds);
        Assert.Equal([101u], runtime.SubmittedNpcIds);
    }

    [Fact]
    public void MenuFailureClosesOwnedUiBeforeAdvancingToNextNpc()
    {
        var clock = new FakeClock();
        var runtime = new FakeRuntime { ApplyItemDelta = true, ApplyCurrencyDelta = true };
        runtime.SelectionMenuNpcIds.Add(100);
        runtime.MenuSelectResults.Enqueue(false);
        runtime.MenuSelectResults.Enqueue(false);
        runtime.MenuSelectResults.Enqueue(false);
        var runner = new ShopPurchaseRunner(
            new FakeCatalog(Resolution(1, [Offer(10, 100, 1), Offer(11, 101, 1)])),
            runtime,
            clock);

        Assert.True(runner.Start(new ShopPurchaseRequest(100, 1)));
        DriveWithTime(runner, clock);

        Assert.True(runner.Status.Succeeded);
        Assert.Equal(3, runtime.MenuSelectCount);
        Assert.Equal([101u], runtime.SubmittedNpcIds);
        Assert.True(runtime.Events.IndexOf("close-ui") < runtime.Events.IndexOf("interact:101"));
    }

    [Fact]
    public void BatchesNeverExceedNinetyNineAndVerifiedDeltasAdvanceOnce()
    {
        var clock = new FakeClock();
        var runtime = new FakeRuntime { ApplyItemDelta = true, ApplyCurrencyDelta = true };
        var runner = CreateRunner(120, runtime, clock);

        Assert.True(runner.Start(new ShopPurchaseRequest(100, 120)));
        Drive(runner);

        Assert.Equal([99, 21], runtime.SubmittedBatches);
        Assert.True(runner.Status.Succeeded);
        Assert.Equal(120, runner.Status.AcquiredQuantity);
        Assert.Equal(0, runner.Status.RemainingQuantity);
        Assert.Equal(120, runtime.ItemCount);
    }

    [Fact]
    public void MissingDeltaNeverDuplicatesCallbackAndTimesOut()
    {
        var clock = new FakeClock();
        var runtime = new FakeRuntime();
        var runner = CreateRunner(5, runtime, clock);
        Assert.True(runner.Start(new ShopPurchaseRequest(100, 5)));
        DriveUntil(runner, () => runtime.SubmitCount == 1);

        for (var index = 0; index < 5; index++)
            runner.Update();
        Assert.Equal(1, runtime.SubmitCount);

        clock.Advance(TimeSpan.FromSeconds(11));
        runner.Update();

        Assert.Equal(1, runtime.SubmitCount);
        Assert.Equal(ShopPurchaseFailureCodes.Timeout, runner.Status.FailureCode);
        Assert.Equal(0, runner.Status.AcquiredQuantity);
    }

    [Fact]
    public void CancellationPreservesPartialAcquisitionTruth()
    {
        var clock = new FakeClock();
        var runtime = new FakeRuntime { ApplyItemDelta = true };
        var runner = CreateRunner(120, runtime, clock);
        Assert.True(runner.Start(new ShopPurchaseRequest(100, 120)));
        DriveUntil(runner, () => runtime.SubmitCount == 1);

        runner.Cancel("test cancellation");

        Assert.False(runner.Status.Running);
        Assert.False(runner.Status.Succeeded);
        Assert.Equal(ShopPurchaseFailureCodes.Cancelled, runner.Status.FailureCode);
        Assert.Equal(99, runner.Status.AcquiredQuantity);
        Assert.Equal(21, runner.Status.RemainingQuantity);
    }

    [Fact]
    public void DeltaTimeoutPreservesPartialAcquisitionTruth()
    {
        var clock = new FakeClock();
        var runtime = new FakeRuntime { ApplyItemDelta = true };
        var runner = CreateRunner(120, runtime, clock);
        Assert.True(runner.Start(new ShopPurchaseRequest(100, 120)));
        DriveUntil(runner, () => runtime.SubmitCount == 1);

        clock.Advance(TimeSpan.FromSeconds(11));
        runner.Update();

        Assert.Equal(ShopPurchaseFailureCodes.Timeout, runner.Status.FailureCode);
        Assert.Equal(99, runner.Status.AcquiredQuantity);
        Assert.Equal(21, runner.Status.RemainingQuantity);
        Assert.Equal(1, runtime.SubmitCount);
    }

    [Fact]
    public void NavigationTimeoutUsesIdenticalCostFallbackAfter120Seconds()
    {
        var clock = new FakeClock();
        var runtime = new FakeRuntime
        {
            ApplyItemDelta = true,
            ApplyCurrencyDelta = true,
        };
        runtime.NpcDistances[100] = 100;
        runtime.NpcDistances[101] = 0;
        var offers = new[]
        {
            Offer(10, 100, 1),
            Offer(11, 101, 1),
        };
        var runner = new ShopPurchaseRunner(new FakeCatalog(Resolution(1, offers)), runtime, clock);
        Assert.True(runner.Start(new ShopPurchaseRequest(100, 1)));

        runner.Update();
        clock.Advance(TimeSpan.FromSeconds(121));
        runner.Update();
        Drive(runner);

        Assert.True(runner.Status.Succeeded);
        Assert.Equal((uint)101, runner.Status.SelectedOffer?.NpcId);
        Assert.Equal(1, runtime.SubmitCount);
        Assert.Equal(2, runtime.StopNavigationCount);
        Assert.True(runtime.Events.IndexOf("stop-navigation") < runtime.Events.LastIndexOf("move:NPC 101"));
    }

    [Fact]
    public void DelayedNavigationStopBlocksInteractionUntilConfirmed()
    {
        var clock = new FakeClock();
        var runtime = new FakeRuntime { ApplyItemDelta = true, ApplyCurrencyDelta = true };
        runtime.NavigationStopResults.Enqueue(ShopNavigationStopResult.StillRunning);
        runtime.NavigationStopResults.Enqueue(ShopNavigationStopResult.Stopped);
        var runner = CreateRunner(1, runtime, clock);
        Assert.True(runner.Start(new ShopPurchaseRequest(100, 1)));

        runner.Update();
        runner.Update();

        Assert.Equal("stopping-navigation", runner.Status.Phase);
        Assert.Equal(1, runtime.StopNavigationCount);
        Assert.Empty(runtime.InteractedNpcIds);
        Assert.Equal(0, runtime.MenuSelectCount);
        Assert.Equal(0, runtime.SubmitCount);

        clock.Advance(TimeSpan.FromSeconds(2.1));
        runner.Update();
        Assert.Equal("interacting", runner.Status.Phase);
        Assert.Empty(runtime.InteractedNpcIds);

        Drive(runner);
        Assert.True(runner.Status.Succeeded);
        Assert.Equal(2, runtime.StopNavigationCount);
        Assert.True(runtime.Events.LastIndexOf("stop-navigation") < runtime.Events.IndexOf("interact:100"));
    }

    [Theory]
    [InlineData((int)ShopNavigationStopResult.StillRunning)]
    [InlineData((int)ShopNavigationStopResult.Unverified)]
    public void PersistentUnstoppedNavigationTerminatesNoRouteWithoutCallbacks(
        int stopResultValue)
    {
        var stopResult = (ShopNavigationStopResult)stopResultValue;
        var clock = new FakeClock();
        var runtime = new FakeRuntime();
        for (var index = 0; index < 4; index++)
            runtime.NavigationStopResults.Enqueue(stopResult);
        var runner = CreateRunner(1, runtime, clock);
        Assert.True(runner.Start(new ShopPurchaseRequest(100, 1)));

        runner.Update();
        runner.Update();
        for (var index = 0; index < 2 && runner.IsRunning; index++)
        {
            clock.Advance(TimeSpan.FromSeconds(2.1));
            runner.Update();
        }

        Assert.False(runner.IsRunning);
        Assert.Equal(ShopPurchaseFailureCodes.NoRoute, runner.Status.FailureCode);
        Assert.Equal(4, runtime.StopNavigationCount);
        Assert.Empty(runtime.InteractedNpcIds);
        Assert.Equal(0, runtime.MenuSelectCount);
        Assert.Equal(0, runtime.SubmitCount);
        Assert.Contains(
            stopResult == ShopNavigationStopResult.Unverified ? "could not verify" : "still running",
            runner.Status.FailureMessage,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CandidateFallbackCannotMoveUntilPreviousNavigationStopIsConfirmed()
    {
        var clock = new FakeClock();
        var runtime = new FakeRuntime { ApplyItemDelta = true, ApplyCurrencyDelta = true };
        runtime.NpcDistances[100] = 100;
        runtime.NpcDistances[101] = 0;
        runtime.NavigationStopResults.Enqueue(ShopNavigationStopResult.StillRunning);
        runtime.NavigationStopResults.Enqueue(ShopNavigationStopResult.Stopped);
        var runner = new ShopPurchaseRunner(
            new FakeCatalog(Resolution(1, [Offer(10, 100, 1), Offer(11, 101, 1)])),
            runtime,
            clock);
        Assert.True(runner.Start(new ShopPurchaseRequest(100, 1)));

        runner.Update();
        clock.Advance(TimeSpan.FromSeconds(121));
        runner.Update();

        Assert.Equal("stopping-navigation", runner.Status.Phase);
        Assert.Equal(["move:NPC 100", "stop-navigation"], runtime.Events);
        Assert.DoesNotContain(101u, runtime.InteractedNpcIds);

        clock.Advance(TimeSpan.FromSeconds(2.1));
        runner.Update();
        Assert.True(runtime.Events.LastIndexOf("stop-navigation") < runtime.Events.IndexOf("move:NPC 101"));

        Drive(runner);
        Assert.True(runner.Status.Succeeded);
        Assert.Equal([101u], runtime.SubmittedNpcIds);
    }

    [Fact]
    public void LiveNpcRetargetCannotMoveUntilPreviousNavigationStopIsConfirmed()
    {
        var clock = new FakeClock();
        var runtime = new FakeRuntime
        {
            ApplyItemDelta = true,
            ApplyCurrencyDelta = true,
            FloorPosition = new Vector3(10, 3, 10),
        };
        runtime.MissingNpcIds.Add(100);
        runtime.NavigationStopResults.Enqueue(ShopNavigationStopResult.StillRunning);
        runtime.NavigationStopResults.Enqueue(ShopNavigationStopResult.Stopped);
        var offlineOffer = Offer(10, 100, 1) with
        {
            NpcPosition = new Vector3(10, 0, 10),
            RequiresFloorResolution = true,
        };
        var runner = new ShopPurchaseRunner(new FakeCatalog(Resolution(1, [offlineOffer])), runtime, clock);
        Assert.True(runner.Start(new ShopPurchaseRequest(100, 1)));
        runner.Update();

        runtime.MissingNpcIds.Remove(100);
        runtime.NpcPositions[100] = new Vector3(20, 5, 20);
        runtime.NpcDistances[100] = 20;
        runner.Update();

        Assert.Equal("stopping-navigation", runner.Status.Phase);
        Assert.Equal([new Vector3(10, 3, 10)], runtime.MoveDestinations);

        clock.Advance(TimeSpan.FromSeconds(2.1));
        runner.Update();
        Assert.Equal([new Vector3(10, 3, 10), new Vector3(20, 5, 20)], runtime.MoveDestinations);
        Assert.True(runtime.Events.LastIndexOf("stop-navigation") < runtime.Events.IndexOf("move:NPC 100", 1));

        runtime.NpcDistances[100] = 0;
        Drive(runner);
        Assert.True(runner.Status.Succeeded);
    }

    [Fact]
    public void AcceptedTeleportPersistsWithoutDuplicateCommands()
    {
        var clock = new FakeClock();
        var runtime = new FakeRuntime();
        runtime.TeleportArrivalResults.Enqueue(false);
        var runner = new ShopPurchaseRunner(
            new FakeCatalog(Resolution(1, [Offer(10, 100, 1, territoryId: 2)])),
            runtime,
            clock);
        Assert.True(runner.Start(new ShopPurchaseRequest(100, 1)));

        runner.Update();
        Assert.Equal(1, runtime.TeleportCount);
        clock.Advance(TimeSpan.FromSeconds(7));
        runner.Update();

        Assert.True(runner.IsRunning);
        Assert.Equal("teleporting", runner.Status.Phase);
        Assert.Equal(1, runtime.TeleportCount);
        runner.Cancel("test complete");
    }

    [Fact]
    public void ConfirmedTeleportRebasesOnlyGilAndCompletesOneShotPurchase()
    {
        var clock = new FakeClock();
        var gil = new ShopCurrencyIdentity(ShopCurrencyKind.Gil, 1);
        var runtime = new FakeRuntime { ApplyItemDelta = true, ApplyCurrencyDelta = true };
        runtime.TeleportArrivalResults.Enqueue(false);
        runtime.NpcDistances[100] = 20;
        runtime.NpcPositions[100] = new Vector3(20, 0, 0);
        var offer = Offer(10, 100, 1, territoryId: 2) with
        {
            Currencies = [new ShopCurrencyCost(ShopCurrencyKind.Gil, 1, "Gil", 10)],
        };
        var runner = new ShopPurchaseRunner(new FakeCatalog(Resolution(1, [offer])), runtime, clock);
        Assert.True(runner.Start(new ShopPurchaseRequest(100, 1)));

        runner.Update();
        runtime.AdjustCurrency(gil, -50);
        runner.Update();
        Assert.True(runner.IsRunning);
        Assert.Equal("teleporting", runner.Status.Phase);

        runtime.CurrentTerritoryId = 2;
        runner.Update();
        Assert.Equal(1, runtime.MoveCount);
        runtime.NpcDistances[100] = 0;
        Drive(runner);

        Assert.True(runner.Status.Succeeded);
        Assert.Equal(1, runtime.TeleportCount);
        Assert.Equal(1, runtime.MoveCount);
        Assert.Equal(1, runtime.SubmitCount);
        Assert.Equal(9_940, runtime.GetAvailableCurrency(offer.Currencies[0]));
    }

    [Fact]
    public void NonGilMutationDuringTeleportStillFailsOnConfirmedArrival()
    {
        var clock = new FakeClock();
        var runtime = new FakeRuntime();
        runtime.TeleportArrivalResults.Enqueue(false);
        var runner = new ShopPurchaseRunner(
            new FakeCatalog(Resolution(1, [Offer(10, 100, 1, territoryId: 2)])),
            runtime,
            clock);
        Assert.True(runner.Start(new ShopPurchaseRequest(100, 1)));

        runner.Update();
        runtime.AdjustCurrency(new ShopCurrencyIdentity(ShopCurrencyKind.Item, 500), -1);
        runner.Update();
        Assert.True(runner.IsRunning);
        Assert.Equal("teleporting", runner.Status.Phase);

        runtime.CurrentTerritoryId = 2;
        runner.Update();

        Assert.Equal(ShopPurchaseFailureCodes.UiMismatch, runner.Status.FailureCode);
        Assert.Equal(0, runtime.MoveCount);
        Assert.Equal(0, runtime.SubmitCount);
    }

    [Fact]
    public void TeleportTimeoutTriesEachIdenticalCostCandidateOnlyOnceWhenAccepted()
    {
        var clock = new FakeClock();
        var runtime = new FakeRuntime { ApplyItemDelta = true, ApplyCurrencyDelta = true };
        runtime.TeleportArrivalResults.Enqueue(false);
        runtime.TeleportArrivalResults.Enqueue(true);
        var runner = new ShopPurchaseRunner(
            new FakeCatalog(Resolution(1, [
                Offer(10, 100, 1, territoryId: 2),
                Offer(11, 101, 1, territoryId: 3),
            ])),
            runtime,
            clock);
        Assert.True(runner.Start(new ShopPurchaseRequest(100, 1)));

        runner.Update();
        clock.Advance(TimeSpan.FromSeconds(91));
        runner.Update();
        Drive(runner);

        Assert.True(runner.Status.Succeeded);
        Assert.Equal(2, runtime.TeleportCount);
        Assert.Equal([52u, 53u], runtime.TeleportedAetherytes);
        Assert.Equal([101u], runtime.SubmittedNpcIds);
    }

    [Fact]
    public void FailedTeleportCommandStopsSendingAfterThreeAttempts()
    {
        var clock = new FakeClock();
        var runtime = new FakeRuntime();
        runtime.TeleportResults.Enqueue(false);
        runtime.TeleportResults.Enqueue(false);
        runtime.TeleportResults.Enqueue(false);
        var runner = new ShopPurchaseRunner(
            new FakeCatalog(Resolution(1, [Offer(10, 100, 1, territoryId: 2)])),
            runtime,
            clock);
        Assert.True(runner.Start(new ShopPurchaseRequest(100, 1)));

        runner.Update();
        for (var index = 0; index < 3 && runner.IsRunning; index++)
        {
            clock.Advance(TimeSpan.FromSeconds(2.1));
            runner.Update();
        }

        Assert.False(runner.IsRunning);
        Assert.Equal(3, runtime.TeleportCount);
        Assert.Equal(ShopPurchaseFailureCodes.NoRoute, runner.Status.FailureCode);
        Assert.Equal(0, runtime.SubmitCount);
    }

    [Fact]
    public void AcceptedNavigationPersistsBeyondSixSecondsWithoutDuplicateCommands()
    {
        var clock = new FakeClock();
        var runtime = new FakeRuntime();
        runtime.NpcDistances[100] = 100;
        var runner = CreateRunner(1, runtime, clock);
        Assert.True(runner.Start(new ShopPurchaseRequest(100, 1)));

        runner.Update();
        Assert.Equal(1, runtime.MoveCount);

        clock.Advance(TimeSpan.FromSeconds(7));
        runner.Update();

        Assert.True(runner.IsRunning);
        Assert.Equal("navigating", runner.Status.Phase);
        Assert.Equal(1, runtime.MoveCount);
    }

    [Fact]
    public void OfflinePlacementResolvesFloorOnlyAfterEnteringTerritory()
    {
        var clock = new FakeClock();
        var runtime = new FakeRuntime
        {
            ApplyItemDelta = true,
            ApplyCurrencyDelta = true,
            FloorPosition = new Vector3(10, 7, 20),
        };
        runtime.MissingNpcIds.Add(100);
        var offlineOffer = Offer(10, 100, 1, territoryId: 2) with
        {
            NpcPosition = new Vector3(10, 0, 20),
            RequiresFloorResolution = true,
        };
        var runner = new ShopPurchaseRunner(new FakeCatalog(Resolution(1, [offlineOffer])), runtime, clock);
        Assert.True(runner.Start(new ShopPurchaseRequest(100, 1)));

        runner.Update();
        Assert.Equal(0, runtime.FloorResolveCount);
        runner.Update();

        Assert.Equal(1, runtime.FloorResolveCount);
        Assert.Equal([new Vector3(10, 7, 20)], runtime.MoveDestinations);
        runtime.MissingNpcIds.Remove(100);
        runtime.NpcDistances[100] = 0;
        Drive(runner);
        Assert.True(runner.Status.Succeeded);
    }

    [Fact]
    public void ReachedOfflineStandOffInteractsWhenNpcCenterIsOutsideNormalDistance()
    {
        var clock = new FakeClock();
        var floorPosition = new Vector3(10, 3, 10);
        var runtime = new FakeRuntime
        {
            ApplyItemDelta = true,
            ApplyCurrencyDelta = true,
            FloorPosition = floorPosition,
        };
        runtime.MissingNpcIds.Add(100);
        var offlineOffer = Offer(10, 100, 1) with
        {
            NpcPosition = new Vector3(10, 0, 10),
            RequiresFloorResolution = true,
        };
        var runner = new ShopPurchaseRunner(new FakeCatalog(Resolution(1, [offlineOffer])), runtime, clock);
        Assert.True(runner.Start(new ShopPurchaseRequest(100, 1)));
        DriveUntil(runner, () => runtime.MoveCount == 1);

        runtime.PlayerPosition = floorPosition;
        runtime.MissingNpcIds.Remove(100);
        runtime.NpcPositions[100] = new Vector3(20, 5, 20);
        runtime.NpcDistances[100] = 20;
        Drive(runner);

        Assert.True(runner.Status.Succeeded);
        Assert.Equal([floorPosition], runtime.MoveDestinations);
        Assert.Equal([100u], runtime.InteractedNpcIds);
        Assert.Equal([100u], runtime.SubmittedNpcIds);
    }

    [Fact]
    public void VendorBehindCounterWithinInteractionReachDoesNotStartAnotherPath()
    {
        var runtime = new FakeRuntime { ApplyItemDelta = true, ApplyCurrencyDelta = true };
        runtime.NpcDistances[100] = 4.5f;
        runtime.NpcWithinInteractionReach.Add(100);
        var runner = CreateRunner(1, runtime, new FakeClock());
        Assert.True(runner.Start(new ShopPurchaseRequest(100, 1)));
        Drive(runner);
        Assert.True(runner.Status.Succeeded);
        Assert.Equal(0, runtime.MoveCount);
        Assert.Equal([100u], runtime.InteractedNpcIds);
    }

    [Fact]
    public void ReachingCounterInteractionRangeStopsOwnedPathBeforeInteraction()
    {
        var runtime = new FakeRuntime { ApplyItemDelta = true, ApplyCurrencyDelta = true };
        runtime.NpcDistances[100] = 20;
        var runner = CreateRunner(1, runtime, new FakeClock());
        Assert.True(runner.Start(new ShopPurchaseRequest(100, 1)));
        runner.Update();
        Assert.Equal(1, runtime.MoveCount);
        runtime.NpcDistances[100] = 4.5f;
        runtime.NpcWithinInteractionReach.Add(100);
        Drive(runner);
        Assert.True(runner.Status.Succeeded);
        Assert.Equal(1, runtime.MoveCount);
        Assert.True(runtime.Events.IndexOf("stop-navigation") < runtime.Events.IndexOf("interact:100"));
    }

    [Fact]
    public void FloorResolutionFailureAdvancesBeforeAnyPurchaseCallback()
    {
        var clock = new FakeClock();
        var runtime = new FakeRuntime { ApplyItemDelta = true, ApplyCurrencyDelta = true };
        runtime.MissingNpcIds.Add(100);
        runtime.FloorResults.Enqueue(false);
        var offlineOffer = Offer(10, 100, 1) with { RequiresFloorResolution = true };
        var runner = new ShopPurchaseRunner(
            new FakeCatalog(Resolution(1, [offlineOffer, Offer(11, 101, 1)])),
            runtime,
            clock);
        Assert.True(runner.Start(new ShopPurchaseRequest(100, 1)));

        runner.Update();
        Assert.Equal(0, runtime.SubmitCount);
        clock.Advance(TimeSpan.FromSeconds(21));
        runner.Update();
        Drive(runner);

        Assert.True(runner.Status.Succeeded);
        Assert.Equal([101u], runtime.SubmittedNpcIds);
        Assert.DoesNotContain(100u, runtime.InteractedNpcIds);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AurianaRequiresApproachPointBeforeStoppingAndInteracting(bool npcInitiallyVisible)
    {
        const uint auriana = 1008119;
        var approach = new Vector3(63.283752f, 31.124842f, -735.31555f);
        var clock = new FakeClock();
        var runtime = new FakeRuntime
        {
            CurrentTerritoryId = 156,
            PlayerPosition = approach + new Vector3(1.01f, 0, 0),
            ApplyItemDelta = true,
            ApplyCurrencyDelta = true,
        };
        runtime.NpcPositions[auriana] = approach + new Vector3(8, 0, 0);
        runtime.NpcDistances[auriana] = 0;
        runtime.NpcWithinInteractionReach.Add(auriana);
        if (!npcInitiallyVisible)
            runtime.MissingNpcIds.Add(auriana);
        runtime.NavigationStopResults.Enqueue(ShopNavigationStopResult.StillRunning);
        runtime.NavigationStopResults.Enqueue(ShopNavigationStopResult.Stopped);
        var offer = Offer(10, auriana, 1, territoryId: 156) with { RequiresFloorResolution = true };
        var runner = new ShopPurchaseRunner(new FakeCatalog(Resolution(1, [offer])), runtime, clock);
        Assert.True(runner.Start(new ShopPurchaseRequest(100, 1)));

        runner.Update();
        Assert.Equal([approach], runtime.MoveDestinations);
        Assert.Equal(0, runtime.FloorResolveCount);
        runtime.MissingNpcIds.Remove(auriana);
        runner.Update();
        Assert.Equal("navigating", runner.Status.Phase);
        Assert.Equal([approach], runtime.MoveDestinations);
        Assert.Equal(0, runtime.StopNavigationCount);
        Assert.Empty(runtime.InteractedNpcIds);

        runtime.PlayerPosition = approach + new Vector3(0.99f, 0, 0);
        runner.Update();
        Assert.Equal("stopping-navigation", runner.Status.Phase);
        Assert.Empty(runtime.InteractedNpcIds);
        clock.Advance(TimeSpan.FromSeconds(2));
        Drive(runner);

        Assert.True(runner.Status.Succeeded);
        Assert.Equal([approach], runtime.MoveDestinations);
        Assert.Equal([auriana], runtime.InteractedNpcIds);
        Assert.True(runtime.Events.IndexOf("stop-navigation") < runtime.Events.IndexOf($"interact:{auriana}"));
    }

    [Theory]
    [InlineData(1008119u, 1u)]
    [InlineData(100u, 156u)]
    public void OtherVendorRoutesStillUseLiveNpcPosition(uint npcId, uint territoryId)
    {
        var position = new Vector3(10, 3, 20);
        var runtime = new FakeRuntime { CurrentTerritoryId = territoryId };
        runtime.NpcPositions[npcId] = position;
        runtime.NpcDistances[npcId] = 20;
        var runner = new ShopPurchaseRunner(
            new FakeCatalog(Resolution(1, [Offer(10, npcId, 1, territoryId)])), runtime, new FakeClock());
        Assert.True(runner.Start(new ShopPurchaseRequest(100, 1)));

        runner.Update();
        runner.Update();

        Assert.Equal([position], runtime.MoveDestinations);
        Assert.Equal("navigating", runner.Status.Phase);
        Assert.Empty(runtime.InteractedNpcIds);
    }

    [Fact]
    public void LiveNpcPositionRetargetsAcceptedApproximateNavigationOnce()
    {
        var clock = new FakeClock();
        var runtime = new FakeRuntime
        {
            ApplyItemDelta = true,
            ApplyCurrencyDelta = true,
            FloorPosition = new Vector3(10, 3, 10),
        };
        runtime.MissingNpcIds.Add(100);
        var offlineOffer = Offer(10, 100, 1) with
        {
            NpcPosition = new Vector3(10, 0, 10),
            RequiresFloorResolution = true,
        };
        var runner = new ShopPurchaseRunner(new FakeCatalog(Resolution(1, [offlineOffer])), runtime, clock);
        Assert.True(runner.Start(new ShopPurchaseRequest(100, 1)));
        runner.Update();

        runtime.MissingNpcIds.Remove(100);
        runtime.NpcPositions[100] = new Vector3(20, 5, 20);
        runtime.NpcDistances[100] = 20;
        runner.Update();

        Assert.Equal([new Vector3(10, 3, 10), new Vector3(20, 5, 20)], runtime.MoveDestinations);
        Assert.Equal(1, runtime.StopNavigationCount);
        runtime.NpcDistances[100] = 0;
        Drive(runner);
        Assert.True(runner.Status.Succeeded);
        Assert.Equal(1, runtime.SubmitCount);
    }

    [Fact]
    public void FailedNavigationCommandStopsSendingAfterThreeAttempts()
    {
        var clock = new FakeClock();
        var runtime = new FakeRuntime();
        runtime.NpcDistances[100] = 100;
        runtime.MoveResults.Enqueue(false);
        runtime.MoveResults.Enqueue(false);
        runtime.MoveResults.Enqueue(false);
        var runner = CreateRunner(1, runtime, clock);
        Assert.True(runner.Start(new ShopPurchaseRequest(100, 1)));

        runner.Update();
        for (var index = 0; index < 3 && runner.IsRunning; index++)
        {
            clock.Advance(TimeSpan.FromSeconds(2.1));
            runner.Update();
        }

        Assert.False(runner.IsRunning);
        Assert.Equal(3, runtime.MoveCount);
        Assert.Equal(ShopPurchaseFailureCodes.NoRoute, runner.Status.FailureCode);
        Assert.Equal(0, runtime.StopNavigationCount);
    }

    [Fact]
    public void ShopUiAppearingDuringNavigationStopsOwnedMovementAndFailsClosed()
    {
        var clock = new FakeClock();
        var runtime = new FakeRuntime();
        runtime.NpcDistances[100] = 100;
        var runner = CreateRunner(1, runtime, clock);
        Assert.True(runner.Start(new ShopPurchaseRequest(100, 1)));

        runner.Update();
        runtime.AnyShopVisible = true;
        runner.Update();

        Assert.False(runner.IsRunning);
        Assert.Equal(ShopPurchaseFailureCodes.UiMismatch, runner.Status.FailureCode);
        Assert.Equal(1, runtime.StopNavigationCount);
        Assert.Equal(0, runtime.SubmitCount);
    }

    [Fact]
    public void PreExistingShopUiRejectsStartBeforeTravelOrCallback()
    {
        var clock = new FakeClock();
        var runtime = new FakeRuntime { AnyShopVisible = true };
        var runner = CreateRunner(1, runtime, clock);

        Assert.False(runner.Start(new ShopPurchaseRequest(100, 1)));

        Assert.Contains("existing shop", runner.Status.LastStartError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, runtime.TeleportCount);
        Assert.Equal(0, runtime.MoveCount);
        Assert.Equal(0, runtime.SubmitCount);
    }

    [Fact]
    public void PurchaseCallbackOccursOnlyAfterOwnedNavigationStops()
    {
        var clock = new FakeClock();
        var runtime = new FakeRuntime { ApplyItemDelta = true, ApplyCurrencyDelta = true };
        var runner = CreateRunner(1, runtime, clock);
        Assert.True(runner.Start(new ShopPurchaseRequest(100, 1)));

        Drive(runner);

        Assert.Equal([1], runtime.StopCountsAtSubmit);
        Assert.Equal(1, runtime.StopNavigationCount);
        Assert.True(runner.Status.Succeeded);
    }

    [Fact]
    public void ValidationTimeoutReportsTheUnavailableRuntimeSurface()
    {
        var clock = new FakeClock();
        var runtime = new FakeRuntime
        {
            Validation = ShopUiValidationResult.NotReady("ShopEventHandler.AgentProxy has no active shop handler."),
        };
        var runner = CreateRunner(1, runtime, clock);
        Assert.True(runner.Start(new ShopPurchaseRequest(100, 1)));
        DriveUntil(runner, () => runner.Status.Phase == "validating-ui");

        runner.Update();
        clock.Advance(TimeSpan.FromSeconds(21));
        runner.Update();

        Assert.Equal(ShopPurchaseFailureCodes.UiMismatch, runner.Status.FailureCode);
        Assert.Contains("ShopEventHandler.AgentProxy", runner.Status.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void FallbackCannotChangeOfferAfterPurchaseCallback()
    {
        var clock = new FakeClock();
        var runtime = new FakeRuntime();
        var offers = new[]
        {
            Offer(10, 100, 1),
            Offer(11, 101, 1),
        };
        var runner = new ShopPurchaseRunner(new FakeCatalog(Resolution(1, offers)), runtime, clock);
        Assert.True(runner.Start(new ShopPurchaseRequest(100, 1)));
        DriveUntil(runner, () => runtime.SubmitCount == 1);

        clock.Advance(TimeSpan.FromSeconds(11));
        runner.Update();

        Assert.Equal(ShopPurchaseFailureCodes.Timeout, runner.Status.FailureCode);
        Assert.Equal((uint)100, runner.Status.SelectedOffer?.NpcId);
        Assert.DoesNotContain(101u, runtime.InteractedNpcIds);
    }

    [Fact]
    public void AllInvalidCandidatesReturnUiMismatchWithZeroCallbacks()
    {
        var clock = new FakeClock();
        var runtime = new FakeRuntime();
        runtime.NpcValidations[100] = ShopUiValidationResult.Mismatch("first invalid row");
        runtime.NpcValidations[101] = ShopUiValidationResult.Mismatch("second invalid row");
        var runner = new ShopPurchaseRunner(
            new FakeCatalog(Resolution(1, [Offer(10, 100, 1), Offer(11, 101, 1)])),
            runtime,
            clock);

        Assert.True(runner.Start(new ShopPurchaseRequest(100, 1)));
        Drive(runner);

        Assert.Equal(ShopPurchaseFailureCodes.UiMismatch, runner.Status.FailureCode);
        Assert.Equal(0, runtime.SubmitCount);
        Assert.Contains("All 2 identical-cost candidates", runner.Status.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void RequestedItemChangeBeforeCallbackFailsWithoutFallback()
    {
        var clock = new FakeClock();
        var runtime = new FakeRuntime();
        runtime.NpcDistances[100] = 100;
        var runner = new ShopPurchaseRunner(
            new FakeCatalog(Resolution(1, [Offer(10, 100, 1), Offer(11, 101, 1)])),
            runtime,
            clock);
        Assert.True(runner.Start(new ShopPurchaseRequest(100, 1)));
        runner.Update();

        runtime.ItemCount = 1;
        runner.Update();

        Assert.Equal(ShopPurchaseFailureCodes.UiMismatch, runner.Status.FailureCode);
        Assert.Equal(0, runtime.SubmitCount);
        Assert.DoesNotContain(101u, runtime.InteractedNpcIds);
        Assert.Equal(1, runtime.StopNavigationCount);
    }

    [Fact]
    public void CurrencyChangeBeforeCallbackFailsWithoutFallback()
    {
        var clock = new FakeClock();
        var runtime = new FakeRuntime();
        runtime.NpcDistances[100] = 100;
        var runner = new ShopPurchaseRunner(
            new FakeCatalog(Resolution(1, [Offer(10, 100, 1), Offer(11, 101, 1)])),
            runtime,
            clock);
        Assert.True(runner.Start(new ShopPurchaseRequest(100, 1)));
        runner.Update();

        runtime.AdjustCurrency(new ShopCurrencyIdentity(ShopCurrencyKind.Item, 500), -1);
        runner.Update();

        Assert.Equal(ShopPurchaseFailureCodes.UiMismatch, runner.Status.FailureCode);
        Assert.Equal(0, runtime.SubmitCount);
        Assert.DoesNotContain(101u, runtime.InteractedNpcIds);
    }

    [Fact]
    public void UnexpectedConfirmationFailsImmediatelyWithoutFallback()
    {
        var clock = new FakeClock();
        var runtime = new FakeRuntime();
        runtime.NpcDistances[100] = 100;
        var runner = new ShopPurchaseRunner(
            new FakeCatalog(Resolution(1, [Offer(10, 100, 1), Offer(11, 101, 1)])),
            runtime,
            clock);
        Assert.True(runner.Start(new ShopPurchaseRequest(100, 1)));
        runner.Update();

        runtime.HasUnexpectedConfirmation = true;
        runner.Update();

        Assert.Equal(ShopPurchaseFailureCodes.UiMismatch, runner.Status.FailureCode);
        Assert.Equal(0, runtime.SubmitCount);
        Assert.DoesNotContain(101u, runtime.InteractedNpcIds);
        Assert.Equal(1, runtime.StopNavigationCount);
    }

    [Fact]
    public void OwnedImmediateConfirmationIsAcceptedExactlyOnce()
    {
        var clock = new FakeClock();
        var runtime = new FakeRuntime
        {
            ApplyItemDelta = true,
            ApplyCurrencyDelta = true,
            ShowConfirmationAfterSubmit = true,
            AcceptOwnedConfirmation = true,
        };
        var runner = CreateRunner(1, runtime, clock);
        Assert.True(runner.Start(new ShopPurchaseRequest(100, 1)));

        Drive(runner);

        Assert.True(runner.Status.Succeeded);
        Assert.Equal(1, runtime.AcceptedConfirmationCount);
        Assert.Equal(1, runtime.SubmitCount);
    }

    [Fact]
    public void OwnedConfirmationWaitsForReadablePromptWithoutResendingCallback()
    {
        var clock = new FakeClock();
        var runtime = new FakeRuntime
        {
            ShowConfirmationAfterSubmit = true,
            AcceptOwnedConfirmation = true,
            UnreadableOwnedConfirmationChecks = 1,
        };
        var runner = CreateRunner(1, runtime, clock);
        Assert.True(runner.Start(new ShopPurchaseRequest(100, 1)));
        DriveUntil(runner, () => runtime.SubmitCount == 1);

        runner.Update();
        Assert.True(runner.IsRunning);
        Assert.Equal(0, runtime.AcceptedConfirmationCount);
        Assert.Equal(1, runtime.SubmitCount);

        clock.Advance(TimeSpan.FromSeconds(5));
        runner.Update();
        Assert.Equal(1, runtime.AcceptedConfirmationCount);
        runtime.ItemCount = 1;
        runtime.AdjustCurrency(new ShopCurrencyIdentity(ShopCurrencyKind.Item, 500), -1);
        runner.Update();

        Assert.True(runner.Status.Succeeded);
        Assert.Equal(1, runtime.SubmitCount);
    }

    [Fact]
    public void OwnedConfirmationAtTenSecondsIsAcceptedExactlyOnce()
    {
        var clock = new FakeClock();
        var runtime = new FakeRuntime
        {
            ShowConfirmationAfterSubmit = true,
            AcceptOwnedConfirmation = true,
        };
        var runner = CreateRunner(1, runtime, clock);
        Assert.True(runner.Start(new ShopPurchaseRequest(100, 1)));
        DriveUntil(runner, () => runtime.SubmitCount == 1);

        clock.Advance(TimeSpan.FromSeconds(10));
        runner.Update();

        Assert.Equal(1, runtime.AcceptedConfirmationCount);
        Assert.Equal(1, runtime.SubmitCount);
        runtime.ItemCount = 1;
        runtime.AdjustCurrency(new ShopCurrencyIdentity(ShopCurrencyKind.Item, 500), -1);
        runner.Update();

        Assert.True(runner.Status.Succeeded);
        Assert.Equal(1, runtime.AcceptedConfirmationCount);
        Assert.Equal(1, runtime.SubmitCount);
    }

    [Fact]
    public void UnreadableOwnedConfirmationTimesOutWithoutResendOrFallback()
    {
        var clock = new FakeClock();
        var runtime = new FakeRuntime
        {
            ShowConfirmationAfterSubmit = true,
            AcceptOwnedConfirmation = true,
            UnreadableOwnedConfirmationChecks = int.MaxValue,
        };
        var runner = new ShopPurchaseRunner(
            new FakeCatalog(Resolution(1, [Offer(10, 100, 1), Offer(11, 101, 1)])),
            runtime,
            clock);
        Assert.True(runner.Start(new ShopPurchaseRequest(100, 1)));
        DriveUntil(runner, () => runtime.SubmitCount == 1);

        clock.Advance(TimeSpan.FromSeconds(10).Add(TimeSpan.FromTicks(1)));
        runner.Update();

        Assert.Equal(ShopPurchaseFailureCodes.Timeout, runner.Status.FailureCode);
        Assert.Equal(1, runtime.SubmitCount);
        Assert.Equal(0, runtime.AcceptedConfirmationCount);
        Assert.DoesNotContain(101u, runtime.InteractedNpcIds);
        Assert.Contains("did not resend", runner.Status.FailureMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadableMismatchedOwnedConfirmationFailsWithoutResendOrFallback()
    {
        var clock = new FakeClock();
        var runtime = new FakeRuntime
        {
            ShowConfirmationAfterSubmit = true,
            AcceptOwnedConfirmation = false,
        };
        var runner = new ShopPurchaseRunner(
            new FakeCatalog(Resolution(1, [Offer(10, 100, 1), Offer(11, 101, 1)])),
            runtime,
            clock);
        Assert.True(runner.Start(new ShopPurchaseRequest(100, 1)));
        DriveUntil(runner, () => runtime.SubmitCount == 1);

        runner.Update();

        Assert.Equal(ShopPurchaseFailureCodes.UiMismatch, runner.Status.FailureCode);
        Assert.Equal(1, runtime.SubmitCount);
        Assert.Equal(0, runtime.AcceptedConfirmationCount);
        Assert.DoesNotContain(101u, runtime.InteractedNpcIds);
    }

    [Fact]
    public void MultiOutputPurchaseVerifiesEveryCoproductDelta()
    {
        var clock = new FakeClock();
        var runtime = new FakeRuntime { ApplyItemDelta = true, ApplyCurrencyDelta = true };
        var offer = Offer(10, 100, 1) with
        {
            Outputs =
            [
                new ShopOfferOutput(100, "Fixture Item", 1, 999, false),
                new ShopOfferOutput(200, "Coproduct", 2, 999, false),
            ],
        };
        var runner = new ShopPurchaseRunner(new FakeCatalog(Resolution(1, [offer])), runtime, clock);

        Assert.True(runner.Start(new ShopPurchaseRequest(100, 1)));
        Drive(runner);

        Assert.True(runner.Status.Succeeded);
        Assert.Equal(2, runtime.GetItemCount(200));
        Assert.Equal(1, runtime.SubmitCount);
    }

    [Fact]
    public void RejectedStartOnlyUpdatesLastStartError()
    {
        var clock = new FakeClock();
        var runtime = new FakeRuntime { ApplyItemDelta = true, ApplyCurrencyDelta = true };
        var runner = CreateRunner(1, runtime, clock);
        Assert.True(runner.Start(new ShopPurchaseRequest(100, 1)));
        Drive(runner);
        Assert.True(runner.Status.Succeeded);

        runtime.HasVnavmesh = false;
        Assert.False(runner.Start(new ShopPurchaseRequest(200, 1)));

        Assert.True(runner.Status.Succeeded);
        Assert.Equal((uint)100, runner.Status.ItemId);
        Assert.Equal(1, runner.Status.AcquiredQuantity);
        Assert.Contains("vnavmesh", runner.Status.LastStartError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CheckpointIsSavedBeforeCallbackAndVerifiedOnlyAfterExactAdditionalPurchase()
    {
        var runtime = new FakeRuntime { ApplyItemDelta = true, ApplyCurrencyDelta = true, ItemCount = 7 };
        var runner = CreatePoeticsRunner(runtime, new FakeClock());
        ShopPurchaseCheckpoint? saved = null;
        ShopPurchaseCheckpoint? verified = null;
        Assert.True(runner.Start(new ShopPurchaseRequest(100, 1), false, Poetics,
            checkpoint =>
            {
                Assert.Equal(0, runtime.SubmitCount);
                Assert.True(runner.HasPurchaseSubmission);
                saved = checkpoint;
            },
            checkpoint =>
            {
                Assert.Equal(1, runtime.SubmitCount);
                Assert.Equal(8, runtime.ItemCount);
                verified = checkpoint;
            }));

        Drive(runner);

        Assert.Equal(new ShopPurchaseCheckpoint(100, 1, 7, Poetics, 10_000, 150), saved);
        Assert.Same(saved, verified);
        Assert.True(runner.Status.Succeeded);
        Assert.Equal(1, runtime.SubmitCount);
    }

    [Fact]
    public void CheckpointVerificationWaitsForBothExactDeltas()
    {
        var runtime = new FakeRuntime();
        var runner = CreatePoeticsRunner(runtime, new FakeClock());
        var verified = 0;
        Assert.True(runner.Start(new ShopPurchaseRequest(100, 1), false, Poetics, _ => { }, _ => verified++));
        DriveUntil(runner, () => runtime.SubmitCount == 1);

        runtime.ItemCount = 1;
        runtime.AdjustCurrency(Poetics, -149);
        runner.Update();
        Assert.Equal(0, verified);
        Assert.True(runner.IsRunning);

        runtime.AdjustCurrency(Poetics, -1);
        runner.Update();
        runner.Update();
        Assert.Equal(1, verified);
        Assert.True(runner.Status.Succeeded);
    }

    [Theory]
    [InlineData(2, 150)]
    [InlineData(1, 151)]
    public void ContradictoryCheckpointDeltasNeverReportVerified(int itemDelta, int currencyDelta)
    {
        var runtime = new FakeRuntime();
        var runner = CreatePoeticsRunner(runtime, new FakeClock());
        var verified = 0;
        Assert.True(runner.Start(new ShopPurchaseRequest(100, 1), false, Poetics, _ => { }, _ => verified++));
        DriveUntil(runner, () => runtime.SubmitCount == 1);

        runtime.ItemCount = itemDelta;
        runtime.AdjustCurrency(Poetics, -currencyDelta);
        runner.Update();

        Assert.False(runner.Status.Succeeded);
        Assert.Equal(ShopPurchaseFailureCodes.UiMismatch, runner.Status.FailureCode);
        Assert.Equal(0, verified);
        Assert.True(runner.HasPurchaseSubmission);
    }

    [Fact]
    public void CheckpointPersistenceFailurePreventsPurchaseAndRetainsSubmissionUncertainty()
    {
        var runtime = new FakeRuntime();
        var runner = CreatePoeticsRunner(runtime, new FakeClock());
        var verified = 0;
        Assert.True(runner.Start(new ShopPurchaseRequest(100, 1), false, Poetics,
            _ => throw new IOException("Fixture save failure"), _ => verified++));

        Drive(runner);

        Assert.False(runner.Status.Succeeded);
        Assert.Contains("Fixture save failure", runner.Status.FailureMessage);
        Assert.Equal(0, runtime.SubmitCount);
        Assert.Equal(0, verified);
        Assert.True(runner.HasPurchaseSubmission);
    }

    [Fact]
    public void RejectedCheckpointStartCannotInvokeAnEarlierCompletionHook()
    {
        var runtime = new FakeRuntime { ApplyItemDelta = true, ApplyCurrencyDelta = true };
        var runner = CreatePoeticsRunner(runtime, new FakeClock());
        var earlierVerified = 0;
        Assert.True(runner.Start(new ShopPurchaseRequest(100, 1), false, Poetics, _ => { }, _ => earlierVerified++));
        Drive(runner);
        Assert.Equal(1, earlierVerified);

        runtime.HasVnavmesh = false;
        var newSaved = 0;
        var newVerified = 0;
        Assert.False(runner.Start(new ShopPurchaseRequest(100, 1), false, Poetics, _ => newSaved++, _ => newVerified++));
        runner.Update();

        Assert.Equal(0, newSaved);
        Assert.Equal(0, newVerified);
        Assert.Equal(1, earlierVerified);
        Assert.Equal(1, runtime.SubmitCount);
        Assert.True(runner.Status.Succeeded); // Existing public IPC status still describes the earlier run.
    }

    [Fact]
    public void CheckpointHooksDoNotLeakIntoTheNextPublicPurchase()
    {
        var runtime = new FakeRuntime { ApplyItemDelta = true, ApplyCurrencyDelta = true };
        var runner = CreatePoeticsRunner(runtime, new FakeClock());
        var saved = 0;
        var verified = 0;
        Assert.True(runner.Start(new ShopPurchaseRequest(100, 1), false, Poetics, _ => saved++, _ => verified++));
        Drive(runner);
        Assert.True(runner.HasPurchaseSubmission);

        Assert.True(runner.Start(new ShopPurchaseRequest(100, 1)));
        Assert.False(runner.HasPurchaseSubmission);
        Drive(runner);

        Assert.True(runner.Status.Succeeded);
        Assert.Equal(2, runtime.ItemCount);
        Assert.Equal(2, runtime.SubmitCount);
        Assert.Equal(1, saved);
        Assert.Equal(1, verified);
    }

    [Fact]
    public void CheckpointCancellationAfterSubmissionRetainsPendingPurchaseWithoutVerification()
    {
        var runtime = new FakeRuntime();
        var runner = CreatePoeticsRunner(runtime, new FakeClock());
        var saved = 0;
        var verified = 0;
        Assert.True(runner.Start(new ShopPurchaseRequest(100, 1), false, Poetics, _ => saved++, _ => verified++));
        DriveUntil(runner, () => runtime.SubmitCount == 1);

        runner.Cancel("Fixture stop");
        runtime.ItemCount = 1;
        runtime.AdjustCurrency(Poetics, -150);
        runner.Update();

        Assert.Equal(1, saved);
        Assert.Equal(0, verified);
        Assert.True(runner.HasPurchaseSubmission);
        Assert.Equal(ShopPurchaseFailureCodes.Cancelled, runner.Status.FailureCode);
    }

    [Fact]
    public void CheckpointEntryRejectsLargerQuantityBeforeStarting()
    {
        var runtime = new FakeRuntime();
        var runner = CreatePoeticsRunner(runtime, new FakeClock());
        var hookCalls = 0;

        Assert.False(runner.Start(new ShopPurchaseRequest(100, 2), false, Poetics,
            _ => hookCalls++, _ => hookCalls++));

        Assert.False(runner.IsRunning);
        Assert.False(runner.HasPurchaseSubmission);
        Assert.Equal(ShopPurchaseFailureCodes.InvalidRequest, runner.LastStartFailureCode);
        Assert.Equal(0, runtime.SubmitCount);
        Assert.Equal(0, hookCalls);
    }

    [Fact]
    public void CheckpointEntryNeverFallsBackToAnotherCurrency()
    {
        var runtime = new FakeRuntime();
        var runner = CreateRunner(1, runtime, new FakeClock());
        var hookCalls = 0;
        Assert.True(runner.Start(new ShopPurchaseRequest(100, 1), false, Poetics,
            _ => hookCalls++, _ => hookCalls++));

        Drive(runner);

        Assert.False(runner.Status.Succeeded);
        Assert.False(runner.HasPurchaseSubmission);
        Assert.Equal(0, runtime.SubmitCount);
        Assert.Equal(0, hookCalls);
    }

    [Fact]
    public void OverallTimeoutStopsFutureCallbacks()
    {
        var clock = new FakeClock();
        var runtime = new FakeRuntime();
        var runner = CreateRunner(1, runtime, clock);
        Assert.True(runner.Start(new ShopPurchaseRequest(100, 1)));

        clock.Advance(TimeSpan.FromMinutes(5.1));
        runner.Update();

        Assert.Equal(ShopPurchaseFailureCodes.Timeout, runner.Status.FailureCode);
        Assert.Equal(0, runtime.SubmitCount);
        Assert.Equal(0, runtime.StopNavigationCount);
        Assert.Equal(0, runtime.CloseUiCount);
    }

    private static ShopPurchaseRunner CreateRunner(int quantity, FakeRuntime runtime, FakeClock clock)
        => new(new FakeCatalog(Resolution(quantity, [Offer(10, 100, quantity)])), runtime, clock);

    private static readonly ShopCurrencyIdentity Poetics = new(ShopCurrencyKind.Tomestone, 28);

    private static ShopPurchaseRunner CreatePoeticsRunner(FakeRuntime runtime, FakeClock clock)
        => new(new FakeCatalog(Resolution(1,
        [
            Offer(10, 100, 1) with
            {
                Kind = ShopOfferKind.SpecialShopTomestone,
                Currencies = [new ShopCurrencyCost(ShopCurrencyKind.Tomestone, 28, "Poetics", 150)],
            },
        ])), runtime, clock);

    private static ShopUiValidationResult ValidateGil(
        uint activeShopId = 262_191,
        ShopRuntimeGilItem[]? items = null,
        int[]? visible = null)
    {
        items ??= [new ShopRuntimeGilItem(33_916, 280, false)];
        visible ??= [0];
        return RegularGilShopRuntimeValidator.Validate(
            activeShopId,
            items.Length,
            items,
            visible.Length,
            visible,
            262_191,
            33_916,
            280);
    }

    private static ShopCatalogResolution Resolution(int quantity, IReadOnlyList<ShopOffer> offers)
        => new(100, "Fixture Item", 999, false, quantity, offers, 0, 0, 0);

    private static ShopOffer Offer(uint shopId, uint npcId, int transactions, uint territoryId = 1)
        => new(
            ShopOfferKind.SpecialShopItem,
            shopId,
            $"Shop {shopId}",
            0,
            npcId,
            $"NPC {npcId}",
            territoryId,
            $"Fixture Territory {territoryId}",
            Vector3.Zero,
            [new ShopMenuPathStep(ShopMenuPathStepKind.ENpcData, 0, shopId)],
            ShopNpcLinkKind.DirectShop,
            ShopNpcPlacementSource.Level,
            0,
            1,
            "Level",
            100,
            "Fixture Item",
            1,
            transactions,
            [new ShopCurrencyCost(ShopCurrencyKind.Item, 500, "Fixture Token", 1)],
            [],
            false,
            territoryId == 1
                ? []
                : [new ShopRouteCandidate(50 + territoryId, $"Aetheryte {territoryId}", Vector3.Zero, 10)]);

    private static void Drive(ShopPurchaseRunner runner, int maximumUpdates = 100)
    {
        for (var index = 0; index < maximumUpdates && runner.IsRunning; index++)
            runner.Update();
        Assert.False(runner.IsRunning);
    }

    private static void DriveUntil(ShopPurchaseRunner runner, Func<bool> predicate, int maximumUpdates = 100)
    {
        for (var index = 0; index < maximumUpdates && runner.IsRunning && !predicate(); index++)
            runner.Update();
        Assert.True(predicate());
    }

    private static void DriveWithTime(ShopPurchaseRunner runner, FakeClock clock, int maximumUpdates = 100)
    {
        for (var index = 0; index < maximumUpdates && runner.IsRunning; index++)
        {
            runner.Update();
            clock.Advance(TimeSpan.FromSeconds(2.1));
        }

        Assert.False(runner.IsRunning);
    }

    private sealed class FakeCatalog(ShopCatalogResolution resolution) : IShopCatalog
    {
        public ShopCatalogResolution Resolve(uint itemId, int quantity) => resolution;
    }

    private sealed class FakeClock : IShopPurchaseClock
    {
        public DateTime UtcNow { get; private set; } = new(2026, 7, 14, 12, 0, 0, DateTimeKind.Utc);
        public void Advance(TimeSpan value) => UtcNow += value;
    }

    private sealed class FakeRuntime : IShopPurchaseRuntime
    {
        private readonly Dictionary<ShopCurrencyIdentity, long> currencies = new()
        {
            [new ShopCurrencyIdentity(ShopCurrencyKind.Item, 500)] = 10_000,
            [new ShopCurrencyIdentity(ShopCurrencyKind.Gil, 1)] = 10_000,
            [new ShopCurrencyIdentity(ShopCurrencyKind.Tomestone, 28)] = 10_000,
        };

        public bool IsLoggedIn { get; set; } = true;
        public ulong CharacterId { get; set; } = 1;
        public bool CleanupBlocked { get; set; }
        public string? ShopListCleanupBlocker => CleanupBlocked || IsAnyShopVisible ? "Synthetic shop cleanup is pending" : null;
        public bool IsBetweenAreas { get; set; }
        public bool IsPlayerAvailable { get; set; } = true;
        public uint CurrentTerritoryId { get; set; } = 1;
        public Vector3 PlayerPosition { get; set; } = Vector3.Zero;
        public bool HasVnavmesh { get; set; } = true;
        public bool HasLifestream { get; set; } = true;
        public bool HasUnexpectedConfirmation { get; set; }
        public bool IsSelectionMenuVisible { get; set; }
        public bool IsAnyShopVisible => AnyShopVisible || ExpectedShopVisible;
        public bool AnyShopVisible { get; set; }
        public bool ExpectedShopVisible { get; set; }
        public ShopUiValidationResult Validation { get; set; } = ShopUiValidationResult.Valid(0);
        public bool ApplyItemDelta { get; set; }
        public bool ApplyCurrencyDelta { get; set; }
        public bool ShowConfirmationAfterSubmit { get; set; }
        public bool AcceptOwnedConfirmation { get; set; }
        public int UnreadableOwnedConfirmationChecks { get; set; }
        public int AcceptedConfirmationCount { get; private set; }
        public long ItemCount { get; set; }
        public long Capacity { get; set; } = 100_000;
        public void SetCurrency(ShopCurrencyIdentity currency, long amount) => currencies[currency] = amount;
        private readonly Dictionary<uint, long> additionalItemCounts = [];
        public int SubmitCount { get; private set; }
        public int TeleportCount { get; private set; }
        public int MoveCount { get; private set; }
        public int StopNavigationCount { get; private set; }
        public int CloseUiCount { get; private set; }
        public int MenuSelectCount { get; private set; }
        public int FloorResolveCount { get; private set; }
        public Vector3 FloorPosition { get; set; } = new(1, 0, 1);
        public List<int> SubmittedBatches { get; } = [];
        public List<uint> SubmittedNpcIds { get; } = [];
        public List<uint> TeleportedAetherytes { get; } = [];
        public List<int> StopCountsAtSubmit { get; } = [];
        public List<uint> InteractedNpcIds { get; } = [];
        public List<string> Events { get; } = [];
        public List<Vector3> MoveDestinations { get; } = [];
        public Dictionary<uint, float> NpcDistances { get; } = [];
        public HashSet<uint> NpcWithinInteractionReach { get; } = [];
        public Dictionary<uint, Vector3> NpcPositions { get; } = [];
        public HashSet<uint> MissingNpcIds { get; } = [];
        public Dictionary<uint, bool> InteractionResults { get; } = [];
        public Dictionary<uint, ShopUiValidationResult> NpcValidations { get; } = [];
        public HashSet<uint> SelectionMenuNpcIds { get; } = [];
        public Queue<bool> MoveResults { get; } = [];
        public Queue<bool> TeleportResults { get; } = [];
        public Queue<bool> TeleportArrivalResults { get; } = [];
        public Queue<bool> MenuSelectResults { get; } = [];
        public Queue<bool> FloorResults { get; } = [];
        public Queue<ShopNavigationStopResult> NavigationStopResults { get; } = [];

        public bool IsAetheryteUnlocked(uint aetheryteId) => true;
        public bool IsQuestComplete(uint questId) => true;
        public long GetItemCount(uint itemId)
            => itemId == 100 ? ItemCount : additionalItemCounts.GetValueOrDefault(itemId);
        public long GetAvailableCurrency(ShopCurrencyCost currency)
            => currencies.TryGetValue(currency.Identity, out var value) ? value : 0;
        public long GetInventoryCapacity(uint itemId, uint stackSize) => Capacity;
        public bool TryResolveFloor(Vector3 approximatePosition, out Vector3 floorPosition)
        {
            FloorResolveCount++;
            floorPosition = FloorPosition;
            return FloorResults.Count == 0 || FloorResults.Dequeue();
        }
        public bool TryTeleport(ResolvedShopRoute route)
        {
            TeleportCount++;
            TeleportedAetherytes.Add(route.AetheryteId);
            Events.Add($"teleport:{route.AetheryteId}");
            var accepted = TeleportResults.Count == 0 || TeleportResults.Dequeue();
            if (accepted && (TeleportArrivalResults.Count == 0 || TeleportArrivalResults.Dequeue()))
                CurrentTerritoryId = route.TerritoryId;
            return accepted;
        }

        public bool TryMove(Vector3 destination, string label)
        {
            MoveCount++;
            MoveDestinations.Add(destination);
            Events.Add($"move:{label}");
            return MoveResults.Count == 0 || MoveResults.Dequeue();
        }
        public ShopNavigationStopResult TryStopNavigation()
        {
            StopNavigationCount++;
            Events.Add("stop-navigation");
            return NavigationStopResults.Count == 0
                ? ShopNavigationStopResult.Stopped
                : NavigationStopResults.Dequeue();
        }

        public bool TryGetNpc(uint npcId, out ShopRuntimeNpc npc)
        {
            if (MissingNpcIds.Contains(npcId))
            {
                npc = default;
                return false;
            }
            var distance = NpcDistances.TryGetValue(npcId, out var configured) ? configured : 0;
            var position = NpcPositions.TryGetValue(npcId, out var configuredPosition)
                ? configuredPosition
                : Vector3.Zero;
            npc = new ShopRuntimeNpc(position, distance, NpcWithinInteractionReach.Contains(npcId));
            return true;
        }

        public bool TryInteractNpc(uint npcId)
        {
            InteractedNpcIds.Add(npcId);
            Events.Add($"interact:{npcId}");
            var accepted = !InteractionResults.TryGetValue(npcId, out var configured) || configured;
            if (!accepted)
                return false;
            if (SelectionMenuNpcIds.Contains(npcId))
                IsSelectionMenuVisible = true;
            else
                ExpectedShopVisible = true;
            return true;
        }

        public bool IsExpectedShopVisible(ShopOfferKind kind) => ExpectedShopVisible;
        public bool TrySelectMenu(int index)
        {
            MenuSelectCount++;
            Events.Add($"select-menu:{index}");
            var accepted = MenuSelectResults.Count == 0 || MenuSelectResults.Dequeue();
            if (accepted)
            {
                IsSelectionMenuVisible = false;
                ExpectedShopVisible = true;
            }
            return accepted;
        }

        public ShopUiValidationResult ValidateShopUi(EvaluatedShopOffer offer)
            => NpcValidations.TryGetValue(offer.Offer.NpcId, out var validation) ? validation : Validation;

        public bool SubmitPurchase(EvaluatedShopOffer offer, int runtimeRow, int transactionCount)
        {
            SubmitCount++;
            SubmittedNpcIds.Add(offer.Offer.NpcId);
            SubmittedBatches.Add(transactionCount);
            StopCountsAtSubmit.Add(StopNavigationCount);
            Events.Add($"submit:{offer.Offer.NpcId}");
            if (ApplyItemDelta)
            {
                foreach (var output in offer.Offer.AllOutputs)
                {
                    var delta = (long)output.Count * transactionCount;
                    if (output.ItemId == 100)
                        ItemCount += delta;
                    else
                        additionalItemCounts[output.ItemId] = additionalItemCounts.GetValueOrDefault(output.ItemId) + delta;
                }
            }
            if (ApplyCurrencyDelta)
            {
                foreach (var currency in offer.Offer.Currencies)
                    currencies[currency.Identity] -= (long)currency.AmountPerTransaction * transactionCount;
            }

            if (ShowConfirmationAfterSubmit)
                HasUnexpectedConfirmation = true;
            return true;
        }

        public bool IsOwnedConfirmationPending(EvaluatedShopOffer offer, int transactionCount)
        {
            if (!HasUnexpectedConfirmation || UnreadableOwnedConfirmationChecks <= 0)
                return false;
            UnreadableOwnedConfirmationChecks--;
            return true;
        }

        public bool TryAcceptOwnedConfirmation(EvaluatedShopOffer offer, int transactionCount)
        {
            if (!AcceptOwnedConfirmation || !HasUnexpectedConfirmation)
                return false;
            HasUnexpectedConfirmation = false;
            AcceptedConfirmationCount++;
            return true;
        }

        public void CloseOwnedShopUi()
        {
            CloseUiCount++;
            ExpectedShopVisible = false;
            IsSelectionMenuVisible = false;
            Events.Add("close-ui");
        }

        public void AdjustCurrency(ShopCurrencyIdentity identity, long delta)
            => currencies[identity] = currencies.GetValueOrDefault(identity) + delta;
    }
}
