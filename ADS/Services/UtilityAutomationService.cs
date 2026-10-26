using System.Globalization;
using System.Numerics;
using ADS.Models;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Command;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace ADS.Services;

public sealed unsafe class UtilityAutomationService
{
    private enum UtilityTask
    {
        None,
        SelfRepair,
        NpcRepair,
        ExtractMateria,
        DesynthFromInventory,
        ShopPurchase,
        ShopListBatch,
        GearMove,
        AutoEquip,
    }

    private enum NpcRepairMode
    {
        InnFallback,
        NoInn,
        NoTeleportNoInn,
        YesInn,
    }

    private enum NpcRepairTravelStage
    {
        None,
        TeleportingToInnAethernet,
        TeleportingToFieldAetheryte,
        WalkingInnPath,
        AwaitingRepairNpc,
        ExitingInn,
        ReturningToInn,
    }

    private const uint RepairShopEventId = 720915;
    private const float RepairNpcSearchRadius = 80.0f;
    private const float NoTeleportNoInnRepairNpcSearchRadius = 120.0f;
    private const float RepairNpcInteractRadius = 3.0f;
    private const float InnPathWaypointReachedRadius = 4.0f;
    private const int SelfRepairGeneralAction = 6;
    private const int DismountGeneralAction = 23;
    private const int MaterializeGeneralAction = 14;
    private const float NoInnRepairAetheryteRadius = 50.0f;
    private const float NoInnRepairAetheryteRadiusSquared = NoInnRepairAetheryteRadius * NoInnRepairAetheryteRadius;
    private const int LastMaterializeCategory = 6;
    private const int FullyRepairedConditionPercent = 100;
    private static readonly AgentSalvage.SalvageItemCategory[] AllDesynthCategories = Enum.GetValues<AgentSalvage.SalvageItemCategory>();

    private static readonly TimeSpan OverallTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan NpcRepairTravelTimeout = TimeSpan.FromSeconds(75);
    private static readonly TimeSpan UiRetryCooldown = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan UiSettleCooldown = TimeSpan.FromMilliseconds(1400);
    private static readonly TimeSpan RepairConfirmRetryCooldown = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DesynthReopenCooldown = TimeSpan.FromMilliseconds(1800);
    private static readonly TimeSpan LifestreamTeleportSettleCooldown = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan NpcRepairFieldRouteLogCooldown = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MoveRetryCooldown = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan InteractRetryCooldown = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MenuRetryCooldown = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan MaterializeResultWait = TimeSpan.FromMilliseconds(800);
    private const int RepairConfirmMaxAttempts = 3;
    private static readonly InnRepairRouteSeed[] InnRepairRouteSeeds =
    [
        new(
            220,
            [
                new Vector3(-161.9f, -15.0f, 205.0f),
            ]),
        new(
            185,
            [
                new Vector3(-89.6f, 1.3f, 25.7f),
                new Vector3(-99.5f, 3.9f, 5.2f),
            ]),
        new(
            152,
            [
                new Vector3(36.4f, 0.0f, 219.9f),
                new Vector3(47.1f, 1.7f, 223.5f),
                new Vector3(62.1f, 1.7f, 245.5f),
            ]),
        new(
            116,
            [
                new Vector3(-79.3f, 18.0f, -171.9f),
                new Vector3(-86.3f, 18.1f, -182.9f),
                new Vector3(-86.3f, 19.0f, -196.9f),
            ]),
        new(
            80,
            [
                new Vector3(84.2f, 24.0f, 20.0f),
                new Vector3(84.3f, 24.0f, 27.3f),
                new Vector3(78.4f, 24.0f, 30.4f),
                new Vector3(79.6f, 19.5f, 42.3f),
                new Vector3(92.0f, 15.0f, 41.9f),
                new Vector3(87.3f, 15.0f, 35.0f),
            ]),
        new(
            94,
            [
                new Vector3(40.0f, -18.8f, 102.8f),
                new Vector3(40.1f, -10.4f, 122.5f),
                new Vector3(35.0f, -8.2f, 128.3f),
                new Vector3(27.3f, -8.2f, 125.2f),
                new Vector3(27.9f, -8.0f, 100.4f),
            ]),
        new(
            33,
            [
                new Vector3(53.7f, 4.0f, -126.0f),
                new Vector3(44.3f, 8.0f, -122.3f),
                new Vector3(33.7f, 8.0f, -122.1f),
                new Vector3(30.4f, 8.0f, -114.4f),
                new Vector3(42.7f, 8.0f, -98.8f),
                new Vector3(31.5f, 7.0f, -82.0f),
            ]),
        new(
            41,
            [
                new Vector3(0.6f, 40.0f, 72.1f),
                new Vector3(1.6f, 39.5f, 16.5f),
                new Vector3(11.0f, 40.0f, 13.8f),
            ]),
    ];

    private static readonly HashSet<uint> FieldRepairDeniedAetheryteIds =
    [
        2,
        8,
        9,
        24,
        70,
        111,
        128,
        133,
        182,
        183,
        210,
        211,
        220,
    ];

    private static readonly uint[] PreferredFieldRepairAetheryteIds =
    [
        100, // Ala Gannha - Independent Mender observed near the aetheryte.
        108, // The House of the Fierce - Independent Mender observed near the aetheryte.
    ];

    private static readonly string[] FieldRepairDeniedNameTerms =
    [
        "limsa lominsa",
        "gridania",
        "ul'dah",
        "uldah",
        "foundation",
        "ishgard",
        "idyllshire",
        "rhalgr",
        "kugane",
        "crystarium",
        "eulmore",
        "old sharlayan",
        "radz-at-han",
        "tuliyollal",
        "solution nine",
        "gold saucer",
        "wolves' den",
        "doman enclave",
        "revenant's toll",
        "baldesion annex",
        "residential",
        "estate",
        "apartment",
        "inn",
        "mizzenmast",
        "roost",
        "hourglass",
        "forgotten knight",
        "pendants",
        "andron",
        "for'ard",
    ];

    private readonly IDataManager dataManager;
    private readonly IObjectTable objectTable;
    private readonly ITargetManager targetManager;
    private readonly ICommandManager commandManager;
    private readonly IClientState clientState;
    private readonly ICondition condition;
    private readonly IPluginLog log;
    private readonly Configuration configuration;
    private readonly DesynthPolicyService desynthPolicyService;
    private readonly DesynthPresetStore desynthPresetStore;
    private readonly DesynthDutyLedgerStore desynthDutyLedgerStore;
    private readonly ShopCatalogService shopCatalogService;
    private readonly ShopPurchaseRunner shopPurchaseRunner;
    private readonly DalamudShopPurchaseRuntime shopPurchaseRuntime;
    private readonly IShopPurchaseRuntime shopRuntime;
    private readonly Func<bool> isDutyOwned;
    private readonly Func<bool> isInnEntryRunning;
    private readonly InnEntryService? innEntryService;
    private readonly Dictionary<uint, int?> repairIndexCache = [];
    private Lumina.Excel.ExcelSheet<Aetheryte>? aetheryteSheet;
    private Lumina.Excel.ExcelSheet<ENpcBase>? enpcBaseSheet;
    private bool cachedLifestreamLoaded;
    private DateTime lifestreamCacheExpiresUtc = DateTime.MinValue;

    private UtilityTask activeTask = UtilityTask.None;
    private bool npcSaleRequest;
    private bool npcSaleCommandSent;
    private bool npcSaleBusyObserved;
    private bool npcSaleOwnsMovement;
    private ulong npcSaleCharacter;
    private NpcSaleStatusSnapshot npcSaleStatus = new(null, false, false, null, "Idle");
    private GearCleanupSelection? gearCleanupSelection;
    private bool gearCleanupSelling;
    private int gearCleanupIndex;
    private bool gearCleanupPending;
    private InventoryType gearCleanupDestination;
    private ushort gearCleanupDestinationSlot;
    private ShopNpcSheetLink? gearCleanupVendor;
    private bool gearCleanupInteracted;
    private uint gearCleanupLastMenuHandler;
    private ulong gearCleanupConfirmationCharacter;
    private long gearCleanupGilBefore = -1;
    private DateTime gearCleanupSettlementReadyUtc;
    private GearSaleStatusSnapshot gearSaleStatus = new(null, string.Empty, false, false, null, 0, 0, 0, "Idle");
    private string gearSaleLastStartError = string.Empty;
    private ulong autoEquipCharacter;
    private byte autoEquipJob;
    private int autoEquipGearset = -1;
    private uint[]? autoEquipExpected;
    private DateTime autoEquipSubmittedUtc;
    public GearSaleStatusSnapshot GearSaleStatus
    {
        get
        {
            if (!gearSaleStatus.Running && !gearSaleStatus.Settled
                && shopRuntime.GearSaleUiSettled)
                gearSaleStatus = gearSaleStatus with { Settled = true };
            return gearSaleStatus with { StatusMessage = gearSaleStatus.Running ? StatusMessage : gearSaleStatus.StatusMessage,
                LastStartError = gearSaleLastStartError };
        }
    }
    private string npcSaleLastStartError = string.Empty;
    public NpcSaleStatusSnapshot NpcSaleStatus
    {
        get
        {
            var status = npcSaleStatus with { LastStartError = npcSaleLastStartError };
            if (status.Running)
                return status with { StatusMessage = StatusMessage, CommandSent = npcSaleCommandSent, Settled = false };
            if (!status.CommandSent) return status;
            try
            {
                var busy = Plugin.PluginInterface.GetIpcSubscriber<bool>("AutoRetainer.PluginState.IsBusy").InvokeFunc();
                return status with { Settled = !busy && !HasUnownedNpcSaleUi() };
            }
            catch { return status with { Settled = false }; }
        }
    }
    internal bool IsLocalNpcSaleRunning => npcSaleRequest && npcSaleStatus.Running
        && activeTask == UtilityTask.NpcRepair && activeNpcRepairMode == NpcRepairMode.NoTeleportNoInn;
    internal bool IsNpcSaleRunning => npcSaleStatus.Running;
    private NpcRepairMode activeNpcRepairMode = NpcRepairMode.InnFallback;
    private DateTime startedAtUtc = DateTime.MinValue;
    private DateTime lastActionUtc = DateTime.MinValue;
    private DateTime lastMoveCommandUtc = DateTime.MinValue;
    private DateTime lastMovementProgressUtc = DateTime.MinValue;
    private Vector3 lastMovementProgressPosition;
    private DateTime lastInteractUtc = DateTime.MinValue;
    private DateTime lastMenuSelectionUtc = DateTime.MinValue;
    private DateTime repairWindowSeenUtc = DateTime.MinValue;
    private ulong targetNpcGameObjectId;
    private uint targetNpcBaseId;
    private string targetNpcName = string.Empty;
    private int targetNpcRepairIndex;
    private ulong npcRepairUiCharacter;
    private NpcRepairTravelStage npcRepairTravelStage = NpcRepairTravelStage.None;
    private DateTime npcRepairTravelStageStartedUtc = DateTime.MinValue;
    private DateTime npcRepairTravelCommandUtc = DateTime.MinValue;
    private DateTime npcRepairInnArrivalReadyUtc = DateTime.MinValue;
    private ResolvedInnRepairRoute? activeNpcRepairInnRoute;
    private ResolvedFieldRepairRoute? activeNpcRepairFieldRoute;
    private readonly HashSet<uint> failedNpcRepairFieldAetheryteIds = [];
    private uint npcRepairFieldRouteStartTerritoryId;
    private bool npcRepairFieldRouteSawLoading;
    private string lastNpcRepairFieldRouteFailure = string.Empty;
    private int npcRepairFieldRouteFailureCount;
    private DateTime lastNpcRepairFieldRouteScanLogUtc = DateTime.MinValue;
    private DateTime lastNpcRepairFieldRouteWaitLogUtc = DateTime.MinValue;
    private int npcRepairInnPathIndex;
    private uint npcRepairStartingInnTerritory;
    private uint npcRepairRequiredInnTerritory;
    private uint npcRepairDestinationAethernet;
    private bool npcRepairMenderReached;
    private bool npcRepairOwnsInnTravel;
    private bool npcRepairInnEntryStarted;
    private bool npcRepairDoorInteracted;
    private bool repairSubmissionSent;
    private DateTime lastRepairConfirmClickUtc = DateTime.MinValue;
    private int repairConfirmClickAttempts;
    private int materializeCategory;
    private bool materializeCategoryArmed;
    private bool materializeAttemptPending;
    private bool extractAttemptedAny;
    private bool extractMateriaDone;
    private bool? extractMateriaSucceeded;
    private string extractMateriaStatusMessage = "No materia extraction has been started.";
    private string extractMateriaSuccessMessage = string.Empty;
    private string extractMateriaFailureMessage = string.Empty;
    private DateTime extractMateriaCompletedUtc = DateTime.MinValue;
    private int desynthCategoryIndex;
    private DateTime desynthWindowSeenUtc = DateTime.MinValue;
    private DateTime desynthCategorySeenUtc = DateTime.MinValue;
    private int desynthSettledCategoryIndex = -1;
    private bool desynthAttemptedAny;
    private DesynthPolicy? activeDesynthPolicy;
    private uint pendingDesynthItemId;
    private float maximumDesynthLevel;
    private HashSet<uint>? desynthGearsetItemIds;
    private string lastDesynthModeName = string.Empty;
    private string lastDesynthSourceName = string.Empty;
    private string lastDesynthScopeName = string.Empty;
    private string lastDesynthPresetName = string.Empty;
    private DateTime nextSlowUtilityLogUtc = DateTime.MinValue;
    private IReadOnlyList<ShopListBatchItem> shopListBatchItems = [];
    private ShopListBatchDefinition? shopListBatchDefinition;
    private List<ShopListBatchRowStatus> shopListBatchRows = [];
    private readonly HashSet<Guid> shopListBatchCompletedNonRepeatableRowIds = [];
    private readonly HashSet<Guid> shopListBatchSkippedRowIds = [];
    private int shopListBatchIndex;
    private int shopListBatchCompletedRows;
    private int shopListBatchVerifiedPurchaseRows;
    private bool shopListSpendCyclePurchased;
    private bool shopListBatchChildAccepted;
    private int shopListBatchChildPurchasedBaseline;
    private bool shopListBatchCancelRequested;
    private string shopListBatchTerminalFailureMessage = string.Empty;
    private ShopOfferIdentity? shopListHeldShop;
    private ShopListBatchStatusSnapshot shopListBatchStatus = EmptyShopListBatchStatus();
    private (bool Succeeded, string Message, string? FailureCode)? shopListCleanup;
    private DateTime shopListCleanupStartedAtUtc;

    public UtilityAutomationService(
        IDataManager dataManager,
        IObjectTable objectTable,
        ITargetManager targetManager,
        ICommandManager commandManager,
        IClientState clientState,
        ICondition condition,
        Configuration configuration,
        DesynthPolicyService desynthPolicyService,
        DesynthPresetStore desynthPresetStore,
        DesynthDutyLedgerStore desynthDutyLedgerStore,
        Func<bool> isDutyOwned,
        Func<bool> isInnEntryRunning,
        IPluginLog log,
        InnEntryService? innEntryService = null)
    {
        this.dataManager = dataManager;
        this.objectTable = objectTable;
        this.targetManager = targetManager;
        this.commandManager = commandManager;
        this.clientState = clientState;
        this.condition = condition;
        this.configuration = configuration;
        this.desynthPolicyService = desynthPolicyService;
        this.desynthPresetStore = desynthPresetStore;
        this.desynthDutyLedgerStore = desynthDutyLedgerStore;
        this.log = log;
        this.isDutyOwned = isDutyOwned;
        this.isInnEntryRunning = isInnEntryRunning;
        this.innEntryService = innEntryService;
        Action<string> shopDiagnostic = message => log.Information("[ADS][Shop] {Diagnostic}", message);
        shopCatalogService = new ShopCatalogService(new LuminaShopSheetSource(dataManager, log), shopDiagnostic);
        shopPurchaseRuntime = new DalamudShopPurchaseRuntime(objectTable, targetManager, commandManager, clientState, condition, log);
        shopRuntime = shopPurchaseRuntime;
        shopPurchaseRunner = new ShopPurchaseRunner(
            shopCatalogService,
            shopPurchaseRuntime,
            new SystemShopPurchaseClock(),
            this.isDutyOwned,
            this.isInnEntryRunning,
            shopDiagnostic);
    }

    public bool IsRunning
        => activeTask != UtilityTask.None;

    internal UtilityAutomationService(ShopCatalogService catalog, IShopPurchaseRuntime runtime, IShopPurchaseClock clock, IPluginLog log)
        : this(null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, () => false, () => false, log)
    {
        shopCatalogService = catalog;
        shopRuntime = runtime;
        shopPurchaseRunner = new(catalog, runtime, clock);
    }

    public bool SuppressesGenericYesNo
        => activeTask is UtilityTask.SelfRepair or UtilityTask.NpcRepair or UtilityTask.ShopPurchase or UtilityTask.ShopListBatch or UtilityTask.GearMove or UtilityTask.AutoEquip
            || SuppressesStoppedGearSaleConfirmation();

    private bool SuppressesStoppedGearSaleConfirmation()
    {
        if (gearCleanupConfirmationCharacter == 0) return false;
        if (!clientState.IsLoggedIn || gearCleanupConfirmationCharacter != Plugin.PlayerState.ContentId
            || (!shopRuntime.IsAnyShopVisible && !shopRuntime.HasUnexpectedConfirmation))
        {
            gearCleanupConfirmationCharacter = 0;
            return false;
        }
        return true;
    }

    public string StatusMessage { get; private set; } = "Idle";

    public string ActiveTaskName
        => gearCleanupSelection != null ? gearCleanupSelling ? "equipment selling" : "equipment moving"
            : npcSaleRequest ? "NPC selling" : activeTask == UtilityTask.None ? string.Empty : GetTaskLabel(activeTask);

    public string ActiveModeName
        => activeTask switch
        {
            UtilityTask.SelfRepair => "self",
            UtilityTask.NpcRepair when gearCleanupSelling => "gear-sell",
            UtilityTask.NpcRepair when npcSaleRequest => "npc-sell",
            UtilityTask.NpcRepair => activeNpcRepairMode switch
            {
                NpcRepairMode.NoInn => "npc-no-inn",
                NpcRepairMode.NoTeleportNoInn => "npc-no-teleport-no-inn",
                NpcRepairMode.YesInn => "npc-yes-inn",
                _ => "npc",
            },
            UtilityTask.ExtractMateria => "extract-materia",
            UtilityTask.DesynthFromInventory => "desynth-inventory",
            UtilityTask.ShopPurchase => "shop-purchase",
            UtilityTask.ShopListBatch => "shop-list-batch",
            UtilityTask.GearMove => "gear-move",
            UtilityTask.AutoEquip => "auto-equip",
            _ => string.Empty,
        };

    public string LastSuccessMessage { get; private set; } = string.Empty;
    public string LastFailureMessage { get; private set; } = string.Empty;
    public DateTime LastCompletionUtc { get; private set; } = DateTime.MinValue;
    public IReadOnlyList<string> DesynthCategoryNames { get; } = DesynthPolicyService.AllLegacyCategoryNames;
    public string ActiveDesynthModeName => activeDesynthPolicy is { } policy ? GetDesynthModeName(policy.Mode) : lastDesynthModeName;
    public string ActiveDesynthSourceName => activeDesynthPolicy?.Source.ToString() ?? (string.IsNullOrEmpty(lastDesynthSourceName) ? configuration.DesynthSource.ToString() : lastDesynthSourceName);
    public string ActiveDesynthScopeName => activeDesynthPolicy is { } policy
        ? DesynthPolicyService.GetScopeName(policy.Scope)
        : string.IsNullOrEmpty(lastDesynthScopeName)
            ? DesynthPolicyService.GetScopeName(configuration.DesynthInventoryScope)
            : lastDesynthScopeName;
    public string ActiveDesynthPresetName => activeDesynthPolicy?.PresetName ?? (string.IsNullOrEmpty(lastDesynthPresetName) ? configuration.DesynthActivePreset : lastDesynthPresetName);
    public int DesynthEligibleCount { get; private set; }
    public int DesynthCompletedCount { get; private set; }
    public bool IsDesynthRunning => activeTask == UtilityTask.DesynthFromInventory;
    public string LastDesynthSuccessMessage { get; private set; } = string.Empty;
    public string LastDesynthFailureMessage { get; private set; } = string.Empty;
    public bool IsExtractMateriaRunning => activeTask == UtilityTask.ExtractMateria;
    public bool IsShopPurchaseRunning => activeTask == UtilityTask.ShopPurchase && shopPurchaseRunner.IsRunning;
    public ShopPurchaseStatusSnapshot ShopPurchaseStatus => shopPurchaseRunner.Status;
    internal bool HasShopPurchaseSubmission => shopPurchaseRunner.HasPurchaseSubmission;
    internal bool IsRelicPurchaseReady => shopPurchaseRuntime.IsRelicPurchaseReady;
    internal static bool TryGetInnDestination(string destination, out uint aethernetId, out uint room)
    {
        aethernetId = destination.Trim().ToLowerInvariant() switch
        {
            "uldah" => 33,
            "gridania" => 94,
            "limsa" => 41,
            "ishgard" => 80,
            "crystarium" => 152,
            "sharlayan" => 185,
            "tuliyollal" => 220,
            _ => 0,
        };
        room = GetInnRoom(aethernetId);
        return aethernetId != 0;
    }

    private static uint GetInnRoom(uint aethernetId) => aethernetId switch
    {
        33 => 178, 94 => 179, 41 => 177, 80 => 429,
        116 => 629, 152 => 843, 185 => 990, 220 => 1205,
        _ => 0,
    };
    internal ShopNavigationStopResult StopRelicPurchaseNavigation() => shopPurchaseRuntime.TryStopNavigation();
    internal string? RelicPurchaseCleanupBlocker => IsRunning ? "ADS utility cleanup is still active"
        : shopPurchaseRuntime.RelicPurchaseCleanupBlocker;
    public ShopListBatchStatusSnapshot ShopListBatchStatus => shopListBatchStatus;

    internal ShopPurchasePreviewResult PreviewShopPurchase(uint itemId, int quantity)
        => ShopPurchaseRequest.TryCreate(itemId, quantity, out var request, out var error)
            ? shopPurchaseRunner.Preview(request)
            : new ShopPurchasePreviewResult(
                default,
                string.Empty,
                null,
                [],
                ShopPurchaseFailureCodes.InvalidRequest,
                error,
                0,
                0,
                0,
                0);

    internal ShopPurchasePreviewResult PreviewShopPurchase(
        uint itemId,
        int quantity,
        ShopCurrencyIdentity requiredCurrency)
        => ShopPurchaseRequest.TryCreate(itemId, quantity, out var request, out var error)
            ? shopPurchaseRunner.Preview(request, requiredCurrency)
            : new ShopPurchasePreviewResult(
                default,
                string.Empty,
                null,
                [],
                ShopPurchaseFailureCodes.InvalidRequest,
                error,
                0,
                0,
                0,
                0);

    internal ShopPurchasePreviewResult PreviewShopPurchaseAtLeast(
        uint itemId,
        int minimumQuantity,
        ShopCurrencyIdentity requiredCurrency)
    {
        ShopPurchasePreviewResult? best = null;
        foreach (var bundle in shopCatalogService.GetReceiveBundleCounts(itemId))
        {
            var quantity = checked(((minimumQuantity + bundle - 1) / bundle) * bundle);
            if (quantity > ShopPurchaseRequest.MaximumQuantity)
                continue;
            var candidate = PreviewShopPurchase(itemId, quantity, requiredCurrency);
            if (candidate.CanPurchase && (best == null || candidate.Request.Quantity < best.Request.Quantity))
                best = candidate;
        }

        return best ?? PreviewShopPurchase(itemId, Math.Clamp(minimumQuantity, 1, ShopPurchaseRequest.MaximumQuantity), requiredCurrency);
    }

