using ADS.Models;
using System.Globalization;
using System.Numerics;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using MountSheet = Lumina.Excel.Sheets.Mount;

namespace ADS.Services;

public sealed class ExecutionService
{
    private const float NavigationPhaseRange = 6f;
    private const float PreferredInteractArrivalRange = 0.8f;
    private const float DirectInteractAttemptRange = 2.0f;
    private const int RequiredInteractionAttemptLimit = 3;
    private const float PreferredMonsterArrivalRange = 2.0f;
    private const float PreferredFollowArrivalRange = 3.0f;
    private const float PreferredRecoveryArrivalRange = 2.0f;
    private const float PreferredFrontierArrivalRange = 4.0f;
    private const float RequiredInteractionConsumedRelocationRange = 20.0f;
    private const uint PraetoriumTerritoryTypeId = 1044;
    private const float MountedCombatClusterRadius = 6.0f;
    private const float RecoveryGhostRetireRadius = 8.0f;
    private const float RecoveryClusterArrivalRange = RecoveryGhostRetireRadius;
    private const float RecoveryTargetSimilarityRadius = RecoveryGhostRetireRadius;
    private const float CloseRangeInteractFallbackHorizontalDistance = 2.5f;
    private const float CloseRangeInteractFallbackVerticalCap = 4.0f;
    private const float CloseRangeInteractFallbackProgressMargin = 0.2f;
    private const float TreasureCofferProgressMargin = 2.0f;
    private static readonly TimeSpan InteractAttemptCooldown = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan CloseRangeInteractFallbackNoProgressTimeout = TimeSpan.FromSeconds(3.0);
    private static readonly TimeSpan RequiredInteractionRetryDelay = InteractAttemptCooldown;
    private static readonly TimeSpan NavigationRetryCooldown = TimeSpan.FromSeconds(2.0);
    private static readonly TimeSpan MapFlagNavigationRetryCooldown = TimeSpan.FromSeconds(6.0);
    private static readonly TimeSpan MountedCombatAttemptCooldown = TimeSpan.FromMilliseconds(800);
    private static readonly TimeSpan RecoveryTruthSettleDelay = TimeSpan.FromSeconds(1.0);
    private static readonly TimeSpan ProgressionInteractResultSettleDelay = TimeSpan.FromSeconds(6.0);
    private static readonly TimeSpan TreasureCofferNoProgressTimeout = TimeSpan.FromSeconds(30.0);
    private static readonly TimeSpan TreasureCofferMaxNavigationDuration = TimeSpan.FromSeconds(75.0);

    private readonly IDataManager dataManager;
    private readonly IObjectTable objectTable;
    private readonly ITargetManager targetManager;
    private readonly ICommandManager commandManager;
    private readonly ObservationMemoryService observationMemoryService;
    private readonly DungeonFrontierService dungeonFrontierService;
    private readonly MapFlagService mapFlagService;
    private readonly ObjectPriorityRuleService objectPriorityRuleService;
    private readonly IPluginLog? log;
    private readonly Dictionary<uint, MountedCombatAction[]> mountedCombatActionCache = [];
    private string? committedInteractableKey;
    private uint committedInteractableMapId;
    private PlannerObjectiveKind committedInteractableObjectiveKind = PlannerObjectiveKind.None;
    private ulong lastInteractGameObjectId;
    private DateTime nextInteractAttemptUtc;
    private DateTime nextNavigationCommandUtc;
    private ulong movementTargetGameObjectId;
    private bool navigationActive;
    private bool mapFlagNavigationActive;
    private PlannerObjectiveKind recoveryTargetObjectiveKind = PlannerObjectiveKind.None;
    private Vector3? recoveryTargetPosition;
    private DateTime recoveryTargetReachedUtc;
    private ObservedInteractable? pendingProgressionInteractable;
    private DateTime pendingProgressionInteractResultUntilUtc;
    private DateTime pendingProgressionInteractAfterWaitUntilUtc;
    private int pendingRequiredInteractionAttemptsSent;
    private string? interactArrivalWaitKey;
    private DateTime interactArrivalWaitUntilUtc;
    private string? treasureCofferRouteKey;
    private DateTime treasureCofferRouteStartedUtc;
    private DateTime treasureCofferLastProgressUtc;
    private float treasureCofferBestHorizontalDistance = float.MaxValue;
    private string? closeRangeInteractFallbackKey;
    private DateTime closeRangeInteractFallbackLastProgressUtc;
    private float closeRangeInteractFallbackBestHorizontalDistance = float.MaxValue;
    private DateTime nextMountedCombatAttemptUtc;

    public ExecutionService(
        IDataManager dataManager,
        IObjectTable objectTable,
        ITargetManager targetManager,
        ICommandManager commandManager,
        ObservationMemoryService observationMemoryService,
        DungeonFrontierService dungeonFrontierService,
        MapFlagService mapFlagService,
        ObjectPriorityRuleService objectPriorityRuleService,
        IPluginLog? log = null)
    {
        this.dataManager = dataManager;
        this.objectTable = objectTable;
        this.targetManager = targetManager;
        this.commandManager = commandManager;
        this.observationMemoryService = observationMemoryService;
        this.dungeonFrontierService = dungeonFrontierService;
        this.mapFlagService = mapFlagService;
        this.objectPriorityRuleService = objectPriorityRuleService;
        this.log = log;
    }

    private readonly record struct MountedCombatAction(uint ActionId, string Name, bool TargetArea, float Range);
    private readonly record struct MountedCombatTarget(ObservedMonster Observed, IGameObject GameObject, float Distance);
    private readonly record struct MountedCombatClusterTarget(ObservedMonster Observed, IGameObject GameObject, float Distance, int ClusterCount);

    public OwnershipMode CurrentMode { get; private set; } = OwnershipMode.Idle;
    public ExecutionPhase CurrentPhase { get; private set; } = ExecutionPhase.Idle;
    public string LastStatus { get; private set; } = "Idle.";

    public bool IsOwned
        => CurrentMode is OwnershipMode.OwnedStartOutside or OwnershipMode.OwnedStartInside or OwnershipMode.OwnedResumeInside or OwnershipMode.Leaving;

    public bool StartDutyFromOutside()
    {
        ClearInteractableCommitment();
        CurrentMode = OwnershipMode.OwnedStartOutside;
        SetPhase(ExecutionPhase.OutsideQueue, "Queued outside start. ADS will claim ownership when you enter a supported ADS duty.");
        return true;
    }

    public bool StartDutyFromInside(DutyContextSnapshot context)
    {
        ClearInteractableCommitment();
        if (!context.InDuty || !context.IsSupportedDuty)
        {
            CurrentMode = context.InDuty ? OwnershipMode.Failed : OwnershipMode.Idle;
            SetPhase(
                CurrentMode == OwnershipMode.Failed ? ExecutionPhase.Failure : ExecutionPhase.Idle,
                "Start inside requires being inside a supported ADS duty.");
            return false;
        }

        CurrentMode = OwnershipMode.OwnedStartInside;
        SetPhase(
            ExecutionPhase.WaitingForTruth,
            $"Owned inside {context.CurrentDuty?.EnglishName}. ADS now runs the staged execution phase engine for this duty.");
        return true;
    }

    public bool ResumeDutyFromInside(DutyContextSnapshot context)
    {
        ClearInteractableCommitment();
        if (!context.InDuty || !context.IsSupportedDuty)
        {
            CurrentMode = context.InDuty ? OwnershipMode.Failed : OwnershipMode.Idle;
            SetPhase(
                CurrentMode == OwnershipMode.Failed ? ExecutionPhase.Failure : ExecutionPhase.Idle,
                "Resume requires being inside a supported ADS duty.");
            return false;
        }

        CurrentMode = OwnershipMode.OwnedResumeInside;
        SetPhase(
            ExecutionPhase.WaitingForTruth,
            $"Resumed ownership inside {context.CurrentDuty?.EnglishName}. ADS execution phases are active.");
        return true;
    }

    public bool LeaveDuty(DutyContextSnapshot context)
    {
        if (!IsOwned || !context.InDuty)
        {
            SetPhase(CurrentPhase, "Leave requires active ADS ownership inside duty.");
            return false;
        }

        CurrentMode = OwnershipMode.Leaving;
        SetPhase(ExecutionPhase.LeavingDuty, "Leave requested. ADS is waiting for duty exit before releasing ownership.");
        return true;
    }

    public void Stop(DutyContextSnapshot context)
    {
        StopMovementAssists();
        ClearInteractableCommitment();
        ResetRecoveryHold();
        CurrentMode = context.InDuty && context.IsSupportedDuty ? OwnershipMode.Observing : OwnershipMode.Idle;
        SetPhase(
            CurrentMode == OwnershipMode.Observing ? ExecutionPhase.ObservingOnly : ExecutionPhase.Idle,
            CurrentMode == OwnershipMode.Observing
            ? "Ownership released. ADS returned to observing-only mode."
            : "ADS stopped and ownership was released.");
    }

    public void CompleteDuty(string dutyName)
    {
        StopMovementAssists();
        ClearInteractableCommitment();
        ResetRecoveryHold();
        CurrentMode = OwnershipMode.Observing;
        SetPhase(
            ExecutionPhase.ObservingOnly,
            $"Duty completed: {dutyName}. ADS stopped automation and cleared recovery follow-through; use Start/Resume for another run.");
    }

