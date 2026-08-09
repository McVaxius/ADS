using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using ADS.Models;
using Dalamud.Game.Command;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Component.Exd;
using Lumina.Excel.Sheets;

namespace ADS.Services;

public sealed class LootAutomationService
{
    private const string RollSignature = "41 83 F8 ?? 0F 83 ?? ?? ?? ?? 48 89 5C 24 08";
    private static readonly TimeSpan RestoreCooldown = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan SameLootRetryDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SignatureRetryCooldown = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LootDiagnosticCooldown = TimeSpan.FromSeconds(10);

    private readonly IDataManager dataManager;
    private readonly ICommandManager commandManager;
    private readonly ISigScanner sigScanner;
    private readonly Func<string, string> searchCurrentCharacterItemsJson;
    private readonly Configuration configuration;
    private readonly IPluginLog log;
    private readonly Dictionary<uint, uint[]> fadedCopyResultCache = [];
    private readonly Dictionary<string, DateTime> nextDiagnosticLogUtcByKey = [];

    private RollItemRaw? rollItemRaw;
    private string activeOwnershipKey = string.Empty;
    private bool lazyLootDisabledForOwnership;
    private DateTime nextRestoreAttemptUtc = DateTime.MinValue;
    private DateTime nextSignatureScanUtc = DateTime.MinValue;
    private DateTime nextFailureLogUtc = DateTime.MinValue;
    private uint lastAttemptItemId;
    private uint lastAttemptIndex;
    private RollResult lastAttemptResult = RollResult.UnAwarded;
    private DateTime lastAttemptUtc = DateTime.MinValue;

    public LootAutomationService(
        IDataManager dataManager,
        ICommandManager commandManager,
        ISigScanner sigScanner,
        Func<string, string> searchCurrentCharacterItemsJson,
        Configuration configuration,
        IPluginLog log)
    {
        this.dataManager = dataManager;
        this.commandManager = commandManager;
        this.sigScanner = sigScanner;
        this.searchCurrentCharacterItemsJson = searchCurrentCharacterItemsJson;
        this.configuration = configuration;
        this.log = log;
    }

    private unsafe delegate bool RollItemRaw(Loot* loot, RollResult option, uint lootItemIndex);

    private enum RegistrableCategory
    {
        Mount,
        Minion,
        FashionAccessory,
        Facewear,
        OrchestrionRoll,
        FadedOrchestrionCopy,
        EmoteHairstyle,
        Barding,
        TripleTriadCard,
    }

    public string Status { get; private set; } = "Loot off.";

    public bool IsActive
        => configuration.LootMode != LootRollMode.Off;