    internal ShopPurchasePreviewResult PreviewOrderShopPurchase(uint itemId, int remaining, ShopCurrencyIdentity currency)
    {
        var complete = PreviewShopPurchaseAtLeast(itemId, remaining, currency);
        return complete.CanPurchase ? complete : PreviewMaximumShopPurchase(itemId, currency, remaining);
    }

    internal ShopPurchasePreviewResult PreviewMaximumShopPurchase(
        uint itemId,
        ShopCurrencyIdentity requiredCurrency,
        int targetRemaining = ShopPurchaseRequest.MaximumQuantity)
    {
        ShopPurchasePreviewResult? best = null;
        ShopPurchasePreviewResult? lastFailure = null;
        foreach (var bundle in shopCatalogService.GetReceiveBundleCounts(itemId))
        {
            var low = 1;
            var high = Math.Min(ShopPurchaseRequest.MaximumQuantity / bundle, (targetRemaining + bundle - 1) / bundle);
            while (low <= high)
            {
                var transactions = low + ((high - low) / 2);
                var candidate = PreviewShopPurchase(itemId, transactions * bundle, requiredCurrency);
                if (candidate.CanPurchase)
                {
                    if (best == null || candidate.Request.Quantity > best.Request.Quantity)
                        best = candidate;
                    low = transactions + 1;
                }
                else if (candidate.FailureCode is ShopPurchaseFailureCodes.InsufficientCurrency
                         or ShopPurchaseFailureCodes.InventoryCapacity)
                {
                    lastFailure = candidate;
                    high = transactions - 1;
                }
                else
                {
                    lastFailure = candidate;
                    break;
                }
            }
        }

        return best ?? lastFailure ?? PreviewShopPurchase(itemId, 1, requiredCurrency);
    }

    internal ShopPurchasePreviewResult PreviewAnyShopPurchaseBundle(
        uint itemId,
        ShopCurrencyIdentity requiredCurrency)
    {
        ShopPurchasePreviewResult? resourceLimited = null;
        ShopPurchasePreviewResult? structuralFailure = null;
        foreach (var bundle in shopCatalogService.GetReceiveBundleCounts(itemId))
        {
            var candidate = PreviewShopPurchase(itemId, bundle, requiredCurrency);
            if (candidate.CanPurchase)
                return candidate;
            if (candidate.FailureCode is ShopPurchaseFailureCodes.InsufficientCurrency
                or ShopPurchaseFailureCodes.InventoryCapacity)
            {
                resourceLimited ??= candidate;
            }
            else
            {
                structuralFailure ??= candidate;
            }
        }

        return resourceLimited ?? structuralFailure ?? PreviewShopPurchase(itemId, 1, requiredCurrency);
    }

    internal long GetAvailableShopCurrency(ShopCurrencyIdentity currency)
        => shopRuntime.GetAvailableCurrency(new ShopCurrencyCost(currency.Kind, currency.ItemId, string.Empty, 1));

    internal ShopCatalogSearchResponse SearchShopCatalog(string? query, ShopCurrencyIdentity? currency, int limit, uint itemIdFilter = 0)
        => shopCatalogService.Search(query, currency, limit, itemIdFilter);

    internal long GetLiveShopItemCount(uint itemId)
        => shopRuntime.GetItemCount(itemId);

    internal ulong ShopCharacterId => shopRuntime.CharacterId;

    /// <summary>Opt in to reusing one open shop across consecutive purchases. See ShopPurchaseRunner.KeepShopOpen.</summary>
    public bool ShopKeepOpen
    {
        get => shopPurchaseRunner.KeepShopOpen;
        set => shopPurchaseRunner.KeepShopOpen = value;
    }

    /// <summary>Closes a shop left standing by <see cref="ShopKeepOpen"/> and reports whether there was
    /// one. <see cref="Cancel"/> cannot: it early-returns unless a run is active, which a finished chain
    /// never is.</summary>
    public bool ReleaseHeldShopUi() => shopPurchaseRunner.ReleaseHeldShopUi();
    internal void ContinueRelicPurchaseCleanup() => shopPurchaseRuntime.ContinueRelicUiCleanup();
    public bool ExtractMateriaDone => extractMateriaDone;
    public bool? ExtractMateriaSucceeded => extractMateriaSucceeded;
    public string ExtractMateriaStatusMessage => IsExtractMateriaRunning ? StatusMessage : extractMateriaStatusMessage;
    public string ExtractMateriaSuccessMessage => extractMateriaSuccessMessage;
    public string ExtractMateriaFailureMessage => extractMateriaFailureMessage;
    public DateTime ExtractMateriaCompletedUtc => extractMateriaCompletedUtc;

    public bool StartSelfRepair()
    {
        if (IsRepairBlockedByMountedState(UtilityTask.SelfRepair))
            return false;

        if (!TryStartTask(UtilityTask.SelfRepair, "Starting self-repair."))
            return false;

        log.Information("[ADS][Utility] Starting self-repair flow.");
        return true;
    }

    public bool StartNpcRepair()
        => StartNpcRepair(NpcRepairMode.InnFallback);

    public bool StartNpcRepairNoInn()
        => StartNpcRepair(NpcRepairMode.NoInn);

    public bool StartNpcRepairNoTeleportNoInn()
        => StartNpcRepair(NpcRepairMode.NoTeleportNoInn);

    public bool StartNpcRepairYesInn(string? destination = null)
    {
        uint aethernet = 0, room = 0;
        if (destination != null && !TryGetInnDestination(destination, out aethernet, out room))
        {
            StatusMessage = $"Unknown inn destination: {destination}.";
            return false;
        }
        return StartNpcRepair(NpcRepairMode.YesInn, aethernet, room);
    }

