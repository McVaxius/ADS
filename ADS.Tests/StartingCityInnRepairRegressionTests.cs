using System.Numerics;
using Dalamud.Bindings.ImGui;
using System.Buffers.Binary;
using System.Collections.Frozen;
using System.Reflection;
using System.Runtime.CompilerServices;
using Dalamud.Game.ClientState.Aetherytes;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using ADS.Services;
using ADS.Models;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using Xunit;

namespace ADS.Tests;

[Collection("ADS configuration IPC")]
public sealed class StartingCityInnRepairRegressionTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public unsafe void NativeGearSaleOwnershipAndCleanupDoNotDependOnTheVendorTab(int mode, bool updatingBuyback)
    {
        var native = new ShopEventHandler
        {
            CurrentMode = mode, BuybackTabActive = mode == 2, UpdatingBuybackItems = updatingBuyback,
        };
        native.Info.EventId.Id = 0x40001;
        Assert.Equal(GearSaleState.Ready, DalamudShopPurchaseRuntime.ReadGearSaleHandlerState(&native,
            10, 0x40001, 50, 50, 10, out var diagnostic));
        Assert.Empty(diagnostic);
        Assert.True(DalamudShopPurchaseRuntime.IsGearSaleTransactionIdle(&native));
        native.WaitingForTransactionToFinish = true;
        Assert.False(DalamudShopPurchaseRuntime.IsGearSaleTransactionIdle(&native));
        native.WaitingForTransactionToFinish = false;
        native.WaitingForSellConfirm = true;
        Assert.False(DalamudShopPurchaseRuntime.IsGearSaleTransactionIdle(&native));
        native.WaitingForSellConfirm = false;
        native.StartingSell = true;
        Assert.False(DalamudShopPurchaseRuntime.IsGearSaleTransactionIdle(&native));
        native.StartingSell = false;
        native.StartingBuy = true;
        Assert.False(DalamudShopPurchaseRuntime.IsGearSaleTransactionIdle(&native));
        native.StartingBuy = false;
        Assert.Equal(GearSaleState.Unsupported, DalamudShopPurchaseRuntime.ReadGearSaleHandlerState(&native,
            10, 0x40001, 50, 51, 10, out _));
        Assert.Equal(GearSaleState.Unsupported, DalamudShopPurchaseRuntime.ReadGearSaleHandlerState(&native,
            10, 0x40001, 50, 50, 11, out _));
        Assert.Equal(GearSaleState.Unsupported, DalamudShopPurchaseRuntime.ReadGearSaleHandlerState(&native,
            10, 0x40002, 50, 50, 10, out _));
        native.IsTradingWithRetainer = true;
        Assert.Equal(GearSaleState.Unsupported, DalamudShopPurchaseRuntime.ReadGearSaleHandlerState(&native,
            10, 0x40001, 50, 50, 10, out _));
    }

    [Fact]
    public void GearSalePartialFailureClosesSettledBuybackAndReportsVerifiedCounts()
    {
        using var fixture = new NpcSaleFixture { NativeGearLifecycle = true };
        fixture.GearItems.AddRange([GearItem(1681, InventoryType.Inventory1, 24), GearItem(1682, InventoryType.Inventory2, 25)]);
        Assert.True(fixture.Service.StartGearSale("partial-native-flow", GearCleanupScope.Inventory, true));
        for (var tick = 0; tick < 12 && fixture.GearSold.Count == 0; tick++) fixture.Service.Update();
        Assert.Single(fixture.GearSold);
        fixture.BeginNativeGearSettlement(false);
        fixture.PublishNativeGearItemRemoval();
        fixture.PublishNativeGearGil();
        fixture.FinishNativeGearSettlement();
        fixture.Service.Update();
        Assert.Equal(1, fixture.Service.GearSaleStatus.SoldCount);
        Assert.Equal(2, fixture.NativeGearMode);
        fixture.GearProtection.Add(1682); // A real protection change blocks the next item.
        fixture.Service.Update();
        var receipt = fixture.Service.GearSaleStatus;
        Assert.False(fixture.Service.IsRunning);
        Assert.False(receipt.Succeeded);
        Assert.Equal(1, receipt.SoldCount);
        Assert.Equal(2, receipt.RequestedCount);
        Assert.Equal(4, receipt.GilEarned);
        Assert.Contains("Sold 1/2; 1 remaining.", receipt.StatusMessage);
        Assert.Equal(receipt.StatusMessage, fixture.Service.StatusMessage);
        Assert.True(receipt.Settled);
        Assert.False(fixture.GearShopOpen);
        Assert.Single(fixture.GearSold);
        Assert.Single(fixture.GearItems);
    }

    [Fact]
    public void GearSaleCancellationPreservesAnUnresolvedNativeSaleOnBuyback()
    {
        using var fixture = new NpcSaleFixture { NativeGearLifecycle = true };
        fixture.GearItems.AddRange([GearItem(1681, InventoryType.Inventory1, 24), GearItem(1682, InventoryType.Inventory2, 25)]);
        Assert.True(fixture.Service.StartGearSale("cancel-native-flow", GearCleanupScope.Inventory, true));
        for (var tick = 0; tick < 12 && fixture.GearSold.Count == 0; tick++) fixture.Service.Update();
        Assert.Single(fixture.GearSold);
        fixture.BeginNativeGearSettlement(false);
        fixture.PublishNativeGearItemRemoval();
        Assert.Equal(2, fixture.NativeGearMode);
        Assert.True(fixture.Service.CancelGearSale("cancel-native-flow"));
        var receipt = fixture.Service.GearSaleStatus;
        Assert.False(receipt.Succeeded);
        Assert.Equal(0, receipt.SoldCount); // Item removal alone does not prove settlement.
        Assert.Contains("Sold 0/2; 2 remaining.", receipt.StatusMessage);
        Assert.False(receipt.Settled);
        Assert.True(fixture.GearShopOpen);
        Assert.Single(fixture.GearSold);
    }

    [Fact]
    public unsafe void MovedGearComparisonIgnoresNativeObjectBytesButPreservesItemState()
    {
        var expected = GearItem(1681, InventoryType.ArmoryMainHand, 7).Item;
        expected.Flags = InventoryItem.ItemFlags.HighQuality;
        expected.Condition = 9000;
        expected.SpiritbondOrCollectability = 500;
        expected.CrafterContentId = 123;
        expected.Materia[0] = 456;
        expected.MateriaGrades[0] = 7;
        expected.Stains[0] = 8;
        expected.Stains[1] = 9;
        expected.GlamourId = 789;
        expected.EventId.Id = 12;
        var moved = expected;
        moved.Container = InventoryType.Inventory3;
        moved.Slot = 19;
        ((byte*)&moved)[0] ^= 0x40; // Native object/vtable bytes are not item identity.
        ((byte*)&moved)[0x0F] ^= 0x20; // Padding between IsSymbolic and ItemId.
        Assert.True(GearCleanupPolicy.Matches(moved, expected, allowMovedSlot: true));
        Assert.False(GearCleanupPolicy.Matches(moved, expected)); // Existing source-slot contract is unchanged.
        for (var field = 0; field < 13; field++)
        {
            var different = moved;
            switch (field)
            {
                case 0: different.ItemId++; break;
                case 1: different.Quantity++; break;
                case 2: different.Flags = InventoryItem.ItemFlags.None; break;
                case 3: different.Condition++; break;
                case 4: different.SpiritbondOrCollectability++; break;
                case 5: different.CrafterContentId++; break;
                case 6: different.Materia[0]++; break;
                case 7: different.MateriaGrades[0]++; break;
                case 8: different.Stains[0]++; break;
                case 9: different.Stains[1]++; break;
                case 10: different.GlamourId++; break;
                case 11: different.EventId.Id++; break;
                case 12: different.IsSymbolic = true; break;
            }
            Assert.False(GearCleanupPolicy.Matches(different, expected, allowMovedSlot: true), $"Changed item field {field} must be rejected.");
        }
    }

    public static IEnumerable<object[]> NativeGearMoveBatchCases()
    {
        foreach (var allUnprotected in new[] { false, true })
        foreach (var receiptOrder in new[] { 0, 1, 2 })
        foreach (var bag in GearCleanupPolicy.Bags)
            yield return new object[] { allUnprotected, receiptOrder, bag };
    }

    [Theory]
    [MemberData(nameof(NativeGearMoveBatchCases))]
    public void GearMoveContinuesAcrossNativeSlotUpdateOrders(bool allUnprotected, int receiptOrder, InventoryType bag)
    {
        using var fixture = new NpcSaleFixture
        {
            GearCompleteOperations = false, GearBagContainer = bag, GearMoveChangesNativeBytes = true,
        };
        var selected = GearCleanupPolicy.Containers(GearCleanupScope.Armoury, allUnprotected)
            .Select((container, index) => GearItem((uint)(2000 + index), container, (ushort)index)).ToList();
        var colored = GearItem(9001, InventoryType.ArmoryBody, 31) with { WhiteEquipment = false };
        if (allUnprotected) selected.Add(colored);
        fixture.GearItems.AddRange(selected);
        if (!allUnprotected) fixture.GearItems.Add(colored);
        fixture.GearItems.Add(GearItem(9000, InventoryType.ArmoryHead, 30));
        fixture.GearProtection.Add(9000);
        Assert.True(fixture.Service.StartGearMove(allUnprotected), fixture.Service.StatusMessage);
        for (var index = 0; index < selected.Count; index++)
        {
            fixture.Service.Update();
            Assert.True(fixture.Service.IsRunning, fixture.Service.StatusMessage);
            Assert.Equal(index + 1, fixture.GearMoved.Count);
            Assert.Equal(selected[index], fixture.GearMoved[index]);
            if (receiptOrder != 2)
            {
                if (receiptOrder == 0) fixture.PublishGearMoveSourceRemoval();
                else fixture.PublishGearMoveDestination();
                for (var tick = 0; tick < 3; tick++) fixture.Service.Update();
                Assert.True(fixture.Service.IsRunning, fixture.Service.StatusMessage);
                Assert.Equal(index + 1, fixture.GearMoved.Count); // Never replay or submit the next item early.
            }
            if (receiptOrder != 0) fixture.PublishGearMoveSourceRemoval();
            if (receiptOrder != 1) fixture.PublishGearMoveDestination();
            fixture.Service.Update();
            Assert.True(fixture.Service.IsRunning, fixture.Service.StatusMessage);
            Assert.Contains($"{index + 1}/{selected.Count}", fixture.Service.StatusMessage);
        }
        fixture.Service.Update();
        Assert.False(fixture.Service.IsRunning);
        Assert.Contains($"{selected.Count} items processed", fixture.Service.LastSuccessMessage);
        Assert.Equal(selected.Count, fixture.GearMoved.Count);
        Assert.Equal(selected.Count, fixture.GearItems.Count(item => item.Container == bag));
        Assert.Contains(fixture.GearItems, item => item.Item.ItemId == 9000 && item.Container == InventoryType.ArmoryHead);
        if (!allUnprotected) Assert.Contains(fixture.GearItems, item => item == colored);
        Assert.Empty(fixture.GearSold);
        Assert.Empty(fixture.Commands);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GearMoveWaitsForDestinationThenStopsWithoutReplayingAnUnverifiedMove(bool differentItem)
    {
        using var fixture = new NpcSaleFixture { GearCompleteOperations = false };
        fixture.GearItems.AddRange([GearItem(1681, InventoryType.ArmoryHead), GearItem(1682, InventoryType.ArmoryBody)]);
        Assert.True(fixture.Service.StartGearMove());
        fixture.Service.Update();
        fixture.PublishGearMoveSourceRemoval();
        if (differentItem)
        {
            fixture.PublishGearMoveDestination();
            var row = fixture.GearItems.Single(item => item.Container == InventoryType.Inventory1);
            var changed = row.Item;
            changed.Flags = InventoryItem.ItemFlags.HighQuality;
            fixture.GearItems[fixture.GearItems.IndexOf(row)] = row with { Item = changed };
        }
        fixture.Service.Update();
        Assert.True(fixture.Service.IsRunning, fixture.Service.StatusMessage);
        Assert.Single(fixture.GearMoved);
        Set(fixture.Service, "lastActionUtc", DateTime.UtcNow.AddSeconds(-2));
        fixture.Service.Update();
        Assert.False(fixture.Service.IsRunning);
        Assert.Contains("could not verify its exact destination item", fixture.Service.LastFailureMessage);
        Assert.Single(fixture.GearMoved);
        Assert.Contains(fixture.GearItems, item => item.Item.ItemId == 1682 && item.Container == InventoryType.ArmoryBody);
    }

    public static IEnumerable<object[]> NativeGearSaleBatchCases()
    {
        foreach (var scope in Enum.GetValues<GearCleanupScope>())
        foreach (var confirmation in new[] { false, true })
        foreach (var receiptOrder in new[] { 0, 1, 2 })
            yield return new object[] { scope, confirmation, receiptOrder };
    }

    [Theory]
    [MemberData(nameof(NativeGearSaleBatchCases))]
    public void GearSaleProcessesAllKnownContainersThroughTheFullDelayedNativeLifecycle(GearCleanupScope scope, bool confirmation, int receiptOrder)
    {
        using var fixture = new NpcSaleFixture { NativeGearLifecycle = true };
        var containers = GearCleanupPolicy.Containers(scope).ToArray();
        var selected = containers.Select((container, index) => GearItem((uint)(2000 + index), container, (ushort)index)).ToArray();
        fixture.GearItems.AddRange(selected);
        fixture.GearItems.Add(GearItem(9000, containers[0], 30));
        fixture.GearProtection.Add(9000);
        fixture.GearItems.Add(GearItem(9001, containers[0], 31) with { WhiteEquipment = false });
        fixture.GearItems.Add(GearItem(9002, containers[0], 32) with { NpcBuyable = false });
        Assert.True(fixture.Service.StartGearSale("complete-native-flow", scope, true), fixture.Service.StatusMessage);
        long earned = 0;
        for (var index = 0; index < selected.Length; index++)
        {
            for (var tick = 0; tick < 12 && fixture.GearSold.Count <= index && fixture.Service.IsRunning; tick++) fixture.Service.Update();
            Assert.True(fixture.Service.IsRunning, fixture.Service.StatusMessage);
            Assert.Equal(index + 1, fixture.GearSold.Count);
            Assert.Equal(selected[index], fixture.GearSold[index]);
            for (var tick = 0; tick < 3; tick++) fixture.Service.Update(); // Native Item-sheet read still pending.
            Assert.Equal(-1, fixture.GearQuotedPrice);
            Assert.Equal(index, fixture.Service.GearSaleStatus.SoldCount);
            Assert.Equal(index + 1, fixture.GearSold.Count); // Never resubmit while native work is pending.
            fixture.BeginNativeGearSettlement(confirmation);
            if (confirmation || receiptOrder != 2)
            {
                Set(fixture.Service, "lastActionUtc", DateTime.UtcNow.AddSeconds(-2));
                fixture.Service.Update();
                Assert.True(fixture.Service.IsRunning, fixture.Service.StatusMessage);
                Assert.Equal(4 + index, fixture.GearQuotedPrice);
            }
            if (receiptOrder == 2)
            {
                fixture.PublishNativeGearItemRemoval();
                fixture.PublishNativeGearGil();
                fixture.FinishNativeGearSettlement(); // Entire completion can happen between observations.
            }
            else
            {
                fixture.PublishNativeGearItemRemoval();
                fixture.Service.Update();
                Assert.Equal(index, fixture.Service.GearSaleStatus.SoldCount);
                if (receiptOrder == 1)
                {
                    fixture.FinishNativeGearSettlement(); // Acknowledgement may precede the gil update.
                    fixture.Service.Update();
                    Assert.True(fixture.Service.IsRunning, fixture.Service.StatusMessage);
                    Assert.Equal(index, fixture.Service.GearSaleStatus.SoldCount);
                }
                fixture.PublishNativeGearGil();
                if (receiptOrder == 0)
                {
                    fixture.Service.Update();
                    Assert.Equal(index, fixture.Service.GearSaleStatus.SoldCount);
                    fixture.FinishNativeGearSettlement();
                }
            }
            fixture.Service.Update();
            earned += 4 + index;
            Assert.Equal(2, fixture.NativeGearMode); // Buyback stays selected for all following sales and cleanup.
            Assert.Equal(index + 1, fixture.Service.GearSaleStatus.SoldCount);
            Assert.Equal(earned, fixture.Service.GearSaleStatus.GilEarned);
        }
        for (var tick = 0; tick < 4 && fixture.Service.IsRunning; tick++) fixture.Service.Update();
        var receipt = fixture.Service.GearSaleStatus;
        Assert.False(fixture.Service.IsRunning);
        Assert.True(receipt.Succeeded, receipt.StatusMessage);
        Assert.True(receipt.Settled);
        Assert.Equal(selected.Length, receipt.RequestedCount);
        Assert.Equal(selected.Length, fixture.GearSold.Count);
        Assert.Equal(confirmation ? selected.Length : 0, fixture.GearConfirmations);
        Assert.False(fixture.GearShopOpen);
        Assert.Equal(new uint[] { 9000, 9001, 9002 }, fixture.GearItems.Select(item => item.Item.ItemId));
    }

    [Fact]
    public unsafe void NativeGearSaleAcceptsClearedTypeOnlyWhileSettlingItsExactSubmittedSlot()
    {
        // Replay the actual FTP6 -04 failure snapshot: all item identity fields
        // match, but TransactionType is already clear while completion is pending.
        var item = GearItem(1681, InventoryType.Inventory1, slot: 24);
        var native = new ShopEventHandler
        {
            TransactionType = 0, TransactionItemId = 1681, TransactionItemCount = 1,
            SellInventoryType = InventoryType.Inventory1, SellInventorySlot = 24,
            SellPrice = 4, WaitingForTransactionToFinish = true,
        };
        Assert.Equal(GearSaleState.Pending, DalamudShopPurchaseRuntime.ReadGearSaleTransactionPhase(&native, item, out _));
        Assert.Equal(GearSaleState.Unsupported, DalamudShopPurchaseRuntime.ReadGearSaleTransactionPhase(&native, null, out _));
        native.WaitingForSellConfirm = true;
        Assert.Equal(GearSaleState.Unsupported, DalamudShopPurchaseRuntime.ReadGearSaleTransactionPhase(&native, item, out _));
        native.WaitingForSellConfirm = false;
        native.SellInventorySlot++;
        Assert.Equal(GearSaleState.Unsupported, DalamudShopPurchaseRuntime.ReadGearSaleTransactionPhase(&native, item, out _));
        native.SellInventorySlot--;
        native.SellInventoryType = InventoryType.Inventory2;
        Assert.Equal(GearSaleState.Unsupported, DalamudShopPurchaseRuntime.ReadGearSaleTransactionPhase(&native, item, out _));
        native.SellInventoryType = InventoryType.Inventory1;
        native.TransactionItemId++;
        Assert.Equal(GearSaleState.Unsupported, DalamudShopPurchaseRuntime.ReadGearSaleTransactionPhase(&native, item, out _));
        native.TransactionItemId--;
        native.TransactionItemCount = 2;
        Assert.Equal(GearSaleState.Unsupported, DalamudShopPurchaseRuntime.ReadGearSaleTransactionPhase(&native, item, out _));
        native.TransactionItemCount = 1;
        native.TransactionType = 1;
        Assert.Equal(GearSaleState.Unsupported, DalamudShopPurchaseRuntime.ReadGearSaleTransactionPhase(&native, item, out _));
    }

    [Fact]
    public unsafe void NativeGearSaleWaitsForItemSheetReadBeforeRequiringCommittedTransactionFields()
    {
        var item = GearItem(51, InventoryType.Inventory2, slot: 7);
        var native = new ShopEventHandler { StartingSell = true };
        // This is the actual SDK struct and classifier used by the native runtime,
        // including zero/stale fields while its Item-sheet request is pending.
        Assert.Equal(GearSaleState.Pending, DalamudShopPurchaseRuntime.ReadGearSaleTransactionPhase(&native, item, out _));
        Assert.Equal(GearSaleState.Unsupported, DalamudShopPurchaseRuntime.ReadGearSaleTransactionPhase(&native, null, out _));
        native.StartingBuy = true;
        Assert.Equal(GearSaleState.Unsupported, DalamudShopPurchaseRuntime.ReadGearSaleTransactionPhase(&native, item, out _));
        native.StartingBuy = false;
        native.StartingSell = false;
        native.WaitingForSellConfirm = true;
        Assert.Equal(GearSaleState.Unsupported, DalamudShopPurchaseRuntime.ReadGearSaleTransactionPhase(&native, item, out _));
        native.TransactionType = 2;
        native.TransactionItemId = item.Item.ItemId;
        native.TransactionItemCount = 1;
        native.SellInventoryType = item.Container;
        native.SellInventorySlot = item.Slot;
        Assert.Equal(GearSaleState.Pending, DalamudShopPurchaseRuntime.ReadGearSaleTransactionPhase(&native, item, out _));
        native.WaitingForSellConfirm = false;
        native.WaitingForTransactionToFinish = true;
        Assert.Equal(GearSaleState.Pending, DalamudShopPurchaseRuntime.ReadGearSaleTransactionPhase(&native, item, out _));
        native.SellInventorySlot++;
        Assert.Equal(GearSaleState.Unsupported, DalamudShopPurchaseRuntime.ReadGearSaleTransactionPhase(&native, item, out _));
        native.SellInventorySlot--;
        native.TransactionItemCount = 0;
        Assert.Equal(GearSaleState.Unsupported, DalamudShopPurchaseRuntime.ReadGearSaleTransactionPhase(&native, item, out _));
        native.WaitingForTransactionToFinish = false;
        Assert.Equal(GearSaleState.Ready, DalamudShopPurchaseRuntime.ReadGearSaleTransactionPhase(&native, item, out _));
    }

    [Theory]
    [InlineData(false, 1f)]
    [InlineData(true, 1f)]
    [InlineData(false, 1.5f)]
    [InlineData(true, 1.5f)]
    public unsafe void ActualVermaxionInlineHelpKeepsFollowingSettingsAtTheirNativeRowOrigin(bool compact, float scale)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        string? path = null;
        while (directory != null)
        {
            var candidate = Path.Combine(directory.FullName, "VERMAXION", "VERMAXION", "bin", "x64", "Debug", "VERMAXION.dll");
            if (File.Exists(candidate)) { path = candidate; break; }
            directory = directory.Parent;
        }
        Assert.NotNull(path);
        var assembly = System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyPath(path!);
        var wrapped = assembly.GetType("VERMAXION.UIConstants", true)!
            .GetMethod("WrappedText", BindingFlags.Static | BindingFlags.NonPublic)!
            .CreateDelegate<Action<Vector4, string, bool>>();
        var previous = ImGui.GetCurrentContext();
        var context = ImGui.CreateContext();
        try
        {
            var io = ImGui.GetIO();
            io.IniFilename = null;
            io.LogFilename = null;
            io.DisplaySize = new(1200, 900);
            io.DeltaTime = 1f / 60;
            io.Fonts.AddFontDefault();
            byte* pixels = null;
            int width = 0, height = 0;
            io.Fonts.GetTexDataAsRGBA32(0, &pixels, &width, &height);
            io.Fonts.SetTexID(0, new ImTextureID((nint)1));
            io.FontGlobalScale = scale;
            var style = ImGui.GetStyle();
            style.WindowPadding = new Vector2(compact ? 8 : 16) * scale;
            style.ItemSpacing = new Vector2(compact ? 6 : 10, compact ? 3 : 8) * scale;
            for (var frame = 0; frame < 2; frame++)
            {
                ImGui.NewFrame();
                ImGui.SetNextWindowSize(new(1000, 700));
                ImGui.Begin("Actual VMX settings origin", ImGuiWindowFlags.NoSavedSettings);
                try
                {
                    var visible = ImGui.BeginChild("Settings subframe", new(800, 550));
                    try
                    {
                        Assert.True(visible);
                        ImGui.Indent(18 * scale);
                        var origin = ImGui.GetCursorPosX();
                        for (var row = 0; row < 5; row++)
                        {
                            ImGui.TextUnformatted("Setting " + row);
                            ImGui.SameLine();
                            ImGui.SetNextItemWidth(100 * scale);
                            var value = row;
                            ImGui.InputInt("##value" + row, ref value);
                            ImGui.SameLine();
                            wrapped(Vector4.One, "(?)", false);
                            Assert.InRange(ImGui.GetCursorPosX(), origin - .01f, origin + .01f);
                        }
                        ImGui.Unindent(18 * scale);
                    }
                    finally { ImGui.EndChild(); }
                }
                finally { ImGui.End(); }
                ImGui.Render();
            }
        }
        finally
        {
            ImGui.DestroyContext(context);
            ImGui.SetCurrentContext(previous);
        }
    }

    [Fact]
    public void GearSaleWaitsForNativeReadinessWithoutSubmittingTwice()
    {
        using var fixture = new NpcSaleFixture();
        fixture.GearItems.Add(GearItem(51, InventoryType.Inventory1));
        Assert.True(fixture.Service.StartGearSale("readiness", GearCleanupScope.Inventory, true));
        fixture.Service.Update();
        fixture.GearState = GearSaleState.NotReady;
        for (var tick = 0; tick < 4; tick++) fixture.Service.Update();
        Assert.True(fixture.Service.IsRunning);
        Assert.Empty(fixture.GearSold);
        fixture.GearState = GearSaleState.Ready;
        for (var tick = 0; tick < 4 && fixture.Service.IsRunning; tick++) fixture.Service.Update();
        Assert.Single(fixture.GearSold);
        var receipt = fixture.Service.GearSaleStatus;
        Assert.Equal("readiness", receipt.OperationId);
        Assert.True(receipt.Succeeded);
        Assert.Equal(1, receipt.SoldCount);
        Assert.Equal(10, receipt.GilEarned);
        Assert.False(fixture.Service.StartGearSale("readiness", GearCleanupScope.Inventory, true));
        Assert.Single(fixture.GearSold);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GearSaleOnlyConfirmsItsExactOwnedTransaction(bool owned)
    {
        using var fixture = new NpcSaleFixture { GearCompleteOperations = false, OwnsGearConfirmation = owned };
        fixture.GearItems.Add(GearItem(52, InventoryType.Inventory1));
        Assert.True(fixture.Service.StartGearSale("confirmation", GearCleanupScope.Inventory, true));
        fixture.Service.Update();
        fixture.Service.Update();
        fixture.GearState = GearSaleState.Confirmation;
        Set(fixture.Service, "lastActionUtc", DateTime.UtcNow.AddSeconds(-2));
        for (var tick = 0; tick < 4 && fixture.Service.IsRunning; tick++) fixture.Service.Update();
        Assert.Equal(owned ? 1 : 0, fixture.GearConfirmations);
        Assert.Single(fixture.GearSold);
        Assert.Equal(owned, fixture.Service.GearSaleStatus.Succeeded);
        Assert.Equal(owned ? 1 : 0, fixture.Service.GearSaleStatus.SoldCount);
        Assert.Empty(fixture.Commands);
    }

    [Fact]
    public void GearSaleRejectsAnUnverifiedGilResultBeforeAdvancing()
    {
        using var fixture = new NpcSaleFixture();
        fixture.GearItems.AddRange([GearItem(53, InventoryType.Inventory1), GearItem(54, InventoryType.Inventory2)]);
        Assert.True(fixture.Service.StartGearSale("gil-check", GearCleanupScope.Both, true));
        fixture.Service.Update();
        fixture.Service.Update();
        fixture.GearGil++;
        fixture.Service.Update();
        Assert.True(fixture.Service.IsRunning); // Allow the normal, bounded receipt-settlement window.
        Assert.Single(fixture.GearSold);
        Set(fixture.Service, "gearCleanupSettlementReadyUtc", DateTime.UtcNow.AddSeconds(-2));
        fixture.Service.Update();
        Assert.False(fixture.Service.IsRunning);
        Assert.Single(fixture.GearSold);
        Assert.False(fixture.Service.GearSaleStatus.Succeeded);
        Assert.Contains("gil result", fixture.Service.LastFailureMessage);
        Assert.Contains(fixture.GearItems, item => item.Item.ItemId == 54);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MoveAllIncludesColouredEquipmentWhileProtectingSavedGearsets(bool allUnprotected)
    {
        using var fixture = new NpcSaleFixture();
        fixture.GearItems.AddRange([
            GearItem(55, InventoryType.ArmoryHead) with { WhiteEquipment = false },
            GearItem(56, InventoryType.ArmoryBody) with { WhiteEquipment = false },
            GearItem(57, InventoryType.ArmoryHands),
            GearItem(60, InventoryType.ArmorySoulCrystal),
        ]);
        fixture.GearProtection.Add(56);
        Assert.True(fixture.Service.StartGearMove(allUnprotected));
        for (var tick = 0; tick < 7 && fixture.Service.IsRunning; tick++) fixture.Service.Update();
        Assert.Equal(allUnprotected ? new uint[] { 55, 57, 60 } : new uint[] { 57 }, fixture.GearMoved.Select(item => item.Item.ItemId));
        Assert.Contains(fixture.GearItems, item => item.Item.ItemId == 56 && item.Container == InventoryType.ArmoryBody);
        Assert.Empty(fixture.GearSold);
    }

    [Fact]
    public void EquipmentSaleCancellationMatchesOnlyTheCallingOperation()
    {
        using var fixture = new NpcSaleFixture();
        fixture.GearItems.Add(GearItem(58, InventoryType.Inventory1));
        Assert.True(fixture.Service.StartGearSale("owner", GearCleanupScope.Inventory, true));
        Assert.False(fixture.Service.StartGearSale("other", GearCleanupScope.Inventory, true));
        Assert.Equal("owner", fixture.Service.GearSaleStatus.OperationId);
        Assert.False(fixture.Service.CancelGearSale("other"));
        Assert.True(fixture.Service.IsRunning);
        Assert.True(fixture.Service.CancelGearSale("owner"));
        Assert.False(fixture.Service.GearSaleStatus.Succeeded);
        Assert.True(fixture.Service.GearSaleStatus.Done);
        Assert.Empty(fixture.GearSold);
        Assert.False(fixture.Service.CancelGearSale("owner"));
    }

    [Fact]
    public void ReadOnlyGearPreviewDoesNotReplaceAnotherUtilityStatus()
    {
        using var fixture = new NpcSaleFixture();
        fixture.GearItems.Add(GearItem(59, InventoryType.ArmoryHead));
        Assert.True(fixture.Service.StartGearMove());
        var original = fixture.Service.StatusMessage;
        Assert.False(fixture.Service.TryPreviewGearSale(GearCleanupScope.Both, out _, out var error));
        Assert.Contains("ownership", error);
        Assert.Equal(original, fixture.Service.StatusMessage);
        Assert.True(fixture.Service.IsRunning);
        Assert.Empty(fixture.GearSold);
    }

    [Theory]
    [InlineData(GearCleanupScope.Armoury, 1)]
    [InlineData(GearCleanupScope.Inventory, 2)]
    [InlineData(GearCleanupScope.Both, 3)]
    public void GearSaleScopesExcludeColoredProtectedHqAndUnbuyableEquipment(GearCleanupScope scope, int expected)
    {
        using var fixture = new NpcSaleFixture();
        fixture.GearItems.AddRange([
            GearItem(1, InventoryType.ArmoryHead), GearItem(2, InventoryType.Inventory1), GearItem(3, InventoryType.Inventory2),
            GearItem(4, InventoryType.ArmoryBody) with { WhiteEquipment = false },
            GearItem(1_000_005, InventoryType.Inventory1, 1), GearItem(6, InventoryType.ArmoryHands),
            GearItem(7, InventoryType.ArmoryLegs) with { NpcBuyable = false },
            GearItem(8, InventoryType.ArmoryFeets, quantity: 2), GearItem(9, InventoryType.ArmorySoulCrystal),
        ]);
        fixture.GearProtection.UnionWith([5u, 6u]); // Fresh normalized gearset and equipped protection.
        Assert.True(fixture.Service.TryPreviewGearSale(scope, out var selection), fixture.Service.StatusMessage);
        Assert.Equal(expected, selection!.Items.Count);
        Assert.All(selection.Items, item => Assert.Contains(item.Item.ItemId, new[] { 1u, 2u, 3u }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GearMovePreservesItemsAndReportsPartialCompletionWhenBagsFill(bool allUnprotected)
    {
        using var fixture = new NpcSaleFixture { GearBagCapacity = 1 };
        var first = GearItem(10, InventoryType.ArmoryHead) with { NpcBuyable = false, WhiteEquipment = !allUnprotected };
        var second = GearItem(11, InventoryType.ArmoryBody);
        fixture.GearItems.AddRange([first, second]);
        Assert.True(fixture.Service.StartGearMove(allUnprotected), fixture.Service.StatusMessage);
        fixture.Service.Update();
        Assert.Single(fixture.GearMoved);
        Assert.True(fixture.Service.IsRunning); // Dispatch alone does not establish the move.
        fixture.Service.Update();
        fixture.Service.Update();
        Assert.False(fixture.Service.IsRunning);
        Assert.Contains("moved 1 of 2", fixture.Service.LastSuccessMessage);
        Assert.Contains(fixture.GearItems, item => item.Container == InventoryType.Inventory1 && GearCleanupPolicy.Matches(item.Item, first.Item, true));
        Assert.Contains(second, fixture.GearItems);
        Assert.Empty(fixture.GearSold);
        Assert.Empty(fixture.Commands);
        Assert.Equal(0, fixture.GearInteractions);
    }

    [Theory]
    [InlineData("direct")]
    [InlineData("pre-handler")]
    [InlineData("topic")]
    public void GearSaleUsesExactFrozenSlotsThroughTheExistingVendorRoute(string handler)
    {
        using var fixture = new NpcSaleFixture(handler);
        fixture.GearUseMenu = handler == "topic";
        var confirmed = GearItem(20, InventoryType.ArmoryHead);
        fixture.GearItems.AddRange([confirmed, GearItem(21, InventoryType.Inventory1)]);
        Assert.True(fixture.Service.TryPreviewGearSale(GearCleanupScope.Armoury, out var selection));
        var newlyEligible = GearItem(22, InventoryType.ArmoryBody);
        fixture.GearItems.Add(newlyEligible);
        Assert.True(fixture.Service.StartGearSale(selection!), fixture.Service.StatusMessage);
        for (var tick = 0; tick < 8 && fixture.Service.IsRunning; tick++) fixture.Service.Update();
        Assert.Equal(new[] { confirmed }, fixture.GearSold);
        Assert.Contains(newlyEligible, fixture.GearItems);
        Assert.Contains(fixture.GearItems, item => item.Item.ItemId == 21);
        Assert.Equal(1, fixture.GearInteractions);
        Assert.Empty(fixture.Commands); // No generic AutoRetainer sale can widen this selection.
        Assert.False(fixture.Service.IsRunning);
        Assert.Contains("1 items processed", fixture.Service.LastSuccessMessage);
        Assert.False(fixture.Service.NpcSaleStatus.Running);
    }

    [Theory]
    [InlineData("item")]
    [InlineData("quality")]
    [InlineData("quantity")]
    [InlineData("protection")]
    [InlineData("unreadable")]
    [InlineData("character")]
    public void GearSaleRejectsAChangedPreviewBeforeAnyOperation(string change)
    {
        using var fixture = new NpcSaleFixture();
        fixture.GearItems.Add(GearItem(30, InventoryType.ArmoryHead));
        Assert.True(fixture.Service.TryPreviewGearSale(GearCleanupScope.Armoury, out var selection));
        var item = fixture.GearItems[0].Item;
        switch (change)
        {
            case "item": item.ItemId++; break;
            case "quality": item.Flags = InventoryItem.ItemFlags.HighQuality; break;
            case "quantity": item.Quantity = 2; break;
            case "protection": fixture.GearProtection.Add(30); break;
            case "unreadable": fixture.GearSnapshotAvailable = false; break;
            case "character": fixture.CharacterId++; break;
        }
        fixture.GearItems[0] = fixture.GearItems[0] with { Item = item };
        Assert.False(fixture.Service.StartGearSale(selection!));
        Assert.False(fixture.Service.IsRunning);
        Assert.Empty(fixture.GearSold);
        Assert.Empty(fixture.GearMoved);
        Assert.Empty(fixture.Commands);
        Assert.Equal(0, fixture.GearInteractions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GearSaleWaitsForSettlementWithoutReplayingAndRejectsAReplacementSlot(bool replacement)
    {
        using var fixture = new NpcSaleFixture();
        var confirmed = GearItem(35, InventoryType.ArmoryHead);
        fixture.GearItems.AddRange([confirmed, GearItem(36, InventoryType.ArmoryBody)]);
        Assert.True(fixture.Service.TryPreviewGearSale(GearCleanupScope.Armoury, out var selection));
        Assert.True(fixture.Service.StartGearSale(selection!));
        fixture.Service.Update(); // Open the owned vendor.
        fixture.Service.Update(); // Remove the exact submitted source slot.
        fixture.GearState = GearSaleState.Pending;
        if (replacement) fixture.GearItems.Add(GearItem(37, confirmed.Container, confirmed.Slot));
        Set(fixture.Service, "lastActionUtc", DateTime.UtcNow.AddSeconds(-2));
        fixture.Service.Update();
        Assert.Single(fixture.GearSold);
        Assert.Equal(!replacement, fixture.Service.IsRunning);
        Assert.Contains(fixture.GearItems, item => item.Item.ItemId == 36);
        if (replacement)
        {
            Assert.Contains("did not observe", fixture.Service.LastFailureMessage);
            return;
        }
        fixture.Service.Update(); // Still pending; no second callback.
        Assert.Single(fixture.GearSold);
        fixture.GearState = GearSaleState.Ready;
        fixture.Service.Update(); // Observe exact settlement.
        Assert.Single(fixture.GearSold);
        fixture.Service.Update(); // Only then submit the next confirmed slot.
        Assert.Equal(new[] { 35u, 36u }, fixture.GearSold.Select(item => item.Item.ItemId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GearSaleUnsupportedConfirmationKeepsGenericYesSuppressedUntilUiClosesOrCharacterChanges(bool characterChanges)
    {
        using var fixture = new NpcSaleFixture();
        fixture.GearItems.Add(GearItem(38, InventoryType.ArmoryHead));
        Assert.True(fixture.Service.TryPreviewGearSale(GearCleanupScope.Armoury, out var selection));
        Assert.True(fixture.Service.StartGearSale(selection!));
        fixture.Service.Update();
        fixture.Service.Update();
        fixture.GearState = GearSaleState.Unsupported;
        fixture.BlockingUi = "confirmation";
        fixture.Service.Update();
        Assert.False(fixture.Service.IsRunning);
        Assert.Single(fixture.GearSold);
        Assert.True(fixture.Service.SuppressesGenericYesNo);
        fixture.BlockingUi = null;
        Assert.True(fixture.Service.SuppressesGenericYesNo); // The owned shop can show its confirmation on a later frame.
        if (characterChanges) fixture.CharacterId++;
        else
        {
            fixture.GearState = GearSaleState.Ready;
            Invoke(fixture, "CloseGearVendor"); // User-owned UI closure, rather than another automation action.
        }
        Assert.False(fixture.Service.SuppressesGenericYesNo);
        fixture.BlockingUi = "confirmation";
        Assert.False(fixture.Service.SuppressesGenericYesNo); // A later unrelated dialog is not held.
        Assert.Empty(fixture.Commands);
    }

    [Fact]
    public void GearSaleRechecksProtectionBeforeEverySlotAndStopsOnUnsupportedShop()
    {
        using var fixture = new NpcSaleFixture();
        fixture.GearItems.AddRange([GearItem(40, InventoryType.ArmoryHead), GearItem(41, InventoryType.ArmoryBody)]);
        Assert.True(fixture.Service.TryPreviewGearSale(GearCleanupScope.Armoury, out var selection));
        Assert.True(fixture.Service.StartGearSale(selection!));
        fixture.Service.Update(); // Open the owned vendor.
        fixture.Service.Update(); // Submit the first exact slot.
        fixture.Service.Update(); // Observe its removal.
        fixture.GearProtection.Add(41);
        fixture.Service.Update();
        Assert.False(fixture.Service.IsRunning);
        Assert.Equal(40u, Assert.Single(fixture.GearSold).Item.ItemId);
        Assert.Contains("confirmed item changed", fixture.Service.LastFailureMessage);

        fixture.GearProtection.Clear();
        Assert.True(fixture.Service.TryPreviewGearSale(GearCleanupScope.Armoury, out selection));
        Assert.True(fixture.Service.StartGearSale(selection!));
        fixture.GearState = GearSaleState.Unsupported;
        fixture.Service.Update();
        fixture.Service.Update();
        Assert.False(fixture.Service.IsRunning);
        Assert.Single(fixture.GearSold);
        Assert.Contains("unsupported shop", fixture.Service.LastFailureMessage);
    }

    [Fact]
    public void GearCleanupCancellationAndUnobservedMovesDoNotProcessFurtherItems()
    {
        using var fixture = new NpcSaleFixture { GearCompleteOperations = false };
        fixture.GearItems.Add(GearItem(50, InventoryType.ArmoryHead));
        Assert.True(fixture.Service.StartGearMove());
        fixture.Service.Cancel("test cancellation");
        Assert.Empty(fixture.GearMoved);
        Assert.True(fixture.Service.StartGearMove());
        fixture.Service.Update();
        Assert.Single(fixture.GearMoved);
        Set(fixture.Service, "lastActionUtc", DateTime.UtcNow.AddSeconds(-2));
        fixture.Service.Update();
        Assert.False(fixture.Service.IsRunning);
        Assert.Single(fixture.GearMoved);
        Assert.Contains("did not observe", fixture.Service.LastFailureMessage);
        Assert.Equal(50u, Assert.Single(fixture.GearItems).Item.ItemId);
        Assert.Empty(fixture.GearSold);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("character")]
    [InlineData("mounted")]
    [InlineData("combat")]
    [InlineData("busy")]
    [InlineData("unreadable")]
    public void GearMoveStopsBeforeDispatchWhenSafetyChanges(string change)
    {
        using var fixture = new NpcSaleFixture();
        fixture.GearItems.Add(GearItem(60, InventoryType.ArmoryHead));
        Assert.True(fixture.Service.StartGearMove());
        switch (change)
        {
            case "owner": fixture.DutyOwned = true; break;
            case "character": fixture.CharacterId++; break;
            case "mounted": fixture.Mounted = true; break;
            case "combat": fixture.Combat = true; break;
            case "busy": fixture.AutoRetainerBusy = true; break;
            case "unreadable": fixture.GearSnapshotAvailable = false; break;
        }
        fixture.Service.Update();
        Assert.False(fixture.Service.IsRunning);
        Assert.Empty(fixture.GearMoved);
        Assert.Empty(fixture.GearSold);
        Assert.Equal(60u, Assert.Single(fixture.GearItems).Item.ItemId);
    }

    [Fact]
    public void GearSaleConfirmationDefaultsAndRestorationUseTheExistingConfigurationSave()
    {
        using var fixture = new NpcSaleFixture();
        var configuration = Newtonsoft.Json.JsonConvert.DeserializeObject<Configuration>("{\"Version\":21,\"PluginEnabled\":false}")!;
        Assert.True(new Configuration().GearSaleConfirmationEnabled);
        Assert.True(configuration.GearSaleConfirmationEnabled);
        configuration.GearSaleConfirmationEnabled = false;
        configuration.Save();
        var restored = Newtonsoft.Json.JsonConvert.DeserializeObject<Configuration>(Assert.Single(fixture.SavedConfigurations))!;
        Assert.False(restored.GearSaleConfirmationEnabled);
        Assert.Equal(21, restored.Version);
        restored.GearSaleConfirmationEnabled = true;
        restored.Save();
        Assert.True(Newtonsoft.Json.JsonConvert.DeserializeObject<Configuration>(fixture.SavedConfigurations.Last())!.GearSaleConfirmationEnabled);
    }

    private static GearCleanupCandidate GearItem(uint id, InventoryType container, ushort slot = 0, int quantity = 1)
        => new(container, slot, new InventoryItem { Container = container, Slot = (short)slot, ItemId = id, Quantity = quantity },
            $"Synthetic equipment {id}", true, true);

    [Theory]
    [InlineData("direct", false, false)]
    [InlineData("pre-handler", false, false)]
    [InlineData("topic", false, false)]
    [InlineData("direct", true, false)]
    [InlineData("direct", false, true)]
    public void NpcSaleCloseVendorHandsOffOnceWithoutOpeningRepairUi(string handler, bool repair, bool localOnly)
    {
        using var fixture = new NpcSaleFixture(handler, repair);
        Assert.True(fixture.Service.StartNpcSale("close-sale", localOnly), fixture.Service.StatusMessage); // No sanctuary/aetheryte is needed for a nearby sale.
        Assert.Equal(DateTime.MinValue, Get(fixture.Service, "lastInteractUtc"));
        Assert.Equal(0, fixture.TargetChanges);
        Assert.Empty(fixture.Commands);
        fixture.Service.Update();
        fixture.Service.Update();
        Assert.Equal(new[] { "/ays itemsell" }, fixture.Commands);
        Assert.Equal(0, fixture.TargetChanges);
        Assert.True(fixture.Service.NpcSaleStatus.Running);
        Assert.Contains("start work", fixture.Service.NpcSaleStatus.StatusMessage);
        Assert.Equal(fixture.Service.StatusMessage, fixture.Service.NpcSaleStatus.StatusMessage);
        Assert.False(fixture.Service.CancelNpcSale("another-operation"));
        fixture.AutoRetainerBusy = true;
        fixture.Service.Update();
        Assert.Contains("settle", fixture.Service.NpcSaleStatus.StatusMessage);
        fixture.AutoRetainerBusy = false;
        fixture.Service.Update();
        Assert.True(fixture.Service.NpcSaleStatus.Succeeded);
        Assert.True(fixture.Service.NpcSaleStatus.Done);
        Assert.False(fixture.Service.IsRunning);
        Assert.Equal(new[] { "/ays itemsell" }, fixture.Commands);

        // Repair still requires its repair event and still attempts its existing interaction.
        Assert.Equal(repair, fixture.Service.StartNpcRepairNoTeleportNoInn());
        if (repair)
        {
            Assert.Equal(1, Get(fixture.Service, "targetNpcRepairIndex"));
            Assert.NotEqual(DateTime.MinValue, Get(fixture.Service, "lastInteractUtc"));
            Assert.Equal(1, fixture.TargetChanges);
            fixture.Service.Cancel("test finished");
        }
    }

    [Fact]
    public void NpcSaleApproachStopsOnlyOwnedNavigationAndSettlesBeforeDispatch()
    {
        using var fixture = new NpcSaleFixture();
        fixture.VendorPosition = new Vector3(10, 0, 0);
        Assert.True(fixture.Service.StartNpcSale("approaching-sale", false), fixture.Service.StatusMessage);
        Assert.Equal(new[] { "/vnav moveto 10.00 0.00 0.00" }, fixture.Commands);
        fixture.Pathfinding = fixture.FollowingPath = true;
        fixture.PlayerPosition = fixture.VendorPosition;
        fixture.Service.Update();
        Assert.Equal(new[] { "/vnav moveto 10.00 0.00 0.00", "vnavmesh.Nav.PathfindCancelAll", "/vnav stop" }, fixture.Commands);
        Assert.Contains("navigation to settle", fixture.Service.NpcSaleStatus.StatusMessage);
        fixture.Service.Update();
        Assert.Equal(3, fixture.Commands.Count);
        Set(fixture.Service, "lastActionUtc", DateTime.UtcNow.AddSeconds(-2));
        fixture.Service.Update();
        Assert.Equal("/ays itemsell", fixture.Commands.Last());
        fixture.AutoRetainerBusy = true;
        fixture.Service.Update();
        Assert.True(fixture.Service.CancelNpcSale("approaching-sale"));
        Assert.False(fixture.Service.NpcSaleStatus.Succeeded);
        Assert.Equal(1, fixture.Commands.Count(command => command == "/vnav stop"));
        Assert.DoesNotContain("/ays reset", fixture.Commands);
        Assert.True(fixture.AutoRetainerBusy); // Global AR work is never reset on cancellation.
    }

    [Fact]
    public void NpcSaleRejectionIncludesReasonAndDoesNotReplaceAnotherOperation()
    {
        using var fixture = new NpcSaleFixture();
        fixture.AutoRetainerBusy = true;
        Assert.False(fixture.Service.StartNpcSale("rejected-sale", true));
        Assert.Equal("rejected-sale", fixture.Service.NpcSaleStatus.OperationId);
        Assert.Contains("AutoRetainer already owns work", fixture.Service.NpcSaleStatus.LastStartError);
        Assert.True(fixture.Service.NpcSaleStatus.Settled);
        Assert.False(fixture.Service.NpcSaleStatus.CommandSent);
        Assert.Empty(fixture.Commands);

        fixture.AutoRetainerBusy = false;
        Assert.True(fixture.Service.StartNpcSale("owned-sale", true));
        Assert.False(fixture.Service.StartNpcSale("another-sale", true));
        Assert.Equal("owned-sale", fixture.Service.NpcSaleStatus.OperationId);
        Assert.True(fixture.Service.NpcSaleStatus.Running);
        Assert.Contains("already running", fixture.Service.NpcSaleStatus.LastStartError);
        Assert.Empty(fixture.Commands);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActualVermaxionSaleFailureReturnsToFishingWithoutEndingItsVoyage(bool acceptedThenFailed)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        string? path = null;
        while (directory != null)
        {
            var candidate = Path.Combine(directory.FullName, "VERMAXION", "VERMAXION", "bin", "x64", "Debug", "VERMAXION.dll");
            if (File.Exists(candidate)) { path = candidate; break; }
            directory = directory.Parent;
        }
        Assert.NotNull(path); // Exercise the actual current product, not a copied policy.
        var assembly = System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyPath(path!);
        var pluginType = assembly.GetType("VERMAXION.Plugin", true)!;
        var serviceType = assembly.GetType("VERMAXION.Services.FishingService", true)!;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var piProperty = pluginType.GetProperty("PluginInterface", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previous = piProperty.GetValue(null);
        var starts = 0;
        var pi = Fake<IDalamudPluginInterface>((method, args) =>
        {
            if (method.Name != "GetIpcSubscriber") return Default(method.ReturnType);
            return (string)args![0]! switch
            {
                "ADS.StartNpcSale" => Fake<ICallGateSubscriber<string, bool, bool>>((call, _) =>
                {
                    if (call.Name != "InvokeFunc") return Default(call.ReturnType);
                    starts++;
                    return false;
                }),
                "ADS.GetNpcSaleStatusJson" => Fake<ICallGateSubscriber<string>>((call, _) => call.Name == "InvokeFunc"
                    ? "{\"operationId\":\"owned-sale\",\"running\":false,\"done\":true,\"succeeded\":false,\"settled\":true,\"lastStartError\":\"Vendor unavailable\",\"statusMessage\":\"Vendor unavailable\"}"
                    : Default(call.ReturnType)),
                _ => throw new InvalidOperationException("Unexpected IPC: " + args[0]),
            };
        });
        try
        {
            piProperty.SetValue(null, pi);
            var service = RuntimeHelpers.GetUninitializedObject(serviceType);
            Set(service, "log", Fake<IPluginLog>((method, _) => Default(method.ReturnType)));
            Set(service, "cleanupFeedback", new List<string>());
            Set(service, "cleanupCommands", Array.CreateInstance(assembly.GetType("VERMAXION.Models.FishingCleanupCommand", true)!, 0));
            Set(service, "npcSaleFailure", string.Empty);
            Set(service, "inventoryRecoveryActive", true);
            Set(service, "inventoryRecoveryReturnPosition", (Vector3?)new Vector3(4, 5, 6));
            if (acceptedThenFailed) Set(service, "npcSaleOperationId", "owned-sale");
            var stateField = serviceType.GetField("state", flags)!;
            stateField.SetValue(service, Enum.Parse(stateField.FieldType, "RunningInventoryCleanup"));

            // Lifecycle/navigation dependencies deliberately remain null: a sale error must not use them.
            serviceType.GetMethod("TickAdsNpcSale", flags)!.Invoke(service, [TimeSpan.Zero]);

            Assert.Equal("MovingToFishingSpot", Get(service, "state")!.ToString());
            Assert.True((bool)Get(service, "inventoryRecoveryActive")!);
            Assert.Equal(new Vector3(4, 5, 6), (Vector3)Get(service, "inventoryRecoveryReturnPosition")!);
            Assert.Null(Get(service, "npcSaleOperationId"));
            Assert.True((bool)Get(service, "inventorySaleFailedThisVoyage")!);
            Assert.Contains("Vendor unavailable", Assert.Single((List<string>)Get(service, "cleanupFeedback")!));
            Assert.Equal(acceptedThenFailed ? 0 : 1, starts);
        }
        finally { piProperty.SetValue(null, previous); }
    }

    [Fact]
    public void NpcSaleDoesNotReleaseFishingUntilSellerAndUiSettle()
    {
        using var fixture = new NpcSaleFixture();
        fixture.StartWorkOnSaleCommand = true;
        Assert.True(fixture.Service.StartNpcSale("fishing-sale", true));
        fixture.Service.Update();
        Assert.True(fixture.Service.NpcSaleStatus.CommandSent);
        fixture.BlockingUi = "shop";
        fixture.AutoRetainerBusy = false;
        fixture.Service.Update();
        Assert.True(fixture.Service.NpcSaleStatus.Running);
        Assert.False(fixture.Service.NpcSaleStatus.Settled);
        fixture.BlockingUi = null;
        fixture.Service.Update();
        Assert.True(fixture.Service.NpcSaleStatus.Succeeded);
        Assert.True(fixture.Service.NpcSaleStatus.Settled);
        Assert.Single(fixture.Commands, command => command == "/ays itemsell");
    }

    [Fact]
    public void CancelledDispatchedSaleRetainsSettlementEvidenceWithoutResettingSeller()
    {
        using var fixture = new NpcSaleFixture();
        fixture.StartWorkOnSaleCommand = true;
        Assert.True(fixture.Service.StartNpcSale("cancelled-fishing-sale", true));
        fixture.Service.Update();
        Assert.True(fixture.Service.CancelNpcSale("cancelled-fishing-sale"));
        Assert.True(fixture.Service.NpcSaleStatus.Done);
        Assert.False(fixture.Service.NpcSaleStatus.Succeeded);
        Assert.True(fixture.Service.NpcSaleStatus.CommandSent);
        Assert.False(fixture.Service.NpcSaleStatus.Settled);
        fixture.AutoRetainerBusy = false;
        fixture.BlockingUi = "confirmation";
        Assert.False(fixture.Service.NpcSaleStatus.Settled);
        fixture.BlockingUi = null;
        Assert.True(fixture.Service.NpcSaleStatus.Settled);
        Assert.DoesNotContain("/ays reset", fixture.Commands);
        Assert.DoesNotContain("/vnav stop", fixture.Commands);
    }

    [Fact]
    public void NpcSaleMatchingCancelStopsAnAcceptedApproachOnceOnTheSameCharacter()
    {
        using var fixture = new NpcSaleFixture();
        fixture.VendorPosition = new Vector3(10, 0, 0);
        Assert.True(fixture.Service.StartNpcSale("cancel-approach", true));
        fixture.Pathfinding = fixture.FollowingPath = true;
        Assert.False(fixture.Service.CancelNpcSale("another-operation"));
        Assert.Single(fixture.Commands);
        Assert.True(fixture.Service.CancelNpcSale("cancel-approach"));
        Assert.Equal(new[] { "/vnav moveto 10.00 0.00 0.00", "vnavmesh.Nav.PathfindCancelAll", "/vnav stop" }, fixture.Commands);
        Assert.False(fixture.Service.CancelNpcSale("cancel-approach"));
        Assert.Equal(3, fixture.Commands.Count);
    }

    [Fact]
    public void NpcSaleObservesWorkThatStartsDuringTheCommandDispatch()
    {
        using var fixture = new NpcSaleFixture();
        fixture.StartWorkOnSaleCommand = true;
        Assert.True(fixture.Service.StartNpcSale("immediate-start", true));
        fixture.Service.Update();
        Assert.True(fixture.Service.NpcSaleStatus.Running);
        fixture.AutoRetainerBusy = false;
        fixture.Service.Update();
        Assert.True(fixture.Service.NpcSaleStatus.Succeeded);
        Assert.Equal(new[] { "/ays itemsell" }, fixture.Commands);
    }

    [Theory]
    [InlineData("not-ready")]
    [InlineData("unrelated-path")]
    [InlineData("rejected-move")]
    [InlineData("character-change")]
    [InlineData("logout")]
    [InlineData("duty-ownership")]
    [InlineData("inn-ownership")]
    public void NpcSaleCancellationCannotStopUnownedOrNewCharacterNavigation(string boundary)
    {
        using var fixture = new NpcSaleFixture();
        fixture.VendorPosition = new Vector3(10, 0, 0);
        fixture.NavigationReady = boundary != "not-ready";
        fixture.FollowingPath = boundary == "unrelated-path";
        fixture.RejectCommand = boundary == "rejected-move" ? "/vnav moveto 10.00 0.00 0.00" : null;
        var started = fixture.Service.StartNpcSale("navigation-boundary", true);
        if (boundary is "unrelated-path" or "rejected-move") Assert.False(started);
        else Assert.True(started);
        if (boundary == "character-change") fixture.CharacterId = 2;
        if (boundary == "logout") fixture.LoggedIn = false;
        if (boundary == "duty-ownership") fixture.DutyOwned = true;
        if (boundary == "inn-ownership") fixture.InnOwned = true;
        if (boundary is "character-change" or "logout" or "duty-ownership" or "inn-ownership") fixture.Service.Update();
        else fixture.Service.CancelNpcSale("navigation-boundary");
        Assert.DoesNotContain("/vnav stop", fixture.Commands);
        Assert.DoesNotContain("vnavmesh.Nav.PathfindCancelAll", fixture.Commands);
        Assert.DoesNotContain("/ays itemsell", fixture.Commands);
        Assert.False(fixture.Service.IsRunning);
    }

    [Theory]
    [InlineData("shop")]
    [InlineData("selection")]
    [InlineData("confirmation")]
    [InlineData("talk")]
    [InlineData("duty")]
    [InlineData("inn")]
    [InlineData("ar-busy")]
    public void NpcSaleRechecksOwnershipAndUnrelatedUiBeforeTheBackendCommand(string boundary)
    {
        using var fixture = new NpcSaleFixture();
        Assert.True(fixture.Service.StartNpcSale("handoff-boundary", false));
        fixture.BlockingUi = boundary;
        fixture.DutyOwned = boundary == "duty";
        fixture.InnOwned = boundary == "inn";
        fixture.AutoRetainerBusy = boundary == "ar-busy";
        fixture.Service.Update();
        Assert.False(fixture.Service.IsRunning);
        Assert.False(fixture.Service.NpcSaleStatus.Succeeded);
        Assert.Empty(fixture.Commands);
        Assert.Equal(0, fixture.TargetChanges);
    }

    [Fact]
    public void NpcSaleLocalOnlyAndFallbackReadinessKeepTheirExistingBounds()
    {
        using var fixture = new NpcSaleFixture();
        fixture.VendorPosition = new Vector3(121, 0, 0);
        Assert.False(fixture.Service.StartNpcSale("local-bound", true));
        Assert.Contains("local-only", fixture.Service.NpcSaleStatus.StatusMessage);
        Assert.Empty(fixture.Commands);
        Assert.False(fixture.Service.StartNpcSale("fallback-readiness", false));
        Assert.Contains("sanctuary", fixture.Service.NpcSaleStatus.StatusMessage);
        fixture.NearAetheryte = true;
        Set(fixture.Service, "cachedLifestreamLoaded", false);
        Set(fixture.Service, "lifestreamCacheExpiresUtc", DateTime.UtcNow.AddMinutes(1));
        Assert.False(fixture.Service.StartNpcSale("fallback-not-loaded", false));
        Assert.Contains("Lifestream was not loaded", fixture.Service.NpcSaleStatus.StatusMessage);
        fixture.LifestreamBusy = true;
        Assert.False(fixture.Service.StartNpcSale("fallback-unowned-travel", false));
        Assert.Contains("already owns work", fixture.Service.NpcSaleStatus.StatusMessage);
        Assert.Empty(fixture.Commands);
    }

    [Theory]
    [InlineData("not-logged-in")]
    [InlineData("character-not-ready")]
    [InlineData("zoning")]
    [InlineData("mounted")]
    public void NpcSaleCannotApproachBeforeItsCharacterIsReady(string boundary)
    {
        using var fixture = new NpcSaleFixture();
        fixture.VendorPosition = new Vector3(10, 0, 0);
        fixture.LoggedIn = boundary != "not-logged-in";
        fixture.CharacterId = boundary == "character-not-ready" ? 0UL : 1UL;
        fixture.Loading = boundary == "zoning";
        fixture.Mounted = boundary == "mounted";
        Assert.False(fixture.Service.StartNpcSale("start-readiness", true));
        Assert.False(fixture.Service.IsRunning);
        Assert.Empty(fixture.Commands);
    }

    [Theory]
    [InlineData("start-ipc")]
    [InlineData("handoff-ipc")]
    [InlineData("completion-ipc")]
    [InlineData("rejected-command")]
    [InlineData("no-observed-work")]
    [InlineData("observed-work-timeout")]
    public void NpcSaleReportsDispatchObservationAndTimeoutFailuresWithoutInventingSales(string boundary)
    {
        using var fixture = new NpcSaleFixture();
        fixture.MissingAutoRetainer = boundary == "start-ipc";
        var started = fixture.Service.StartNpcSale("failure-boundary", false);
        Assert.Equal(boundary != "start-ipc", started);
        if (!started) { Assert.Contains("unavailable", fixture.Service.StatusMessage); return; }
        fixture.MissingAutoRetainer = boundary == "handoff-ipc";
        fixture.RejectCommand = boundary == "rejected-command" ? "/ays itemsell" : null;
        fixture.Service.Update();
        if (boundary == "completion-ipc") fixture.MissingAutoRetainer = true;
        if (boundary == "observed-work-timeout")
        {
            fixture.AutoRetainerBusy = true;
            fixture.Service.Update();
        }
        if (boundary is "no-observed-work" or "observed-work-timeout")
            Set(fixture.Service, "startedAtUtc", DateTime.UtcNow.AddSeconds(-121));
        fixture.Service.Update();
        Assert.False(fixture.Service.IsRunning);
        Assert.False(fixture.Service.NpcSaleStatus.Succeeded);
        Assert.NotEmpty(fixture.Service.LastFailureMessage);
        Assert.DoesNotContain("/ays reset", fixture.Commands);
        Assert.DoesNotContain("/vnav stop", fixture.Commands);
        Assert.InRange(fixture.Commands.Count(command => command == "/ays itemsell"), 0, 1);
        if (boundary == "no-observed-work") Assert.Contains("did not start observable work", fixture.Service.LastFailureMessage);
        if (boundary == "observed-work-timeout") Assert.Contains("Timed out waiting", fixture.Service.LastFailureMessage);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("character-change")]
    [InlineData("settle")]
    public void NpcSaleFieldTravelUsesOwnedCancellationAndStableArrivalBeforeVendorApproach(string boundary)
    {
        using var fixture = new NpcSaleFixture();
        Assert.True(fixture.Service.StartNpcSale("field-travel", false));
        var routeType = typeof(UtilityAutomationService).GetNestedType("ResolvedFieldRepairRoute", BindingFlags.NonPublic)!;
        var route = Activator.CreateInstance(routeType, [130u, "Synthetic field", 100u, "Synthetic crystal", 100])!;
        Assert.True((bool)Invoke(fixture.Service, "TrySendNpcRepairFieldTeleport", route)!);
        Set(fixture.Service, "activeNpcRepairFieldRoute", route);
        var stageType = typeof(UtilityAutomationService).GetNestedType("NpcRepairTravelStage", BindingFlags.NonPublic)!;
        Set(fixture.Service, "npcRepairTravelStage", Enum.Parse(stageType, "TeleportingToFieldAetheryte"));
        Set(fixture.Service, "npcRepairTravelStageStartedUtc", DateTime.UtcNow);
        Assert.True((bool)Get(fixture.Service, "npcRepairOwnsInnTravel")!);
        fixture.LifestreamBusy = true;
        fixture.Service.Update();
        Assert.Contains("Lifestream to finish", fixture.Service.NpcSaleStatus.StatusMessage);
        if (boundary is "cancel" or "character-change")
        {
            if (boundary == "character-change") fixture.CharacterId = 2;
            fixture.Service.CancelNpcSale("field-travel");
            Assert.Equal(boundary == "cancel" ? 1 : 0, fixture.Commands.Count(command => command == "Lifestream.Abort"));
            Assert.DoesNotContain("/vnav stop", fixture.Commands);
            return;
        }
        var tick = DateTime.UtcNow;
        fixture.LifestreamBusy = false;
        Set(fixture.Service, "npcRepairTravelCommandUtc", tick.AddSeconds(-30));
        Invoke(fixture.Service, "UpdateNpcRepairFieldTeleport", tick, route);
        Assert.Equal("TeleportingToFieldAetheryte", Get(fixture.Service, "npcRepairTravelStage")!.ToString());
        fixture.Loading = true;
        Invoke(fixture.Service, "UpdateNpcRepairFieldTeleport", tick.AddSeconds(1), route);
        fixture.Loading = false;
        Invoke(fixture.Service, "UpdateNpcRepairFieldTeleport", tick.AddSeconds(2), route);
        Invoke(fixture.Service, "UpdateNpcRepairFieldTeleport", tick.AddMilliseconds(3399), route);
        Assert.Equal("TeleportingToFieldAetheryte", Get(fixture.Service, "npcRepairTravelStage")!.ToString());
        Invoke(fixture.Service, "UpdateNpcRepairFieldTeleport", tick.AddMilliseconds(3400), route);
        Assert.Equal("AwaitingRepairNpc", Get(fixture.Service, "npcRepairTravelStage")!.ToString());
        Assert.False((bool)Get(fixture.Service, "npcRepairOwnsInnTravel")!);
        Set(fixture.Service, "startedAtUtc", DateTime.UtcNow.AddSeconds(-121));
        fixture.Service.Update(); // The existing travel exemption lets candidate selection reset the approach clock.
        Assert.Equal("None", Get(fixture.Service, "npcRepairTravelStage")!.ToString());
        Assert.True((DateTime)Get(fixture.Service, "startedAtUtc")! > DateTime.UtcNow.AddSeconds(-2));
        fixture.Service.Update();
        Assert.Equal(1, fixture.Commands.Count(command => command == "/ays itemsell"));
        fixture.Service.CancelNpcSale("field-travel");
        Assert.DoesNotContain("Lifestream.Abort", fixture.Commands);
    }

    [Fact]
    public void InnRouteUsesUnlockedRootFromTheSameAethernetGroup()
    {
        // Limsa's main crystal and inn shard occupy different territories.
        // Another main crystal in the inn territory must not be selected instead.
        var aetherytes = RepairAetherytes(
            (8, true, 129, 1), (41, false, 128, 1), (2, true, 132, 2), (94, false, 132, 2),
            (9, true, 130, 3), (33, false, 130, 3), (999, true, 128, 4));
        Assert.Equal(7, aetherytes.Count());
        Assert.True(aetherytes.GetRow(8).IsAetheryte);
        Assert.Equal(129u, aetherytes.GetRow(8).Territory.RowId);
        Assert.Equal(aetherytes.GetRow(8).AethernetGroup, aetherytes.GetRow(41).AethernetGroup);
        var unlocked = new HashSet<uint> { 2, 8, 9, 999 };
        var ids = new uint[] { 2, 8, 9, 999 };
        var list = Fake<IAetheryteList>((method, args) => method.Name switch
        {
            "get_Length" => ids.Length,
            "get_Item" => Fake<IAetheryteEntry>((entryMethod, _) => entryMethod.Name switch
            {
                "get_AetheryteId" => unlocked.Contains(ids[(int)args![0]!]) ? ids[(int)args[0]!] : 0u,
                "get_GilCost" => ids[(int)args![0]!] * 10u,
                _ => Default(entryMethod.ReturnType),
            }),
            _ => Default(method.ReturnType),
        });
        var property = typeof(Plugin).GetProperty("AetheryteList", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previous = property.GetValue(null);
        property.SetValue(null, list);
        try
        {
            var data = new TestData(InnTerritories([177, 178, 179]), aetherytes);
            var service = new UtilityAutomationService(data, null!, null!, null!, null!, null!,
                new Configuration(), null!, null!, null!, () => false, () => false, Fake<IPluginLog>((call, args) =>
                {
                    if (call.Name == "Warning" && args?[0] is Exception exception)
                        throw new InvalidOperationException("Unexpected route lookup failure", exception);
                    return Default(call.ReturnType);
                }));
            var method = typeof(UtilityAutomationService).GetMethod("TryGetUnlockedInnRouteGilCost", BindingFlags.Instance | BindingFlags.NonPublic)!;
            foreach (var (shard, root) in new[] { (41u, 8u), (94u, 2u), (33u, 9u) })
            {
                object[] args = [aetherytes.GetRow(shard), 0];
                Assert.True((bool)method.Invoke(service, args)!);
                Assert.Equal((int)root * 10, args[1]);
                unlocked.Remove(root);
                Assert.False((bool)method.Invoke(service, args)!);
                unlocked.Add(root);
            }
        }
        finally
        {
            property.SetValue(null, previous);
        }
    }

    [Fact]
    public void InnTravelSettlesAfterFinalHopAndStalledMovementRetries()
    {
        var tick = new DateTime(2026, 9, 24, 14, 0, 0, DateTimeKind.Utc);
        var progressPosition = Vector3.Zero;
        var progressTime = tick;
        Assert.False(InnEntryService.IsMovementStalled(Vector3.Zero, tick.AddMilliseconds(2999), ref progressPosition, ref progressTime));
        Assert.True(InnEntryService.IsMovementStalled(Vector3.Zero, tick.AddSeconds(3), ref progressPosition, ref progressTime));
        Assert.False(InnEntryService.IsMovementStalled(Vector3.UnitX, tick.AddSeconds(3), ref progressPosition, ref progressTime));
        Assert.False(InnEntryService.IsMovementStalled(Vector3.UnitX, tick.AddMilliseconds(5999), ref progressPosition, ref progressTime));
        Assert.True(InnEntryService.IsMovementStalled(Vector3.UnitX, tick.AddSeconds(6), ref progressPosition, ref progressTime));

        var events = new List<string>();
        var ready = true;
        var pathfinding = false;
        var following = false;
        var busy = true;
        var zoning = false;
        uint territory = 178;
        var position = new Vector3(-137.8995f, -3.154889f, -168.48999f);
        var player = Fake<IPlayerCharacter>((method, _) => method.Name == "get_Position" ? position : Default(method.ReturnType));
        var objects = Fake<IObjectTable>((method, _) => method.Name == "get_LocalPlayer" ? player : Default(method.ReturnType));
        var client = Fake<IClientState>((method, _) => method.Name switch
        {
            "get_IsLoggedIn" => true, "get_TerritoryType" => territory, _ => Default(method.ReturnType),
        });
        var condition = Fake<ICondition>((method, _) => method.Name == "get_Item" ? zoning : Default(method.ReturnType));
        var command = Fake<ICommandManager>((method, args) =>
        {
            if (method.Name != "ProcessCommand") return Default(method.ReturnType);
            var text = (string)args![0]!;
            events.Add(text);
            if (text == "/vnav stop") following = false;
            return true;
        });
        var pluginInterface = Fake<IDalamudPluginInterface>((method, args) =>
        {
            if (method.Name != "GetIpcSubscriber") return Default(method.ReturnType);
            var name = (string)args![0]!;
            if (method.GetGenericArguments().Single() == typeof(bool))
                return Fake<ICallGateSubscriber<bool>>((call, _) => call.Name != "InvokeFunc" ? Default(call.ReturnType) : name switch
                {
                    "Lifestream.IsBusy" => busy,
                    "vnavmesh.Nav.IsReady" => ready,
                    "vnavmesh.SimpleMove.PathfindInProgress" => pathfinding,
                    "vnavmesh.Path.IsRunning" => following,
                    _ => throw new InvalidOperationException(name),
                });
            return Fake<ICallGateSubscriber<object>>((call, _) =>
            {
                if (call.Name == "InvokeAction")
                {
                    events.Add(name);
                    if (name == "vnavmesh.Nav.PathfindCancelAll") pathfinding = false;
                }
                return Default(call.ReturnType);
            });
        });
        var property = typeof(Plugin).GetProperty("PluginInterface", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previous = property.GetValue(null);
        property.SetValue(null, pluginInterface);
        try
        {
            var data = new TestData(InnTerritories([178, 179, 177]));
            var log = Fake<IPluginLog>();
            var inn = new InnEntryService(data, objects, null!, command, client, condition, log);
            var service = new UtilityAutomationService(data, objects, null!, command, client, condition,
                new Configuration(), null!, null!, null!, () => false, () => false, log, inn);
            Assert.True(service.StartNpcRepairYesInn("uldah"));
            territory = 130;
            var routeType = typeof(UtilityAutomationService).GetNestedType("ResolvedInnRepairRoute", BindingFlags.NonPublic)!;
            var route = Activator.CreateInstance(routeType, 130u, "Ul'dah", 33u, "Adventurers' Guild",
                new[] { new Vector3(53.7f, 4, -126) }, 0)!;
            var stageType = Get(service, "npcRepairTravelStage")!.GetType();
            Set(service, "npcRepairTravelStage", Enum.Parse(stageType, "TeleportingToInnAethernet"));
            Set(service, "npcRepairOwnsInnTravel", true);
            Set(service, "npcRepairTravelCommandUtc", tick.AddSeconds(-20));

            Invoke(service, "UpdateNpcRepairInnTeleport", tick, route);
            Assert.Equal(DateTime.MinValue, Get(service, "npcRepairInnArrivalReadyUtc"));
            busy = false;
            Invoke(service, "UpdateNpcRepairInnTeleport", tick, route);
            Invoke(service, "UpdateNpcRepairInnTeleport", tick.AddMilliseconds(400), route);
            Assert.Equal("TeleportingToInnAethernet", Get(service, "npcRepairTravelStage")!.ToString());

            // Replay the reported gap: IsBusy clears, then the final aethernet hop starts.
            zoning = true;
            service.Update();
            Assert.Equal(DateTime.MinValue, Get(service, "npcRepairInnArrivalReadyUtc"));
            zoning = false;
            Invoke(service, "UpdateNpcRepairInnTeleport", tick.AddSeconds(2), route);
            Invoke(service, "UpdateNpcRepairInnTeleport", tick.AddMilliseconds(3399), route);
            Assert.Equal("TeleportingToInnAethernet", Get(service, "npcRepairTravelStage")!.ToString());
            Invoke(service, "UpdateNpcRepairInnTeleport", tick.AddMilliseconds(3400), route);
            Assert.Equal("WalkingInnPath", Get(service, "npcRepairTravelStage")!.ToString());
            Assert.Empty(events);

            ready = false;
            Invoke(service, "UpdateNpcRepairInnPath", DateTime.UtcNow, route);
            Assert.Empty(events);
            ready = true;
            Invoke(service, "UpdateNpcRepairInnPath", DateTime.UtcNow, route);
            Assert.Equal("/vnav moveto 53.70 4.00 -126.00", Assert.Single(events));
            events.Clear();
            following = true;
            Set(service, "lastMovementProgressUtc", DateTime.UtcNow.AddSeconds(-2.5));
            Set(service, "lastMoveCommandUtc", DateTime.UtcNow.AddSeconds(-2.1));
            Invoke(service, "UpdateNpcRepairInnPath", DateTime.UtcNow, route);
            Assert.Empty(events);
            pathfinding = true;
            Set(service, "lastMovementProgressUtc", DateTime.UtcNow.AddSeconds(-3.1));
            Invoke(service, "UpdateNpcRepairInnPath", DateTime.UtcNow, route);
            Assert.Equal(new[] { "vnavmesh.Nav.PathfindCancelAll", "/vnav stop" }, events);
            Invoke(service, "UpdateNpcRepairInnPath", DateTime.UtcNow, route);
            Assert.Equal("/vnav moveto 53.70 4.00 -126.00", events.Last());
            events.Clear();

            following = true;
            position += Vector3.UnitX;
            Set(service, "lastMovementProgressUtc", DateTime.UtcNow.AddSeconds(-4));
            Invoke(service, "UpdateNpcRepairInnPath", DateTime.UtcNow, route);
            Assert.Empty(events); // Moving paths are not restarted.
            pathfinding = true;
            zoning = true;
            service.Update();
            Assert.Equal(new[] { "vnavmesh.Nav.PathfindCancelAll", "/vnav stop" }, events);
            events.Clear();
            service.Update();
            Assert.Empty(events); // Loading cancellation is issued once.
            zoning = false;
            service.Cancel("test Stop");
            events.Clear();
            service.Update();
            Assert.Empty(events);
            Assert.False(service.IsRunning);

            // The innkeeper approach shares the same recovery, retaining the Ul'dah counter fix.
            var npc = Fake<IGameObject>((method, _) => method.Name switch
            {
                "get_BaseId" => 1001976u, "get_Position" => new Vector3(28.86f, 7, -80.13f), _ => Default(method.ReturnType),
            });
            Invoke(inn, "SendMoveCommand", npc, true);
            Assert.Equal("/vnav moveto 31.50 7.00 -82.00", Assert.Single(events));
            events.Clear();
            following = true;
            Set(inn, "lastMovementProgressUtc", DateTime.UtcNow.AddSeconds(-3.1));
            Invoke(inn, "SendMoveCommand", npc, false);
            Assert.Equal("/vnav stop", Assert.Single(events));
            Invoke(inn, "SendMoveCommand", npc, false);
            Assert.Equal("/vnav moveto 31.50 7.00 -82.00", events.Last());
        }
        finally
        {
            property.SetValue(null, previous);
        }
    }

    [Fact]
    public void ManualInnRepairPreservesDestinationAndCancellationBoundaries()
    {
        // Otopa is more than three yalms away from the accessible side of the counter.
        var npc = new Vector3(28.86f, 7, -80.13f);
        var approach = new Vector3(31.5f, 7, -82);
        Assert.True(Vector3.Distance(npc, approach) > 3);
        Assert.Equal(approach, InnEntryService.InteractionPoint(130, 1001976, npc));
        Assert.True(InnEntryService.HasReachedInteractionPoint(130, 1001976, npc, approach));
        Assert.True(InnEntryService.HasReachedInteractionPoint(130, 1001976, npc, approach + Vector3.UnitX));
        Assert.False(InnEntryService.HasReachedInteractionPoint(130, 1001976, npc, approach + Vector3.UnitX * 1.01f));
        foreach (var (cityTerritory, id) in new[] { (128u, 1000974u), (132u, 1000102u), (131u, 1001976u) })
        {
            Assert.Equal(npc, InnEntryService.InteractionPoint(cityTerritory, id, npc));
            Assert.True(InnEntryService.HasReachedInteractionPoint(cityTerritory, id, npc, npc + Vector3.UnitX * 3));
            Assert.False(InnEntryService.HasReachedInteractionPoint(cityTerritory, id, npc, npc + Vector3.UnitX * 3.01f));
        }
        Assert.True(InnEntryService.ShouldSubmitMovement(() => false, () => false));
        Assert.False(InnEntryService.ShouldSubmitMovement(() => true, () => false));
        Assert.False(InnEntryService.ShouldSubmitMovement(() => false, () => true));
        Assert.False(InnEntryService.ShouldSubmitMovement(() => true, () => throw new InvalidOperationException("must short-circuit")));

        var cities = new[] { ("uldah", 33u, 178u), ("gridania", 94u, 179u), ("limsa", 41u, 177u),
            ("ishgard", 80u, 429u), ("crystarium", 152u, 843u), ("sharlayan", 185u, 990u), ("tuliyollal", 220u, 1205u) };
        foreach (var (name, route, room) in cities)
        {
            Assert.True(UtilityAutomationService.TryGetInnDestination(name.ToUpperInvariant(), out var actualRoute, out var actualRoom));
            Assert.Equal(route, actualRoute);
            Assert.Equal(room, actualRoom);
            Assert.Equal("npc-yes-inn-" + name, Plugin.NormalizeRepairMode(" NPC-YES-INN-" + name.ToUpperInvariant() + " "));
        }
        foreach (var invalid in new[] { "", "kugane", "unknown", "uldah extra" })
        {
            Assert.False(UtilityAutomationService.TryGetInnDestination(invalid, out _, out _));
            Assert.Empty(Plugin.NormalizeRepairMode("npc-yes-inn-" + invalid));
        }
        Assert.Equal("npc-yes-inn", Plugin.NormalizeRepairMode("yesinn"));
        Assert.Equal("npc", Plugin.NormalizeRepairMode("npcrepair"));
        Assert.Equal("self", Plugin.NormalizeRepairMode("selfrepair"));
        Assert.Equal("npc-no-inn", Plugin.NormalizeRepairMode("noinn"));
        Assert.Equal("npc-no-teleport-no-inn", Plugin.NormalizeRepairMode("npc-no-tp-no-inn"));

        var log = Fake<IPluginLog>();
        uint territory = 178;
        var rejectMovement = false;
        var commands = new List<string>();
        var command = Fake<ICommandManager>((method, args) =>
        {
            if (method.Name != "ProcessCommand") return Default(method.ReturnType);
            commands.Add((string)args![0]!);
            return !rejectMovement || !((string)args[0]!).StartsWith("/vnav moveto");
        });
        var player = Fake<IPlayerCharacter>();
        var objects = Fake<IObjectTable>((method, _) => method.Name switch
        {
            "get_LocalPlayer" => player,
            "GetEnumerator" => ((IEnumerable<Dalamud.Game.ClientState.Objects.Types.IGameObject>)Array.Empty<Dalamud.Game.ClientState.Objects.Types.IGameObject>()).GetEnumerator(),
            _ => Default(method.ReturnType),
        });
        var client = Fake<IClientState>((method, _) => method.Name switch
        {
            "get_IsLoggedIn" => true, "get_TerritoryType" => territory, _ => Default(method.ReturnType),
        });
        var territories = InnTerritories(cities.Select(city => city.Item3).Append(629u).ToArray());
        var data = new TestData(territories);
        var condition = Fake<ICondition>();
        var innEntry = new InnEntryService(data, objects, null!, command, client, condition, log);
        UtilityAutomationService Create() => new(data, objects, null!, command, client, condition,
            new Configuration(), null!, null!, null!, () => false, () => false, log, innEntry);

        // Actual start state, terminal entry checks, and cleanup; no game process/native callbacks.
        foreach (var (name, route, room) in cities.Take(3))
        {
            var startingRoom = room;
            territory = startingRoom;
            var service = Create();
            Assert.True(service.StartNpcRepairYesInn(name));
            Assert.True(service.IsRunning);
            Assert.Equal("ExitingInn", Get(service, "npcRepairTravelStage")!.ToString());
            Assert.Equal(startingRoom, Get(service, "npcRepairStartingInnTerritory"));
            Assert.Equal(room, Get(service, "npcRepairRequiredInnTerritory"));
            Assert.Equal(route, Get(service, "npcRepairDestinationAethernet"));

            // Invalid input cannot restart or redirect an accepted attempt.
            Assert.False(service.StartNpcRepairYesInn("kugane"));
            Assert.Equal(room, Get(service, "npcRepairRequiredInnTerritory"));
            Assert.True(service.IsRunning);
            // Even fully repaired gear cannot complete before reaching the mender.
            Assert.False((bool)Invoke(service, "TryCompleteRepairIfFinished", "fully repaired")!);
            Assert.True(service.IsRunning);

            // Simulate the repair result after the visit. Completion must remain pending for entry.
            Set(service, "npcRepairMenderReached", true);
            Invoke(service, "Complete", "Equipped gear is fully repaired.");
            Assert.True(service.IsRunning);
            Assert.Empty(service.LastSuccessMessage);
            Assert.Equal("ReturningToInn", Get(service, "npcRepairTravelStage")!.ToString());
            territory = room;
            Invoke(service, "UpdateNpcRepairInnReturn");
            Assert.False(service.IsRunning);
            Assert.Contains($"room {room}", service.LastSuccessMessage);
            Assert.Empty(service.LastFailureMessage);
            Assert.Equal(0u, Get(service, "npcRepairRequiredInnTerritory"));

            // Another inn is never accepted as the destination.
            territory = startingRoom;
            service = Create();
            Assert.True(service.StartNpcRepairYesInn(name));
            Invoke(service, "Complete", "repaired");
            territory = room == 178 ? 179u : 178u;
            Invoke(service, "UpdateNpcRepairInnReturn");
            Assert.False(service.IsRunning);
            Assert.Empty(service.LastSuccessMessage);
            Assert.Contains("expected", service.LastFailureMessage);

            foreach (var reason in new[] { "Stop", "logout", "plugin dispose" })
            {
                territory = startingRoom;
                service = Create();
                Assert.True(service.StartNpcRepairYesInn(name));
                service.Cancel(reason);
                Assert.False(service.IsRunning);
                Assert.Contains(reason, service.LastFailureMessage);
                Assert.Equal(0u, Get(service, "npcRepairStartingInnTerritory"));
                Assert.Equal(0u, Get(service, "npcRepairDestinationAethernet"));
            }

            service = Create();
            Assert.True(service.StartNpcRepairYesInn(name));
            Set(service, "startedAtUtc", DateTime.UtcNow.AddMinutes(-3));
            service.Update();
            Assert.False(service.IsRunning);
            Assert.Contains("Timed out", service.LastFailureMessage);
        }

        // Other-inn starts go directly to route resolution, never to the exit door.
        // With missing route data they fail immediately, preserving the requested destination.
        foreach (var (name, _, room) in cities.Take(3))
        foreach (var otherRoom in cities.Take(3).Select(city => city.Item3).Where(candidate => candidate != room))
        {
            territory = otherRoom;
            var crossCity = Create();
            var commandCount = commands.Count;
            Assert.False(crossCity.StartNpcRepairYesInn(name));
            Assert.False(crossCity.IsRunning);
            Assert.Contains("requested inn repair route", crossCity.LastFailureMessage);
            Assert.DoesNotContain(commands.Skip(commandCount), text => text.StartsWith("/vnav moveto"));
        }

        territory = 179;
        var automatic = Create();
        Assert.True(automatic.StartNpcRepairYesInn());
        Assert.Equal(179u, Get(automatic, "npcRepairRequiredInnTerritory"));
        Assert.Equal(0u, Get(automatic, "npcRepairDestinationAethernet"));
        automatic.Cancel("test complete");

        // Missing doors and innkeepers fail; a loading mender waits within the existing timeouts.
        territory = 178;
        var missingDoor = Create();
        Assert.True(missingDoor.StartNpcRepairYesInn("uldah"));
        Invoke(missingDoor, "UpdateNpcRepairInnExit");
        Assert.False(missingDoor.IsRunning);
        Assert.Contains("exit door", missingDoor.LastFailureMessage);

        var routeType = typeof(UtilityAutomationService).GetNestedType("ResolvedInnRepairRoute", BindingFlags.NonPublic)!;
        var resolvedRoute = Activator.CreateInstance(routeType, 132u, "Gridania", 94u, "test route", Array.Empty<Vector3>(), 0)!;
        foreach (var ending in new[] { "stage timeout", "overall timeout", "Stop", "logout", "plugin dispose" })
        {
            territory = 179;
            var missingMender = Create();
            Assert.True(missingMender.StartNpcRepairYesInn("gridania"));
            territory = 132;
            Set(missingMender, "activeNpcRepairInnRoute", resolvedRoute);
            Set(missingMender, "npcRepairTravelStage", Enum.Parse(Get(missingMender, "npcRepairTravelStage")!.GetType(), "AwaitingRepairNpc"));
            var searchStarted = DateTime.UtcNow.AddSeconds(-3);
            Set(missingMender, "npcRepairTravelStageStartedUtc", searchStarted);
            var commandCount = commands.Count;
            foreach (var elapsedSeconds in new[] { 3, 74 })
            {
                Invoke(missingMender, "UpdateNpcRepairTravel", searchStarted.AddSeconds(elapsedSeconds));
                Assert.True(missingMender.IsRunning);
                Assert.Empty(missingMender.LastFailureMessage);
                Assert.Empty(missingMender.LastSuccessMessage);
                Assert.Contains("Looking for a repair NPC", missingMender.StatusMessage);
                Assert.Equal(179u, Get(missingMender, "npcRepairRequiredInnTerritory"));
                Assert.Equal(searchStarted, Get(missingMender, "npcRepairTravelStageStartedUtc"));
                Assert.Equal(commandCount, commands.Count);
            }

            if (ending == "stage timeout")
                Invoke(missingMender, "UpdateNpcRepairTravel", searchStarted.AddSeconds(76));
            else if (ending == "overall timeout")
            {
                Set(missingMender, "startedAtUtc", DateTime.UtcNow.AddSeconds(-121));
                missingMender.Update();
            }
            else
                missingMender.Cancel(ending);

            Assert.False(missingMender.IsRunning);
            Assert.Empty(missingMender.LastSuccessMessage);
            Assert.Contains(ending.EndsWith("timeout") ? "Timed out" : ending, missingMender.LastFailureMessage);
            Assert.Equal(0u, Get(missingMender, "npcRepairRequiredInnTerritory"));
            Assert.Equal("None", Get(missingMender, "npcRepairTravelStage")!.ToString());
        }

        territory = 179;
        var failedEntry = Create();
        Assert.True(failedEntry.StartNpcRepairYesInn("gridania"));
        Invoke(failedEntry, "Complete", "repaired");
        territory = 132;
        Set(failedEntry, "npcRepairTravelStageStartedUtc", DateTime.UtcNow.AddSeconds(-3));
        Invoke(failedEntry, "UpdateNpcRepairInnReturn");
        Assert.False(failedEntry.IsRunning);
        Assert.Contains("No innkeeper", failedEntry.LastFailureMessage);

        territory = 178;
        var rejectedMove = Create();
        Assert.True(rejectedMove.StartNpcRepairYesInn("uldah"));
        rejectMovement = true;
        Invoke(rejectedMove, "SendMoveCommand", Vector3.One, "test mender", true);
        Assert.False(rejectedMove.IsRunning);
        Assert.Contains("rejected", rejectedMove.LastFailureMessage);
        rejectMovement = false;

        // An outside start with missing route data fails instead of substituting another destination.
        territory = 130;
        foreach (var (name, _, _) in cities.Take(3))
        {
            var service = Create();
            Assert.False(service.StartNpcRepairYesInn(name));
            Assert.False(service.IsRunning);
            Assert.NotEmpty(service.LastFailureMessage);
        }
        Assert.DoesNotContain(commands, text => text.StartsWith("/li "));
        Assert.Contains("/vnav stop", commands);
    }

    // In-memory TerritoryType rows exercise the real intended-use check without installed game data.
    private static ExcelSheet<TerritoryType> InnTerritories(uint[] ids)
    {
        var raw = (RawExcelSheet)RuntimeHelpers.GetUninitializedObject(typeof(RawExcelSheet));
        var bytes = new byte[512];
        var page = (ExcelPage)Activator.CreateInstance(typeof(ExcelPage), BindingFlags.Instance | BindingFlags.NonPublic,
            null, [raw, bytes, (ushort)0], null)!;
        var probe = CreateRow<TerritoryType>(page, ids[0]);
        var found = false;
        for (var offset = 0; offset < bytes.Length; offset++)
        {
            bytes[offset] = 2;
            if (probe.TerritoryIntendedUse.RowId == 2) { found = true; break; }
            bytes[offset] = 0;
        }
        Assert.True(found, "TerritoryIntendedUse field must be represented in the test row.");
        var lookupType = typeof(RawExcelSheet).GetNestedType("RowOffsetLookup", BindingFlags.NonPublic)!;
        var lookups = Array.CreateInstance(lookupType, ids.Length);
        var indices = Enumerable.Repeat(-1, (int)ids.Max() + 1).ToArray();
        for (var i = 0; i < ids.Length; i++)
        {
            lookups.SetValue(Activator.CreateInstance(lookupType, ids[i], 0u, (ushort)0, (ushort)1), i);
            indices[ids[i]] = i;
        }
        Set(raw, "_pages", new[] { page });
        Set(raw, "_rowOffsetLookupTable", lookups);
        Set(raw, "_rowIndexLookupArray", indices);
        Set(raw, "_rowIndexLookupDict", FrozenDictionary<int, int>.Empty);
        return new ExcelSheet<TerritoryType>(raw);
    }

    private static T CreateRow<T>(ExcelPage page, uint id) where T : struct, IExcelRow<T> => T.Create(page, 0, id);

    private sealed class TestData(ExcelSheet<TerritoryType> territories, ExcelSheet<Aetheryte>? aetherytes = null,
        ExcelSheet<ENpcBase>? npcs = null, ExcelSheet<PreHandler>? preHandlers = null, ExcelSheet<TopicSelect>? topics = null) : IDataManager
    {
        public Dalamud.Game.ClientLanguage Language => Dalamud.Game.ClientLanguage.English;
        public Lumina.GameData GameData => null!;
        public ExcelModule Excel => null!;
        public bool HasModifiedGameDataFiles => false;
        public ExcelSheet<T> GetExcelSheet<T>(Dalamud.Game.ClientLanguage? language = null, string? name = null)
            where T : struct, IExcelRow<T> => typeof(T) == typeof(TerritoryType) ? (ExcelSheet<T>)(object)territories
                : typeof(T) == typeof(Aetheryte) ? (ExcelSheet<T>)(object)aetherytes!
                : typeof(T) == typeof(ENpcBase) ? (ExcelSheet<T>)(object)npcs!
                : typeof(T) == typeof(PreHandler) ? (ExcelSheet<T>)(object)preHandlers!
                : typeof(T) == typeof(TopicSelect) ? (ExcelSheet<T>)(object)topics! : null!;
        public SubrowExcelSheet<T> GetSubrowExcelSheet<T>(Dalamud.Game.ClientLanguage? language = null, string? name = null)
            where T : struct, IExcelSubrow<T> => null!;
        public Lumina.Data.FileResource? GetFile(string path) => null;
        public T? GetFile<T>(string path) where T : Lumina.Data.FileResource => null;
        public Task<T> GetFileAsync<T>(string path, CancellationToken cancellationToken) where T : Lumina.Data.FileResource => Task.FromResult<T>(null!);
        public bool FileExists(string path) => false;
    }

    private static ExcelSheet<Aetheryte> RepairAetherytes(params (uint Id, bool Main, byte Territory, byte Group)[] entries)
    {
        entries = entries.OrderBy(entry => entry.Id).ToArray();
        var raw = (RawExcelSheet)RuntimeHelpers.GetUninitializedObject(typeof(RawExcelSheet));
        var pages = new ExcelPage[entries.Length];
        var lookupType = typeof(RawExcelSheet).GetNestedType("RowOffsetLookup", BindingFlags.NonPublic)!;
        var lookups = Array.CreateInstance(lookupType, entries.Length);
        var indices = Enumerable.Repeat(-1, (int)entries.Max(entry => entry.Id) + 1).ToArray();
        for (var i = 0; i < entries.Length; i++)
        {
            var entry = entries[i];
            var bytes = new byte[512];
            var page = (ExcelPage)Activator.CreateInstance(typeof(ExcelPage), BindingFlags.Instance | BindingFlags.NonPublic,
                null, [raw, bytes, (ushort)0], null)!;
            var row = CreateRow<Aetheryte>(page, entry.Id);
            WriteField(() => row.Territory.RowId, entry.Territory);
            WriteField(() => row.AethernetGroup, entry.Group);
            if (entry.Main)
            {
                var found = false;
                for (var offset = 0; offset < bytes.Length && !found; offset++)
                for (var bit = 1; bit <= 128 && !found; bit <<= 1)
                {
                    var saved = bytes[offset];
                    bytes[offset] |= (byte)bit;
                    if (row.IsAetheryte) found = true;
                    else bytes[offset] = saved;
                }
                Assert.True(found);
            }
            pages[i] = page;
            lookups.SetValue(Activator.CreateInstance(lookupType, entry.Id, 0u, (ushort)i, (ushort)1), i);
            indices[entry.Id] = i;

            void WriteField(Func<uint> read, byte value)
            {
                for (var offset = 0; offset < bytes.Length; offset++)
                {
                    var saved = bytes[offset];
                    bytes[offset] = 123;
                    if (read() == 123) { bytes[offset] = value; return; }
                    bytes[offset] = saved;
                }
                Assert.Fail("Aetheryte field must be represented in the test row.");
            }
        }
        Set(raw, "_pages", pages);
        Set(raw, "<Count>k__BackingField", entries.Length);
        Set(raw, "_rowOffsetLookupTable", lookups);
        Set(raw, "_rowIndexLookupArray", indices);
        Set(raw, "_rowIndexLookupDict", FrozenDictionary<int, int>.Empty);
        return new ExcelSheet<Aetheryte>(raw);
    }

    // Extend the existing real Lumina/proxy seam with the sale vendor's actual event fields.
    private static ExcelSheet<T> SaleRow<T>(ExcelModule module, uint id, Action<T, byte[]> fill) where T : struct, IExcelRow<T>
    {
        var raw = (RawExcelSheet)RuntimeHelpers.GetUninitializedObject(typeof(RawExcelSheet));
        Set(raw, "<Module>k__BackingField", module);
        var bytes = new byte[2048];
        var page = (ExcelPage)Activator.CreateInstance(typeof(ExcelPage), BindingFlags.Instance | BindingFlags.NonPublic,
            null, [raw, bytes, (ushort)0], null)!;
        fill(CreateRow<T>(page, id), bytes);
        var lookupType = typeof(RawExcelSheet).GetNestedType("RowOffsetLookup", BindingFlags.NonPublic)!;
        var lookups = Array.CreateInstance(lookupType, 1);
        lookups.SetValue(Activator.CreateInstance(lookupType, id, 0u, (ushort)0, (ushort)1), 0);
        Set(raw, "_pages", new[] { page });
        Set(raw, "<Count>k__BackingField", 1);
        Set(raw, "_rowOffsetLookupTable", lookups);
        Set(raw, "_rowIndexLookupArray", Array.Empty<int>());
        Set(raw, "_rowIndexLookupDict", new Dictionary<int, int> { [(int)id] = 0 }.ToFrozenDictionary());
        return new ExcelSheet<T>(raw);
    }

    private static ExcelModule SaleExcelModule()
    {
        // Polymorphic ENpcData references resolve through Lumina's real sheet intervals.
        // Supply only row metadata; no installed game files or purchased-item catalog is loaded.
        var module = (ExcelModule)RuntimeHelpers.GetUninitializedObject(typeof(ExcelModule));
        var gameData = (Lumina.GameData)RuntimeHelpers.GetUninitializedObject(typeof(Lumina.GameData));
        Set(gameData, "<Options>k__BackingField", new Lumina.LuminaOptions());
        Set(module, "<GameData>k__BackingField", gameData);
        foreach (var property in new[] { "AdhocSheetCache", "SheetAttributeCache", "RowRefIntervalCache" })
        {
            var field = typeof(ExcelModule).GetField($"<{property}>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!;
            field.SetValue(module, Activator.CreateInstance(field.FieldType));
        }

        var header = new Lumina.Data.Files.Excel.ExcelHeaderFile();
        Set(header, "<Header>k__BackingField", new Lumina.Data.Structs.Excel.ExcelHeaderHeader
            { Variant = Lumina.Data.Structs.Excel.ExcelVariant.Default });
        var sheetDataType = typeof(ExcelModule).GetNestedType("SheetData", BindingFlags.NonPublic)!;
        var lookupType = typeof(RawExcelSheet).GetNestedType("RowOffsetLookup", BindingFlags.NonPublic)!;
        var sheets = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in typeof(ENpcBase).Assembly.GetTypes())
        {
            if (type.Namespace != typeof(ENpcBase).Namespace) continue;
            var attribute = type.GetCustomAttribute<SheetAttribute>();
            if (attribute?.Name == null) continue;
            var raw = (RawExcelSheet)RuntimeHelpers.GetUninitializedObject(typeof(RawExcelSheet));
            var ids = attribute.Name switch
            {
                "GilShop" => new[] { 0x40001u },
                "PreHandler" => new[] { 0x10000001u },
                "TopicSelect" => new[] { 0x10000002u },
                _ => Array.Empty<uint>(),
            };
            var lookups = Array.CreateInstance(lookupType, ids.Length);
            for (var i = 0; i < ids.Length; i++)
                lookups.SetValue(Activator.CreateInstance(lookupType, ids[i], 0u, (ushort)0, (ushort)1), i);
            Set(raw, "<Module>k__BackingField", module);
            Set(raw, "<Count>k__BackingField", ids.Length);
            Set(raw, "_pages", Array.Empty<ExcelPage>());
            Set(raw, "_rowOffsetLookupTable", lookups);
            Set(raw, "_rowIndexLookupArray", Array.Empty<int>());
            Set(raw, "_rowIndexLookupDict", ids.Select((id, index) => ((int)id, index)).ToFrozenDictionary(pair => pair.Item1, pair => pair.index));
            var sheetData = RuntimeHelpers.GetUninitializedObject(sheetDataType);
            Set(sheetData, "<HeaderFile>k__BackingField", header);
            Set(sheetData, "<LanguageCache>k__BackingField", new Lazy<RawExcelSheet>?[] { new(() => raw) });
            sheets[attribute.Name] = sheetData;
        }
        var freeze = typeof(StartingCityInnRepairRegressionTests).GetMethod(nameof(FreezeSaleSheets), BindingFlags.NonPublic | BindingFlags.Static)!;
        Set(module, "<DefinedSheetCache>k__BackingField", freeze.MakeGenericMethod(sheetDataType).Invoke(null, [sheets])!);
        return module;
    }

    private static object FreezeSaleSheets<T>(Dictionary<string, object> sheets)
        => sheets.ToFrozenDictionary(pair => pair.Key, pair => (T)pair.Value, StringComparer.OrdinalIgnoreCase);

    private static int WriteSaleRowId(byte[] bytes, Func<uint> read, uint value)
    {
        for (var offset = 0; offset <= bytes.Length - sizeof(uint); offset++)
        {
            var saved = bytes.AsSpan(offset, sizeof(uint)).ToArray();
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset), 0x12345678);
            if (read() == 0x12345678)
            {
                BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset), value);
                return offset;
            }
            saved.CopyTo(bytes, offset);
        }
        Assert.Fail("Sale event field must be represented in the real Lumina test row.");
        return -1;
    }

    private sealed unsafe class NpcSaleFixture : IDisposable
    {
        private readonly PropertyInfo piProperty = typeof(Plugin).GetProperty("PluginInterface", BindingFlags.Static | BindingFlags.NonPublic)!;
        private readonly PropertyInfo playerStateProperty = typeof(Plugin).GetProperty("PlayerState", BindingFlags.Static | BindingFlags.NonPublic)!;
        private readonly object? previousPi;
        private readonly object? previousPlayerState;
        public List<string> Commands { get; } = [];
        public UtilityAutomationService Service { get; }
        public Vector3 VendorPosition { get; set; } = Vector3.UnitX;
        public Vector3 PlayerPosition { get; set; }
        public ulong CharacterId { get; set; } = 1;
        public bool LoggedIn { get; set; } = true;
        public bool Loading { get; set; }
        public bool Mounted { get; set; }
        public bool Combat { get; set; }
        public bool NearAetheryte { get; set; }
        public bool DutyOwned { get; set; }
        public bool InnOwned { get; set; }
        public bool AutoRetainerBusy { get; set; }
        public bool MissingAutoRetainer { get; set; }
        public bool StartWorkOnSaleCommand { get; set; }
        public bool LifestreamBusy { get; set; }
        public bool NavigationReady { get; set; } = true;
        public bool Pathfinding { get; set; }
        public bool FollowingPath { get; set; }
        public string? RejectCommand { get; set; }
        public string? BlockingUi { get; set; }
        public int TargetChanges { get; private set; }
        public List<GearCleanupCandidate> GearItems { get; } = [];
        public HashSet<uint> GearProtection { get; } = [];
        public List<GearCleanupCandidate> GearMoved { get; } = [];
        public List<GearCleanupCandidate> GearSold { get; } = [];
        public List<string> SavedConfigurations { get; } = [];
        public bool GearSnapshotAvailable { get; set; } = true;
        public bool GearCompleteOperations { get; set; } = true;
        public int GearBagCapacity { get; set; } = 35;
        public InventoryType GearBagContainer { get; set; } = InventoryType.Inventory1;
        public bool GearMoveChangesNativeBytes { get; set; }
        private ushort gearMoveDestinationSlot;
        public int GearInteractions { get; private set; }
        public bool GearUseMenu { get; set; }
        private bool gearShopVisible;
        private bool gearMenuVisible;
        public GearSaleState GearState { get; set; } = GearSaleState.Ready;
        public long GearGil { get; set; } = 100;
        public bool OwnsGearConfirmation { get; set; } = true;
        public int GearConfirmations { get; private set; }
        public bool NativeGearLifecycle { get; set; }
        public bool GearShopOpen => gearShopVisible;
        private ShopEventHandler nativeGearSale;
        public int NativeGearMode => nativeGearSale.CurrentMode;
        private GearCleanupCandidate? nativeGearItem;
        private long nativeGearQuote = -1;
        public long GearQuotedPrice => NativeGearLifecycle ? nativeGearQuote : 10;
        private bool NativeGearIdle
        {
            get { fixed (ShopEventHandler* state = &nativeGearSale) return DalamudShopPurchaseRuntime.IsGearSaleTransactionIdle(state); }
        }

        public NpcSaleFixture(string handler = "direct", bool repair = false)
        {
            nativeGearSale.CurrentMode = 1;
            nativeGearSale.Info.EventId.Id = 0x40001;
            var module = SaleExcelModule();
            Assert.True(module.GetRawSheet("GilShop").HasRow(0x40001), "The sale module must contain its GilShop interval.");
            Assert.True(module.GetRawSheet("PreHandler").HasRow(0x10000001), "The sale module must contain its PreHandler interval.");
            Assert.True(module.GetRawSheet("TopicSelect").HasRow(0x10000002), "The sale module must contain its TopicSelect interval.");
            var handlerId = handler switch { "pre-handler" => 0x10000001u, "topic" => 0x10000002u, _ => 0x40001u };
            var npcs = SaleRow<ENpcBase>(module, 10, (row, bytes) =>
            {
                WriteSaleRowId(bytes, () => row.ENpcData[0].RowId, handlerId);
                Assert.Equal(handlerId, row.ENpcData[0].RowId);
                if (repair) WriteSaleRowId(bytes, () => row.ENpcData[1].RowId, 720915);
            });
            var preHandlers = handler == "pre-handler" ? SaleRow<PreHandler>(module, handlerId,
                (row, bytes) => WriteSaleRowId(bytes, () => row.Target.RowId, 0x40001)) : null;
            var topics = handler == "topic" ? SaleRow<TopicSelect>(module, handlerId,
                (row, bytes) => WriteSaleRowId(bytes, () => row.Shop[0].RowId, 0x40001)) : null;
            Assert.True(npcs.TryGetRow(10, out var savedNpc), "The synthetic NPC row must be readable.");
            Assert.Equal(handlerId, savedNpc.ENpcData[0].RowId);
            Assert.Equal(handler switch { "pre-handler" => typeof(PreHandler), "topic" => typeof(TopicSelect), _ => typeof(GilShop) }, savedNpc.ENpcData[0].RowType);
            var player = Fake<IPlayerCharacter>((method, _) => method.Name == "get_Position" ? PlayerPosition : Default(method.ReturnType));
            var npc = Fake<IGameObject>((method, _) => method.Name switch
            {
                "get_BaseId" => 10u, "get_GameObjectId" => 100UL, "get_ObjectKind" => ObjectKind.EventNpc,
                "get_IsTargetable" => true, "get_Position" => VendorPosition,
                "get_Name" => new SeString(new TextPayload("Synthetic sale vendor")), _ => Default(method.ReturnType),
            });
            var crystal = Fake<IGameObject>((method, _) => method.Name switch
            {
                "get_ObjectKind" => ObjectKind.Aetheryte, "get_Position" => PlayerPosition, _ => Default(method.ReturnType),
            });
            var objects = Fake<IObjectTable>((method, _) => method.Name switch
            {
                "get_LocalPlayer" => player,
                "GetEnumerator" => ((IEnumerable<IGameObject>)(NearAetheryte ? [npc, crystal] : new[] { npc })).GetEnumerator(),
                _ => Default(method.ReturnType),
            });
            var target = Fake<ITargetManager>((method, _) =>
            {
                if (method.Name == "set_Target") TargetChanges++;
                return Default(method.ReturnType);
            });
            var client = Fake<IClientState>((method, _) => method.Name switch
            {
                "get_IsLoggedIn" => LoggedIn, "get_TerritoryType" => 130u, _ => Default(method.ReturnType),
            });
            var condition = Fake<ICondition>((method, args) => method.Name != "get_Item" ? Default(method.ReturnType)
                : (ConditionFlag)args![0]! switch
                {
                    ConditionFlag.BetweenAreas or ConditionFlag.BetweenAreas51 => Loading,
                    ConditionFlag.Mounted => Mounted,
                    ConditionFlag.InCombat => Combat,
                    _ => false,
                });
            var command = Fake<ICommandManager>((method, args) =>
            {
                if (method.Name != "ProcessCommand") return Default(method.ReturnType);
                var text = (string)args![0]!;
                Commands.Add(text);
                if (text == RejectCommand) return false;
                if (text == "/ays itemsell" && StartWorkOnSaleCommand) AutoRetainerBusy = true;
                if (text == "/vnav stop") Pathfinding = FollowingPath = false;
                return true;
            });
            var pi = Fake<IDalamudPluginInterface>((method, args) =>
            {
                if (method.Name == "SavePluginConfig")
                { SavedConfigurations.Add(Newtonsoft.Json.JsonConvert.SerializeObject(args![0])); return null; }
                if (method.Name != "GetIpcSubscriber") return Default(method.ReturnType);
                var name = (string)args![0]!;
                if (method.GetGenericArguments().Single() == typeof(bool))
                    return Fake<ICallGateSubscriber<bool>>((call, _) => call.Name != "InvokeFunc" ? Default(call.ReturnType) : name switch
                    {
                        "AutoRetainer.PluginState.IsBusy" => MissingAutoRetainer ? throw new InvalidOperationException("Synthetic IPC unavailable") : AutoRetainerBusy,
                        "Lifestream.IsBusy" => LifestreamBusy,
                        "vnavmesh.Nav.IsReady" => NavigationReady,
                        "vnavmesh.SimpleMove.PathfindInProgress" => Pathfinding,
                        "vnavmesh.Path.IsRunning" => FollowingPath,
                        _ => throw new InvalidOperationException(name),
                    });
                return Fake<ICallGateSubscriber<object>>((call, _) =>
                {
                    if (call.Name == "InvokeAction")
                    {
                        Commands.Add(name);
                        if (name == "vnavmesh.Nav.PathfindCancelAll") Pathfinding = false;
                        if (name == "Lifestream.Abort") LifestreamBusy = false;
                    }
                    return Default(call.ReturnType);
                });
            });
            previousPi = piProperty.GetValue(null);
            previousPlayerState = playerStateProperty.GetValue(null);
            piProperty.SetValue(null, pi);
            playerStateProperty.SetValue(null, Fake<IPlayerState>((method, _) => method.Name == "get_ContentId" ? CharacterId : Default(method.ReturnType)));
            Service = new UtilityAutomationService(new TestData(InnTerritories([177, 178, 179]), npcs: npcs, preHandlers: preHandlers, topics: topics),
                objects, target, command, client, condition, new Configuration(), null!, null!, null!,
                () => DutyOwned, () => InnOwned, Fake<IPluginLog>());
            Set(Service, "shopRuntime", Fake<IShopPurchaseRuntime>((method, args) => method.Name switch
            {
                "get_IsAnyShopVisible" => BlockingUi == "shop" || gearShopVisible,
                "get_IsSelectionMenuVisible" => BlockingUi == "selection" || gearMenuVisible,
                "get_HasUnexpectedConfirmation" => BlockingUi == "confirmation", "get_IsTalkVisible" => BlockingUi == "talk",
                "get_HasAutoRetainer" => !MissingAutoRetainer,
                "TryCaptureGearCleanup" => CaptureGear(args!),
                "TryReadGearCleanupSlot" => ReadGear(args!),
                "TryMoveGearCleanupItem" => MoveGear(args!),
                "SubmitGearCleanupItem" => SubmitGear(args!),
                "get_GearSaleGil" => GearGil,
                "get_GearSaleUnitPrice" => GearQuotedPrice,
                "get_GearSaleDiagnostic" => "Synthetic unsupported shop or confirmation state.",
                "get_GearSaleUiSettled" => !gearShopVisible && !gearMenuVisible && BlockingUi == null
                    && (NativeGearLifecycle ? NativeGearIdle : GearState == GearSaleState.Ready),
                "GetGearSaleState" => NativeGearLifecycle ? ReadNativeGearSaleState(args!)
                    : GearState == GearSaleState.Confirmation && !OwnsGearConfirmation ? GearSaleState.Unsupported : GearState,
                "TryAcceptGearSaleConfirmation" => ConfirmGearSale(),
                "TryInteractNpc" => OpenGearVendor(),
                "IsExpectedShopVisible" => gearShopVisible,
                "TrySelectGearSaleMenu" => SelectGearMenu(args!),
                "CloseOwnedGearSaleUi" => CloseGearVendor(),
                _ => Default(method.ReturnType),
            }));
            Assert.True((bool)Invoke(Service, "HasGilShop", 10u)!, $"Synthetic {handler} vendor must resolve through its real Lumina handler.");
        }

        private bool CaptureGear(object?[] args)
        {
            var scope = (GearCleanupScope)args[0]!;
            args[1] = GearItems.Where(item => GearCleanupPolicy.Containers(scope, allUnprotected: true).Contains(item.Container)).ToArray();
            args[2] = new HashSet<uint>(GearProtection);
            return GearSnapshotAvailable;
        }

        private bool ReadGear(object?[] args)
        {
            var container = (InventoryType)args[0]!;
            var slot = (ushort)args[1]!;
            args[2] = GearItems.FirstOrDefault(item => item.Container == container && item.Slot == slot)?.Item
                ?? new InventoryItem { Container = container, Slot = (short)slot };
            return GearSnapshotAvailable;
        }

        private bool MoveGear(object?[] args)
        {
            var item = (GearCleanupCandidate)args[0]!;
            var target = Enumerable.Range(0, GearBagCapacity).FirstOrDefault(index =>
                !GearItems.Any(row => row.Container == GearBagContainer && row.Slot == index), -1);
            args[1] = GearBagContainer;
            args[2] = (ushort)Math.Max(0, target);
            args[3] = target < 0;
            if (target < 0) return false;
            gearMoveDestinationSlot = (ushort)target;
            GearMoved.Add(item);
            if (GearCompleteOperations)
            {
                PublishGearMoveSourceRemoval();
                PublishGearMoveDestination();
            }
            return true;
        }

        public void PublishGearMoveSourceRemoval()
        {
            var item = GearMoved[^1];
            GearItems.RemoveAll(row => row.Container == item.Container && row.Slot == item.Slot);
        }

        public void PublishGearMoveDestination()
        {
            var item = GearMoved[^1];
            var moved = item.Item;
            moved.Container = GearBagContainer;
            moved.Slot = (short)gearMoveDestinationSlot;
            if (GearMoveChangesNativeBytes)
            {
                ((byte*)&moved)[0] ^= 0x40;
                ((byte*)&moved)[0x0F] ^= 0x20;
            }
            GearItems.Add(item with { Container = GearBagContainer, Slot = gearMoveDestinationSlot, Item = moved });
        }

        private GearSaleState SubmitGear(object?[] args)
        {
            if (GearState == GearSaleState.NotReady) return GearSaleState.NotReady;
            if (NativeGearLifecycle)
            {
                var state = ReadNativeGearSaleState(args);
                if (state != GearSaleState.Ready) return state;
            }
            return SellGear(args) ? GearSaleState.Pending : GearSaleState.Unsupported;
        }

        private bool SellGear(object?[] args)
        {
            if (GearState != GearSaleState.Ready || (NativeGearLifecycle && !NativeGearIdle)) return false;
            var item = (GearCleanupCandidate)args[2]!;
            GearSold.Add(item);
            if (NativeGearLifecycle)
            {
                nativeGearItem = item;
                nativeGearQuote = -1;
                nativeGearSale = new ShopEventHandler
                {
                    StartingSell = true, CurrentMode = nativeGearSale.CurrentMode,
                    BuybackTabActive = nativeGearSale.BuybackTabActive,
                };
                nativeGearSale.Info.EventId.Id = 0x40001;
            }
            else if (GearCompleteOperations)
            {
                GearItems.RemoveAll(row => row.Container == item.Container && row.Slot == item.Slot);
                GearGil += 10;
            }
            return true;
        }

        private GearSaleState ReadNativeGearSaleState(object?[] args)
        {
            fixed (ShopEventHandler* state = &nativeGearSale)
            {
                var ownership = DalamudShopPurchaseRuntime.ReadGearSaleHandlerState(state,
                    10, 0x40001, 1, 1, 10, out _);
                if (ownership != GearSaleState.Ready) return ownership;
                var phase = DalamudShopPurchaseRuntime.ReadGearSaleTransactionPhase(state, args[2] as GearCleanupCandidate, out var quote);
                if (quote > 0) nativeGearQuote = quote;
                if (phase == GearSaleState.Pending && state->WaitingForSellConfirm)
                    return OwnsGearConfirmation ? GearSaleState.Confirmation : GearSaleState.Unsupported;
                return phase;
            }
        }

        public void BeginNativeGearSettlement(bool confirmation)
        {
            var item = nativeGearItem!;
            nativeGearSale = new ShopEventHandler
            {
                CurrentMode = nativeGearSale.CurrentMode, BuybackTabActive = nativeGearSale.BuybackTabActive,
                TransactionType = confirmation ? 2 : 0, TransactionItemId = item.Item.ItemId,
                TransactionItemCount = 1, SellInventoryType = item.Container, SellInventorySlot = item.Slot,
                SellPrice = 3 + GearSold.Count, WaitingForSellConfirm = confirmation,
                WaitingForTransactionToFinish = !confirmation,
            };
            nativeGearSale.Info.EventId.Id = 0x40001;
        }

        public void PublishNativeGearItemRemoval()
        {
            GearItems.RemoveAll(row => row.Container == nativeGearItem!.Container && row.Slot == nativeGearItem.Slot);
            nativeGearSale.CurrentMode = 2;
            nativeGearSale.BuybackTabActive = true;
            nativeGearSale.UpdatingBuybackItems = true;
        }
        public void PublishNativeGearGil() => GearGil += nativeGearSale.SellPrice;
        public void FinishNativeGearSettlement()
        {
            nativeGearSale.WaitingForTransactionToFinish = false;
            nativeGearSale.TransactionType = 0;
            nativeGearSale.UpdatingBuybackItems = false;
        }

        private bool OpenGearVendor()
        {
            GearInteractions++;
            gearMenuVisible = GearUseMenu;
            gearShopVisible = !GearUseMenu;
            return true;
        }

        private bool ConfirmGearSale()
        {
            if (!OwnsGearConfirmation || GearSold.Count == 0
                || (NativeGearLifecycle ? !nativeGearSale.WaitingForSellConfirm : GearState != GearSaleState.Confirmation)) return false;
            GearConfirmations++;
            if (NativeGearLifecycle)
            {
                nativeGearSale.WaitingForSellConfirm = false;
                nativeGearSale.WaitingForTransactionToFinish = true;
                nativeGearSale.TransactionType = 0;
                return true;
            }
            var item = GearSold[^1];
            GearItems.RemoveAll(row => row.Container == item.Container && row.Slot == item.Slot);
            GearGil += 10;
            GearState = GearSaleState.Ready;
            return true;
        }

        private bool SelectGearMenu(object?[] args)
        {
            args[3] = ((IReadOnlyList<ShopMenuPathStep>)args[1]!)[^1].HandlerId;
            gearMenuVisible = false;
            gearShopVisible = true;
            return true;
        }

        private object? CloseGearVendor()
        {
            if (NativeGearLifecycle)
            {
                fixed (ShopEventHandler* state = &nativeGearSale)
                    if (DalamudShopPurchaseRuntime.ReadGearSaleHandlerState(state, 10, 0x40001, 1, 1, 10, out _) != GearSaleState.Ready)
                        return null;
            }
            if (NativeGearLifecycle ? NativeGearIdle : GearState == GearSaleState.Ready) gearShopVisible = false;
            return null;
        }

        public void Dispose()
        {
            piProperty.SetValue(null, previousPi);
            playerStateProperty.SetValue(null, previousPlayerState);
        }
    }

    private static object? Get(object target, string name) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);
    private static void Set(object target, string name, object value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static object? Invoke(object target, string name, params object[] args) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static object? Default(Type type) => type == typeof(void) ? null : type.IsValueType ? Activator.CreateInstance(type) : null;
    private static T Fake<T>(Func<MethodInfo, object?[]?, object?>? handler = null) where T : class
    {
        var value = DispatchProxy.Create<T, Stub>();
        ((Stub)(object)value).Handler = handler;
        return value;
    }
    public class Stub : DispatchProxy
    {
        internal Func<MethodInfo, object?[]?, object?>? Handler;
        protected override object? Invoke(MethodInfo? method, object?[]? args) =>
            Handler == null ? Default(method!.ReturnType) : Handler(method!, args);
    }
}