    public void Update(DutyContextSnapshot context, OwnershipMode ownershipMode, bool pluginEnabled)
    {
        if (configuration.LootMode == LootRollMode.Off)
        {
            ResetOwnershipLatch();
            Status = "Loot off.";
            return;
        }

        if (!pluginEnabled)
        {
            ResetOwnershipLatch();
            Status = "Loot waiting for ADS enabled.";
            return;
        }

        if (!context.IsLoggedIn)
        {
            ResetOwnershipLatch();
            Status = "Loot waiting for login.";
            return;
        }

        var ownsStartOrLeaveFlow = IsOwnedOrLeaving(ownershipMode);
        var safeForLoot = IsSafeForLoot(context);
        var needGreedVisible = GameInteractionHelper.IsAddonVisible("NeedGreed");
        var notificationLootVisible = GameInteractionHelper.IsAddonVisible("_NotificationLoot");
        var visibleLootPresent = needGreedVisible || notificationLootVisible;
        var visibleLootAssistEligible = safeForLoot && visibleLootPresent;

        if (!ownsStartOrLeaveFlow && !visibleLootAssistEligible)
        {
            ResetOwnershipLatch(resetAttempts: !visibleLootPresent);
            if (visibleLootPresent)
            {
                Status = "Loot visible; waiting for stable game state.";
                LogLootDiagnostic(
                    "unsafe-visible-loot",
                    $"Loot visible outside ADS ownership but game state is unsafe; needGreed={needGreedVisible}, notificationLoot={notificationLootVisible}, territory={context.TerritoryTypeId.ToString(CultureInfo.InvariantCulture)}, cfc={context.ContentFinderConditionId.ToString(CultureInfo.InvariantCulture)}.");
            }
            else
            {
                Status = "Loot waiting for ADS ownership or visible loot.";
            }

            return;
        }

        if (ownsStartOrLeaveFlow)
            EnsureOwnershipLatch(context);
        else
            ResetOwnershipLatch(resetAttempts: false);

        if (ownsStartOrLeaveFlow && context.InInstancedDuty)
            EnsureLazyLootDisabledForOwnership();

        if (!safeForLoot)
        {
            Status = "Loot armed; waiting for stable game state.";
            if (visibleLootPresent)
            {
                LogLootDiagnostic(
                    "unsafe-owned-loot",
                    $"Loot visible during ADS ownership but game state is unsafe; needGreed={needGreedVisible}, notificationLoot={notificationLootVisible}, territory={context.TerritoryTypeId.ToString(CultureInfo.InvariantCulture)}, cfc={context.ContentFinderConditionId.ToString(CultureInfo.InvariantCulture)}.");
            }

            return;
        }

        if (!ownsStartOrLeaveFlow)
        {
            LogLootDiagnostic(
                "visible-loot-assist",
                $"Visible loot assist active outside ADS ownership; mode={configuration.LootMode}, needGreed={needGreedVisible}, notificationLoot={notificationLootVisible}, territory={context.TerritoryTypeId.ToString(CultureInfo.InvariantCulture)}, cfc={context.ContentFinderConditionId.ToString(CultureInfo.InvariantCulture)}.");
        }

        if (!needGreedVisible)
        {
            var restoreAttempted = false;
            if (notificationLootVisible)
            {
                restoreAttempted = true;
                if (TryRestoreMinimizedLoot())
                    needGreedVisible = GameInteractionHelper.IsAddonVisible("NeedGreed");
            }

            if (!needGreedVisible)
            {
                if (!restoreAttempted)
                    Status = $"Loot {configuration.LootMode}; waiting for loot window.";
                return;
            }
        }

        var now = DateTime.UtcNow;
        if (!TryGetNextLootItem(now, out var index, out var lootItem, out var blockedDuplicateItemId))
        {
            if (blockedDuplicateItemId != 0)
            {
                var duplicateName = TryGetItem(blockedDuplicateItemId, out var duplicateItem)
                    ? duplicateItem.Name.ToString()
                    : $"item {blockedDuplicateItemId.ToString(CultureInfo.InvariantCulture)}";
                Status = $"Loot {configuration.LootMode}; waiting {GetRemainingSeconds(lastAttemptUtc + SameLootRetryDelay):0.0}s to retry {duplicateName}.";
                return;
            }

            ResetAttemptState();
            Status = $"Loot {configuration.LootMode}; NeedGreed visible but no eligible loot rows.";
            return;
        }

        var itemId = NormalizeItemId(lootItem.ItemId);
        var hasItem = TryGetItem(itemId, out var item);
        var itemName = hasItem ? item.Name.ToString() : $"item {itemId.ToString(CultureInfo.InvariantCulture)}";
        var decision = hasItem
            ? ResolveDecision(lootItem, itemId, item)
            : ResolveMissingItemDecision(lootItem);
        if (TryRoll(decision.Result, index))
        {
            lastAttemptItemId = itemId;
            lastAttemptIndex = index;
            lastAttemptResult = decision.Result;
            lastAttemptUtc = now;
            Status = $"Loot {configuration.LootMode}; {FormatRollResult(decision.Result)} {itemName}.";
            log.Information(
                $"[ADS][Loot] Rolled {FormatRollResult(decision.Result)} on {EscapeLogText(itemName)} ({itemId}) slot={index.ToString(CultureInfo.InvariantCulture)}; {decision.Reason}");
            return;
        }

        Status = $"Loot {configuration.LootMode}; failed to roll {itemName}.";
        LogRollFailure(itemId, index, "native roll call failed");
    }