    public void Update(DutyContextSnapshot context, PlannerSnapshot planner, ObservationSnapshot observation, bool pluginEnabled)
    {
        if (context.BetweenAreas)
            StopMovementAssists();

        if (!pluginEnabled)
        {
            StopMovementAssists();
            ClearInteractableCommitment();
            CurrentMode = OwnershipMode.Idle;
            SetPhase(ExecutionPhase.Idle, "ADS disabled.");
            return;
        }

        switch (CurrentMode)
        {
            case OwnershipMode.OwnedStartOutside:
                if (!context.InDuty)
                {
                    StopMovementAssists();
                    ClearInteractableCommitment();
                    SetPhase(ExecutionPhase.OutsideQueue, "Waiting to enter a supported duty from outside.");
                    return;
                }

                if (!context.IsSupportedDuty)
                {
                    StopMovementAssists();
                    ClearInteractableCommitment();
                    SetPhase(ExecutionPhase.AwaitingSupportedPilotDuty, "Outside-start ownership is pending a supported ADS duty.");
                    return;
                }

                UpdateOwnedPhase(context, planner, observation, $"Owned after outside start in {context.CurrentDuty?.EnglishName}.");
                return;

            case OwnershipMode.OwnedStartInside:
            case OwnershipMode.OwnedResumeInside:
                if (!context.InDuty)
                {
                    StopMovementAssists();
                    ClearInteractableCommitment();
                    CurrentMode = OwnershipMode.Idle;
                    SetPhase(ExecutionPhase.Idle, "Duty ended; ADS ownership released.");
                    return;
                }

                if (!context.IsSupportedDuty)
                {
                    StopMovementAssists();
                    ClearInteractableCommitment();
                    CurrentMode = OwnershipMode.Failed;
                    SetPhase(ExecutionPhase.Failure, "Current duty is not an ADS-supported 4-man duty.");
                    return;
                }

                UpdateOwnedPhase(context, planner, observation, $"Owned in {context.CurrentDuty?.EnglishName}.");
                return;

            case OwnershipMode.Leaving:
                if (!context.InDuty)
                {
                    StopMovementAssists();
                    ClearInteractableCommitment();
                    CurrentMode = OwnershipMode.Idle;
                    SetPhase(ExecutionPhase.Idle, "Duty exit detected; ADS ownership released.");
                    return;
                }

                SetPhase(ExecutionPhase.LeavingDuty, "Leave requested. Waiting for duty exit.");
                return;

            case OwnershipMode.Failed:
                if (!context.InDuty)
                {
                    StopMovementAssists();
                    ClearInteractableCommitment();
                    CurrentMode = OwnershipMode.Idle;
                    SetPhase(ExecutionPhase.Idle, "Failure state cleared outside duty.");
                    return;
                }

                SetPhase(ExecutionPhase.Failure, "ADS is in a failure state until the current unsupported ownership context clears.");
                return;
        }

        if (context.InDuty && context.IsSupportedDuty)
        {
            StopMovementAssists();
            ClearInteractableCommitment();
            CurrentMode = OwnershipMode.Observing;
            SetPhase(ExecutionPhase.ObservingOnly, "Observing only; ADS does not currently own this duty.");
            return;
        }

        StopMovementAssists();
        ClearInteractableCommitment();
        CurrentMode = OwnershipMode.Idle;
        SetPhase(ExecutionPhase.Idle, "Idle.");
    }

    private void UpdateOwnedPhase(DutyContextSnapshot context, PlannerSnapshot planner, ObservationSnapshot observation, string prefix)
    {
        if (context.IsUnsafeTransition || planner.Mode == PlannerMode.UnsafeTransition)
        {
            if (pendingProgressionInteractable is not null)
            {
                observationMemoryService.MarkProgressionInteractionSent(context, pendingProgressionInteractable);
                ClearInteractableCommitment();
            }

            ResetRecoveryHold();
            StopMovementAssists();
            SetPhase(ExecutionPhase.TransitionHold, $"{prefix} Waiting for safe post-transition duty truth before advancing.");
            return;
        }

        if (TryHoldPendingProgressionInteractResult(context, observation, prefix))
            return;

        if ((context.InCombat || planner.Mode == PlannerMode.Combat)
            && !ShouldBypassCombatHold(context, planner))
        {
            ResetRecoveryHold();
            StopMovementAssists();
            SetPhase(ExecutionPhase.CombatHold, $"{prefix} Combat is active, so ADS is holding progression until combat clears.");
            return;
        }

        if (TryAdvancePraetoriumMountedCombat(context, observation, prefix))
            return;

        if (planner.Mode == PlannerMode.Recovery)
        {
            TryAdvanceRecoveryObjective(planner, observation, $"{prefix}");
            return;
        }

        ResetRecoveryHold();

        if (planner.Mode == PlannerMode.Progression)
        {
            var committedInteractable = ResolveCommittedInteractable(context, observation);
            if (committedInteractable is not null)
            {
                if (IsStickyInteractableCommitment(committedInteractableObjectiveKind)
                    && planner.ObjectiveKind == committedInteractableObjectiveKind)
                {
                    TryAdvanceInteractableObjective(context, committedInteractable, $"{prefix} Following through on the committed treasure coffer so ADS does not bounce between chest and combat objectives.");
                    return;
                }

                if (planner.ObjectiveKind is PlannerObjectiveKind.RequiredInteractable
                    or PlannerObjectiveKind.CombatFriendlyInteractable
                    or PlannerObjectiveKind.ExpendableInteractable
                    or PlannerObjectiveKind.OptionalInteractable
                    or PlannerObjectiveKind.TreasureCoffer)
                {
                    var replannedInteractable = ResolveObservedInteractable(planner, observation);
                    if (replannedInteractable is not null && replannedInteractable.Key != committedInteractable.Key)
                    {
                        CommitInteractable(replannedInteractable, planner.ObjectiveKind);
                        TryAdvanceInteractableObjective(context, replannedInteractable, $"{prefix} Switching follow-through to the planner's newer interactable selection.");
                        return;
                    }
                }
                else
                {
                    if (committedInteractableObjectiveKind == PlannerObjectiveKind.TreasureCoffer)
                    {
                        ClearInteractableCommitment();
                    }
                    else
                    {
                        var playerPosition = objectTable.LocalPlayer?.Position;
                        var committedDistance = playerPosition.HasValue
                            ? GetHorizontalDistance(committedInteractable.Position, playerPosition.Value)
                            : (float?)null;
                        if (committedDistance.HasValue && committedDistance.Value > NavigationPhaseRange)
                        {
                            ClearInteractableCommitment();
                        }
                    }
                }
            }

            committedInteractable = ResolveCommittedInteractable(context, observation);
            if (committedInteractable is not null)
            {
                TryAdvanceInteractableObjective(context, committedInteractable, $"{prefix} Following through on the previously selected interactable.");
                return;
            }

            if (ShouldContinueCommittedManualDestination(planner)
                && TryAdvanceCommittedManualDestination(context, $"{prefix}"))
            {
                return;
            }

            if (planner.ObjectiveKind is PlannerObjectiveKind.Monster or PlannerObjectiveKind.BossFightMonster)
            {
                TryAdvanceMonsterObjective(planner, observation, $"{prefix}");
                return;
            }

            if (planner.ObjectiveKind == PlannerObjectiveKind.FollowTarget)
            {
                TryAdvanceFollowObjective(planner, observation, $"{prefix}");
                return;
            }

            if (planner.ObjectiveKind is PlannerObjectiveKind.Frontier or PlannerObjectiveKind.MapXzDestination or PlannerObjectiveKind.XyzDestination)
            {
                TryAdvanceFrontierObjective(context, planner, $"{prefix}");
                return;
            }

            if (planner.ObjectiveKind is PlannerObjectiveKind.RequiredInteractable
                or PlannerObjectiveKind.CombatFriendlyInteractable
                or PlannerObjectiveKind.ExpendableInteractable
                or PlannerObjectiveKind.OptionalInteractable
                or PlannerObjectiveKind.TreasureCoffer)
            {
                var selectedInteractable = ResolveObservedInteractable(planner, observation);
                if (selectedInteractable is null)
                {
                    StopMovementAssists();
                    SetPhase(ExecutionPhase.ReadyForInteractableObjective, $"{prefix} Interactable objective armed but not currently resolved in live object truth: {planner.Objective}");
                    return;
                }

                CommitInteractable(selectedInteractable, planner.ObjectiveKind);
                TryAdvanceInteractableObjective(context, selectedInteractable, $"{prefix}");
                return;
            }
        }

        StopMovementAssists();
        SetPhase(ExecutionPhase.WaitingForTruth, $"{prefix} Waiting for a stronger live objective signal.");
    }

