using System.Globalization;
using System.Numerics;
using ADS.Models;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace ADS.Services;

internal enum ShopUiValidationState
{
    NotReady,
    Valid,
    Mismatch,
}

internal sealed record ShopUiValidationResult(ShopUiValidationState State, int RuntimeRow, string Message)
{
    public static ShopUiValidationResult NotReady(string message) => new(ShopUiValidationState.NotReady, -1, message);
    public static ShopUiValidationResult Valid(int row, string message = "Runtime shop row validated.")
        => new(ShopUiValidationState.Valid, row, message);
    public static ShopUiValidationResult Mismatch(string message) => new(ShopUiValidationState.Mismatch, -1, message);
}

internal readonly record struct ShopRuntimeNpc(Vector3 Position, float Distance);

internal readonly record struct ShopRuntimeCostValue(uint ItemId, uint Amount);

internal readonly record struct ShopRuntimeExchangeItem(uint ItemId, uint ItemCount);

internal readonly record struct ShopRuntimeGilItem(uint ItemId, int PriceBuy, bool IsHq);

internal static class RegularGilShopRuntimeValidator
{
    public static ShopUiValidationResult Validate(
        uint activeShopId,
        int itemCount,
        ReadOnlySpan<ShopRuntimeGilItem> itemBuffer,
        int visibleItemCount,
        ReadOnlySpan<int> visibleItemBuffer,
        uint expectedShopId,
        uint expectedItemId,
        uint expectedPrice)
    {
        if (activeShopId != expectedShopId)
        {
            return ShopUiValidationResult.Mismatch(
                $"ShopEventHandler shop ID {activeShopId} does not match expected GilShop {expectedShopId}.");
        }

        if (itemCount < 0 || itemCount > itemBuffer.Length)
            return ShopUiValidationResult.Mismatch("ShopEventHandler item count is outside its runtime buffer.");
        if (visibleItemCount < 0 || visibleItemCount > visibleItemBuffer.Length)
            return ShopUiValidationResult.Mismatch("ShopEventHandler visible-item count is outside its runtime buffer.");
        if (itemCount == 0 || visibleItemCount == 0)
            return ShopUiValidationResult.NotReady("ShopEventHandler visible items are not populated yet.");

        var seenItemIndices = new HashSet<int>();
        var matchingVisibleRows = new List<int>();
        for (var visibleRow = 0; visibleRow < visibleItemCount; visibleRow++)
        {
            var itemIndex = visibleItemBuffer[visibleRow];
            if (itemIndex < 0 || itemIndex >= itemCount)
                return ShopUiValidationResult.Mismatch("ShopEventHandler contains an out-of-range visible-item index.");
            if (!seenItemIndices.Add(itemIndex))
                return ShopUiValidationResult.Mismatch("ShopEventHandler contains a duplicate visible-item row.");
            if (itemBuffer[itemIndex].ItemId == expectedItemId)
                matchingVisibleRows.Add(visibleRow);
        }

        if (matchingVisibleRows.Count == 0)
        {
            return ShopUiValidationResult.Mismatch(
                $"ShopEventHandler has no visible row for expected item {expectedItemId}.");
        }

        if (matchingVisibleRows.Count != 1)
        {
            return ShopUiValidationResult.Mismatch(
                $"ShopEventHandler has multiple visible rows for expected item {expectedItemId}; ADS will not guess.");
        }

        var runtimeRow = matchingVisibleRows[0];
        var runtimeItem = itemBuffer[visibleItemBuffer[runtimeRow]];
        if (runtimeItem.IsHq)
            return ShopUiValidationResult.Mismatch("The visible regular-shop item is HQ; ADS expected a non-HQ item.");
        if (runtimeItem.PriceBuy < 0 || (uint)runtimeItem.PriceBuy != expectedPrice)
        {
            return ShopUiValidationResult.Mismatch(
                $"The visible regular-shop price {runtimeItem.PriceBuy} does not match expected gil price {expectedPrice}.");
        }

        return ShopUiValidationResult.Valid(
            runtimeRow,
            $"ShopEventHandler validated GilShop {expectedShopId}, item {expectedItemId}, price {expectedPrice}, visible row {runtimeRow}.");
    }
}

