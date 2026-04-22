using ADS.Models;
using System.Globalization;
using System.Numerics;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
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
    private const float TreasureDoorFollowThroughDistance = 10.0f;
    private const float TreasureDoorFollowThroughArrivalRange = 2.0f;
    private const uint PraetoriumTerritoryTypeId = 1044;
    private const uint PraetoriumMagitekCannonActionId = 1128;
    private const uint PraetoriumPhotonStreamActionId = 1129;
    private const float MountedCombatClusterRadius = 6.0f;
    private const float PhotonStreamFrontDotThreshold = 0.5f;
    private const float PraetoriumTrailingPackDotThreshold = 0.65f;
    private const float PraetoriumRearTargetMaxHorizontalDistance = 35.0f;
    private const float PraetoriumRearTargetMaxVerticalDelta = 10.0f;
    private const float RecoveryGhostRetireRadius = 8.0f;
    private const float RecoveryClusterArrivalRange = RecoveryGhostRetireRadius;
    private const float RecoveryTargetSimilarityRadius = RecoveryGhostRetireRadius;
    private const float CloseRangeInteractFallbackHorizontalDistance = 2.5f;
    private const float CloseRangeInteractFallbackVerticalCap = 4.0f;
    private const float CloseRangeInteractFallbackProgressMargin = 0.2f;
    private const float TreasureCofferProgressMargin = 2.0f;
    //private const float LeaveTreasureSweepHorizontalRange = 20.0f;
    private const float LeaveTreasureSweepHorizontalRange = 50.0f;  //this might be better range i saw it skip a few times in a relatively normal boss arena.
    private const float LeaveTreasureSweepVerticalCap = 6.0f;
    private const float ManualDestinationSatisfiedByProgressionRadius = 8.0f;
    private const float InteractableRetryIdentityPositionBucketSize = 5.0f;
    private static readonly TimeSpan InteractAttemptCooldown = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan CloseRangeInteractFallbackNoProgressTimeout = TimeSpan.FromSeconds(3.0);
    private static readonly TimeSpan RequiredInteractionRetryDelay = TimeSpan.FromSeconds(2.5);
    private static readonly TimeSpan ForceMarchFollowThroughDuration = TimeSpan.FromSeconds(8.0);
    private static readonly TimeSpan NavigationRetryCooldown = TimeSpan.FromSeconds(2.0);
    private static readonly TimeSpan MapFlagNavigationRetryCooldown = TimeSpan.FromSeconds(6.0);
    private static readonly TimeSpan MountedCombatAttemptCooldown = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan RecoveryTruthSettleDelay = TimeSpan.FromSeconds(1.0);
    private static readonly TimeSpan ProgressionInteractResultSettleDelay = TimeSpan.FromSeconds(6.0);
    private static readonly TimeSpan TreasureCofferNoProgressTimeout = TimeSpan.FromSeconds(30.0);
    private static readonly TimeSpan TreasureCofferMaxNavigationDuration = TimeSpan.FromSeconds(75.0);
    private static readonly TimeSpan LeaveUiRetryCooldown = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan LeaveLootDistributionDelay = TimeSpan.FromSeconds(10.0);
    private static readonly TimeSpan LeaveTreasureSweepSettleDelay = TimeSpan.FromSeconds(2.0);
    private static readonly TimeSpan LivePartyDamageProgressWindow = TimeSpan.FromSeconds(4.0);
    private const float LivePartyDamageProgressRange = 35.0f;

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
    private readonly HashSet<uint> loggedMountedCombatActionMounts = [];
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
    private DungeonFrontierPoint? pendingTreasureDoorTransitionPoint;
    private DungeonFrontierPoint? pendingSatisfiedManualDestination;
    private string? pendingSatisfiedManualInteractableKey;
    private DateTime pendingProgressionInteractResultUntilUtc;
    private DateTime pendingProgressionInteractAfterWaitUntilUtc;
    private int pendingRequiredInteractionAttemptsSent;
    private DungeonFrontierPoint? committedForceMarchManualDestination;
    private DateTime committedForceMarchManualDestinationUntilUtc;
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
    private DateTime nextLeaveUiAttemptUtc;
    private DateTime leaveLootDistributionWaitUntilUtc;
    private DateTime leaveTreasureSweepClearSinceUtc;
    private bool leaveTreasureInteractionSent;
    private readonly Dictionary<ulong, uint> recentVisiblePartyMemberHp = [];
    private readonly Dictionary<ulong, uint> recentNearbyMonsterHp = [];
    private DateTime livePartyDamageProgressUntilUtc;
    private string lastMountedCombatYieldObjective = string.Empty;
    private string lastMountedCombatPhotonFallbackKey = string.Empty;
    private string lastMountedCombatRearPreferenceKey = string.Empty;
    private string lastMountedCombatAggressionModeKey = string.Empty;
    private string lastMountedCombatNoMagitekCannonResolutionKey = string.Empty;
    private string lastPraetoriumMagitekArmorCameraIndependentInteractLogKey = string.Empty;
    private readonly HashSet<string> loggedLiveTargetNavigationModes = [];

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
    private readonly record struct MountedCombatTargetResolution(MountedCombatAction Action, MountedCombatTarget Target, bool UseGroundTarget, string TargetSummary, bool UsedRearPreference);

    public OwnershipMode CurrentMode { get; private set; } = OwnershipMode.Idle;
    public ExecutionPhase CurrentPhase { get; private set; } = ExecutionPhase.Idle;
    public string LastStatus { get; private set; } = "Idle.";

    public bool IsOwned
        => CurrentMode is OwnershipMode.OwnedStartOutside or OwnershipMode.OwnedStartInside or OwnershipMode.OwnedResumeInside or OwnershipMode.Leaving;

    public bool StartDutyFromOutside()
    {
        ClearInteractableCommitment();
        ClearCommittedForceMarchManualDestination();
        CurrentMode = OwnershipMode.OwnedStartOutside;
        SetPhase(ExecutionPhase.OutsideQueue, "Queued outside start. ADS will claim ownership when you enter instanced duty.");
        return true;
    }

    public bool StartDutyFromInside(DutyContextSnapshot context)
    {
        ClearInteractableCommitment();
        ClearCommittedForceMarchManualDestination();
        if (!context.InInstancedDuty)
        {
            CurrentMode = OwnershipMode.Idle;
            SetPhase(
                ExecutionPhase.Idle,
                "Start inside requires being inside instanced duty.");
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
        ClearCommittedForceMarchManualDestination();
        if (!context.InInstancedDuty)
        {
            CurrentMode = OwnershipMode.Idle;
            SetPhase(
                ExecutionPhase.Idle,
                "Resume requires being inside instanced duty.");
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
        if (!context.InInstancedDuty)
        {
            SetPhase(CurrentPhase, "Leave requires being inside duty.");
            return false;
        }

        StopMovementAssists();
        ClearInteractableCommitment();
        ClearCommittedForceMarchManualDestination();
        ResetLeaveState();
        CurrentMode = OwnershipMode.Leaving;
        SetPhase(ExecutionPhase.LeavingDuty, "Leave requested. Clearing nearby treasure before duty exit.");
        return true;
    }

    public void Stop(DutyContextSnapshot context)
    {
        StopMovementAssists();
        ClearInteractableCommitment();
        ClearCommittedForceMarchManualDestination();
        ResetRecoveryHold();
        ResetLeaveState();
        CurrentMode = context.InInstancedDuty ? OwnershipMode.Observing : OwnershipMode.Idle;
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
        ClearCommittedForceMarchManualDestination();
        ResetRecoveryHold();
        ResetLeaveState();
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
            ClearCommittedForceMarchManualDestination();
            ResetLeaveState();
            CurrentMode = OwnershipMode.Idle;
            SetPhase(ExecutionPhase.Idle, "ADS disabled.");
            return;
        }

        switch (CurrentMode)
        {
            case OwnershipMode.OwnedStartOutside:
                if (!context.InInstancedDuty)
                {
                    StopMovementAssists();
                    ClearInteractableCommitment();
                    ClearCommittedForceMarchManualDestination();
                    SetPhase(ExecutionPhase.OutsideQueue, "Waiting to enter instanced duty from outside.");
                    return;
                }

                UpdateOwnedPhase(context, planner, observation, $"Owned after outside start in {context.CurrentDuty?.EnglishName}.");
                return;

            case OwnershipMode.OwnedStartInside:
            case OwnershipMode.OwnedResumeInside:
                if (!context.InInstancedDuty)
                {
                    StopMovementAssists();
                    ClearInteractableCommitment();
                    ClearCommittedForceMarchManualDestination();
                    ResetLeaveState();
                    CurrentMode = OwnershipMode.Idle;
                    SetPhase(ExecutionPhase.Idle, "Duty ended; ADS ownership released.");
                    return;
                }

                UpdateOwnedPhase(context, planner, observation, $"Owned in {context.CurrentDuty?.EnglishName}.");
                return;

            case OwnershipMode.Leaving:
                if (!context.InInstancedDuty)
                {
                    StopMovementAssists();
                    ClearInteractableCommitment();
                    ClearCommittedForceMarchManualDestination();
                    ResetLeaveState();
                    CurrentMode = OwnershipMode.Idle;
                    SetPhase(ExecutionPhase.Idle, "Duty exit detected; ADS ownership released.");
                    return;
                }

                UpdateLeaveDuty(context, observation);
                return;

            case OwnershipMode.Failed:
                if (!context.InInstancedDuty)
                {
                    StopMovementAssists();
                    ClearInteractableCommitment();
                    ClearCommittedForceMarchManualDestination();
                    ResetLeaveState();
                    CurrentMode = OwnershipMode.Idle;
                    SetPhase(ExecutionPhase.Idle, "Failure state cleared outside duty.");
                    return;
                }

                SetPhase(ExecutionPhase.Failure, "ADS is in a failure state until the current unsupported ownership context clears.");
                return;
        }

        if (context.InInstancedDuty)
        {
            StopMovementAssists();
            ClearInteractableCommitment();
            ClearCommittedForceMarchManualDestination();
            ResetLeaveState();
            CurrentMode = OwnershipMode.Observing;
            SetPhase(ExecutionPhase.ObservingOnly, "Observing only; ADS does not currently own this duty.");
            return;
        }

        StopMovementAssists();
        ClearInteractableCommitment();
        ClearCommittedForceMarchManualDestination();
        ResetLeaveState();
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
                TryRetirePendingSatisfiedManualDestination(pendingProgressionInteractable, objectTable.LocalPlayer?.Position);
                ClearInteractableCommitment();
            }

            ResetRecoveryHold();
            StopMovementAssists();
            SetPhase(ExecutionPhase.TransitionHold, $"{prefix} Waiting for safe post-transition duty truth before advancing.");
            return;
        }

        if (TryHoldPendingProgressionInteractResult(context, planner, observation, prefix))
            return;

        if (IsForceMarchPlannerObjective(planner.ObjectiveKind)
            || committedForceMarchManualDestination is not null)
        {
            TryFirePraetoriumMountedCombatAction(context, observation, prefix, preserveMovement: true);
        }

        if (TryAdvanceCommittedForceMarchManualDestination(context, planner, prefix))
            return;

        if ((context.InCombat || planner.Mode == PlannerMode.Combat)
            && !ShouldBypassCombatHold(context, planner))
        {
            ResetRecoveryHold();
            StopMovementAssists();
            SetPhase(ExecutionPhase.CombatHold, $"{prefix} Combat is active, so ADS is holding progression until combat clears.");
            return;
        }

        if (TryAdvancePraetoriumMountedCombat(context, planner, observation, prefix))
            return;

        if (planner.Mode == PlannerMode.Recovery)
        {
            TryAdvanceRecoveryObjective(planner, observation, $"{prefix}");
            return;
        }

        ResetRecoveryHold();

        if (planner.Mode == PlannerMode.Progression)
        {
            var plannerInteractable = IsInteractablePlannerObjective(planner.ObjectiveKind)
                ? ResolveObservedInteractable(planner, observation)
                : null;
            var committedInteractable = ResolveCommittedInteractable(context, observation);
            if (committedInteractable is not null)
            {
                if (plannerInteractable is not null
                    && !IsEquivalentProgressionInteractable(plannerInteractable, committedInteractable)
                    && ShouldSwitchToPlannerInteractable(
                        context,
                        committedInteractable,
                        committedInteractableObjectiveKind,
                        plannerInteractable,
                        planner.ObjectiveKind,
                        out var switchReason))
                {
                    CommitInteractable(plannerInteractable, planner.ObjectiveKind);
                    TryAdvanceInteractableObjective(context, plannerInteractable, $"{prefix} {switchReason}");
                    return;
                }

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
                    or PlannerObjectiveKind.TreasureDoor
                    or PlannerObjectiveKind.TreasureCoffer)
                {
                    var replannedInteractable = plannerInteractable;
                    if (replannedInteractable is not null && !IsEquivalentProgressionInteractable(replannedInteractable, committedInteractable))
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

            if (planner.ObjectiveKind is PlannerObjectiveKind.Frontier
                or PlannerObjectiveKind.MapXzDestination
                or PlannerObjectiveKind.XyzDestination
                or PlannerObjectiveKind.MapXzForceMarchDestination
                or PlannerObjectiveKind.XyzForceMarchDestination)
            {
                TryAdvanceFrontierObjective(context, planner, $"{prefix}");
                return;
            }

            if (planner.ObjectiveKind is PlannerObjectiveKind.RequiredInteractable
                or PlannerObjectiveKind.CombatFriendlyInteractable
                or PlannerObjectiveKind.ExpendableInteractable
                or PlannerObjectiveKind.OptionalInteractable
                or PlannerObjectiveKind.TreasureDoor
                or PlannerObjectiveKind.TreasureCoffer)
            {
                var selectedInteractable = plannerInteractable;
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
            LogLiveTargetNavigation("monster", gameObject.GameObjectId, observedMonster.Name, playerPosition.Value, gameObject.Position, targetDistance, mountedCombat: false);
            TryBeginNavigation(gameObject.GameObjectId, gameObject.Position);
            SetPhase(
                ExecutionPhase.NavigatingToMonsterObjective,
                $"{prefix} Navigating toward {objectiveLabel} {observedMonster.Name} ({targetDistance:0.0}y) using the live target position as the navmesh destination with a close combat arrival target.");
            return;
        }

        StopMovementAssists();
        SetPhase(
            ExecutionPhase.ReadyForMonsterObjective,
            $"{prefix} Positioned near {objectiveLabel} {observedMonster.Name} ({targetDistance:0.0}y). Waiting for combat tooling / aggro to take over.");
    }

    private bool TryAdvancePraetoriumMountedCombat(DutyContextSnapshot context, PlannerSnapshot planner, ObservationSnapshot observation, string prefix)
    {
        if (!IsPraetoriumMountedCombatContext(context))
        {
            lastMountedCombatYieldObjective = string.Empty;
            lastMountedCombatPhotonFallbackKey = string.Empty;
            lastMountedCombatRearPreferenceKey = string.Empty;
            lastMountedCombatAggressionModeKey = string.Empty;
            lastMountedCombatNoMagitekCannonResolutionKey = string.Empty;
            return false;
        }

        if (!TryBuildPraetoriumMountedCombatState(context, observation, out var localPlayerPosition, out var mountedActions, out var targets))
            return false;

        if (IsForceMarchPlannerObjective(planner.ObjectiveKind))
        {
            var objectiveKey = planner.TargetName ?? planner.Objective;
            if (!string.Equals(lastMountedCombatYieldObjective, objectiveKey, StringComparison.Ordinal))
            {
                lastMountedCombatYieldObjective = objectiveKey;
                log?.Information(
                    $"[ADS] Praetorium mounted combat is yielding to planner force-march objective {objectiveKey} so ADS keeps honoring the authored bypass contract instead of stopping to clear incidental mounted trash.");
            }

            return false;
        }

        lastMountedCombatYieldObjective = string.Empty;

        if (TryFireReadyMountedCombatAction(localPlayerPosition, mountedActions, targets, prefix, preserveMovement: false))
            return true;

        if (AnyTargetsInMountedCombatRange(localPlayerPosition, mountedActions, targets))
        {
            StopMovementAssists();
            SetPhase(
                ExecutionPhase.MountedDutyCombat,
                $"{prefix} Mounted Praetorium combat is holding near {targets.Count} live enemy target(s) while the current mount weapons cool down.");
            return true;
        }

        var approachTarget = targets
            .OrderBy(x => x.Distance)
            .FirstOrDefault();
        LogLiveTargetNavigation("mounted-combat", approachTarget.GameObject.GameObjectId, approachTarget.Observed.Name, localPlayerPosition, approachTarget.GameObject.Position, approachTarget.Distance, mountedCombat: true);
        TryBeginNavigation(approachTarget.GameObject.GameObjectId, approachTarget.GameObject.Position);
        SetPhase(
            ExecutionPhase.MountedDutyCombat,
            $"{prefix} Mounted Praetorium combat is moving toward {approachTarget.Observed.Name} ({approachTarget.Distance:0.0}y) using the live target position as the navmesh destination to enter mount weapon range while no force-march objective is active.");
        return true;
    }

    private bool TryFirePraetoriumMountedCombatAction(
        DutyContextSnapshot context,
        ObservationSnapshot observation,
        string prefix,
        bool preserveMovement)
    {
        return TryBuildPraetoriumMountedCombatState(context, observation, out var localPlayerPosition, out var mountedActions, out var targets)
               && TryFireReadyMountedCombatAction(localPlayerPosition, mountedActions, targets, prefix, preserveMovement);
    }

    private bool TryBuildPraetoriumMountedCombatState(
        DutyContextSnapshot context,
        ObservationSnapshot observation,
        out Vector3 localPlayerPosition,
        out MountedCombatAction[] mountedActions,
        out List<MountedCombatTarget> targets)
    {
        localPlayerPosition = default;
        mountedActions = [];
        targets = [];

        if (!IsPraetoriumMountedCombatContext(context))
            return false;

        var localPlayer = objectTable.LocalPlayer;
        if (localPlayer is null)
            return false;

        var mountId = localPlayer.CurrentMount?.RowId ?? 0;
        if (!TryGetCurrentMountCombatActions(mountId, out mountedActions))
            return false;

        var playerPosition = localPlayer.Position;
        localPlayerPosition = playerPosition;
        targets = observation.LiveMonsters
            .Where(x => MatchesCurrentMap(context, x.MapId))
            .Select(x => (Observed: x, GameObject: ResolveGameObject(x)))
            .Where(x => x.GameObject is not null && x.GameObject.IsTargetable)
            .Select(x => new MountedCombatTarget(x.Observed, x.GameObject!, Vector3.Distance(playerPosition, x.GameObject!.Position)))
            .ToList();
        return targets.Count > 0;
    }

    private bool TryFireReadyMountedCombatAction(
        Vector3 localPlayerPosition,
        IReadOnlyCollection<MountedCombatAction> mountedActions,
        IReadOnlyCollection<MountedCombatTarget> targets,
        string prefix,
        bool preserveMovement)
    {
        var readyActions = mountedActions
            .Where(x => IsMountedCombatActionReady(x.ActionId))
            .OrderBy(x => GetMountedCombatPriority(x.ActionId))
            .ThenBy(x => x.ActionId)
            .ToArray();

        var magitekCannon = readyActions.FirstOrDefault(x => x.ActionId == PraetoriumMagitekCannonActionId);
        if (magitekCannon.ActionId != 0)
        {
            LogMountedCombatAggressionMode(targets.Count, preserveMovement);
            if (TryResolveMountedCombatTarget(localPlayerPosition, magitekCannon, targets, out var magitekResolution))
            {
                lastMountedCombatPhotonFallbackKey = string.Empty;
                lastMountedCombatNoMagitekCannonResolutionKey = string.Empty;
                return TryExecuteMountedCombatResolution(magitekResolution, prefix, preserveMovement);
            }

            LogMountedCombatNoMagitekCannonResolution(localPlayerPosition, magitekCannon, targets, preserveMovement);
        }

        var photonStream = readyActions.FirstOrDefault(x => x.ActionId == PraetoriumPhotonStreamActionId);
        if (photonStream.ActionId != 0
            && TryResolveMountedCombatTarget(localPlayerPosition, photonStream, targets, out var photonResolution))
        {
            LogMountedCombatPhotonFallback(photonResolution.Target);
            return TryExecuteMountedCombatResolution(photonResolution, prefix, preserveMovement);
        }

        foreach (var action in readyActions.Where(x => x.ActionId != PraetoriumMagitekCannonActionId && x.ActionId != PraetoriumPhotonStreamActionId))
        {
            if (!TryResolveMountedCombatTarget(localPlayerPosition, action, targets, out var resolution))
                continue;

            if (TryExecuteMountedCombatResolution(resolution, prefix, preserveMovement))
            {
                return true;
            }
        }

        return false;
    }

    private bool TryObserveMountedCombatSendWindow(
        MountedCombatAction action,
        string targetName,
        string prefix,
        bool preserveMovement)
    {
        if (DateTime.UtcNow >= nextMountedCombatAttemptUtc)
            return true;

        if (preserveMovement)
            return false;

        StopMovementAssists();
        SetPhase(
            ExecutionPhase.MountedDutyCombat,
            $"{prefix} Mounted Praetorium combat is active near {targetName}; waiting for the next {action.Name} send window.");
        return false;
    }

    private bool TryExecuteMountedCombatResolution(
        MountedCombatTargetResolution resolution,
        string prefix,
        bool preserveMovement)
    {
        if (!TryObserveMountedCombatSendWindow(resolution.Action, resolution.Target.Observed.Name, prefix, preserveMovement))
            return false;

        var used = resolution.UseGroundTarget
            ? TryUseGroundTargetAction(resolution.Action.ActionId, resolution.Target.GameObject.Position)
            : TryUseTargetedAction(resolution.Action.ActionId, resolution.Target.GameObject);
        if (!used)
            return false;

        if (resolution.Action.ActionId == PraetoriumMagitekCannonActionId)
        {
            if (resolution.UsedRearPreference)
                LogMountedCombatRearPreference(resolution.Target);
            else
                lastMountedCombatRearPreferenceKey = string.Empty;

            lastMountedCombatNoMagitekCannonResolutionKey = string.Empty;
        }

        nextMountedCombatAttemptUtc = DateTime.UtcNow + MountedCombatAttemptCooldown;
        ReportMountedCombatAction(prefix, resolution.Action.Name, resolution.Target.Observed.Name, preserveMovement, resolution.TargetSummary);
        return true;
    }

    private bool TryResolveMountedCombatTarget(
        Vector3 localPlayerPosition,
        MountedCombatAction action,
        IReadOnlyCollection<MountedCombatTarget> targets,
        out MountedCombatTargetResolution resolution)
    {
        if (action.ActionId == PraetoriumMagitekCannonActionId)
            return TryGetPreferredMagitekCannonTarget(localPlayerPosition, action, targets, out resolution);

        if (action.ActionId == PraetoriumPhotonStreamActionId)
            return TryGetPreferredPhotonStreamTarget(action, targets, out resolution);

        if (action.TargetArea)
        {
            if (TryGetBestMountedGroundTarget(localPlayerPosition, targets, action, out var groundTarget))
            {
                resolution = new MountedCombatTargetResolution(
                    action,
                    new MountedCombatTarget(groundTarget.Observed, groundTarget.GameObject, groundTarget.Distance),
                    UseGroundTarget: true,
                    $"covering about {groundTarget.ClusterCount} nearby enemy target(s)",
                    UsedRearPreference: false);
                return true;
            }

            resolution = default;
            return false;
        }

        if (TryGetBestMountedTargetForAction(action.ActionId, targets, out var targetedTarget)
            || TryGetNearestMountedCombatTargetInRange(targets, action, out targetedTarget))
        {
            resolution = new MountedCombatTargetResolution(action, targetedTarget, UseGroundTarget: false, $"{targetedTarget.Distance:0.0}y", UsedRearPreference: false);
            return true;
        }

        resolution = default;
        return false;
    }

    private bool TryGetPreferredMagitekCannonTarget(
        Vector3 localPlayerPosition,
        MountedCombatAction action,
        IReadOnlyCollection<MountedCombatTarget> targets,
        out MountedCombatTargetResolution resolution)
    {
        var inEnvelopeTargets = targets
            .Where(target => IsMountedCombatTargetWithinRearPreferenceEnvelope(localPlayerPosition, action, target))
            .OrderBy(target => GetHorizontalDistance(localPlayerPosition, target.GameObject.Position))
            .ThenBy(target => target.Distance)
            .ToArray();
        if (inEnvelopeTargets.Length == 0)
        {
            resolution = default;
            return false;
        }

        var localPlayer = objectTable.LocalPlayer;
        if (localPlayer is not null)
        {
            var rearCandidates = inEnvelopeTargets
                .Where(target => IsMountedCombatTargetBehind(localPlayerPosition, localPlayer.Rotation, target))
                .ToArray();
            var trailingCandidates = inEnvelopeTargets
                .Where(target => IsMountedCombatTargetInTrailingPack(localPlayerPosition, localPlayer.Rotation, target))
                .ToArray();
            var preferredCandidates = rearCandidates.Length > 0 ? rearCandidates : trailingCandidates;

            if (preferredCandidates.Length > 0 && action.TargetArea)
            {
                if (TryGetBestMountedClusterTarget(preferredCandidates, out var preferredTrailingTarget))
                {
                    var horizontalDistance = GetHorizontalDistance(localPlayerPosition, preferredTrailingTarget.GameObject.Position);
                    var verticalDelta = MathF.Abs(preferredTrailingTarget.GameObject.Position.Y - localPlayerPosition.Y);
                    resolution = new MountedCombatTargetResolution(
                        action,
                        new MountedCombatTarget(preferredTrailingTarget.Observed, preferredTrailingTarget.GameObject, preferredTrailingTarget.Distance),
                        UseGroundTarget: true,
                        rearCandidates.Length > 0
                            ? $"{preferredTrailingTarget.Distance:0.0}y strict rear trailing-pack preference (XZ {horizontalDistance:0.0}y, Y {verticalDelta:0.0}y, cluster {preferredTrailingTarget.ClusterCount})"
                            : $"{preferredTrailingTarget.Distance:0.0}y aggressive trailing-pack preference (XZ {horizontalDistance:0.0}y, Y {verticalDelta:0.0}y, cluster {preferredTrailingTarget.ClusterCount})",
                        UsedRearPreference: true);
                    return true;
                }
            }
            else if (preferredCandidates.Length > 0
                     && (TryGetBestMountedTargetForAction(action.ActionId, preferredCandidates, out var targetedRearTarget)
                         || TryGetNearestMountedCombatTargetInRange(preferredCandidates, action, out targetedRearTarget)))
            {
                var horizontalDistance = GetHorizontalDistance(localPlayerPosition, targetedRearTarget.GameObject.Position);
                var verticalDelta = MathF.Abs(targetedRearTarget.GameObject.Position.Y - localPlayerPosition.Y);
                resolution = new MountedCombatTargetResolution(
                    action,
                    targetedRearTarget,
                    UseGroundTarget: false,
                    rearCandidates.Length > 0
                        ? $"{targetedRearTarget.Distance:0.0}y strict rear trailing-pack preference (XZ {horizontalDistance:0.0}y, Y {verticalDelta:0.0}y)"
                        : $"{targetedRearTarget.Distance:0.0}y aggressive trailing-pack preference (XZ {horizontalDistance:0.0}y, Y {verticalDelta:0.0}y)",
                    UsedRearPreference: true);
                return true;
            }
        }

        if (action.TargetArea)
        {
            var directShotTarget = inEnvelopeTargets[0];
            var horizontalDistance = GetHorizontalDistance(localPlayerPosition, directShotTarget.GameObject.Position);
            var verticalDelta = MathF.Abs(directShotTarget.GameObject.Position.Y - localPlayerPosition.Y);
            resolution = new MountedCombatTargetResolution(
                action,
                directShotTarget,
                UseGroundTarget: true,
                $"{directShotTarget.Distance:0.0}y aggressive direct shot on refresh (XZ {horizontalDistance:0.0}y, Y {verticalDelta:0.0}y)",
                UsedRearPreference: false);
            return true;
        }
        else if (TryGetBestMountedTargetForAction(action.ActionId, inEnvelopeTargets, out var targetedTarget)
                 || TryGetNearestMountedCombatTargetInRange(inEnvelopeTargets, action, out targetedTarget))
        {
            var horizontalDistance = GetHorizontalDistance(localPlayerPosition, targetedTarget.GameObject.Position);
            var verticalDelta = MathF.Abs(targetedTarget.GameObject.Position.Y - localPlayerPosition.Y);
            resolution = new MountedCombatTargetResolution(
                action,
                targetedTarget,
                UseGroundTarget: false,
                $"{targetedTarget.Distance:0.0}y aggressive direct shot on refresh (XZ {horizontalDistance:0.0}y, Y {verticalDelta:0.0}y)",
                UsedRearPreference: false);
            return true;
        }

        resolution = default;
        return false;
    }

    private static bool TryGetBestMountedClusterTarget(
        IReadOnlyCollection<MountedCombatTarget> targets,
        out MountedCombatClusterTarget selectedTarget)
    {
        var clusterRadiusSquared = MountedCombatClusterRadius * MountedCombatClusterRadius;
        var bestTarget = default(MountedCombatClusterTarget);
        var found = false;
        foreach (var target in targets)
        {
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

    private bool TryGetPreferredPhotonStreamTarget(
        MountedCombatAction action,
        IReadOnlyCollection<MountedCombatTarget> targets,
        out MountedCombatTargetResolution resolution)
    {
        var localPlayer = objectTable.LocalPlayer;
        if (localPlayer is null)
        {
            resolution = default;
            return false;
        }

        var frontTargets = targets
            .Where(target => IsMountedCombatTargetInFront(localPlayer.Position, localPlayer.Rotation, target))
            .ToArray();
        if (frontTargets.Length == 0)
        {
            resolution = default;
            return false;
        }

        if (action.TargetArea)
        {
            if (TryGetNearestMountedGroundTargetInRange(frontTargets, action, out var groundTarget))
            {
                resolution = new MountedCombatTargetResolution(
                    action,
                    groundTarget,
                    UseGroundTarget: true,
                    $"{groundTarget.Distance:0.0}y front-facing fallback",
                    UsedRearPreference: false);
                return true;
            }
        }
        else if (TryGetBestMountedTargetForAction(action.ActionId, frontTargets, out var targetedTarget)
                 || TryGetNearestMountedCombatTargetInRange(frontTargets, action, out targetedTarget))
        {
            resolution = new MountedCombatTargetResolution(action, targetedTarget, UseGroundTarget: false, $"{targetedTarget.Distance:0.0}y front-facing fallback", UsedRearPreference: false);
            return true;
        }

        resolution = default;
        return false;
    }

    private void LogMountedCombatPhotonFallback(MountedCombatTarget target)
    {
        var fallbackKey = $"{target.GameObject.GameObjectId}:{target.Observed.Name}";
        if (string.Equals(lastMountedCombatPhotonFallbackKey, fallbackKey, StringComparison.Ordinal))
            return;

        lastMountedCombatPhotonFallbackKey = fallbackKey;
        log?.Information(
            $"[ADS] Mounted Praetorium combat is falling back to 1129 Photon Stream on {target.Observed.Name} because 1128 Magitek Cannon is not currently usable; fallback remains front-facing only.");
    }

    private void LogMountedCombatRearPreference(MountedCombatTarget target)
    {
        var preferenceKey = $"{target.GameObject.GameObjectId}:{target.Observed.Name}";
        if (string.Equals(lastMountedCombatRearPreferenceKey, preferenceKey, StringComparison.Ordinal))
            return;

        lastMountedCombatRearPreferenceKey = preferenceKey;
        var localPlayerPosition = objectTable.LocalPlayer?.Position;
        if (!localPlayerPosition.HasValue)
        {
            log?.Information($"[ADS] Mounted Praetorium combat preferred aggressive trailing 1128 target {target.Observed.Name}.");
            return;
        }

        var horizontalDistance = GetHorizontalDistance(localPlayerPosition.Value, target.GameObject.Position);
        var verticalDelta = MathF.Abs(target.GameObject.Position.Y - localPlayerPosition.Value.Y);
        log?.Information($"[ADS] Mounted Praetorium combat preferred aggressive trailing 1128 target {target.Observed.Name} (XZ {horizontalDistance:0.0}y, Y {verticalDelta:0.0}y).");
    }

    private void LogMountedCombatAggressionMode(int targetCount, bool preserveMovement)
    {
        var mode = preserveMovement ? "force-march" : "cleanup";
        var aggressionKey = $"{mode}:{targetCount}";
        if (string.Equals(lastMountedCombatAggressionModeKey, aggressionKey, StringComparison.Ordinal))
            return;

        lastMountedCombatAggressionModeKey = aggressionKey;
        log?.Information(
            preserveMovement
                ? $"[ADS] Mounted Praetorium combat entered aggressive 1128 force-march mode with {targetCount} live target(s). ADS will keep firing 1128 on refresh while preserving authored movement."
                : $"[ADS] Mounted Praetorium combat entered aggressive 1128 cleanup mode with {targetCount} live target(s). ADS will keep firing 1128 on refresh before considering 1129.");
    }

    private void LogMountedCombatNoMagitekCannonResolution(
        Vector3 localPlayerPosition,
        MountedCombatAction action,
        IReadOnlyCollection<MountedCombatTarget> targets,
        bool preserveMovement)
    {
        var localPlayerRotation = objectTable.LocalPlayer?.Rotation ?? 0f;
        var inEnvelopeTargets = targets
            .Where(target => IsMountedCombatTargetWithinRearPreferenceEnvelope(localPlayerPosition, action, target))
            .ToArray();
        var trailingTargets = inEnvelopeTargets
            .Where(target => IsMountedCombatTargetInTrailingPack(localPlayerPosition, localPlayerRotation, target))
            .ToArray();
        var resolutionKey = $"{preserveMovement}:{targets.Count}:{inEnvelopeTargets.Length}:{trailingTargets.Length}";
        if (string.Equals(lastMountedCombatNoMagitekCannonResolutionKey, resolutionKey, StringComparison.Ordinal))
            return;

        lastMountedCombatNoMagitekCannonResolutionKey = resolutionKey;
        log?.Information(
            preserveMovement
                ? $"[ADS] Mounted Praetorium combat kept force-march 1128 priority but found no viable send target this tick (live={targets.Count}, in-envelope={inEnvelopeTargets.Length}, trailing={trailingTargets.Length}). ADS will retry on the next refresh without dropping authored movement."
                : $"[ADS] Mounted Praetorium combat kept 1128 cleanup priority but found no viable send target this tick (live={targets.Count}, in-envelope={inEnvelopeTargets.Length}, trailing={trailingTargets.Length}). ADS will retry on the next refresh before falling back to movement.");
    }

    private static int GetMountedCombatPriority(uint actionId)
        => actionId switch
        {
            PraetoriumMagitekCannonActionId => 0,
            PraetoriumPhotonStreamActionId => 1,
            _ => 2,
        };

    private void LogLiveTargetNavigation(
        string navigationMode,
        ulong gameObjectId,
        string targetName,
        Vector3 playerPosition,
        Vector3 targetPosition,
        float targetDistance,
        bool mountedCombat)
    {
        var logKey = $"{navigationMode}:{gameObjectId}";
        if (!loggedLiveTargetNavigationModes.Add(logKey))
            return;

        log?.Information(
            $"[ADS] Live-target navigation mode {navigationMode} is using {targetName} at {FormatVector(targetPosition)} as the /vnav destination instead of a generated stand-off point; player {FormatVector(playerPosition)}, distance {targetDistance:0.0}y, mountedCombat={mountedCombat}.");
    }

    private void ReportMountedCombatAction(
        string prefix,
        string actionName,
        string targetName,
        bool preserveMovement,
        string targetSummary)
    {
        if (preserveMovement)
        {
            log?.Information(
                $"[ADS] Mounted Praetorium combat opportunistically fired {actionName} at {targetName} ({targetSummary}) while force-march navigation remains active.");
            return;
        }

        SetPhase(
            ExecutionPhase.MountedDutyCombat,
            $"{prefix} Mounted Praetorium combat fired {actionName} at {targetName}, {targetSummary}.");
    }

    private bool AnyTargetsInMountedCombatRange(
        Vector3 localPlayerPosition,
        IEnumerable<MountedCombatAction> mountedActions,
        IReadOnlyCollection<MountedCombatTarget> targets)
    {
        return mountedActions.Any(action => TryResolveMountedCombatTarget(localPlayerPosition, action, targets, out _));
    }

    private static float GetMountedCombatApproachRange(IEnumerable<MountedCombatAction> mountedActions)
    {
        var maxRange = mountedActions
            .Select(x => x.Range > 0f ? x.Range : 50f)
            .DefaultIfEmpty(50f)
            .Max();
        return MathF.Max(PreferredMonsterArrivalRange, maxRange * 0.85f);
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
        var wantsMapXzDestination = planner.ObjectiveKind is PlannerObjectiveKind.MapXzDestination or PlannerObjectiveKind.MapXzForceMarchDestination;
        var wantsXyzDestination = planner.ObjectiveKind is PlannerObjectiveKind.XyzDestination or PlannerObjectiveKind.XyzForceMarchDestination;
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
        if (frontierPoint.AllowCombatBypass
            && planner.ObjectiveKind is PlannerObjectiveKind.MapXzForceMarchDestination or PlannerObjectiveKind.XyzForceMarchDestination)
        {
            RefreshCommittedForceMarchManualDestination(frontierPoint, "planner selected the authored force-march handoff");
        }

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

    private bool TryAdvanceCommittedForceMarchManualDestination(DutyContextSnapshot context, PlannerSnapshot planner, string prefix)
    {
        var committedDestination = committedForceMarchManualDestination;
        if (committedDestination is null)
            return false;

        if (planner.Mode == PlannerMode.Progression
            && ShouldRetireCommittedForceMarchForLiveProgression(context, planner.ObjectiveKind))
        {
            RetireCommittedForceMarchManualDestination(
                "LiveProgressionAppeared",
                $"because planner promoted live progression objective {planner.ObjectiveKind} ({planner.TargetName ?? planner.Objective}).");
            return false;
        }

        var now = DateTime.UtcNow;
        if (now >= committedForceMarchManualDestinationUntilUtc)
        {
            if (PlannerStillSelectsForceMarchDestination(planner, committedDestination))
            {
                committedForceMarchManualDestinationUntilUtc = now + ForceMarchFollowThroughDuration;
            }
            else
            {
                RetireCommittedForceMarchManualDestination("TimedOut", "because no live progression handoff appeared before the bounded follow-through window expired.");
                return false;
            }
        }

        var playerPosition = objectTable.LocalPlayer?.Position;
        var navigationPoint = RebuildCommittedForceMarchManualDestination(committedDestination, playerPosition);
        if (playerPosition.HasValue
            && IsFrontierDestinationReached(
                navigationPoint,
                playerPosition.Value,
                navigationPoint.IsManualMapXzDestination,
                navigationPoint.IsManualXyzDestination,
                out _,
                out _,
                out _,
                out _,
                out _))
        {
            if (navigationPoint.IsManualMapXzDestination || navigationPoint.IsManualXyzDestination)
                dungeonFrontierService.MarkVisited(navigationPoint, playerPosition.Value);

            ClearCommittedForceMarchManualDestinationIfMatches(navigationPoint);

            if (planner.Mode == PlannerMode.Progression
                && IsLiveProgressionPlannerObjective(planner.ObjectiveKind))
            {
                return false;
            }

            StopMovementAssists();
            var destinationLabel = navigationPoint.IsManualXyzDestination ? "force-march XYZ destination" : "force-march map XZ destination";
            SetPhase(
                GetFrontierHintPhase(navigationPoint.IsManualMapXzDestination, navigationPoint.IsManualXyzDestination),
                $"{prefix} Reached committed {destinationLabel} {navigationPoint.Name}. ADS ghosted this manual handoff and is waiting for live progression truth.");
            return true;
        }

        TryAdvanceFrontierPoint(
            context,
            navigationPoint,
            navigationPoint.IsManualMapXzDestination,
            navigationPoint.IsManualXyzDestination,
            $"{prefix} Continuing committed force-march manual destination follow-through.");
        return true;
    }

    private void RefreshCommittedForceMarchManualDestination(DungeonFrontierPoint point, string detail)
    {
        var now = DateTime.UtcNow;
        var wasNewCommit = committedForceMarchManualDestination is null
            || !string.Equals(committedForceMarchManualDestination.Key, point.Key, StringComparison.Ordinal);

        committedForceMarchManualDestination = point;
        committedForceMarchManualDestinationUntilUtc = now + ForceMarchFollowThroughDuration;

        if (wasNewCommit)
        {
            log?.Information(
                $"[ADS] Committed force-march {(point.IsManualXyzDestination ? "XYZ" : "map XZ")} destination {point.Name} at {FormatVector(point.Position)} for bounded handoff follow-through because {detail}.");
        }
    }

    private void RetireCommittedForceMarchManualDestination(string reason, string detail)
    {
        var committedDestination = committedForceMarchManualDestination;
        if (committedDestination is null)
            return;

        log?.Information(
            $"[ADS] Retired committed force-march {(committedDestination.IsManualXyzDestination ? "XYZ" : "map XZ")} destination {committedDestination.Name} ({reason}) {detail}");
        ClearCommittedForceMarchManualDestination();
    }

    private void ClearCommittedForceMarchManualDestination()
    {
        committedForceMarchManualDestination = null;
        committedForceMarchManualDestinationUntilUtc = DateTime.MinValue;
        lastMountedCombatYieldObjective = string.Empty;
    }

    private void ClearCommittedForceMarchManualDestinationIfMatches(DungeonFrontierPoint point)
    {
        var committedDestination = committedForceMarchManualDestination;
        if (committedDestination is null)
            return;

        if (string.Equals(committedDestination.Key, point.Key, StringComparison.Ordinal))
            ClearCommittedForceMarchManualDestination();
    }

    private bool PlannerStillSelectsForceMarchDestination(PlannerSnapshot planner, DungeonFrontierPoint point)
    {
        if (!IsForceMarchPlannerObjective(planner.ObjectiveKind))
            return false;

        if (string.Equals(planner.TargetName, point.Name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(planner.Objective, point.Name, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return dungeonFrontierService.CurrentTarget is { AllowCombatBypass: true } currentTarget
               && string.Equals(currentTarget.Key, point.Key, StringComparison.Ordinal);
    }

    private static bool IsForceMarchPlannerObjective(PlannerObjectiveKind objectiveKind)
        => objectiveKind is PlannerObjectiveKind.MapXzForceMarchDestination or PlannerObjectiveKind.XyzForceMarchDestination;

    private static bool ShouldRetireCommittedForceMarchForLiveProgression(DutyContextSnapshot context, PlannerObjectiveKind objectiveKind)
    {
        if (!IsLiveProgressionPlannerObjective(objectiveKind))
            return false;

//        if (context.TerritoryTypeId == PraetoriumTerritoryTypeId //because AI is too stupid to understand
//            && objectiveKind is PlannerObjectiveKind.Monster or PlannerObjectiveKind.BossFightMonster)  //because AI is too stupid to understand
		  if (objectiveKind is PlannerObjectiveKind.Monster or PlannerObjectiveKind.BossFightMonster)
        {
            return false;
        }

        return true;
    }

    private static bool IsLiveProgressionPlannerObjective(PlannerObjectiveKind objectiveKind)
        => objectiveKind is PlannerObjectiveKind.Monster
            or PlannerObjectiveKind.BossFightMonster
            or PlannerObjectiveKind.FollowTarget
            or PlannerObjectiveKind.RequiredInteractable
            or PlannerObjectiveKind.CombatFriendlyInteractable
            or PlannerObjectiveKind.ExpendableInteractable
            or PlannerObjectiveKind.OptionalInteractable
            or PlannerObjectiveKind.TreasureDoor
            or PlannerObjectiveKind.TreasureCoffer;

    private static DungeonFrontierPoint RebuildCommittedForceMarchManualDestination(DungeonFrontierPoint point, Vector3? playerPosition)
    {
        if (!point.UsePlayerYForNavigation || !playerPosition.HasValue)
            return point;

        return new DungeonFrontierPoint
        {
            Key = point.Key,
            Name = point.Name,
            Position = new Vector3(point.Position.X, playerPosition.Value.Y, point.Position.Z),
            LevelRowId = point.LevelRowId,
            MapId = point.MapId,
            Priority = point.Priority,
            MapCoordinates = point.MapCoordinates,
            UsePlayerYForNavigation = point.UsePlayerYForNavigation,
            ManualDestinationKind = point.ManualDestinationKind,
            AllowCombatBypass = point.AllowCombatBypass,
            ArrivalRadiusXz = point.ArrivalRadiusXz,
            ArrivalRadius3d = point.ArrivalRadius3d,
        };
    }

    private static bool IsFrontierDestinationReached(
        DungeonFrontierPoint frontierPoint,
        Vector3 playerPosition,
        bool isMapXzDestination,
        bool isXyzDestination,
        out float targetHorizontalDistance,
        out float targetDistance,
        out float targetVerticalDelta,
        out float arrivalRange,
        out float xyzArrivalRange)
    {
        targetHorizontalDistance = GetHorizontalDistance(frontierPoint.Position, playerPosition);
        targetDistance = Vector3.Distance(frontierPoint.Position, playerPosition);
        targetVerticalDelta = MathF.Abs(frontierPoint.Position.Y - playerPosition.Y);
        arrivalRange = isMapXzDestination && frontierPoint.ArrivalRadiusXz > 0f
            ? frontierPoint.ArrivalRadiusXz
            : PreferredFrontierArrivalRange;
        xyzArrivalRange = isXyzDestination && frontierPoint.ArrivalRadius3d > 0f
            ? frontierPoint.ArrivalRadius3d
            : PreferredFrontierArrivalRange;

        return isXyzDestination
            ? targetDistance <= xyzArrivalRange
            : targetHorizontalDistance <= arrivalRange;
    }

    private void TryAdvanceFrontierPoint(DutyContextSnapshot context, DungeonFrontierPoint frontierPoint, bool isMapXzDestination, bool isXyzDestination, string prefix)
    {
        var playerPosition = objectTable.LocalPlayer?.Position;
        var frontierLabel = dungeonFrontierService.CurrentMode == FrontierMode.HeadingScout
            ? "forward scout"
            : dungeonFrontierService.CurrentMode == FrontierMode.TreasureDungeon
                ? "treasure-dungeon route point"
            : isMapXzDestination
                ? "map XZ destination"
                : isXyzDestination
                    ? "XYZ destination"
                : "map frontier";
        var frontierReason = isMapXzDestination
            ? "because a human-authored Map XZ destination is configured for this no-live-object gap."
            : isXyzDestination
                ? "because a human-authored XYZ destination is configured for this no-live-object gap."
            : dungeonFrontierService.CurrentMode == FrontierMode.TreasureDungeon
                ? "because LootGoblin-derived treasure-dungeon routing data is available for this territory and no live duty objects are currently visible."
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

        var destinationReached = IsFrontierDestinationReached(
            frontierPoint,
            playerPosition.Value,
            isMapXzDestination,
            isXyzDestination,
            out var targetHorizontalDistance,
            out var targetDistance,
            out var targetVerticalDelta,
            out var arrivalRange,
            out var xyzArrivalRange);
        if (!destinationReached)
        {
            var frontierTargetId = BuildFrontierTargetId(frontierPoint);
            var canUseMapFlagNavigation = dungeonFrontierService.CurrentMode == FrontierMode.Label
                                          || (isMapXzDestination && !frontierPoint.UsePlayerYForNavigation);
            var usedMapFlagNavigation = canUseMapFlagNavigation
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

        if (!frontierPoint.AllowCombatBypass
            && (isMapXzDestination || isXyzDestination)
            && TryAdvancePraetoriumOnFootForceMarchHandoff(context, frontierPoint, prefix, playerPosition.Value))
        {
            return;
        }

        if (frontierPoint.AllowCombatBypass && (isMapXzDestination || isXyzDestination))
        {
            ClearCommittedForceMarchManualDestinationIfMatches(frontierPoint);
            SetPhase(
                GetFrontierHintPhase(isMapXzDestination, isXyzDestination),
                isXyzDestination
                    ? $"{prefix} Reached force-march {frontierLabel} {frontierPoint.Name} (3D {targetDistance:0.0}y, XZ {targetHorizontalDistance:0.0}y, Y {targetVerticalDelta:0.0}). ADS ghosted this manual handoff and will not resume it after combat."
                    : $"{prefix} Reached force-march {frontierLabel} {frontierPoint.Name} (XZ {targetHorizontalDistance:0.0}y, Y {targetVerticalDelta:0.0}). ADS ghosted this manual handoff and will not resume it after combat.");
            return;
        }

        SetPhase(
            GetFrontierHintPhase(isMapXzDestination, isXyzDestination),
            isXyzDestination
                ? $"{prefix} Reached {frontierLabel} {frontierPoint.Name} (3D {targetDistance:0.0}y, XZ {targetHorizontalDistance:0.0}y, Y {targetVerticalDelta:0.0}). Waiting for live duty objects or duty completion."
                : $"{prefix} Reached {frontierLabel} {frontierPoint.Name} (XZ {targetHorizontalDistance:0.0}y, Y {targetVerticalDelta:0.0}). Waiting for live duty objects or duty completion.");
    }

    private bool TryAdvancePraetoriumOnFootForceMarchHandoff(
        DutyContextSnapshot context,
        DungeonFrontierPoint frontierPoint,
        string prefix,
        Vector3 playerPosition)
    {
        //if (context.Mounted || context.TerritoryTypeId != PraetoriumTerritoryTypeId) //because AI was too stupid to understand
        if (context.Mounted)
            return false;

        var nextForceMarchDestination = dungeonFrontierService.FindNextForceMarchManualDestinationAfter(context, playerPosition, frontierPoint);
        if (nextForceMarchDestination is null)
            return false;

        RefreshCommittedForceMarchManualDestination(
            nextForceMarchDestination,
            $"the prior authored Praetorium waypoint {frontierPoint.Name} completed and the next authored waypoint is a force-march handoff");
        TryAdvanceFrontierPoint(
            context,
            nextForceMarchDestination,
            nextForceMarchDestination.IsManualMapXzDestination,
            nextForceMarchDestination.IsManualXyzDestination,
            $"{prefix} Continuing directly into the next authored Praetorium force-march handoff.");
        return true;
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

        StageSatisfiedManualDestinationForLiveProgression(context, observedInteractable, playerPosition.Value);

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

        var useCameraIndependentInteract = ShouldUsePraetoriumMagitekArmorCameraIndependentInteract(context, observedInteractable);
        if (useCameraIndependentInteract)
            LogPraetoriumMagitekArmorCameraIndependentInteract(observedInteractable);

        if (TryInteractWithObject(gameObject, cameraBasedInteract: !useCameraIndependentInteract))
        {
            if (observedInteractable.Classification == InteractableClass.TreasureCoffer)
            {
                observationMemoryService.MarkTreasureInteractionSent(observedInteractable);
            }
            else
            {
                var isRequiredLikeInteractable = observedInteractable.Classification is InteractableClass.Required or InteractableClass.TreasureDoor;
                var previousPendingProgressionInteractable = pendingProgressionInteractable;
                var continuingRequiredFollowThrough = isRequiredLikeInteractable
                    && previousPendingProgressionInteractable is not null
                    && IsEquivalentProgressionInteractable(previousPendingProgressionInteractable, observedInteractable);
                pendingProgressionInteractable = observedInteractable;
                pendingProgressionInteractResultUntilUtc = isRequiredLikeInteractable
                    ? now + RequiredInteractionRetryDelay
                    : now + ProgressionInteractResultSettleDelay;
                pendingProgressionInteractAfterWaitUntilUtc = waitAfterInteractSeconds > 0f
                    ? now + TimeSpan.FromSeconds(waitAfterInteractSeconds)
                    : DateTime.MinValue;
                if (pendingProgressionInteractAfterWaitUntilUtc > pendingProgressionInteractResultUntilUtc)
                    pendingProgressionInteractResultUntilUtc = pendingProgressionInteractAfterWaitUntilUtc;
                pendingRequiredInteractionAttemptsSent = isRequiredLikeInteractable
                    ? (continuingRequiredFollowThrough ? pendingRequiredInteractionAttemptsSent + 1 : 1)
                    : 0;
                pendingTreasureDoorTransitionPoint = observedInteractable.Classification == InteractableClass.TreasureDoor
                    ? BuildTreasureDoorFollowThroughPoint(context, observedInteractable, playerPosition.Value)
                    : null;
                if (isRequiredLikeInteractable)
                {
                    var retryIdentity = continuingRequiredFollowThrough && previousPendingProgressionInteractable is not null
                        ? DescribeProgressionInteractableReuse(previousPendingProgressionInteractable, observedInteractable)
                        : "new required follow-through window";
                    log?.Information(
                        $"[ADS] Required interact follow-through on {observedInteractable.Name} sent attempt {pendingRequiredInteractionAttemptsSent}/{RequiredInteractionAttemptLimit} using {retryIdentity}.");
                }
            }

            lastInteractGameObjectId = observedInteractable.GameObjectId;
            nextInteractAttemptUtc = now + InteractAttemptCooldown;
            var interactResult = observedInteractable.Classification switch
            {
                InteractableClass.Required => $"Direct interact sent to {observedInteractable.Name} (required attempt {pendingRequiredInteractionAttemptsSent}/{RequiredInteractionAttemptLimit}).",
                InteractableClass.TreasureDoor => $"Direct interact sent to treasure door {observedInteractable.Name} (attempt {pendingRequiredInteractionAttemptsSent}/{RequiredInteractionAttemptLimit}).",
                InteractableClass.Expendable => $"Direct interact sent to {observedInteractable.Name}; ADS will keep retrying this expendable until it disappears.",
                _ => $"Direct interact sent to {observedInteractable.Name}.",
            };
            if (usingCloseRangeInteractFallback)
            {
                interactResult = $"{interactResult} Close-XZ fallback engaged after {CloseRangeInteractFallbackNoProgressTimeout.TotalSeconds:0}s with no XZ progress (XZ {targetHorizontalDistance:0.0}y, 3D {targetDistance:0.0}y, Y {targetVerticalDelta:0.0}).";
            }
            if (useCameraIndependentInteract)
            {
                interactResult = $"{interactResult} Praetorium Magitek Armor camera-independent interact mode used to avoid zoom/camera obstruction.";
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

    private void StageSatisfiedManualDestinationForLiveProgression(
        DutyContextSnapshot context,
        ObservedInteractable observedInteractable,
        Vector3 playerPosition)
    {
        pendingSatisfiedManualDestination = null;
        pendingSatisfiedManualInteractableKey = null;

        if (observedInteractable.Classification is not (InteractableClass.Required or InteractableClass.CombatFriendly or InteractableClass.Expendable))
            return;

        var frontierPoint = dungeonFrontierService.FindNearbyUnvisitedManualDestination(
            context,
            playerPosition,
            observedInteractable.Position,
            ManualDestinationSatisfiedByProgressionRadius,
            requirePlayerNear: true);
        if (frontierPoint is null)
            return;

        pendingSatisfiedManualDestination = frontierPoint;
        pendingSatisfiedManualInteractableKey = observedInteractable.Key;
    }

    private void TryRetirePendingSatisfiedManualDestination(ObservedInteractable observedInteractable, Vector3? playerPosition)
    {
        var frontierPoint = pendingSatisfiedManualDestination;
        var interactableKey = pendingSatisfiedManualInteractableKey;
        pendingSatisfiedManualDestination = null;
        pendingSatisfiedManualInteractableKey = null;

        if (frontierPoint is null
            || !string.Equals(interactableKey, observedInteractable.Key, StringComparison.Ordinal))
        {
            return;
        }

        var interactableDistanceFromManual = frontierPoint.IsManualXyzDestination
            ? Vector3.Distance(frontierPoint.Position, observedInteractable.Position)
            : GetHorizontalDistance(frontierPoint.Position, observedInteractable.Position);
        var distanceLabel = frontierPoint.IsManualXyzDestination ? "3D" : "XZ";
        var detail = playerPosition.HasValue
            ? frontierPoint.IsManualXyzDestination
                ? $"after staging into nearby live progression interactable {observedInteractable.Name} ({distanceLabel} player {Vector3.Distance(frontierPoint.Position, playerPosition.Value):0.0}y, {distanceLabel} interactable {interactableDistanceFromManual:0.0}y)"
                : $"after staging into nearby live progression interactable {observedInteractable.Name} ({distanceLabel} player {GetHorizontalDistance(frontierPoint.Position, playerPosition.Value):0.0}y, {distanceLabel} interactable {interactableDistanceFromManual:0.0}y)"
            : $"after staging into nearby live progression interactable {observedInteractable.Name} ({distanceLabel} interactable {interactableDistanceFromManual:0.0}y)";
        dungeonFrontierService.RetireManualDestination(frontierPoint, "SatisfiedByLiveProgression", detail);
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

    private static bool IsInteractablePlannerObjective(PlannerObjectiveKind objectiveKind)
        => objectiveKind is PlannerObjectiveKind.RequiredInteractable
            or PlannerObjectiveKind.CombatFriendlyInteractable
            or PlannerObjectiveKind.ExpendableInteractable
            or PlannerObjectiveKind.OptionalInteractable
            or PlannerObjectiveKind.TreasureDoor
            or PlannerObjectiveKind.TreasureCoffer;

    private bool ShouldSwitchToPlannerInteractable(
        DutyContextSnapshot context,
        ObservedInteractable currentInteractable,
        PlannerObjectiveKind currentObjectiveKind,
        ObservedInteractable plannerInteractable,
        PlannerObjectiveKind plannerObjectiveKind,
        out string reason)
    {
        reason = string.Empty;
        var playerPosition = objectTable.LocalPlayer?.Position;
        var currentDistance = playerPosition.HasValue
            ? Vector3.Distance(playerPosition.Value, currentInteractable.Position)
            : (float?)null;
        var currentVerticalDelta = playerPosition.HasValue
            ? MathF.Abs(currentInteractable.Position.Y - playerPosition.Value.Y)
            : (float?)null;
        var plannerDistance = playerPosition.HasValue
            ? Vector3.Distance(playerPosition.Value, plannerInteractable.Position)
            : (float?)null;
        var plannerVerticalDelta = playerPosition.HasValue
            ? MathF.Abs(plannerInteractable.Position.Y - playerPosition.Value.Y)
            : (float?)null;
        var currentPriority = objectPriorityRuleService.GetEffectivePriority(context, currentInteractable, currentDistance, currentVerticalDelta);
        var plannerPriority = objectPriorityRuleService.GetEffectivePriority(context, plannerInteractable, plannerDistance, plannerVerticalDelta);

        if (plannerPriority < currentPriority)
        {
            reason = $"Switching from stale {currentInteractable.Classification} target {currentInteractable.Name} to higher-priority planner target {plannerInteractable.Name} ({plannerPriority} < {currentPriority}).";
            return true;
        }

        if (currentObjectiveKind == PlannerObjectiveKind.ExpendableInteractable
            && plannerObjectiveKind != PlannerObjectiveKind.ExpendableInteractable
            && plannerPriority <= currentPriority)
        {
            reason = $"Breaking stale expendable follow-through on {currentInteractable.Name} so ADS can switch to planner target {plannerInteractable.Name} ({plannerPriority} <= {currentPriority}).";
            return true;
        }

        return false;
    }

    private static PlannerObjectiveKind GetObjectiveKindForInteractable(InteractableClass classification)
        => classification switch
        {
            InteractableClass.CombatFriendly => PlannerObjectiveKind.CombatFriendlyInteractable,
            InteractableClass.Expendable => PlannerObjectiveKind.ExpendableInteractable,
            InteractableClass.Optional => PlannerObjectiveKind.OptionalInteractable,
            InteractableClass.TreasureDoor => PlannerObjectiveKind.TreasureDoor,
            InteractableClass.TreasureCoffer => PlannerObjectiveKind.TreasureCoffer,
            _ => PlannerObjectiveKind.RequiredInteractable,
        };

    private bool ShouldBypassCombatHold(DutyContextSnapshot context, PlannerSnapshot planner)
    {
        if (IsPraetoriumMountedCombatContext(context))
            return true;

        if (committedForceMarchManualDestination is not null
            && DateTime.UtcNow < committedForceMarchManualDestinationUntilUtc)
        {
            return true;
        }

        if (!context.InCombat)
        {
            ResetLivePartyDamageProgress();
            return false;
        }

        if (planner.Mode == PlannerMode.Progression
            && planner.ObjectiveKind is PlannerObjectiveKind.CombatFriendlyInteractable
                or PlannerObjectiveKind.BossFightMonster
                or PlannerObjectiveKind.MapXzForceMarchDestination
                or PlannerObjectiveKind.XyzForceMarchDestination)
        {
            return true;
        }

        if (pendingProgressionInteractable?.Classification == InteractableClass.CombatFriendly)
            return true;

        return HasRecentLivePartyDamageProgression();
    }

    private static bool ShouldContinueCommittedManualDestination(PlannerSnapshot planner)
        => planner.ObjectiveKind is not (
            PlannerObjectiveKind.RequiredInteractable
            or PlannerObjectiveKind.CombatFriendlyInteractable
            or PlannerObjectiveKind.ExpendableInteractable
            or PlannerObjectiveKind.OptionalInteractable
            or PlannerObjectiveKind.TreasureDoor
            or PlannerObjectiveKind.TreasureCoffer
            or PlannerObjectiveKind.BossFightMonster
            or PlannerObjectiveKind.MapXzDestination
            or PlannerObjectiveKind.XyzDestination
            or PlannerObjectiveKind.MapXzForceMarchDestination
            or PlannerObjectiveKind.XyzForceMarchDestination);

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
        var retryIdentity = BuildInteractableRetryIdentity(observedInteractable);
        if (!string.Equals(closeRangeInteractFallbackKey, retryIdentity, StringComparison.Ordinal))
        {
            closeRangeInteractFallbackKey = retryIdentity;
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

    private bool IsEquivalentProgressionInteractable(ObservedInteractable left, ObservedInteractable right)
    {
        if (string.Equals(left.Key, right.Key, StringComparison.Ordinal))
            return true;

        if (left.GameObjectId != 0 && right.GameObjectId != 0 && left.GameObjectId == right.GameObjectId)
            return true;

        return (left.MapId == 0 || right.MapId == 0 || left.MapId == right.MapId)
               && left.ObjectKind == right.ObjectKind
               && left.DataId == right.DataId
               && left.Classification == right.Classification
               && string.Equals(left.Name, right.Name, StringComparison.OrdinalIgnoreCase)
               && GetHorizontalDistance(left.Position, right.Position) <= DirectInteractAttemptRange;
    }

    private string DescribeProgressionInteractableReuse(ObservedInteractable previous, ObservedInteractable current)
    {
        if (string.Equals(previous.Key, current.Key, StringComparison.Ordinal))
            return "the same live interactable key";

        if (previous.GameObjectId != 0 && current.GameObjectId != 0 && previous.GameObjectId == current.GameObjectId)
            return "the same live object id";

        if (GetHorizontalDistance(previous.Position, current.Position) <= CloseRangeInteractFallbackHorizontalDistance)
            return "the same close-XZ retry pocket after live re-resolution";

        return "a newly resolved live interactable match";
    }

    private static string BuildInteractableRetryIdentity(ObservedInteractable interactable)
    {
        var positionBucket = BuildRetryIdentityPositionBucket(interactable.Position);
        return $"{interactable.MapId}:{interactable.ObjectKind}:{interactable.DataId}:{interactable.Classification}:{interactable.Name}:{positionBucket}";
    }

    private static string BuildRetryIdentityPositionBucket(Vector3 position)
        => $"{MathF.Round(position.X / InteractableRetryIdentityPositionBucketSize, 0):0},{MathF.Round(position.Y / InteractableRetryIdentityPositionBucketSize, 0):0},{MathF.Round(position.Z / InteractableRetryIdentityPositionBucketSize, 0):0}";

    private static bool ShouldUsePraetoriumMagitekArmorCameraIndependentInteract(DutyContextSnapshot context, ObservedInteractable interactable)
    {
        return context.TerritoryTypeId == PraetoriumTerritoryTypeId
               && string.Equals(interactable.Name, "Magitek Armor", StringComparison.OrdinalIgnoreCase);
    }

    private void LogPraetoriumMagitekArmorCameraIndependentInteract(ObservedInteractable interactable)
    {
        var logKey = BuildInteractableRetryIdentity(interactable);
        if (string.Equals(lastPraetoriumMagitekArmorCameraIndependentInteractLogKey, logKey, StringComparison.Ordinal))
            return;

        lastPraetoriumMagitekArmorCameraIndependentInteractLogKey = logKey;
        log?.Information(
            "[ADS] Praetorium Magitek Armor is using camera-independent interact mode (InteractWithObject cameraBasedInteract=false) because standard interact can be blocked by zoom/camera obstruction.");
    }

    private bool TryHoldPendingProgressionInteractResult(DutyContextSnapshot context, PlannerSnapshot planner, ObservationSnapshot observation, string prefix)
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

        if (planner.Mode == PlannerMode.Progression && IsInteractablePlannerObjective(planner.ObjectiveKind))
        {
            var plannerInteractable = ResolveObservedInteractable(planner, observation);
            if (plannerInteractable is not null
                && !IsEquivalentProgressionInteractable(plannerInteractable, pendingInteractable)
                && ShouldSwitchToPlannerInteractable(
                    context,
                    pendingInteractable,
                    GetObjectiveKindForInteractable(pendingInteractable.Classification),
                    plannerInteractable,
                    planner.ObjectiveKind,
                    out var switchReason))
            {
                log?.Information($"[ADS] Clearing stale interact follow-through on {pendingInteractable.Name}. {switchReason}");
                ClearInteractableCommitment();
                return false;
            }
        }

        var isExpendableFollowThrough = pendingInteractable.Classification == InteractableClass.Expendable;
        var isTreasureDoorFollowThrough = pendingInteractable.Classification == InteractableClass.TreasureDoor;
        var isRequiredFollowThrough = pendingInteractable.Classification is InteractableClass.Required or InteractableClass.TreasureDoor;
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
            log?.Information($"[ADS] Required interact follow-through on {pendingInteractable.Name} is closing because Mounted became true after the interact.");
            observationMemoryService.MarkProgressionInteractionSent(context, pendingInteractable);
            TryRetirePendingSatisfiedManualDestination(pendingInteractable, objectTable.LocalPlayer?.Position);
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
                log?.Information($"[ADS] Required interact follow-through on {pendingInteractable.Name} is closing because the player relocated to XZ {pendingHorizontalDistance:0.0}y from the live interactable after the interact.");
                observationMemoryService.MarkProgressionInteractionSent(context, pendingInteractable);
                TryRetirePendingSatisfiedManualDestination(pendingInteractable, playerPosition);
                ClearInteractableCommitment();
                StopMovementAssists();
                SetPhase(
                    ExecutionPhase.AttemptingInteractableObjective,
                    $"{prefix} Required interact follow-through for {pendingInteractable.Name} stayed live, but the player was displaced to XZ {pendingHorizontalDistance:0.0}y after the interact. ADS is treating that interactable position as consumed and suppressing it until duty reset.");
                return true;
            }
        }

        var now = DateTime.UtcNow;
        var treasureDoorReadyForFollowThrough = isTreasureDoorFollowThrough
            && pendingLiveInteractable is null
            && pendingProgressionInteractAfterWaitUntilUtc <= now;
        if (now < pendingProgressionInteractResultUntilUtc)
        {
            if (treasureDoorReadyForFollowThrough
                && TryAdvanceTreasureDoorFollowThrough(prefix, out var reachedTreasureDoorTransitionPoint))
            {
                if (reachedTreasureDoorTransitionPoint)
                {
                    observationMemoryService.MarkProgressionInteractionSent(context, pendingInteractable);
                    TryRetirePendingSatisfiedManualDestination(pendingInteractable, playerPosition);
                    ClearInteractableCommitment();
                }

                return true;
            }

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
                log?.Information($"[ADS] Required interact follow-through on {pendingInteractable.Name} hit the {RequiredInteractionAttemptLimit}-attempt limit and is releasing the attempt window.");
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

        if (treasureDoorReadyForFollowThrough
            && TryAdvanceTreasureDoorFollowThrough(prefix, out var reachedTreasureDoorTransitionPointAfterWindow))
        {
            if (reachedTreasureDoorTransitionPointAfterWindow)
            {
                observationMemoryService.MarkProgressionInteractionSent(context, pendingInteractable);
                TryRetirePendingSatisfiedManualDestination(pendingInteractable, playerPosition);
                ClearInteractableCommitment();
            }

            return true;
        }

        observationMemoryService.MarkProgressionInteractionSent(context, pendingInteractable);
        TryRetirePendingSatisfiedManualDestination(pendingInteractable, objectTable.LocalPlayer?.Position);
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
        pendingTreasureDoorTransitionPoint = null;
        pendingSatisfiedManualDestination = null;
        pendingSatisfiedManualInteractableKey = null;
        pendingProgressionInteractResultUntilUtc = DateTime.MinValue;
        pendingProgressionInteractAfterWaitUntilUtc = DateTime.MinValue;
        pendingRequiredInteractionAttemptsSent = 0;
    }

    private bool TryAdvanceTreasureDoorFollowThrough(string prefix, out bool reachedTransitionPoint)
    {
        reachedTransitionPoint = false;
        var frontierPoint = pendingTreasureDoorTransitionPoint;
        var playerPosition = objectTable.LocalPlayer?.Position;
        if (frontierPoint is null || !playerPosition.HasValue)
            return false;

        var targetHorizontalDistance = GetHorizontalDistance(frontierPoint.Position, playerPosition.Value);
        var targetDistance = Vector3.Distance(frontierPoint.Position, playerPosition.Value);
        var targetVerticalDelta = MathF.Abs(frontierPoint.Position.Y - playerPosition.Value.Y);
        var arrivalRange = frontierPoint.ArrivalRadiusXz > 0f
            ? frontierPoint.ArrivalRadiusXz
            : PreferredFrontierArrivalRange;
        if (targetHorizontalDistance > arrivalRange)
        {
            TryBeginNavigation(BuildFrontierTargetId(frontierPoint), frontierPoint.Position);
            SetPhase(
                ExecutionPhase.AttemptingInteractableObjective,
                $"{prefix} Treasure door follow-through is advancing through {frontierPoint.Name} after the interact (XZ {targetHorizontalDistance:0.0}y, 3D {targetDistance:0.0}y, Y {targetVerticalDelta:0.0}y).");
            return true;
        }

        reachedTransitionPoint = true;
        StopMovementAssists();
        SetPhase(
            ExecutionPhase.AttemptingInteractableObjective,
            $"{prefix} Treasure door follow-through reached {frontierPoint.Name} (XZ {targetHorizontalDistance:0.0}y, 3D {targetDistance:0.0}y, Y {targetVerticalDelta:0.0}y) and is waiting for refreshed duty truth.");
        return true;
    }

    private static DungeonFrontierPoint BuildTreasureDoorFollowThroughPoint(
        DutyContextSnapshot context,
        ObservedInteractable observedInteractable,
        Vector3 playerPosition)
        => new()
        {
            Key = $"treasure-door-follow-through:{context.TerritoryTypeId}:{observedInteractable.Key}",
            Name = $"{observedInteractable.Name} follow-through",
            Position = BuildTreasureDoorFollowThroughPosition(playerPosition, observedInteractable.Position),
            LevelRowId = 0,
            MapId = context.MapId,
            Priority = 0,
            ManualDestinationKind = ManualDestinationKind.None,
            ArrivalRadiusXz = TreasureDoorFollowThroughArrivalRange,
        };

    private static Vector3 BuildTreasureDoorFollowThroughPosition(Vector3 playerPosition, Vector3 doorPosition)
    {
        var flatDelta = new Vector3(doorPosition.X - playerPosition.X, 0f, doorPosition.Z - playerPosition.Z);
        var flatDistance = flatDelta.Length();
        if (flatDistance <= float.Epsilon)
            return doorPosition;

        var flatDirection = Vector3.Normalize(flatDelta);
        return new Vector3(
            doorPosition.X + (flatDirection.X * TreasureDoorFollowThroughDistance),
            doorPosition.Y,
            doorPosition.Z + (flatDirection.Z * TreasureDoorFollowThroughDistance));
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

    private void UpdateLeaveDuty(DutyContextSnapshot context, ObservationSnapshot observation)
    {
        if (context.InCombat)
        {
            StopMovementAssists();
            leaveTreasureSweepClearSinceUtc = DateTime.MinValue;
            SetPhase(ExecutionPhase.LeavingDuty, "Leave requested. Waiting for combat to clear before duty exit.");
            return;
        }

        var now = DateTime.UtcNow;
        if (leaveTreasureInteractionSent && now < nextInteractAttemptUtc)
        {
            StopMovementAssists();
            SetPhase(ExecutionPhase.LeavingDuty, "Leave requested. Clearing nearby treasure before duty exit.");
            return;
        }

        var playerPosition = objectTable.LocalPlayer?.Position;
        if (playerPosition.HasValue)
        {
            var nearbyTreasure = FindNearestLeaveSweepTreasureCoffer(context, observation, playerPosition.Value);
            if (nearbyTreasure is not null)
            {
                leaveTreasureSweepClearSinceUtc = DateTime.MinValue;
                leaveLootDistributionWaitUntilUtc = DateTime.MinValue;
                var previousInteractGameObjectId = lastInteractGameObjectId;
                var previousInteractAttemptUtc = nextInteractAttemptUtc;
                TryAdvanceInteractableObjective(
                    context,
                    nearbyTreasure,
                    "Leave requested. Clearing nearby treasure before duty exit.");
                if (lastInteractGameObjectId == nearbyTreasure.GameObjectId
                    && (previousInteractGameObjectId != lastInteractGameObjectId
                        || nextInteractAttemptUtc > previousInteractAttemptUtc))
                {
                    leaveTreasureInteractionSent = true;
                }
                return;
            }
        }

        if (leaveTreasureInteractionSent)
        {
            if (leaveTreasureSweepClearSinceUtc == DateTime.MinValue)
                leaveTreasureSweepClearSinceUtc = now;

            if (now - leaveTreasureSweepClearSinceUtc < LeaveTreasureSweepSettleDelay)
            {
                StopMovementAssists();
                SetPhase(ExecutionPhase.LeavingDuty, "Leave requested. Waiting for nearby treasure list to settle before duty exit.");
                return;
            }

            if (leaveLootDistributionWaitUntilUtc == DateTime.MinValue)
                leaveLootDistributionWaitUntilUtc = now + LeaveLootDistributionDelay;

            if (now < leaveLootDistributionWaitUntilUtc)
            {
                StopMovementAssists();
                SetPhase(
                    ExecutionPhase.LeavingDuty,
                    $"Leave requested. Nearby treasure sweep is clear, waiting {(leaveLootDistributionWaitUntilUtc - now).TotalSeconds:0.0}s for loot distribution before duty exit.");
                return;
            }
        }

        StopMovementAssists();
        TrySendLeaveDutyUi();
    }

    private ObservedInteractable? FindNearestLeaveSweepTreasureCoffer(
        DutyContextSnapshot context,
        ObservationSnapshot observation,
        Vector3 playerPosition)
        => observation.LiveInteractables
            .Where(x => x.Classification == InteractableClass.TreasureCoffer
                && MatchesCurrentMap(context, x.MapId)
                && IsInteractableStillAllowedInContext(context, x))
            .Select(x => new
            {
                Interactable = x,
                GameObject = ResolveGameObject(x),
                HorizontalDistance = GetHorizontalDistance(x.Position, playerPosition),
                VerticalDelta = MathF.Abs(x.Position.Y - playerPosition.Y),
            })
            .Where(x => x.GameObject is not null
                && x.HorizontalDistance <= LeaveTreasureSweepHorizontalRange
                && x.VerticalDelta <= LeaveTreasureSweepVerticalCap)
            .OrderBy(x => x.HorizontalDistance)
            .Select(x => x.Interactable)
            .FirstOrDefault();

    private void ResetRecoveryHold()
    {
        recoveryTargetObjectiveKind = PlannerObjectiveKind.None;
        recoveryTargetPosition = null;
        recoveryTargetReachedUtc = DateTime.MinValue;
    }

    private void ResetLeaveState()
    {
        nextLeaveUiAttemptUtc = DateTime.MinValue;
        leaveLootDistributionWaitUntilUtc = DateTime.MinValue;
        leaveTreasureSweepClearSinceUtc = DateTime.MinValue;
        leaveTreasureInteractionSent = false;
    }

    private bool HasRecentLivePartyDamageProgression()
    {
        var now = DateTime.UtcNow;
        var localPlayer = objectTable.LocalPlayer;
        if (localPlayer == null)
        {
            ResetLivePartyDamageProgress();
            return false;
        }

        var partyMemberNames = BuildOtherPartyMemberNames(localPlayer.Name.TextValue);
        if (partyMemberNames.Count == 0)
        {
            ResetLivePartyDamageProgress();
            return false;
        }

        var visiblePartyMemberIds = new HashSet<ulong>();
        var visibleMonsterIds = new HashSet<ulong>();
        var hasVisiblePartyMember = false;
        foreach (var obj in objectTable)
        {
            if (obj.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Player
                || obj is not ICharacter playerCharacter)
                continue;

            if (!partyMemberNames.Contains(playerCharacter.Name.TextValue))
                continue;

            if (Vector3.Distance(localPlayer.Position, playerCharacter.Position) > LivePartyDamageProgressRange)
                continue;

            hasVisiblePartyMember = true;
            visiblePartyMemberIds.Add(playerCharacter.GameObjectId);
            if (recentVisiblePartyMemberHp.TryGetValue(playerCharacter.GameObjectId, out var previousHp)
                && playerCharacter.CurrentHp < previousHp)
            {
                livePartyDamageProgressUntilUtc = now + LivePartyDamageProgressWindow;
            }

            recentVisiblePartyMemberHp[playerCharacter.GameObjectId] = playerCharacter.CurrentHp;
        }

        PruneHitPointCache(recentVisiblePartyMemberHp, visiblePartyMemberIds);
        if (!hasVisiblePartyMember)
        {
            recentNearbyMonsterHp.Clear();
            return livePartyDamageProgressUntilUtc > now;
        }

        foreach (var obj in objectTable)
        {
            if (obj is not IBattleNpc battleNpc || battleNpc.CurrentHp <= 1)
                continue;

            if (Vector3.Distance(localPlayer.Position, obj.Position) > LivePartyDamageProgressRange)
                continue;

            visibleMonsterIds.Add(obj.GameObjectId);
            if (recentNearbyMonsterHp.TryGetValue(obj.GameObjectId, out var previousHp)
                && battleNpc.CurrentHp < previousHp)
            {
                livePartyDamageProgressUntilUtc = now + LivePartyDamageProgressWindow;
            }

            recentNearbyMonsterHp[obj.GameObjectId] = battleNpc.CurrentHp;
        }

        PruneHitPointCache(recentNearbyMonsterHp, visibleMonsterIds);
        return livePartyDamageProgressUntilUtc > now;
    }

    private void ResetLivePartyDamageProgress()
    {
        recentVisiblePartyMemberHp.Clear();
        recentNearbyMonsterHp.Clear();
        livePartyDamageProgressUntilUtc = DateTime.MinValue;
    }

    private static void PruneHitPointCache(Dictionary<ulong, uint> cache, HashSet<ulong> visibleIds)
    {
        if (cache.Count == 0)
            return;

        foreach (var key in cache.Keys.Where(key => !visibleIds.Contains(key)).ToArray())
            cache.Remove(key);
    }

    private static HashSet<string> BuildOtherPartyMemberNames(string localPlayerName)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < Plugin.PartyList.Length; i++)
        {
            var member = Plugin.PartyList[i];
            if (member is null)
                continue;

            var name = member.Name.TextValue.Trim();
            if (string.IsNullOrWhiteSpace(name)
                || string.Equals(name, localPlayerName, StringComparison.Ordinal))
            {
                continue;
            }

            names.Add(name);
        }

        return names;
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
        => context.InInstancedDuty
            && context.Mounted;  //added the ;
            //&& context.TerritoryTypeId == PraetoriumTerritoryTypeId; //no . bad AI

    private bool TryGetCurrentMountCombatActions(uint mountId, out MountedCombatAction[] mountedActions)
    {
        mountedActions = [];

        if (mountId == 0)
            return false;

        if (mountedCombatActionCache.TryGetValue(mountId, out var cachedActions))
        {
            mountedActions = cachedActions;
            LogMountedCombatActionPriority(mountId, mountedActions);
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
        LogMountedCombatActionPriority(mountId, mountedActions);
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
        var maxRange = action.Range > 0 ? action.Range : 50f;
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

    private static bool TryGetNearestMountedGroundTargetInRange(
        IEnumerable<MountedCombatTarget> targets,
        MountedCombatAction action,
        out MountedCombatTarget selectedTarget)
    {
        var maxRange = action.Range > 0 ? action.Range : 50f;
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

    private void LogMountedCombatActionPriority(uint mountId, IReadOnlyCollection<MountedCombatAction> mountedActions)
    {
        if (mountedActions.Count == 0)
            return;

        if (!loggedMountedCombatActionMounts.Add(mountId))
            return;

        var actionSummary = string.Join(
            ", ",
            mountedActions
                .OrderBy(action => GetMountedCombatPriority(action.ActionId))
                .ThenBy(action => action.ActionId)
                .Select(action => $"{action.Name}({action.ActionId})"));
        log?.Information(
            $"[ADS] Mounted Praetorium combat resolved mount {mountId} actions: {actionSummary}. Priority policy: 1128 Magitek Cannon first, aggressively retrying trailing-pack shots on refresh while movement continues; 1129 Photon Stream only when 1128 is unavailable and the target is in front.");
    }

    private static bool IsMountedCombatTargetInFront(Vector3 localPlayerPosition, float localPlayerRotation, MountedCombatTarget target)
    {
        var flatDelta = new Vector2(
            target.GameObject.Position.X - localPlayerPosition.X,
            target.GameObject.Position.Z - localPlayerPosition.Z);
        if (flatDelta.LengthSquared() <= 0.01f)
            return true;

        var toTarget = Vector2.Normalize(flatDelta);
        var forward = new Vector2(MathF.Sin(localPlayerRotation), MathF.Cos(localPlayerRotation));
        return Vector2.Dot(forward, toTarget) >= PhotonStreamFrontDotThreshold;
    }

    private static bool IsMountedCombatTargetBehind(Vector3 localPlayerPosition, float localPlayerRotation, MountedCombatTarget target)
    {
        var flatDelta = new Vector2(
            target.GameObject.Position.X - localPlayerPosition.X,
            target.GameObject.Position.Z - localPlayerPosition.Z);
        if (flatDelta.LengthSquared() <= 0.01f)
            return false;

        var toTarget = Vector2.Normalize(flatDelta);
        var forward = new Vector2(MathF.Sin(localPlayerRotation), MathF.Cos(localPlayerRotation));
        return Vector2.Dot(forward, toTarget) < 0f;
    }

    private static bool IsMountedCombatTargetInTrailingPack(Vector3 localPlayerPosition, float localPlayerRotation, MountedCombatTarget target)
    {
        var flatDelta = new Vector2(
            target.GameObject.Position.X - localPlayerPosition.X,
            target.GameObject.Position.Z - localPlayerPosition.Z);
        if (flatDelta.LengthSquared() <= 0.01f)
            return true;

        var toTarget = Vector2.Normalize(flatDelta);
        var forward = new Vector2(MathF.Sin(localPlayerRotation), MathF.Cos(localPlayerRotation));
        return Vector2.Dot(forward, toTarget) <= PraetoriumTrailingPackDotThreshold;
    }

    private static bool IsMountedCombatTargetWithinRearPreferenceEnvelope(
        Vector3 localPlayerPosition,
        MountedCombatAction action,
        MountedCombatTarget target)
    {
        var maxRange = action.Range > 0 ? action.Range : 50f;
        var horizontalDistance = GetHorizontalDistance(localPlayerPosition, target.GameObject.Position);
        var verticalDelta = MathF.Abs(target.GameObject.Position.Y - localPlayerPosition.Y);
        return target.Distance <= maxRange
               && horizontalDistance <= PraetoriumRearTargetMaxHorizontalDistance
               && verticalDelta <= PraetoriumRearTargetMaxVerticalDelta;
    }

    private static bool TryGetNearestMountedCombatTargetInRange(
        IEnumerable<MountedCombatTarget> targets,
        MountedCombatAction action,
        out MountedCombatTarget selectedTarget)
    {
        var maxRange = action.Range > 0 ? action.Range : 50f;
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
            PlannerObjectiveKind.TreasureDoor => InteractableClass.TreasureDoor,
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

    private unsafe bool TryInteractWithObject(IGameObject gameObject, bool cameraBasedInteract = true)
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

            targetSystem->InteractWithObject(nativeObject, cameraBasedInteract);
            return true;
        }
        catch (Exception ex)
        {
            log?.Warning(ex, $"[ADS] Direct interact failed for {gameObject.Name.TextValue} using cameraBasedInteract={cameraBasedInteract}.");
            return false;
        }
    }

    private unsafe void TrySendLeaveDutyUi()
    {
        var now = DateTime.UtcNow;
        if (GameInteractionHelper.ClickYesIfVisible(log))
        {
            nextLeaveUiAttemptUtc = now + LeaveUiRetryCooldown;
            SetPhase(ExecutionPhase.LeavingDuty, "Leave requested. Confirmed the duty-exit prompt; waiting for the zone-out.");
            return;
        }

        if (now < nextLeaveUiAttemptUtc)
        {
            SetPhase(ExecutionPhase.LeavingDuty, "Leave requested. Waiting for the next leave-duty UI retry window.");
            return;
        }

        nextLeaveUiAttemptUtc = now + LeaveUiRetryCooldown;

        var agentModule = AgentModule.Instance();
        if (agentModule != null)
        {
            var contentsFinderMenuAgent = agentModule->GetAgentByInternalId(AgentId.ContentsFinderMenu);
            if (contentsFinderMenuAgent != null)
                contentsFinderMenuAgent->Show();
        }

        if (GameInteractionHelper.IsAddonVisible("ContentsFinderMenu"))
        {
            GameInteractionHelper.FireAddonCallback("ContentsFinderMenu", true, 0);
            GameInteractionHelper.FireAddonCallback("ContentsFinderMenu", false, -2);
            SetPhase(ExecutionPhase.LeavingDuty, "Leave requested. Opened Contents Finder and sent the Leave Duty callback.");
            return;
        }

        if (GameInteractionHelper.TrySendChatCommand(commandManager, "/dutyfinder", log))
        {
            SetPhase(ExecutionPhase.LeavingDuty, "Leave requested. Opening Duty Finder so ADS can send the Leave Duty callback.");
            return;
        }

        SetPhase(ExecutionPhase.LeavingDuty, "Leave requested, but ADS could not open the leave-duty UI yet.");
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

    private static string FormatVector(Vector3 value)
        => string.Create(CultureInfo.InvariantCulture, $"{value.X:0.00},{value.Y:0.00},{value.Z:0.00}");

    private void SetPhase(ExecutionPhase phase, string status)
    {
        if (phase != CurrentPhase)
            log?.Information($"[ADS] Execution phase -> {phase}: {status}");

        CurrentPhase = phase;
        LastStatus = status;
    }
}
