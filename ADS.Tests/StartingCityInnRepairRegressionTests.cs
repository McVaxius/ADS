using System.Numerics;
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
using Xunit;

namespace ADS.Tests;

[Collection("ADS configuration IPC")]
public sealed class StartingCityInnRepairRegressionTests
{
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

    [Fact]
    public void GearMovePreservesItemsAndReportsPartialCompletionWhenBagsFill()
    {
        using var fixture = new NpcSaleFixture { GearBagCapacity = 1 };
        var first = GearItem(10, InventoryType.ArmoryHead) with { NpcBuyable = false };
        var second = GearItem(11, InventoryType.ArmoryBody);
        fixture.GearItems.AddRange([first, second]);
        Assert.True(fixture.Service.StartGearMove(), fixture.Service.StatusMessage);
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

    private sealed class NpcSaleFixture : IDisposable
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
        public int GearInteractions { get; private set; }
        public bool GearUseMenu { get; set; }
        private bool gearShopVisible;
        private bool gearMenuVisible;
        public GearSaleState GearState { get; set; } = GearSaleState.Ready;

        public NpcSaleFixture(string handler = "direct", bool repair = false)
        {
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
                "TrySellGearCleanupItem" => SellGear(args!),
                "GetGearSaleState" => GearState,
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
            args[1] = GearItems.Where(item => GearCleanupPolicy.Containers(scope).Contains(item.Container)).ToArray();
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
                !GearItems.Any(row => row.Container == InventoryType.Inventory1 && row.Slot == index), -1);
            args[1] = InventoryType.Inventory1;
            args[2] = (ushort)Math.Max(0, target);
            args[3] = target < 0;
            if (target < 0) return false;
            GearMoved.Add(item);
            if (GearCompleteOperations)
            {
                GearItems.RemoveAll(row => row.Container == item.Container && row.Slot == item.Slot);
                var moved = item.Item;
                moved.Container = InventoryType.Inventory1;
                moved.Slot = (short)target;
                GearItems.Add(item with { Container = InventoryType.Inventory1, Slot = (ushort)target, Item = moved });
            }
            return true;
        }

        private bool SellGear(object?[] args)
        {
            if (GearState != GearSaleState.Ready) return false;
            var item = (GearCleanupCandidate)args[2]!;
            GearSold.Add(item);
            if (GearCompleteOperations) GearItems.RemoveAll(row => row.Container == item.Container && row.Slot == item.Slot);
            return true;
        }

        private bool OpenGearVendor()
        {
            GearInteractions++;
            gearMenuVisible = GearUseMenu;
            gearShopVisible = !GearUseMenu;
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
            if (GearState == GearSaleState.Ready) gearShopVisible = false;
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