internal static class ShopRuntimeCostMatcher
{
    public static bool Matches(ReadOnlySpan<ShopRuntimeCostValue> runtimeCosts, IReadOnlyList<ShopCurrencyCost> expected)
    {
        if (runtimeCosts.Length is < 1 or > 3 || expected.Count == 0)
            return false;

        var unmatched = expected.ToList();
        var populatedRuntimeCosts = 0;
        foreach (var runtime in runtimeCosts)
        {
            if (runtime.ItemId == 0 && runtime.Amount == 0)
                continue;
            populatedRuntimeCosts++;
            var matchIndex = unmatched.FindIndex(cost => runtime.ItemId == cost.ItemId && runtime.Amount == cost.AmountPerTransaction);
            if (matchIndex < 0)
                return false;
            unmatched.RemoveAt(matchIndex);
        }

        return populatedRuntimeCosts == expected.Count && unmatched.Count == 0;
    }
}

internal static class ExchangeShopRuntimeValidator
{
    public static ShopUiValidationResult Validate(
        string runtimeShopName,
        ReadOnlySpan<ShopRuntimeExchangeItem> runtimeReceives,
        ReadOnlySpan<ShopRuntimeCostValue> runtimeCosts,
        string expectedShopName,
        uint expectedItemId,
        uint expectedReceiveCount,
        IReadOnlyList<ShopCurrencyCost> expectedCosts)
    {
        if (string.IsNullOrWhiteSpace(runtimeShopName))
            return ShopUiValidationResult.NotReady("AgentShop has not populated its active SpecialShop name yet.");
        if (!string.Equals(runtimeShopName, expectedShopName, StringComparison.Ordinal))
        {
            return ShopUiValidationResult.Mismatch(
                $"AgentShop SpecialShop name '{runtimeShopName}' does not match expected '{expectedShopName}'.");
        }

        if (runtimeReceives.Length == 0)
            return ShopUiValidationResult.NotReady("AgentShop receive rows are not populated yet.");
        if (runtimeCosts.Length == 0)
            return ShopUiValidationResult.NotReady("AgentShop cost rows are not populated yet.");
        if (runtimeCosts.Length % runtimeReceives.Length != 0)
            return ShopUiValidationResult.Mismatch("AgentShop cost rows do not form a complete rectangular receive/cost layout.");

        var costsPerReceive = runtimeCosts.Length / runtimeReceives.Length;
        if (costsPerReceive is < 1 or > 3)
            return ShopUiValidationResult.Mismatch("AgentShop does not expose one to three cost slots per receive row.");

        var matchingRows = new List<int>();
        for (var row = 0; row < runtimeReceives.Length; row++)
        {
            var receive = runtimeReceives[row];
            if (receive.ItemId != expectedItemId || receive.ItemCount != expectedReceiveCount)
                continue;
            if (ShopRuntimeCostMatcher.Matches(runtimeCosts.Slice(row * costsPerReceive, costsPerReceive), expectedCosts))
                matchingRows.Add(row);
        }

        return matchingRows.Count switch
        {
            1 => ShopUiValidationResult.Valid(
                matchingRows[0],
                $"AgentShop validated SpecialShop '{expectedShopName}', item {expectedItemId}, bundle {expectedReceiveCount}, and exact costs at row {matchingRows[0]}."),
            0 => ShopUiValidationResult.Mismatch("The active AgentShop has no row matching the sheet item, non-HQ identity, bundle count, currency IDs, and costs."),
            _ => ShopUiValidationResult.Mismatch("The active AgentShop has multiple indistinguishable matching rows; ADS will not guess."),
        };
    }
}

internal interface IShopPurchaseRuntime
{
    bool IsLoggedIn { get; }
    bool IsBetweenAreas { get; }
    bool IsPlayerAvailable { get; }
    uint CurrentTerritoryId { get; }
    Vector3 PlayerPosition { get; }
    bool HasVnavmesh { get; }
    bool HasLifestream { get; }
    bool HasUnexpectedConfirmation { get; }
    bool IsSelectionMenuVisible { get; }
    bool IsAnyShopVisible { get; }