    private void TryAdvanceRecoveryObjective(PlannerSnapshot planner, ObservationSnapshot observation, string prefix)
    {
        var playerPosition = objectTable.LocalPlayer?.Position;
        if (!playerPosition.HasValue)
        {
            StopMovementAssists();
            SetPhase(ExecutionPhase.RecoveryHint, $"{prefix} Recovery phase selected but local player position was unavailable: {planner.Objective}");
            return;
        }

        var ghostPosition = ResolveRecoveryGhostPosition(planner, observation, playerPosition.Value);
        if (!ghostPosition.HasValue)
        {
            StopMovementAssists();
            SetPhase(ExecutionPhase.RecoveryHint, $"{prefix} Recovery phase selected but no matching ghost position was resolved: {planner.Objective}");
            return;
        }

        var targetDistance = Vector3.Distance(playerPosition.Value, ghostPosition.Value);
        if (targetDistance > RecoveryClusterArrivalRange)
        {
            ResetRecoveryHold();
            var preferredApproachPoint = BuildPreferredApproachPoint(playerPosition.Value, ghostPosition.Value, PreferredRecoveryArrivalRange);
            TryBeginNavigation(BuildRecoveryTargetId(planner), preferredApproachPoint);
            SetPhase(
                ExecutionPhase.NavigatingToRecoveryObjective,
                $"{prefix} Recovering toward {planner.Objective} ({targetDistance:0.0}y) using ghost memory.");
            return;
        }

        StopMovementAssists();
        var now = DateTime.UtcNow;
        if (!MatchesRecoveryTarget(planner.ObjectiveKind, ghostPosition.Value))
        {
            recoveryTargetObjectiveKind = planner.ObjectiveKind;
            recoveryTargetPosition = ghostPosition.Value;
            recoveryTargetReachedUtc = now;
            SetPhase(
                ExecutionPhase.RecoveryHint,
                $"{prefix} Reached the recovery cluster area for {planner.Objective} ({targetDistance:0.0}y). Holding briefly for stronger live truth.");
            return;
        }

        if (now - recoveryTargetReachedUtc < RecoveryTruthSettleDelay)
        {
            SetPhase(
                ExecutionPhase.RecoveryHint,
                $"{prefix} Reached the recovery cluster area for {planner.Objective} ({targetDistance:0.0}y). Holding briefly for stronger live truth.");
            return;
        }

        var retiredCount = observationMemoryService.RetireNearbyRecoveryGhosts(
            planner.ObjectiveKind,
            ghostPosition.Value,
            RecoveryGhostRetireRadius);
        ResetRecoveryHold();
        SetPhase(
            ExecutionPhase.RecoveryHint,
            retiredCount > 0
                ? $"{prefix} Reached the recovery cluster area for {planner.Objective} ({targetDistance:0.0}y). No stronger live truth appeared, so ADS retired {retiredCount} nearby ghost hint(s) and will advance to the next recovery candidate."
                : $"{prefix} Reached the recovery cluster area for {planner.Objective} ({targetDistance:0.0}y). Waiting for stronger live truth.");
    }

    private void TryAdvanceMonsterObjective(PlannerSnapshot planner, ObservationSnapshot observation, string prefix)
    {
        var observedMonster = ResolveObservedMonster(planner, observation);
        var objectiveLabel = planner.ObjectiveKind == PlannerObjectiveKind.BossFightMonster
            ? "boss-fight target"
            : "monster pack";
        if (observedMonster is null)
        {
            StopMovementAssists();
            SetPhase(ExecutionPhase.ReadyForMonsterObjective, $"{prefix} {objectiveLabel} objective armed but not currently resolved in live object truth: {planner.Objective}");
            return;
        }

        var gameObject = ResolveGameObject(observedMonster);
        if (gameObject is null)
        {
            StopMovementAssists();
            SetPhase(ExecutionPhase.ReadyForMonsterObjective, $"{prefix} {objectiveLabel} objective is on the planner but no live game object was resolved yet: {planner.Objective}");
            return;
        }

        var playerPosition = objectTable.LocalPlayer?.Position;
        var targetDistance = playerPosition.HasValue
            ? Vector3.Distance(gameObject.Position, playerPosition.Value)
            : float.MaxValue;

        if (!playerPosition.HasValue)
        {
            StopMovementAssists();
            SetPhase(ExecutionPhase.ReadyForMonsterObjective, $"{prefix} Local player position was unavailable while resolving {observedMonster.Name}.");
            return;
        }

        targetManager.Target = gameObject;
        if (targetDistance > PreferredMonsterArrivalRange)
        {
            var preferredApproachPoint = BuildPreferredApproachPoint(playerPosition.Value, gameObject.Position, PreferredMonsterArrivalRange);
            TryBeginNavigation(gameObject.GameObjectId, preferredApproachPoint);
            SetPhase(
                ExecutionPhase.NavigatingToMonsterObjective,
                $"{prefix} Navigating toward {objectiveLabel} {observedMonster.Name} ({targetDistance:0.0}y) with a close combat arrival target.");
            return;
        }

        StopMovementAssists();
        SetPhase(
            ExecutionPhase.ReadyForMonsterObjective,
            $"{prefix} Positioned near {objectiveLabel} {observedMonster.Name} ({targetDistance:0.0}y). Waiting for combat tooling / aggro to take over.");
    }

    private bool TryAdvancePraetoriumMountedCombat(DutyContextSnapshot context, ObservationSnapshot observation, string prefix)
    {
        if (!IsPraetoriumMountedCombatContext(context))
            return false;

        var localPlayer = objectTable.LocalPlayer;
        if (localPlayer is null)
            return false;

        var mountId = localPlayer.CurrentMount?.RowId ?? 0;
        if (!TryGetCurrentMountCombatActions(mountId, out var mountedActions))
            return false;

        var targets = observation.LiveMonsters
            .Where(x => MatchesCurrentMap(context, x.MapId))
            .Select(x => (Observed: x, GameObject: ResolveGameObject(x)))
            .Where(x => x.GameObject is not null && x.GameObject.IsTargetable)
            .Select(x => new MountedCombatTarget(x.Observed, x.GameObject!, Vector3.Distance(localPlayer.Position, x.GameObject!.Position)))
            .ToList();
        if (targets.Count == 0)
            return false;

        var readyGroundActions = mountedActions
            .Where(x => x.TargetArea && IsMountedCombatActionReady(x.ActionId))
            .ToArray();
        foreach (var action in readyGroundActions)
        {
            if (!TryGetBestMountedGroundTarget(localPlayer.Position, targets, action, out var target))
                continue;

            StopMovementAssists();
            if (DateTime.UtcNow < nextMountedCombatAttemptUtc)
            {
                SetPhase(
                    ExecutionPhase.MountedDutyCombat,
                    $"{prefix} Mounted Praetorium combat is active near {target.Observed.Name}; waiting for the next {action.Name} send window.");
                return true;
            }

            if (TryUseGroundTargetAction(action.ActionId, target.GameObject.Position))
            {
                nextMountedCombatAttemptUtc = DateTime.UtcNow + MountedCombatAttemptCooldown;
                SetPhase(
                    ExecutionPhase.MountedDutyCombat,
                    $"{prefix} Mounted Praetorium combat fired {action.Name} at {target.Observed.Name}, covering about {target.ClusterCount} nearby enemy target(s).");
                return true;
            }
        }

        var readyTargetedActions = mountedActions
            .Where(x => !x.TargetArea && IsMountedCombatActionReady(x.ActionId))
            .ToArray();
        foreach (var action in readyTargetedActions)
        {
            if (!TryGetBestMountedTargetForAction(action.ActionId, targets, out var target)
                && !TryGetNearestMountedCombatTargetInRange(targets, action, out target))
                continue;

            StopMovementAssists();
            if (DateTime.UtcNow < nextMountedCombatAttemptUtc)
            {
                SetPhase(
                    ExecutionPhase.MountedDutyCombat,
                    $"{prefix} Mounted Praetorium combat is active near {target.Observed.Name}; waiting for the next {action.Name} send window.");
                return true;
            }

            if (TryUseTargetedAction(action.ActionId, target.GameObject))
            {
                nextMountedCombatAttemptUtc = DateTime.UtcNow + MountedCombatAttemptCooldown;
                SetPhase(
                    ExecutionPhase.MountedDutyCombat,
                    $"{prefix} Mounted Praetorium combat fired {action.Name} at {target.Observed.Name} ({target.Distance:0.0}y).");
                return true;
            }
        }

        var anyTargetsInMountedCombatRange = mountedActions.Any(action =>
            action.TargetArea
                ? TryGetBestMountedGroundTarget(localPlayer.Position, targets, action, out _)
                : TryGetBestMountedTargetForAction(action.ActionId, targets, out _)
                    || TryGetNearestMountedCombatTargetInRange(targets, action, out _));
        if (!anyTargetsInMountedCombatRange)
            return false;

        StopMovementAssists();
        SetPhase(
            ExecutionPhase.MountedDutyCombat,
            $"{prefix} Mounted Praetorium combat is holding near {targets.Count} live enemy target(s) while the current mount weapons cool down.");
        return true;
    }

    private void TryAdvanceFollowObjective(PlannerSnapshot planner, ObservationSnapshot observation, string prefix)
    {
        var followTarget = ResolveObservedFollowTarget(planner, observation);
        if (followTarget is null)
        {
            StopMovementAssists();
            SetPhase(ExecutionPhase.ReadyForFollowObjective, $"{prefix} Follow objective armed but not currently resolved in live object truth: {planner.Objective}");
            return;
        }

        var gameObject = ResolveGameObject(followTarget);
        if (gameObject is null)
        {
            StopMovementAssists();
            SetPhase(ExecutionPhase.ReadyForFollowObjective, $"{prefix} Follow objective is on the planner but no live targetable game object was resolved yet: {planner.Objective}");
            return;
        }

        var playerPosition = objectTable.LocalPlayer?.Position;
        if (!playerPosition.HasValue)
        {
            StopMovementAssists();
            SetPhase(ExecutionPhase.ReadyForFollowObjective, $"{prefix} Local player position was unavailable while following {followTarget.Name}.");
            return;
        }

        var targetDistance = Vector3.Distance(gameObject.Position, playerPosition.Value);
        targetManager.Target = gameObject;
        if (targetDistance > PreferredFollowArrivalRange)
        {
            var preferredApproachPoint = BuildPreferredApproachPoint(playerPosition.Value, gameObject.Position, PreferredFollowArrivalRange);
            TryBeginNavigation(gameObject.GameObjectId, preferredApproachPoint);
            SetPhase(
                ExecutionPhase.NavigatingToFollowObjective,
                $"{prefix} Following live anchor {followTarget.Name} ({targetDistance:0.0}y) until a live monster or interactable appears.");
            return;
        }

        StopMovementAssists();
        SetPhase(
            ExecutionPhase.ReadyForFollowObjective,
            $"{prefix} Holding near live anchor {followTarget.Name} ({targetDistance:0.0}y) until a live monster or interactable appears.");
    }