    private void EnsureOwnershipLatch(DutyContextSnapshot context)
    {
        var key = $"{context.TerritoryTypeId.ToString(CultureInfo.InvariantCulture)}:{context.ContentFinderConditionId.ToString(CultureInfo.InvariantCulture)}";
        if (string.Equals(activeOwnershipKey, key, StringComparison.Ordinal))
            return;

        activeOwnershipKey = key;
        lazyLootDisabledForOwnership = false;
        ResetAttemptState();
    }

    private void ResetOwnershipLatch(bool resetAttempts = true)
    {
        activeOwnershipKey = string.Empty;
        lazyLootDisabledForOwnership = false;
        if (resetAttempts)
            ResetAttemptState();
    }

    private void EnsureLazyLootDisabledForOwnership()
    {
        if (lazyLootDisabledForOwnership)
            return;

        lazyLootDisabledForOwnership = true;
        if (GameInteractionHelper.TrySendChatCommand(commandManager, "/xldisableplugin lazyloot", log))
        {
            log.Information("[ADS][Loot] Disabled LazyLoot for this ADS-owned duty.");
            return;
        }

        log.Warning("[ADS][Loot] Failed to send /xldisableplugin lazyloot for this ADS-owned duty.");
    }

    private bool TryRestoreMinimizedLoot()
    {
        var now = DateTime.UtcNow;
        if (now < nextRestoreAttemptUtc)
        {
            Status = $"Loot {configuration.LootMode}; minimized loot restore cooling down.";
            return false;
        }

        nextRestoreAttemptUtc = now + RestoreCooldown;
        if (GameInteractionHelper.TryFireAddonCallback("_Notification", true, 0, 2))
        {
            Status = "Loot notification restored; waiting for NeedGreed.";
            LogLootDiagnostic("restore-minimized-loot", "Restored minimized loot via _Notification true 0 2.");
            return true;
        }

        Status = "Loot notification visible; restore callback failed.";
        LogLootDiagnostic("restore-minimized-loot-failed", "Failed minimized loot restore via _Notification true 0 2.", warning: true);
        return false;
    }

    private RollDecision ResolveMissingItemDecision(LootItem lootItem)
    {
        var baseDesired = MapBaseMode(configuration.LootMode);
        var hardCap = GetHardCap(lootItem);
        var final = ResultMerge(baseDesired, hardCap);
        var reason =
            $"base={configuration.LootMode}/{FormatRollResult(baseDesired)}, " +
            $"registrable=unknown(itemSheet=missing), " +
            $"needMissing={configuration.LootRegistrableNeedingEnabled}, " +
            $"glamour=unknown(itemSheet=missing), " +
            $"glamourNeedMissing={configuration.LootGlamourNeedingEnabled}, " +
            $"override=none(itemSheet=missing), " +
            $"rollStateCap={FormatRollResult(GetRollStateCap(lootItem))}, " +
            $"lootModeCap={FormatRollResult(GetLootModeCap(lootItem))}, " +
            $"liveCap={FormatRollResult(hardCap)}, " +
            $"final={FormatRollResult(final)}";
        return new RollDecision(final, reason);
    }

