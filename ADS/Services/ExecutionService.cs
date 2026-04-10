using ADS.Models;
using System.Globalization;
using System.Numerics;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace ADS.Services;

public sealed class ExecutionService
{
    private const float NavigationPhaseRange = 6f;
    private const float PreferredInteractArrivalRange = 0.8f;
    private const float DirectInteractAttemptRange = 2.0f;
    private const float PreferredMonsterArrivalRange = 2.0f;
    private const float PreferredRecoveryArrivalRange = 2.0f;
    private const float RecoveryGhostRetireRadius = 8.0f;
    private const float RecoveryClusterArrivalRange = RecoveryGhostRetireRadius;
    private const float RecoveryTargetSimilarityRadius = RecoveryGhostRetireRadius;
    private static readonly TimeSpan InteractAttemptCooldown = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan NavigationRetryCooldown = TimeSpan.FromSeconds(2.0);
    private static readonly TimeSpan RecoveryTruthSettleDelay = TimeSpan.FromSeconds(1.0);

    private readonly IObjectTable objectTable;
    private readonly ITargetManager targetManager;
    private readonly ICommandManager commandManager;
    private readonly ObservationMemoryService observationMemoryService;
    private readonly IPluginLog? log;
    private string? committedInteractableKey;
    private PlannerObjectiveKind committedInteractableObjectiveKind = PlannerObjectiveKind.None;
    private ulong lastInteractGameObjectId;
    private DateTime nextInteractAttemptUtc;
    private DateTime nextNavigationCommandUtc;
    private ulong movementTargetGameObjectId;
    private bool navigationActive;
    private PlannerObjectiveKind recoveryTargetObjectiveKind = PlannerObjectiveKind.None;
    private Vector3? recoveryTargetPosition;
    private DateTime recoveryTargetReachedUtc;

    public ExecutionService(
        IObjectTable objectTable,
        ITargetManager targetManager,
        ICommandManager commandManager,
        ObservationMemoryService observationMemoryService,
        IPluginLog? log = null)
    {
        this.objectTable = objectTable;
        this.targetManager = targetManager;
        this.commandManager = commandManager;
        this.observationMemoryService = observationMemoryService;
        this.log = log;
    }

    public OwnershipMode CurrentMode { get; private set; } = OwnershipMode.Idle;
    public ExecutionPhase CurrentPhase { get; private set; } = ExecutionPhase.Idle;
    public string LastStatus { get; private set; } = "Idle.";

    public bool IsOwned
        => CurrentMode is OwnershipMode.OwnedStartOutside or OwnershipMode.OwnedStartInside or OwnershipMode.OwnedResumeInside or OwnershipMode.Leaving;

    public bool StartDutyFromOutside()
    {
        ClearInteractableCommitment();
        CurrentMode = OwnershipMode.OwnedStartOutside;
        SetPhase(ExecutionPhase.OutsideQueue, "Queued outside start. ADS will claim ownership when you enter a supported pilot duty.");
        return true;
    }

    public bool StartDutyFromInside(DutyContextSnapshot context)
    {
        ClearInteractableCommitment();
        if (!context.InDuty || !context.AllowsActiveExecution)
        {
            CurrentMode = context.InDuty ? OwnershipMode.Failed : OwnershipMode.Idle;
            SetPhase(
                CurrentMode == OwnershipMode.Failed ? ExecutionPhase.Failure : ExecutionPhase.Idle,
                "Start inside requires being inside one of the supported pilot duties.");
            return false;
        }

        CurrentMode = OwnershipMode.OwnedStartInside;
        SetPhase(
            ExecutionPhase.WaitingForTruth,
            $"Owned inside {context.CurrentDuty?.EnglishName}. ADS now runs a staged execution phase engine while live movement and interaction remain pending.");
        return true;
    }