    private void TryAdvanceFrontierObjective(DutyContextSnapshot context, PlannerSnapshot planner, string prefix)
    {
        var wantsMapXzDestination = planner.ObjectiveKind == PlannerObjectiveKind.MapXzDestination;
        var wantsXyzDestination = planner.ObjectiveKind == PlannerObjectiveKind.XyzDestination;
        var frontierPoint = dungeonFrontierService.CurrentTarget;
        if (frontierPoint is null
            || !string.Equals(frontierPoint.Name, planner.TargetName ?? string.Empty, StringComparison.OrdinalIgnoreCase))
        {
            StopMovementAssists();
            SetPhase(
                GetFrontierHintPhase(wantsMapXzDestination, wantsXyzDestination),
                $"{prefix} Frontier objective armed but no current waypoint was resolved: {planner.Objective}");
            return;
        }

        var isManualMapXzDestination = wantsMapXzDestination
            || frontierPoint.IsManualMapXzDestination;
        var isManualXyzDestination = wantsXyzDestination
            || frontierPoint.IsManualXyzDestination;
        TryAdvanceFrontierPoint(context, frontierPoint, isManualMapXzDestination, isManualXyzDestination, prefix);
    }

    private bool TryAdvanceCommittedManualDestination(DutyContextSnapshot context, string prefix)
    {
        var frontierPoint = dungeonFrontierService.GetCurrentOrRememberedManualDestination(objectTable.LocalPlayer?.Position);
        if (frontierPoint is null)
            return false;

        var destinationLabel = frontierPoint.IsManualXyzDestination ? "manual XYZ destination" : "manual Map XZ destination";
        var destinationCompletion = frontierPoint.IsManualXyzDestination
            ? "configured XYZ point"
            : "configured XZ point";
        TryAdvanceFrontierPoint(
            context,
            frontierPoint,
            frontierPoint.IsManualMapXzDestination,
            frontierPoint.IsManualXyzDestination,
            $"{prefix} Following through on the committed {destinationLabel} until ADS reaches the {destinationCompletion} or hits BetweenAreas.");
        return true;
    }

    private void TryAdvanceFrontierPoint(DutyContextSnapshot context, DungeonFrontierPoint frontierPoint, bool isMapXzDestination, bool isXyzDestination, string prefix)
    {
        var playerPosition = objectTable.LocalPlayer?.Position;
        var frontierLabel = dungeonFrontierService.CurrentMode == FrontierMode.HeadingScout
            ? "forward scout"
            : isMapXzDestination
                ? "map XZ destination"
                : isXyzDestination
                    ? "XYZ destination"
                : "map frontier";
        var frontierReason = isMapXzDestination
            ? "because a human-authored Map XZ destination is configured for this no-live-object gap."
            : isXyzDestination
                ? "because a human-authored XYZ destination is configured for this no-live-object gap."
            : dungeonFrontierService.CurrentMode == FrontierMode.HeadingScout
                ? "because Lumina frontier labels were unavailable and no live duty objects are currently visible."
                : "because no live duty objects are currently visible.";
        if (!playerPosition.HasValue)
        {
            StopMovementAssists();
            SetPhase(
                GetFrontierHintPhase(isMapXzDestination, isXyzDestination),
                $"{prefix} Local player position was unavailable while resolving {frontierLabel} {frontierPoint.Name}.");
            return;
        }

        var targetHorizontalDistance = GetHorizontalDistance(frontierPoint.Position, playerPosition.Value);
        var targetDistance = Vector3.Distance(frontierPoint.Position, playerPosition.Value);
        var targetVerticalDelta = MathF.Abs(frontierPoint.Position.Y - playerPosition.Value.Y);
        var arrivalRange = isMapXzDestination && frontierPoint.ArrivalRadiusXz > 0f
            ? frontierPoint.ArrivalRadiusXz
            : PreferredFrontierArrivalRange;
        var xyzArrivalRange = isXyzDestination && frontierPoint.ArrivalRadius3d > 0f
            ? frontierPoint.ArrivalRadius3d
            : PreferredFrontierArrivalRange;
        var destinationReached = isXyzDestination
            ? targetDistance <= xyzArrivalRange
            : targetHorizontalDistance <= arrivalRange;
        if (!destinationReached)
        {
            var frontierTargetId = BuildFrontierTargetId(frontierPoint);
            var usedMapFlagNavigation = (isMapXzDestination || dungeonFrontierService.CurrentMode == FrontierMode.Label)
                && TryBeginMapFlagNavigation(context, frontierTargetId, frontierPoint.Name, frontierPoint.Position);
            if (!usedMapFlagNavigation)
            {
                var navigationDestination = isMapXzDestination || isXyzDestination
                    ? frontierPoint.Position
                    : BuildPreferredApproachPoint(playerPosition.Value, frontierPoint.Position, PreferredFrontierArrivalRange);
                TryBeginNavigation(frontierTargetId, navigationDestination);
            }

            var navigationMethod = usedMapFlagNavigation
                ? "via map flag and /vnav moveflag"
                : "via direct /vnav moveto";
            SetPhase(
                GetFrontierNavigatingPhase(isMapXzDestination, isXyzDestination),
                isXyzDestination
                    ? $"{prefix} Advancing toward {frontierLabel} {frontierPoint.Name} (3D {targetDistance:0.0}y, XZ {targetHorizontalDistance:0.0}y, Y {targetVerticalDelta:0.0}; ghost radius {xyzArrivalRange:0.0}y) {navigationMethod} {frontierReason}"
                    : $"{prefix} Advancing toward {frontierLabel} {frontierPoint.Name} (XZ {targetHorizontalDistance:0.0}y, Y {targetVerticalDelta:0.0}; ghost radius {arrivalRange:0.0}y) {navigationMethod} {frontierReason}");
            return;
        }

        StopMovementAssists();
        if (isMapXzDestination || isXyzDestination)
            dungeonFrontierService.MarkVisited(frontierPoint, playerPosition.Value);

        SetPhase(
            GetFrontierHintPhase(isMapXzDestination, isXyzDestination),
            isXyzDestination
                ? $"{prefix} Reached {frontierLabel} {frontierPoint.Name} (3D {targetDistance:0.0}y, XZ {targetHorizontalDistance:0.0}y, Y {targetVerticalDelta:0.0}). Waiting for live duty objects or duty completion."
                : $"{prefix} Reached {frontierLabel} {frontierPoint.Name} (XZ {targetHorizontalDistance:0.0}y, Y {targetVerticalDelta:0.0}). Waiting for live duty objects or duty completion.");
    }