    private RollDecision ResolveDecision(LootItem lootItem, uint itemId, Item item)
    {
        var baseDesired = MapBaseMode(configuration.LootMode);
        var desired = baseDesired;
        var registrableReason = "not-registrable";
        var glamourReason = item.EquipSlotCategory.RowId == 0 ? "not-equippable" : "disabled";
        var overrideReason = "none(not-registrable)";

        if (TryClassifyRegistrable(itemId, item, out var category, out var categoryLabel, out var registrationItemIds))
        {
            var categoryEnabled = IsCategoryEnabled(category);
            var inventoryCount = GetInventoryCount(itemId);
            var alreadyRegistered = IsAlreadyRegistered(itemId, registrationItemIds);
            var owned = inventoryCount > 0 || alreadyRegistered;
            var missing = !owned;

            if (categoryEnabled && missing && configuration.LootRegistrableNeedingEnabled)
            {
                desired = RollResult.Needed;
                overrideReason = "need-missing";
            }
            else if (!categoryEnabled)
            {
                overrideReason = "none(category-disabled)";
            }
            else if (missing)
            {
                overrideReason = "none(need-missing-disabled)";
            }
            else
            {
                overrideReason = "none(owned)";
            }

            registrableReason =
                $"{categoryLabel}, categoryEnabled={categoryEnabled}, " +
                $"inventory={inventoryCount.ToString(CultureInfo.InvariantCulture)}, " +
                $"registered={alreadyRegistered}, owned={owned}, missing={missing}";
        }

        if (configuration.LootGlamourNeedingEnabled && item.EquipSlotCategory.RowId != 0)
        {
            if (TryResolveGlamourOwnership(itemId, out var owned, out glamourReason) && !owned)
            {
                desired = RollResult.Needed;
                overrideReason = "need-missing-glamour";
            }
        }

        var rollStateCap = GetRollStateCap(lootItem);
        var lootModeCap = GetLootModeCap(lootItem);
        var hardCap = ResultMerge(rollStateCap, lootModeCap);
        var uniqueOwnedCap = item.IsUnique && (GetInventoryCount(itemId) > 0 || IsItemActionUnlocked(itemId));
        if (uniqueOwnedCap)
            hardCap = RollResult.Passed;

        var final = ResultMerge(desired, hardCap);
        var reason =
            $"base={configuration.LootMode}/{FormatRollResult(baseDesired)}, " +
            $"registrable={registrableReason}, " +
            $"needMissing={configuration.LootRegistrableNeedingEnabled}, " +
            $"glamour={glamourReason}, " +
            $"glamourNeedMissing={configuration.LootGlamourNeedingEnabled}, " +
            $"override={overrideReason}, " +
            $"desired={FormatRollResult(desired)}, " +
            $"rollStateCap={FormatRollResult(rollStateCap)}, " +
            $"lootModeCap={FormatRollResult(lootModeCap)}, " +
            $"uniqueOwnedCap={uniqueOwnedCap}, " +
            $"liveCap={FormatRollResult(hardCap)}, " +
            $"final={FormatRollResult(final)}";
        return new RollDecision(final, reason);
    }