    public bool ResumeDutyFromInside(DutyContextSnapshot context)
    {
        ClearInteractableCommitment();
        if (!context.InDuty || !context.AllowsActiveExecution)
        {
            CurrentMode = context.InDuty ? OwnershipMode.Failed : OwnershipMode.Idle;
            SetPhase(
                CurrentMode == OwnershipMode.Failed ? ExecutionPhase.Failure : ExecutionPhase.Idle,
                "Resume requires being inside one of the supported pilot duties.");
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
                    SetPhase(ExecutionPhase.AwaitingSupportedPilotDuty, "Outside-start ownership is pending a supported observer duty; current duty is catalog-only.");
                    return;
                }

                if (!context.AllowsActiveExecution)
                {
                    StopMovementAssists();
                    ClearInteractableCommitment();
                    SetPhase(ExecutionPhase.AwaitingSupportedPilotDuty, $"Entered {context.CurrentDuty?.EnglishName}, but ADS active ownership is still limited to pilot duties.");
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

                if (!context.AllowsActiveExecution)
                {
                    StopMovementAssists();
                    ClearInteractableCommitment();
                    CurrentMode = OwnershipMode.Failed;
                    SetPhase(ExecutionPhase.Failure, "Current duty does not have an ADS pilot profile.");
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
            ResetRecoveryHold();
            StopMovementAssists();
            SetPhase(ExecutionPhase.TransitionHold, $"{prefix} Waiting for safe post-transition duty truth before advancing.");
            return;
        }

        if (context.InCombat || planner.Mode == PlannerMode.Combat)
        {
            ResetRecoveryHold();
            StopMovementAssists();
            SetPhase(ExecutionPhase.CombatHold, $"{prefix} Combat is active, so ADS is holding progression until combat clears.");
            return;
        }

        if (planner.Mode == PlannerMode.Recovery)
        {
            TryAdvanceRecoveryObjective(planner, observation, $"{prefix}");
            return;
        }

        ResetRecoveryHold();

        if (planner.Mode == PlannerMode.Progression)
        {
            var committedInteractable = ResolveCommittedInteractable(observation);
            if (committedInteractable is not null)
            {
                if (planner.ObjectiveKind is PlannerObjectiveKind.RequiredInteractable
                    or PlannerObjectiveKind.CombatFriendlyInteractable
                    or PlannerObjectiveKind.OptionalInteractable
                    or PlannerObjectiveKind.TreasureCoffer)
                {
                    var replannedInteractable = ResolveObservedInteractable(planner, observation);
                    if (replannedInteractable is not null && replannedInteractable.Key != committedInteractable.Key)
                    {
                        CommitInteractable(replannedInteractable, planner.ObjectiveKind);
                        TryAdvanceInteractableObjective(replannedInteractable, $"{prefix} Switching follow-through to the planner's newer interactable selection.");
                        return;
                    }
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

            committedInteractable = ResolveCommittedInteractable(observation);
            if (committedInteractable is not null)
            {
                TryAdvanceInteractableObjective(committedInteractable, $"{prefix} Following through on the previously selected interactable.");
                return;
            }

            if (planner.ObjectiveKind == PlannerObjectiveKind.Monster)
            {
                TryAdvanceMonsterObjective(planner, observation, $"{prefix}");
                return;
            }

            if (planner.ObjectiveKind is PlannerObjectiveKind.RequiredInteractable
                or PlannerObjectiveKind.CombatFriendlyInteractable
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
                TryAdvanceInteractableObjective(selectedInteractable, $"{prefix}");
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
        if (observedMonster is null)
        {
            StopMovementAssists();
            SetPhase(ExecutionPhase.ReadyForMonsterObjective, $"{prefix} Monster objective armed but not currently resolved in live object truth: {planner.Objective}");
            return;
        }

        var gameObject = ResolveGameObject(observedMonster);
        if (gameObject is null)
        {
            StopMovementAssists();
            SetPhase(ExecutionPhase.ReadyForMonsterObjective, $"{prefix} Monster objective is on the planner but no live game object was resolved yet: {planner.Objective}");
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
                $"{prefix} Navigating toward monster pack {observedMonster.Name} ({targetDistance:0.0}y) with a close combat arrival target.");
            return;
        }

        StopMovementAssists();
        SetPhase(
            ExecutionPhase.ReadyForMonsterObjective,
            $"{prefix} Positioned near monster pack {observedMonster.Name} ({targetDistance:0.0}y). Waiting for combat tooling / aggro to take over.");
    }

    private void TryAdvanceInteractableObjective(ObservedInteractable observedInteractable, string prefix)
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

        var preferredApproachPoint = BuildPreferredInteractApproachPoint(playerPosition.Value, gameObject.Position);

        if (targetHorizontalDistance > DirectInteractAttemptRange)
        {
            TryBeginNavigation(gameObject.GameObjectId, preferredApproachPoint);

            var phase = targetHorizontalDistance > NavigationPhaseRange
                ? ExecutionPhase.NavigatingToInteractableObjective
                : ExecutionPhase.ApproachingInteractableObjective;
            var verb = phase == ExecutionPhase.NavigatingToInteractableObjective
                ? "Navigating toward"
                : "Close-navigating toward";
            SetPhase(
                phase,
                $"{prefix} {verb} {observedInteractable.Name} (XZ {targetHorizontalDistance:0.0}y, 3D {targetDistance:0.0}y, Y {targetVerticalDelta:0.0}) with a <1y XZ stand-off target.");
            return;
        }

        StopMovementAssists();
        var now = DateTime.UtcNow;
        if (now < nextInteractAttemptUtc && lastInteractGameObjectId == observedInteractable.GameObjectId)
        {
            SetPhase(ExecutionPhase.AttemptingInteractableObjective, $"{prefix} Waiting for interact cooldown/result on {observedInteractable.Name}.");
            return;
        }

        if (TryInteractWithObject(gameObject))
        {
            if (observedInteractable.Classification == InteractableClass.TreasureCoffer)
                observationMemoryService.MarkTreasureInteractionSent(observedInteractable);

            lastInteractGameObjectId = observedInteractable.GameObjectId;
            nextInteractAttemptUtc = now + InteractAttemptCooldown;
            SetPhase(ExecutionPhase.AttemptingInteractableObjective, $"{prefix} Direct interact sent to {observedInteractable.Name}.");
            return;
        }

        nextInteractAttemptUtc = now + InteractAttemptCooldown;
        if (targetHorizontalDistance > PreferredInteractArrivalRange)
        {
            TryBeginNavigation(gameObject.GameObjectId, preferredApproachPoint);
            SetPhase(
                ExecutionPhase.ApproachingInteractableObjective,
                $"{prefix} Direct interact did not land for {observedInteractable.Name} at XZ {targetHorizontalDistance:0.0}y (3D {targetDistance:0.0}y, Y {targetVerticalDelta:0.0}); continuing close-nav toward the <1y XZ stand-off target.");
            return;
        }

        SetPhase(ExecutionPhase.ReadyForInteractableObjective, $"{prefix} Direct interact attempt failed for {observedInteractable.Name}; waiting to retry.");
    }

    private void CommitInteractable(ObservedInteractable interactable, PlannerObjectiveKind objectiveKind)
    {
        if (committedInteractableKey == interactable.Key && committedInteractableObjectiveKind == objectiveKind)
            return;

        committedInteractableKey = interactable.Key;
        committedInteractableObjectiveKind = objectiveKind;
        lastInteractGameObjectId = 0;
        nextInteractAttemptUtc = DateTime.MinValue;
    }

    private ObservedInteractable? ResolveCommittedInteractable(ObservationSnapshot observation)
    {
        if (string.IsNullOrWhiteSpace(committedInteractableKey))
            return null;

        var interactable = observation.LiveInteractables.FirstOrDefault(x => x.Key == committedInteractableKey);
        if (interactable is not null)
            return interactable;

        ClearInteractableCommitment();
        return null;
    }

    private void ClearInteractableCommitment()
    {
        committedInteractableKey = null;
        committedInteractableObjectiveKind = PlannerObjectiveKind.None;
        lastInteractGameObjectId = 0;
        nextInteractAttemptUtc = DateTime.MinValue;
    }

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
        if (navigationActive && movementTargetGameObjectId == gameObjectId && now < nextNavigationCommandUtc)
            return;

        if (TrySendCommand(string.Format(
                CultureInfo.InvariantCulture,
                "/vnav moveto {0:F2} {1:F2} {2:F2}",
                destination.X,
                destination.Y,
                destination.Z)))
        {
            navigationActive = true;
            movementTargetGameObjectId = gameObjectId;
            nextNavigationCommandUtc = now + NavigationRetryCooldown;
        }
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
        var approachPoint = BuildPreferredApproachPoint(playerPosition, objectPosition);
        return new Vector3(approachPoint.X, playerPosition.Y, approachPoint.Z);
    }

    private static float GetHorizontalDistance(Vector3 a, Vector3 b)
    {
        var x = a.X - b.X;
        var z = a.Z - b.Z;
        return MathF.Sqrt((x * x) + (z * z));
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

    private static ulong BuildRecoveryTargetId(PlannerSnapshot planner)
    {
        var nameHash = (ulong)(planner.TargetName?.GetHashCode(StringComparison.OrdinalIgnoreCase) ?? 0);
        return 0xF000000000000000UL | ((ulong)planner.ObjectiveKind << 32) | (nameHash & 0x00000000FFFFFFFFUL);
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