    private void TryAdvanceInteractableObjective(DutyContextSnapshot context, ObservedInteractable observedInteractable, string prefix)
    {
        var gameObject = ResolveGameObject(observedInteractable);
        if (gameObject is null)
        {
            StopMovementAssists();
            SetPhase(ExecutionPhase.ReadyForInteractableObjective, $"{prefix} Interactable objective is selected, but no live game object was resolved yet: {observedInteractable.Name}");
            return;
        }

        var playerPosition = objectTable.LocalPlayer?.Position;
        var targetHorizontalDistance = playerPosition.HasValue
            ? GetHorizontalDistance(gameObject.Position, playerPosition.Value)
            : float.MaxValue;
        var targetDistance = playerPosition.HasValue
            ? Vector3.Distance(gameObject.Position, playerPosition.Value)
            : float.MaxValue;
        var targetVerticalDelta = playerPosition.HasValue
            ? MathF.Abs(gameObject.Position.Y - playerPosition.Value.Y)
            : float.MaxValue;

        if (!playerPosition.HasValue)
        {
            StopMovementAssists();
            SetPhase(ExecutionPhase.ReadyForInteractableObjective, $"{prefix} Local player position was unavailable while resolving {observedInteractable.Name}.");
            return;
        }

        var effectiveRule = objectPriorityRuleService.GetEffectiveRule(
            context,
            observedInteractable,
            targetDistance,
            targetVerticalDelta);
        var waitBeforeInteractSeconds = effectiveRule?.WaitAtDestinationSeconds ?? 0f;
        var waitAfterInteractSeconds = effectiveRule?.WaitAfterInteractSeconds ?? 0f;

        var preferredApproachPoint = BuildPreferredInteractApproachPoint(playerPosition.Value, gameObject.Position);
        if (TrySkipStuckTreasureCoffer(observedInteractable, targetHorizontalDistance, prefix))
            return;

        var usingCloseRangeInteractFallback = targetDistance > DirectInteractAttemptRange
            && ShouldUseCloseRangeInteractFallback(observedInteractable, targetHorizontalDistance, targetVerticalDelta);
        if (targetDistance > DirectInteractAttemptRange && !usingCloseRangeInteractFallback)
        {
            TryBeginNavigation(gameObject.GameObjectId, preferredApproachPoint);

            var phase = targetDistance > NavigationPhaseRange
                ? ExecutionPhase.NavigatingToInteractableObjective
                : ExecutionPhase.ApproachingInteractableObjective;
            var verb = phase == ExecutionPhase.NavigatingToInteractableObjective
                ? "Navigating toward"
                : "Close-navigating toward";
            SetPhase(
                phase,
                $"{prefix} {verb} {observedInteractable.Name} (XZ {targetHorizontalDistance:0.0}y, 3D {targetDistance:0.0}y, Y {targetVerticalDelta:0.0}) with a full XYZ stand-off target.");
            return;
        }

        StopMovementAssists();
        var now = DateTime.UtcNow;
        if (TryHoldConfiguredArrivalWait(
                observedInteractable,
                waitBeforeInteractSeconds,
                targetHorizontalDistance,
                targetDistance,
                targetVerticalDelta,
                prefix,
                now))
        {
            return;
        }

        if (now < nextInteractAttemptUtc && lastInteractGameObjectId == observedInteractable.GameObjectId)
        {
            var cooldownReason = usingCloseRangeInteractFallback
                ? $"Holding close-XZ interact fallback on {observedInteractable.Name} after {CloseRangeInteractFallbackNoProgressTimeout.TotalSeconds:0}s with no XZ progress (XZ {targetHorizontalDistance:0.0}y, 3D {targetDistance:0.0}y, Y {targetVerticalDelta:0.0}); waiting for interact cooldown/result."
                : $"Waiting for interact cooldown/result on {observedInteractable.Name}.";
            SetPhase(ExecutionPhase.AttemptingInteractableObjective, $"{prefix} {cooldownReason}");
            return;
        }

        if (TryInteractWithObject(gameObject))
        {
            if (observedInteractable.Classification == InteractableClass.TreasureCoffer)
            {
                observationMemoryService.MarkTreasureInteractionSent(observedInteractable);
            }
            else
            {
                var continuingRequiredFollowThrough = observedInteractable.Classification == InteractableClass.Required
                    && pendingProgressionInteractable is not null
                    && string.Equals(pendingProgressionInteractable.Key, observedInteractable.Key, StringComparison.Ordinal);
                pendingProgressionInteractable = observedInteractable;
                pendingProgressionInteractResultUntilUtc = observedInteractable.Classification == InteractableClass.Required
                    ? now + RequiredInteractionRetryDelay
                    : now + ProgressionInteractResultSettleDelay;
                pendingProgressionInteractAfterWaitUntilUtc = waitAfterInteractSeconds > 0f
                    ? now + TimeSpan.FromSeconds(waitAfterInteractSeconds)
                    : DateTime.MinValue;
                if (pendingProgressionInteractAfterWaitUntilUtc > pendingProgressionInteractResultUntilUtc)
                    pendingProgressionInteractResultUntilUtc = pendingProgressionInteractAfterWaitUntilUtc;
                pendingRequiredInteractionAttemptsSent = observedInteractable.Classification == InteractableClass.Required
                    ? (continuingRequiredFollowThrough ? pendingRequiredInteractionAttemptsSent + 1 : 1)
                    : 0;
            }

            lastInteractGameObjectId = observedInteractable.GameObjectId;
            nextInteractAttemptUtc = now + InteractAttemptCooldown;
            var interactResult = observedInteractable.Classification switch
            {
                InteractableClass.Required => $"Direct interact sent to {observedInteractable.Name} (required attempt {pendingRequiredInteractionAttemptsSent}/{RequiredInteractionAttemptLimit}).",
                InteractableClass.Expendable => $"Direct interact sent to {observedInteractable.Name}; ADS will keep retrying this expendable until it disappears.",
                _ => $"Direct interact sent to {observedInteractable.Name}.",
            };
            if (usingCloseRangeInteractFallback)
            {
                interactResult = $"{interactResult} Close-XZ fallback engaged after {CloseRangeInteractFallbackNoProgressTimeout.TotalSeconds:0}s with no XZ progress (XZ {targetHorizontalDistance:0.0}y, 3D {targetDistance:0.0}y, Y {targetVerticalDelta:0.0}).";
            }

            SetPhase(ExecutionPhase.AttemptingInteractableObjective, $"{prefix} {interactResult}");
            return;
        }

        nextInteractAttemptUtc = now + InteractAttemptCooldown;
        if (targetDistance > PreferredInteractArrivalRange)
        {
            if (usingCloseRangeInteractFallback)
            {
                SetPhase(
                    ExecutionPhase.AttemptingInteractableObjective,
                    $"{prefix} Direct interact did not land for {observedInteractable.Name}; holding close-XZ interact fallback and retrying from XZ {targetHorizontalDistance:0.0}y (3D {targetDistance:0.0}y, Y {targetVerticalDelta:0.0}).");
                return;
            }

            TryBeginNavigation(gameObject.GameObjectId, preferredApproachPoint);
            SetPhase(
                ExecutionPhase.ApproachingInteractableObjective,
                $"{prefix} Direct interact did not land for {observedInteractable.Name} at XZ {targetHorizontalDistance:0.0}y (3D {targetDistance:0.0}y, Y {targetVerticalDelta:0.0}); continuing close-nav toward the full XYZ stand-off target.");
            return;
        }

        SetPhase(ExecutionPhase.ReadyForInteractableObjective, $"{prefix} Direct interact attempt failed for {observedInteractable.Name}; waiting to retry.");
    }

    private void CommitInteractable(ObservedInteractable interactable, PlannerObjectiveKind objectiveKind)
    {
        if (committedInteractableKey == interactable.Key && committedInteractableObjectiveKind == objectiveKind)
            return;

        if (objectiveKind != PlannerObjectiveKind.TreasureCoffer || committedInteractableKey != interactable.Key)
            ResetTreasureCofferRouteTracking();

        if (committedInteractableKey != interactable.Key || committedInteractableObjectiveKind != objectiveKind)
            ResetCloseRangeInteractFallbackTracking();

        committedInteractableKey = interactable.Key;
        committedInteractableMapId = interactable.MapId;
        committedInteractableObjectiveKind = objectiveKind;
        lastInteractGameObjectId = 0;
        nextInteractAttemptUtc = DateTime.MinValue;
    }

    private static bool IsStickyInteractableCommitment(PlannerObjectiveKind objectiveKind)
        => objectiveKind == PlannerObjectiveKind.TreasureCoffer;

    private bool ShouldBypassCombatHold(DutyContextSnapshot context, PlannerSnapshot planner)
    {
        if (IsPraetoriumMountedCombatContext(context))
            return true;

        if (!context.InCombat)
            return false;

        if (planner.Mode == PlannerMode.Progression
            && planner.ObjectiveKind is PlannerObjectiveKind.CombatFriendlyInteractable or PlannerObjectiveKind.BossFightMonster)
        {
            return true;
        }

        return pendingProgressionInteractable?.Classification == InteractableClass.CombatFriendly;
    }

    private static bool ShouldContinueCommittedManualDestination(PlannerSnapshot planner)
        => planner.ObjectiveKind is not (
            PlannerObjectiveKind.RequiredInteractable
            or PlannerObjectiveKind.CombatFriendlyInteractable
            or PlannerObjectiveKind.ExpendableInteractable
            or PlannerObjectiveKind.OptionalInteractable
            or PlannerObjectiveKind.TreasureCoffer
            or PlannerObjectiveKind.BossFightMonster
            or PlannerObjectiveKind.MapXzDestination
            or PlannerObjectiveKind.XyzDestination);

    private ObservedInteractable? ResolveCommittedInteractable(DutyContextSnapshot context, ObservationSnapshot observation)
    {
        if (string.IsNullOrWhiteSpace(committedInteractableKey))
            return null;

        if (!MatchesCurrentMap(context, committedInteractableMapId))
        {
            ClearInteractableCommitment();
            return null;
        }

        var interactable = observation.LiveInteractables.FirstOrDefault(x =>
            x.Key == committedInteractableKey
            && MatchesCurrentMap(context, x.MapId));
        if (interactable is not null)
        {
            if (IsInteractableStillAllowedInContext(context, interactable))
                return interactable;

            ClearInteractableCommitment();
            return null;
        }

        ClearInteractableCommitment();
        return null;
    }

    private void ClearInteractableCommitment()
    {
        committedInteractableKey = null;
        committedInteractableMapId = 0;
        committedInteractableObjectiveKind = PlannerObjectiveKind.None;
        lastInteractGameObjectId = 0;
        nextInteractAttemptUtc = DateTime.MinValue;
        ResetTreasureCofferRouteTracking();
        ResetCloseRangeInteractFallbackTracking();
        ClearPendingProgressionInteractResult();
        ResetInteractArrivalWait();
    }