    private bool TryResolveGlamourOwnership(uint itemId, out bool owned, out string reason)
    {
        owned = false;
        var request = JsonSerializer.Serialize(new
        {
            version = 1,
            itemIds = new[] { itemId },
            includeZeroQuantity = false,
        });

        try
        {
            var response = searchCurrentCharacterItemsJson(request);
            if (string.IsNullOrWhiteSpace(response))
                return PreserveBaseForGlamour(itemId, "empty response", out owned, out reason);

            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return PreserveBaseForGlamour(itemId, "response root is not an object", out owned, out reason);
            if (!root.TryGetProperty("version", out var version)
                || !version.TryGetInt32(out var versionValue)
                || versionValue != 1)
            {
                return PreserveBaseForGlamour(itemId, "response version is missing or unsupported", out owned, out reason);
            }

            if (!root.TryGetProperty("ready", out var ready) || ready.ValueKind != JsonValueKind.True)
                return PreserveBaseForGlamour(itemId, "response is not ready", out owned, out reason);
            if (!root.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array)
                return PreserveBaseForGlamour(itemId, "rows are missing or invalid", out owned, out reason);

            foreach (var row in rows.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object
                    || !row.TryGetProperty("itemId", out var rowItemId)
                    || !rowItemId.TryGetUInt32(out var rowItemIdValue))
                {
                    return PreserveBaseForGlamour(itemId, "row itemId is missing or invalid", out owned, out reason);
                }

                if (rowItemIdValue != itemId)
                    continue;
                if (!row.TryGetProperty("quantity", out var quantity)
                    || !quantity.TryGetInt64(out var quantityValue)
                    || quantityValue <= 0)
                {
                    return PreserveBaseForGlamour(itemId, "matching row quantity is not positive", out owned, out reason);
                }

                owned = true;
                reason = $"owned(quantity={quantityValue.ToString(CultureInfo.InvariantCulture)})";
                return true;
            }

            reason = "missing(no matching row)";
            return true;
        }
        catch (Exception ex)
        {
            return PreserveBaseForGlamour(itemId, $"IPC failure ({ex.GetType().Name})", out owned, out reason);
        }
    }

    private bool PreserveBaseForGlamour(uint itemId, string detail, out bool owned, out string reason)
    {
        owned = false;
        reason = $"unavailable({detail})";
        LogLootDiagnostic(
            "xa-database-glamour",
            $"XA Database glamour ownership is unavailable for item {itemId.ToString(CultureInfo.InvariantCulture)}: {detail}; preserving configured loot mode.",
            warning: true);
        return false;
    }

    private bool TryClassifyRegistrable(
        uint itemId,
        Item item,
        out RegistrableCategory category,
        out string label,
        out IReadOnlyList<uint> registrationItemIds)
    {
        registrationItemIds = [itemId];
        if (IsFadedOrchestrionCopy(item))
        {
            category = RegistrableCategory.FadedOrchestrionCopy;
            label = "faded orchestrion copy";
            registrationItemIds = GetFadedCopyResultIds(itemId, item);
            return true;
        }

        var actionId = GetItemActionId(item);
        (category, label) = actionId switch
        {
            1322 => (RegistrableCategory.Mount, "mount"),
            853 => (RegistrableCategory.Minion, "minion"),
            20086 => (RegistrableCategory.FashionAccessory, "fashion accessory"),
            37312 => (RegistrableCategory.Facewear, "facewear"),
            25183 => (RegistrableCategory.OrchestrionRoll, "orchestrion roll"),
            2633 => (RegistrableCategory.EmoteHairstyle, "emote/hairstyle"),
            1013 => (RegistrableCategory.Barding, "barding"),
            3357 => (RegistrableCategory.TripleTriadCard, "Triple Triad card"),
            _ => (default, string.Empty),
        };

        return !string.IsNullOrWhiteSpace(label);
    }

    private bool IsCategoryEnabled(RegistrableCategory category)
        => category switch
        {
            RegistrableCategory.Mount => configuration.LootRegistrableMountsEnabled,
            RegistrableCategory.Minion => configuration.LootRegistrableMinionsEnabled,
            RegistrableCategory.FashionAccessory => configuration.LootRegistrableFashionAccessoriesEnabled,
            RegistrableCategory.Facewear => configuration.LootRegistrableFacewearEnabled,
            RegistrableCategory.OrchestrionRoll => configuration.LootRegistrableOrchestrionRollsEnabled,
            RegistrableCategory.FadedOrchestrionCopy => configuration.LootRegistrableFadedOrchestrionCopiesEnabled,
            RegistrableCategory.EmoteHairstyle => configuration.LootRegistrableEmotesHairstylesEnabled,
            RegistrableCategory.Barding => configuration.LootRegistrableBardingsEnabled,
            RegistrableCategory.TripleTriadCard => configuration.LootRegistrableTripleTriadCardsEnabled,
            _ => false,
        };

    private uint[] GetFadedCopyResultIds(uint itemId, Item item)
    {
        if (fadedCopyResultCache.TryGetValue(itemId, out var cached))
            return cached;

        var results = Array.Empty<uint>();
        try
        {
            var recipes = dataManager.GetExcelSheet<Recipe>();
            results = recipes
                .Where(recipe => recipe.Ingredient.Any(ingredient => ingredient.RowId == item.RowId))
                .Select(recipe => recipe.ItemResult.RowId)
                .Where(rowId => rowId != 0)
                .Distinct()
                .ToArray();
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"[ADS][Loot] Failed to resolve faded orchestrion copy recipe for item {itemId}.");
        }

        fadedCopyResultCache[itemId] = results;
        return results;
    }

    private static RollResult GetHardCap(LootItem lootItem)
        => ResultMerge(GetRollStateCap(lootItem), GetLootModeCap(lootItem));

    private static RollResult GetRollStateCap(LootItem lootItem)
        => lootItem.RollState switch
        {
            RollState.UpToNeed => RollResult.Needed,
            RollState.UpToGreed => RollResult.Greeded,
            _ => RollResult.Passed,
        };

    private static RollResult GetLootModeCap(LootItem lootItem)
        => lootItem.LootMode switch
        {
            FFXIVClientStructs.FFXIV.Client.Game.UI.LootMode.Normal => RollResult.Needed,
            FFXIVClientStructs.FFXIV.Client.Game.UI.LootMode.GreedOnly => RollResult.Greeded,
            _ => RollResult.Passed,
        };

    private unsafe bool TryGetNextLootItem(DateTime now, out uint index, out LootItem lootItem, out uint blockedDuplicateItemId)
    {
        blockedDuplicateItemId = 0;
        var loot = Loot.Instance();
        if (loot == null)
        {
            index = 0;
            lootItem = default;
            return false;
        }

        var span = loot->Items;
        for (index = 0; index < span.Length; index++)
        {
            lootItem = span[(int)index];
            lootItem.ItemId = NormalizeItemId(lootItem.ItemId);

            if (lootItem.ChestObjectId is 0 or 0xE0000000)
                continue;
            if (lootItem.RollResult != RollResult.UnAwarded)
                continue;
            if (lootItem.RollState is RollState.Rolled or RollState.Unavailable or RollState.Unknown)
                continue;
            if (lootItem.ItemId == 0)
                continue;
            if (lootItem.LootMode is FFXIVClientStructs.FFXIV.Client.Game.UI.LootMode.LootMasterGreedOnly
                or FFXIVClientStructs.FFXIV.Client.Game.UI.LootMode.Unavailable)
            {
                continue;
            }

            if (IsSameLootAttempt(lootItem.ItemId, index) && now - lastAttemptUtc < SameLootRetryDelay)
            {
                blockedDuplicateItemId = lootItem.ItemId;
                continue;
            }

            return true;
        }

        lootItem = default;
        return false;
    }

    private unsafe bool TryRoll(RollResult result, uint index)
    {
        try
        {
            if (rollItemRaw == null)
            {
                var now = DateTime.UtcNow;
                if (now < nextSignatureScanUtc)
                    return false;

                nextSignatureScanUtc = now + SignatureRetryCooldown;
                rollItemRaw = Marshal.GetDelegateForFunctionPointer<RollItemRaw>(sigScanner.ScanText(RollSignature));
            }

            var loot = Loot.Instance();
            return loot != null && rollItemRaw.Invoke(loot, result, index);
        }
        catch (Exception ex)
        {
            rollItemRaw = null;
            LogNativeRollException(ex);
            return false;
        }
    }

    private bool TryGetItem(uint itemId, out Item item)
    {
        var sheet = dataManager.GetExcelSheet<Item>();
        return sheet.TryGetRow(itemId, out item);
    }

    private static RollResult MapBaseMode(LootRollMode mode)
        => mode switch
        {
            LootRollMode.Need => RollResult.Needed,
            LootRollMode.Greed => RollResult.Greeded,
            LootRollMode.Pass => RollResult.Passed,
            _ => RollResult.Passed,
        };

    private static RollResult ResultMerge(params RollResult[] results)
        => results.Max() switch
        {
            RollResult.Needed => RollResult.Needed,
            RollResult.Greeded => RollResult.Greeded,
            _ => RollResult.Passed,
        };

    private unsafe int GetInventoryCount(uint itemId)
    {
        try
        {
            var inventory = InventoryManager.Instance();
            return inventory == null ? 0 : inventory->GetInventoryItemCount(itemId);
        }
        catch
        {
            return 0;
        }
    }

    private static unsafe bool IsItemActionUnlocked(uint itemId)
    {
        try
        {
            var exdItem = ExdModule.GetItemRowById(itemId);
            var uiState = UIState.Instance();
            return exdItem != null && uiState != null && uiState->IsItemActionUnlocked(exdItem) is 1;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsAlreadyRegistered(uint itemId, IReadOnlyList<uint> registrationItemIds)
    {
        if (registrationItemIds.Count == 0)
            return IsItemActionUnlocked(itemId);

        return registrationItemIds.All(IsItemActionUnlocked);
    }

    private static bool IsFadedOrchestrionCopy(Item item)
        => item.FilterGroup == 12 && item.ItemUICategory.RowId == 94;

    private static uint GetItemActionId(Item item)
    {
        try
        {
            return item.ItemAction.Value.Action.Value.RowId;
        }
        catch
        {
            return 0;
        }
    }

    private bool IsSameLootAttempt(uint itemId, uint index)
        => itemId == lastAttemptItemId && index == lastAttemptIndex && lastAttemptResult != RollResult.UnAwarded;

    private void ResetAttemptState()
    {
        lastAttemptItemId = 0;
        lastAttemptIndex = 0;
        lastAttemptResult = RollResult.UnAwarded;
        lastAttemptUtc = DateTime.MinValue;
    }

    private void LogLootDiagnostic(string key, string message, bool warning = false)
    {
        var now = DateTime.UtcNow;
        if (nextDiagnosticLogUtcByKey.TryGetValue(key, out var nextLogUtc) && now < nextLogUtc)
            return;

        nextDiagnosticLogUtcByKey[key] = now + LootDiagnosticCooldown;
        if (warning)
            log.Warning($"[ADS][Loot] {message}");
        else
            log.Information($"[ADS][Loot] {message}");
    }

    private void LogRollFailure(uint itemId, uint index, string reason)
    {
        var now = DateTime.UtcNow;
        if (now < nextFailureLogUtc)
            return;

        nextFailureLogUtc = now + SignatureRetryCooldown;
        log.Warning(
            $"[ADS][Loot] Roll failure item={itemId.ToString(CultureInfo.InvariantCulture)} slot={index.ToString(CultureInfo.InvariantCulture)} reason={reason}.");
    }

    private void LogNativeRollException(Exception ex)
    {
        var now = DateTime.UtcNow;
        if (now < nextFailureLogUtc)
            return;

        nextFailureLogUtc = now + SignatureRetryCooldown;
        log.Warning(ex, "[ADS][Loot] Native loot roll failed.");
    }

    private static bool IsOwnedOrLeaving(OwnershipMode ownershipMode)
        => ownershipMode is OwnershipMode.OwnedStartOutside
            or OwnershipMode.OwnedStartInside
            or OwnershipMode.OwnedResumeInside
            or OwnershipMode.Leaving;

    private static bool IsSafeForLoot(DutyContextSnapshot context)
        => !context.IsUnsafeTransition
           && !context.OccupiedInCutSceneEvent
           && !context.WatchingCutscene;

    private static uint NormalizeItemId(uint itemId)
        => itemId >= 1_000_000 ? itemId - 1_000_000 : itemId;

    private static double GetRemainingSeconds(DateTime untilUtc)
        => Math.Max(0, (untilUtc - DateTime.UtcNow).TotalSeconds);

    private static string FormatRollResult(RollResult result)
        => result switch
        {
            RollResult.Needed => "Need",
            RollResult.Greeded => "Greed",
            RollResult.Passed => "Pass",
            _ => result.ToString(),
        };

    private static string EscapeLogText(string value)
        => (value ?? string.Empty)
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);

    private readonly record struct RollDecision(RollResult Result, string Reason);
}
