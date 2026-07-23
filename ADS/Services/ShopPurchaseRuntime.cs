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
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

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
    byte CurrentGrandCompany => 0;
    byte CurrentGrandCompanyRank => 0;
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
    bool TrySelectMenu(ShopMenuPathStep step, uint npcId) => TrySelectMenu(step.Index);
    ShopUiValidationResult ValidateShopUi(EvaluatedShopOffer offer);
    bool SubmitPurchase(EvaluatedShopOffer offer, int runtimeRow, int transactionCount);
    bool IsOwnedConfirmationPending(EvaluatedShopOffer offer, int transactionCount) => false;
    bool TryAcceptOwnedConfirmation(EvaluatedShopOffer offer, int transactionCount) => false;
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
    private string inclusionRouteKey = string.Empty;
    private int inclusionSelectionStage;
    private string grandCompanyRouteKey = string.Empty;
    private int grandCompanySelectionStage;
    private ShopConfirmationToken? confirmationToken;
    private string? readableOwnedSelectYesNoPrompt;

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

    private sealed class MixedExchangeShopUiAdapter : IShopUiAdapter
    {
        public ShopOfferKind Kind => ShopOfferKind.SpecialShopMixed;
        public string AddonName => "ShopExchangeItem";
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
        new MixedExchangeShopUiAdapter(),
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
    public byte CurrentGrandCompany
    {
        get
        {
            var state = PlayerState.Instance();
            return state == null ? (byte)0 : state->GrandCompany;
        }
    }
    public byte CurrentGrandCompanyRank
    {
        get
        {
            var state = PlayerState.Instance();
            return state == null ? (byte)0 : state->GetGrandCompanyRank();
        }
    }
    public Vector3 PlayerPosition => objectTable.LocalPlayer?.Position ?? default;
    public bool HasVnavmesh => IsPluginLoaded("vnavmesh", "vnav");
    public bool HasLifestream => IsPluginLoaded("Lifestream");
    public bool HasUnexpectedConfirmation => GameInteractionHelper.IsAddonVisible("SelectYesno")
        || GameInteractionHelper.IsAddonVisible("ShopExchangeItemDialog")
        || GameInteractionHelper.IsAddonVisible("ShopExchangeCurrencyDialog");
    public bool IsSelectionMenuVisible => GameInteractionHelper.IsAddonVisible("SelectIconString")
        || GameInteractionHelper.IsAddonVisible("SelectString");
    public bool IsAnyShopVisible => UiAdapters.Any(adapter => GameInteractionHelper.IsAddonVisible(adapter.AddonName))
        || GameInteractionHelper.IsAddonVisible("InclusionShop")
        || GameInteractionHelper.IsAddonVisible("GrandCompanyExchange")
        || GameInteractionHelper.IsAddonVisible("FreeCompanyCreditShop")
        || GameInteractionHelper.IsAddonVisible("ShopExchangeItemDialog")
        || GameInteractionHelper.IsAddonVisible("ShopExchangeCurrencyDialog");

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
                ShopCurrencyKind.CompanySeal
                    or ShopCurrencyKind.Mgp
                    or ShopCurrencyKind.WolfMark
                    or ShopCurrencyKind.AlliedSeal
                    or ShopCurrencyKind.CurrencyManager => GetManagedCurrency(currency.ItemId),
                ShopCurrencyKind.FreeCompanyCredit => TryGetLiveFreeCompanyCredits(out var credits) ? credits : -1,
                _ => 0,
            };
        }
        catch
        {
            return 0;
        }
    }

    private static long GetManagedCurrency(uint itemId)
    {
        var manager = CurrencyManager.Instance();
        return manager == null ? 0 : manager->GetItemCount(itemId);
    }

    private static bool TryGetLiveFreeCompanyCredits(out long credits)
    {
        credits = 0;
        var addon = RaptureAtkUnitManager.Instance()->GetAddonByName("FreeCompanyCreditShop");
        if (addon == null || !addon->IsVisible || addon->AtkValues == null || addon->AtkValuesCount <= 9)
            return false;
        credits = ReadUnsigned(addon->AtkValues[3]);
        return credits >= 0;
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
        => kind switch
        {
            ShopOfferKind.SpecialShopMixed => GameInteractionHelper.IsAddonVisible("ShopExchangeItem")
                || GameInteractionHelper.IsAddonVisible("ShopExchangeCurrency"),
            ShopOfferKind.InclusionShop => GameInteractionHelper.IsAddonVisible("InclusionShop"),
            ShopOfferKind.GrandCompanyShop => GameInteractionHelper.IsAddonVisible("GrandCompanyExchange"),
            ShopOfferKind.FreeCompanyShop => GameInteractionHelper.IsAddonVisible("FreeCompanyCreditShop"),
            _ => GameInteractionHelper.IsAddonVisible(GetAdapter(kind).AddonName),
        };

    public bool TrySelectMenu(int index)
    {
        if (index < 0)
            return false;
        if (GameInteractionHelper.IsAddonVisible("SelectIconString"))
            return GameInteractionHelper.TryFireAddonCallback("SelectIconString", true, index);
        return GameInteractionHelper.IsAddonVisible("SelectString")
            && GameInteractionHelper.TryFireAddonCallback("SelectString", true, index);
    }

    public bool TrySelectMenu(ShopMenuPathStep step, uint npcId)
    {
        if (step.Kind is ShopMenuPathStepKind.InclusionPage or ShopMenuPathStepKind.InclusionSubpage)
            return false;

        try
        {
            var selector = EventHandlerSelector.Instance();
            if (selector == null || selector->Target == null || selector->OptionsCount is < 1 or > 32)
                return false;

            var options = new ShopRuntimeMenuOption[selector->OptionsCount];
            for (var index = 0; index < selector->OptionsCount; index++)
            {
                var option = selector->Options[index];
                options[index] = new ShopRuntimeMenuOption(
                    option.Handler == null ? 0 : option.Handler->Info.EventId.Id,
                    option.LocalIndex);
            }

            if (!ShopMenuRouteResolver.TryResolveVisibleIndex(
                    npcId,
                    selector->Target->BaseId,
                    step,
                    options,
                    out var liveIndex,
                    out var resolutionDiagnostic))
            {
                log.Warning("[ADS][Shop] Live menu resolution rejected: {Diagnostic}", resolutionDiagnostic);
                return false;
            }

            log.Debug("[ADS][Shop] {Diagnostic}", resolutionDiagnostic);
            return TrySelectMenu(liveIndex);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[ADS][Shop] Failed to resolve the selected NPC's live event-handler menu.");
            return false;
        }
    }

    public ShopUiValidationResult ValidateShopUi(EvaluatedShopOffer offer)
    {
        if (HasUnexpectedConfirmation)
            return ShopUiValidationResult.Mismatch("An unexpected confirmation dialog appeared; ADS will not accept it.");
        if (offer.Offer.Kind == ShopOfferKind.InclusionShop)
            return ValidateInclusionShop(offer);
        if (offer.Offer.Kind == ShopOfferKind.GrandCompanyShop)
            return ValidateGrandCompanyShop(offer);
        if (offer.Offer.Kind == ShopOfferKind.FreeCompanyShop)
            return ValidateFreeCompanyShop(offer);
        if (offer.Offer.Kind == ShopOfferKind.SpecialShopMixed)
        {
            if (!GameInteractionHelper.IsAddonVisible("ShopExchangeItem")
                && !GameInteractionHelper.IsAddonVisible("ShopExchangeCurrency"))
                return ShopUiValidationResult.NotReady("Waiting for a supported mixed-cost AgentShop addon.");
            return ValidateExchangeShop(offer);
        }

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
        var accepted = offer.Offer.Kind switch
        {
            ShopOfferKind.InclusionShop => SubmitInclusionPurchase(offer, runtimeRow, transactionCount),
            ShopOfferKind.GrandCompanyShop => SubmitGrandCompanyPurchase(offer, runtimeRow, transactionCount),
            ShopOfferKind.FreeCompanyShop => SubmitFreeCompanyPurchase(offer, runtimeRow, transactionCount),
            ShopOfferKind.SpecialShopMixed when GameInteractionHelper.IsAddonVisible("ShopExchangeCurrency")
                => GameInteractionHelper.TryFireAddonCallback(
                    "ShopExchangeCurrency", true, 0, runtimeRow, transactionCount, 0),
            ShopOfferKind.SpecialShopMixed => GameInteractionHelper.TryFireAddonCallback(
                "ShopExchangeItem", true, 0, runtimeRow, transactionCount, 0),
            _ => GetAdapter(offer.Offer.Kind).Submit(runtimeRow, transactionCount),
        };
        if (accepted)
        {
            readableOwnedSelectYesNoPrompt = null;
            confirmationToken = new ShopConfirmationToken(offer, transactionCount, DateTime.UtcNow);
        }
        return accepted;
    }

    public bool IsOwnedConfirmationPending(EvaluatedShopOffer offer, int transactionCount)
    {
        readableOwnedSelectYesNoPrompt = null;
        var token = confirmationToken;
        if (token is not { IsConsumed: false }
            || transactionCount <= 0
            || token.ItemId != offer.Offer.ReceiveItemId
            || token.Quantity != checked((int)((long)offer.Offer.ReceiveCount * transactionCount))
            || !GameInteractionHelper.IsAddonVisible("SelectYesno"))
            return false;

        if (!GameInteractionHelper.TryGetSelectYesNoPromptText(Plugin.GameGui, out var prompt)
            || string.IsNullOrWhiteSpace(prompt))
            return true;

        readableOwnedSelectYesNoPrompt = prompt;
        return false;
    }

    public bool TryAcceptOwnedConfirmation(EvaluatedShopOffer offer, int transactionCount)
    {
        var token = confirmationToken;
        if (token == null || token.IsConsumed || transactionCount <= 0)
            return false;
        var now = DateTime.UtcNow;
        if (GameInteractionHelper.IsAddonVisible("SelectYesno"))
        {
            var prompt = readableOwnedSelectYesNoPrompt;
            readableOwnedSelectYesNoPrompt = null;
            if (string.IsNullOrWhiteSpace(prompt)
                && (!GameInteractionHelper.TryGetSelectYesNoPromptText(Plugin.GameGui, out prompt)
                    || string.IsNullOrWhiteSpace(prompt)))
                return false;
            if (!token.TryConsumePrompt(prompt, now))
                return false;
            return GameInteractionHelper.TrySelectYesNo(true, Plugin.GameGui, log: log);
        }

        var costs = offer.Offer.Currencies.ToDictionary(
            currency => currency.Identity,
            currency => checked((long)currency.AmountPerTransaction * transactionCount));
        if (GameInteractionHelper.IsAddonVisible("ShopExchangeCurrencyDialog"))
        {
            var addon = RaptureAtkUnitManager.Instance()->GetAddonByName("ShopExchangeCurrencyDialog");
            if (addon == null || addon->UldManager.NodeListCount <= 8 || addon->UldManager.NodeList[8] == null)
                return false;
            var input = addon->UldManager.NodeList[8]->GetAsAtkComponentNumericInput();
            if (input == null)
                return false;
            if (input->Value != token.Quantity)
            {
                input->SetValue(token.Quantity);
                return true;
            }
            if (!token.TryConsumeStructured(token.ItemId, token.Quantity, costs, now))
                return false;
            return GameInteractionHelper.TryClickAddonNodeButton("ShopExchangeCurrencyDialog", 17, Plugin.GameGui, log);
        }

        if (GameInteractionHelper.IsAddonVisible("ShopExchangeItemDialog"))
        {
            if (!token.TryConsumeStructured(token.ItemId, token.Quantity, costs, now))
                return false;
            return GameInteractionHelper.TryClickAddonNodeButton("ShopExchangeItemDialog", 18, Plugin.GameGui, log);
        }
        return false;
    }

    public void CloseOwnedShopUi()
    {
        foreach (var adapter in UiAdapters)
            GameInteractionHelper.TryCloseAddon(adapter.AddonName, log);
        GameInteractionHelper.TryCloseAddon("InclusionShop", log);
        GameInteractionHelper.TryCloseAddon("GrandCompanyExchange", log);
        GameInteractionHelper.TryCloseAddon("FreeCompanyCreditShop", log);
        GameInteractionHelper.TryCloseAddon("ShopExchangeItemDialog", log);
        GameInteractionHelper.TryCloseAddon("ShopExchangeCurrencyDialog", log);
        GameInteractionHelper.TryCloseAddon("SelectIconString", log);
        GameInteractionHelper.TryCloseAddon("SelectString", log);
        inclusionRouteKey = string.Empty;
        inclusionSelectionStage = 0;
        grandCompanyRouteKey = string.Empty;
        grandCompanySelectionStage = 0;
        readableOwnedSelectYesNoPrompt = null;
        confirmationToken = null;
    }

    private ShopUiValidationResult ValidateInclusionShop(EvaluatedShopOffer offer)
    {
        var page = offer.Offer.CallbackPath.FirstOrDefault(step => step.Kind == ShopMenuPathStepKind.InclusionPage);
        var subpage = offer.Offer.CallbackPath.FirstOrDefault(step => step.Kind == ShopMenuPathStepKind.InclusionSubpage);
        if (page.Kind != ShopMenuPathStepKind.InclusionPage || subpage.Kind != ShopMenuPathStepKind.InclusionSubpage)
            return ShopUiValidationResult.Mismatch("InclusionShop route is missing its typed page and subpage.");

        var routeKey = $"{offer.Offer.NpcId}:{offer.Offer.ShopId}:{page.Index}:{subpage.Index}";
        if (!string.Equals(routeKey, inclusionRouteKey, StringComparison.Ordinal))
        {
            inclusionRouteKey = routeKey;
            inclusionSelectionStage = 0;
        }
        if (inclusionSelectionStage == 0)
        {
            if (!GameInteractionHelper.TryFireAddonCallback("InclusionShop", true, 12, page.Index))
                return ShopUiValidationResult.NotReady("Waiting to select the validated InclusionShop page.");
            inclusionSelectionStage = 1;
            return ShopUiValidationResult.NotReady("Selected the InclusionShop page; waiting for the live rows to refresh.");
        }
        if (inclusionSelectionStage == 1)
        {
            if (!GameInteractionHelper.TryFireAddonCallback("InclusionShop", true, 13, subpage.Index))
                return ShopUiValidationResult.NotReady("Waiting to select the validated InclusionShop subpage.");
            inclusionSelectionStage = 2;
            return ShopUiValidationResult.NotReady("Selected the InclusionShop subpage; waiting for the live rows to refresh.");
        }

        if (offer.Offer.AllOutputs.Count != 1)
            return ShopUiValidationResult.Mismatch("The live InclusionShop layout cannot prove a multi-output row; ADS will not guess.");
        var addon = (AddonInclusionShop*)RaptureAtkUnitManager.Instance()->GetAddonByName("InclusionShop");
        if (addon == null || !addon->AtkUnitBase.IsVisible || addon->AtkUnitBase.AtkValues == null)
            return ShopUiValidationResult.NotReady("InclusionShop addon data is unavailable.");
        var values = (AddonInclusionShop.InclusionShopAtkValues*)addon->AtkUnitBase.AtkValues;
        var itemCount = Math.Min(60, checked((int)ReadUnsigned(values->ItemCount)));
        if (itemCount <= 0)
            return ShopUiValidationResult.NotReady("InclusionShop rows are not populated yet.");

        var matches = new List<int>();
        for (var row = 0; row < itemCount; row++)
        {
            var item = values->Items[row];
            if (ReadUnsigned(item.ItemId) != offer.Offer.ReceiveItemId
                || ReadUnsigned(item.Stacksize) != offer.Offer.ReceiveCount)
                continue;
            var giveCount = Math.Min(3, checked((int)ReadUnsigned(item.GiveCount)));
            var liveCosts = new ShopRuntimeCostValue[giveCount];
            for (var costIndex = 0; costIndex < giveCount; costIndex++)
            {
                liveCosts[costIndex] = new ShopRuntimeCostValue(
                    checked((uint)ReadUnsigned(item.GiveItemId[costIndex])),
                    checked((uint)ReadUnsigned(item.GiveAmount[costIndex])));
            }
            if (ShopRuntimeCostMatcher.Matches(liveCosts, offer.Offer.Currencies))
                matches.Add(checked((int)ReadUnsigned(item.Index)));
        }

        return matches.Count switch
        {
            1 => ShopUiValidationResult.Valid(matches[0], "InclusionShop validated a unique live item, bundle, and exact cost row."),
            0 => ShopUiValidationResult.Mismatch("InclusionShop has no live row matching the requested item, bundle, and exact costs."),
            _ => ShopUiValidationResult.Mismatch("InclusionShop has duplicate indistinguishable rows; ADS will not guess."),
        };
    }

    private ShopUiValidationResult ValidateGrandCompanyShop(EvaluatedShopOffer offer)
    {
        var addon = RaptureAtkUnitManager.Instance()->GetAddonByName("GrandCompanyExchange");
        if (addon == null || !addon->IsVisible || addon->AtkValues == null)
            return ShopUiValidationResult.NotReady("GrandCompanyExchange addon data is unavailable.");

        var playerState = PlayerState.Instance();
        if (playerState == null)
            return ShopUiValidationResult.NotReady("PlayerState is unavailable for Grand Company validation.");
        if (playerState->GrandCompany != offer.Offer.RequiredGrandCompany)
            return ShopUiValidationResult.Mismatch("The active Grand Company does not match the sheet shop family.");
        if (playerState->GetGrandCompanyRank() < offer.Offer.RequiredGrandCompanyRank)
            return ShopUiValidationResult.Mismatch("The current Grand Company rank is below the offer requirement.");

        var routeKey = $"{offer.Offer.NpcId}:{offer.Offer.ShopId}:{offer.Offer.RankTab}:{offer.Offer.CategoryTab}";
        if (!string.Equals(routeKey, grandCompanyRouteKey, StringComparison.Ordinal))
        {
            grandCompanyRouteKey = routeKey;
            grandCompanySelectionStage = 0;
        }
        if (grandCompanySelectionStage == 0)
        {
            var tab = EnsureGrandCompanyTab(addon, 37, offer.Offer.RankTab);
            if (tab == TabSelectionResult.Failed)
                return ShopUiValidationResult.Mismatch("The sheet-derived Grand Company rank tab is unavailable.");
            if (tab == TabSelectionResult.Clicked)
                return ShopUiValidationResult.NotReady("Selected the Grand Company rank tab; waiting for rows to refresh.");
            grandCompanySelectionStage = 1;
        }
        if (grandCompanySelectionStage == 1)
        {
            var tab = EnsureGrandCompanyTab(addon, 44, offer.Offer.CategoryTab);
            if (tab == TabSelectionResult.Failed)
                return ShopUiValidationResult.Mismatch("The sheet-derived Grand Company category tab is unavailable.");
            if (tab == TabSelectionResult.Clicked)
                return ShopUiValidationResult.NotReady("Selected the Grand Company category tab; waiting for rows to refresh.");
            grandCompanySelectionStage = 2;
        }

        var itemCount = Math.Min(100, checked((int)ReadUnsigned(addon->AtkValues[1])));
        if (itemCount <= 0 || addon->AtkValuesCount <= 467 + itemCount)
            return ShopUiValidationResult.NotReady("GrandCompanyExchange live rows are not populated yet.");
        var matches = new List<int>();
        for (var row = 0; row < itemCount; row++)
        {
            var begin = 17 + row;
            var itemId = ReadUnsigned(addon->AtkValues[begin + 300]);
            var cost = ReadUnsigned(addon->AtkValues[begin + 50]);
            var rank = ReadUnsigned(addon->AtkValues[begin + 400]);
            if (itemId == offer.Offer.ReceiveItemId
                && cost == offer.Offer.Currencies.Single().AmountPerTransaction
                && rank == offer.Offer.RequiredGrandCompanyRank)
                matches.Add(row);
        }
        return matches.Count switch
        {
            1 => ShopUiValidationResult.Valid(matches[0], "GrandCompanyExchange validated a unique item, seal cost, and required-rank row."),
            0 => ShopUiValidationResult.Mismatch("GrandCompanyExchange has no exact item, seal-cost, and required-rank row match."),
            _ => ShopUiValidationResult.Mismatch("GrandCompanyExchange has duplicate indistinguishable rows; ADS will not guess."),
        };
    }

    private ShopUiValidationResult ValidateFreeCompanyShop(EvaluatedShopOffer offer)
    {
        var addon = RaptureAtkUnitManager.Instance()->GetAddonByName("FreeCompanyCreditShop");
        if (addon == null || !addon->IsVisible || addon->AtkValues == null || addon->AtkValuesCount <= 130)
            return ShopUiValidationResult.NotReady("FreeCompanyCreditShop addon data is unavailable.");
        var rank = ReadUnsigned(addon->AtkValues[0]);
        var credits = ReadUnsigned(addon->AtkValues[3]);
        var itemCount = Math.Min(20, checked((int)ReadUnsigned(addon->AtkValues[9])));
        if (itemCount <= 0 || addon->AtkValuesCount <= 130 + itemCount)
            return ShopUiValidationResult.NotReady("FreeCompanyCreditShop rows are not populated yet.");

        var matches = new List<int>();
        for (var row = 0; row < itemCount; row++)
        {
            var itemId = ReadUnsigned(addon->AtkValues[30 + row]);
            var requiredRank = ReadUnsigned(addon->AtkValues[70 + row]);
            var maxQuantity = ReadSigned(addon->AtkValues[110 + row]);
            var price = ReadUnsigned(addon->AtkValues[130 + row]);
            if (itemId == offer.Offer.ReceiveItemId
                && price == offer.Offer.Currencies.Single().AmountPerTransaction
                && requiredRank == offer.Offer.RequiredGrandCompanyRank
                && rank >= requiredRank
                && maxQuantity > 0
                && credits >= price)
                matches.Add(row);
        }
        return matches.Count switch
        {
            1 => ShopUiValidationResult.Valid(matches[0], "FreeCompanyCreditShop validated a unique item, price, rank, credits, and quantity row."),
            0 => ShopUiValidationResult.Mismatch("FreeCompanyCreditShop has no affordable exact item, price, rank, and quantity row match."),
            _ => ShopUiValidationResult.Mismatch("FreeCompanyCreditShop has duplicate indistinguishable rows; ADS will not guess."),
        };
    }

    private static bool SubmitGrandCompanyPurchase(EvaluatedShopOffer offer, int runtimeRow, int transactionCount)
    {
        var addon = RaptureAtkUnitManager.Instance()->GetAddonByName("GrandCompanyExchange");
        if (addon == null || !addon->IsVisible || addon->AtkValues == null
            || runtimeRow < 0 || addon->AtkValuesCount <= 467 + runtimeRow)
            return false;
        var begin = 17 + runtimeRow;
        var itemId = ReadUnsigned(addon->AtkValues[begin + 300]);
        var sealCost = ReadUnsigned(addon->AtkValues[begin + 50]);
        var iconId = ReadUnsigned(addon->AtkValues[begin + 150]);
        var requiredRank = ReadUnsigned(addon->AtkValues[begin + 400]);
        var opensCurrencyDialog = ReadBoolean(addon->AtkValues[begin + 450]);
        var player = PlayerState.Instance();
        if (player == null
            || player->GrandCompany != offer.Offer.RequiredGrandCompany
            || player->GetGrandCompanyRank() < requiredRank
            || itemId != offer.Offer.ReceiveItemId
            || sealCost != offer.Offer.Currencies.Single().AmountPerTransaction
            || requiredRank != offer.Offer.RequiredGrandCompanyRank)
            return false;

        return opensCurrencyDialog
            ? GameInteractionHelper.TryFireAddonCallback(
                "GrandCompanyExchange",
                true,
                0,
                runtimeRow,
                1,
                0,
                true,
                true,
                checked((uint)itemId),
                checked((uint)iconId),
                checked((uint)sealCost))
            : GameInteractionHelper.TryFireAddonCallback(
                "GrandCompanyExchange",
                true,
                0,
                runtimeRow,
                transactionCount,
                0,
                true,
                false,
                0,
                0,
                0);
    }

    private static bool SubmitInclusionPurchase(EvaluatedShopOffer offer, int runtimeRow, int transactionCount)
    {
        var addon = (AddonInclusionShop*)RaptureAtkUnitManager.Instance()->GetAddonByName("InclusionShop");
        if (addon == null || !addon->AtkUnitBase.IsVisible || addon->AtkUnitBase.AtkValues == null)
            return false;
        var values = (AddonInclusionShop.InclusionShopAtkValues*)addon->AtkUnitBase.AtkValues;
        var itemCount = Math.Min(60, checked((int)ReadUnsigned(values->ItemCount)));
        var matches = 0;
        for (var row = 0; row < itemCount; row++)
        {
            var item = values->Items[row];
            if (ReadUnsigned(item.Index) != runtimeRow
                || ReadUnsigned(item.ItemId) != offer.Offer.ReceiveItemId
                || ReadUnsigned(item.Stacksize) != offer.Offer.ReceiveCount
                || ReadUnsigned(item.MaxAmount) < transactionCount)
                continue;
            matches++;
        }
        return matches == 1
            && GameInteractionHelper.TryFireAddonCallback("InclusionShop", true, 14, runtimeRow, transactionCount);
    }

    private static bool SubmitFreeCompanyPurchase(EvaluatedShopOffer offer, int runtimeRow, int transactionCount)
    {
        var addon = RaptureAtkUnitManager.Instance()->GetAddonByName("FreeCompanyCreditShop");
        if (addon == null || !addon->IsVisible || addon->AtkValues == null
            || runtimeRow < 0 || addon->AtkValuesCount <= 130 + runtimeRow)
            return false;
        var itemId = ReadUnsigned(addon->AtkValues[30 + runtimeRow]);
        var requiredRank = ReadUnsigned(addon->AtkValues[70 + runtimeRow]);
        var maxQuantity = ReadSigned(addon->AtkValues[110 + runtimeRow]);
        var price = ReadUnsigned(addon->AtkValues[130 + runtimeRow]);
        var rank = ReadUnsigned(addon->AtkValues[0]);
        var credits = ReadUnsigned(addon->AtkValues[3]);
        long requiredCredits;
        try
        {
            requiredCredits = checked(price * (long)transactionCount);
        }
        catch (OverflowException)
        {
            return false;
        }
        return itemId == offer.Offer.ReceiveItemId
            && requiredRank == offer.Offer.RequiredGrandCompanyRank
            && price == offer.Offer.Currencies.Single().AmountPerTransaction
            && rank >= requiredRank
            && transactionCount <= maxQuantity
            && credits >= requiredCredits
            && GameInteractionHelper.TryFireAddonCallback(
                "FreeCompanyCreditShop", true, 0, runtimeRow, transactionCount);
    }

    private enum TabSelectionResult
    {
        Failed,
        Selected,
        Clicked,
    }

    private static TabSelectionResult EnsureGrandCompanyTab(AtkUnitBase* addon, uint baseNodeId, int tabIndex)
    {
        var node = addon->GetNodeById(baseNodeId + checked((uint)tabIndex));
        if (node == null || !node->IsVisible())
            return TabSelectionResult.Failed;
        var button = node->GetAsAtkComponentRadioButton();
        if (button == null)
            return TabSelectionResult.Failed;
        if (button->IsSelected)
            return TabSelectionResult.Selected;
        if (!button->AtkComponentButton.IsEnabled)
            return TabSelectionResult.Failed;
        var owner = button->AtkComponentButton.AtkComponentBase.OwnerNode;
        var eventPointer = owner == null ? null : owner->AtkResNode.AtkEventManager.Event;
        if (eventPointer == null)
            return TabSelectionResult.Failed;
        var atkEvent = (AtkEvent*)eventPointer;
        addon->ReceiveEvent(atkEvent->State.EventType, (int)atkEvent->Param, eventPointer);
        return TabSelectionResult.Clicked;
    }

    private static long ReadUnsigned(AtkValue value)
        => value.Type switch
        {
            AtkValueType.UInt => value.UInt,
            AtkValueType.Int => Math.Max(0, value.Int),
            _ => -1,
        };

    private static int ReadSigned(AtkValue value)
        => value.Type switch
        {
            AtkValueType.UInt => value.UInt > int.MaxValue ? int.MaxValue : (int)value.UInt,
            AtkValueType.Int => value.Int,
            _ => -1,
        };

    private static bool ReadBoolean(AtkValue value)
        => value.Type switch
        {
            AtkValueType.Bool => value.Bool,
            AtkValueType.UInt => value.UInt != 0,
            AtkValueType.Int => value.Int != 0,
            _ => false,
        };

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