    private bool TrySkipStuckTreasureCoffer(ObservedInteractable observedInteractable, float targetHorizontalDistance, string prefix)
    {
        if (observedInteractable.Classification != InteractableClass.TreasureCoffer)
            return false;

        var now = DateTime.UtcNow;
        if (treasureCofferRouteKey != observedInteractable.Key)
        {
            treasureCofferRouteKey = observedInteractable.Key;
            treasureCofferRouteStartedUtc = now;
            treasureCofferLastProgressUtc = now;
            treasureCofferBestHorizontalDistance = targetHorizontalDistance;
            return false;
        }

        if (targetHorizontalDistance + TreasureCofferProgressMargin < treasureCofferBestHorizontalDistance)
        {
            treasureCofferBestHorizontalDistance = targetHorizontalDistance;
            treasureCofferLastProgressUtc = now;
            return false;
        }

        if (targetHorizontalDistance <= NavigationPhaseRange)
            return false;

        var noProgressTooLong = now - treasureCofferLastProgressUtc > TreasureCofferNoProgressTimeout;
        var routeTooLong = now - treasureCofferRouteStartedUtc > TreasureCofferMaxNavigationDuration;
        if (!noProgressTooLong && !routeTooLong)
            return false;

        var reason = routeTooLong
            ? $"route exceeded {TreasureCofferMaxNavigationDuration.TotalSeconds:0}s"
            : $"no XZ progress for {TreasureCofferNoProgressTimeout.TotalSeconds:0}s";
        var bestHorizontalDistance = treasureCofferBestHorizontalDistance;
        observationMemoryService.MarkTreasureCofferSkipped(observedInteractable, reason);
        ClearInteractableCommitment();
        StopMovementAssists();
        SetPhase(
            ExecutionPhase.ReadyForInteractableObjective,
            $"{prefix} Skipping optional treasure coffer after {reason}; best XZ was {bestHorizontalDistance:0.0}y and current XZ is {targetHorizontalDistance:0.0}y.");
        return true;
    }

    private void ResetTreasureCofferRouteTracking()
    {
        treasureCofferRouteKey = null;
        treasureCofferRouteStartedUtc = DateTime.MinValue;
        treasureCofferLastProgressUtc = DateTime.MinValue;
        treasureCofferBestHorizontalDistance = float.MaxValue;
    }

    private bool ShouldUseCloseRangeInteractFallback(ObservedInteractable observedInteractable, float targetHorizontalDistance, float targetVerticalDelta)
    {
        var now = DateTime.UtcNow;
        if (!string.Equals(closeRangeInteractFallbackKey, observedInteractable.Key, StringComparison.Ordinal))
        {
            closeRangeInteractFallbackKey = observedInteractable.Key;
            closeRangeInteractFallbackLastProgressUtc = now;
            closeRangeInteractFallbackBestHorizontalDistance = targetHorizontalDistance;
            return false;
        }

        if (targetHorizontalDistance > CloseRangeInteractFallbackHorizontalDistance
            || targetVerticalDelta > CloseRangeInteractFallbackVerticalCap)
        {
            closeRangeInteractFallbackLastProgressUtc = now;
            closeRangeInteractFallbackBestHorizontalDistance = targetHorizontalDistance;
            return false;
        }

        if (targetHorizontalDistance + CloseRangeInteractFallbackProgressMargin < closeRangeInteractFallbackBestHorizontalDistance)
        {
            closeRangeInteractFallbackBestHorizontalDistance = targetHorizontalDistance;
            closeRangeInteractFallbackLastProgressUtc = now;
            return false;
        }

        return now - closeRangeInteractFallbackLastProgressUtc >= CloseRangeInteractFallbackNoProgressTimeout;
    }

    private void ResetCloseRangeInteractFallbackTracking()
    {
        closeRangeInteractFallbackKey = null;
        closeRangeInteractFallbackLastProgressUtc = DateTime.MinValue;
        closeRangeInteractFallbackBestHorizontalDistance = float.MaxValue;
    }

    private bool TryHoldPendingProgressionInteractResult(DutyContextSnapshot context, ObservationSnapshot observation, string prefix)
    {
        if (pendingProgressionInteractable is null)
            return false;

        var pendingInteractable = pendingProgressionInteractable;
        if (!IsInteractableStillAllowedInContext(context, pendingInteractable))
        {
            log?.Information($"[ADS] Cleared interact follow-through for {pendingInteractable.Name} after the live map/rule context changed.");
            ClearInteractableCommitment();
            return false;
        }

        var pendingLiveInteractable = ResolvePendingProgressionInteractable(context, observation, pendingInteractable);
        if (pendingLiveInteractable is not null && !IsInteractableStillAllowedInContext(context, pendingLiveInteractable))
        {
            log?.Information($"[ADS] Cleared interact follow-through for {pendingLiveInteractable.Name} after the live map/rule context changed.");
            ClearInteractableCommitment();
            return false;
        }
        var isExpendableFollowThrough = pendingInteractable.Classification == InteractableClass.Expendable;
        var isRequiredFollowThrough = pendingInteractable.Classification == InteractableClass.Required;
        if (isRequiredFollowThrough && context.IsUnsafeTransition)
        {
            ClearInteractableCommitment();
            StopMovementAssists();
            SetPhase(
                ExecutionPhase.AttemptingInteractableObjective,
                $"{prefix} Required interact follow-through for {pendingInteractable.Name} ended because BetweenAreas is active.");
            return true;
        }

        if (context.Mounted)
        {
            observationMemoryService.MarkProgressionInteractionSent(context, pendingInteractable);
            ClearInteractableCommitment();
            StopMovementAssists();
            SetPhase(
                ExecutionPhase.AttemptingInteractableObjective,
                $"{prefix} Interact follow-through finished for {pendingInteractable.Name}; Mounted became active after the interact, so ADS treated that interactable position as consumed and is waiting for refreshed duty truth.");
            return true;
        }

        var playerPosition = objectTable.LocalPlayer?.Position;
        if (isRequiredFollowThrough
            && pendingLiveInteractable is not null
            && playerPosition.HasValue)
        {
            var pendingHorizontalDistance = GetHorizontalDistance(pendingLiveInteractable.Position, playerPosition.Value);
            if (pendingHorizontalDistance >= RequiredInteractionConsumedRelocationRange)
            {
                observationMemoryService.MarkProgressionInteractionSent(context, pendingInteractable);
                ClearInteractableCommitment();
                StopMovementAssists();
                SetPhase(
                    ExecutionPhase.AttemptingInteractableObjective,
                    $"{prefix} Required interact follow-through for {pendingInteractable.Name} stayed live, but the player was displaced to XZ {pendingHorizontalDistance:0.0}y after the interact. ADS is treating that interactable position as consumed and suppressing it until duty reset.");
                return true;
            }
        }

        var now = DateTime.UtcNow;
        if (now < pendingProgressionInteractResultUntilUtc)
        {
            StopMovementAssists();
            var holdReason = pendingProgressionInteractAfterWaitUntilUtc > now
                ? $"Holding configured post-interact wait on {pendingInteractable.Name} for another {(pendingProgressionInteractAfterWaitUntilUtc - now).TotalSeconds:0.0}s before ADS retries or replans."
                : isRequiredFollowThrough
                    ? $"Holding still for required interact follow-through on {pendingInteractable.Name} (attempt {pendingRequiredInteractionAttemptsSent}/{RequiredInteractionAttemptLimit})."
                    : isExpendableFollowThrough
                        ? $"Waiting for interact result on {pendingInteractable.Name}; ADS will keep this expendable selected until it disappears."
                        : $"Waiting for interact result on {pendingInteractable.Name}; ADS will not select another objective until the interact follow-through window finishes.";
            SetPhase(
                ExecutionPhase.AttemptingInteractableObjective,
                $"{prefix} {holdReason}");
            return true;
        }

        if (isRequiredFollowThrough && pendingLiveInteractable is not null)
        {
            if (pendingRequiredInteractionAttemptsSent >= RequiredInteractionAttemptLimit)
            {
                ClearInteractableCommitment();
                StopMovementAssists();
                SetPhase(
                    ExecutionPhase.AttemptingInteractableObjective,
                    $"{prefix} Required interact follow-through finished {RequiredInteractionAttemptLimit} sent attempt(s) for {pendingInteractable.Name}; ADS is releasing this attempt window.");
                return true;
            }

            TryAdvanceInteractableObjective(
                context,
                pendingLiveInteractable,
                $"{prefix} Required interact follow-through is still live, so ADS is retrying attempt {pendingRequiredInteractionAttemptsSent + 1}/{RequiredInteractionAttemptLimit} while holding the target steady.");
            return true;
        }

        if (isExpendableFollowThrough && pendingLiveInteractable is not null)
        {
            TryAdvanceInteractableObjective(
                context,
                pendingLiveInteractable,
                $"{prefix} Expendable interact follow-through is still live, so ADS is retrying it from the same <1y XZ stand-off.");
            return true;
        }

        observationMemoryService.MarkProgressionInteractionSent(context, pendingInteractable);
        ClearInteractableCommitment();
        StopMovementAssists();
        var completionReason = isRequiredFollowThrough
            ? $"Required interact follow-through finished for {pendingInteractable.Name}; the object is gone, so ADS is waiting for refreshed duty truth before selecting another objective."
            : isExpendableFollowThrough
                ? $"Expendable interact follow-through finished for {pendingInteractable.Name}; the object is gone, so ADS is waiting for refreshed duty truth before selecting another objective."
                : $"Interact follow-through finished for {pendingInteractable.Name}; waiting for refreshed duty truth before selecting another objective.";
        SetPhase(
            ExecutionPhase.AttemptingInteractableObjective,
            $"{prefix} {completionReason}");
        return true;
    }