    bool IsAetheryteUnlocked(uint aetheryteId);
    bool IsQuestComplete(uint questId);
    long GetItemCount(uint itemId);
    long GetAvailableCurrency(ShopCurrencyCost currency);
    long GetInventoryCapacity(uint itemId, uint stackSize);
    bool TryResolveFloor(Vector3 approximatePosition, out Vector3 floorPosition);
    bool TryTeleport(ResolvedShopRoute route);
    bool TryMove(Vector3 destination, string label);
    void StopNavigation();
    bool TryGetNpc(uint npcId, out ShopRuntimeNpc npc);
    bool TryInteractNpc(uint npcId);
    bool IsExpectedShopVisible(ShopOfferKind kind);
    bool TrySelectMenu(int index);
    ShopUiValidationResult ValidateShopUi(EvaluatedShopOffer offer);
    bool SubmitPurchase(EvaluatedShopOffer offer, int runtimeRow, int transactionCount);
    void CloseOwnedShopUi();
}

internal sealed unsafe class DalamudShopPurchaseRuntime(
    IObjectTable objectTable,
    ITargetManager targetManager,
    ICommandManager commandManager,
    IClientState clientState,
    ICondition condition,
    IPluginLog log) : IShopPurchaseRuntime
{
    private const string PointOnFloorIpc = "vnavmesh.Query.Mesh.PointOnFloor";
    private const float FloorQueryHalfExtent = 5f;
    private DateTime nextFloorQueryFailureLogUtc = DateTime.MinValue;

    private interface IShopUiAdapter
    {
        ShopOfferKind Kind { get; }
        string AddonName { get; }
        ShopUiValidationResult Validate(EvaluatedShopOffer offer);
        bool Submit(int runtimeRow, int transactionCount);
    }

    private sealed class GilShopUiAdapter : IShopUiAdapter
    {
        public ShopOfferKind Kind => ShopOfferKind.GilShop;
        public string AddonName => "Shop";
        public ShopUiValidationResult Validate(EvaluatedShopOffer offer)
            => ValidateRegularGilShop(offer);
        public bool Submit(int runtimeRow, int transactionCount)
            => GameInteractionHelper.TryFireAddonCallback(AddonName, true, 0, runtimeRow, transactionCount);
    }

    private sealed class ItemExchangeShopUiAdapter : IShopUiAdapter
    {
        public ShopOfferKind Kind => ShopOfferKind.SpecialShopItem;
        public string AddonName => "ShopExchangeItem";
        public ShopUiValidationResult Validate(EvaluatedShopOffer offer)
            => ValidateExchangeShop(offer);
        public bool Submit(int runtimeRow, int transactionCount)
            => GameInteractionHelper.TryFireAddonCallback(AddonName, true, 0, runtimeRow, transactionCount);
    }

    private sealed class TomestoneExchangeShopUiAdapter : IShopUiAdapter
    {
        public ShopOfferKind Kind => ShopOfferKind.SpecialShopTomestone;
        public string AddonName => "ShopExchangeCurrency";
        public ShopUiValidationResult Validate(EvaluatedShopOffer offer)
            => ValidateExchangeShop(offer);
        public bool Submit(int runtimeRow, int transactionCount)
            => GameInteractionHelper.TryFireAddonCallback(AddonName, true, 0, runtimeRow, transactionCount, 0);
    }

    private static readonly IShopUiAdapter[] UiAdapters =
    [
        new GilShopUiAdapter(),
        new ItemExchangeShopUiAdapter(),
        new TomestoneExchangeShopUiAdapter(),
    ];

    private static readonly InventoryType[] RegularInventoryTypes =
    [
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
    ];

    public bool IsLoggedIn => clientState.IsLoggedIn;
    public bool IsBetweenAreas => condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51];
    public bool IsPlayerAvailable => IsLoggedIn
        && objectTable.LocalPlayer != null
        && !IsBetweenAreas
        && !condition[ConditionFlag.OccupiedInCutSceneEvent]
        && !condition[ConditionFlag.WatchingCutscene]
        && !condition[ConditionFlag.RidingPillion];
    public uint CurrentTerritoryId => clientState.TerritoryType;
    public Vector3 PlayerPosition => objectTable.LocalPlayer?.Position ?? default;
    public bool HasVnavmesh => IsPluginLoaded("vnavmesh", "vnav");
    public bool HasLifestream => IsPluginLoaded("Lifestream");
    public bool HasUnexpectedConfirmation => GameInteractionHelper.IsAddonVisible("SelectYesno");
    public bool IsSelectionMenuVisible => GameInteractionHelper.IsAddonVisible("SelectIconString")
        || GameInteractionHelper.IsAddonVisible("SelectString");
    public bool IsAnyShopVisible => UiAdapters.Any(adapter => GameInteractionHelper.IsAddonVisible(adapter.AddonName));

    public bool IsAetheryteUnlocked(uint aetheryteId)
    {
        try
        {
            for (var index = 0; index < Plugin.AetheryteList.Length; index++)
            {
                var entry = Plugin.AetheryteList[index];
                if (entry?.AetheryteId == aetheryteId)
                    return true;
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[ADS][Shop] Failed to inspect unlocked aetherytes.");
        }

        return false;
    }

    public bool IsQuestComplete(uint questId)
    {
        try
        {
            return questId == 0 || QuestManager.IsQuestComplete(questId);
        }
        catch
        {
            return false;
        }
    }

    public long GetItemCount(uint itemId)
    {
        try
        {
            var manager = InventoryManager.Instance();
            return manager == null
                ? 0
                : Math.Max(0, manager->GetInventoryItemCount(itemId, false, false, false));
        }
        catch
        {
            return 0;
        }
    }

    public long GetAvailableCurrency(ShopCurrencyCost currency)
    {
        try
        {
            var manager = InventoryManager.Instance();
            if (manager == null)
                return 0;
            return currency.Kind switch
            {
                ShopCurrencyKind.Gil => manager->GetGil(),
                ShopCurrencyKind.Item => Math.Max(0, manager->GetInventoryItemCount(currency.ItemId, false, false, false)),
                ShopCurrencyKind.Tomestone => manager->GetTomestoneCount(currency.ItemId),
                _ => 0,
            };
        }
        catch
        {
            return 0;
        }
    }

    public long GetInventoryCapacity(uint itemId, uint stackSize)
    {
        if (itemId == 0 || stackSize == 0)
            return 0;

        try
        {
            long capacity = 0;
            var manager = InventoryManager.Instance();
            if (manager == null)
                return 0;
            foreach (var inventoryType in RegularInventoryTypes)
            {
                var container = manager->GetInventoryContainer(inventoryType);
                if (container == null)
                    continue;
                for (var index = 0; index < container->Size; index++)
                {
                    var slot = container->GetInventorySlot(index);
                    if (slot == null || slot->ItemId == 0)
                    {
                        capacity = checked(capacity + stackSize);
                        continue;
                    }

                    if (DesynthPolicyService.NormalizeBaseItemId(slot->ItemId) != itemId || slot->Quantity >= stackSize)
                        continue;
                    capacity = checked(capacity + stackSize - slot->Quantity);
                }
            }

            return capacity;
        }
        catch (OverflowException)
        {
            return long.MaxValue;
        }
        catch
        {
            return 0;
        }
    }

    public bool TryResolveFloor(Vector3 approximatePosition, out Vector3 floorPosition)
    {
        floorPosition = default;
        if (!float.IsFinite(approximatePosition.X) || !float.IsFinite(approximatePosition.Z))
            return false;

        try
        {
            var probe = new Vector3(approximatePosition.X, 1024f, approximatePosition.Z);
            var result = Plugin.PluginInterface
                .GetIpcSubscriber<Vector3, bool, float, Vector3?>(PointOnFloorIpc)
                .InvokeFunc(probe, false, FloorQueryHalfExtent);
            if (result is not { } resolved
                || !float.IsFinite(resolved.X)
                || !float.IsFinite(resolved.Y)
                || !float.IsFinite(resolved.Z))
            {
                return false;
            }

            floorPosition = resolved;
            return true;
        }
        catch (Exception ex)
        {
            var now = DateTime.UtcNow;
            if (now >= nextFloorQueryFailureLogUtc)
            {
                nextFloorQueryFailureLogUtc = now + TimeSpan.FromSeconds(5);
                log.Debug(ex, "[ADS][Shop] vnavmesh floor query failed.");
            }
            return false;
        }
    }

    public bool TryTeleport(ResolvedShopRoute route)
    {
        if (!route.RequiresTeleport || string.IsNullOrWhiteSpace(route.AetheryteName))
            return false;
        return GameInteractionHelper.TrySendChatCommand(commandManager, $"/li {route.AetheryteName}", log);
    }

    public bool TryMove(Vector3 destination, string label)
    {
        var command = string.Format(
            CultureInfo.InvariantCulture,
            "/vnav moveto {0:F2} {1:F2} {2:F2}",
            destination.X,
            destination.Y,
            destination.Z);
        var sent = GameInteractionHelper.TrySendChatCommand(commandManager, command, log);
        if (sent)
            log.Information("[ADS][Shop] Moving toward {Label} at {X:F2},{Y:F2},{Z:F2}.", label, destination.X, destination.Y, destination.Z);
        return sent;
    }

    public void StopNavigation()
        => GameInteractionHelper.TrySendChatCommand(commandManager, "/vnav stop", log);

    public bool TryGetNpc(uint npcId, out ShopRuntimeNpc npc)
    {
        npc = default;
        var player = objectTable.LocalPlayer;
        if (player == null)
            return false;

        IGameObject? nearest = null;
        var nearestDistance = float.MaxValue;
        foreach (var gameObject in objectTable)
        {
            if (gameObject == null
                || gameObject.ObjectKind != ObjectKind.EventNpc
                || gameObject.BaseId != npcId
                || !gameObject.IsTargetable)
            {
                continue;
            }

            var distance = Vector3.Distance(player.Position, gameObject.Position);
            if (distance >= nearestDistance)
                continue;
            nearest = gameObject;
            nearestDistance = distance;
        }

        if (nearest == null)
            return false;
        npc = new ShopRuntimeNpc(nearest.Position, nearestDistance);
        return true;
    }

    public bool TryInteractNpc(uint npcId)
    {
        if (condition[ConditionFlag.Mounted])
        {
            GameInteractionHelper.TryUseGeneralAction(23, log);
            return false;
        }

        IGameObject? nearest = null;
        var nearestDistance = float.MaxValue;
        foreach (var gameObject in objectTable)
        {
            if (gameObject == null
                || gameObject.ObjectKind != ObjectKind.EventNpc
                || gameObject.BaseId != npcId
                || !gameObject.IsTargetable)
            {
                continue;
            }

            var distance = objectTable.LocalPlayer == null
                ? float.MaxValue
                : Vector3.Distance(objectTable.LocalPlayer.Position, gameObject.Position);
            if (distance >= nearestDistance)
                continue;
            nearest = gameObject;
            nearestDistance = distance;
        }

        return nearest != null && GameInteractionHelper.TryInteractWithObject(targetManager, nearest, log);
    }

    public bool IsExpectedShopVisible(ShopOfferKind kind)
        => GameInteractionHelper.IsAddonVisible(GetAdapter(kind).AddonName);

    public bool TrySelectMenu(int index)
    {
        if (index < 0)
            return false;
        if (GameInteractionHelper.IsAddonVisible("SelectIconString"))
            return GameInteractionHelper.TryFireAddonCallback("SelectIconString", true, index);
        return GameInteractionHelper.IsAddonVisible("SelectString")
            && GameInteractionHelper.TryFireAddonCallback("SelectString", true, index);
    }

    public ShopUiValidationResult ValidateShopUi(EvaluatedShopOffer offer)
    {
        if (HasUnexpectedConfirmation)
            return ShopUiValidationResult.Mismatch("An unexpected confirmation dialog appeared; ADS will not accept it.");
        var adapter = GetAdapter(offer.Offer.Kind);
        var addon = adapter.AddonName;
        if (!GameInteractionHelper.IsAddonVisible(addon))
            return ShopUiValidationResult.NotReady($"Waiting for {addon}.");

        try
        {
            return adapter.Validate(offer);
        }
        catch (Exception ex)
        {
            return ShopUiValidationResult.Mismatch($"{adapter.AddonName} runtime validation failed: {ex.Message}");
        }
    }

    public bool SubmitPurchase(EvaluatedShopOffer offer, int runtimeRow, int transactionCount)
    {
        if (transactionCount is < 1 or > 99 || runtimeRow < 0 || HasUnexpectedConfirmation)
            return false;
        return GetAdapter(offer.Offer.Kind).Submit(runtimeRow, transactionCount);
    }

    public void CloseOwnedShopUi()
    {
        foreach (var adapter in UiAdapters)
            GameInteractionHelper.TryCloseAddon(adapter.AddonName, log);
        GameInteractionHelper.TryCloseAddon("SelectIconString", log);
        GameInteractionHelper.TryCloseAddon("SelectString", log);
    }

    private static ShopUiValidationResult ValidateRegularGilShop(EvaluatedShopOffer offer)
    {
        if (offer.Offer.Currencies.Count != 1
            || offer.Offer.Currencies[0] is not { Kind: ShopCurrencyKind.Gil, ItemId: 1 } gil)
        {
            return ShopUiValidationResult.Mismatch("The regular GilShop offer does not contain one exact gil cost.");
        }

        var proxy = ShopEventHandler.AgentProxy.Instance();
        if (proxy == null)
            return ShopUiValidationResult.NotReady("ShopEventHandler.AgentProxy is unavailable.");
        var handler = proxy->Handler;
        if (handler == null)
            return ShopUiValidationResult.NotReady("ShopEventHandler.AgentProxy has no active shop handler.");

        var nativeItems = handler->Items;
        var items = new ShopRuntimeGilItem[nativeItems.Length];
        for (var index = 0; index < nativeItems.Length; index++)
        {
            var nativeItem = nativeItems[index];
            items[index] = new ShopRuntimeGilItem(nativeItem.ItemId, nativeItem.PriceBuy, nativeItem.IsHQ);
        }

        return RegularGilShopRuntimeValidator.Validate(
            handler->EventHandler.Info.EventId.Id,
            handler->ItemsCount,
            items,
            handler->VisibleItemsCount,
            handler->VisibleItems,
            offer.Offer.ShopId,
            offer.Offer.ReceiveItemId,
            gil.AmountPerTransaction);
    }

    private static ShopUiValidationResult ValidateExchangeShop(EvaluatedShopOffer offer)
    {
        var agent = AgentShop.Instance();
        if (agent == null)
            return ShopUiValidationResult.NotReady("AgentShop is unavailable.");
        if (!agent->IsAgentActive())
            return ShopUiValidationResult.NotReady("AgentShop is not active.");
        if (!agent->IsAddonReady())
            return ShopUiValidationResult.NotReady("AgentShop addon data is not ready.");
        var receives = agent->ItemReceiveSpan;
        var costs = agent->ItemCostSpan;
        var runtimeReceives = new ShopRuntimeExchangeItem[receives.Length];
        for (var index = 0; index < receives.Length; index++)
            runtimeReceives[index] = new ShopRuntimeExchangeItem(receives[index].ItemId, receives[index].ItemCount);
        var runtimeCosts = new ShopRuntimeCostValue[costs.Length];
        for (var index = 0; index < costs.Length; index++)
            runtimeCosts[index] = new ShopRuntimeCostValue(costs[index].ItemId, costs[index].ItemCount);

        return ExchangeShopRuntimeValidator.Validate(
            agent->ShopName.ToString(),
            runtimeReceives,
            runtimeCosts,
            offer.Offer.ShopName,
            offer.Offer.ReceiveItemId,
            offer.Offer.ReceiveCount,
            offer.Offer.Currencies);
    }

    private static IShopUiAdapter GetAdapter(ShopOfferKind kind)
        => UiAdapters.First(adapter => adapter.Kind == kind);

    private static bool IsPluginLoaded(params string[] names)
    {
        try
        {
            return Plugin.PluginInterface.InstalledPlugins.Any(plugin =>
                plugin.IsLoaded
                && names.Any(name =>
                    string.Equals(plugin.InternalName, name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(plugin.Name, name, StringComparison.OrdinalIgnoreCase)
                    || plugin.Name.Contains(name, StringComparison.OrdinalIgnoreCase)));
        }
        catch
        {
            return false;
        }
    }
}