    public bool StartNpcSale(string operationId, bool localOnly)
    {
        npcSaleLastStartError = string.Empty;
        if (string.IsNullOrWhiteSpace(operationId) || operationId.Length > 128)
            return RejectNpcSaleStart(operationId, "NPC selling requires a valid operation ID.");
        if (IsRunning) return RejectNpcSaleStart(operationId, $"ADS is already running {ActiveTaskName}.");
        if (isDutyOwned() || isInnEntryRunning())
            return RejectNpcSaleStart(operationId, "NPC selling cannot start while duty or inn automation owns ADS.");
        if (!clientState.IsLoggedIn || objectTable.LocalPlayer == null || Plugin.PlayerState.ContentId == 0)
            return RejectNpcSaleStart(operationId, "NPC selling requires a logged-in character.");
        if (condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51])
            return RejectNpcSaleStart(operationId, "Cannot start NPC selling while zoning.");
        if (HasUnownedNpcSaleUi())
            return RejectNpcSaleStart(operationId, "Close existing shop or confirmation UI before requesting an NPC sale.");
        try
        {
            if (Plugin.PluginInterface.GetIpcSubscriber<bool>("AutoRetainer.PluginState.IsBusy").InvokeFunc())
                return RejectNpcSaleStart(operationId, "AutoRetainer already owns work; NPC selling was not started.");
        }
        catch (Exception ex) { return RejectNpcSaleStart(operationId, $"AutoRetainer selling is unavailable: {ex.Message}"); }
        try
        {
            var accepted = StartNpcRepair(localOnly ? NpcRepairMode.NoTeleportNoInn : NpcRepairMode.NoInn,
                saleOperationId: operationId);
            return accepted || RejectNpcSaleStart(operationId, StatusMessage);
        }
        catch (Exception ex)
        {
            var message = $"NPC selling could not start: {ex.Message}";
            if (npcSaleRequest) Fail(message);
            else StatusMessage = message;
            return RejectNpcSaleStart(operationId, message);
        }
    }

    public bool RejectNpcSaleStart(string operationId, string message)
    {
        npcSaleLastStartError = message;
        if (!IsRunning)
        {
            StatusMessage = message;
            npcSaleStatus = new(operationId, false, true, false, message);
        }
        return false;
    }

    public bool CancelNpcSale(string operationId)
    {
        if (!npcSaleRequest || !npcSaleStatus.Running || npcSaleStatus.OperationId != operationId) return false;
        Cancel("matching NPC sale cancelled");
        return true;
    }

    public bool TryPreviewGearSale(GearCleanupScope scope, out GearCleanupSelection? selection)
        => TryCaptureGearCleanupSelection(scope, true, out selection);

    public (int BagsFree, int ArmouryFree, int BagGear, int ArmouryGear)? ReadInventorySummary()
    {
        if (!clientState.IsLoggedIn) return null;
        var manager = InventoryManager.Instance();
        if (manager == null
            || !shopRuntime.TryCaptureGearCleanup(GearCleanupScope.Both, out var items, out var protection)) return null;
        var armouryFree = 0;
        foreach (var type in GearCleanupPolicy.Containers(GearCleanupScope.Armoury, allUnprotected: true))
        {
            var container = manager->GetInventoryContainer(type);
            if (container == null || !container->IsLoaded) return null;
            for (var index = 0; index < container->Size; index++)
            {
                var item = container->GetInventorySlot(index);
                if (item == null) return null;
                if (item->ItemId == 0 && !item->IsSymbolic) armouryFree++;
            }
        }
        return (checked((int)manager->GetEmptySlotsInBag()), armouryFree,
            items.Count(item => GearCleanupPolicy.IsEligible(item, GearCleanupScope.Inventory, true, protection)),
            items.Count(item => GearCleanupPolicy.IsEligible(item, GearCleanupScope.Armoury, true, protection)));
    }

    public bool TryPreviewGearSale(GearCleanupScope scope, out GearCleanupSelection? selection, out string message)
    {
        var previous = StatusMessage;
        try
        {
            var accepted = TryPreviewGearSale(scope, out selection);
            message = StatusMessage;
            return accepted;
        }
        finally { StatusMessage = previous; }
    }

    private bool TryCaptureGearCleanupSelection(GearCleanupScope scope, bool selling, out GearCleanupSelection? selection,
        bool allUnprotected = false)
    {
        selection = null;
        if (!Enum.IsDefined(scope) || IsRunning)
        { StatusMessage = "Equipment cleanup cannot start with the current selection or ownership."; return false; }
        if (!CanRunGearCleanup(requireClosedUi: true)) return false;
        if (!shopRuntime.TryCaptureGearCleanup(scope, out var items, out var protection))
        { StatusMessage = "Equipment cleanup requires readable inventory and gearset protection."; return false; }
        var eligible = items.Where(item => GearCleanupPolicy.IsEligible(item, scope, selling, protection, allUnprotected)).ToArray();
        if (eligible.Length == 0)
        { StatusMessage = allUnprotected ? "No eligible unprotected equipment was found." : "No eligible white-name equipment was found."; return false; }
        selection = new(Plugin.PlayerState.ContentId, scope, Array.AsReadOnly(eligible), allUnprotected);
        StatusMessage = $"Equipment sale preview: {eligible.Length} items.";
        return true;
    }

    public bool StartGearMove(bool allUnprotected = false)
        => TryCaptureGearCleanupSelection(GearCleanupScope.Armoury, false, out var selection, allUnprotected)
            && StartGearCleanup(selection!, selling: false);

    public bool StartAutoEquip()
    {
        if (!CanRunGearCleanup(requireClosedUi: true)) return false;
        var recommended = RecommendEquipModule.Instance();
        var gearsets = RaptureGearsetModule.Instance();
        var job = objectTable.LocalPlayer?.ClassJob.RowId ?? 0;
        if (recommended == null || recommended->IsUpdating || job is 0 or > byte.MaxValue)
        { StatusMessage = "Recommended Gear is unavailable or already in use."; return false; }
        if (!TryStartTask(UtilityTask.AutoEquip, "Calculating recommended gear for the current job.")) return false;
        autoEquipCharacter = Plugin.PlayerState.ContentId;
        autoEquipJob = (byte)job;
        if (gearsets != null && gearsets->CurrentGearsetIndex is >= 0 and < 100
            && gearsets->IsValidGearset(gearsets->CurrentGearsetIndex)
            && gearsets->GetGearset(gearsets->CurrentGearsetIndex)->ClassJob == job)
            autoEquipGearset = gearsets->CurrentGearsetIndex;
        // The native Boolean is unreliable; completion is reported by IsUpdating.
        recommended->SetupForClassJob(autoEquipJob);
        return true;
    }

    private static uint EquipmentItemId(InventoryItem item)
        => item.ItemId == 0 ? 0 : DesynthPolicyService.NormalizeBaseItemId(item.ItemId)
            + ((item.Flags & InventoryItem.ItemFlags.HighQuality) != 0 ? 1_000_000u : 0);

    private void UpdateAutoEquip()
    {
        var recommended = RecommendEquipModule.Instance();
        var gearsets = RaptureGearsetModule.Instance();
        if (!CanRunGearCleanup(requireClosedUi: true) || autoEquipCharacter != Plugin.PlayerState.ContentId
            || objectTable.LocalPlayer?.ClassJob.RowId != autoEquipJob || recommended == null
            || recommended->ClassJob != autoEquipJob
            || (autoEquipGearset >= 0 && (gearsets == null || gearsets->CurrentGearsetIndex != autoEquipGearset
                || !gearsets->IsValidGearset(autoEquipGearset) || gearsets->GetGearset(autoEquipGearset)->ClassJob != autoEquipJob)))
        { Fail("Auto Equip stopped because its character, job, gearset or native ownership changed."); return; }
        if (recommended->IsUpdating)
        {
            if (DateTime.UtcNow - startedAtUtc > TimeSpan.FromSeconds(10))
                Fail("Recommended Gear did not finish calculating within 10 seconds.");
            return;
        }
        if (autoEquipExpected == null)
        {
            autoEquipExpected = new uint[recommended->RecommendedItems.Length];
            for (var index = 0; index < autoEquipExpected.Length; index++)
            {
                InventoryItem* item = recommended->RecommendedItems[index];
                if (item != null) autoEquipExpected[index] = EquipmentItemId(*item);
            }
            recommended->EquipRecommendedGear();
            autoEquipSubmittedUtc = DateTime.UtcNow;
            StatusMessage = "Waiting for recommended equipment to be verified.";
            return;
        }
        var manager = InventoryManager.Instance();
        var equipped = manager == null ? null : manager->GetInventoryContainer(InventoryType.EquippedItems);
        if (equipped == null || !equipped->IsLoaded || equipped->Size < autoEquipExpected.Length) return;
        var equippedIds = new uint[autoEquipExpected.Length];
        for (var index = 0; index < equippedIds.Length; index++)
        {
            var slot = equipped->GetInventorySlot(index);
            if (slot == null) return;
            equippedIds[index] = EquipmentItemId(*slot);
            if (autoEquipExpected[index] != 0 && equippedIds[index] != autoEquipExpected[index])
            {
                if (DateTime.UtcNow - autoEquipSubmittedUtc > TimeSpan.FromSeconds(10))
                    Fail("Auto Equip could not verify the recommended equipment; the gearset was not updated.");
                return;
            }
        }
        if (autoEquipGearset >= 0)
        {
            var result = gearsets->UpdateGearset(autoEquipGearset);
            if (result is < 0 or >= 100)
            { Fail("Recommended equipment is equipped, but the active gearset update was rejected."); return; }
            gearsets->SaveFile(false);
            var saved = gearsets->GetGearset(autoEquipGearset);
            if (saved->Items.Length != equippedIds.Length)
            { Fail("The updated gearset has an unexpected number of equipment slots."); return; }
            for (var index = 0; index < equippedIds.Length; index++)
                if (saved->Items[index].ItemId != equippedIds[index])
                { Fail("The saved gearset does not match the verified equipped items."); return; }
        }
        Complete(autoEquipGearset >= 0 ? "Recommended equipment and the active gearset were verified."
            : "Recommended equipment was verified; no matching active saved gearset was changed.");
    }

    public bool StartGearSale(GearCleanupSelection selection)
        => StartGearCleanup(selection, selling: true, operationId: $"manual-gear-{Guid.NewGuid():N}");

    public bool StartGearSale(string operationId, GearCleanupScope scope, bool localOnly)
    {
        if (IsRunning) return RejectGearSaleStart($"ADS is already running {ActiveTaskName}.");
        if (string.IsNullOrWhiteSpace(operationId) || operationId.Length > 128 || operationId != operationId.Trim()
            || operationId == gearSaleStatus.OperationId)
            return RejectGearSaleStart("Equipment selling requires a new, nonempty operation ID of at most 128 characters.");
        if (!TryCaptureGearCleanupSelection(scope, true, out var selection)) return RejectGearSaleStart(StatusMessage);
        var accepted = StartGearCleanup(selection!, true, operationId, localOnly);
        return accepted || RejectGearSaleStart(StatusMessage);
    }

    public bool RejectGearSaleStart(string message)
    { gearSaleLastStartError = message; return false; }

    public bool CancelGearSale(string operationId)
    {
        if (!gearSaleStatus.Running || gearSaleStatus.OperationId != operationId || !gearCleanupSelling) return false;
        Cancel("matching equipment sale cancelled");
        return true;
    }

    private bool StartGearCleanup(GearCleanupSelection selection, bool selling, string? operationId = null, bool localOnly = false)
    {
        if (IsRunning || selection.CharacterId == 0 || selection.CharacterId != Plugin.PlayerState.ContentId
            || !Enum.IsDefined(selection.Scope) || selection.Items.Count == 0
            || (!selling && selection.Scope != GearCleanupScope.Armoury) || !CanRunGearCleanup(requireClosedUi: true))
        { StatusMessage = "Equipment cleanup cannot start with the current selection or ownership."; return false; }
        var frozen = selection with { Items = Array.AsReadOnly(selection.Items.ToArray()) };
        if (frozen.Items.Select(item => (item.Container, item.Slot)).Distinct().Count() != frozen.Items.Count)
        { StatusMessage = "Equipment cleanup cannot start with the current selection or ownership."; return false; }
        if (!TryRevalidateGearCleanup(frozen, selling, frozen.Items, out _)) return false;
        if (selling)
        {
            gearSaleLastStartError = string.Empty;
            gearSaleStatus = new(operationId, selection.Scope.ToString().ToLowerInvariant(), true, false, null,
                frozen.Items.Count, 0, 0, "Starting equipment selling.", Settled: false);
            var accepted = StartNpcRepair(localOnly ? NpcRepairMode.NoTeleportNoInn : NpcRepairMode.NoInn, gearSale: frozen);
            if (!accepted && gearSaleStatus.Running) FinishGearSale(false, StatusMessage);
            return accepted;
        }
        if (!TryStartTask(UtilityTask.GearMove, frozen.AllUnprotected
                ? "Moving unprotected equipment into inventory." : "Moving white-name equipment into inventory.")) return false;
        gearCleanupSelection = frozen;
        return true;
    }

    private bool CanRunGearCleanup(bool requireClosedUi, bool allowOwnedTravel = false)
    {
        if (!clientState.IsLoggedIn || (!allowOwnedTravel && objectTable.LocalPlayer == null) || Plugin.PlayerState.ContentId == 0
            || isDutyOwned() || isInnEntryRunning() || condition[ConditionFlag.InCombat]
            || condition[ConditionFlag.WatchingCutscene] || condition[ConditionFlag.OccupiedInCutSceneEvent]
            || TryGetMountedOrRidingOrMountingBlocker(out _)
            || (!allowOwnedTravel && (condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51])))
        { StatusMessage = "Equipment cleanup stopped because character or automation safety changed."; return false; }
        if (requireClosedUi && (HasUnownedNpcSaleUi() || shopRuntime.IsInventoryContextVisible))
        { StatusMessage = "Close existing vendor or inventory confirmation UI before equipment cleanup."; return false; }
        try
        {
            if (shopRuntime.HasAutoRetainer && Plugin.PluginInterface.GetIpcSubscriber<bool>("AutoRetainer.PluginState.IsBusy").InvokeFunc())
            { StatusMessage = "Equipment cleanup stopped because AutoRetainer owns inventory work."; return false; }
            if (!allowOwnedTravel && shopRuntime.HasLifestream && Plugin.PluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy").InvokeFunc())
            { StatusMessage = "Equipment cleanup stopped because Lifestream is busy."; return false; }
        }
        catch (Exception ex)
        { StatusMessage = $"Equipment cleanup safety could not be verified: {ex.Message}"; return false; }
        return true;
    }

    private bool TryRevalidateGearCleanup(GearCleanupSelection selection, bool selling, IEnumerable<GearCleanupCandidate> expected,
        out bool invalidItem)
    {
        invalidItem = false;
        if (selection.CharacterId != Plugin.PlayerState.ContentId
            || !shopRuntime.TryCaptureGearCleanup(selection.Scope, out var items, out var protection))
        { StatusMessage = "Equipment cleanup requires readable inventory and gearset protection."; return false; }
        foreach (var item in expected)
        {
            var current = items.FirstOrDefault(row => row.Container == item.Container && row.Slot == item.Slot);
            if (current == null || !GearCleanupPolicy.Matches(current.Item, item.Item)
                || !GearCleanupPolicy.IsEligible(current, selection.Scope, selling, protection, selection.AllUnprotected))
            { invalidItem = true; StatusMessage = $"Equipment cleanup stopped because the confirmed item changed: {item.Name}."; return false; }
        }
        return true;
    }

    private void UpdateGearCleanup()
    {
        var selection = gearCleanupSelection;
        if (selection == null || !CanRunGearCleanup(requireClosedUi: !gearCleanupSelling)
            || selection.CharacterId != Plugin.PlayerState.ContentId)
        { Fail("Equipment cleanup stopped because character or automation safety changed."); return; }
        if (gearCleanupIndex >= selection.Items.Count)
        {
            Complete(gearCleanupSelling && gearSaleStatus.SkippedCount > 0
                ? $"Equipment selling finished: {gearSaleStatus.SoldCount} sold; {gearSaleStatus.SkippedCount} skipped."
                : $"Equipment cleanup finished: {gearCleanupIndex} items processed.");
            return;
        }
        var item = selection.Items[gearCleanupIndex];
        if (gearCleanupPending)
        {
            if (!shopRuntime.TryReadGearCleanupSlot(item.Container, item.Slot, out var source))
            { Fail("Equipment cleanup could not verify the submitted inventory slot."); return; }
            var saleState = gearCleanupSelling
                ? shopRuntime.GetGearSaleState(gearCleanupVendor!.NpcId, gearCleanupVendor.ShopId, item) : GearSaleState.Ready;
            if (saleState == GearSaleState.Unsupported)
            { Fail($"Equipment selling stopped: {shopRuntime.GearSaleDiagnostic}"); return; }
            if (saleState == GearSaleState.NotReady)
            { StatusMessage = "Waiting for the equipment shop or selected item menu to be ready."; return; }
            if (saleState == GearSaleState.Confirmation)
            {
                if (!TryRevalidateGearCleanup(selection, true, [item], out _)) { Fail(StatusMessage); return; }
                if (DateTime.UtcNow - lastActionUtc >= UiRetryCooldown
                    && shopRuntime.TryAcceptGearSaleConfirmation(gearCleanupVendor!.NpcId, gearCleanupVendor.ShopId, item))
                    lastActionUtc = DateTime.UtcNow;
                StatusMessage = "Waiting for the exact equipment sale confirmation to settle.";
                return;
            }
            if (saleState == GearSaleState.Ready && gearCleanupSettlementReadyUtc == default)
                gearCleanupSettlementReadyUtc = DateTime.UtcNow;
            var sourceEmpty = source.ItemId == 0 && !source.IsSymbolic;
            if (sourceEmpty && saleState == GearSaleState.Ready)
            {
                if (gearCleanupSelling)
                {
                    var gil = shopRuntime.GearSaleGil;
                    var price = shopRuntime.GearSaleUnitPrice;
                    if (price <= 0 || gil < 0 || gearCleanupGilBefore < 0 || gil - gearCleanupGilBefore != price)
                    {
                        if (DateTime.UtcNow - gearCleanupSettlementReadyUtc <= UiSettleCooldown)
                        { StatusMessage = "Waiting for the exact equipment sale price and gil result."; return; }
                        SkipGearSaleItem(item, "Equipment sale removed its item, but the exact gil result could not be verified.");
                        return;
                    }
                    gearSaleStatus = gearSaleStatus with { SoldCount = gearSaleStatus.SoldCount + 1,
                        GilEarned = checked(gearSaleStatus.GilEarned + price) };
                    gearCleanupGilBefore = -1;
                }
                if (!gearCleanupSelling)
                {
                    var readable = shopRuntime.TryReadGearCleanupSlot(gearCleanupDestination, gearCleanupDestinationSlot, out var moved);
                    if (!readable || !GearCleanupPolicy.Matches(moved, item.Item, allowMovedSlot: true))
                    {
                        if (DateTime.UtcNow - gearCleanupSettlementReadyUtc <= UiSettleCooldown)
                        { StatusMessage = "Waiting for the exact equipment operation to finish."; return; }
                        log.Warning("[ADS][GearMove] Destination did not settle; from={SourceInventory}/{SourceSlot} to={DestinationInventory}/{DestinationSlot} readable={Readable}; expected item={ExpectedItem} quantity={ExpectedQuantity} flags={ExpectedFlags}; actual item={ActualItem} quantity={ActualQuantity} flags={ActualFlags}",
                            item.Container, item.Slot, gearCleanupDestination, gearCleanupDestinationSlot, readable,
                            item.Item.ItemId, item.Item.Quantity, item.Item.Flags, moved.ItemId, moved.Quantity, moved.Flags);
                        Fail("Equipment move could not verify its exact destination item.");
                        return;
                    }
                }
                gearCleanupIndex++;
                gearCleanupPending = false;
                gearCleanupSettlementReadyUtc = default;
                StatusMessage = $"Equipment cleanup progress: {gearCleanupIndex}/{selection.Items.Count}.";
                return;
            }
            if ((sourceEmpty ? saleState != GearSaleState.Pending : !GearCleanupPolicy.Matches(source, item.Item))
                || (DateTime.UtcNow - gearCleanupSettlementReadyUtc > UiSettleCooldown
                    && saleState != GearSaleState.Pending))
            {
                if (gearCleanupSelling && saleState == GearSaleState.Ready)
                    SkipGearSaleItem(item, "Equipment cleanup did not observe the exact submitted operation finish.");
                else Fail("Equipment cleanup did not observe the exact submitted operation finish.");
                return;
            }
            StatusMessage = "Waiting for the exact equipment operation to finish.";
            return;
        }
        if (!TryRevalidateGearCleanup(selection, gearCleanupSelling, [item], out var invalidItem))
        {
            if (gearCleanupSelling && invalidItem) SkipGearSaleItem(item, StatusMessage);
            else Fail(StatusMessage);
            return;
        }
        if (gearCleanupSelling)
        {
            if (gearCleanupGilBefore < 0) gearCleanupGilBefore = shopRuntime.GearSaleGil;
            if (gearCleanupGilBefore < 0)
            { Fail("Equipment selling requires readable gil before submission."); return; }
            var submission = shopRuntime.SubmitGearCleanupItem(gearCleanupVendor!.NpcId, gearCleanupVendor.ShopId, item);
            if (submission == GearSaleState.NotReady)
            { StatusMessage = "Waiting for the equipment shop or selected item menu to be ready."; return; }
            if (submission == GearSaleState.Unavailable)
            { SkipGearSaleItem(item, shopRuntime.GearSaleDiagnostic); return; }
            if (submission != GearSaleState.Pending)
            { Fail($"Equipment selling stopped: {shopRuntime.GearSaleDiagnostic}"); return; }
            gearSaleStatus = gearSaleStatus with { CommandSent = true };
        }
        else if (!shopRuntime.TryMoveGearCleanupItem(item, out gearCleanupDestination, out gearCleanupDestinationSlot, out var full))
        {
            if (full) Complete($"Inventory is full: moved {gearCleanupIndex} of {selection.Items.Count} items; remaining items were kept.");
            else Fail("Equipment move could not submit its exact inventory slot.");
            return;
        }
        gearCleanupPending = true;
        gearCleanupSettlementReadyUtc = default;
        lastActionUtc = DateTime.UtcNow;
        StatusMessage = "Waiting for the exact equipment operation to finish.";
    }

    private void SkipGearSaleItem(GearCleanupCandidate item, string reason)
    {
        if (!shopRuntime.TrySkipGearSaleItem(gearCleanupVendor!.NpcId, gearCleanupVendor.ShopId, item))
        { Fail($"Equipment selling stopped: {shopRuntime.GearSaleDiagnostic}"); return; }
        // Remove only this attempt from the frozen batch, never retry its callback
        // or widen the selection to replacement/newly eligible inventory items.
        gearCleanupIndex++;
        gearCleanupPending = false;
        gearCleanupGilBefore = -1;
        gearCleanupSettlementReadyUtc = default;
        gearSaleStatus = gearSaleStatus with { SkippedCount = gearSaleStatus.SkippedCount + 1 };
        StatusMessage = $"Equipment selling skipped {item.Name}: {reason}";
        log.Warning("[ADS][GearSale] Skipped item={Item} inventory={Inventory} slot={Slot}; reason={Reason}",
            item.Item.ItemId, item.Container, item.Slot, reason);
    }

    private void UpdateGearCleanupVendor()
    {
        if (!CanRunGearCleanup(requireClosedUi: !gearCleanupInteracted))
        { Fail(StatusMessage); return; }
        if (gearCleanupVendor == null)
        {
            gearCleanupVendor = ResolveGearCleanupVendor(targetNpcBaseId);
            if (gearCleanupVendor == null)
            { Fail("Equipment selling stopped at an unsupported shop or confirmation state."); return; }
        }
        if (!gearCleanupInteracted)
        {
            if (!shopRuntime.TryInteractNpc(gearCleanupVendor.NpcId))
            { Fail("Equipment selling could not open the selected vendor."); return; }
            gearCleanupInteracted = true;
            lastInteractUtc = DateTime.UtcNow;
            StatusMessage = "Opening the selected equipment vendor.";
            return;
        }
        if (shopRuntime.IsTalkVisible)
        {
            if (gearCleanupPending)
            { Fail("A dialog interrupted the submitted equipment sale."); return; }
            if (DateTime.UtcNow - lastInteractUtc >= UiRetryCooldown && shopRuntime.TryAdvanceGearSaleTalk(gearCleanupVendor.NpcId))
                lastInteractUtc = DateTime.UtcNow;
            StatusMessage = "Waiting for the selected equipment vendor's dialog to finish.";
            return;
        }
        if (shopRuntime.HasUnexpectedConfirmation && !gearCleanupPending)
        { Fail("Equipment selling stopped because an unowned confirmation appeared before submission."); return; }
        if (shopRuntime.IsExpectedShopVisible(ShopOfferKind.GilShop))
        { UpdateGearCleanup(); return; }
        if (shopRuntime.IsAnyShopVisible)
        { Fail("Equipment selling stopped at an unsupported shop or confirmation state."); return; }
        if (shopRuntime.IsSelectionMenuVisible)
        {
            if (DateTime.UtcNow - lastMenuSelectionUtc < MenuRetryCooldown) return;
            if (!shopRuntime.TrySelectGearSaleMenu(gearCleanupVendor.NpcId, gearCleanupVendor.CallbackPath, gearCleanupLastMenuHandler, out var handler)
                || handler == gearCleanupLastMenuHandler)
            { Fail("Equipment selling stopped at an unsupported shop or confirmation state."); return; }
            gearCleanupLastMenuHandler = handler;
            lastMenuSelectionUtc = DateTime.UtcNow;
        }
        StatusMessage = "Opening the selected equipment vendor.";
    }

    private ShopNpcSheetLink? ResolveGearCleanupVendor(uint npcId)
    {
        if (GetENpcBaseSheet() is not { } sheet || !sheet.TryGetRow(npcId, out var npc)) return null;
        var events = npc.ENpcData.Select(LuminaShopSheetSource.ToEventReference).ToArray();
        var gilIds = new HashSet<uint>();
        var topics = new List<ShopTopicSelectSheetRow>();
        var preHandlers = new List<ShopPreHandlerSheetRow>();
        var visited = new HashSet<(ShopNpcEventKind, uint)>();
        foreach (var reference in events) Visit(reference);
        return ShopNpcLinkBuilder.Build(gilIds, new HashSet<uint>(), [new(npcId, targetNpcName, events)], topics, preHandlers)
            .FirstOrDefault(link => !link.HasUnknownGate && link.RequiredQuestIds.All(shopRuntime.IsQuestComplete));

        void Visit(ShopNpcEventReference reference)
        {
            if (!visited.Add((reference.Kind, reference.RowId))) return;
            if (reference.Kind == ShopNpcEventKind.GilShop) gilIds.Add(reference.RowId);
            else if (reference.Kind == ShopNpcEventKind.PreHandler
                && dataManager.GetExcelSheet<PreHandler>() is { } preSheet && preSheet.TryGetRow(reference.RowId, out var pre))
            {
                var target = LuminaShopSheetSource.ToEventReference(pre.Target);
                preHandlers.Add(new(pre.RowId, target, pre.UnlockQuest.RowId, false));
                Visit(target);
            }
            else if (reference.Kind == ShopNpcEventKind.TopicSelect
                && dataManager.GetExcelSheet<TopicSelect>() is { } topicSheet && topicSheet.TryGetRow(reference.RowId, out var topic))
            {
                var shops = topic.Shop.Select(LuminaShopSheetSource.ToEventReference).ToArray();
                topics.Add(new(topic.RowId, shops));
                foreach (var target in shops) Visit(target);
            }
        }
    }

    private bool StartNpcRepair(NpcRepairMode mode, uint destinationAethernet = 0, uint requiredRoom = 0,
        string? saleOperationId = null, GearCleanupSelection? gearSale = null)
    {
        if (IsRepairBlockedByMountedState(UtilityTask.NpcRepair))
            return false;

        var statusMessage = saleOperationId != null || gearSale != null ? "Looking for a nearby NPC vendor for selling." : mode switch
        {
            NpcRepairMode.NoInn => "Starting NPC repair without inn fallback.",
            NpcRepairMode.NoTeleportNoInn => "Starting NPC repair without inn fallback or teleport.",
            NpcRepairMode.YesInn => "Starting NPC repair and returning to an inn room.",
            _ => "Starting NPC repair.",
        };
        if (!TryStartTask(UtilityTask.NpcRepair, statusMessage))
            return false;

        activeNpcRepairMode = mode;
        if (saleOperationId != null || gearSale != null)
        {
            npcSaleRequest = true;
            npcSaleCharacter = gearSale?.CharacterId ?? Plugin.PlayerState.ContentId;
            if (gearSale != null)
            {
                gearCleanupSelection = gearSale;
                gearCleanupSelling = true;
            }
            else npcSaleStatus = new(saleOperationId, true, false, null, "Approaching an NPC vendor through ADS.");
        }
        failedNpcRepairFieldAetheryteIds.Clear();
        if (mode == NpcRepairMode.YesInn)
        {
            npcRepairDestinationAethernet = destinationAethernet;
            npcRepairRequiredInnTerritory = requiredRoom;
            log.Information("[ADS][InnRepair] dispatch; destination={Destination}; requiredRoom={Room}; startingTerritory={Territory}",
                destinationAethernet, requiredRoom, clientState.TerritoryType);
            if (GameInteractionHelper.IsInnTerritory(dataManager, (ushort)clientState.TerritoryType))
            {
                npcRepairStartingInnTerritory = clientState.TerritoryType;
                if (npcRepairRequiredInnTerritory == 0)
                    npcRepairRequiredInnTerritory = npcRepairStartingInnTerritory;
                if (npcRepairRequiredInnTerritory == npcRepairStartingInnTerritory)
                {
                    SetNpcRepairTravelStage(NpcRepairTravelStage.ExitingInn, "Leaving the inn room through its door for NPC repair.");
                    return true;
                }
            }

            if (TryBeginNpcRepairInnTravel(out var innFailure))
                return true;

            Fail(innFailure);
            return false;
        }

        var repairNpcSearchRadius = mode == NpcRepairMode.NoTeleportNoInn
            ? NoTeleportNoInnRepairNpcSearchRadius
            : RepairNpcSearchRadius;
        if (TryFindNearbyRepairNpc(out var targetNpc, repairNpcSearchRadius))
        {
            BeginNpcRepairWithCandidate(targetNpc, "Starting NPC repair with");
            return IsRunning;
        }

        if (mode == NpcRepairMode.NoTeleportNoInn)
        {
            Fail(npcSaleRequest
                ? $"No NPC sale vendor found within {NoTeleportNoInnRepairNpcSearchRadius:0}y; local-only selling does not travel."
                : $"No repair NPC found within {NoTeleportNoInnRepairNpcSearchRadius:0}y for NPC no-inn/no-teleport repair.");
            return false;
        }

        if (mode == NpcRepairMode.NoInn && !CanStartNpcRepairNoInnHere())
        {
            Fail(npcSaleRequest
                ? "No nearby sale vendor was found; field travel requires a sanctuary or nearby Aetheryte/Aethernet."
                : "NPC repair without inn fallback can only start from a sanctuary or nearby Aetheryte/Aethernet. Move to a sanctuary, stand near an Aetheryte/Aethernet, or use /ads npcrepair for inn fallback.");
            return false;
        }

        var startedTravel = mode == NpcRepairMode.NoInn
            ? TryBeginNpcRepairFieldTravel(out var failureMessage)
            : TryBeginNpcRepairInnTravel(out failureMessage);

        if (!startedTravel)
        {
            Fail(failureMessage);
            return false;
        }

        return true;
    }

    private bool CanStartNpcRepairNoInnHere()
        => IsInSanctuary() || IsNearAetheryteOrAethernet();

    private bool IsRepairBlockedByMountedState(UtilityTask task)
    {
        if (!TryGetMountedOrRidingOrMountingBlocker(out var blocker))
            return false;

        StatusMessage = $"Cannot start {GetTaskLabel(task)} while {blocker}.";
        log.Warning($"[ADS][Utility] {StatusMessage}");
        return true;
    }

    private bool TryGetMountedOrRidingOrMountingBlocker(out string blocker)
    {
        if (condition[ConditionFlag.Mounting71])
        {
            blocker = "mounting";
            return true;
        }

        if (condition[ConditionFlag.RidingPillion])
        {
            blocker = "riding pillion";
            return true;
        }

        if (condition[ConditionFlag.Mounted])
        {
            blocker = "mounted";
            return true;
        }

        blocker = string.Empty;
        return false;
    }

    private static bool IsInSanctuary()
    {
        try
        {
            var actionManager = ActionManager.Instance();
            if (actionManager == null)
                return false;

            return actionManager->GetActionStatus(ActionType.GeneralAction, 9) != 0;
        }
        catch
        {
            return false;
        }
    }

    private bool IsNearAetheryteOrAethernet()
    {
        try
        {
            var player = objectTable.LocalPlayer;
            if (player == null)
                return false;

            var playerPosition = player.Position;
            foreach (var obj in objectTable)
            {
                if (obj == null || !IsAetheryteOrAethernet(obj))
                    continue;

                if (Vector3.DistanceSquared(playerPosition, obj.Position) <= NoInnRepairAetheryteRadiusSquared)
                    return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static bool IsAetheryteOrAethernet(IGameObject obj)
    {
        if (obj.ObjectKind == ObjectKind.Aetheryte)
            return true;

        var name = obj.Name.TextValue;
        return !string.IsNullOrEmpty(name)
            && (name.Contains("Aetheryte", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Aethernet", StringComparison.OrdinalIgnoreCase));
    }

    public bool StartExtractMateria()
    {
        if (!TryStartTask(UtilityTask.ExtractMateria, "Starting materia extraction."))
            return false;

        extractMateriaDone = false;
        extractMateriaSucceeded = null;
        extractMateriaStatusMessage = StatusMessage;
        extractMateriaSuccessMessage = string.Empty;
        extractMateriaFailureMessage = string.Empty;
        extractMateriaCompletedUtc = DateTime.MinValue;
        log.Information("[ADS][Utility] Starting materia extraction flow.");
        return true;
    }

    public bool StartDesynthFromInventory()
        => StartDesynth(DesynthRunMode.InventoryOnly);

    public bool StartDesynth(DesynthRunMode mode)
    {
        if (!TryStartTask(UtilityTask.DesynthFromInventory, "Starting inventory desynthesis."))
            return false;

        activeDesynthPolicy = desynthPolicyService.Compose(mode, configuration, desynthPresetStore, desynthDutyLedgerStore);
        lastDesynthModeName = GetDesynthModeName(mode);
        lastDesynthSourceName = activeDesynthPolicy.Source.ToString();
        lastDesynthScopeName = DesynthPolicyService.GetScopeName(activeDesynthPolicy.Scope);
        lastDesynthPresetName = activeDesynthPolicy.PresetName;
        DesynthEligibleCount = 0;
        DesynthCompletedCount = 0;
        LastDesynthSuccessMessage = string.Empty;
        LastDesynthFailureMessage = string.Empty;
        maximumDesynthLevel = GetMaximumDesynthLevel();
        desynthGearsetItemIds = activeDesynthPolicy.ProtectGearsets ? GetGearsetItemIds() : null;
        StatusMessage = $"Starting {mode.ToString().ToLowerInvariant()} desynthesis.";
        log.Information($"[ADS][Utility] Starting {mode} desynthesis flow: source={activeDesynthPolicy.Source}, scope={activeDesynthPolicy.Scope}, preset={activeDesynthPolicy.PresetName}, categories={string.Join(",", activeDesynthPolicy.Categories)}.");
        return true;
    }

    public bool StartShopPurchase(ShopPurchaseRequest request)
        => StartShopPurchaseCore(request, null, null, null);

    internal bool StartShopPurchase(ShopPurchaseRequest request, ShopCurrencyIdentity currency)
        => StartShopPurchaseCore(request, currency, null, null);

    internal bool StartShopPurchase(ShopPurchaseRequest request, ShopCurrencyIdentity currency,
        Action<ShopPurchaseCheckpoint> beforeSubmit, Action<ShopPurchaseCheckpoint> verified,
        Func<ShopPurchaseCheckpoint, bool>? confirmationGuard = null)
        => StartShopPurchaseCore(request, currency, beforeSubmit, verified, confirmationGuard);

    private bool StartShopPurchaseCore(ShopPurchaseRequest request, ShopCurrencyIdentity? currency,
        Action<ShopPurchaseCheckpoint>? beforeSubmit, Action<ShopPurchaseCheckpoint>? verified,
        Func<ShopPurchaseCheckpoint, bool>? confirmationGuard = null)
    {
        if (IsRunning)
        {
            var message = $"Cannot start shop purchasing while {GetTaskLabel(activeTask)} is active.";
            shopPurchaseRunner.RejectStart(message);
            StatusMessage = message;
            return false;
        }

        var accepted = currency.HasValue
            ? beforeSubmit != null
                ? shopPurchaseRunner.Start(request, false, currency.Value, beforeSubmit, verified!, confirmationGuard)
                : shopPurchaseRunner.Start(request, false, currency.Value)
            : shopPurchaseRunner.Start(request);
        if (!accepted)
        {
            StatusMessage = shopPurchaseRunner.Status.LastStartError;
            return false;
        }

        ResetState();
        activeTask = UtilityTask.ShopPurchase;
        startedAtUtc = DateTime.UtcNow;
        LastSuccessMessage = string.Empty;
        LastFailureMessage = string.Empty;
        LastCompletionUtc = DateTime.MinValue;
        StatusMessage = shopPurchaseRunner.Status.StatusMessage;
        log.Information(
            "[ADS][Shop] Accepted purchase request item={ItemId}, quantity={Quantity}.",
            request.ItemId,
            request.Quantity);
        return true;
    }

    internal bool StartShopListBatch(ShopListBatchDefinition definition)
    {
        var items = definition.Items;
        if (items.Count == 0)
        {
            StatusMessage = "The active shop-list preset has no rows.";
            return false;
        }
        if (IsRunning)
        {
            StatusMessage = $"Cannot start a shop-list batch while {GetTaskLabel(activeTask)} is active.";
            return false;
        }
        if (isDutyOwned())
        {
            StatusMessage = "Cannot start shop-list purchasing while ADS owns active duty execution.";
            return false;
        }
        if (isInnEntryRunning())
        {
            StatusMessage = "Cannot start shop-list purchasing while /ads enterinn is running.";
            return false;
        }
        if (!shopRuntime.IsLoggedIn || !shopRuntime.IsPlayerAvailable || shopRuntime.IsBetweenAreas)
        {
            StatusMessage = "Shop Lists requires a logged-in, available character who is not zoning.";
            return false;
        }

        foreach (var item in items)
        {
            if (!ShopPurchaseRequest.TryCreate(item.ItemId, item.RefillToAtLeast, out _, out var error))
            {
                StatusMessage = $"Shop-list batch row {item.ItemId} is invalid: {error}";
                return false;
            }
            if (item.RowId == Guid.Empty)
            {
                StatusMessage = $"Shop-list batch row {item.ItemId} has no stable row ID.";
                return false;
            }
            if (item.TriggerBelow < 1 || item.RefillToAtLeast < item.TriggerBelow)
            {
                StatusMessage = $"Shop-list batch row {item.ItemId} has an invalid trigger/refill rule.";
                return false;
            }
            if (item.RetainerQuantity < 0)
            {
                StatusMessage = $"Shop-list batch row {item.ItemId} has an invalid retainer quantity.";
                return false;
            }
        }

        var rowIds = items.Select(item => item.RowId).ToHashSet();
        var nonRepeatableRowIds = items.Where(item => !item.Repeatable).Select(item => item.RowId).ToHashSet();
        if (rowIds.Count != items.Count
            || definition.AssociationCompletedRowIds.Distinct().Count() != definition.AssociationCompletedRowIds.Count
            || definition.AssociationCompletedRowIds.Any(rowId => !rowIds.Contains(rowId))
            || definition.InitiallyCompletedNonRepeatableRowIds.Any(rowId => !nonRepeatableRowIds.Contains(rowId)))
        {
            StatusMessage = "Shop-list batch row IDs or association-completed row IDs are invalid for this preset.";
            return false;
        }
        var availableCurrency = GetAvailableShopCurrency(definition.Currency);
        if (availableCurrency < 0)
        {
            StatusMessage = "The selected currency balance is unavailable; ADS will not start the preset.";
            return false;
        }
        if (availableCurrency < definition.CurrencyThreshold)
        {
            StatusMessage = $"The selected currency balance {availableCurrency} has not reached trigger {definition.CurrencyThreshold}.";
            return false;
        }

        shopPurchaseRunner.ReleaseHeldShopUi();
        ResetState();
        activeTask = UtilityTask.ShopListBatch;
        startedAtUtc = DateTime.UtcNow;
        shopListBatchDefinition = definition;
        shopListBatchItems = items.ToArray();
        shopListBatchRows = [];
        shopListBatchCompletedNonRepeatableRowIds.Clear();
        foreach (var rowId in definition.InitiallyCompletedNonRepeatableRowIds)
            shopListBatchCompletedNonRepeatableRowIds.Add(rowId);
        shopListBatchSkippedRowIds.Clear();
        shopListBatchIndex = 0;
        shopListBatchCompletedRows = 0;
        shopListBatchVerifiedPurchaseRows = 0;
        shopListSpendCyclePurchased = false;
        shopListBatchChildAccepted = false;
        shopListBatchCancelRequested = false;
        shopListBatchTerminalFailureMessage = string.Empty;
        shopListHeldShop = null;
        LastSuccessMessage = string.Empty;
        LastFailureMessage = string.Empty;
        LastCompletionUtc = DateTime.MinValue;
        var associationCompleted = definition.AssociationCompletedRowIds.ToHashSet();
        foreach (var item in shopListBatchItems)
        {
            var inventory = Math.Max(0L, shopRuntime.GetItemCount(item.ItemId));
            var owned = inventory + (item.OwnershipScope == ShopListOwnershipScope.InventoryAndRetainers
                ? item.RetainerQuantity
                : 0);
            var skipped = definition.Mode == ShopListMode.FillOrderOverMultipleRuns
                ? definition.CreditedQuantities?.GetValueOrDefault(item.RowId) >= item.RefillToAtLeast
                : !item.Repeatable && associationCompleted.Contains(item.RowId);
            if (skipped)
            {
                shopListBatchSkippedRowIds.Add(item.RowId);
                shopListBatchCompletedRows++;
                if (!item.Repeatable)
                    shopListBatchCompletedNonRepeatableRowIds.Add(item.RowId);
            }
            shopListBatchRows.Add(new ShopListBatchRowStatus(
                item.RowId,
                item.ItemId,
                item.ItemName,
                item.Repeatable,
                OwnershipScopeName(item.OwnershipScope),
                item.TriggerBelow,
                item.RefillToAtLeast,
                owned,
                0,
                0,
                skipped ? "association-completed" : "pending",
                skipped ? "Skipped because this exact association already completed the non-repeatable row." : "Pending evaluation."));
        }
        StatusMessage = $"Starting shop-list batch with {items.Count} row(s).";
        shopListBatchStatus = new ShopListBatchStatusSnapshot(
            1,
            definition.OperationId,
            definition.PresetId,
            true,
            false,
            null,
            "running",
            shopListBatchCompletedRows,
            items.Count,
            0,
            string.Empty,
            CurrentCompletedNonRepeatableRowIds(),
            CurrentSkippedRowIds(),
            null,
            StatusMessage,
            string.Empty,
            shopListBatchRows.ToArray(),
            null) { CreditedQuantities = definition.CreditedQuantities };
        log.Information(
            "[ADS][ShopLists] Accepted preset={PresetId} operation={OperationId} with {Count} row(s).",
            definition.PresetId,
            definition.OperationId,
            items.Count);
        return true;
    }

    internal bool RetainShopListPresetNoOp(
        string operationId,
        Guid presetId,
        string disposition,
        string message,
        IReadOnlyList<ShopListPreviewRow> previewRows,
        IReadOnlyList<Guid> completedNonRepeatableRowIds,
        IReadOnlyDictionary<Guid, long>? creditedQuantities = null)
    {
        if (IsRunning)
            return false;

        var rows = previewRows.Select(row => new ShopListBatchRowStatus(
            row.RowId,
            row.ItemId,
            row.ItemName,
            row.Repeatable,
            row.OwnershipScope,
            row.TriggerBelow,
            row.RefillToAtLeast,
            row.OwnedQuantity,
            row.PurchaseQuantity,
            0,
            row.Outcome,
            row.StatusMessage)).ToArray();
        var skipped = rows.Where(row => row.Outcome == "association-completed")
            .Select(row => row.RowId)
            .ToArray();
        var completedAt = DateTime.UtcNow;
        shopListBatchStatus = new ShopListBatchStatusSnapshot(
            1,
            operationId,
            presetId,
            false,
            true,
            true,
            disposition,
            rows.Count(row => row.Outcome is "association-completed" or "already-satisfied"),
            rows.Length,
            0,
            string.Empty,
            completedNonRepeatableRowIds,
            skipped,
            null,
            message,
            string.Empty,
            rows,
            completedAt) { CreditedQuantities = creditedQuantities };
        LastSuccessMessage = message;
        LastFailureMessage = string.Empty;
        LastCompletionUtc = completedAt;
        StatusMessage = $"Shop-list preset {disposition}: {message}";
        log.Information(
            "[ADS][ShopLists] Retained no-op preset={PresetId} operation={OperationId} disposition={Disposition}.",
            presetId,
            operationId,
            disposition);
        return true;
    }

    public bool RejectShopPurchaseStart(string message)
    {
        var rejected = shopPurchaseRunner.RejectStart(message);
        if (!IsRunning)
            StatusMessage = shopPurchaseRunner.Status.LastStartError;
        return rejected;
    }

    public void Update()
    {
        shopRuntime.UpdateOwnedShopCleanup();
        if (!IsRunning)
            return;

        var updateStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var measuredTask = activeTask;
        try
        {
            var now = DateTime.UtcNow;
            if (activeTask is not (UtilityTask.ShopPurchase or UtilityTask.ShopListBatch)
                && now - startedAtUtc > OverallTimeout
                && !IsNpcRepairFieldRouteAttemptActive())
            {
                if (gearCleanupSelling)
                { Fail($"Equipment selling timed out: {(string.IsNullOrWhiteSpace(shopRuntime.GearSaleDiagnostic) ? StatusMessage : shopRuntime.GearSaleDiagnostic)}"); return; }
                Fail(npcSaleRequest && npcSaleCommandSent
                    ? npcSaleBusyObserved
                        ? "Timed out waiting for AutoRetainer work after the NPC sale command."
                        : "AutoRetainer did not start observable work after the NPC sale command."
                    : $"Timed out while running {ActiveTaskName}.");
                return;
            }

            switch (activeTask)
            {
                case UtilityTask.SelfRepair:
                    UpdateSelfRepair();
                    break;
                case UtilityTask.NpcRepair:
                    UpdateNpcRepair();
                    break;
                case UtilityTask.ExtractMateria:
                    UpdateExtractMateria();
                    break;
                case UtilityTask.DesynthFromInventory:
                    UpdateDesynthFromInventory();
                    break;
                case UtilityTask.ShopPurchase:
                    shopPurchaseRunner.Update();
                    SyncShopPurchaseRunner();
                    break;
                case UtilityTask.ShopListBatch:
                    UpdateShopListBatch();
                    break;
                case UtilityTask.GearMove:
                    UpdateGearCleanup();
                    break;
                case UtilityTask.AutoEquip:
                    UpdateAutoEquip();
                    break;
            }
        }
        catch (Exception ex)
        {
            if (activeTask == UtilityTask.ShopPurchase)
            {
                shopPurchaseRunner.Cancel($"runner exception: {ex.Message}");
                SyncShopPurchaseRunner();
            }
            else if (activeTask == UtilityTask.ShopListBatch)
            {
                shopListBatchTerminalFailureMessage = $"Shop-list batch failed safely: {ex.Message}";
                shopPurchaseRunner.Cancel(shopListBatchTerminalFailureMessage);
                UpdateShopListBatch();
            }
            else
            {
                Fail($"{ActiveTaskName} failed: {ex.Message}");
            }
        }
        finally
        {
            updateStopwatch.Stop();
            ReportSlowUtilityUpdate(measuredTask, updateStopwatch.Elapsed.TotalMilliseconds);
        }
    }

    private void ReportSlowUtilityUpdate(UtilityTask task, double elapsedMs)
    {
        if (elapsedMs < 25d)
            return;

        var now = DateTime.UtcNow;
        if (now < nextSlowUtilityLogUtc)
            return;

        nextSlowUtilityLogUtc = now.AddSeconds(5);
        log.Warning(
            "[ADS][HITCH] utility update slow task={Task}; elapsedMs={ElapsedMs:0.0}; stage={Stage}; repairMode={RepairMode}; targetNpc={TargetNpc}; route={Route}.",
            task,
            elapsedMs,
            npcRepairTravelStage,
            activeNpcRepairMode,
            string.IsNullOrWhiteSpace(targetNpcName) ? "(none)" : targetNpcName,
            activeNpcRepairFieldRoute.HasValue
                ? FormatFieldRepairRoute(activeNpcRepairFieldRoute.Value)
                : activeNpcRepairInnRoute.HasValue
                    ? activeNpcRepairInnRoute.Value.TerritoryName
                    : "(none)");
    }

    private bool IsNpcRepairFieldRouteAttemptActive()
        => activeTask == UtilityTask.NpcRepair
            && activeNpcRepairMode == NpcRepairMode.NoInn
            && activeNpcRepairFieldRoute.HasValue
            && npcRepairTravelStage is NpcRepairTravelStage.TeleportingToFieldAetheryte
                or NpcRepairTravelStage.AwaitingRepairNpc;

    public void Cancel(string reason)
    {
        if (!IsRunning)
            return;

        if (activeTask == UtilityTask.ShopPurchase)
        {
            shopPurchaseRunner.Cancel(reason);
            SyncShopPurchaseRunner();
            return;
        }

        if (activeTask == UtilityTask.ShopListBatch)
        {
            shopListBatchCancelRequested = true;
            StatusMessage = $"Cancelling shop-list batch: {reason}";
            shopPurchaseRunner.Cancel(reason);
            UpdateShopListBatch();
            return;
        }

        var cancelledTask = activeTask;
        var message = $"Cancelled {ActiveTaskName}: {reason}";
        FinishNpcSale(false, message);
        message = FinishGearSale(false, message);
        LastFailureMessage = message;
        if (cancelledTask == UtilityTask.ExtractMateria)
            RecordExtractMateriaCompletion(false, message);
        if (cancelledTask == UtilityTask.DesynthFromInventory)
            LastDesynthFailureMessage = message;
        LastCompletionUtc = DateTime.UtcNow;
        StopMovementIfNpcRepair();
        log.Warning($"[ADS][Utility] Cancelled {GetTaskLabel(activeTask)}: {reason}");
        ResetState();
        StatusMessage = message;
    }

    public bool CancelShopListPreset(string operationId)
    {
        if (activeTask != UtilityTask.ShopListBatch
            || shopListBatchDefinition == null
            || !string.Equals(shopListBatchDefinition.OperationId, operationId?.Trim(), StringComparison.Ordinal))
        {
            return false;
        }

        Cancel($"matching preset operation {shopListBatchDefinition.OperationId}");
        return true;
    }

    private bool TryStartTask(UtilityTask task, string statusMessage)
    {
        if (!clientState.IsLoggedIn || objectTable.LocalPlayer == null)
        {
            StatusMessage = $"{GetTaskLabel(task)} requires a logged-in character.";
            return false;
        }

        if (condition[ConditionFlag.BetweenAreas])
        {
            StatusMessage = $"Cannot start {GetTaskLabel(task)} while zoning.";
            return false;
        }

        if (IsRunning)
        {
            StatusMessage = $"Cannot start {GetTaskLabel(task)} while {GetTaskLabel(activeTask)} is active.";
            return false;
        }

        ResetState();
        activeTask = task;
        startedAtUtc = DateTime.UtcNow;
        LastSuccessMessage = string.Empty;
        LastFailureMessage = string.Empty;
        LastCompletionUtc = DateTime.MinValue;
        StatusMessage = statusMessage;
        return true;
    }

    private void UpdateSelfRepair()
    {
        if (TryCompleteRepairIfFinished("Self-repair finished; equipped gear is fully repaired."))
            return;

        if (!PrepareForUiWork("self-repair", allowDismount: false))
            return;

        var now = DateTime.UtcNow;
        if (TryHandleRepairConfirm(now, "Confirming self-repair."))
            return;

        var repairAddon = GetVisibleAddon<AddonRepair>("Repair");
        if (repairAddon == null)
        {
            repairWindowSeenUtc = DateTime.MinValue;
            if (now - lastActionUtc >= UiRetryCooldown
                && GameInteractionHelper.TryUseGeneralAction(SelfRepairGeneralAction, log))
            {
                lastActionUtc = now;
                StatusMessage = "Opening self-repair window.";
            }

            return;
        }

        NoteRepairWindowSeen(now);
        if (!repairSubmissionSent)
        {
            if (now - repairWindowSeenUtc < UiSettleCooldown)
            {
                StatusMessage = "Waiting for self-repair window to populate.";
                return;
            }

            if (!IsRepairAllEnabled(repairAddon))
            {
                GameInteractionHelper.TryCloseAddon("Repair", log);
                Complete("No self-repairable gear or dark matter was available.");
                return;
            }

            if (now - lastActionUtc >= UiRetryCooldown)
            {
                ClickRepairAll(repairAddon);
                repairSubmissionSent = true;
                lastActionUtc = now;
                StatusMessage = "Submitting self-repair.";
            }

            return;
        }

        if (TryCompleteRepairIfFinished("Self-repair finished; equipped gear is fully repaired."))
            return;

        if (GameInteractionHelper.IsAddonVisible("SelectYesno"))
        {
            StatusMessage = "Confirming self-repair.";
            return;
        }

        if (now - lastActionUtc < TimeSpan.FromSeconds(5))
        {
            StatusMessage = "Waiting for self-repair to settle.";
            return;
        }

        var stillEnabled = IsRepairAllEnabled(repairAddon);
        GameInteractionHelper.TryCloseAddon("Repair", log);
        Complete(stillEnabled
            ? "Self-repair settled, but some gear may remain unrepaired."
            : "Self-repair finished.");
    }

    private void UpdateNpcRepair()
    {
        if (npcSaleRequest && (!clientState.IsLoggedIn || npcSaleCharacter == 0 || npcSaleCharacter != Plugin.PlayerState.ContentId))
        { Fail("The NPC sale character changed."); return; }
        if (npcSaleRequest && (isDutyOwned() || isInnEntryRunning()))
        { Fail("Duty or inn automation took ownership before the NPC sale finished."); return; }
        if (gearCleanupSelection != null && !CanRunGearCleanup(requireClosedUi: !gearCleanupInteracted,
            allowOwnedTravel: npcRepairTravelStage != NpcRepairTravelStage.None))
        { Fail(StatusMessage); return; }
        if (gearCleanupSelection != null && gearCleanupInteracted)
        { UpdateGearCleanupVendor(); return; }
        if (npcSaleRequest && npcSaleCommandSent)
        {
            try
            {
                var busy = Plugin.PluginInterface.GetIpcSubscriber<bool>("AutoRetainer.PluginState.IsBusy").InvokeFunc();
                if (busy)
                {
                    npcSaleBusyObserved = true;
                    StatusMessage = "Waiting for AutoRetainer work after the NPC sale command to settle.";
                }
                else if (npcSaleBusyObserved)
                {
                    if (HasUnownedNpcSaleUi())
                        StatusMessage = "AutoRetainer finished; waiting for its sale UI to close.";
                    else
                        Complete("AutoRetainer work and sale UI settled after the NPC sale command.");
                }
                else StatusMessage = "Waiting for AutoRetainer to start work after the NPC sale command.";
            }
            catch (Exception ex) { Fail($"NPC sale completion is unverified: {ex.Message}"); }
            return;
        }
        if (condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51] || objectTable.LocalPlayer == null)
        {
            npcRepairInnArrivalReadyUtc = DateTime.MinValue;
            if (lastMoveCommandUtc != DateTime.MinValue)
                StopMovementIfNpcRepair();
            StatusMessage = "Waiting for zoning to finish before NPC repair.";
            return;
        }

        if (activeNpcRepairMode == NpcRepairMode.YesInn)
        {
            // Travel and inn dialogs own the flow until the repair stop is reached.
            if (npcRepairTravelStage == NpcRepairTravelStage.ExitingInn)
            {
                UpdateNpcRepairInnExit();
                return;
            }
            if (npcRepairTravelStage == NpcRepairTravelStage.ReturningToInn)
            {
                UpdateNpcRepairInnReturn();
                return;
            }
            if (npcRepairTravelStage != NpcRepairTravelStage.None)
            {
                if (PrepareForUiWork("inn repair travel", allowDismount: false))
                    UpdateNpcRepairTravel(DateTime.UtcNow);
                return;
            }
        }

        var now = DateTime.UtcNow;
        if (!npcSaleRequest && (shopRuntime.IsAnyShopVisible || shopRuntime.IsSelectionMenuVisible
                || GameInteractionHelper.IsAddonVisible("Repair") || GameInteractionHelper.IsAddonVisible("SelectYesno"))
            && !OwnsNpcRepairInteraction())
        {
            Fail("Unowned NPC UI appeared during repair; ADS did not interact with it.");
            return;
        }

        if (!npcSaleRequest && GameInteractionHelper.IsAddonVisible("Shop"))
        {
            var shopAddon = GetVisibleAddon<AtkUnitBase>("Shop");
            var proxy = ShopEventHandler.AgentProxy.Instance();
            var agent = AgentShop.Instance();
            var npc = proxy == null || proxy->Handler == null ? null : proxy->Handler->EventHandler.EventGameObject;
            if (shopAddon == null || agent == null || proxy == null || proxy->AddonId != shopAddon->Id
                || agent->EventReceiver != (AtkModuleInterface.AtkEventInterface*)proxy
                || npc == null || !OwnsNpcRepairNpc((ulong)npc->GetGameObjectId(), npc->BaseId))
            {
                Fail("The purchase menu does not belong to ADS's repair NPC; ADS left it untouched.");
                return;
            }

            if (!shopAddon->IsReady || now - lastActionUtc < UiRetryCooldown)
            {
                StatusMessage = $"Waiting for the owned purchase menu from {targetNpcName} to settle before repair.";
                return;
            }

            if (!shopPurchaseRuntime.CloseShopAddon("Shop"))
            {
                Fail("ADS could not safely cancel its purchase menu to continue NPC repair.");
                return;
            }

            lastActionUtc = now;
            StatusMessage = $"Backing out of the purchase menu from {targetNpcName}; continuing NPC repair.";
            return;
        }

        if (!npcSaleRequest && (GameInteractionHelper.IsAddonVisible("Repair")
                || GameInteractionHelper.IsAddonVisible("SelectYesno")) && !OwnsNpcRepairWindow())
        {
            Fail("The repair window does not belong to ADS's repair NPC; ADS left it untouched.");
            return;
        }

        if (TryCompleteRepairIfFinished("NPC repair finished; equipped gear is fully repaired."))
            return;

        if (!PrepareForUiWork(npcSaleRequest ? "NPC selling" : "NPC repair", allowDismount: false))
            return;

        if (npcSaleRequest && HasUnownedNpcSaleUi())
        { Fail("Unowned NPC UI appeared before the sale command; ADS did not interact with it."); return; }
        if (GameInteractionHelper.IsAddonVisible("SelectIconString")
            || GameInteractionHelper.IsAddonVisible("SelectString"))
        {
            TrySelectRepairMenuOption(now);
            return;
        }

        if (GameInteractionHelper.IsAddonVisible("Repair")
            || GameInteractionHelper.IsAddonVisible("SelectYesno"))
        {
            UpdateNpcRepairWindow(now);
            return;
        }

        if (npcRepairTravelStage != NpcRepairTravelStage.None)
        {
            UpdateNpcRepairTravel(now);
            return;
        }

        var targetNpc = FindTrackedRepairNpc();
        if (targetNpc == null)
        {
            Fail(npcSaleRequest ? $"Sale vendor {targetNpcName} is no longer nearby."
                : $"Repair NPC {targetNpcName} is no longer nearby.");
            return;
        }

        var distance = DistanceToLocalPlayer(targetNpc);
        if (distance > RepairNpcInteractRadius)
        {
            StatusMessage = npcSaleRequest ? $"Moving to sale vendor {targetNpcName}."
                : $"Moving to repair NPC {targetNpcName}.";
            SendMoveCommand(targetNpc.Position, targetNpcName, initial: false);

            return;
        }

        StopMovementIfNpcRepair();
        npcRepairMenderReached = true;
        if (npcSaleRequest)
        {
            if (!targetNpc.IsTargetable || !HasGilShop(targetNpc.BaseId))
            { Fail("The selected NPC does not expose a gil shop; no sale command sent."); return; }
            if (lastActionUtc != DateTime.MinValue)
            {
                if (Plugin.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.SimpleMove.PathfindInProgress").InvokeFunc()
                    || Plugin.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning").InvokeFunc()
                    || now - lastActionUtc < UiSettleCooldown)
                { StatusMessage = "Waiting for ADS vendor navigation to settle."; return; }
            }
            if (gearCleanupSelection != null)
            { UpdateGearCleanupVendor(); return; }
            if (!clientState.IsLoggedIn || npcSaleCharacter != Plugin.PlayerState.ContentId
                || isDutyOwned() || isInnEntryRunning() || HasUnownedNpcSaleUi())
            { Fail("ADS ownership or unrelated UI changed before the NPC sale command."); return; }
            if (Plugin.PluginInterface.GetIpcSubscriber<bool>("AutoRetainer.PluginState.IsBusy").InvokeFunc())
            { Fail("AutoRetainer became busy before ADS could dispatch its sale."); return; }
            npcSaleCommandSent = GameInteractionHelper.TrySendChatCommand(commandManager, "/ays itemsell", log);
            if (!npcSaleCommandSent)
            { Fail("The AutoRetainer NPC sale command was not accepted."); return; }
            startedAtUtc = now;
            npcSaleBusyObserved = Plugin.PluginInterface.GetIpcSubscriber<bool>("AutoRetainer.PluginState.IsBusy").InvokeFunc();
            StatusMessage = npcSaleBusyObserved
                ? "Waiting for AutoRetainer work after the NPC sale command to settle."
                : "Waiting for AutoRetainer to start work after the NPC sale command.";
            return;
        }
        if (activeNpcRepairMode == NpcRepairMode.YesInn
            && TryCompleteRepairIfFinished("Equipped gear is already fully repaired; reached the mender."))
            return;
        if (now - lastInteractUtc >= InteractRetryCooldown)
        {
            StatusMessage = $"Interacting with repair NPC {targetNpcName}.";
            TryInteractWithRepairNpc(targetNpc);
        }
    }

    private void UpdateNpcRepairWindow(DateTime now)
    {
        if (TryCompleteRepairIfFinished($"NPC repair finished with {targetNpcName}; equipped gear is fully repaired."))
            return;

        if (TryHandleRepairConfirm(now, $"Confirming NPC repair with {targetNpcName}."))
            return;

        var repairAddon = GetVisibleAddon<AddonRepair>("Repair");
        if (repairAddon == null)
        {
            repairWindowSeenUtc = DateTime.MinValue;
            StatusMessage = $"Waiting for repair window from {targetNpcName}.";
            return;
        }

        NoteRepairWindowSeen(now);
        if (!repairSubmissionSent)
        {
            if (now - repairWindowSeenUtc < UiSettleCooldown)
            {
                StatusMessage = $"Waiting for the repair window from {targetNpcName} to populate.";
                return;
            }

            if (!IsRepairAllEnabled(repairAddon))
            {
                GameInteractionHelper.TryCloseAddon("Repair", log);
                Complete($"No NPC-repairable gear was available through {targetNpcName}.");
                return;
            }

            if (now - lastActionUtc >= UiRetryCooldown)
            {
                ClickRepairAll(repairAddon);
                repairSubmissionSent = true;
                lastActionUtc = now;
                StatusMessage = $"Submitting NPC repair with {targetNpcName}.";
            }

            return;
        }

        if (TryCompleteRepairIfFinished($"NPC repair finished with {targetNpcName}; equipped gear is fully repaired."))
            return;

        if (GameInteractionHelper.IsAddonVisible("SelectYesno"))
        {
            StatusMessage = $"Confirming NPC repair with {targetNpcName}.";
            return;
        }

        if (now - lastActionUtc < TimeSpan.FromSeconds(5))
        {
            StatusMessage = $"Waiting for NPC repair with {targetNpcName} to settle.";
            return;
        }

        var stillEnabled = IsRepairAllEnabled(repairAddon);
        GameInteractionHelper.TryCloseAddon("Repair", log);
        Complete(stillEnabled
            ? $"NPC repair settled with {targetNpcName}, but some gear may remain unrepaired."
            : $"NPC repair finished with {targetNpcName}.");
    }

    private void UpdateExtractMateria()
    {
        if (!PrepareForUiWork("materia extraction"))
            return;

        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
        {
            Fail("Inventory manager was unavailable.");
            return;
        }

        if (inventoryManager->GetEmptySlotsInBag() < 1)
        {
            Fail("Materia extraction needs at least one empty inventory slot.");
            return;
        }

        var now = DateTime.UtcNow;
        var materializeDialog = GetVisibleAddon<AddonMaterializeDialog>("MaterializeDialog");
        if (materializeDialog != null)
        {
            if (now - lastActionUtc >= UiRetryCooldown)
            {
                ClickButtonIfEnabled(materializeDialog->YesButton, (AtkUnitBase*)materializeDialog);
                extractAttemptedAny = true;
                materializeAttemptPending = false;
                lastActionUtc = now;
                StatusMessage = "Confirming materia extraction.";
            }

            return;
        }

        if (!GameInteractionHelper.IsAddonVisible("Materialize"))
        {
            if (materializeCategory > LastMaterializeCategory)
            {
                Complete(extractAttemptedAny
                    ? "Materia extraction finished."
                    : "No extractable materia was found.");
                return;
            }

            if (now - lastActionUtc >= UiRetryCooldown
                && GameInteractionHelper.TryUseGeneralAction(MaterializeGeneralAction, log))
            {
                lastActionUtc = now;
                StatusMessage = "Opening materia extraction.";
            }

            return;
        }

        if (materializeCategory > LastMaterializeCategory)
        {
            GameInteractionHelper.TryCloseAddon("Materialize", log);
            Complete(extractAttemptedAny
                ? "Materia extraction finished."
                : "No extractable materia was found.");
            return;
        }

        if (!materializeCategoryArmed)
        {
            if (now - lastActionUtc >= UiRetryCooldown)
            {
                GameInteractionHelper.FireAddonCallback("Materialize", false, 1, materializeCategory);
                materializeCategoryArmed = true;
                materializeAttemptPending = false;
                lastActionUtc = now;
                StatusMessage = $"Switching materia extraction to category {materializeCategory}.";
            }

            return;
        }

        if (!materializeAttemptPending)
        {
            if (now - lastActionUtc >= UiRetryCooldown)
            {
                GameInteractionHelper.FireAddonCallback("Materialize", true, 2, 0);
                materializeAttemptPending = true;
                lastActionUtc = now;
                StatusMessage = $"Trying materia extraction in category {materializeCategory}.";
            }

            return;
        }

        if (now - lastActionUtc < MaterializeResultWait)
        {
            StatusMessage = $"Waiting for materia extraction result in category {materializeCategory}.";
            return;
        }

        materializeCategory++;
        materializeCategoryArmed = false;
        materializeAttemptPending = false;
        StatusMessage = materializeCategory <= LastMaterializeCategory
            ? $"No extractable item found in the previous category; advancing to {materializeCategory}."
            : "No further materia categories remain.";
    }

    private void UpdateDesynthFromInventory()
    {
        if (!PrepareForUiWork("inventory desynthesis"))
            return;

        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
        {
            Fail("Inventory manager was unavailable.");
            return;
        }

        if (inventoryManager->GetEmptySlotsInBag() < 1)
        {
            Fail("Inventory desynthesis needs at least one empty inventory slot.");
            return;
        }

        var now = DateTime.UtcNow;
        var salvageResult = GetVisibleAddon<AtkUnitBase>("SalvageResult");
        if (salvageResult != null)
        {
            if (pendingDesynthItemId != 0)
            {
                if (activeDesynthPolicy?.Source == DesynthSource.LastDutyGains)
                    desynthDutyLedgerStore.Consume(pendingDesynthItemId);
                DesynthCompletedCount++;
                pendingDesynthItemId = 0;
            }
            salvageResult->Close(true);
            lastActionUtc = now;
            desynthCategorySeenUtc = now;
            desynthSettledCategoryIndex = -1;
            StatusMessage = "Closing desynthesis result window.";
            return;
        }

        var salvageDialog = GetVisibleAddon<AddonSalvageDialog>("SalvageDialog");
        if (salvageDialog != null)
        {
            if (now - lastActionUtc >= UiRetryCooldown)
            {
                ClickButtonIfEnabled(salvageDialog->DesynthesizeButton, (AtkUnitBase*)salvageDialog);
                desynthAttemptedAny = true;
                lastActionUtc = now;
                desynthCategorySeenUtc = now;
                desynthSettledCategoryIndex = -1;
                StatusMessage = "Confirming desynthesis.";
            }

            return;
        }

        var desynthCategories = GetActiveDesynthCategories();
        if (desynthCategoryIndex >= desynthCategories.Count)
        {
            GameInteractionHelper.TryCloseAddon("SalvageItemSelector", log);
            Complete(desynthAttemptedAny
                ? $"Desynthesis finished; completed {DesynthCompletedCount} item(s)."
                : "No desynthable inventory items were found.");
            return;
        }

        var agent = AgentSalvage.Instance();
        if (agent == null)
        {
            Fail("Desynthesis agent was unavailable.");
            return;
        }

        var selector = GetVisibleAddon<AddonSalvageItemSelector>("SalvageItemSelector");
        if (selector == null)
        {
            desynthWindowSeenUtc = DateTime.MinValue;
            desynthCategorySeenUtc = DateTime.MinValue;
            desynthSettledCategoryIndex = -1;
            if (condition[ConditionFlag.Occupied39] || condition[ConditionFlag.OccupiedInQuestEvent])
            {
                StatusMessage = "Waiting for desynthesis to finish settling.";
                return;
            }

            if (now - lastActionUtc >= DesynthReopenCooldown)
            {
                agent->AgentInterface.Show();
                lastActionUtc = now;
                StatusMessage = "Opening desynthesis window.";
                return;
            }

            StatusMessage = "Waiting for desynthesis to settle before reopening.";
            return;
        }

        if (desynthWindowSeenUtc == DateTime.MinValue)
            desynthWindowSeenUtc = now;

        agent->ItemListRefresh(true);
        var desiredCategory = desynthCategories[desynthCategoryIndex];
        if (agent->SelectedCategory != desiredCategory)
        {
            agent->SelectedCategory = desiredCategory;
            lastActionUtc = now;
            desynthCategorySeenUtc = now;
            desynthSettledCategoryIndex = -1;
            StatusMessage = $"Switching desynthesis to {GetDesynthCategoryLabel(desiredCategory)}.";
            return;
        }

        if (desynthSettledCategoryIndex != desynthCategoryIndex)
        {
            if (desynthCategorySeenUtc == DateTime.MinValue)
                desynthCategorySeenUtc = now;

            if (now - desynthWindowSeenUtc < UiSettleCooldown
                || now - desynthCategorySeenUtc < UiSettleCooldown)
            {
                StatusMessage = $"Waiting for {GetDesynthCategoryLabel(desiredCategory)} to populate.";
                return;
            }

            desynthSettledCategoryIndex = desynthCategoryIndex;
        }

        if (selector->ItemCount == 0 || agent->ItemCount == 0)
        {
            desynthCategoryIndex++;
            lastActionUtc = now;
            desynthCategorySeenUtc = DateTime.MinValue;
            desynthSettledCategoryIndex = -1;
            StatusMessage = desynthCategoryIndex < desynthCategories.Count
                ? $"No desynthable items remained in {GetDesynthCategoryLabel(desiredCategory)}; moving on."
                : "No further desynthesis categories remain.";
            return;
        }

        if (now - lastActionUtc >= UiRetryCooldown)
        {
            var eligibleIndex = FindEligibleDesynthItemIndex(agent, desiredCategory, out var eligibleItemId, out var eligibleCount);
            DesynthEligibleCount = eligibleCount;
            if (eligibleIndex >= 0)
            {
                pendingDesynthItemId = eligibleItemId;
                GameInteractionHelper.FireAddonCallback("SalvageItemSelector", true, 12, eligibleIndex);
                lastActionUtc = now;
                StatusMessage = $"Selecting eligible item {eligibleItemId} from {GetDesynthCategoryLabel(desiredCategory)} for desynthesis.";
                return;
            }

            desynthCategoryIndex++;
            lastActionUtc = now;
            desynthCategorySeenUtc = DateTime.MinValue;
            desynthSettledCategoryIndex = -1;
            StatusMessage = desynthCategoryIndex < desynthCategories.Count
                ? $"No policy-eligible items remained in {GetDesynthCategoryLabel(desiredCategory)}; moving on."
                : "No further policy-eligible desynthesis categories remain.";
            return;
        }

        StatusMessage = $"Waiting for desynthesis response in {GetDesynthCategoryLabel(desiredCategory)}.";
    }

    private void UpdateNpcRepairTravel(DateTime now)
    {
        if (activeNpcRepairFieldRoute is { } fieldRoute)
        {
            UpdateNpcRepairFieldTravel(now, fieldRoute);
            return;
        }

        if (activeNpcRepairInnRoute is not { } route)
        {
            Fail("NPC repair inn-travel state was lost.");
            return;
        }

        if (now - npcRepairTravelStageStartedUtc > NpcRepairTravelTimeout)
        {
            Fail($"Timed out while travelling to {route.TerritoryName} for NPC repair.");
            return;
        }

        switch (npcRepairTravelStage)
        {
            case NpcRepairTravelStage.TeleportingToInnAethernet:
                UpdateNpcRepairInnTeleport(now, route);
                break;
            case NpcRepairTravelStage.WalkingInnPath:
                UpdateNpcRepairInnPath(now, route);
                break;
            case NpcRepairTravelStage.AwaitingRepairNpc:
                UpdateNpcRepairInnNpcSearch(route);
                break;
        }
    }

    private void UpdateNpcRepairFieldTravel(DateTime now, ResolvedFieldRepairRoute route)
    {
        if (now - npcRepairTravelStageStartedUtc > NpcRepairTravelTimeout)
        {
            var routeFailure = BuildNpcRepairFieldRouteTimeoutReason(now, route);
            if (TryRetryNpcRepairFieldRoute(route, routeFailure, out var exhaustedMessage))
                return;

            Fail(exhaustedMessage);
            return;
        }

        switch (npcRepairTravelStage)
        {
            case NpcRepairTravelStage.TeleportingToFieldAetheryte:
                UpdateNpcRepairFieldTeleport(now, route);
                break;
            case NpcRepairTravelStage.AwaitingRepairNpc:
                UpdateNpcRepairFieldNpcSearch(now, route);
                break;
            default:
                Fail("NPC repair field-travel state was invalid.");
                break;
        }
    }

    private bool PrepareForUiWork(string actionLabel, bool allowDismount = true)
    {
        if (condition[ConditionFlag.BetweenAreas])
        {
            StatusMessage = $"Waiting for zoning to finish before {actionLabel}.";
            return false;
        }

        if (!TryGetMountedOrRidingOrMountingBlocker(out var blocker))
            return true;

        if (!allowDismount || condition[ConditionFlag.RidingPillion] || condition[ConditionFlag.Mounting71])
        {
            StatusMessage = $"Waiting for {blocker} to clear before {actionLabel}.";
            return false;
        }

        var now = DateTime.UtcNow;
        if (now - lastActionUtc >= UiRetryCooldown
            && GameInteractionHelper.TryUseGeneralAction(DismountGeneralAction, log))
        {
            lastActionUtc = now;
            StatusMessage = $"Dismounting before {actionLabel}.";
        }

        return false;
    }

    private void TrySelectRepairMenuOption(DateTime now)
    {
        if (!OwnsNpcRepairInteraction())
        {
            Fail("ADS did not open this repair menu; no menu option was selected.");
            return;
        }
        // The selector may not be populated yet, or may already be cleared while
        // its addon closes after dispatch. Let the existing transition cooldown settle.
        if (now - lastInteractUtc < MenuRetryCooldown || now - lastMenuSelectionUtc < MenuRetryCooldown)
        {
            StatusMessage = $"Waiting for repair menu retry window on {targetNpcName}.";
            return;
        }
        var selector = EventHandlerSelector.Instance();
        if (selector == null || selector->Target == null
            || !OwnsNpcRepairNpc((ulong)selector->Target->GetGameObjectId(), selector->Target->BaseId)
            || selector->OptionsCount is < 1 or > 32)
        {
            Fail("ADS could not identify its repair NPC's live menu; no menu option was selected.");
            return;
        }

        var options = new ShopRuntimeMenuOption[selector->OptionsCount];
        for (var index = 0; index < options.Length; index++)
        {
            var option = selector->Options[index];
            options[index] = new ShopRuntimeMenuOption(
                option.Handler == null ? 0 : option.Handler->Info.EventId.Id,
                option.GlobalIndex, option.LocalIndex);
        }

        if (!ShopMenuRouteResolver.TryResolveSelectorIndex(targetNpcBaseId, selector->Target->BaseId,
                new ShopMenuPathStep(ShopMenuPathStepKind.ENpcData, targetNpcRepairIndex, RepairShopEventId),
                options, out var selectorIndex, out var diagnostic))
        {
            Fail($"ADS could not identify a unique repair option: {diagnostic}");
            return;
        }

        var events = EventFramework.Instance();
        if (events == null)
        {
            Fail("ADS could not identify its repair NPC's live menu; no menu option was selected.");
            return;
        }
        log.Debug("[ADS][Utility] {Diagnostic}", diagnostic);
        events->InteractWithHandlerFromSelector(selectorIndex);
        lastMenuSelectionUtc = now;
        StatusMessage = $"Selecting the verified repair handler on {targetNpcName}.";
    }

    private unsafe void ClickRepairAll(AddonRepair* repairAddon)
        => ClickButtonIfEnabled(repairAddon->RepairAllButton, (AtkUnitBase*)repairAddon);

    private static unsafe bool IsRepairAllEnabled(AddonRepair* repairAddon)
        => repairAddon->RepairAllButton != null && repairAddon->RepairAllButton->IsEnabled;

    private bool TryCompleteRepairIfFinished(string message)
    {
        if (npcSaleRequest) return false;
        if (activeTask == UtilityTask.NpcRepair && activeNpcRepairMode == NpcRepairMode.YesInn && !npcRepairMenderReached)
            return false;
        if (!TryGetEquippedGearNeedsRepair(out var needsRepair) || needsRepair)
            return false;

        CloseRepairAddons();
        Complete(message);
        return true;
    }

    private static unsafe bool TryGetEquippedGearNeedsRepair(out bool needsRepair)
    {
        needsRepair = false;

        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
            return false;

        var equippedContainer = inventoryManager->GetInventoryContainer(InventoryType.EquippedItems);
        if (equippedContainer == null)
            return false;

        for (var i = 0; i < equippedContainer->Size; i++)
        {
            var item = equippedContainer->GetInventorySlot(i);
            if (item == null || item->ItemId == 0)
                continue;

            var conditionPercent = item->Condition / 300;
            if (conditionPercent < FullyRepairedConditionPercent)
            {
                needsRepair = true;
                return true;
            }
        }

        return true;
    }

    private void NoteRepairWindowSeen(DateTime now)
    {
        if (repairWindowSeenUtc == DateTime.MinValue)
            repairWindowSeenUtc = now;
    }

    private bool TryHandleRepairConfirm(DateTime now, string statusMessage)
    {
        if (!GameInteractionHelper.IsAddonVisible("SelectYesno"))
            return false;

        repairSubmissionSent = true;
        if (TryCompleteRepairIfFinished($"{statusMessage} Equipped gear is fully repaired."))
            return true;

        if (IsRepairInteractionBlocked(out var blocker))
        {
            StatusMessage = $"{statusMessage} Waiting for {blocker}; rechecking durability instead of clicking again.";
            return true;
        }

        if (repairConfirmClickAttempts >= RepairConfirmMaxAttempts)
        {
            StatusMessage = $"{statusMessage} Waiting for the confirmation dialog to close.";
            return true;
        }

        if (lastRepairConfirmClickUtc != DateTime.MinValue
            && now - lastRepairConfirmClickUtc < RepairConfirmRetryCooldown)
        {
            StatusMessage = statusMessage;
            return true;
        }

        lastRepairConfirmClickUtc = now;
        lastActionUtc = now;

        if (GameInteractionHelper.ClickYesIfVisible(log))
        {
            repairConfirmClickAttempts++;
            StatusMessage = repairConfirmClickAttempts == 1
                ? statusMessage
                : $"{statusMessage} Retrying confirmation ({repairConfirmClickAttempts}/{RepairConfirmMaxAttempts}).";
            return true;
        }

        StatusMessage = $"{statusMessage} Yes click failed; retrying confirmation after cooldown ({repairConfirmClickAttempts}/{RepairConfirmMaxAttempts} sent).";
        log.Warning("[ADS] Repair SelectYesno was visible, but the Yes click failed; ADS will retry after cooldown without consuming a repair-confirm attempt.");
        return true;
    }

    private bool IsRepairInteractionBlocked(out string blocker)
    {
        if (condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51])
        {
            blocker = "zoning";
            return true;
        }

        if (condition[ConditionFlag.OccupiedInCutSceneEvent]
            || condition[ConditionFlag.WatchingCutscene])
        {
            blocker = "cutscene";
            return true;
        }

        blocker = string.Empty;
        return false;
    }

    private void CloseRepairAddons()
    {
        if (activeTask == UtilityTask.NpcRepair && !OwnsNpcRepairWindow())
            return;
        GameInteractionHelper.TryCloseAddon("SelectYesno", log);
        GameInteractionHelper.TryCloseAddon("Repair", log);
    }

    private void CloseOwnedNpcRepairMenus()
    {
        if (!OwnsNpcRepairInteraction() || !shopRuntime.IsSelectionMenuVisible)
            return;
        var selector = EventHandlerSelector.Instance();
        if (selector == null || selector->Target == null
            || !OwnsNpcRepairNpc((ulong)selector->Target->GetGameObjectId(), selector->Target->BaseId))
            return;
        GameInteractionHelper.TryCloseAddon("SelectString", log);
        GameInteractionHelper.TryCloseAddon("SelectIconString", log);
    }

    private void ResetRepairSubmission()
    {
        repairSubmissionSent = false;
        lastRepairConfirmClickUtc = DateTime.MinValue;
        repairConfirmClickAttempts = 0;
    }

    private void BeginNpcRepairWithCandidate(RepairNpcCandidate targetNpc, string logPrefix)
    {
        var innRoute = activeNpcRepairInnRoute;
        var innPathIndex = npcRepairInnPathIndex;
        ClearNpcRepairInnTravel();
        if (activeNpcRepairMode == NpcRepairMode.YesInn)
        {
            activeNpcRepairInnRoute = innRoute;
            npcRepairInnPathIndex = innPathIndex;
        }
        targetNpcGameObjectId = targetNpc.GameObjectId;
        targetNpcBaseId = targetNpc.BaseId;
        targetNpcName = targetNpc.Name;
        targetNpcRepairIndex = targetNpc.RepairIndex;
        npcRepairUiCharacter = 0;
        ResetRepairSubmission();
        if (npcSaleRequest)
            startedAtUtc = DateTime.UtcNow;

        var distance = targetNpc.Distance;
        if (distance <= RepairNpcInteractRadius)
        {
            npcRepairMenderReached = true;
            StopMovementIfNpcRepair();
            if (activeNpcRepairMode == NpcRepairMode.YesInn
                && TryCompleteRepairIfFinished("Equipped gear is already fully repaired; reached the mender."))
                return;
            if (npcSaleRequest)
                StatusMessage = gearCleanupSelection != null ? $"Reached sale vendor {targetNpcName}; preparing equipment selling."
                    : $"Reached sale vendor {targetNpcName}; preparing the AutoRetainer handoff.";
            else
            {
                StatusMessage = $"Interacting with repair NPC {targetNpcName}.";
                TryInteractWithRepairNpc(targetNpc.GameObject);
            }
        }
        else
        {
            StatusMessage = npcSaleRequest ? $"Moving to sale vendor {targetNpcName}."
                : $"Moving to repair NPC {targetNpcName}.";
            SendMoveCommand(targetNpc.GameObject.Position, targetNpcName, initial: true);
        }

        log.Information($"[ADS][Utility] {logPrefix} {targetNpcName} at {distance:0.0}y.");
    }

    private bool TryBeginNpcRepairInnTravel(out string failureMessage)
    {
        failureMessage = string.Empty;
        if (activeNpcRepairMode != NpcRepairMode.YesInn && !IsLifestreamLoaded())
        {
            failureMessage = $"No repair NPC found within {RepairNpcSearchRadius:0}y, and Lifestream was not loaded for the inn fallback.";
            return false;
        }

        if (!TryResolveInnRepairRoute(out var route))
        {
            failureMessage = npcRepairDestinationAethernet != 0
                ? $"The requested inn repair route (aethernet {npcRepairDestinationAethernet}) has no available unlocked city aetheryte or route data."
                : $"No repair NPC found within {RepairNpcSearchRadius:0}y, and no unlocked inn repair route was available.";
            return false;
        }

        activeNpcRepairInnRoute = route;
        if (activeNpcRepairMode == NpcRepairMode.YesInn && npcRepairRequiredInnTerritory == 0)
            npcRepairRequiredInnTerritory = GetInnRoom(route.AethernetId);
        npcRepairInnPathIndex = 0;
        npcRepairTravelCommandUtc = DateTime.MinValue;
        ResetRepairSubmission();

        if (route.TerritoryTypeId == clientState.TerritoryType)
        {
            if (TryFindNearbyRepairNpc(out var targetNpc))
            {
                BeginNpcRepairWithCandidate(targetNpc, "Starting inn repair with");
                return IsRunning;
            }
            SetNpcRepairTravelStage(
                route.Path.Length > 0
                    ? NpcRepairTravelStage.WalkingInnPath
                    : NpcRepairTravelStage.AwaitingRepairNpc,
                route.Path.Length > 0
                    ? $"Moving toward the {route.TerritoryName} inn repair route."
                    : $"Looking for a repair NPC near the {route.TerritoryName} inn.");
            log.Information($"[ADS][Utility] No local repair NPC was found; using the current-territory inn repair route in {route.TerritoryName}.");
            return true;
        }

        if (!IsLifestreamLoaded() || !TrySendNpcRepairInnTeleport(route))
        {
            activeNpcRepairInnRoute = null;
            failureMessage = $"No repair NPC found within {RepairNpcSearchRadius:0}y, and ADS could not start the Lifestream inn teleport.";
            return false;
        }

        SetNpcRepairTravelStage(
            NpcRepairTravelStage.TeleportingToInnAethernet,
            $"Travelling to {route.AethernetName} in {route.TerritoryName} for NPC repair.");
        return true;
    }

    private bool TryBeginNpcRepairFieldTravel(out string failureMessage)
    {
        failureMessage = string.Empty;
        if (npcSaleRequest)
        {
            try
            {
                if (Plugin.PluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy").InvokeFunc())
                { failureMessage = "Lifestream already owns work; NPC sale travel was not started."; return false; }
            }
            catch (Exception ex)
            { failureMessage = $"NPC sale travel is unavailable: {ex.Message}"; return false; }
        }
        if (!IsLifestreamLoaded())
        {
            failureMessage = $"No repair NPC found within {RepairNpcSearchRadius:0}y, and Lifestream was not loaded for field-aetheryte repair travel.";
            return false;
        }

        if (!TryResolveFieldRepairRoute(out var route))
        {
            failureMessage = $"No repair NPC found within {RepairNpcSearchRadius:0}y, and no eligible unlocked non-city field aetheryte was available for repair travel.";
            return false;
        }

        activeNpcRepairFieldRoute = route;
        npcRepairInnPathIndex = 0;
        ResetRepairSubmission();

        if (!TrySendNpcRepairFieldTeleport(route))
        {
            if (TryRetryNpcRepairFieldRoute(
                    route,
                    $"ADS could not send the Lifestream field-aetheryte teleport command to {FormatFieldRepairRoute(route)}.",
                    out failureMessage))
            {
                return true;
            }

            activeNpcRepairFieldRoute = null;
            return false;
        }

        SetNpcRepairTravelStage(
            NpcRepairTravelStage.TeleportingToFieldAetheryte,
            $"Travelling to {route.AetheryteName} in {route.TerritoryName} for NPC repair.");
        return true;
    }

    private bool TryResolveInnRepairRoute(out ResolvedInnRepairRoute route)
    {
        route = default;
        var aetheryteSheet = GetAetheryteSheet();
        if (aetheryteSheet == null)
            return false;

        ResolvedInnRepairRoute? sameTerritoryRoute = null;
        ResolvedInnRepairRoute? bestUnlockedRoute = null;
        foreach (var seed in InnRepairRouteSeeds)
        {
            if (npcRepairDestinationAethernet != 0 && seed.AethernetId != npcRepairDestinationAethernet)
                continue;
            if (!aetheryteSheet.TryGetRow(seed.AethernetId, out var aetheryte))
                continue;

            var territoryTypeId = aetheryte.Territory.RowId;
            var territoryName = GameInteractionHelper.GetTerritoryName(dataManager, territoryTypeId);
            var aethernetName = aetheryte.AethernetName.ValueNullable?.Name.ToString().Trim();
            if (string.IsNullOrWhiteSpace(aethernetName))
                continue;

            if (territoryTypeId == clientState.TerritoryType)
            {
                sameTerritoryRoute = new ResolvedInnRepairRoute(
                    territoryTypeId,
                    territoryName,
                    seed.AethernetId,
                    aethernetName,
                    seed.Path,
                    0);
                continue;
            }

            if (!TryGetUnlockedInnRouteGilCost(aetheryte, out var gilCost))
                continue;

            var candidate = new ResolvedInnRepairRoute(
                territoryTypeId,
                territoryName,
                seed.AethernetId,
                aethernetName,
                seed.Path,
                gilCost);
            if (bestUnlockedRoute is null || candidate.GilCost < bestUnlockedRoute.Value.GilCost)
                bestUnlockedRoute = candidate;
        }

        if (sameTerritoryRoute is not null)
        {
            route = sameTerritoryRoute.Value;
            return true;
        }

        if (bestUnlockedRoute is not null)
        {
            route = bestUnlockedRoute.Value;
            return true;
        }

        return false;
    }

    private bool TryResolveFieldRepairRoute(out ResolvedFieldRepairRoute route)
    {
        route = default;
        var aetheryteSheet = GetAetheryteSheet();
        if (aetheryteSheet == null)
            return false;

        var eligibleCount = 0;
        var candidates = new List<ResolvedFieldRepairRoute>();
        try
        {
            for (var index = 0; index < Plugin.AetheryteList.Length; index++)
            {
                var entry = Plugin.AetheryteList[index];
                if (entry == null || entry.AetheryteId == 0)
                    continue;

                if (!aetheryteSheet.TryGetRow(entry.AetheryteId, out var aetheryte))
                    continue;

                if (!aetheryte.IsAetheryte)
                    continue;

                var territoryTypeId = aetheryte.Territory.RowId;
                var territoryName = GameInteractionHelper.GetTerritoryName(dataManager, territoryTypeId);
                var aetheryteName = aetheryte.PlaceName.ValueNullable?.Name.ToString().Trim();
                if (string.IsNullOrWhiteSpace(aetheryteName))
                    continue;

                if (!IsEligibleFieldRepairAetheryte(aetheryte.RowId, aetheryteName, territoryName))
                    continue;

                eligibleCount++;
                if (failedNpcRepairFieldAetheryteIds.Contains(aetheryte.RowId))
                    continue;

                var candidate = new ResolvedFieldRepairRoute(
                    territoryTypeId,
                    territoryName,
                    aetheryte.RowId,
                    aetheryteName,
                    (int)entry.GilCost);

                candidates.Add(candidate);
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[ADS][Utility] Failed to inspect unlocked aetherytes for NPC repair field travel.");
        }

        var selectedRoute = candidates
            .OrderBy(candidate => GetPreferredFieldRepairRouteRank(candidate.AetheryteId))
            .ThenBy(candidate => candidate.TerritoryTypeId == clientState.TerritoryType ? 0 : 1)
            .ThenBy(candidate => candidate.GilCost)
            .ThenBy(candidate => candidate.TerritoryName, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.AetheryteName, StringComparer.Ordinal)
            .FirstOrDefault();

        if (selectedRoute.AetheryteId == 0)
        {
            LogNpcRepairFieldRouteScan(eligibleCount, candidates.Count, null);
            return false;
        }

        LogNpcRepairFieldRouteScan(eligibleCount, candidates.Count, selectedRoute);
        route = selectedRoute;
        return true;
    }

    private void LogNpcRepairFieldRouteScan(int eligibleCount, int remainingCount, ResolvedFieldRepairRoute? selectedRoute)
    {
        var now = DateTime.UtcNow;
        if (now - lastNpcRepairFieldRouteScanLogUtc < NpcRepairFieldRouteLogCooldown)
            return;

        lastNpcRepairFieldRouteScanLogUtc = now;
        var selectedText = selectedRoute is { } route
            ? FormatFieldRepairRoute(route)
            : "none";
        log.Information(
            $"[ADS][Utility] Field repair route scan: eligible={eligibleCount}, remaining={remainingCount}, " +
            $"failed={failedNpcRepairFieldAetheryteIds.Count}, selected={selectedText}.");
    }

    private static int GetPreferredFieldRepairRouteRank(uint aetheryteId)
    {
        for (var index = 0; index < PreferredFieldRepairAetheryteIds.Length; index++)
        {
            if (PreferredFieldRepairAetheryteIds[index] == aetheryteId)
                return index;
        }

        return PreferredFieldRepairAetheryteIds.Length;
    }

    private static bool IsEligibleFieldRepairAetheryte(uint aetheryteId, string aetheryteName, string territoryName)
    {
        if (FieldRepairDeniedAetheryteIds.Contains(aetheryteId))
            return false;

        var combined = $"{aetheryteName} {territoryName}".ToLowerInvariant();
        return !FieldRepairDeniedNameTerms.Any(combined.Contains);
    }

    private bool TryGetUnlockedInnRouteGilCost(Aetheryte destination, out int gilCost)
    {
        gilCost = int.MaxValue;
        var aetheryteSheet = GetAetheryteSheet();
        if (aetheryteSheet == null)
            return false;

        uint aetheryteId = 0;
        foreach (var aetheryte in aetheryteSheet)
        {
            // City aethernet shards can be in another territory from their main aetheryte
            // (for example, Limsa's inn is in Upper Decks and its main crystal is in Lower Decks).
            if (!aetheryte.IsAetheryte
                || (destination.AethernetGroup != 0
                    ? aetheryte.AethernetGroup != destination.AethernetGroup
                    : aetheryte.Territory.RowId != destination.Territory.RowId))
                continue;

            aetheryteId = aetheryte.RowId;
            break;
        }

        if (aetheryteId == 0)
            return false;

        try
        {
            for (var index = 0; index < Plugin.AetheryteList.Length; index++)
            {
                var entry = Plugin.AetheryteList[index];
                if (entry == null)
                    continue;

                if (entry.AetheryteId != aetheryteId)
                    continue;

                gilCost = (int)entry.GilCost;
                return true;
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[ADS][Utility] Failed to inspect unlocked aetherytes for NPC repair fallback.");
        }

        return false;
    }

    private bool TrySendNpcRepairInnTeleport(ResolvedInnRepairRoute route)
    {
        if (activeNpcRepairMode == NpcRepairMode.YesInn)
        {
            try
            {
                if (Plugin.PluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy").InvokeFunc())
                    return false;
            }
            catch (Exception ex)
            {
                log.Warning(ex, "[ADS][InnRepair] Cannot verify Lifestream readiness.");
                return false;
            }
        }
        var command = $"/li {route.AethernetName}";
        npcRepairTravelCommandUtc = DateTime.UtcNow;
        npcRepairInnArrivalReadyUtc = DateTime.MinValue;
        if (!GameInteractionHelper.TrySendChatCommand(commandManager, command, log))
            return false;

        npcRepairOwnsInnTravel = activeNpcRepairMode == NpcRepairMode.YesInn;
        log.Information($"[ADS][Utility] No local repair NPC was found; sending {command} to reach {route.TerritoryName} for NPC repair.");
        return true;
    }

    private bool TrySendNpcRepairFieldTeleport(ResolvedFieldRepairRoute route)
    {
        if (npcSaleRequest && Plugin.PluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy").InvokeFunc())
            return false;
        var command = $"/li {route.AetheryteName}";
        npcRepairTravelCommandUtc = DateTime.UtcNow;
        npcRepairFieldRouteStartTerritoryId = clientState.TerritoryType;
        npcRepairFieldRouteSawLoading = false;
        npcRepairInnArrivalReadyUtc = DateTime.MinValue;
        lastNpcRepairFieldRouteWaitLogUtc = DateTime.MinValue;
        if (!GameInteractionHelper.TrySendChatCommand(commandManager, command, log))
            return false;

        if (npcSaleRequest)
            npcRepairOwnsInnTravel = true;
        log.Information($"[ADS][Utility] No local repair NPC was found; sent {command} for field repair route {FormatFieldRepairRoute(route)}.");
        return true;
    }

    private void UpdateNpcRepairInnTeleport(DateTime now, ResolvedInnRepairRoute route)
    {
        if (npcRepairOwnsInnTravel && Plugin.PluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy").InvokeFunc())
        {
            npcRepairInnArrivalReadyUtc = DateTime.MinValue;
            return;
        }
        if (condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51])
        {
            npcRepairInnArrivalReadyUtc = DateTime.MinValue;
            StatusMessage = $"Waiting for the inn teleport to {route.TerritoryName} to finish.";
            return;
        }

        if (clientState.TerritoryType != route.TerritoryTypeId)
        {
            npcRepairInnArrivalReadyUtc = DateTime.MinValue;
            StatusMessage = now - npcRepairTravelCommandUtc < LifestreamTeleportSettleCooldown
                ? $"Waiting for Lifestream to route to {route.AethernetName}."
                : $"Waiting to arrive at {route.TerritoryName} for NPC repair.";
            return;
        }

        if (objectTable.LocalPlayer == null)
        {
            npcRepairInnArrivalReadyUtc = DateTime.MinValue;
            return;
        }

        // Lifestream can finish its task queue before the final aethernet loading starts.
        // Measure stable arrival here, not from when the travel command was sent.
        if (npcRepairInnArrivalReadyUtc == DateTime.MinValue)
            npcRepairInnArrivalReadyUtc = now;
        if (now - npcRepairInnArrivalReadyUtc < UiSettleCooldown)
        {
            StatusMessage = $"Waiting for {route.TerritoryName} to settle after the Lifestream hop.";
            return;
        }

        npcRepairOwnsInnTravel = false;
        SetNpcRepairTravelStage(
            route.Path.Length > 0
                ? NpcRepairTravelStage.WalkingInnPath
                : NpcRepairTravelStage.AwaitingRepairNpc,
            route.Path.Length > 0
                ? $"Moving toward the {route.TerritoryName} inn repair route."
                : $"Looking for a repair NPC near the {route.TerritoryName} inn.");
    }

    private void UpdateNpcRepairFieldTeleport(DateTime now, ResolvedFieldRepairRoute route)
    {
        if (npcSaleRequest && Plugin.PluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy").InvokeFunc())
        {
            npcRepairInnArrivalReadyUtc = DateTime.MinValue;
            StatusMessage = "Waiting for ADS sale travel through Lifestream to finish.";
            return;
        }
        if (condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51])
        {
            npcRepairInnArrivalReadyUtc = DateTime.MinValue;
            if (!npcRepairFieldRouteSawLoading)
                log.Information($"[ADS][Utility] Field repair route {FormatFieldRepairRoute(route)} entered loading after {(now - npcRepairTravelCommandUtc).TotalSeconds:0.0}s.");

            npcRepairFieldRouteSawLoading = true;
            StatusMessage = $"Waiting for the field-aetheryte teleport to {route.TerritoryName} to finish.";
            return;
        }

        if (clientState.TerritoryType != route.TerritoryTypeId)
        {
            npcRepairInnArrivalReadyUtc = DateTime.MinValue;
            StatusMessage = now - npcRepairTravelCommandUtc < LifestreamTeleportSettleCooldown
                ? $"Waiting for Lifestream to route to {route.AetheryteName}."
                : $"Waiting to arrive at {route.TerritoryName} for NPC repair.";
            LogNpcRepairFieldRouteWait(
                now,
                route,
                $"Waiting for field repair route arrival; current territory {clientState.TerritoryType}, expected {route.TerritoryTypeId}.");
            return;
        }

        if (objectTable.LocalPlayer == null)
        {
            npcRepairInnArrivalReadyUtc = DateTime.MinValue;
            StatusMessage = $"Waiting for {route.TerritoryName} to settle after the field-aetheryte hop.";
            return;
        }

        if (npcSaleRequest && npcRepairInnArrivalReadyUtc == DateTime.MinValue)
            npcRepairInnArrivalReadyUtc = now;
        var settledSince = npcSaleRequest ? npcRepairInnArrivalReadyUtc : npcRepairTravelCommandUtc;
        if (now - settledSince < UiSettleCooldown)
        {
            StatusMessage = $"Waiting for {route.TerritoryName} to settle after the field-aetheryte hop.";
            return;
        }
        if (npcSaleRequest)
            npcRepairOwnsInnTravel = false;

        SetNpcRepairTravelStage(
            NpcRepairTravelStage.AwaitingRepairNpc,
            $"Looking for a repair NPC near {route.AetheryteName} in {route.TerritoryName}.");
    }

    private void UpdateNpcRepairInnPath(DateTime now, ResolvedInnRepairRoute route)
    {
        if (objectTable.LocalPlayer == null)
        {
            StatusMessage = $"Waiting for the player object before moving through {route.TerritoryName}.";
            return;
        }

        if (npcRepairTravelStage != NpcRepairTravelStage.ReturningToInn
            && TryFindNearbyRepairNpc(out var targetNpc))
        {
            BeginNpcRepairWithCandidate(targetNpc, $"Found a mender on the {route.TerritoryName} inn route:");
            return;
        }

        if (npcRepairInnPathIndex >= route.Path.Length)
        {
            StopMovementIfNpcRepair();
            if (npcRepairTravelStage == NpcRepairTravelStage.ReturningToInn)
                return;
            SetNpcRepairTravelStage(
                NpcRepairTravelStage.AwaitingRepairNpc,
                $"Looking for a repair NPC near the {route.TerritoryName} inn.");
            return;
        }

        var waypoint = route.Path[npcRepairInnPathIndex];
        var distance = Vector3.Distance(objectTable.LocalPlayer.Position, waypoint);
        if (distance <= InnPathWaypointReachedRadius)
        {
            npcRepairInnPathIndex++;
            lastMoveCommandUtc = DateTime.MinValue;
            if (npcRepairInnPathIndex >= route.Path.Length)
            {
                StopMovementIfNpcRepair();
                if (npcRepairTravelStage == NpcRepairTravelStage.ReturningToInn)
                    return;
                SetNpcRepairTravelStage(
                    NpcRepairTravelStage.AwaitingRepairNpc,
                    $"Looking for a repair NPC near the {route.TerritoryName} inn.");
            }
            else
            {
                StatusMessage = $"Continuing toward the {route.TerritoryName} inn repair route.";
            }

            return;
        }

        var waypointLabel = $"{route.TerritoryName} inn waypoint {npcRepairInnPathIndex + 1}/{route.Path.Length}";
        StatusMessage = $"Moving to {waypointLabel}.";
        SendMoveCommand(waypoint, waypointLabel, initial: lastMoveCommandUtc == DateTime.MinValue);
    }

    private void UpdateNpcRepairInnNpcSearch(ResolvedInnRepairRoute route)
    {
        if (TryFindNearbyRepairNpc(out var targetNpc))
        {
            BeginNpcRepairWithCandidate(targetNpc, $"Reached the {route.TerritoryName} inn fallback and found");
            return;
        }

        StatusMessage = $"Looking for a repair NPC near the {route.TerritoryName} inn.";
    }

    private void UpdateNpcRepairInnExit()
    {
        var now = DateTime.UtcNow;
        if (now - npcRepairTravelStageStartedUtc > NpcRepairTravelTimeout)
        {
            Fail("Timed out leaving the inn room through its door.");
            return;
        }
        if (condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51] || objectTable.LocalPlayer == null)
            return;

        if (clientState.TerritoryType != npcRepairStartingInnTerritory)
        {
            if (GameInteractionHelper.IsInnTerritory(dataManager, (ushort)clientState.TerritoryType))
            {
                Fail("Inn exit reached an unexpected inn room.");
                return;
            }
            log.Information("[ADS][InnRepair] door exit; startingRoom={Room}; outsideTerritory={Territory}",
                npcRepairStartingInnTerritory, clientState.TerritoryType);
            StopMovementIfNpcRepair();
            if (npcRepairDestinationAethernet != 0 && npcRepairRequiredInnTerritory != npcRepairStartingInnTerritory)
            {
                if (!TryBeginNpcRepairInnTravel(out var failure))
                    Fail(failure);
                return;
            }
            // Same-room and automatic trips must find the mender outside this inn.
            if (!TryResolveInnRepairRoute(out var route) || route.TerritoryTypeId != clientState.TerritoryType)
            {
                Fail("Inn exit did not reach a supported inn repair city.");
                return;
            }
            activeNpcRepairInnRoute = route;
            SetNpcRepairTravelStage(route.Path.Length > 0
                    ? NpcRepairTravelStage.WalkingInnPath : NpcRepairTravelStage.AwaitingRepairNpc,
                "Looking for a repair NPC outside the inn room.");
            return;
        }

        if (npcRepairDoorInteracted && GameInteractionHelper.IsAddonVisible("SelectYesno"))
        {
            if (now - lastMenuSelectionUtc >= MenuRetryCooldown)
            {
                GameInteractionHelper.ClickYesIfVisible(log);
                lastMenuSelectionUtc = now;
            }
            return;
        }

        // EObj IDs whose Warp rows are the eight inn-room exits.
        var door = objectTable.FirstOrDefault(obj => obj is { ObjectKind: ObjectKind.EventObj, IsTargetable: true }
            && obj.BaseId is 2000087 or 2001010 or 2001011 or 2005628 or 2007707 or 2009759 or 2011853 or 2014367);
        if (door == null)
        {
            Fail("No inn-room exit door was found nearby.");
            return;
        }
        if (DistanceToLocalPlayer(door) > RepairNpcInteractRadius)
        {
            SendMoveCommand(door.Position, "inn-room exit", initial: lastMoveCommandUtc == DateTime.MinValue);
            return;
        }
        StopMovementIfNpcRepair();
        if (now - lastInteractUtc >= InteractRetryCooldown)
        {
            lastInteractUtc = now;
            npcRepairDoorInteracted |= GameInteractionHelper.TryInteractWithObject(targetManager, door, log);
        }
    }

    private void UpdateNpcRepairInnReturn()
    {
        if (condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51] || objectTable.LocalPlayer == null)
            return;

        if (GameInteractionHelper.IsInnTerritory(dataManager, (ushort)clientState.TerritoryType))
        {
            if (npcRepairRequiredInnTerritory == 0 || clientState.TerritoryType != npcRepairRequiredInnTerritory)
                Fail($"NPC repair entered room {clientState.TerritoryType}, expected {npcRepairRequiredInnTerritory}.");
            else
                Complete($"NPC repair trip finished; confirmed entry into inn room {npcRepairRequiredInnTerritory}.");
            return;
        }
        if (activeNpcRepairInnRoute is { } route && npcRepairInnPathIndex < route.Path.Length)
        {
            if (clientState.TerritoryType != route.TerritoryTypeId)
            {
                Fail("NPC repair left the selected inn city before returning to its inn.");
                return;
            }
            UpdateNpcRepairInnPath(DateTime.UtcNow, route);
            return;
        }
        if (innEntryService == null)
        {
            Fail("Inn entry service is unavailable for NPC repair.");
            return;
        }
        if (!npcRepairInnEntryStarted)
        {
            if (DateTime.UtcNow - npcRepairTravelStageStartedUtc < UiSettleCooldown)
                return;
            if (!innEntryService.StartManualEntry())
            {
                Fail($"NPC repair could not enter the inn: {innEntryService.StatusMessage}");
                return;
            }
            npcRepairInnEntryStarted = true;
        }
        if (!innEntryService.IsRunning)
        {
            Fail($"NPC repair did not enter the inn room: {innEntryService.StatusMessage}");
            return;
        }
        StatusMessage = $"Returning to the inn room: {innEntryService.StatusMessage}";
    }

    private void UpdateNpcRepairFieldNpcSearch(DateTime now, ResolvedFieldRepairRoute route)
    {
        if (TryFindNearbyRepairNpc(out var targetNpc))
        {
            BeginNpcRepairWithCandidate(targetNpc, $"Reached field aetheryte {route.AetheryteName} and found");
            return;
        }

        if (now - npcRepairTravelStageStartedUtc < UiSettleCooldown)
        {
            StatusMessage = $"Looking for a repair NPC near {route.AetheryteName} in {route.TerritoryName}.";
            return;
        }

        var routeFailure = $"No repair NPC was found within {RepairNpcSearchRadius:0}y near {FormatFieldRepairRoute(route)}.";
        if (TryRetryNpcRepairFieldRoute(route, routeFailure, out var exhaustedMessage))
            return;

        Fail(exhaustedMessage);
    }

    private bool TryRetryNpcRepairFieldRoute(
        ResolvedFieldRepairRoute failedRoute,
        string failureReason,
        out string exhaustedMessage)
    {
        exhaustedMessage = string.Empty;
        RecordNpcRepairFieldRouteFailure(failedRoute, failureReason);
        if (npcSaleRequest)
        {
            StopOwnedNpcRepairTravel();
            if (Plugin.PluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy").InvokeFunc())
            {
                exhaustedMessage = "Lifestream remained busy after ADS sale travel ended; no further travel was sent.";
                return false;
            }
        }

        while (TryResolveFieldRepairRoute(out var nextRoute))
        {
            npcRepairInnPathIndex = 0;
            ResetRepairSubmission();

            if (!TrySendNpcRepairFieldTeleport(nextRoute))
            {
                RecordNpcRepairFieldRouteFailure(
                    nextRoute,
                    $"ADS could not send the Lifestream field-aetheryte teleport command to {FormatFieldRepairRoute(nextRoute)}.");
                continue;
            }

            activeNpcRepairFieldRoute = nextRoute;
            log.Information(
                $"[ADS][Utility] Field repair route failed: {failureReason} Trying next route {FormatFieldRepairRoute(nextRoute)}.");
            SetNpcRepairTravelStage(
                NpcRepairTravelStage.TeleportingToFieldAetheryte,
                $"Field repair route failed; trying {nextRoute.AetheryteName} in {nextRoute.TerritoryName}.");
            return true;
        }

        activeNpcRepairFieldRoute = null;
        exhaustedMessage = BuildNpcRepairFieldRoutesExhaustedMessage();
        log.Warning($"[ADS][Utility] {exhaustedMessage}");
        return false;
    }

    private void RecordNpcRepairFieldRouteFailure(ResolvedFieldRepairRoute failedRoute, string failureReason)
    {
        failedNpcRepairFieldAetheryteIds.Add(failedRoute.AetheryteId);
        npcRepairFieldRouteFailureCount++;
        lastNpcRepairFieldRouteFailure = $"{FormatFieldRepairRoute(failedRoute)}: {failureReason}";
        log.Warning($"[ADS][Utility] Field repair route failed ({npcRepairFieldRouteFailureCount}): {lastNpcRepairFieldRouteFailure}");
    }

    private string BuildNpcRepairFieldRouteTimeoutReason(DateTime now, ResolvedFieldRepairRoute route)
    {
        var elapsedSeconds = (now - npcRepairTravelStageStartedUtc).TotalSeconds;
        if (npcRepairTravelStage == NpcRepairTravelStage.TeleportingToFieldAetheryte)
        {
            if (!npcRepairFieldRouteSawLoading && clientState.TerritoryType == npcRepairFieldRouteStartTerritoryId)
            {
                return
                    $"Lifestream route to {FormatFieldRepairRoute(route)} produced no loading and no territory change " +
                    $"after {elapsedSeconds:0}s; current territory {clientState.TerritoryType}.";
            }

            if (!npcRepairFieldRouteSawLoading)
            {
                return
                    $"Lifestream route to {FormatFieldRepairRoute(route)} produced no loading before timeout " +
                    $"after {elapsedSeconds:0}s; current territory {clientState.TerritoryType}, expected {route.TerritoryTypeId}.";
            }

            return
                $"Timed out after {elapsedSeconds:0}s while travelling to {FormatFieldRepairRoute(route)}; " +
                $"current territory {clientState.TerritoryType}, expected {route.TerritoryTypeId}.";
        }

        return $"Timed out after {elapsedSeconds:0}s while looking for a repair NPC near {FormatFieldRepairRoute(route)}.";
    }

    private string BuildNpcRepairFieldRoutesExhaustedMessage()
    {
        var lastFailure = string.IsNullOrWhiteSpace(lastNpcRepairFieldRouteFailure)
            ? "No route failure detail was captured."
            : lastNpcRepairFieldRouteFailure;
        return
            $"All eligible field repair routes were exhausted after {npcRepairFieldRouteFailureCount} failed route(s). " +
            $"Last failure: {lastFailure}";
    }

    private void LogNpcRepairFieldRouteWait(DateTime now, ResolvedFieldRepairRoute route, string message)
    {
        if (now - lastNpcRepairFieldRouteWaitLogUtc < NpcRepairFieldRouteLogCooldown)
            return;

        lastNpcRepairFieldRouteWaitLogUtc = now;
        var elapsedSeconds = npcRepairTravelCommandUtc == DateTime.MinValue
            ? 0
            : (now - npcRepairTravelCommandUtc).TotalSeconds;
        log.Information($"[ADS][Utility] {message} route={FormatFieldRepairRoute(route)}, elapsed={elapsedSeconds:0.0}s.");
    }

    private static string FormatFieldRepairRoute(ResolvedFieldRepairRoute route)
        => $"{route.AetheryteName} (ID {route.AetheryteId}) in {route.TerritoryName} " +
           $"(territory {route.TerritoryTypeId}, {route.GilCost} gil)";

    private void SetNpcRepairTravelStage(NpcRepairTravelStage nextStage, string statusMessage)
    {
        npcRepairTravelStage = nextStage;
        npcRepairTravelStageStartedUtc = DateTime.UtcNow;
        StatusMessage = statusMessage;
    }

    private void ClearNpcRepairInnTravel()
    {
        npcRepairTravelStage = NpcRepairTravelStage.None;
        npcRepairTravelStageStartedUtc = DateTime.MinValue;
        npcRepairTravelCommandUtc = DateTime.MinValue;
        npcRepairInnArrivalReadyUtc = DateTime.MinValue;
        activeNpcRepairInnRoute = null;
        activeNpcRepairFieldRoute = null;
        failedNpcRepairFieldAetheryteIds.Clear();
        npcRepairFieldRouteStartTerritoryId = 0;
        npcRepairFieldRouteSawLoading = false;
        lastNpcRepairFieldRouteFailure = string.Empty;
        npcRepairFieldRouteFailureCount = 0;
        lastNpcRepairFieldRouteScanLogUtc = DateTime.MinValue;
        lastNpcRepairFieldRouteWaitLogUtc = DateTime.MinValue;
        npcRepairInnPathIndex = 0;
    }

    private Lumina.Excel.ExcelSheet<Aetheryte>? GetAetheryteSheet()
        => aetheryteSheet ??= dataManager.GetExcelSheet<Aetheryte>();

    private Lumina.Excel.ExcelSheet<ENpcBase>? GetENpcBaseSheet()
        => enpcBaseSheet ??= dataManager.GetExcelSheet<ENpcBase>();

    private bool IsLifestreamLoaded()
    {
        var now = DateTime.UtcNow;
        if (now < lifestreamCacheExpiresUtc)
            return cachedLifestreamLoaded;

        try
        {
            cachedLifestreamLoaded = Plugin.PluginInterface.InstalledPlugins.Any(plugin =>
                plugin.IsLoaded
                && (string.Equals(plugin.InternalName, "Lifestream", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(plugin.Name, "Lifestream", StringComparison.OrdinalIgnoreCase)
                    || plugin.Name.Contains("Lifestream", StringComparison.OrdinalIgnoreCase)));
        }
        catch
        {
            cachedLifestreamLoaded = false;
        }

        lifestreamCacheExpiresUtc = now.AddSeconds(2);
        return cachedLifestreamLoaded;
    }

    private unsafe bool TryFindNearbyRepairNpc(out RepairNpcCandidate candidate, float searchRadius = RepairNpcSearchRadius)
    {
        candidate = default;
        var player = objectTable.LocalPlayer;
        if (player == null)
            return false;

        RepairNpcCandidate? nearestCandidate = null;
        foreach (var obj in objectTable)
        {
            if (obj == null
                || obj.ObjectKind != ObjectKind.EventNpc
                || !obj.IsTargetable)
            {
                continue;
            }

            var distance = Vector3.Distance(player.Position, obj.Position);
            if (distance > searchRadius)
                continue;

            var repairIndex = -1;
            if (npcSaleRequest)
            {
                if (!HasGilShop(obj.BaseId)) continue;
            }
            else if (!TryGetRepairIndex(obj.BaseId, out repairIndex))
                continue;

            var nextCandidate = new RepairNpcCandidate(obj, repairIndex, distance);
            if (nearestCandidate is null || nextCandidate.Distance < nearestCandidate.Value.Distance)
                nearestCandidate = nextCandidate;
        }

        if (nearestCandidate is not null)
        {
            candidate = nearestCandidate.Value;
            return true;
        }

        return false;
    }

    private unsafe IGameObject? FindTrackedRepairNpc()
    {
        IGameObject? nearest = null;
        var nearestDistance = float.MaxValue;
        foreach (var obj in objectTable)
        {
            if (obj == null || obj.ObjectKind != ObjectKind.EventNpc)
                continue;

            if (targetNpcGameObjectId != 0 && obj.GameObjectId == targetNpcGameObjectId)
                return obj;

            if (obj.BaseId != targetNpcBaseId)
                continue;

            if (!string.Equals(obj.Name.TextValue, targetNpcName, StringComparison.Ordinal))
                continue;

            var distance = DistanceToLocalPlayer(obj);
            if (distance < nearestDistance)
            {
                nearest = obj;
                nearestDistance = distance;
            }
        }

        return nearest;
    }

    private bool TryGetRepairIndex(uint baseId, out int repairIndex)
    {
        if (repairIndexCache.TryGetValue(baseId, out var cachedRepairIndex))
        {
            repairIndex = cachedRepairIndex ?? -1;
            return cachedRepairIndex.HasValue;
        }

        repairIndex = -1;
        var sheet = GetENpcBaseSheet();
        if (sheet == null || !sheet.TryGetRow(baseId, out var enpcBase))
        {
            repairIndexCache[baseId] = null;
            return false;
        }

        var index = 0;
        foreach (var eventData in enpcBase.ENpcData)
        {
            if (eventData.RowId == RepairShopEventId)
            {
                repairIndex = index;
                repairIndexCache[baseId] = repairIndex;
                return true;
            }

            index++;
        }

        repairIndexCache[baseId] = null;
        return false;
    }

    private bool HasGilShop(uint baseId)
    {
        var sheet = GetENpcBaseSheet();
        if (sheet == null || !sheet.TryGetRow(baseId, out var npc)) return false;
        foreach (var entry in npc.ENpcData)
        {
            // Match the existing AutoRetainer sale backend's supported vendor handlers.
            if (entry.Is<GilShop>()) return true;
            if (entry.Is<PreHandler>()
                && dataManager.GetExcelSheet<PreHandler>() is { } preHandlers
                && preHandlers.TryGetRow(entry.RowId, out var preHandler)
                && preHandler.Target.Is<GilShop>()) return true;
            if (entry.Is<TopicSelect>()
                && dataManager.GetExcelSheet<TopicSelect>() is { } topics
                && topics.TryGetRow(entry.RowId, out var topic)
                && topic.Shop.Any(shop => shop.Is<GilShop>())) return true;
        }
        return false;
    }

    private bool HasUnownedNpcSaleUi()
        => shopRuntime.IsAnyShopVisible || shopRuntime.IsSelectionMenuVisible || shopRuntime.HasUnexpectedConfirmation
            || shopRuntime.IsTalkVisible || GameInteractionHelper.IsAddonVisible("Repair");

    private void FinishNpcSale(bool succeeded, string message)
    {
        if (!npcSaleRequest || !npcSaleStatus.Running) return;
        // Global AR busy/idle and a selected NPC do not establish ownership of its queue or UI.
        npcSaleStatus = npcSaleStatus with { Running = false, Done = true, Succeeded = succeeded,
            StatusMessage = message, CommandSent = npcSaleCommandSent, Settled = !npcSaleCommandSent };
    }

    private string FinishGearSale(bool succeeded, string message)
    {
        if (!gearSaleStatus.Running) return message;
        if (!succeeded)
            message = $"Equipment sale: {message} Sold {gearSaleStatus.SoldCount}/{gearSaleStatus.RequestedCount}; {gearSaleStatus.RequestedCount - gearSaleStatus.SoldCount} remaining.";
        gearSaleStatus = gearSaleStatus with { Running = false, Done = true, Succeeded = succeeded && gearSaleStatus.SkippedCount == 0,
            StatusMessage = message, Settled = false };
        return message;
    }

    private void TryInteractWithRepairNpc(IGameObject npc)
    {
        if (HasUnownedNpcSaleUi())
        {
            Fail("NPC UI was already open before ADS could interact for repair; ADS left it untouched.");
            return;
        }

        npcRepairUiCharacter = 0;
        lastInteractUtc = DateTime.UtcNow;
        if (GameInteractionHelper.TryInteractWithObject(targetManager, npc, log))
        {
            npcRepairUiCharacter = Plugin.PlayerState.ContentId;
            targetNpcGameObjectId = npc.GameObjectId;
            log.Information($"[ADS][Utility] Interacting with repair NPC {targetNpcName}.");
        }
    }

    private bool OwnsNpcRepairInteraction()
        => npcRepairUiCharacter != 0 && clientState.IsLoggedIn
            && npcRepairUiCharacter == Plugin.PlayerState.ContentId
            && !condition[ConditionFlag.BetweenAreas] && !condition[ConditionFlag.BetweenAreas51];

    private bool OwnsNpcRepairNpc(ulong npcId, uint npcBaseId)
        => OwnsNpcRepairInteraction() && targetNpcGameObjectId != 0
            && targetNpcGameObjectId == npcId && targetNpcBaseId == npcBaseId;

    private bool OwnsNpcRepairWindow()
    {
        if (!OwnsNpcRepairInteraction())
            return false;
        var repairAddon = GetVisibleAddon<AtkUnitBase>("Repair");
        var agent = AgentRepair.Instance();
        if (repairAddon == null || agent == null || agent->AddonId != repairAddon->Id
            || agent->IsSelfRepairOpen || agent->UseSelfRepair || agent->NpcRepairCallback == null)
            return false;
        var confirmation = GetVisibleAddon<AtkUnitBase>("SelectYesno");
        if (confirmation != null && agent->DialogAddonId != confirmation->Id)
            return false;
        var handler = agent->NpcRepairCallback->EventHandler;
        if (handler == null || handler->Info.EventId.Id != RepairShopEventId)
            return false;
        var npc = handler == null ? null : handler->EventGameObject;
        return npc != null && OwnsNpcRepairNpc((ulong)npc->GetGameObjectId(), npc->BaseId);
    }

    private void SendMoveCommand(Vector3 destination, string label, bool initial)
    {
        if (objectTable.LocalPlayer is not { } player)
            return;
        var now = DateTime.UtcNow;
        try
        {
            if (!Plugin.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady").InvokeFunc())
            {
                lastMovementProgressUtc = DateTime.MinValue;
                StatusMessage = $"Waiting for the navigation mesh before moving toward {label}.";
                return;
            }
        }
        catch (Exception ex)
        {
            if (npcSaleRequest)
            { Fail($"NPC sale navigation is unavailable: {ex.Message}"); return; }
            // Preserve command-based repair movement when readiness IPC is unavailable.
        }

        if (npcSaleRequest && !npcSaleOwnsMovement)
        {
            try
            {
                if (Plugin.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.SimpleMove.PathfindInProgress").InvokeFunc()
                    || Plugin.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning").InvokeFunc())
                { Fail("Navigation already owns work; ADS did not start the NPC sale approach."); return; }
            }
            catch (Exception ex)
            { Fail($"NPC sale navigation is unavailable: {ex.Message}"); return; }
        }

        if (!initial)
        {
            if (InnEntryService.IsMovementStalled(player.Position, now, ref lastMovementProgressPosition, ref lastMovementProgressUtc))
            {
                StopMovementIfNpcRepair();
                log.Warning($"[ADS][Utility] No movement for 3 seconds toward {label}; cancelled navigation and will retry from the current position.");
                return;
            }
            if (now - lastMoveCommandUtc < MoveRetryCooldown)
                return;
            if (!npcSaleRequest)
                lastMoveCommandUtc = now;
            try
            {
                if (Plugin.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.SimpleMove.PathfindInProgress").InvokeFunc()
                    || Plugin.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning").InvokeFunc())
                    return;
            }
            catch (Exception ex)
            {
                if (npcSaleRequest)
                { Fail($"NPC sale navigation is unavailable: {ex.Message}"); return; }
                // Keep the existing movement retry when navigation IPC is unavailable.
            }
        }

        var command = string.Format(
            CultureInfo.InvariantCulture,
            "/vnav moveto {0:F2} {1:F2} {2:F2}",
            destination.X,
            destination.Y,
            destination.Z);
        if (!GameInteractionHelper.TrySendChatCommand(commandManager, command, log))
        {
            Fail($"Movement toward {label} was rejected.");
            return;
        }
        if (npcSaleRequest)
            npcSaleOwnsMovement = true;
        lastMoveCommandUtc = now;
        lastMovementProgressUtc = now;
        lastMovementProgressPosition = player.Position;
        log.Information($"[ADS][Utility] {(initial ? "Starting" : "Refreshing")} movement toward {label}.");
    }

    private void StopMovementIfNpcRepair()
    {
        if (activeTask != UtilityTask.NpcRepair)
            return;

        if (npcSaleRequest)
        {
            if (!npcSaleOwnsMovement) return;
            npcSaleOwnsMovement = false;
            if (!clientState.IsLoggedIn || npcSaleCharacter == 0 || npcSaleCharacter != Plugin.PlayerState.ContentId
                || isDutyOwned() || isInnEntryRunning())
            {
                lastMoveCommandUtc = DateTime.MinValue;
                lastMovementProgressUtc = DateTime.MinValue;
                return;
            }
            lastActionUtc = DateTime.UtcNow;
        }
        InnEntryService.StopNavigation(commandManager, log);
        lastMoveCommandUtc = DateTime.MinValue;
        lastMovementProgressUtc = DateTime.MinValue;
    }

    private float DistanceToLocalPlayer(IGameObject obj)
    {
        var player = objectTable.LocalPlayer;
        return player == null ? float.MaxValue : Vector3.Distance(player.Position, obj.Position);
    }

    private void SyncShopPurchaseRunner()
    {
        var purchase = shopPurchaseRunner.Status;
        StatusMessage = purchase.StatusMessage;
        if (purchase.Running || activeTask != UtilityTask.ShopPurchase)
            return;

        LastSuccessMessage = purchase.Succeeded == true ? purchase.SuccessMessage : string.Empty;
        LastFailureMessage = purchase.Succeeded == true ? string.Empty : purchase.FailureMessage;
        LastCompletionUtc = purchase.CompletedAtUtc ?? DateTime.UtcNow;
        log.Information(
            "[ADS][Shop] Purchase finished succeeded={Succeeded}, acquired={Acquired}/{Requested}, failureCode={FailureCode}, status={Status}.",
            purchase.Succeeded ?? false,
            purchase.AcquiredQuantity,
            purchase.RequestedQuantity,
            purchase.FailureCode ?? string.Empty,
            purchase.StatusMessage);
        ResetState();
        StatusMessage = purchase.StatusMessage;
    }

    private void UpdateShopListBatch()
    {
        if (activeTask != UtilityTask.ShopListBatch || shopListBatchDefinition == null)
            return;
        if (shopListCleanup != null)
        {
            ContinueShopListCleanup();
            return;
        }

        if (shopPurchaseRunner.IsRunning)
        {
            shopPurchaseRunner.Update();
            var running = shopPurchaseRunner.Status;
            if (shopListBatchDefinition.Mode == ShopListMode.FillOrderOverMultipleRuns)
                running = running with { AcquiredQuantity = shopPurchaseRunner.VerifiedAcquiredQuantity };
            StatusMessage = running.StatusMessage;
            shopListBatchStatus = shopListBatchStatus with
            {
                StatusMessage = running.StatusMessage,
                CurrentItemId = running.ItemId,
                CurrentItemName = running.ItemName,
                Rows = shopListBatchRows.ToArray(),
            };
            if (shopListBatchChildAccepted && shopListBatchIndex < shopListBatchRows.Count)
            {
                var row = shopListBatchRows[shopListBatchIndex];
                shopListBatchRows[shopListBatchIndex] = row with
                {
                    PurchasedQuantity = checked(shopListBatchChildPurchasedBaseline + running.AcquiredQuantity),
                    Message = running.StatusMessage,
                };
                SyncShopListBatchStatus();
            }
            if (running.Running)
                return;
        }

        if (shopListBatchChildAccepted)
        {
            var child = shopPurchaseRunner.Status;
            if (shopListBatchDefinition.Mode == ShopListMode.FillOrderOverMultipleRuns)
                child = child with { AcquiredQuantity = shopPurchaseRunner.VerifiedAcquiredQuantity };
            shopListBatchChildAccepted = false;
            if (!string.IsNullOrEmpty(shopListBatchTerminalFailureMessage))
            {
                if (child.Succeeded == true && child.SelectedOffer != null)
                    RecordSuccessfulShopListChild(child);
                else
                    MarkCurrentBatchRow("failed", shopListBatchTerminalFailureMessage, child.AcquiredQuantity);
                FinishShopListBatch(false, shopListBatchTerminalFailureMessage, child.FailureCode ?? "batch-failed");
                return;
            }
            if (shopListBatchCancelRequested)
            {
                var cleanupDetail = child.FailureCode == ShopPurchaseFailureCodes.Cancelled
                    ? string.Empty
                    : $" Cleanup result: {child.FailureMessage}";
                if (child.Succeeded == true && child.SelectedOffer != null)
                    RecordSuccessfulShopListChild(child);
                else
                    MarkCurrentBatchRow("cancelled", child.FailureMessage, child.AcquiredQuantity);
                FinishShopListBatch(
                    false,
                    $"Shop-list batch cancelled after {shopListBatchCompletedRows} completed row(s); current row acquired {child.AcquiredQuantity} of {child.RequestedQuantity}.{cleanupDetail}",
                    ShopPurchaseFailureCodes.Cancelled);
                return;
            }
            if (child.Succeeded != true)
            {
                if (shopListBatchDefinition.Mode == ShopListMode.FillOrderOverMultipleRuns &&
                    child.FailureCode is ShopPurchaseFailureCodes.InsufficientCurrency or ShopPurchaseFailureCodes.InventoryCapacity)
                {
                    MarkCurrentBatchRow("pending", child.FailureMessage, child.AcquiredQuantity);
                    shopListBatchIndex++;
                    UpdateShopListBatch();
                    return;
                }
                MarkCurrentBatchRow("failed", child.FailureMessage, child.AcquiredQuantity);
                FinishShopListBatch(
                    false,
                    $"Shop-list batch stopped on {child.ItemName}: {child.FailureMessage} Acquired {child.AcquiredQuantity} of {child.RequestedQuantity} before stopping.",
                    child.FailureCode ?? "batch-failed");
                return;
            }
            if (child.SelectedOffer == null)
            {
                MarkCurrentBatchRow("failed", "Purchase completed without a selected shop identity.", child.AcquiredQuantity);
                FinishShopListBatch(false, "Shop-list batch child completed without a selected shop identity.", ShopPurchaseFailureCodes.UiMismatch);
                return;
            }

            RecordSuccessfulShopListChild(child);
        }

        if (!string.IsNullOrEmpty(shopListBatchTerminalFailureMessage))
        {
            FinishShopListBatch(false, shopListBatchTerminalFailureMessage, "batch-failed");
            return;
        }
        if (shopListBatchCancelRequested)
        {
            FinishShopListBatch(
                false,
                $"Shop-list batch cancelled after {shopListBatchCompletedRows} completed row(s).",
                ShopPurchaseFailureCodes.Cancelled);
            return;
        }

        while (shopListBatchIndex < shopListBatchItems.Count)
        {
            var item = shopListBatchItems[shopListBatchIndex];
            var row = shopListBatchRows[shopListBatchIndex];
            if (row.Outcome == "association-completed"
                || (shopListBatchDefinition.Mode == ShopListMode.SpendUntilCurrencyOrCapacity
                    && !item.Repeatable
                    && row.Outcome == "purchased"))
            {
                shopListBatchIndex++;
                continue;
            }

            var owned = GetShopListOwnedQuantity(item);
            if (shopListBatchDefinition.Mode == ShopListMode.FillOrderOverMultipleRuns)
            {
                if (GetAvailableShopCurrency(shopListBatchDefinition.Currency) < 0)
                {
                    FinishShopListBatch(false, "The exact selected currency balance became unavailable.", ShopPurchaseFailureCodes.InvalidRequest);
                    return;
                }
                var credited = shopListBatchStatus.CreditedQuantities?.GetValueOrDefault(item.RowId) ?? 0;
                if (credited >= item.RefillToAtLeast)
                {
                    shopListBatchCompletedNonRepeatableRowIds.Add(item.RowId);
                    shopListBatchRows[shopListBatchIndex] = row with { Outcome = "already-satisfied", Message = "Order target credited." };
                    shopListBatchCompletedRows++;
                    shopListBatchIndex++;
                    SyncShopListBatchStatus();
                    continue;
                }
            }
            if (shopListBatchDefinition.Mode == ShopListMode.TargetedRefill && owned >= item.TriggerBelow)
            {
                shopListBatchRows[shopListBatchIndex] = row with
                {
                    OwnedQuantity = owned,
                    Outcome = "already-satisfied",
                    Message = $"Owned quantity {owned} is not below trigger {item.TriggerBelow}.",
                };
                if (!item.Repeatable)
                    shopListBatchCompletedNonRepeatableRowIds.Add(item.RowId);
                shopListBatchCompletedRows++;
                shopListBatchIndex++;
                SyncShopListBatchStatus();
                continue;
            }

            ShopPurchasePreviewResult purchasePreview;
            if (shopListBatchDefinition.Mode == ShopListMode.TargetedRefill)
            {
                var minimum = item.RefillToAtLeast - owned;
                if (minimum <= 0 || minimum > ShopPurchaseRequest.MaximumQuantity)
                {
                    var message = $"{item.ItemName} requires invalid additional quantity {minimum}.";
                    MarkCurrentBatchRow("failed", message);
                    FinishShopListBatch(false, message, ShopPurchaseFailureCodes.InvalidRequest);
                    return;
                }
                purchasePreview = PreviewShopPurchaseAtLeast(item.ItemId, (int)minimum, shopListBatchDefinition.Currency);
            }
            else
            {
                var remaining = shopListBatchDefinition.Mode == ShopListMode.FillOrderOverMultipleRuns
                    ? checked((int)(item.RefillToAtLeast - (shopListBatchStatus.CreditedQuantities?.GetValueOrDefault(item.RowId) ?? 0)))
                    : ShopPurchaseRequest.MaximumQuantity;
                purchasePreview = shopListBatchDefinition.Mode == ShopListMode.FillOrderOverMultipleRuns
                    ? PreviewOrderShopPurchase(item.ItemId, remaining, shopListBatchDefinition.Currency)
                    : PreviewMaximumShopPurchase(item.ItemId, shopListBatchDefinition.Currency);
            }

            if (!purchasePreview.CanPurchase || purchasePreview.SelectedOffer == null)
            {
                if (shopListBatchDefinition.Mode is ShopListMode.SpendUntilCurrencyOrCapacity or ShopListMode.FillOrderOverMultipleRuns
                    && purchasePreview.FailureCode is ShopPurchaseFailureCodes.InsufficientCurrency
                        or ShopPurchaseFailureCodes.InventoryCapacity)
                {
                    shopListBatchRows[shopListBatchIndex] = row with
                    {
                        OwnedQuantity = owned,
                        Outcome = shopListBatchDefinition.Mode == ShopListMode.FillOrderOverMultipleRuns
                            ? "pending" : row.PurchasedQuantity > 0 ? "purchased" : "skipped",
                        Message = row.PurchasedQuantity > 0
                            ? $"{purchasePreview.Message} Preserved {row.PurchasedQuantity} previously verified purchased item(s)."
                            : purchasePreview.Message,
                    };
                    shopListBatchIndex++;
                    SyncShopListBatchStatus();
                    continue;
                }

                shopPurchaseRunner.ReleaseHeldShopUi();
                shopListHeldShop = null;
                MarkCurrentBatchRow("failed", purchasePreview.Message);
                FinishShopListBatch(
                    false,
                    $"{item.ItemName} cannot be purchased with the selected currency: {purchasePreview.Message}",
                    purchasePreview.FailureCode ?? ShopPurchaseFailureCodes.UnsupportedOffer);
                return;
            }

            var nextShop = ShopOfferIdentity.From(purchasePreview.SelectedOffer);
            if (shopListHeldShop.HasValue && shopListHeldShop.Value != nextShop)
            {
                shopPurchaseRunner.ReleaseHeldShopUi();
                shopListHeldShop = null;
            }

            var request = purchasePreview.Request;
            if (!shopPurchaseRunner.Start(request, holdShopOpenOnSuccess: true, shopListBatchDefinition.Currency))
            {
                shopPurchaseRunner.ReleaseHeldShopUi();
                shopListHeldShop = null;
                MarkCurrentBatchRow("failed", shopPurchaseRunner.Status.LastStartError);
                FinishShopListBatch(
                    false,
                    $"{item.ItemName} could not start: {shopPurchaseRunner.Status.LastStartError}",
                    shopPurchaseRunner.LastStartFailureCode ?? ShopPurchaseFailureCodes.Busy);
                return;
            }

            shopListBatchChildPurchasedBaseline = row.PurchasedQuantity;
            shopListBatchRows[shopListBatchIndex] = row with
            {
                OwnedQuantity = owned,
                RequestedQuantity = checked(row.RequestedQuantity + request.Quantity),
                Outcome = "running",
                Message = shopPurchaseRunner.Status.StatusMessage,
            };
            shopListBatchChildAccepted = true;
            StatusMessage = shopPurchaseRunner.Status.StatusMessage;
            SyncShopListBatchStatus(item.ItemId, item.ItemName, StatusMessage);
            return;
        }

        if (shopListBatchDefinition.Mode == ShopListMode.SpendUntilCurrencyOrCapacity)
        {
            if (shopListSpendCyclePurchased)
            {
                shopListSpendCyclePurchased = false;
                shopListBatchIndex = 0;
                SyncShopListBatchStatus();
                return;
            }
            if (shopListBatchVerifiedPurchaseRows > 0)
            {
                FinishShopListBatch(true, "Spend preset reached the selected currency or inventory-capacity limit after verified purchases.");
                return;
            }
            FinishShopListBatch(true, "Spend preset is already at the selected currency or inventory-capacity limit; no purchase was needed.");
            return;
        }

        FinishShopListBatch(true, shopListBatchDefinition.Mode == ShopListMode.FillOrderOverMultipleRuns
            ? $"Order pass completed; {shopListBatchCompletedRows}/{shopListBatchItems.Count} targets credited. Outstanding quantities remain pending."
            : $"Targeted refill preset completed {shopListBatchCompletedRows} row(s) without overbuying.");
    }

    private void FinishShopListBatch(bool succeeded, string message, string? failureCode = null)
    {
        if (shopListBatchDefinition?.Mode == ShopListMode.FillOrderOverMultipleRuns || shopListHeldShop.HasValue)
        {
            shopPurchaseRunner.ReleaseHeldShopUi();
            shopListCleanup = (succeeded, message, failureCode);
            shopListCleanupStartedAtUtc = DateTime.UtcNow;
            ContinueShopListCleanup();
            return;
        }
        CompleteShopListBatch(succeeded, message, failureCode);
    }

    private void ContinueShopListCleanup()
    {
        if (shopListCleanup is not { } terminal)
            return;
        try
        {
            shopRuntime.ContinueShopListCleanup();
            var blocker = shopRuntime.ShopListCleanupBlocker;
            if (blocker != null)
            {
                if (DateTime.UtcNow - shopListCleanupStartedAtUtc < ShopPurchaseTiming.ConfirmationAndVerificationTimeout)
                {
                    SyncShopListBatchStatus(message: $"Finishing order cleanup: {blocker}.");
                    return;
                }
                terminal = (false, $"Order cleanup failed: {blocker}.", ShopPurchaseFailureCodes.UiMismatch);
            }
            else if (shopListBatchCancelRequested)
                terminal = (false, "Order cancelled; verified progress was retained.", ShopPurchaseFailureCodes.Cancelled);
        }
        catch (Exception ex)
        {
            terminal = (false, $"Order cleanup failed: {ex.Message}", ShopPurchaseFailureCodes.UiMismatch);
        }
        shopListCleanup = null;
        CompleteShopListBatch(terminal.Succeeded, terminal.Message, terminal.FailureCode);
    }

    private void CompleteShopListBatch(bool succeeded, string message, string? failureCode)
    {
        shopPurchaseRunner.ReleaseHeldShopUi();
        shopListHeldShop = null;
        var completedAt = DateTime.UtcNow;
        var finiteOrder = shopListBatchDefinition?.Mode == ShopListMode.FillOrderOverMultipleRuns;
        if (succeeded && !finiteOrder)
            shopListBatchCompletedRows = shopListBatchRows.Count;
        shopListBatchStatus = shopListBatchStatus with
        {
            Running = false,
            Done = true,
            Succeeded = succeeded,
            Disposition = succeeded
                ? finiteOrder && shopListBatchCompletedRows < shopListBatchItems.Count ? "partial" : finiteOrder ? "fulfilled" : "succeeded"
                : failureCode == ShopPurchaseFailureCodes.Cancelled
                    ? "cancelled"
                    : "failed",
            CompletedRows = shopListBatchCompletedRows,
            CompletedNonRepeatableRowIds = CurrentCompletedNonRepeatableRowIds(),
            SkippedRowIds = CurrentSkippedRowIds(),
            FailureCode = succeeded ? null : failureCode,
            StatusMessage = message,
            FailureMessage = succeeded ? string.Empty : message,
            Rows = shopListBatchRows.ToArray(),
            CompletedAtUtc = completedAt,
        };
        LastSuccessMessage = succeeded ? message : string.Empty;
        LastFailureMessage = succeeded ? string.Empty : message;
        LastCompletionUtc = completedAt;
        StatusMessage = message;
        if (succeeded)
            log.Information("[ADS][ShopLists] {Message}", message);
        else
            log.Warning("[ADS][ShopLists] {Message}", message);
        ResetState();
        StatusMessage = message;
    }

    private long GetShopListOwnedQuantity(ShopListBatchItem item)
    {
        var inventory = Math.Max(0L, shopRuntime.GetItemCount(item.ItemId));
        return inventory + (item.OwnershipScope == ShopListOwnershipScope.InventoryAndRetainers
            ? item.RetainerQuantity
            : 0);
    }

    private void RecordSuccessfulShopListChild(ShopPurchaseStatusSnapshot child)
    {
        if (shopListBatchDefinition == null
            || child.SelectedOffer == null
            || shopListBatchIndex < 0
            || shopListBatchIndex >= shopListBatchItems.Count)
        {
            return;
        }

        var completedItem = shopListBatchItems[shopListBatchIndex];
        var completedRow = shopListBatchRows[shopListBatchIndex];
        shopListBatchRows[shopListBatchIndex] = completedRow with
        {
            PurchasedQuantity = checked(shopListBatchChildPurchasedBaseline + child.AcquiredQuantity),
            Outcome = "purchased",
            Message = $"Verified purchase acquired {child.AcquiredQuantity} item(s).",
        };
        var finiteOrder = shopListBatchDefinition.Mode == ShopListMode.FillOrderOverMultipleRuns;
        var fulfilled = !finiteOrder ||
            (shopListBatchDefinition.CreditedQuantities?.GetValueOrDefault(completedItem.RowId) ?? 0)
                + shopListBatchChildPurchasedBaseline + child.AcquiredQuantity >= completedItem.RefillToAtLeast;
        if (!completedItem.Repeatable && fulfilled)
            shopListBatchCompletedNonRepeatableRowIds.Add(completedItem.RowId);
        if (fulfilled && (shopListBatchDefinition.Mode == ShopListMode.TargetedRefill || !completedItem.Repeatable))
            shopListBatchCompletedRows++;
        shopListBatchVerifiedPurchaseRows++;
        shopListSpendCyclePurchased = true;
        shopListHeldShop = ShopOfferIdentity.From(child.SelectedOffer);
        if (!finiteOrder || fulfilled)
            shopListBatchIndex++;
        SyncShopListBatchStatus();
    }

    private void MarkCurrentBatchRow(string outcome, string message, int acquiredQuantity = 0)
    {
        if (shopListBatchIndex < 0 || shopListBatchIndex >= shopListBatchRows.Count)
            return;
        var row = shopListBatchRows[shopListBatchIndex];
        shopListBatchRows[shopListBatchIndex] = row with
        {
            PurchasedQuantity = Math.Max(
                row.PurchasedQuantity,
                checked(shopListBatchChildPurchasedBaseline + acquiredQuantity)),
            Outcome = outcome,
            Message = message,
        };
        SyncShopListBatchStatus();
    }

    private void SyncShopListBatchStatus(
        uint? currentItemId = null,
        string? currentItemName = null,
        string? message = null)
    {
        if (shopListBatchDefinition?.Mode == ShopListMode.FillOrderOverMultipleRuns)
        {
            var totals = shopListBatchRows.ToDictionary(row => row.RowId, row => checked(
                (shopListBatchDefinition.CreditedQuantities?.GetValueOrDefault(row.RowId) ?? 0) + row.PurchasedQuantity));
            var changed = shopListBatchStatus.CreditedQuantities == null || totals.Any(pair =>
                shopListBatchStatus.CreditedQuantities.GetValueOrDefault(pair.Key, -1) != pair.Value);
            shopListBatchStatus = shopListBatchStatus with { CreditedQuantities = totals };
            shopListBatchCompletedNonRepeatableRowIds.Clear();
            foreach (var row in shopListBatchRows.Where(row => totals[row.RowId] >= row.RefillToAtLeast))
                shopListBatchCompletedNonRepeatableRowIds.Add(row.RowId);
            shopListBatchCompletedRows = shopListBatchCompletedNonRepeatableRowIds.Count;
            if (changed)
                shopListBatchDefinition.SaveProgress?.Invoke(totals);
        }
        shopListBatchStatus = shopListBatchStatus with
        {
            CompletedRows = shopListBatchCompletedRows,
            CurrentItemId = currentItemId ?? shopListBatchStatus.CurrentItemId,
            CurrentItemName = currentItemName ?? shopListBatchStatus.CurrentItemName,
            CompletedNonRepeatableRowIds = CurrentCompletedNonRepeatableRowIds(),
            SkippedRowIds = CurrentSkippedRowIds(),
            StatusMessage = message ?? shopListBatchStatus.StatusMessage,
            Rows = shopListBatchRows.ToArray(),
        };
    }

    private IReadOnlyList<Guid> CurrentCompletedNonRepeatableRowIds()
        => shopListBatchItems.Where(item => shopListBatchCompletedNonRepeatableRowIds.Contains(item.RowId))
            .Select(item => item.RowId)
            .ToArray();

    private IReadOnlyList<Guid> CurrentSkippedRowIds()
        => shopListBatchItems.Where(item => shopListBatchSkippedRowIds.Contains(item.RowId))
            .Select(item => item.RowId)
            .ToArray();

    private static string OwnershipScopeName(ShopListOwnershipScope scope)
        => scope == ShopListOwnershipScope.InventoryOnly
            ? "inventory-only"
            : "inventory-and-retainers";

    private void Complete(string message)
    {
        FinishNpcSale(true, message);
        FinishGearSale(true, message);
        if (activeTask == UtilityTask.NpcRepair && activeNpcRepairMode == NpcRepairMode.YesInn
            && npcRepairTravelStage != NpcRepairTravelStage.ReturningToInn)
        {
            StopMovementIfNpcRepair();
            CloseRepairAddons();
            CloseOwnedNpcRepairMenus();
            SetNpcRepairTravelStage(NpcRepairTravelStage.ReturningToInn, $"{message} Returning to the inn room.");
            return;
        }

        var completedTask = activeTask;
        StopMovementIfNpcRepair();
        log.Information($"[ADS][Utility] {message}");
        LastSuccessMessage = message;
        if (completedTask == UtilityTask.ExtractMateria)
            RecordExtractMateriaCompletion(true, message);
        if (completedTask == UtilityTask.DesynthFromInventory)
        {
            LastDesynthSuccessMessage = message;
            LastDesynthFailureMessage = string.Empty;
        }
        LastCompletionUtc = DateTime.UtcNow;
        ResetState();
        StatusMessage = message;
    }

    private void Fail(string message)
    {
        FinishNpcSale(false, message);
        message = FinishGearSale(false, message);
        var failedTask = activeTask;
        StopMovementIfNpcRepair();
        log.Warning($"[ADS][Utility] {message}");
        LastFailureMessage = message;
        if (failedTask == UtilityTask.ExtractMateria)
            RecordExtractMateriaCompletion(false, message);
        if (failedTask == UtilityTask.DesynthFromInventory)
            LastDesynthFailureMessage = message;
        LastCompletionUtc = DateTime.UtcNow;
        ResetState();
        StatusMessage = message;
    }

    private void RecordExtractMateriaCompletion(bool succeeded, string message)
    {
        extractMateriaDone = true;
        extractMateriaSucceeded = succeeded;
        extractMateriaStatusMessage = message;
        extractMateriaSuccessMessage = succeeded ? message : string.Empty;
        extractMateriaFailureMessage = succeeded ? string.Empty : message;
        extractMateriaCompletedUtc = DateTime.UtcNow;
    }

    private void ResetState()
    {
        if (activeTask == UtilityTask.AutoEquip && autoEquipCharacter != 0 && autoEquipCharacter == Plugin.PlayerState.ContentId)
        {
            var recommended = RecommendEquipModule.Instance();
            if (recommended != null && recommended->ClassJob == autoEquipJob) recommended->Clear();
        }
        autoEquipCharacter = 0;
        autoEquipJob = 0;
        autoEquipGearset = -1;
        autoEquipExpected = null;
        autoEquipSubmittedUtc = default;
        // A submitted sale can leave a native confirmation behind after failure/cancellation.
        // Keep the existing generic-dialog suppression until that character's shop UI closes.
        if (gearCleanupSelling && gearCleanupPending && clientState.IsLoggedIn
            && gearCleanupSelection?.CharacterId == Plugin.PlayerState.ContentId)
            gearCleanupConfirmationCharacter = gearCleanupSelection.CharacterId;
        if (gearCleanupInteracted && gearCleanupVendor != null && !isDutyOwned() && !isInnEntryRunning()
            && clientState.IsLoggedIn && gearCleanupSelection?.CharacterId == Plugin.PlayerState.ContentId)
            shopRuntime.CloseOwnedGearSaleUi(gearCleanupVendor.NpcId, gearCleanupVendor.ShopId);
        gearCleanupSelection = null;
        gearCleanupSelling = false;
        gearCleanupIndex = 0;
        gearCleanupPending = false;
        gearCleanupVendor = null;
        gearCleanupInteracted = false;
        gearCleanupLastMenuHandler = 0;
        gearCleanupGilBefore = -1;
        gearCleanupSettlementReadyUtc = default;
        StopOwnedNpcRepairTravel();
        npcSaleRequest = false;
        npcSaleCommandSent = false;
        npcSaleBusyObserved = false;
        npcSaleOwnsMovement = false;
        npcSaleCharacter = 0;
        if (activeTask == UtilityTask.NpcRepair && activeNpcRepairMode == NpcRepairMode.YesInn)
        {
            CloseRepairAddons();
            CloseOwnedNpcRepairMenus();
        }
        if (npcRepairInnEntryStarted)
            innEntryService?.Cancel("NPC repair ended");
        npcRepairInnEntryStarted = false;
        npcRepairStartingInnTerritory = 0;
        npcRepairRequiredInnTerritory = 0;
        npcRepairDestinationAethernet = 0;
        npcRepairMenderReached = false;
        npcRepairDoorInteracted = false;
        activeTask = UtilityTask.None;
        activeNpcRepairMode = NpcRepairMode.InnFallback;
        startedAtUtc = DateTime.MinValue;
        lastActionUtc = DateTime.MinValue;
        lastMoveCommandUtc = DateTime.MinValue;
        lastMovementProgressUtc = DateTime.MinValue;
        lastMovementProgressPosition = default;
        lastInteractUtc = DateTime.MinValue;
        lastMenuSelectionUtc = DateTime.MinValue;
        repairWindowSeenUtc = DateTime.MinValue;
        targetNpcGameObjectId = 0;
        targetNpcBaseId = 0;
        targetNpcName = string.Empty;
        targetNpcRepairIndex = 0;
        npcRepairUiCharacter = 0;
        ClearNpcRepairInnTravel();
        ResetRepairSubmission();
        materializeCategory = 0;
        materializeCategoryArmed = false;
        materializeAttemptPending = false;
        extractAttemptedAny = false;
        desynthCategoryIndex = 0;
        desynthWindowSeenUtc = DateTime.MinValue;
        desynthCategorySeenUtc = DateTime.MinValue;
        desynthSettledCategoryIndex = -1;
        desynthAttemptedAny = false;
        activeDesynthPolicy = null;
        pendingDesynthItemId = 0;
        maximumDesynthLevel = 0;
        desynthGearsetItemIds = null;
        shopListBatchItems = [];
        shopListBatchDefinition = null;
        shopListCleanup = null;
        shopListBatchRows = [];
        shopListBatchIndex = 0;
        shopListBatchChildPurchasedBaseline = 0;
        shopListBatchChildAccepted = false;
        shopListBatchCancelRequested = false;
        shopListBatchTerminalFailureMessage = string.Empty;
        shopListHeldShop = null;
        if (StatusMessage == "Idle")
            return;

        if (activeTask == UtilityTask.None && string.IsNullOrWhiteSpace(StatusMessage))
            StatusMessage = "Idle";
    }

    private void StopOwnedNpcRepairTravel()
    {
        if (!npcRepairOwnsInnTravel) return;
        npcRepairOwnsInnTravel = false;
        if (npcSaleRequest && (!clientState.IsLoggedIn || npcSaleCharacter == 0 || npcSaleCharacter != Plugin.PlayerState.ContentId
            || isDutyOwned() || isInnEntryRunning()))
            return;
        try
        {
            if (!npcSaleRequest || Plugin.PluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy").InvokeFunc())
                Plugin.PluginInterface.GetIpcSubscriber<object>("Lifestream.Abort").InvokeAction();
        }
        catch (Exception ex) { log.Warning(ex, "[ADS][Utility] Could not cancel owned NPC travel."); }
    }

    private static string GetTaskLabel(UtilityTask task)
        => task switch
        {
            UtilityTask.SelfRepair => "self-repair",
            UtilityTask.NpcRepair => "NPC repair",
            UtilityTask.ExtractMateria => "materia extraction",
            UtilityTask.DesynthFromInventory => "inventory desynthesis",
            UtilityTask.ShopPurchase => "shop purchasing",
            UtilityTask.ShopListBatch => "shop-list purchasing",
            UtilityTask.GearMove => "equipment moving",
            UtilityTask.AutoEquip => "recommended equipment",
            _ => "utility automation",
        };

    private static ShopListBatchStatusSnapshot EmptyShopListBatchStatus()
        => new(
            1,
            string.Empty,
            Guid.Empty,
            false,
            false,
            null,
            "none",
            0,
            0,
            0,
            string.Empty,
            [],
            [],
            null,
            "No shop-list batch has been started.",
            string.Empty,
            [],
            null);

    private static string GetDesynthCategoryLabel(AgentSalvage.SalvageItemCategory category)
        => category switch
        {
            AgentSalvage.SalvageItemCategory.InventoryEquipment => "inventory equipment",
            AgentSalvage.SalvageItemCategory.InventoryHousing => "inventory housing",
            _ => category.ToString(),
        };

    private IReadOnlyList<AgentSalvage.SalvageItemCategory> GetActiveDesynthCategories()
    {
        if (activeDesynthPolicy == null)
            return [];

        return AllDesynthCategories.Where(x => activeDesynthPolicy.Categories.Contains(x.ToString())).ToArray();
    }

    private static string GetDesynthModeName(DesynthRunMode mode)
        => DesynthPolicyService.GetModeName(mode);

    private int FindEligibleDesynthItemIndex(
        AgentSalvage* agent,
        AgentSalvage.SalvageItemCategory category,
        out uint eligibleItemId,
        out int eligibleCount)
    {
        eligibleItemId = 0;
        eligibleCount = 0;
        if (activeDesynthPolicy == null)
            return -1;

        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
            return -1;

        var firstIndex = -1;
        for (var index = 0; index < agent->ItemCount; index++)
        {
            var salvageItem = agent->ItemList[index];
            var inventoryItem = inventoryManager->GetInventorySlot(salvageItem.InventoryType, (int)salvageItem.InventorySlot);
            if (inventoryItem == null || inventoryItem->ItemId == 0)
                continue;

            var itemId = DesynthPolicyService.NormalizeBaseItemId(inventoryItem->ItemId);
            if (!dataManager.GetExcelSheet<Item>().TryGetRow(itemId, out var item) || item.Desynth == 0)
                continue;

            var candidate = new DesynthCandidate(
                itemId,
                category.ToString(),
                item.LevelItem.RowId,
                PlayerState.Instance()->GetDesynthesisLevel(salvageItem.ClassJob),
                maximumDesynthLevel,
                desynthGearsetItemIds?.Contains(itemId) == true);
            if (!activeDesynthPolicy.IsEligible(candidate))
                continue;

            eligibleCount++;
            if (firstIndex >= 0)
                continue;

            firstIndex = index;
            eligibleItemId = itemId;
        }

        return firstIndex;
    }

    private float GetMaximumDesynthLevel()
        => dataManager.GetExcelSheet<Item>()
            .Where(x => x.Desynth > 0)
            .Select(x => (float)x.LevelItem.RowId)
            .DefaultIfEmpty(1)
            .Max();

    private static HashSet<uint> GetGearsetItemIds()
    {
        TryGetGearsetItemIds(out var result);
        return result;
    }

    internal static bool TryGetGearsetItemIds(out HashSet<uint> result)
    {
        result = new HashSet<uint>();
        var module = RaptureGearsetModule.Instance();
        if (module == null)
            return false;

        foreach (var entry in module->Entries)
        {
            foreach (var item in entry.Items)
            {
                if (item.ItemId > 0)
                    result.Add(DesynthPolicyService.NormalizeBaseItemId(item.ItemId));
            }
        }

        return true;
    }

    private static unsafe void ClickButtonIfEnabled(AtkComponentButton* button, AtkUnitBase* addon)
    {
        if (button == null || !button->IsEnabled)
            return;

        var buttonNode = button->AtkComponentBase.OwnerNode;
        var eventData = buttonNode->AtkResNode.AtkEventManager.Event;
        addon->ReceiveEvent(eventData->State.EventType, (int)eventData->Param, eventData);
    }

    private static unsafe T* GetVisibleAddon<T>(string addonName)
        where T : unmanaged
    {
        nint addonPtr = Plugin.GameGui.GetAddonByName(addonName, 1);
        if (addonPtr == nint.Zero)
            return null;

        var addon = (AtkUnitBase*)addonPtr;
        return addon->IsVisible ? (T*)addonPtr : null;
    }

    private readonly record struct InnRepairRouteSeed(uint AethernetId, Vector3[] Path);

    private readonly record struct ResolvedInnRepairRoute(
        uint TerritoryTypeId,
        string TerritoryName,
        uint AethernetId,
        string AethernetName,
        Vector3[] Path,
        int GilCost);

    private readonly record struct ResolvedFieldRepairRoute(
        uint TerritoryTypeId,
        string TerritoryName,
        uint AetheryteId,
        string AetheryteName,
        int GilCost);

    private readonly record struct RepairNpcCandidate(IGameObject GameObject, int RepairIndex, float Distance)
    {
        public ulong GameObjectId
            => GameObject.GameObjectId;

        public uint BaseId
            => GameObject.BaseId;

        public string Name
            => GameObject.Name.TextValue;
    }
}