    private void ClearPendingProgressionInteractResult()
    {
        pendingProgressionInteractable = null;
        pendingProgressionInteractResultUntilUtc = DateTime.MinValue;
        pendingProgressionInteractAfterWaitUntilUtc = DateTime.MinValue;
        pendingRequiredInteractionAttemptsSent = 0;
    }

    private bool TryHoldConfiguredArrivalWait(
        ObservedInteractable observedInteractable,
        float waitBeforeInteractSeconds,
        float targetHorizontalDistance,
        float targetDistance,
        float targetVerticalDelta,
        string prefix,
        DateTime now)
    {
        if (waitBeforeInteractSeconds <= 0f)
            return false;

        if (!string.Equals(interactArrivalWaitKey, observedInteractable.Key, StringComparison.Ordinal))
        {
            interactArrivalWaitKey = observedInteractable.Key;
            interactArrivalWaitUntilUtc = now + TimeSpan.FromSeconds(waitBeforeInteractSeconds);
        }

        if (now >= interactArrivalWaitUntilUtc)
            return false;

        StopMovementAssists();
        SetPhase(
            ExecutionPhase.AttemptingInteractableObjective,
            $"{prefix} Holding configured pre-interact wait on {observedInteractable.Name} for another {(interactArrivalWaitUntilUtc - now).TotalSeconds:0.0}s after arriving in range (XZ {targetHorizontalDistance:0.0}y, 3D {targetDistance:0.0}y, Y {targetVerticalDelta:0.0}y).");
        return true;
    }

    private void ResetInteractArrivalWait()
    {
        interactArrivalWaitKey = null;
        interactArrivalWaitUntilUtc = DateTime.MinValue;
    }

    private static ObservedInteractable? ResolvePendingProgressionInteractable(
        DutyContextSnapshot context,
        ObservationSnapshot observation,
        ObservedInteractable pendingInteractable)
    {
        var liveInteractable = observation.LiveInteractables.FirstOrDefault(x =>
            x.Key == pendingInteractable.Key
            && MatchesCurrentMap(context, x.MapId));
        if (liveInteractable is not null)
            return liveInteractable;

        return observation.LiveInteractables.FirstOrDefault(x =>
            MatchesCurrentMap(context, x.MapId)
            && MatchesCurrentMap(context, pendingInteractable.MapId)
            && x.MapId == pendingInteractable.MapId
            && x.ObjectKind == pendingInteractable.ObjectKind
            && x.DataId == pendingInteractable.DataId
            && x.Classification == pendingInteractable.Classification
            && string.Equals(x.Name, pendingInteractable.Name, StringComparison.OrdinalIgnoreCase)
            && GetHorizontalDistance(x.Position, pendingInteractable.Position) <= DirectInteractAttemptRange);
    }

    private bool IsInteractableStillAllowedInContext(DutyContextSnapshot context, ObservedInteractable interactable)
    {
        if (!MatchesCurrentMap(context, interactable.MapId))
            return false;

        var playerPosition = objectTable.LocalPlayer?.Position;
        var distance = playerPosition.HasValue
            ? Vector3.Distance(playerPosition.Value, interactable.Position)
            : (float?)null;
        var verticalDelta = playerPosition.HasValue
            ? MathF.Abs(interactable.Position.Y - playerPosition.Value.Y)
            : (float?)null;
        return !objectPriorityRuleService.ShouldIgnoreInteractable(context, interactable, distance, verticalDelta);
    }

    private static bool MatchesCurrentMap(DutyContextSnapshot context, uint mapId)
        => context.MapId == 0 || mapId == 0 || context.MapId == mapId;

    private bool MatchesRecoveryTarget(PlannerObjectiveKind objectiveKind, Vector3 position)
    {
        return recoveryTargetPosition.HasValue
            && recoveryTargetObjectiveKind == objectiveKind
            && Vector3.Distance(recoveryTargetPosition.Value, position) <= RecoveryTargetSimilarityRadius;
    }

    private void ResetRecoveryHold()
    {
        recoveryTargetObjectiveKind = PlannerObjectiveKind.None;
        recoveryTargetPosition = null;
        recoveryTargetReachedUtc = DateTime.MinValue;
    }

    private void TryBeginNavigation(ulong gameObjectId, Vector3 destination)
    {
        var now = DateTime.UtcNow;
        if (navigationActive && !mapFlagNavigationActive && movementTargetGameObjectId == gameObjectId && now < nextNavigationCommandUtc)
            return;

        if (TrySendCommand(string.Format(
                CultureInfo.InvariantCulture,
                "/vnav moveto {0:F2} {1:F2} {2:F2}",
                destination.X,
                destination.Y,
                destination.Z)))
        {
            navigationActive = true;
            mapFlagNavigationActive = false;
            movementTargetGameObjectId = gameObjectId;
            nextNavigationCommandUtc = now + NavigationRetryCooldown;
        }
    }

    private bool TryBeginMapFlagNavigation(DutyContextSnapshot context, ulong targetId, string targetName, Vector3 destination)
    {
        var now = DateTime.UtcNow;
        if (navigationActive && mapFlagNavigationActive && movementTargetGameObjectId == targetId && now < nextNavigationCommandUtc)
            return true;

        if (!mapFlagService.TryPlaceFlag(context.TerritoryTypeId, destination, targetName, out _))
            return false;

        if (!TrySendCommand("/vnav moveflag"))
            return false;

        navigationActive = true;
        mapFlagNavigationActive = true;
        movementTargetGameObjectId = targetId;
        nextNavigationCommandUtc = now + MapFlagNavigationRetryCooldown;
        return true;
    }

    private static Vector3 BuildPreferredApproachPoint(Vector3 playerPosition, Vector3 objectPosition, float preferredArrivalRange = PreferredInteractArrivalRange)
    {
        var flatDelta = new Vector3(objectPosition.X - playerPosition.X, 0f, objectPosition.Z - playerPosition.Z);
        var flatDistance = flatDelta.Length();
        if (flatDistance <= preferredArrivalRange || flatDistance <= float.Epsilon)
            return objectPosition;

        var flatDirection = Vector3.Normalize(flatDelta);
        return new Vector3(
            objectPosition.X - (flatDirection.X * preferredArrivalRange),
            objectPosition.Y,
            objectPosition.Z - (flatDirection.Z * preferredArrivalRange));
    }

    private static Vector3 BuildPreferredInteractApproachPoint(Vector3 playerPosition, Vector3 objectPosition)
    {
        return BuildPreferredApproachPoint(playerPosition, objectPosition);
    }

    private static float GetHorizontalDistance(Vector3 a, Vector3 b)
    {
        var x = a.X - b.X;
        var z = a.Z - b.Z;
        return MathF.Sqrt((x * x) + (z * z));
    }

    private static bool IsPraetoriumMountedCombatContext(DutyContextSnapshot context)
        => context.InDuty
            && context.Mounted
            && context.TerritoryTypeId == PraetoriumTerritoryTypeId;

    private bool TryGetCurrentMountCombatActions(uint mountId, out MountedCombatAction[] mountedActions)
    {
        mountedActions = [];

        if (mountId == 0)
            return false;

        if (mountedCombatActionCache.TryGetValue(mountId, out var cachedActions))
        {
            mountedActions = cachedActions;
            return mountedActions.Length > 0;
        }

        var mountSheet = dataManager.GetExcelSheet<MountSheet>();
        if (mountSheet is null
            || !mountSheet.TryGetRow(mountId, out var mount)
            || mount.MountAction.ValueNullable is not { Action: { Count: > 0 } actionRefs })
        {
            mountedCombatActionCache[mountId] = [];
            return false;
        }

        mountedActions = actionRefs
            .Where(x => x.RowId != 0 && x.ValueNullable is not null)
            .Select(x => new MountedCombatAction(
                x.RowId,
                x.Value.Name.ToString(),
                x.Value.TargetArea,
                MathF.Max(0f, ActionManager.GetActionRange(x.RowId))))
            .ToArray();
        mountedCombatActionCache[mountId] = mountedActions;
        return mountedActions.Length > 0;
    }

    private static unsafe bool IsMountedCombatActionReady(uint actionId)
    {
        var actionManager = ActionManager.Instance();
        return actionManager is not null && actionManager->GetActionStatus(ActionType.Action, actionId) == 0;
    }

    private static bool TryGetBestMountedGroundTarget(
        Vector3 playerPosition,
        IReadOnlyCollection<MountedCombatTarget> targets,
        MountedCombatAction action,
        out MountedCombatClusterTarget selectedTarget)
    {
        var maxRange = action.Range > 0 ? action.Range : 30f;
        var maxRangeSquared = maxRange * maxRange;
        var clusterRadiusSquared = MountedCombatClusterRadius * MountedCombatClusterRadius;

        var bestTarget = default(MountedCombatClusterTarget);
        var found = false;
        foreach (var target in targets)
        {
            var playerDistanceSquared = Vector3.DistanceSquared(playerPosition, target.GameObject.Position);
            if (playerDistanceSquared > maxRangeSquared)
                continue;

            var clusterCount = 0;
            foreach (var otherTarget in targets)
            {
                if (Vector3.DistanceSquared(target.GameObject.Position, otherTarget.GameObject.Position) <= clusterRadiusSquared)
                    clusterCount++;
            }

            var candidate = new MountedCombatClusterTarget(target.Observed, target.GameObject, target.Distance, clusterCount);
            if (!found
                || candidate.ClusterCount > bestTarget.ClusterCount
                || (candidate.ClusterCount == bestTarget.ClusterCount && candidate.Distance < bestTarget.Distance))
            {
                bestTarget = candidate;
                found = true;
            }
        }

        selectedTarget = bestTarget;
        return found;
    }

