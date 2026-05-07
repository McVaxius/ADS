using System.Globalization;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Command;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
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
    }

    private enum NpcRepairMode
    {
        InnFallback,
        NoInn,
    }

    private enum NpcRepairTravelStage
    {
        None,
        TeleportingToInnAethernet,
        TeleportingToFieldAetheryte,
        WalkingInnPath,
        AwaitingRepairNpc,
    }

    private const uint RepairShopEventId = 720915;
    private const float RepairNpcSearchRadius = 80.0f;
    private const float RepairNpcInteractRadius = 3.0f;
    private const float InnPathWaypointReachedRadius = 4.0f;
    private const int SelfRepairGeneralAction = 6;
    private const int DismountGeneralAction = 23;
    private const int MaterializeGeneralAction = 14;
    private const int LastMaterializeCategory = 6;
    private const int FullyRepairedConditionPercent = 100;
    private static readonly AgentSalvage.SalvageItemCategory[] DesynthInventoryCategories =
    [
        AgentSalvage.SalvageItemCategory.InventoryEquipment,
        AgentSalvage.SalvageItemCategory.InventoryHousing,
    ];

    private static readonly TimeSpan OverallTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan NpcRepairTravelTimeout = TimeSpan.FromSeconds(75);
    private static readonly TimeSpan UiRetryCooldown = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan UiSettleCooldown = TimeSpan.FromMilliseconds(1400);
    private static readonly TimeSpan RepairConfirmRetryCooldown = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DesynthReopenCooldown = TimeSpan.FromMilliseconds(1800);
    private static readonly TimeSpan LifestreamTeleportSettleCooldown = TimeSpan.FromSeconds(6);
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
    private readonly Dictionary<uint, int?> repairIndexCache = [];

    private UtilityTask activeTask = UtilityTask.None;
    private NpcRepairMode activeNpcRepairMode = NpcRepairMode.InnFallback;
    private DateTime startedAtUtc = DateTime.MinValue;
    private DateTime lastActionUtc = DateTime.MinValue;
    private DateTime lastMoveCommandUtc = DateTime.MinValue;
    private DateTime lastInteractUtc = DateTime.MinValue;
    private DateTime lastMenuSelectionUtc = DateTime.MinValue;
    private DateTime repairWindowSeenUtc = DateTime.MinValue;
    private ulong targetNpcGameObjectId;
    private uint targetNpcBaseId;
    private string targetNpcName = string.Empty;
    private int targetNpcRepairIndex;
    private bool npcRepairFallbackToFirstOption;
    private NpcRepairTravelStage npcRepairTravelStage = NpcRepairTravelStage.None;
    private DateTime npcRepairTravelStageStartedUtc = DateTime.MinValue;
    private DateTime npcRepairTravelCommandUtc = DateTime.MinValue;
    private ResolvedInnRepairRoute? activeNpcRepairInnRoute;
    private ResolvedFieldRepairRoute? activeNpcRepairFieldRoute;
    private int npcRepairInnPathIndex;
    private bool repairSubmissionSent;
    private DateTime lastRepairConfirmClickUtc = DateTime.MinValue;
    private int repairConfirmClickAttempts;
    private int materializeCategory;
    private bool materializeCategoryArmed;
    private bool materializeAttemptPending;
    private bool extractAttemptedAny;
    private int desynthCategoryIndex;
    private DateTime desynthWindowSeenUtc = DateTime.MinValue;
    private DateTime desynthCategorySeenUtc = DateTime.MinValue;
    private int desynthSettledCategoryIndex = -1;
    private bool desynthAttemptedAny;

    public UtilityAutomationService(
        IDataManager dataManager,
        IObjectTable objectTable,
        ITargetManager targetManager,
        ICommandManager commandManager,
        IClientState clientState,
        ICondition condition,
        IPluginLog log)
    {
        this.dataManager = dataManager;
        this.objectTable = objectTable;
        this.targetManager = targetManager;
        this.commandManager = commandManager;
        this.clientState = clientState;
        this.condition = condition;
        this.log = log;
    }

    public bool IsRunning
        => activeTask != UtilityTask.None;

    public string StatusMessage { get; private set; } = "Idle";

    public string ActiveTaskName
        => activeTask == UtilityTask.None ? string.Empty : GetTaskLabel(activeTask);

    public string ActiveModeName
        => activeTask switch
        {
            UtilityTask.SelfRepair => "self",
            UtilityTask.NpcRepair => activeNpcRepairMode == NpcRepairMode.NoInn ? "npc-no-inn" : "npc",
            UtilityTask.ExtractMateria => "extract-materia",
            UtilityTask.DesynthFromInventory => "desynth-inventory",
            _ => string.Empty,
        };

    public string LastSuccessMessage { get; private set; } = string.Empty;
    public string LastFailureMessage { get; private set; } = string.Empty;
    public DateTime LastCompletionUtc { get; private set; } = DateTime.MinValue;

    public bool StartSelfRepair()
    {
        if (!TryStartTask(UtilityTask.SelfRepair, "Starting self-repair."))
            return false;

        log.Information("[ADS][Utility] Starting self-repair flow.");
        return true;
    }

    public bool StartNpcRepair()
        => StartNpcRepair(NpcRepairMode.InnFallback);

    public bool StartNpcRepairNoInn()
        => StartNpcRepair(NpcRepairMode.NoInn);

    private bool StartNpcRepair(NpcRepairMode mode)
    {
        var statusMessage = mode == NpcRepairMode.NoInn
            ? "Starting NPC repair without inn fallback."
            : "Starting NPC repair.";
        if (!TryStartTask(UtilityTask.NpcRepair, statusMessage))
            return false;

        activeNpcRepairMode = mode;
        if (TryFindNearbyRepairNpc(out var targetNpc))
        {
            BeginNpcRepairWithCandidate(targetNpc, "Starting NPC repair with");
            return true;
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

    public bool StartExtractMateria()
    {
        if (!TryStartTask(UtilityTask.ExtractMateria, "Starting materia extraction."))
            return false;

        log.Information("[ADS][Utility] Starting materia extraction flow.");
        return true;
    }

    public bool StartDesynthFromInventory()
    {
        if (!TryStartTask(UtilityTask.DesynthFromInventory, "Starting inventory desynthesis."))
            return false;

        log.Information("[ADS][Utility] Starting inventory desynthesis flow.");
        return true;
    }

    public void Update()
    {
        if (!IsRunning)
            return;

        try
        {
            if (DateTime.UtcNow - startedAtUtc > OverallTimeout)
            {
                Fail($"Timed out while running {GetTaskLabel(activeTask)}.");
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
            }
        }
        catch (Exception ex)
        {
            Fail($"{GetTaskLabel(activeTask)} failed: {ex.Message}");
        }
    }

    public void Cancel(string reason)
    {
        if (!IsRunning)
            return;

        var message = $"Cancelled {GetTaskLabel(activeTask)}: {reason}";
        LastFailureMessage = message;
        LastCompletionUtc = DateTime.UtcNow;
        StopMovementIfNpcRepair();
        log.Warning($"[ADS][Utility] Cancelled {GetTaskLabel(activeTask)}: {reason}");
        ResetState();
        StatusMessage = message;
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
            Cancel("manual restart");

        ResetState();
        activeTask = task;
        startedAtUtc = DateTime.UtcNow;
        StatusMessage = statusMessage;
        return true;
    }

    private void UpdateSelfRepair()
    {
        if (TryCompleteRepairIfFinished("Self-repair finished; equipped gear is fully repaired."))
            return;

        if (!PrepareForUiWork("self-repair"))
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

        if (now - lastActionUtc < UiSettleCooldown)
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
        if (TryCompleteRepairIfFinished("NPC repair finished; equipped gear is fully repaired."))
            return;

        if (!PrepareForUiWork("NPC repair"))
            return;

        var now = DateTime.UtcNow;
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
            Fail($"Repair NPC {targetNpcName} is no longer nearby.");
            return;
        }

        var distance = DistanceToLocalPlayer(targetNpc);
        if (distance > RepairNpcInteractRadius)
        {
            if (now - lastMoveCommandUtc >= MoveRetryCooldown)
            {
                StatusMessage = $"Moving to repair NPC {targetNpcName}.";
                SendMoveCommand(targetNpc.Position, targetNpcName, initial: false);
            }

            return;
        }

        StopMovementIfNpcRepair();
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

        if (now - lastActionUtc < UiSettleCooldown)
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

        if (desynthCategoryIndex >= DesynthInventoryCategories.Length)
        {
            GameInteractionHelper.TryCloseAddon("SalvageItemSelector", log);
            Complete(desynthAttemptedAny
                ? "Inventory desynthesis finished."
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
        var desiredCategory = DesynthInventoryCategories[desynthCategoryIndex];
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
            StatusMessage = desynthCategoryIndex < DesynthInventoryCategories.Length
                ? $"No desynthable items remained in {GetDesynthCategoryLabel(desiredCategory)}; moving on."
                : "No further desynthesis categories remain.";
            return;
        }

        if (now - lastActionUtc >= UiRetryCooldown)
        {
            GameInteractionHelper.FireAddonCallback("SalvageItemSelector", true, 12, 0);
            lastActionUtc = now;
            StatusMessage = $"Selecting the next item from {GetDesynthCategoryLabel(desiredCategory)} for desynthesis.";
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
                UpdateNpcRepairInnNpcSearch(now, route);
                break;
        }
    }

    private void UpdateNpcRepairFieldTravel(DateTime now, ResolvedFieldRepairRoute route)
    {
        if (now - npcRepairTravelStageStartedUtc > NpcRepairTravelTimeout)
        {
            Fail($"Timed out while travelling to {route.AetheryteName} in {route.TerritoryName} for NPC repair.");
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

    private bool PrepareForUiWork(string actionLabel)
    {
        if (condition[ConditionFlag.BetweenAreas])
        {
            StatusMessage = $"Waiting for zoning to finish before {actionLabel}.";
            return false;
        }

        if (!condition[ConditionFlag.Mounted])
            return true;

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
        if (now - lastMenuSelectionUtc < MenuRetryCooldown)
        {
            StatusMessage = $"Waiting for repair menu retry window on {targetNpcName}.";
            return;
        }

        var optionIndex = npcRepairFallbackToFirstOption ? 0 : Math.Max(0, targetNpcRepairIndex);
        if (GameInteractionHelper.IsAddonVisible("SelectIconString"))
        {
            GameInteractionHelper.FireAddonCallback("SelectIconString", true, optionIndex);
            lastMenuSelectionUtc = now;
            StatusMessage = $"Selecting repair option {optionIndex} on {targetNpcName}.";
        }
        else if (GameInteractionHelper.IsAddonVisible("SelectString"))
        {
            GameInteractionHelper.FireAddonCallback("SelectString", true, optionIndex);
            lastMenuSelectionUtc = now;
            StatusMessage = $"Selecting repair menu row {optionIndex} on {targetNpcName}.";
        }

        if (!npcRepairFallbackToFirstOption && targetNpcRepairIndex != 0)
            npcRepairFallbackToFirstOption = true;
    }

    private unsafe void ClickRepairAll(AddonRepair* repairAddon)
        => ClickButtonIfEnabled(repairAddon->RepairAllButton, (AtkUnitBase*)repairAddon);

    private static unsafe bool IsRepairAllEnabled(AddonRepair* repairAddon)
        => repairAddon->RepairAllButton != null && repairAddon->RepairAllButton->IsEnabled;

    private bool TryCompleteRepairIfFinished(string message)
    {
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
        GameInteractionHelper.TryCloseAddon("SelectYesno", log);
        GameInteractionHelper.TryCloseAddon("Repair", log);
    }

    private void ResetRepairSubmission()
    {
        repairSubmissionSent = false;
        lastRepairConfirmClickUtc = DateTime.MinValue;
        repairConfirmClickAttempts = 0;
    }

    private void BeginNpcRepairWithCandidate(RepairNpcCandidate targetNpc, string logPrefix)
    {
        ClearNpcRepairInnTravel();
        targetNpcGameObjectId = targetNpc.GameObjectId;
        targetNpcBaseId = targetNpc.BaseId;
        targetNpcName = targetNpc.Name;
        targetNpcRepairIndex = targetNpc.RepairIndex;
        npcRepairFallbackToFirstOption = false;
        ResetRepairSubmission();

        var distance = targetNpc.Distance;
        if (distance <= RepairNpcInteractRadius)
        {
            StatusMessage = $"Interacting with repair NPC {targetNpcName}.";
            TryInteractWithRepairNpc(targetNpc.GameObject);
        }
        else
        {
            StatusMessage = $"Moving to repair NPC {targetNpcName}.";
            SendMoveCommand(targetNpc.GameObject.Position, targetNpcName, initial: true);
        }

        log.Information($"[ADS][Utility] {logPrefix} {targetNpcName} at {distance:0.0}y.");
    }

    private bool TryBeginNpcRepairInnTravel(out string failureMessage)
    {
        failureMessage = string.Empty;
        if (!IsLifestreamLoaded())
        {
            failureMessage = $"No repair NPC found within {RepairNpcSearchRadius:0}y, and Lifestream was not loaded for the inn fallback.";
            return false;
        }

        if (!TryResolveInnRepairRoute(out var route))
        {
            failureMessage = $"No repair NPC found within {RepairNpcSearchRadius:0}y, and no unlocked inn repair route was available.";
            return false;
        }

        activeNpcRepairInnRoute = route;
        npcRepairInnPathIndex = 0;
        npcRepairTravelCommandUtc = DateTime.MinValue;
        ResetRepairSubmission();

        if (route.TerritoryTypeId == clientState.TerritoryType)
        {
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

        if (!TrySendNpcRepairInnTeleport(route))
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
            activeNpcRepairFieldRoute = null;
            failureMessage = $"No repair NPC found within {RepairNpcSearchRadius:0}y, and ADS could not start the Lifestream field-aetheryte teleport.";
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
        var aetheryteSheet = dataManager.GetExcelSheet<Aetheryte>();
        if (aetheryteSheet == null)
            return false;

        ResolvedInnRepairRoute? sameTerritoryRoute = null;
        ResolvedInnRepairRoute? bestUnlockedRoute = null;
        foreach (var seed in InnRepairRouteSeeds)
        {
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

            if (!TryGetUnlockedInnTerritoryGilCost(territoryTypeId, out var gilCost))
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
        var aetheryteSheet = dataManager.GetExcelSheet<Aetheryte>();
        if (aetheryteSheet == null)
            return false;

        ResolvedFieldRepairRoute? sameTerritoryRoute = null;
        ResolvedFieldRepairRoute? bestUnlockedRoute = null;
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

                var candidate = new ResolvedFieldRepairRoute(
                    territoryTypeId,
                    territoryName,
                    aetheryte.RowId,
                    aetheryteName,
                    (int)entry.GilCost);

                if (territoryTypeId == clientState.TerritoryType)
                {
                    if (sameTerritoryRoute is null || candidate.GilCost < sameTerritoryRoute.Value.GilCost)
                        sameTerritoryRoute = candidate;
                    continue;
                }

                if (bestUnlockedRoute is null || candidate.GilCost < bestUnlockedRoute.Value.GilCost)
                    bestUnlockedRoute = candidate;
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[ADS][Utility] Failed to inspect unlocked aetherytes for NPC repair field travel.");
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

    private static bool IsEligibleFieldRepairAetheryte(uint aetheryteId, string aetheryteName, string territoryName)
    {
        if (FieldRepairDeniedAetheryteIds.Contains(aetheryteId))
            return false;

        var combined = $"{aetheryteName} {territoryName}".ToLowerInvariant();
        return !FieldRepairDeniedNameTerms.Any(combined.Contains);
    }

    private bool TryGetUnlockedInnTerritoryGilCost(uint territoryTypeId, out int gilCost)
    {
        gilCost = int.MaxValue;
        var aetheryteSheet = dataManager.GetExcelSheet<Aetheryte>();
        if (aetheryteSheet == null)
            return false;

        uint aetheryteId = 0;
        foreach (var aetheryte in aetheryteSheet)
        {
            if (!aetheryte.IsAetheryte || aetheryte.Territory.RowId != territoryTypeId)
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
        var command = $"/li {route.AethernetName}";
        npcRepairTravelCommandUtc = DateTime.UtcNow;
        if (!GameInteractionHelper.TrySendChatCommand(commandManager, command, log))
            return false;

        log.Information($"[ADS][Utility] No local repair NPC was found; sending {command} to reach {route.TerritoryName} for NPC repair.");
        return true;
    }

    private bool TrySendNpcRepairFieldTeleport(ResolvedFieldRepairRoute route)
    {
        var command = $"/li {route.AetheryteName}";
        npcRepairTravelCommandUtc = DateTime.UtcNow;
        if (!GameInteractionHelper.TrySendChatCommand(commandManager, command, log))
            return false;

        log.Information($"[ADS][Utility] No local repair NPC was found; sending {command} to reach field aetheryte {route.AetheryteName} in {route.TerritoryName} for NPC repair.");
        return true;
    }

    private void UpdateNpcRepairInnTeleport(DateTime now, ResolvedInnRepairRoute route)
    {
        if (condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51])
        {
            StatusMessage = $"Waiting for the inn teleport to {route.TerritoryName} to finish.";
            return;
        }

        if (clientState.TerritoryType != route.TerritoryTypeId)
        {
            StatusMessage = now - npcRepairTravelCommandUtc < LifestreamTeleportSettleCooldown
                ? $"Waiting for Lifestream to route to {route.AethernetName}."
                : $"Waiting to arrive at {route.TerritoryName} for NPC repair.";
            return;
        }

        if (objectTable.LocalPlayer == null || now - npcRepairTravelCommandUtc < UiSettleCooldown)
        {
            StatusMessage = $"Waiting for {route.TerritoryName} to settle after the Lifestream hop.";
            return;
        }

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
        if (condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51])
        {
            StatusMessage = $"Waiting for the field-aetheryte teleport to {route.TerritoryName} to finish.";
            return;
        }

        if (clientState.TerritoryType != route.TerritoryTypeId)
        {
            StatusMessage = now - npcRepairTravelCommandUtc < LifestreamTeleportSettleCooldown
                ? $"Waiting for Lifestream to route to {route.AetheryteName}."
                : $"Waiting to arrive at {route.TerritoryName} for NPC repair.";
            return;
        }

        if (objectTable.LocalPlayer == null || now - npcRepairTravelCommandUtc < UiSettleCooldown)
        {
            StatusMessage = $"Waiting for {route.TerritoryName} to settle after the field-aetheryte hop.";
            return;
        }

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

        if (npcRepairInnPathIndex >= route.Path.Length)
        {
            StopMovementIfNpcRepair();
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

        if (now - lastMoveCommandUtc >= MoveRetryCooldown)
        {
            var waypointLabel = $"{route.TerritoryName} inn waypoint {npcRepairInnPathIndex + 1}/{route.Path.Length}";
            StatusMessage = $"Moving to {waypointLabel}.";
            SendMoveCommand(waypoint, waypointLabel, initial: lastMoveCommandUtc == DateTime.MinValue);
        }
    }

    private void UpdateNpcRepairInnNpcSearch(DateTime now, ResolvedInnRepairRoute route)
    {
        if (TryFindNearbyRepairNpc(out var targetNpc))
        {
            BeginNpcRepairWithCandidate(targetNpc, $"Reached the {route.TerritoryName} inn fallback and found");
            return;
        }

        if (now - npcRepairTravelStageStartedUtc < UiSettleCooldown)
        {
            StatusMessage = $"Looking for a repair NPC near the {route.TerritoryName} inn.";
            return;
        }

        Fail($"Reached the {route.TerritoryName} inn repair route, but no repair NPC was found within {RepairNpcSearchRadius:0}y.");
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

        Fail($"Reached field aetheryte {route.AetheryteName} in {route.TerritoryName}, but no repair NPC was found within {RepairNpcSearchRadius:0}y.");
    }

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
        activeNpcRepairInnRoute = null;
        activeNpcRepairFieldRoute = null;
        npcRepairInnPathIndex = 0;
    }

    private static bool IsLifestreamLoaded()
    {
        try
        {
            return Plugin.PluginInterface.InstalledPlugins.Any(plugin =>
                plugin.IsLoaded
                && (string.Equals(plugin.InternalName, "Lifestream", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(plugin.Name, "Lifestream", StringComparison.OrdinalIgnoreCase)
                    || plugin.Name.Contains("Lifestream", StringComparison.OrdinalIgnoreCase)));
        }
        catch
        {
            return false;
        }
    }

    private unsafe bool TryFindNearbyRepairNpc(out RepairNpcCandidate candidate)
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
            if (distance > RepairNpcSearchRadius)
                continue;

            if (!TryGetRepairIndex(obj.BaseId, out var repairIndex))
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
        var sheet = dataManager.GetExcelSheet<ENpcBase>();
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

    private void TryInteractWithRepairNpc(IGameObject npc)
    {
        lastInteractUtc = DateTime.UtcNow;
        if (GameInteractionHelper.TryInteractWithObject(targetManager, npc, log))
            log.Information($"[ADS][Utility] Interacting with repair NPC {targetNpcName}.");
    }

    private void SendMoveCommand(Vector3 destination, string label, bool initial)
    {
        lastMoveCommandUtc = DateTime.UtcNow;
        var command = string.Format(
            CultureInfo.InvariantCulture,
            "/vnav moveto {0:F2} {1:F2} {2:F2}",
            destination.X,
            destination.Y,
            destination.Z);
        GameInteractionHelper.TrySendChatCommand(commandManager, command, log);
        log.Information($"[ADS][Utility] {(initial ? "Starting" : "Refreshing")} movement toward {label}.");
    }

    private void StopMovementIfNpcRepair()
    {
        if (activeTask != UtilityTask.NpcRepair)
            return;

        GameInteractionHelper.TrySendChatCommand(commandManager, "/vnav stop", log);
    }

    private float DistanceToLocalPlayer(IGameObject obj)
    {
        var player = objectTable.LocalPlayer;
        return player == null ? float.MaxValue : Vector3.Distance(player.Position, obj.Position);
    }

    private void Complete(string message)
    {
        StopMovementIfNpcRepair();
        log.Information($"[ADS][Utility] {message}");
        LastSuccessMessage = message;
        LastCompletionUtc = DateTime.UtcNow;
        ResetState();
        StatusMessage = message;
    }

    private void Fail(string message)
    {
        StopMovementIfNpcRepair();
        log.Warning($"[ADS][Utility] {message}");
        LastFailureMessage = message;
        LastCompletionUtc = DateTime.UtcNow;
        ResetState();
        StatusMessage = message;
    }

    private void ResetState()
    {
        activeTask = UtilityTask.None;
        activeNpcRepairMode = NpcRepairMode.InnFallback;
        startedAtUtc = DateTime.MinValue;
        lastActionUtc = DateTime.MinValue;
        lastMoveCommandUtc = DateTime.MinValue;
        lastInteractUtc = DateTime.MinValue;
        lastMenuSelectionUtc = DateTime.MinValue;
        repairWindowSeenUtc = DateTime.MinValue;
        targetNpcGameObjectId = 0;
        targetNpcBaseId = 0;
        targetNpcName = string.Empty;
        targetNpcRepairIndex = 0;
        npcRepairFallbackToFirstOption = false;
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
        if (StatusMessage == "Idle")
            return;

        if (activeTask == UtilityTask.None && string.IsNullOrWhiteSpace(StatusMessage))
            StatusMessage = "Idle";
    }

    private static string GetTaskLabel(UtilityTask task)
        => task switch
        {
            UtilityTask.SelfRepair => "self-repair",
            UtilityTask.NpcRepair => "NPC repair",
            UtilityTask.ExtractMateria => "materia extraction",
            UtilityTask.DesynthFromInventory => "inventory desynthesis",
            _ => "utility automation",
        };

    private static string GetDesynthCategoryLabel(AgentSalvage.SalvageItemCategory category)
        => category switch
        {
            AgentSalvage.SalvageItemCategory.InventoryEquipment => "inventory equipment",
            AgentSalvage.SalvageItemCategory.InventoryHousing => "inventory housing",
            _ => category.ToString(),
        };

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
