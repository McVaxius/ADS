using System.Globalization;
using System.Runtime.InteropServices;
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
    private static readonly TimeSpan RollCooldown = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan RestoreCooldown = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SameLootRetryDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SignatureRetryCooldown = TimeSpan.FromSeconds(30);

    private readonly IDataManager dataManager;
    private readonly ICommandManager commandManager;
    private readonly ISigScanner sigScanner;
    private readonly Configuration configuration;
    private readonly IPluginLog log;
    private readonly Dictionary<uint, uint[]> fadedCopyResultCache = [];

    private RollItemRaw? rollItemRaw;
    private string activeOwnershipKey = string.Empty;
    private bool lazyLootDisabledForOwnership;
    private DateTime nextRollAttemptUtc = DateTime.MinValue;
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
        Configuration configuration,
        IPluginLog log)
    {
        this.dataManager = dataManager;
        this.commandManager = commandManager;
        this.sigScanner = sigScanner;
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
        var ownsStartOrLeaveFlow = pluginEnabled
                                    && context.IsLoggedIn
                                    && IsOwnedOrLeaving(ownershipMode);
        var ownershipEligible = ownsStartOrLeaveFlow
                                && (context.InInstancedDuty || configuration.LootMode != LootRollMode.Off);
        if (!ownershipEligible)
        {
            ResetOwnershipLatch();
            Status = configuration.LootMode == LootRollMode.Off
                ? "Loot off."
                : "Loot waiting for ADS-owned duty.";
            return;
        }

        EnsureOwnershipLatch(context);

        if (configuration.LootMode == LootRollMode.Off)
        {
            ResetAttemptState();
            Status = "Loot off for ADS-owned duty.";
            return;
        }

        if (context.InInstancedDuty)
            EnsureLazyLootDisabledForOwnership();

        var needGreedVisible = GameInteractionHelper.IsAddonVisible("NeedGreed");
        if (!needGreedVisible)
        {
            if (GameInteractionHelper.IsAddonVisible("_NotificationLoot"))
                TryRestoreMinimizedLoot();
            else
                Status = $"Loot {configuration.LootMode}; waiting for loot window.";

            return;
        }

        if (context.IsUnsafeTransition || context.OccupiedInCutSceneEvent || context.WatchingCutscene)
        {
            Status = "Loot armed; waiting for stable duty state.";
            return;
        }

        var now = DateTime.UtcNow;
        if (now < nextRollAttemptUtc)
        {
            Status = $"Loot {configuration.LootMode}; waiting {GetRemainingSeconds(nextRollAttemptUtc):0.0}s before next roll.";
            return;
        }

        if (!TryGetNextLootItem(out var index, out var lootItem))
        {
            ResetAttemptState();
            Status = $"Loot {configuration.LootMode}; NeedGreed visible but no eligible loot rows.";
            return;
        }

        var itemId = NormalizeItemId(lootItem.ItemId);
        var hasItem = TryGetItem(itemId, out var item);
        var itemName = hasItem ? item.Name.ToString() : $"item {itemId.ToString(CultureInfo.InvariantCulture)}";
        var decision = hasItem
            ? ResolveDecision(lootItem, itemId, item)
            : new RollDecision(
                ResultMerge(MapBaseMode(configuration.LootMode), GetHardCap(lootItem)),
                $"base={configuration.LootMode}, itemSheet=missing");
        if (IsSameLootAttempt(itemId, index) && now - lastAttemptUtc < SameLootRetryDelay)
        {
            Status = $"Loot {configuration.LootMode}; waiting {GetRemainingSeconds(lastAttemptUtc + SameLootRetryDelay):0.0}s to retry {itemName}.";
            return;
        }

        if (TryRoll(decision.Result, index))
        {
            lastAttemptItemId = itemId;
            lastAttemptIndex = index;
            lastAttemptResult = decision.Result;
            lastAttemptUtc = now;
            nextRollAttemptUtc = now + RollCooldown;
            Status = $"Loot {configuration.LootMode}; {FormatRollResult(decision.Result)} {itemName}.";
            log.Information(
                $"[ADS][Loot] Rolled {FormatRollResult(decision.Result)} on {EscapeLogText(itemName)} ({itemId}) slot={index.ToString(CultureInfo.InvariantCulture)}; {decision.Reason}");
            return;
        }

        nextRollAttemptUtc = now + RollCooldown;
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

    private void ResetOwnershipLatch()
    {
        activeOwnershipKey = string.Empty;
        lazyLootDisabledForOwnership = false;
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

    private void TryRestoreMinimizedLoot()
    {
        var now = DateTime.UtcNow;
        if (now < nextRestoreAttemptUtc)
        {
            Status = $"Loot {configuration.LootMode}; minimized loot restore cooling down.";
            return;
        }

        nextRestoreAttemptUtc = now + RestoreCooldown;
        if (GameInteractionHelper.TryFireAddonCallback("_Notification", true, 0, 2))
        {
            Status = "Loot notification restored; waiting for NeedGreed.";
            log.Information("[ADS][Loot] Restored minimized loot via _Notification true 0 2.");
            return;
        }

        Status = "Loot notification visible; restore callback failed.";
        log.Warning("[ADS][Loot] Failed minimized loot restore via _Notification true 0 2.");
    }

    private RollDecision ResolveDecision(LootItem lootItem, uint itemId, Item item)
    {
        var desired = MapBaseMode(configuration.LootMode);
        var reason = $"base={configuration.LootMode}";

        if (configuration.LootRegistrableNeedingEnabled
            && TryClassifyRegistrable(itemId, item, out var category, out var categoryLabel, out var registrationItemIds)
            && IsCategoryEnabled(category))
        {
            var inventoryCount = GetInventoryCount(itemId);
            var alreadyRegistered = IsAlreadyRegistered(itemId, registrationItemIds);
            desired = inventoryCount > 0 || alreadyRegistered
                ? RollResult.Passed
                : RollResult.Needed;
            reason = $"registrable={categoryLabel}, inventory={inventoryCount.ToString(CultureInfo.InvariantCulture)}, registered={alreadyRegistered}";
        }

        var hardCap = GetHardCap(lootItem, itemId, item);
        return new RollDecision(ResultMerge(desired, hardCap), reason);
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

    private RollResult GetHardCap(LootItem lootItem, uint itemId, Item item)
    {
        var stateMax = GetRollStateCap(lootItem);
        if (item.IsUnique && (GetInventoryCount(itemId) > 0 || IsItemActionUnlocked(itemId)))
            stateMax = RollResult.Passed;

        return ResultMerge(stateMax, GetLootModeCap(lootItem));
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

    private unsafe bool TryGetNextLootItem(out uint index, out LootItem lootItem)
    {
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
        nextRollAttemptUtc = DateTime.MinValue;
        lastAttemptItemId = 0;
        lastAttemptIndex = 0;
        lastAttemptResult = RollResult.UnAwarded;
        lastAttemptUtc = DateTime.MinValue;
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