    private static unsafe bool TryGetBestMountedTargetForAction(
        uint actionId,
        IEnumerable<MountedCombatTarget> targets,
        out MountedCombatTarget selectedTarget)
    {
        foreach (var target in targets.OrderBy(x => x.Distance))
        {
            var nativeObject = (GameObject*)target.GameObject.Address;
            if (nativeObject is null)
                continue;

            if (ActionManager.CanUseActionOnTarget(actionId, nativeObject))
            {
                selectedTarget = target;
                return true;
            }
        }

        selectedTarget = default;
        return false;
    }

    private static bool TryGetNearestMountedCombatTargetInRange(
        IEnumerable<MountedCombatTarget> targets,
        MountedCombatAction action,
        out MountedCombatTarget selectedTarget)
    {
        var maxRange = action.Range > 0 ? action.Range : 30f;
        foreach (var target in targets.OrderBy(x => x.Distance))
        {
            if (target.Distance <= maxRange)
            {
                selectedTarget = target;
                return true;
            }
        }

        selectedTarget = default;
        return false;
    }

    private ObservedMonster? ResolveObservedMonster(PlannerSnapshot planner, ObservationSnapshot observation)
    {
        var candidates = observation.LiveMonsters
            .Where(x => x.Name.Equals(planner.TargetName ?? string.Empty, StringComparison.OrdinalIgnoreCase));

        if (planner.TargetDistance.HasValue)
        {
            return candidates
                .OrderBy(x => MathF.Abs(Vector3.Distance(x.Position, objectTable.LocalPlayer?.Position ?? x.Position) - planner.TargetDistance.Value))
                .FirstOrDefault();
        }

        return candidates.FirstOrDefault();
    }

    private ObservedMonster? ResolveObservedFollowTarget(PlannerSnapshot planner, ObservationSnapshot observation)
    {
        var candidates = observation.LiveFollowTargets
            .Where(x => x.Name.Equals(planner.TargetName ?? string.Empty, StringComparison.OrdinalIgnoreCase));

        if (planner.TargetDistance.HasValue)
        {
            return candidates
                .OrderBy(x => MathF.Abs(Vector3.Distance(x.Position, objectTable.LocalPlayer?.Position ?? x.Position) - planner.TargetDistance.Value))
                .FirstOrDefault();
        }

        return candidates.FirstOrDefault();
    }

    private static ulong BuildRecoveryTargetId(PlannerSnapshot planner)
    {
        var nameHash = (ulong)(planner.TargetName?.GetHashCode(StringComparison.OrdinalIgnoreCase) ?? 0);
        return 0xF000000000000000UL | ((ulong)planner.ObjectiveKind << 32) | (nameHash & 0x00000000FFFFFFFFUL);
    }

    private static ExecutionPhase GetFrontierNavigatingPhase(bool isMapXzDestination, bool isXyzDestination)
        => isXyzDestination
            ? ExecutionPhase.NavigatingToXyzDestination
            : isMapXzDestination
                ? ExecutionPhase.NavigatingToMapXzDestination
                : ExecutionPhase.NavigatingToFrontierObjective;

    private static ExecutionPhase GetFrontierHintPhase(bool isMapXzDestination, bool isXyzDestination)
        => isXyzDestination
            ? ExecutionPhase.XyzDestinationHint
            : isMapXzDestination
                ? ExecutionPhase.MapXzDestinationHint
                : ExecutionPhase.FrontierHint;

    private static ulong BuildFrontierTargetId(DungeonFrontierPoint frontierPoint)
        => 0xE000000000000000UL | (ComputeStableHash(frontierPoint.Key) & 0x0FFFFFFFFFFFFFFFUL);

    private static ulong ComputeStableHash(string value)
    {
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;

        var hash = offsetBasis;
        foreach (var character in value)
        {
            hash ^= character;
            hash *= prime;
        }

        return hash;
    }

    private Vector3? ResolveRecoveryGhostPosition(PlannerSnapshot planner, ObservationSnapshot observation, Vector3 playerPosition)
    {
        return planner.ObjectiveKind switch
        {
            PlannerObjectiveKind.MonsterGhost => observation.MonsterGhosts
                .Where(x => x.Name.Equals(planner.TargetName ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => MathF.Abs(Vector3.Distance(x.Position, playerPosition) - (planner.TargetDistance ?? Vector3.Distance(x.Position, playerPosition))))
                .Select(x => (Vector3?)x.Position)
                .FirstOrDefault(),
            PlannerObjectiveKind.InteractableGhost => observation.InteractableGhosts
                .Where(x => x.Name.Equals(planner.TargetName ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => MathF.Abs(Vector3.Distance(x.Position, playerPosition) - (planner.TargetDistance ?? Vector3.Distance(x.Position, playerPosition))))
                .Select(x => (Vector3?)x.Position)
                .FirstOrDefault(),
            _ => null,
        };
    }

    private ObservedInteractable? ResolveObservedInteractable(PlannerSnapshot planner, ObservationSnapshot observation)
    {
        var expectedClass = planner.ObjectiveKind switch
        {
            PlannerObjectiveKind.RequiredInteractable => InteractableClass.Required,
            PlannerObjectiveKind.CombatFriendlyInteractable => InteractableClass.CombatFriendly,
            PlannerObjectiveKind.ExpendableInteractable => InteractableClass.Expendable,
            PlannerObjectiveKind.OptionalInteractable => InteractableClass.Optional,
            PlannerObjectiveKind.TreasureCoffer => InteractableClass.TreasureCoffer,
            _ => (InteractableClass?)null,
        };

        if (!expectedClass.HasValue)
            return null;

        var candidates = observation.LiveInteractables
            .Where(x => x.Name.Equals(planner.TargetName ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                && x.Classification == expectedClass.Value);

        if (planner.TargetDistance.HasValue)
        {
            return candidates
                .OrderBy(x => MathF.Abs(Vector3.Distance(x.Position, objectTable.LocalPlayer?.Position ?? x.Position) - planner.TargetDistance.Value))
                .FirstOrDefault();
        }

        return candidates.FirstOrDefault();
    }

    private IGameObject? ResolveGameObject(ObservedInteractable observedInteractable)
    {
        foreach (var gameObject in objectTable)
        {
            if (gameObject is null || !gameObject.IsTargetable)
                continue;

            if (observedInteractable.GameObjectId != 0 && gameObject.GameObjectId == observedInteractable.GameObjectId)
                return gameObject;
        }

        return null;
    }

    private IGameObject? ResolveGameObject(ObservedMonster observedMonster)
    {
        foreach (var gameObject in objectTable)
        {
            if (gameObject is null || !gameObject.IsTargetable)
                continue;

            if (observedMonster.GameObjectId != 0 && gameObject.GameObjectId == observedMonster.GameObjectId)
                return gameObject;
        }

        return null;
    }

    private unsafe bool TryUseGroundTargetAction(uint actionId, Vector3 targetPosition)
    {
        try
        {
            var actionManager = ActionManager.Instance();
            if (actionManager == null)
                return false;

            var position = targetPosition;
            actionManager->UseActionLocation(ActionType.Action, actionId, 0xE0000000, &position, 0xFFFF);
            return true;
        }
        catch (Exception ex)
        {
            log?.Warning(ex, $"[ADS] Ground-target action failed for action {actionId}.");
            return false;
        }
    }

    private unsafe bool TryUseTargetedAction(uint actionId, IGameObject gameObject)
    {
        try
        {
            var actionManager = ActionManager.Instance();
            if (actionManager == null)
                return false;

            targetManager.Target = gameObject;
            actionManager->UseAction(ActionType.Action, actionId);
            return true;
        }
        catch (Exception ex)
        {
            log?.Warning(ex, $"[ADS] Targeted action failed for {gameObject.Name.TextValue} using action {actionId}.");
            return false;
        }
    }

    private unsafe bool TryInteractWithObject(IGameObject gameObject)
    {
        try
        {
            targetManager.Target = gameObject;

            var targetSystem = TargetSystem.Instance();
            if (targetSystem == null)
                return false;

            var nativeObject = (GameObject*)gameObject.Address;
            if (nativeObject == null)
                return false;

            targetSystem->InteractWithObject(nativeObject, true);
            return true;
        }
        catch (Exception ex)
        {
            log?.Warning(ex, $"[ADS] Direct interact failed for {gameObject.Name.TextValue}.");
            return false;
        }
    }

    private void StopMovementAssists()
    {
        StopNavigationIfNeeded();
        movementTargetGameObjectId = 0;
        mapFlagNavigationActive = false;
    }

    private void StopNavigationIfNeeded()
    {
        if (!navigationActive)
            return;

        TrySendCommand("/vnav stop");
        navigationActive = false;
    }

    private bool TrySendCommand(string command)
    {
        try
        {
            return commandManager.ProcessCommand(command);
        }
        catch (Exception ex)
        {
            log?.Warning(ex, $"[ADS] Command failed: {command}");
            return false;
        }
    }

    private void SetPhase(ExecutionPhase phase, string status)
    {
        if (phase != CurrentPhase)
            log?.Information($"[ADS] Execution phase -> {phase}: {status}");

        CurrentPhase = phase;
        LastStatus = status;
    }
}
