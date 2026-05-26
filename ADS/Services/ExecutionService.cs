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
    private const float TreasureFollowerRouteVerticalCap = 5.0f;
    private const float BossFightCombatGhostRange = 5.0f;
    private const float RequiredInteractionConsumedRelocationRange = 20.0f;
    private const float TreasureDoorFollowThroughDistance = 10.0f;
    private const float TreasureDoorFollowThroughArrivalRange = 2.0f;
    private const float TreasureDoorFollowThroughStaleVerticalDelta = 20.0f;
    private const float TreasureDoorPlannerSameFloorVerticalDelta = 6.0f;
    private const uint AquapolisTerritoryTypeId = 558;
    private const uint PraetoriumTerritoryTypeId = 1044;
    private const uint BigCheekedCakeMonsters = 6942069; // Hello adventurer, are you enjoying my ai slop today :D
    private const uint PraetoriumMagitekCannonActionId = 1128;
    private const uint PraetoriumPhotonStreamActionId = 1129;
    private const float MountedCombatClusterRadius = 6.0f;
    private const float PhotonStreamFrontDotThreshold = 0.5f;
    private const float PraetoriumTrailingPackDotThreshold = 0.65f;
    private const float PraetoriumRearTargetMaxVerticalDelta = 10.0f;
    private const float DefaultMountedCombatActionRange = 50.0f;
    private const float PraetoriumBlindRearFallbackDistance = 1.0f;
    private const float RecoveryGhostRetireRadius = 8.0f;
    private const float RecoveryClusterArrivalRange = RecoveryGhostRetireRadius;
    private const float RecoveryTargetSimilarityRadius = RecoveryGhostRetireRadius;
    private const float CloseRangeInteractFallbackHorizontalDistance = 2.5f;
    private const float CloseRangeInteractFallbackVerticalCap = 4.0f;
    private const float CloseRangeInteractFallbackProgressMargin = 0.2f;
    private const float TreasureCofferProgressMargin = 2.0f;
    private const float TreasureRouteStuckProgressDistance = 0.5f;
    private const float TreasureRouteStuckNudgeXOffset = 0.5f;
    private const float AquapolisRouteWiggleSideOffset = 1.25f;
    private const int TreasureFollowerDoorFollowThroughNudgeAttemptLimit = 2;
    private const float TreasureDoorNudgeProgressDistance = 0.5f;
    private const float TreasureDoorNudgeSideOffset = 1.25f;
    private const float TreasureDoorNudgeForwardOffset = 1.5f;
    //private const float LeaveTreasureSweepHorizontalRange = 20.0f;
    private const float LeaveTreasureSweepHorizontalRange = 50.0f;  //this might be better range i saw it skip a few times in a relatively normal boss arena.
    private const float LeaveTreasureSweepVerticalCap = 6.0f;
    private const float ManualDestinationSatisfiedByProgressionRadius = 8.0f;
    private const float ManualDestinationNoProgressDistance = 0.5f;
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
    private static readonly TimeSpan TreasureCofferLockedRetryDelay = TimeSpan.FromSeconds(15.0);
    private static readonly TimeSpan TreasureRouteStuckTimeout = TimeSpan.FromSeconds(10.0);
    private static readonly TimeSpan AquapolisRouteWiggleHoldDuration = TimeSpan.FromSeconds(1.0);
    private static readonly TimeSpan ManualDestinationNoProgressTimeout = TimeSpan.FromSeconds(12.0);
    private static readonly TimeSpan TreasureDoorNudgeStuckTimeout = TimeSpan.FromSeconds(10.0);
    private static readonly TimeSpan TreasureDoorNudgeHoldDuration = TimeSpan.FromSeconds(2.5);
    private static readonly TimeSpan TreasureFollowerDoorFollowThroughTruthTimeout = TimeSpan.FromSeconds(8.0);
    private static readonly TimeSpan TreasureFollowerPostTransitSettleDelay = TimeSpan.FromSeconds(2.0);
    private static readonly TimeSpan TreasureFollowerPostCutsceneSettleDelay = TimeSpan.FromSeconds(4.0);
    private static readonly TimeSpan ResetCameraBeforeInteractDelay = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan LeaveUiRetryCooldown = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan LeaveLootDistributionDelay = TimeSpan.FromSeconds(10.0);
    private static readonly TimeSpan LeaveTreasureSweepSettleDelay = TimeSpan.FromSeconds(2.0);
    private static readonly TimeSpan LeaveFinalCofferSpawnGrace = TimeSpan.FromSeconds(2.0);
    private static readonly TimeSpan LeavePromptLogCooldown = TimeSpan.FromSeconds(5.0);
    private static readonly TimeSpan LivePartyDamageProgressWindow = TimeSpan.FromSeconds(4.0);
    private const float LivePartyDamageProgressRange = 35.0f;
    private const string TreasureCofferLockedTightMessage = "The coffer is locked tight.";

    private readonly IDataManager dataManager;
    private readonly IObjectTable objectTable;
    private readonly ITargetManager targetManager;
    private readonly ICommandManager commandManager;
    private readonly ObservationMemoryService observationMemoryService;
    private readonly DungeonFrontierService dungeonFrontierService;
    private readonly MapFlagService mapFlagService;
    private readonly ObjectPriorityRuleService objectPriorityRuleService;
    private readonly Configuration configuration;
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
    private bool unsafeTransitionNavigationStopLatched;
    private bool treasureDungeonCombatStopLatched;
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
    private string? treasureCofferLockedRetryKey;
    private DateTime treasureCofferLockedNextRetryUtc;
    private string? treasureRouteStuckTargetKey;
    private Vector3? treasureRouteStuckBaselinePosition;
    private DateTime treasureRouteStuckLastProgressUtc;
    private int treasureRouteStuckOffsetAttempt;
    private string? aquapolisRouteWiggleTargetKey;
    private Vector3? aquapolisRouteWiggleBaselinePosition;
    private DateTime aquapolisRouteWiggleLastProgressUtc;
    private int aquapolisRouteWiggleAttempt;
    private DateTime aquapolisRouteWiggleUntilUtc = DateTime.MinValue;
    private Vector3 aquapolisRouteWiggleDestination;
    private string? treasureFollowerDoorFollowThroughCandidateKey;
    private DungeonFrontierPoint? treasureFollowerDoorFollowThroughCandidatePoint;
    private DungeonFrontierPoint? treasureFollowerDoorFollowThroughPoint;
    private bool treasureFollowerDoorFollowThroughTransitObserved;
    private bool treasureFollowerDoorFollowThroughReached;
    private DateTime treasureFollowerDoorFollowThroughReachedUtc = DateTime.MinValue;
    private bool treasureFollowerRouteTransitHoldLatched;
    private bool treasureFollowerRouteTransitCutsceneSeen;
    private DateTime treasureFollowerPostTransitSettleUntilUtc = DateTime.MinValue;
    private string treasureFollowerPostTransitSettleReason = string.Empty;
    private bool treasureFollowerPostTransitSettleStopSent;
    private string? treasureDoorNudgeTargetKey;
    private Vector3? treasureDoorNudgeBaselinePosition;
    private DateTime treasureDoorNudgeLastProgressUtc;
    private int treasureDoorNudgeAttempt;
    private DateTime treasureDoorNudgeUntilUtc = DateTime.MinValue;
    private Vector3 treasureDoorNudgeDestination;
    private string? manualDestinationNoProgressTargetKey;
    private ulong? manualDestinationNavigationTargetId;
    private Vector3? manualDestinationNoProgressBaselinePosition;
    private DateTime manualDestinationLastProgressUtc;
    private string currentManualDestinationTarget = string.Empty;
    private float? currentManualDestinationDistance;
    private string? closeRangeInteractFallbackKey;
    private DateTime closeRangeInteractFallbackLastProgressUtc;
    private float closeRangeInteractFallbackBestHorizontalDistance = float.MaxValue;
    private DateTime nextMountedCombatAttemptUtc;
    private DateTime nextLeaveUiAttemptUtc;
    private DateTime leaveLootDistributionWaitUntilUtc;
    private DateTime leaveTreasureSweepClearSinceUtc;
    private DateTime leaveTreasureSweepGraceUntilUtc;
    private bool leaveTreasureInteractionSent;
    private bool leaveTreasureSweepStarted;
    private bool leaveTreasureSweepGraceLogged;
    private bool leaveTreasureSweepClearLogged;
    private bool leaveLootDistributionWaitLogged;
    private bool leaveDutyExitArmed;
    private string lastLoggedLeaveTreasureKey = string.Empty;
    private string lastLoggedLeavePromptKey = string.Empty;
    private DateTime lastLoggedLeavePromptAtUtc = DateTime.MinValue;
    private readonly Dictionary<ulong, uint> recentVisiblePartyMemberHp = [];
    private readonly Dictionary<ulong, uint> recentNearbyMonsterHp = [];
    private DateTime livePartyDamageProgressUntilUtc;
    private string lastMountedCombatYieldObjective = string.Empty;
    private string lastMountedCombatPhotonFallbackKey = string.Empty;
    private string lastMountedCombatRearPreferenceKey = string.Empty;
    private string lastMountedCombatAggressionModeKey = string.Empty;
    private string lastMountedCombatNoMagitekCannonResolutionKey = string.Empty;
    private string lastMountedCombatBlindRearFallbackKey = string.Empty;
    private string lastPraetoriumMagitekArmorCameraIndependentInteractLogKey = string.Empty;
    private string? resetCameraBeforeInteractKey;
    private DateTime resetCameraBeforeInteractReadyUtc;
    private string lastResetCameraBeforeInteractLogKey = string.Empty;
    private readonly HashSet<string> loggedLiveTargetNavigationModes = [];
    private string? bossFightCombatGhostKey;
    private string bossFightCombatGhostName = string.Empty;
    private uint bossFightCombatGhostMapId;
    private string currentDialogAutomationStatus = string.Empty;
    private bool higherLowerAutomationHold;
    private bool higherLowerBlocksDutyExit;
    private string higherLowerAutomationStatus = string.Empty;
    private DateTime higherLowerLastActivityUtc = DateTime.MinValue;
    private bool higherLowerOwnedMovementHoldLogged;
    private bool higherLowerDutyExitHoldLogged;
    private string lastTreasureFollowerSelectYesnoPreserveLogKey = string.Empty;
    private string lastTreasureFollowerDoorFollowThroughRetargetLogKey = string.Empty;

    public ExecutionService(
        IDataManager dataManager,
        IObjectTable objectTable,
        ITargetManager targetManager,
        ICommandManager commandManager,
        ObservationMemoryService observationMemoryService,
        DungeonFrontierService dungeonFrontierService,
        MapFlagService mapFlagService,
        ObjectPriorityRuleService objectPriorityRuleService,
        Configuration configuration,
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
        this.configuration = configuration;
        this.log = log;
    }

    private readonly record struct MountedCombatAction(uint ActionId, string Name, bool TargetArea, float Range);
    private readonly record struct MountedCombatTarget(ObservedMonster Observed, IGameObject GameObject, float HorizontalDistance, float Distance3d, float VerticalDelta);
    private readonly record struct MountedCombatClusterTarget(ObservedMonster Observed, IGameObject GameObject, float HorizontalDistance, float Distance3d, float VerticalDelta, int ClusterCount);
    private readonly record struct MountedCombatTargetResolution(MountedCombatAction Action, MountedCombatTarget Target, bool UseGroundTarget, string TargetSummary, bool UsedRearPreference);
    private readonly record struct TreasureRouteNavigationDecision(Vector3 Destination, bool ForceNavigationRestart, bool NudgeApplied, int NudgeAttempt, bool CandidateFailed);
    private readonly record struct TreasureDoorNavigationDecision(Vector3 Destination, ulong NavigationTargetId, string StatusText, bool StuckRecoveryExhausted);

    public OwnershipMode CurrentMode { get; private set; } = OwnershipMode.Idle;
    public ExecutionPhase CurrentPhase { get; private set; } = ExecutionPhase.Idle;
    public string LastStatus { get; private set; } = "Idle.";

    public TreasureDungeonRole TreasureDungeonRole { get; private set; } = ADS.Models.TreasureDungeonRole.MapOpener;

    public string TreasureDungeonRoleSource { get; private set; } = "Default";

    public string TreasureDungeonRoleDetail { get; private set; } = "No external treasure-role source was active; ADS keeps map-opener behavior.";

    private TreasureDungeonRole EffectiveTreasureDungeonRole
        => dungeonFrontierService.EffectiveTreasureDungeonRole;

    public string CurrentManualDestinationTarget
        => currentManualDestinationTarget;

    public float? CurrentManualDestinationDistance
        => currentManualDestinationDistance;

    public double? ManualDestinationLastProgressAgeSeconds
        => manualDestinationNoProgressTargetKey is null || manualDestinationLastProgressUtc == DateTime.MinValue
            ? null
            : Math.Max(0, (DateTime.UtcNow - manualDestinationLastProgressUtc).TotalSeconds);

    public bool TreasureFollowerDoorFollowThroughActive
        => treasureFollowerDoorFollowThroughCandidateKey is not null;

    public string TreasureFollowerDoorFollowThroughCandidateKey
        => treasureFollowerDoorFollowThroughCandidateKey ?? string.Empty;

    public string TreasureFollowerDoorFollowThroughCandidateName
        => treasureFollowerDoorFollowThroughCandidatePoint?.Name ?? string.Empty;

    public string TreasureFollowerDoorFollowThroughTarget
        => treasureFollowerDoorFollowThroughPoint is { } point
            ? $"{point.Name} @ {FormatVector(point.Position)}"
            : string.Empty;

    public string TreasureFollowerDoorFollowThroughStage
    {
        get
        {
            if (HasPendingTreasureFollowerDoorFollowThrough())
                return treasureFollowerDoorFollowThroughReached ? "ClearThroughReached" : "ClearThrough";

            return EffectiveTreasureDungeonRole == ADS.Models.TreasureDungeonRole.Follower
                   && dungeonFrontierService.CurrentMode == FrontierMode.TreasureDungeon
                   && dungeonFrontierService.CurrentTarget is { IsTreasurePassageCandidate: true }
                ? "DoorThroat"
                : string.Empty;
        }
    }

    public double TreasureFollowerPostTransitSettleRemainingSeconds
        => GetRemainingSeconds(treasureFollowerPostTransitSettleUntilUtc);

    public bool IsOwned
        => CurrentMode is OwnershipMode.OwnedStartOutside or OwnershipMode.OwnedStartInside or OwnershipMode.OwnedResumeInside or OwnershipMode.Leaving;

    public void SetTreasureDungeonRole(TreasureDungeonRoleInference inference)
    {
        TreasureDungeonRole = inference.Role;
        TreasureDungeonRoleSource = inference.Source;
        TreasureDungeonRoleDetail = inference.Detail;
        ClearTreasureFollowerDoorFollowThrough(resetStuckTracking: true);
        ClearTreasureFollowerPostTransitSettle("treasure role changed");
    }

    public bool HandleChatMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message)
            || !message.Contains(TreasureCofferLockedTightMessage, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!IsActiveOwnedDutyMode()
            || committedInteractableObjectiveKind != PlannerObjectiveKind.TreasureCoffer
            || string.IsNullOrWhiteSpace(committedInteractableKey))
        {
            return false;
        }

        var now = DateTime.UtcNow;
        ArmTreasureCofferLockedRetry(committedInteractableKey, now, "locked-tight chat");
        observationMemoryService.ClearTreasureCofferSuppression(committedInteractableKey, "locked-tight chat");
        StopMovementAssists();
        SetPhase(
            ExecutionPhase.AttemptingInteractableObjective,
            $"Treasure coffer reported locked tight. ADS is holding the committed coffer and will retry in {TreasureCofferLockedRetryDelay.TotalSeconds:0}s.");
        return true;
    }

    public void ReleaseHeldMovementKeys(string reason)
    {
        var hadNudge = treasureDoorNudgeTargetKey != null || treasureDoorNudgeUntilUtc != DateTime.MinValue;
        var hadAquapolisWiggle = aquapolisRouteWiggleTargetKey != null || aquapolisRouteWiggleUntilUtc != DateTime.MinValue;
        ClearBossFightCombatGhost(reason);
        ClearTreasureFollowerDoorFollowThrough(resetStuckTracking: true);
        ClearTreasureFollowerPostTransitSettle(reason);
        ResetTreasureDoorJiggleTracking(releaseKeys: true);
        if (hadNudge)
            log?.Information($"[ADS] Cleared treasure door vnav side-nudge recovery during {reason}.");
        if (hadAquapolisWiggle)
            log?.Information($"[ADS] Cleared Aquapolis passage vnav side-step recovery during {reason}.");
    }

    private bool IsActiveOwnedDutyMode()
        => CurrentMode is OwnershipMode.OwnedStartOutside or OwnershipMode.OwnedStartInside or OwnershipMode.OwnedResumeInside;

    public void SetHigherLowerAutomationHold(bool hold, string status, bool blocksDutyExit, DateTime lastActivityUtc)
    {
        higherLowerAutomationHold = hold;
        higherLowerBlocksDutyExit = blocksDutyExit;
        higherLowerAutomationStatus = status;
        higherLowerLastActivityUtc = lastActivityUtc;
        if (!hold)
            higherLowerOwnedMovementHoldLogged = false;
        if (!blocksDutyExit)
            higherLowerDutyExitHoldLogged = false;
    }

    private string BuildTreasureRoleStatus()
        => TreasureDungeonRole == ADS.Models.TreasureDungeonRole.Follower
            ? $"Treasure role: follower ({TreasureDungeonRoleSource})."
            : $"Treasure role: map opener ({TreasureDungeonRoleSource}).";

    public bool StartDutyFromOutside()
    {
        ClearInteractableCommitment();
        ClearCommittedForceMarchManualDestination();
        ClearBossFightCombatGhost("outside start");
        ClearTreasureFollowerPostTransitSettle("outside start");
        CurrentMode = OwnershipMode.OwnedStartOutside;
        SetPhase(ExecutionPhase.OutsideQueue, "Queued outside start. ADS will claim ownership when you enter instanced duty.");
        return true;
    }

    public bool StartDutyFromInside(DutyContextSnapshot context)
    {
        ClearInteractableCommitment();
        ClearCommittedForceMarchManualDestination();
        ClearBossFightCombatGhost("inside start");
        ClearTreasureFollowerPostTransitSettle("inside start");
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
            $"Owned inside {context.CurrentDuty?.EnglishName}. ADS now runs the staged execution phase engine for this duty. {BuildTreasureRoleStatus()}");
        return true;
    }

    public bool ResumeDutyFromInside(DutyContextSnapshot context)
    {
        ClearInteractableCommitment();
        ClearCommittedForceMarchManualDestination();
        ClearBossFightCombatGhost("inside resume");
        ClearTreasureFollowerPostTransitSettle("inside resume");
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
            $"Resumed ownership inside {context.CurrentDuty?.EnglishName}. ADS execution phases are active. {BuildTreasureRoleStatus()}");
        return true;
    }

    public bool LeaveDuty(DutyContextSnapshot context, bool considerTreasureCoffers)
    {
        if (!context.InInstancedDuty)
        {
            SetPhase(CurrentPhase, "Leave requires being inside duty.");
            return false;
        }

        StopMovementAssists();
        ClearInteractableCommitment();
        ClearCommittedForceMarchManualDestination();
        ClearBossFightCombatGhost("manual leave request");
        ClearTreasureFollowerPostTransitSettle("manual leave request");
        ResetLeaveState();
        if (considerTreasureCoffers)
            BeginLeaveTreasureSweep(DateTime.UtcNow, "manual leave request");
        CurrentMode = OwnershipMode.Leaving;
        SetPhase(
            ExecutionPhase.LeavingDuty,
            considerTreasureCoffers
                ? "Leave requested. Final treasure sweep started before duty exit."
                : "Leave requested. Treasure-coffer scan is off; preparing duty exit.");
        return true;
    }

    public bool BeginDutyCompletionTreasureSweep(DutyContextSnapshot context, string dutyName)
    {
        if (!context.InInstancedDuty)
        {
            SetPhase(CurrentPhase, $"Duty completed: {dutyName}, but ADS is already outside duty.");
            return false;
        }

        StopMovementAssists();
        ClearInteractableCommitment();
        ClearCommittedForceMarchManualDestination();
        ClearBossFightCombatGhost("duty completion treasure sweep");
        ClearTreasureFollowerPostTransitSettle("duty completion treasure sweep");
        ResetRecoveryHold();
        ResetLeaveState();
        BeginLeaveTreasureSweep(DateTime.UtcNow, $"DutyCompleted for {dutyName}");
        CurrentMode = OwnershipMode.Leaving;
        SetPhase(
            ExecutionPhase.LeavingDuty,
            $"Duty completed: {dutyName}. Final treasure sweep started; ADS will arm duty exit after coffers and loot settle.");
        return true;
    }

    public void Stop(DutyContextSnapshot context)
    {
        StopMovementAssists();
        ClearInteractableCommitment();
        ClearCommittedForceMarchManualDestination();
        ClearBossFightCombatGhost("stop");
        ClearTreasureFollowerPostTransitSettle("stop");
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
        ClearBossFightCombatGhost("duty complete");
        ClearTreasureFollowerPostTransitSettle("duty complete");
        ResetRecoveryHold();
        ResetLeaveState();
        CurrentMode = OwnershipMode.Observing;
        SetPhase(
            ExecutionPhase.ObservingOnly,
            $"Duty completed: {dutyName}. ADS stopped automation and cleared recovery follow-through; use Start/Resume for another run.");
    }

    public void Update(
        DutyContextSnapshot context,
        PlannerSnapshot planner,
        ObservationSnapshot observation,
        bool pluginEnabled,
        bool considerTreasureCoffers,
        string dialogAutomationStatus)
    {
        currentDialogAutomationStatus = dialogAutomationStatus;
        UpdateUnsafeTransitionNavigationStop(context);
        UpdateTreasureFollowerRouteTransitClearStop(context, planner);
        ResetTreasureDungeonCombatNavigationStopLatchIfClear(context, planner);
        if (!context.InCombat)
            ClearBossFightCombatGhost("combat cleared");

        if (!pluginEnabled)
        {
            StopMovementAssists();
            ClearInteractableCommitment();
            ClearCommittedForceMarchManualDestination();
            ClearBossFightCombatGhost("plugin disabled");
            ClearTreasureFollowerPostTransitSettle("plugin disabled");
            ResetLeaveState();
            CurrentMode = OwnershipMode.Idle;
            SetPhase(ExecutionPhase.Idle, "ADS disabled.");
            return;
        }

        if (context.IsUnsafeTransition)
        {
            ClearBossFightCombatGhost("unsafe transition");
            UpdateUnsafeTransitionHold(context);
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
                    ClearBossFightCombatGhost("outside start waiting outside duty");
                    ClearTreasureFollowerPostTransitSettle("outside start waiting outside duty");
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
                    ClearBossFightCombatGhost("duty ended");
                    ClearTreasureFollowerPostTransitSettle("duty ended");
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
                    ClearBossFightCombatGhost("duty exit detected");
                    ClearTreasureFollowerPostTransitSettle("duty exit detected");
                    observationMemoryService.Reset();
                    dungeonFrontierService.Reset();
                    ResetLeaveState();
                    CurrentMode = OwnershipMode.Idle;
                    SetPhase(ExecutionPhase.Idle, "Duty exit detected; ADS ownership released.");
                    return;
                }

                UpdateLeaveDuty(context, observation, considerTreasureCoffers);
                return;

            case OwnershipMode.Failed:
                if (!context.InInstancedDuty)
                {
                    StopMovementAssists();
                    ClearInteractableCommitment();
                    ClearCommittedForceMarchManualDestination();
                    ClearBossFightCombatGhost("failure cleared outside duty");
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
            ClearBossFightCombatGhost("ownership observing");
            ClearTreasureFollowerPostTransitSettle("ownership observing");
            ResetLeaveState();
            CurrentMode = OwnershipMode.Observing;
            SetPhase(ExecutionPhase.ObservingOnly, "Observing only; ADS does not currently own this duty.");
            return;
        }

        StopMovementAssists();
        ClearInteractableCommitment();
        ClearCommittedForceMarchManualDestination();
        ClearBossFightCombatGhost("ownership idle");
        ClearTreasureFollowerPostTransitSettle("ownership idle");
        ResetLeaveState();
        CurrentMode = OwnershipMode.Idle;
        SetPhase(ExecutionPhase.Idle, "Idle.");
    }

    private void UpdateOwnedPhase(DutyContextSnapshot context, PlannerSnapshot planner, ObservationSnapshot observation, string prefix)
    {
        if (context.IsUnsafeTransition || planner.Mode == PlannerMode.UnsafeTransition)
        {
            ObserveTreasureFollowerRouteTransit(context, "unsafe transition hold");
            if (pendingProgressionInteractable is not null)
            {
                observationMemoryService.MarkProgressionInteractionSent(context, pendingProgressionInteractable);
                TryRetirePendingSatisfiedManualDestination(pendingProgressionInteractable, objectTable.LocalPlayer?.Position);
            }

            ClearInteractableCommitment();
            ClearCommittedForceMarchManualDestination();
            ClearBossFightCombatGhost("unsafe transition");
            ResetRecoveryHold();
            StopMovementAssists(preserveTreasureFollowerDoorFollowThrough: ShouldPreserveTreasureFollowerDoorFollowThroughForTransit(context));
            SetPhase(ExecutionPhase.TransitionHold, $"{prefix} Waiting for safe post-transition duty truth before advancing.");
            return;
        }

        if (ShouldHoldTreasureFollowerRouteTransit(context, planner))
        {
            ObserveTreasureFollowerRouteTransit(context, "treasure route transit hold");
            StopNavigationForTreasureRouteTransit();
            SetPhase(
                ExecutionPhase.TransitionHold,
                $"{prefix} Waiting for treasure entry/passage movement to settle.");
            return;
        }

        if (TryHoldTreasureFollowerPostTransitSettle(prefix))
            return;

        if (higherLowerAutomationHold)
        {
            PauseMovementAssistsForHigherLower();
            LogHigherLowerOwnedMovementHoldOnce();
            SetPhase(
                ExecutionPhase.AttemptingInteractableObjective,
                $"{prefix} {higherLowerAutomationStatus}");
            return;
        }

        if (TryHoldPendingProgressionInteractResult(context, planner, observation, prefix))
            return;

        if (GameInteractionHelper.IsAddonVisible("SelectYesno"))
        {
            StopMovementAssistsForSelectYesno(planner);
            SetPhase(
                ExecutionPhase.AttemptingInteractableObjective,
                $"{prefix} {BuildSelectYesnoHoldStatus("SelectYesno is visible; ADS is holding movement while dialog automation resolves the prompt.")}");
            return;
        }

        if (IsForceMarchPlannerObjective(planner.ObjectiveKind)
            || committedForceMarchManualDestination is not null)
        {
            TryFirePraetoriumMountedCombatAction(context, observation, prefix, preserveMovement: true);
        }

        if (TryAdvanceCommittedForceMarchManualDestination(context, planner, prefix))
            return;

        if (TryHoldTreasureFollowerCombatYield(context, planner, observation, prefix))
            return;

        if ((context.InCombat || planner.Mode == PlannerMode.Combat)
            && !ShouldBypassCombatHold(context, planner, observation))
        {
            ResetRecoveryHold();
            StopMovementAssists(preserveTreasureFollowerDoorFollowThrough: HasPendingTreasureFollowerDoorFollowThrough());
            SetPhase(ExecutionPhase.CombatHold, BuildCombatHoldStatus(prefix, planner, observation));
            return;
        }

        if (TryAdvancePendingTreasureFollowerDoorFollowThrough(context, planner, prefix))
            return;

        var lockedRetryInteractable = ResolveCommittedInteractable(context, observation);
        if (lockedRetryInteractable is not null
            && TryAdvanceCommittedTreasureCofferLockedRetry(context, lockedRetryInteractable, prefix))
        {
            return;
        }

        if (TryAdvancePraetoriumMountedCombat(context, planner, observation, prefix))
            return;

        if ((planner.Mode != PlannerMode.Progression || !IsFrontierLikePlannerObjective(planner.ObjectiveKind))
            && !ShouldPreserveTreasureFollowerDoorFollowThroughForTransit(context))
            ClearTreasureFollowerDoorFollowThrough(resetStuckTracking: true);

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
                    PrepareInteractableSwitch(
                        context,
                        committedInteractable,
                        committedInteractableObjectiveKind,
                        plannerInteractable,
                        planner.ObjectiveKind,
                        switchReason);
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
                TryAdvanceMonsterObjective(context, planner, observation, $"{prefix}");
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

        if (dungeonFrontierService.TreasureFollowerDoorChaseHoldActive)
        {
            StopMovementAssists();
            var holdStatus = string.IsNullOrWhiteSpace(dungeonFrontierService.CurrentLabelStatus)
                ? "Waiting for treasure follower door-chase gate before chasing passage doors."
                : dungeonFrontierService.CurrentLabelStatus;
            SetPhase(ExecutionPhase.WaitingForTruth, $"{prefix} {holdStatus}");
            return;
        }

        StopMovementAssists();
        SetPhase(ExecutionPhase.WaitingForTruth, $"{prefix} Waiting for a stronger live objective signal.");
    }

    private void UpdateUnsafeTransitionHold(DutyContextSnapshot context)
    {
        ObserveTreasureFollowerRouteTransit(context, "unsafe transition hold");
        if (pendingProgressionInteractable is not null)
        {
            observationMemoryService.MarkProgressionInteractionSent(context, pendingProgressionInteractable);
            TryRetirePendingSatisfiedManualDestination(pendingProgressionInteractable, objectTable.LocalPlayer?.Position);
        }

        ClearInteractableCommitment();
        ClearCommittedForceMarchManualDestination();
        ClearBossFightCombatGhost("unsafe transition hold");
        ResetRecoveryHold();
        StopMovementAssists(preserveTreasureFollowerDoorFollowThrough: ShouldPreserveTreasureFollowerDoorFollowThroughForTransit(context));
        SetPhase(
            ExecutionPhase.TransitionHold,
            $"Unsafe transition active ({FormatUnsafeTransitionFlags(context)}); ADS stopped navigation and is waiting for stable post-transition duty truth.");
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

    private void TryAdvanceMonsterObjective(DutyContextSnapshot context, PlannerSnapshot planner, ObservationSnapshot observation, string prefix)
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
        if (planner.ObjectiveKind == PlannerObjectiveKind.BossFightMonster && context.InCombat)
        {
            if (IsBossFightCombatGhosted(observedMonster))
            {
                StopMovementAssists();
                SetPhase(
                    ExecutionPhase.CombatHold,
                    $"{prefix} BossFight target {observedMonster.Name} already reached {BossFightCombatGhostRange:0.0}y in this combat. ADS is holding progression and combat owns movement until combat clears.");
                return;
            }

            if (targetDistance <= BossFightCombatGhostRange)
            {
                ArmBossFightCombatGhost(observedMonster, targetDistance);
                StopMovementAssists();
                SetPhase(
                    ExecutionPhase.CombatHold,
                    $"{prefix} BossFight target {observedMonster.Name} reached {targetDistance:0.0}y (<= {BossFightCombatGhostRange:0.0}y). ADS stopped navigation and combat owns movement until combat clears.");
                return;
            }
        }

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
            lastMountedCombatBlindRearFallbackKey = string.Empty;
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
            .OrderBy(x => x.HorizontalDistance)
            .FirstOrDefault();
        LogLiveTargetNavigation("mounted-combat", approachTarget.GameObject.GameObjectId, approachTarget.Observed.Name, localPlayerPosition, approachTarget.GameObject.Position, approachTarget.HorizontalDistance, mountedCombat: true);
        TryBeginNavigation(approachTarget.GameObject.GameObjectId, approachTarget.GameObject.Position);
        SetPhase(
            ExecutionPhase.MountedDutyCombat,
            $"{prefix} Mounted Praetorium combat is moving toward {approachTarget.Observed.Name} (XZ {approachTarget.HorizontalDistance:0.0}y, Y {approachTarget.VerticalDelta:0.0}y) using the live target position as the navmesh destination to enter mount weapon range while no force-march objective is active.");
        return true;
    }

    private bool TryFirePraetoriumMountedCombatAction(
        DutyContextSnapshot context,
        ObservationSnapshot observation,
        string prefix,
        bool preserveMovement)
    {
        return TryBuildPraetoriumMountedCombatState(context, observation, out var localPlayerPosition, out var mountedActions, out var targets, requireTargets: false)
               && TryFireReadyMountedCombatAction(localPlayerPosition, mountedActions, targets, prefix, preserveMovement);
    }

    private bool TryBuildPraetoriumMountedCombatState(
        DutyContextSnapshot context,
        ObservationSnapshot observation,
        out Vector3 localPlayerPosition,
        out MountedCombatAction[] mountedActions,
        out List<MountedCombatTarget> targets,
        bool requireTargets = true)
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
            .Select(x =>
            {
                var horizontalDistance = GetHorizontalDistance(playerPosition, x.GameObject!.Position);
                var verticalDelta = MathF.Abs(x.GameObject.Position.Y - playerPosition.Y);
                return new MountedCombatTarget(
                    x.Observed,
                    x.GameObject!,
                    horizontalDistance,
                    Vector3.Distance(playerPosition, x.GameObject.Position),
                    verticalDelta);
            })
            .ToList();
        return !requireTargets || targets.Count > 0;
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

            if (TryExecuteBlindRearMagitekCannonFallback(localPlayerPosition, magitekCannon, prefix, preserveMovement))
            {
                lastMountedCombatPhotonFallbackKey = string.Empty;
                lastMountedCombatNoMagitekCannonResolutionKey = string.Empty;
                return true;
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
            lastMountedCombatBlindRearFallbackKey = string.Empty;
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
            if (TryGetBestMountedGroundTarget(targets, action, out var groundTarget))
            {
                resolution = new MountedCombatTargetResolution(
                    action,
                    new MountedCombatTarget(
                        groundTarget.Observed,
                        groundTarget.GameObject,
                        groundTarget.HorizontalDistance,
                        groundTarget.Distance3d,
                        groundTarget.VerticalDelta),
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
            resolution = new MountedCombatTargetResolution(
                action,
                targetedTarget,
                UseGroundTarget: false,
                $"XZ {targetedTarget.HorizontalDistance:0.0}y, Y {targetedTarget.VerticalDelta:0.0}y",
                UsedRearPreference: false);
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
            .OrderBy(target => target.HorizontalDistance)
            .ThenBy(target => target.VerticalDelta)
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
                    resolution = new MountedCombatTargetResolution(
                        action,
                        new MountedCombatTarget(
                            preferredTrailingTarget.Observed,
                            preferredTrailingTarget.GameObject,
                            preferredTrailingTarget.HorizontalDistance,
                            preferredTrailingTarget.Distance3d,
                            preferredTrailingTarget.VerticalDelta),
                        UseGroundTarget: true,
                        rearCandidates.Length > 0
                            ? $"strict rear trailing-pack preference (XZ {preferredTrailingTarget.HorizontalDistance:0.0}y, Y {preferredTrailingTarget.VerticalDelta:0.0}y, cluster {preferredTrailingTarget.ClusterCount})"
                            : $"aggressive trailing-pack preference (XZ {preferredTrailingTarget.HorizontalDistance:0.0}y, Y {preferredTrailingTarget.VerticalDelta:0.0}y, cluster {preferredTrailingTarget.ClusterCount})",
                        UsedRearPreference: true);
                    return true;
                }
            }
            else if (preferredCandidates.Length > 0
                     && (TryGetBestMountedTargetForAction(action.ActionId, preferredCandidates, out var targetedRearTarget)
                         || TryGetNearestMountedCombatTarget(preferredCandidates, out targetedRearTarget)))
            {
                resolution = new MountedCombatTargetResolution(
                    action,
                    targetedRearTarget,
                    UseGroundTarget: false,
                    rearCandidates.Length > 0
                        ? $"strict rear trailing-pack preference (XZ {targetedRearTarget.HorizontalDistance:0.0}y, Y {targetedRearTarget.VerticalDelta:0.0}y)"
                        : $"aggressive trailing-pack preference (XZ {targetedRearTarget.HorizontalDistance:0.0}y, Y {targetedRearTarget.VerticalDelta:0.0}y)",
                    UsedRearPreference: true);
                return true;
            }
        }

        if (action.TargetArea)
        {
            var directShotTarget = inEnvelopeTargets[0];
            resolution = new MountedCombatTargetResolution(
                action,
                directShotTarget,
                UseGroundTarget: true,
                $"aggressive direct shot on refresh (XZ {directShotTarget.HorizontalDistance:0.0}y, Y {directShotTarget.VerticalDelta:0.0}y)",
                UsedRearPreference: false);
            return true;
        }
        else if (TryGetBestMountedTargetForAction(action.ActionId, inEnvelopeTargets, out var targetedTarget)
                 || TryGetNearestMountedCombatTarget(inEnvelopeTargets, out targetedTarget))
        {
            resolution = new MountedCombatTargetResolution(
                action,
                targetedTarget,
                UseGroundTarget: false,
                $"aggressive direct shot on refresh (XZ {targetedTarget.HorizontalDistance:0.0}y, Y {targetedTarget.VerticalDelta:0.0}y)",
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

            var candidate = new MountedCombatClusterTarget(target.Observed, target.GameObject, target.HorizontalDistance, target.Distance3d, target.VerticalDelta, clusterCount);
            if (!found
                || candidate.ClusterCount > bestTarget.ClusterCount
                || (candidate.ClusterCount == bestTarget.ClusterCount && candidate.HorizontalDistance < bestTarget.HorizontalDistance))
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
                    $"front-facing fallback (XZ {groundTarget.HorizontalDistance:0.0}y, Y {groundTarget.VerticalDelta:0.0}y)",
                    UsedRearPreference: false);
                return true;
            }
        }
        else if (TryGetBestMountedTargetForAction(action.ActionId, frontTargets, out var targetedTarget)
                 || TryGetNearestMountedCombatTargetInRange(frontTargets, action, out targetedTarget))
        {
            resolution = new MountedCombatTargetResolution(
                action,
                targetedTarget,
                UseGroundTarget: false,
                $"front-facing fallback (XZ {targetedTarget.HorizontalDistance:0.0}y, Y {targetedTarget.VerticalDelta:0.0}y)",
                UsedRearPreference: false);
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

        log?.Information($"[ADS] Mounted Praetorium combat preferred aggressive trailing 1128 target {target.Observed.Name} (XZ {target.HorizontalDistance:0.0}y, Y {target.VerticalDelta:0.0}y).");
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

    private bool TryExecuteBlindRearMagitekCannonFallback(
        Vector3 localPlayerPosition,
        MountedCombatAction action,
        string prefix,
        bool preserveMovement)
    {
        if (!preserveMovement || action.ActionId != PraetoriumMagitekCannonActionId || !action.TargetArea)
            return false;

        var localPlayer = objectTable.LocalPlayer;
        if (localPlayer is null)
            return false;

        if (!TryObserveMountedCombatSendWindow(action, "blind rear fallback", prefix, preserveMovement))
            return false;

        var fallbackPosition = BuildMountedCombatBlindRearFallbackPosition(localPlayerPosition, localPlayer.Rotation);
        if (!TryUseGroundTargetAction(action.ActionId, fallbackPosition))
            return false;

        LogMountedCombatBlindRearFallback(fallbackPosition);
        lastMountedCombatRearPreferenceKey = string.Empty;
        lastMountedCombatNoMagitekCannonResolutionKey = string.Empty;
        nextMountedCombatAttemptUtc = DateTime.UtcNow + MountedCombatAttemptCooldown;
        ReportMountedCombatAction(
            prefix,
            action.Name,
            "blind rear fallback",
            preserveMovement,
            $"1.0y directly behind the player at {FormatVector(fallbackPosition)}");
        return true;
    }

    private void LogMountedCombatBlindRearFallback(Vector3 fallbackPosition)
    {
        const string fallbackKey = "force-march-blind-rear";
        if (string.Equals(lastMountedCombatBlindRearFallbackKey, fallbackKey, StringComparison.Ordinal))
            return;

        lastMountedCombatBlindRearFallbackKey = fallbackKey;
        log?.Information(
            $"[ADS] Mounted Praetorium combat used blind rear 1128 fallback during force-march because no valid mounted target resolved; ground target placed 1.0y behind the player at {FormatVector(fallbackPosition)}.");
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
                enforceVerticalArrivalCap: false,
                forcedArrivalRangeXz: null,
                forcedVerticalArrivalCap: null,
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

    private static bool IsFrontierLikePlannerObjective(PlannerObjectiveKind objectiveKind)
        => objectiveKind is PlannerObjectiveKind.Frontier
            or PlannerObjectiveKind.MapXzDestination
            or PlannerObjectiveKind.XyzDestination
            or PlannerObjectiveKind.MapXzForceMarchDestination
            or PlannerObjectiveKind.XyzForceMarchDestination;

    private bool ShouldHoldTreasureFollowerRouteTransit(DutyContextSnapshot context, PlannerSnapshot planner)
    {
        if (!context.IsTreasureRouteTransitHold
            || TreasureDungeonRole != ADS.Models.TreasureDungeonRole.Follower)
        {
            return false;
        }

        return true;
    }

    private bool ShouldPreserveTreasureFollowerDoorFollowThroughForTransit(DutyContextSnapshot context)
        => context.IsTreasureRouteTransitHold
           && TreasureDungeonRole == ADS.Models.TreasureDungeonRole.Follower
           && HasPendingTreasureFollowerDoorFollowThrough();

    private bool HasPendingTreasureFollowerDoorFollowThrough()
        => treasureFollowerDoorFollowThroughCandidateKey is not null;

    private void ObserveTreasureFollowerDoorFollowThroughTransit(DutyContextSnapshot context)
    {
        if (!context.IsTreasureRouteTransitHold || !HasPendingTreasureFollowerDoorFollowThrough())
            return;

        treasureFollowerDoorFollowThroughTransitObserved = true;
    }

    private void ObserveTreasureFollowerRouteTransit(DutyContextSnapshot context, string reason)
    {
        if (!context.IsTreasureRouteTransitHold
            || TreasureDungeonRole != ADS.Models.TreasureDungeonRole.Follower)
        {
            return;
        }

        dungeonFrontierService.LatchTreasureFollowerCandidateForRouteTransit(reason);
        ObserveTreasureFollowerDoorFollowThroughTransit(context);
    }

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
            TreasureRouteIndex = point.TreasureRouteIndex,
            TreasureRoomIndex = point.TreasureRoomIndex,
            TreasurePassageGroup = point.TreasurePassageGroup,
            TreasureClearThroughPosition = point.TreasureClearThroughPosition,
            IsLiveTreasureDoorCandidate = point.IsLiveTreasureDoorCandidate,
            IsTreasureFollowerStagingPoint = point.IsTreasureFollowerStagingPoint,
        };
    }

    private static bool IsFrontierDestinationReached(
        DungeonFrontierPoint frontierPoint,
        Vector3 playerPosition,
        bool isMapXzDestination,
        bool isXyzDestination,
        bool enforceVerticalArrivalCap,
        float? forcedArrivalRangeXz,
        float? forcedVerticalArrivalCap,
        out float targetHorizontalDistance,
        out float targetDistance,
        out float targetVerticalDelta,
        out float arrivalRange,
        out float xyzArrivalRange)
    {
        targetHorizontalDistance = GetHorizontalDistance(frontierPoint.Position, playerPosition);
        targetDistance = Vector3.Distance(frontierPoint.Position, playerPosition);
        targetVerticalDelta = MathF.Abs(frontierPoint.Position.Y - playerPosition.Y);
        arrivalRange = forcedArrivalRangeXz
            ?? (isMapXzDestination && frontierPoint.ArrivalRadiusXz > 0f
            ? frontierPoint.ArrivalRadiusXz
            : PreferredFrontierArrivalRange);
        var useForcedXzArrivalForXyz = isXyzDestination && forcedArrivalRangeXz.HasValue;
        xyzArrivalRange = useForcedXzArrivalForXyz
            ? arrivalRange
            : isXyzDestination && frontierPoint.ArrivalRadius3d > 0f
            ? frontierPoint.ArrivalRadius3d
            : PreferredFrontierArrivalRange;

        var verticalArrivalCap = forcedVerticalArrivalCap ?? TreasureFollowerRouteVerticalCap;
        if (enforceVerticalArrivalCap && targetVerticalDelta > verticalArrivalCap)
            return false;

        return isXyzDestination && !useForcedXzArrivalForXyz
            ? targetDistance <= xyzArrivalRange
            : targetHorizontalDistance <= arrivalRange;
    }

    private static string BuildTreasureRouteRadiusStatus(
        DungeonFrontierPoint frontierPoint,
        bool isTreasureFollowerPassageCandidate)
    {
        if (!frontierPoint.IsTreasureRoutePoint)
            return string.Empty;

        var preSweepRadius = frontierPoint.ArrivalRadiusXz > 0f
            ? frontierPoint.ArrivalRadiusXz
            : PreferredFrontierArrivalRange;
        if (isTreasureFollowerPassageCandidate)
        {
            return $" Treasure follower passage reach uses XZ <= {PreferredFrontierArrivalRange:0.0}y and Y <= {TreasureFollowerRouteVerticalCap:0.0}y; door follow-through completion uses XZ <= {TreasureDoorFollowThroughArrivalRange:0.0}y and stale cross-floor detection uses Y > {TreasureDoorFollowThroughStaleVerticalDelta:0.0}y; frontier pre-sweep currently uses XZ <= {preSweepRadius:0.0}y and Y <= {TreasureFollowerRouteVerticalCap:0.0}y.";
        }

        return $" Treasure route reach uses XZ <= {PreferredFrontierArrivalRange:0.0}y (not 3D XYZ); pre-sweep currently uses XZ <= {preSweepRadius:0.0}y.";
    }

    private void TryAdvanceFrontierPoint(DutyContextSnapshot context, DungeonFrontierPoint frontierPoint, bool isMapXzDestination, bool isXyzDestination, string prefix)
    {
        var playerPosition = objectTable.LocalPlayer?.Position;
        var isTreasureRoutePoint = IsTreasureRouteFrontierPoint();
        var isTreasureFollowerRoutePoint = isTreasureRoutePoint && EffectiveTreasureDungeonRole == ADS.Models.TreasureDungeonRole.Follower;
        var isTreasureFollowerPassageCandidate = isTreasureFollowerRoutePoint && frontierPoint.IsTreasurePassageCandidate;
        var isAquapolisRouteWiggleTarget = IsAquapolisRouteWiggleTarget(context, frontierPoint);
        var frontierLabel = dungeonFrontierService.CurrentMode == FrontierMode.HeadingScout
            ? "forward scout"
            : dungeonFrontierService.CurrentMode == FrontierMode.TreasureDungeon
                ? frontierPoint.IsTreasureFollowerStagingPoint
                    ? "treasure-dungeon follower staging point"
                    : isTreasureFollowerPassageCandidate ? "treasure-dungeon follower passage candidate" : "treasure-dungeon route point"
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
                ? frontierPoint.IsTreasureFollowerStagingPoint
                    ? frontierPoint.TreasurePassageGroup == "StagingCoffer"
                        ? "because ADS inferred this client is a treasure-dungeon follower and is staging near the live coffer before room combat."
                        : "because ADS inferred this client is a treasure-dungeon follower and is staging before room combat."
                    : isTreasureFollowerPassageCandidate
                    ? "because ADS inferred this client is a treasure-dungeon follower and is sweeping current-room passage candidates after live blockers are gone."
                    : isTreasureFollowerRoutePoint
                        ? "because ADS inferred this client is a treasure-dungeon follower and is using the authored static treasure route."
                    : "because ADS inferred map-opener/default treasure role and LootGoblin-derived treasure-dungeon routing data is available for this territory with no live duty objects currently visible."
            : dungeonFrontierService.CurrentMode == FrontierMode.HeadingScout
                ? "because Lumina frontier labels were unavailable and no live duty objects are currently visible."
                : "because no live duty objects are currently visible.";
        var treasureRouteRadiusStatus = BuildTreasureRouteRadiusStatus(
            frontierPoint,
            isTreasureFollowerPassageCandidate);
        if (!isTreasureRoutePoint)
            ResetTreasureRouteStuckTracking();

        if (!playerPosition.HasValue)
        {
            StopMovementAssists();
            SetPhase(
                GetFrontierHintPhase(isMapXzDestination, isXyzDestination),
                $"{prefix} Local player position was unavailable while resolving {frontierLabel} {frontierPoint.Name}.");
            return;
        }

        var isManualDestination = isMapXzDestination || isXyzDestination;
        var destinationReached = IsFrontierDestinationReached(
            frontierPoint,
            playerPosition.Value,
            isMapXzDestination,
            isXyzDestination,
            isTreasureFollowerPassageCandidate,
            null,
            null,
            out var targetHorizontalDistance,
            out var targetDistance,
            out var targetVerticalDelta,
            out var arrivalRange,
            out var xyzArrivalRange);
        if (isTreasureFollowerPassageCandidate && targetVerticalDelta > TreasureFollowerRouteVerticalCap)
        {
            StopNavigationForTreasureRouteNudge();
            var failureDetail =
                $"Player {FormatVector(playerPosition.Value)}, candidate {FormatVector(frontierPoint.Position)}, XZ {targetHorizontalDistance:0.0}y, 3D {targetDistance:0.0}y, Y {targetVerticalDelta:0.0}/{TreasureFollowerRouteVerticalCap:0.0}y before navigation.";
            dungeonFrontierService.MarkTreasureFollowerCandidateFailed(frontierPoint, "OffFloorVerticalGate", failureDetail);
            ResetTreasureRouteStuckTracking();
            SetPhase(
                ExecutionPhase.FrontierHint,
                $"{prefix} Treasure follower skipped off-floor passage candidate {frontierPoint.Name} (Y {targetVerticalDelta:0.0}/{TreasureFollowerRouteVerticalCap:0.0}y); ADS is trying another forward-eligible candidate.{treasureRouteRadiusStatus}");
            return;
        }

        if (TryAdvanceTreasureFollowerPassageFollowThrough(
                frontierPoint,
                playerPosition.Value,
                destinationReached,
                targetHorizontalDistance,
                targetDistance,
                targetVerticalDelta,
                treasureRouteRadiusStatus,
                prefix))
        {
            return;
        }

        if (!destinationReached)
        {
            if (isManualDestination
                && !isTreasureRoutePoint
                && TryRetireManualDestinationForNoProgress(
                    frontierPoint,
                    playerPosition.Value,
                    isXyzDestination,
                    targetHorizontalDistance,
                    targetDistance,
                    targetVerticalDelta,
                    prefix))
            {
                return;
            }

            var frontierTargetId = BuildFrontierTargetId(frontierPoint);
            var canUseMapFlagNavigation = dungeonFrontierService.CurrentMode == FrontierMode.Label
                                          || (isMapXzDestination && !frontierPoint.UsePlayerYForNavigation);
            var usedMapFlagNavigation = canUseMapFlagNavigation
                && TryBeginMapFlagNavigation(context, frontierTargetId, frontierPoint.Name, frontierPoint.Position);
            var treasureRouteNudgeApplied = false;
            var treasureRouteNudgeAttempt = 0;
            var treasureRouteNudgeDestination = Vector3.Zero;
            if (!usedMapFlagNavigation)
            {
                var originalNavigationDestination = isTreasureRoutePoint || isMapXzDestination || isXyzDestination
                    ? frontierPoint.Position
                    : BuildPreferredApproachPoint(playerPosition.Value, frontierPoint.Position, PreferredFrontierArrivalRange);
                var navigationDecision = ResolveTreasureRouteNavigationDecision(
                    context,
                    frontierPoint,
                    playerPosition.Value,
                    originalNavigationDestination);
                if (navigationDecision.CandidateFailed)
                {
                    StopNavigationForTreasureRouteNudge();
                    var failureDetail =
                        $"Player {FormatVector(playerPosition.Value)}, candidate {FormatVector(frontierPoint.Position)}, XZ {targetHorizontalDistance:0.0}y, 3D {targetDistance:0.0}y, Y {targetVerticalDelta:0.0} after {TreasureRouteStuckTimeout.TotalSeconds:0}s no-progress windows and {treasureRouteStuckOffsetAttempt} stuck nudge attempt(s).";
                    dungeonFrontierService.MarkTreasureFollowerCandidateFailed(frontierPoint, "StuckRecoveryExhausted", failureDetail);
                    ResetTreasureRouteStuckTracking();
                    SetPhase(
                        ExecutionPhase.FrontierHint,
                        $"{prefix} Treasure follower passage candidate {frontierPoint.Name} exhausted door-throat stuck recovery without transition; ADS is entering the room retry cooldown.");
                    return;
                }

                if (navigationDecision.ForceNavigationRestart)
                    StopNavigationForTreasureRouteNudge();

                if (navigationDecision.NudgeApplied)
                {
                    if (!isAquapolisRouteWiggleTarget)
                        CommitTreasureRouteStuckNudge(frontierPoint, playerPosition.Value, navigationDecision.NudgeAttempt);

                    treasureRouteNudgeApplied = true;
                    treasureRouteNudgeAttempt = navigationDecision.NudgeAttempt;
                    treasureRouteNudgeDestination = navigationDecision.Destination;
                    if (!isAquapolisRouteWiggleTarget)
                    {
                        log?.Information(
                            $"[ADS] Treasure route stuck nudge attempt {treasureRouteNudgeAttempt} for {frontierPoint.Name}: original {FormatVector(frontierPoint.Position)}, adjusted {FormatVector(treasureRouteNudgeDestination)} after XYZ movement stayed under {TreasureRouteStuckProgressDistance:0.0}y for {TreasureRouteStuckTimeout.TotalSeconds:0}s.");
                    }
                }

                TryBeginNavigation(frontierTargetId, navigationDecision.Destination);
            }

            var navigationMethod = usedMapFlagNavigation
                ? "via map flag and /vnav moveflag"
                : "via direct /vnav moveto";
            var treasureRouteNudgeStatus = treasureRouteNudgeApplied
                ? isAquapolisRouteWiggleTarget
                    ? $"; Aquapolis passage recovery side-step {FormatAquapolisRouteWiggleSide(treasureRouteNudgeAttempt)} to {FormatVector(treasureRouteNudgeDestination)}"
                    : $"; applied stuck nudge attempt {treasureRouteNudgeAttempt} from {FormatVector(frontierPoint.Position)} to {FormatVector(treasureRouteNudgeDestination)}"
                : string.Empty;
            SetPhase(
                GetFrontierNavigatingPhase(isMapXzDestination, isXyzDestination),
                isXyzDestination
                    ? $"{prefix} Advancing toward {frontierLabel} {frontierPoint.Name} (3D {targetDistance:0.0}y, XZ {targetHorizontalDistance:0.0}y, Y {targetVerticalDelta:0.0}; ghost radius {xyzArrivalRange:0.0}y) {navigationMethod}{treasureRouteNudgeStatus} {frontierReason}{treasureRouteRadiusStatus}"
                    : $"{prefix} Advancing toward {frontierLabel} {frontierPoint.Name} (XZ {targetHorizontalDistance:0.0}y, Y {targetVerticalDelta:0.0}; ghost radius {arrivalRange:0.0}y) {navigationMethod}{treasureRouteNudgeStatus} {frontierReason}{treasureRouteRadiusStatus}");
            return;
        }

        StopMovementAssists();
        if (frontierPoint.IsTreasureFollowerStagingPoint)
            dungeonFrontierService.MarkTreasureFollowerCofferSeekReached(frontierPoint, playerPosition.Value, "ExecutionArrival");

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
                ? $"{prefix} Reached {frontierLabel} {frontierPoint.Name} (3D {targetDistance:0.0}y, XZ {targetHorizontalDistance:0.0}y, Y {targetVerticalDelta:0.0}). Waiting for live duty objects or duty completion.{treasureRouteRadiusStatus}"
                : $"{prefix} Reached {frontierLabel} {frontierPoint.Name} (XZ {targetHorizontalDistance:0.0}y, Y {targetVerticalDelta:0.0}). Waiting for live duty objects or duty completion.{treasureRouteRadiusStatus}");
    }

    private bool TryAdvanceTreasureFollowerPassageFollowThrough(
        DungeonFrontierPoint frontierPoint,
        Vector3 playerPosition,
        bool candidateReached,
        float candidateHorizontalDistance,
        float candidateDistance,
        float candidateVerticalDelta,
        string treasureRouteRadiusStatus,
        string prefix)
    {
        if (!IsTreasureFollowerPassageCandidate(frontierPoint))
            return false;

        if (HasPendingTreasureFollowerDoorFollowThrough())
        {
            var pendingCandidate = treasureFollowerDoorFollowThroughCandidatePoint;
            var pendingFollowThroughPoint = treasureFollowerDoorFollowThroughPoint;
            if (pendingCandidate is null || pendingFollowThroughPoint is null)
            {
                ClearTreasureFollowerDoorFollowThrough(resetStuckTracking: true);
                return false;
            }

            return TryAdvanceStoredTreasureFollowerDoorFollowThrough(
                pendingCandidate,
                pendingFollowThroughPoint,
                playerPosition,
                BuildTreasureRouteRadiusStatus(pendingCandidate, isTreasureFollowerPassageCandidate: true),
                prefix);
        }

        if (!candidateReached)
            return false;

        var newFollowThroughPoint = BuildTreasureFollowerDoorFollowThroughPoint(frontierPoint, playerPosition);
        treasureFollowerDoorFollowThroughCandidateKey = frontierPoint.Key;
        treasureFollowerDoorFollowThroughCandidatePoint = frontierPoint;
        treasureFollowerDoorFollowThroughPoint = newFollowThroughPoint;
        treasureFollowerDoorFollowThroughTransitObserved = false;
        treasureFollowerDoorFollowThroughReached = false;
        treasureFollowerDoorFollowThroughReachedUtc = DateTime.MinValue;
        lastTreasureFollowerDoorFollowThroughRetargetLogKey = string.Empty;
        dungeonFrontierService.MarkTreasureFollowerCandidateReached(frontierPoint);
        ResetTreasureRouteStuckTracking();
        StopNavigationForTreasureRouteNudge();
        ResetTreasureDoorJiggleTracking(releaseKeys: true);
        log?.Information(
            $"[ADS] Treasure follower door follow-through active for {frontierPoint.Name} ({frontierPoint.TreasureRouteSource}, {frontierPoint.TreasurePassageGroup}, room {frontierPoint.TreasureRoomIndex}) after candidate reach XZ {candidateHorizontalDistance:0.0}y; target {FormatVector(newFollowThroughPoint.Position)}.");

        return TryAdvanceStoredTreasureFollowerDoorFollowThrough(
            frontierPoint,
            newFollowThroughPoint,
            playerPosition,
            treasureRouteRadiusStatus,
            prefix);
    }

    private bool TryAdvancePendingTreasureFollowerDoorFollowThrough(
        DutyContextSnapshot context,
        PlannerSnapshot planner,
        string prefix)
    {
        if (!HasPendingTreasureFollowerDoorFollowThrough())
            return false;

        if (EffectiveTreasureDungeonRole != ADS.Models.TreasureDungeonRole.Follower)
        {
            ClearTreasureFollowerDoorFollowThrough(resetStuckTracking: true);
            return false;
        }

        if (dungeonFrontierService.CurrentTarget is { IsManualDestination: true } manualTarget)
        {
            log?.Information(
                $"[ADS] Treasure follower door follow-through cleared for manual destination override {manualTarget.Name}.");
            ClearTreasureFollowerDoorFollowThrough(resetStuckTracking: true);
            return false;
        }

        if (treasureFollowerDoorFollowThroughTransitObserved)
        {
            if (dungeonFrontierService.TreasureFollowerHeldCandidateTransitObserved)
            {
                StopMovementAssists(preserveTreasureFollowerDoorFollowThrough: true);
                SetPhase(
                    ExecutionPhase.TransitionHold,
                    $"{prefix} Treasure follower door follow-through observed route transit and is waiting for frontier route consumption before planner retargets resume.");
                return true;
            }

            var resolvedCandidate = treasureFollowerDoorFollowThroughCandidatePoint;
            log?.Information(
                $"[ADS] Treasure follower door follow-through resolved after observed transit for {resolvedCandidate?.Name ?? "held passage candidate"}; refreshed planner targets may resume.");
            ClearTreasureFollowerDoorFollowThrough(resetStuckTracking: true);
            return false;
        }

        var candidate = treasureFollowerDoorFollowThroughCandidatePoint;
        var followThroughPoint = treasureFollowerDoorFollowThroughPoint;
        if (candidate is null || followThroughPoint is null)
        {
            ClearTreasureFollowerDoorFollowThrough(resetStuckTracking: true);
            return false;
        }

        var playerPosition = objectTable.LocalPlayer?.Position;
        if (!playerPosition.HasValue)
        {
            StopMovementAssists(preserveTreasureFollowerDoorFollowThrough: true);
            SetPhase(
                ExecutionPhase.FrontierHint,
                $"{prefix} Treasure follower door follow-through for {candidate.Name} is held because local player position is unavailable.");
            return true;
        }

        LogTreasureFollowerDoorFollowThroughRetargetHold(planner, candidate, followThroughPoint);
        return TryAdvanceStoredTreasureFollowerDoorFollowThrough(
            candidate,
            followThroughPoint,
            playerPosition.Value,
            BuildTreasureRouteRadiusStatus(candidate, isTreasureFollowerPassageCandidate: true),
            prefix);
    }

    private bool TryAdvanceStoredTreasureFollowerDoorFollowThrough(
        DungeonFrontierPoint candidate,
        DungeonFrontierPoint followThroughPoint,
        Vector3 playerPosition,
        string treasureRouteRadiusStatus,
        string prefix)
    {
        var candidateHorizontalDistance = GetHorizontalDistance(candidate.Position, playerPosition);
        var candidateDistance = Vector3.Distance(candidate.Position, playerPosition);
        var candidateVerticalDelta = MathF.Abs(candidate.Position.Y - playerPosition.Y);

        if (treasureFollowerDoorFollowThroughReached)
            return HoldReachedTreasureFollowerDoorFollowThrough(candidate, followThroughPoint, playerPosition, candidateHorizontalDistance, candidateDistance, candidateVerticalDelta, treasureRouteRadiusStatus, prefix);

        if (TryAdvanceTreasureDoorFollowThroughPoint(
                followThroughPoint,
                prefix,
                "Treasure follower door",
                "after reaching the movement-only passage candidate",
                pauseForSelectYesno: false,
                stopMovementOnReach: false,
                maxNudgeAttempts: TreasureFollowerDoorFollowThroughNudgeAttemptLimit,
                out var reachedFollowThrough,
                out var staleCrossFloorTarget,
                out var stuckRecoveryExhausted))
        {
            if (reachedFollowThrough)
            {
                var followThroughHorizontalDistance = GetHorizontalDistance(followThroughPoint.Position, playerPosition);
                var followThroughDistance = Vector3.Distance(followThroughPoint.Position, playerPosition);
                var followThroughVerticalDelta = MathF.Abs(followThroughPoint.Position.Y - playerPosition.Y);
                treasureFollowerDoorFollowThroughReached = true;
                treasureFollowerDoorFollowThroughReachedUtc = DateTime.UtcNow;
                SetPhase(
                    ExecutionPhase.FrontierHint,
                    $"{prefix} Treasure follower door follow-through for passage candidate {candidate.Name} reached {FormatVector(followThroughPoint.Position)} (follow-through XZ {followThroughHorizontalDistance:0.0}y, 3D {followThroughDistance:0.0}y, Y {followThroughVerticalDelta:0.0}y). ADS is holding this committed clear-through until transition, combat, stale-floor evidence, or timeout.{treasureRouteRadiusStatus}");
            }

            return true;
        }

        if (stuckRecoveryExhausted)
        {
            StopNavigationForTreasureRouteNudge();
            var failureDetail =
                $"Clear-through target {FormatVector(followThroughPoint.Position)} made no progress after {TreasureDoorNudgeStuckTimeout.TotalSeconds:0}s windows and {TreasureFollowerDoorFollowThroughNudgeAttemptLimit} side-nudge attempt(s). Player {FormatVector(playerPosition)}, candidate {FormatVector(candidate.Position)}, candidate XZ {candidateHorizontalDistance:0.0}y, 3D {candidateDistance:0.0}y, Y {candidateVerticalDelta:0.0}y.";
            dungeonFrontierService.MarkTreasureFollowerCandidateFailed(candidate, "DoorFollowThroughStuckRecoveryExhausted", failureDetail);
            ClearTreasureFollowerDoorFollowThrough(resetStuckTracking: true);
            SetPhase(
                ExecutionPhase.FrontierHint,
                $"{prefix} Treasure follower door follow-through for passage candidate {candidate.Name} exhausted clear-through side-nudge recovery; ADS is entering the room retry cooldown.{treasureRouteRadiusStatus}");
            return true;
        }

        if (staleCrossFloorTarget)
        {
            StopNavigationForTreasureRouteNudge();
            var staleDetail =
                $"Follow-through target {FormatVector(followThroughPoint.Position)} became Y {MathF.Abs(followThroughPoint.Position.Y - playerPosition.Y):0.0}y from player {FormatVector(playerPosition)} after candidate reach XZ {candidateHorizontalDistance:0.0}y, 3D {candidateDistance:0.0}y, Y {candidateVerticalDelta:0.0}y.";
            dungeonFrontierService.MarkTreasureFollowerCandidateFailed(candidate, "DoorFollowThroughStaleFloor", staleDetail);
            ClearTreasureFollowerDoorFollowThrough(resetStuckTracking: true);
            SetPhase(
                ExecutionPhase.FrontierHint,
                $"{prefix} Treasure follower door follow-through for passage candidate {candidate.Name} became stale across floors; ADS marked that same-room passage group failed and is trying another candidate.{treasureRouteRadiusStatus}");
            return true;
        }

        ClearTreasureFollowerDoorFollowThrough(resetStuckTracking: true);
        return false;
    }

    private bool HoldReachedTreasureFollowerDoorFollowThrough(
        DungeonFrontierPoint candidate,
        DungeonFrontierPoint followThroughPoint,
        Vector3 playerPosition,
        float candidateHorizontalDistance,
        float candidateDistance,
        float candidateVerticalDelta,
        string treasureRouteRadiusStatus,
        string prefix)
    {
        var now = DateTime.UtcNow;
        var reachedUtc = treasureFollowerDoorFollowThroughReachedUtc == DateTime.MinValue
            ? now
            : treasureFollowerDoorFollowThroughReachedUtc;
        treasureFollowerDoorFollowThroughReachedUtc = reachedUtc;
        var elapsed = now - reachedUtc;
        if (elapsed >= TreasureFollowerDoorFollowThroughTruthTimeout)
        {
            var followThroughHorizontalDistance = GetHorizontalDistance(followThroughPoint.Position, playerPosition);
            var followThroughDistance = Vector3.Distance(followThroughPoint.Position, playerPosition);
            var followThroughVerticalDelta = MathF.Abs(followThroughPoint.Position.Y - playerPosition.Y);
            var failureDetail =
                $"Clear-through target {FormatVector(followThroughPoint.Position)} stayed reached for {elapsed.TotalSeconds:0.0}s without transition or combat. Follow-through XZ {followThroughHorizontalDistance:0.0}y, 3D {followThroughDistance:0.0}y, Y {followThroughVerticalDelta:0.0}y; candidate XZ {candidateHorizontalDistance:0.0}y, 3D {candidateDistance:0.0}y, Y {candidateVerticalDelta:0.0}y.";
            dungeonFrontierService.MarkTreasureFollowerCandidateFailed(candidate, "DoorFollowThroughTruthTimeout", failureDetail);
            ClearTreasureFollowerDoorFollowThrough(resetStuckTracking: true);
            SetPhase(
                ExecutionPhase.FrontierHint,
                $"{prefix} Treasure follower door follow-through for passage candidate {candidate.Name} timed out after reaching clear-through without refreshed truth; ADS is entering the room retry cooldown.{treasureRouteRadiusStatus}");
            return true;
        }

        StopNavigationForTreasureRouteNudge();
        var remaining = Math.Max(0, (TreasureFollowerDoorFollowThroughTruthTimeout - elapsed).TotalSeconds);
        SetPhase(
            ExecutionPhase.FrontierHint,
            $"{prefix} Treasure follower door follow-through for passage candidate {candidate.Name} is holding after clear-through reach; waiting {remaining:0.0}s for transition or combat before retrying.{treasureRouteRadiusStatus}");
        return true;
    }

    private void LogTreasureFollowerDoorFollowThroughRetargetHold(
        PlannerSnapshot planner,
        DungeonFrontierPoint candidate,
        DungeonFrontierPoint followThroughPoint)
    {
        var currentTarget = dungeonFrontierService.CurrentTarget;
        if (planner.Mode == PlannerMode.Progression
            && planner.ObjectiveKind == PlannerObjectiveKind.Frontier
            && currentTarget is not null
            && string.Equals(currentTarget.Key, candidate.Key, StringComparison.Ordinal))
        {
            lastTreasureFollowerDoorFollowThroughRetargetLogKey = string.Empty;
            return;
        }

        var plannerTarget = planner.TargetName ?? planner.Objective;
        var frontierTarget = currentTarget is null
            ? "none"
            : $"{currentTarget.Name} ({currentTarget.TreasureRouteSource}, {currentTarget.TreasurePassageGroup}, room {currentTarget.TreasureRoomIndex}, key {currentTarget.Key})";
        var retargetKey = $"{candidate.Key}:{planner.Mode}:{planner.ObjectiveKind}:{plannerTarget}:{currentTarget?.Key}";
        if (string.Equals(retargetKey, lastTreasureFollowerDoorFollowThroughRetargetLogKey, StringComparison.Ordinal))
            return;

        lastTreasureFollowerDoorFollowThroughRetargetLogKey = retargetKey;
        var equivalence = IsSameTreasureFollowerPassageGroup(candidate, currentTarget)
            ? "same treasure room/passage group"
            : "different planner/frontier target";
        log?.Information(
            $"[ADS] Treasure follower door follow-through hold kept committed candidate {candidate.Name} ({candidate.TreasureRouteSource}, {candidate.TreasurePassageGroup}, room {candidate.TreasureRoomIndex}) toward {FormatVector(followThroughPoint.Position)}; ignoring planner {planner.Mode}/{planner.ObjectiveKind} '{plannerTarget}' and frontier {frontierTarget} ({equivalence}) until follow-through resolves.");
    }

    private bool IsTreasureRouteFrontierPoint()
        => dungeonFrontierService.CurrentMode == FrontierMode.TreasureDungeon;

    private bool IsTreasureFollowerPassageCandidate(DungeonFrontierPoint frontierPoint)
        => IsTreasureRouteFrontierPoint()
           && EffectiveTreasureDungeonRole == ADS.Models.TreasureDungeonRole.Follower
           && frontierPoint.IsTreasurePassageCandidate;

    private static bool IsSameTreasureFollowerPassageGroup(DungeonFrontierPoint left, DungeonFrontierPoint? right)
        => right is not null
           && left.TreasureRoomIndex > 0
           && right.TreasureRoomIndex > 0
           && left.TreasureRoomIndex == right.TreasureRoomIndex
           && string.Equals(left.TreasurePassageGroup, right.TreasurePassageGroup, StringComparison.Ordinal);

    private TreasureRouteNavigationDecision ResolveTreasureRouteNavigationDecision(
        DutyContextSnapshot context,
        DungeonFrontierPoint frontierPoint,
        Vector3 playerPosition,
        Vector3 originalDestination,
        int maxFollowerPassageNudgeAttempts = 2)
    {
        if (!IsTreasureRouteFrontierPoint())
            return new TreasureRouteNavigationDecision(originalDestination, ForceNavigationRestart: false, NudgeApplied: false, NudgeAttempt: 0, CandidateFailed: false);

        if (IsAquapolisRoutePassageTarget(context, frontierPoint))
        {
            ResetStandardTreasureRouteStuckTracking();
            if (IsAquapolisRouteWiggleHoldActive(context))
            {
                ResetAquapolisRouteWiggleTracking();
                return new TreasureRouteNavigationDecision(originalDestination, ForceNavigationRestart: false, NudgeApplied: false, NudgeAttempt: 0, CandidateFailed: false);
            }

            return ResolveAquapolisRouteWiggleNavigationDecision(frontierPoint, playerPosition, originalDestination);
        }

        ResetAquapolisRouteWiggleTracking();
        var now = DateTime.UtcNow;
        if (!string.Equals(treasureRouteStuckTargetKey, frontierPoint.Key, StringComparison.Ordinal)
            || !treasureRouteStuckBaselinePosition.HasValue)
        {
            StartTreasureRouteStuckTracking(frontierPoint, playerPosition, now);
            return new TreasureRouteNavigationDecision(originalDestination, ForceNavigationRestart: false, NudgeApplied: false, NudgeAttempt: 0, CandidateFailed: false);
        }

        if (Vector3.Distance(treasureRouteStuckBaselinePosition.Value, playerPosition) > TreasureRouteStuckProgressDistance)
        {
            StartTreasureRouteStuckTracking(frontierPoint, playerPosition, now);
            return new TreasureRouteNavigationDecision(originalDestination, ForceNavigationRestart: false, NudgeApplied: false, NudgeAttempt: 0, CandidateFailed: false);
        }

        if (now - treasureRouteStuckLastProgressUtc < TreasureRouteStuckTimeout)
        {
            return new TreasureRouteNavigationDecision(
                BuildTreasureRouteNavigationDestination(originalDestination, treasureRouteStuckOffsetAttempt),
                ForceNavigationRestart: false,
                NudgeApplied: false,
                NudgeAttempt: treasureRouteStuckOffsetAttempt,
                CandidateFailed: false);
        }

        var maxNudgeAttempts = EffectiveTreasureDungeonRole == ADS.Models.TreasureDungeonRole.Follower && frontierPoint.IsTreasurePassageCandidate
            ? maxFollowerPassageNudgeAttempts
            : 2;
        if (treasureRouteStuckOffsetAttempt >= maxNudgeAttempts)
        {
            if (EffectiveTreasureDungeonRole == ADS.Models.TreasureDungeonRole.Follower && frontierPoint.IsTreasurePassageCandidate)
            {
                return new TreasureRouteNavigationDecision(
                    originalDestination,
                    ForceNavigationRestart: false,
                    NudgeApplied: false,
                    NudgeAttempt: treasureRouteStuckOffsetAttempt,
                    CandidateFailed: true);
            }

            treasureRouteStuckOffsetAttempt = 3;
            treasureRouteStuckBaselinePosition = playerPosition;
            treasureRouteStuckLastProgressUtc = now;
            return new TreasureRouteNavigationDecision(originalDestination, ForceNavigationRestart: false, NudgeApplied: false, NudgeAttempt: 0, CandidateFailed: false);
        }

        var nextAttempt = treasureRouteStuckOffsetAttempt + 1;
        return new TreasureRouteNavigationDecision(
            BuildTreasureRouteNavigationDestination(originalDestination, nextAttempt),
            ForceNavigationRestart: true,
            NudgeApplied: true,
            NudgeAttempt: nextAttempt,
            CandidateFailed: false);
    }

    private static bool IsAquapolisRouteWiggleTarget(DutyContextSnapshot context, DungeonFrontierPoint frontierPoint)
        => IsAquapolisRoutePassageTarget(context, frontierPoint)
           && !IsAquapolisRouteWiggleHoldActive(context);

    private static bool IsAquapolisRoutePassageTarget(DutyContextSnapshot context, DungeonFrontierPoint frontierPoint)
        => context.TerritoryTypeId == AquapolisTerritoryTypeId
           && frontierPoint.IsTreasurePassageCandidate
           && frontierPoint.TreasureRoomIndex is >= 2 and <= 7;

    private static bool IsAquapolisRouteWiggleHoldActive(DutyContextSnapshot context)
        => context.BetweenAreas
           || context.BetweenAreas51
           || context.OccupiedInCutSceneEvent
           || context.WatchingCutscene;

    private TreasureRouteNavigationDecision ResolveAquapolisRouteWiggleNavigationDecision(
        DungeonFrontierPoint frontierPoint,
        Vector3 playerPosition,
        Vector3 originalDestination)
    {
        var now = DateTime.UtcNow;
        if (!string.Equals(aquapolisRouteWiggleTargetKey, frontierPoint.Key, StringComparison.Ordinal)
            || !aquapolisRouteWiggleBaselinePosition.HasValue)
        {
            StartAquapolisRouteWiggleTracking(frontierPoint, playerPosition, now, resetNextSide: true);
            return new TreasureRouteNavigationDecision(originalDestination, ForceNavigationRestart: false, NudgeApplied: false, NudgeAttempt: 0, CandidateFailed: false);
        }

        if (now < aquapolisRouteWiggleUntilUtc)
        {
            return new TreasureRouteNavigationDecision(
                aquapolisRouteWiggleDestination,
                ForceNavigationRestart: false,
                NudgeApplied: true,
                NudgeAttempt: aquapolisRouteWiggleAttempt,
                CandidateFailed: false);
        }

        if (aquapolisRouteWiggleUntilUtc != DateTime.MinValue)
        {
            ResumeAquapolisRouteWiggleTrackingAfterSideStep(frontierPoint, playerPosition, now);
            return new TreasureRouteNavigationDecision(
                originalDestination,
                ForceNavigationRestart: true,
                NudgeApplied: false,
                NudgeAttempt: aquapolisRouteWiggleAttempt,
                CandidateFailed: false);
        }

        if (GetHorizontalDistance(aquapolisRouteWiggleBaselinePosition.Value, playerPosition) >= TreasureRouteStuckProgressDistance)
        {
            StartAquapolisRouteWiggleTracking(frontierPoint, playerPosition, now, resetNextSide: true);
            return new TreasureRouteNavigationDecision(originalDestination, ForceNavigationRestart: false, NudgeApplied: false, NudgeAttempt: 0, CandidateFailed: false);
        }

        if (now - aquapolisRouteWiggleLastProgressUtc < TreasureRouteStuckTimeout)
            return new TreasureRouteNavigationDecision(originalDestination, ForceNavigationRestart: false, NudgeApplied: false, NudgeAttempt: aquapolisRouteWiggleAttempt, CandidateFailed: false);

        aquapolisRouteWiggleAttempt = aquapolisRouteWiggleAttempt >= 2 ? 1 : aquapolisRouteWiggleAttempt + 1;
        aquapolisRouteWiggleDestination = BuildAquapolisRouteWiggleDestination(frontierPoint.Position, playerPosition, aquapolisRouteWiggleAttempt);
        aquapolisRouteWiggleUntilUtc = now + AquapolisRouteWiggleHoldDuration;
        log?.Information(
            $"[ADS] Aquapolis passage recovery side-step {FormatAquapolisRouteWiggleSide(aquapolisRouteWiggleAttempt)} started for {frontierPoint.Name}; XZ movement stayed under {TreasureRouteStuckProgressDistance:0.0}y for {TreasureRouteStuckTimeout.TotalSeconds:0}s. Destination {FormatVector(aquapolisRouteWiggleDestination)}.");

        return new TreasureRouteNavigationDecision(
            aquapolisRouteWiggleDestination,
            ForceNavigationRestart: true,
            NudgeApplied: true,
            NudgeAttempt: aquapolisRouteWiggleAttempt,
            CandidateFailed: false);
    }

    private void StartTreasureRouteStuckTracking(DungeonFrontierPoint frontierPoint, Vector3 playerPosition, DateTime now)
    {
        treasureRouteStuckTargetKey = frontierPoint.Key;
        treasureRouteStuckBaselinePosition = playerPosition;
        treasureRouteStuckLastProgressUtc = now;
        treasureRouteStuckOffsetAttempt = 0;
    }

    private void CommitTreasureRouteStuckNudge(DungeonFrontierPoint frontierPoint, Vector3 playerPosition, int offsetAttempt)
    {
        treasureRouteStuckTargetKey = frontierPoint.Key;
        treasureRouteStuckBaselinePosition = playerPosition;
        treasureRouteStuckLastProgressUtc = DateTime.UtcNow;
        treasureRouteStuckOffsetAttempt = offsetAttempt;
    }

    private void ResetTreasureRouteStuckTracking()
    {
        ResetStandardTreasureRouteStuckTracking();
        ResetAquapolisRouteWiggleTracking();
    }

    private void ResetStandardTreasureRouteStuckTracking()
    {
        treasureRouteStuckTargetKey = null;
        treasureRouteStuckBaselinePosition = null;
        treasureRouteStuckLastProgressUtc = DateTime.MinValue;
        treasureRouteStuckOffsetAttempt = 0;
    }

    private void StartAquapolisRouteWiggleTracking(
        DungeonFrontierPoint frontierPoint,
        Vector3 playerPosition,
        DateTime now,
        bool resetNextSide)
    {
        aquapolisRouteWiggleTargetKey = frontierPoint.Key;
        aquapolisRouteWiggleBaselinePosition = playerPosition;
        aquapolisRouteWiggleLastProgressUtc = now;
        aquapolisRouteWiggleUntilUtc = DateTime.MinValue;
        aquapolisRouteWiggleDestination = Vector3.Zero;
        if (resetNextSide)
            aquapolisRouteWiggleAttempt = 0;
    }

    private void ResumeAquapolisRouteWiggleTrackingAfterSideStep(
        DungeonFrontierPoint frontierPoint,
        Vector3 playerPosition,
        DateTime now)
    {
        aquapolisRouteWiggleTargetKey = frontierPoint.Key;
        aquapolisRouteWiggleBaselinePosition = playerPosition;
        aquapolisRouteWiggleLastProgressUtc = now;
        aquapolisRouteWiggleUntilUtc = DateTime.MinValue;
        aquapolisRouteWiggleDestination = Vector3.Zero;
    }

    private void ResetAquapolisRouteWiggleTracking()
    {
        aquapolisRouteWiggleTargetKey = null;
        aquapolisRouteWiggleBaselinePosition = null;
        aquapolisRouteWiggleLastProgressUtc = DateTime.MinValue;
        aquapolisRouteWiggleAttempt = 0;
        aquapolisRouteWiggleUntilUtc = DateTime.MinValue;
        aquapolisRouteWiggleDestination = Vector3.Zero;
    }

    private void PauseTreasureFollowerDoorFollowThrough()
    {
        if (HasPendingTreasureFollowerDoorFollowThrough())
            ResetTreasureRouteStuckTracking();
    }

    private void ClearTreasureFollowerDoorFollowThrough(bool resetStuckTracking, bool clearCandidateHold = true)
    {
        treasureFollowerDoorFollowThroughCandidateKey = null;
        treasureFollowerDoorFollowThroughCandidatePoint = null;
        treasureFollowerDoorFollowThroughPoint = null;
        treasureFollowerDoorFollowThroughTransitObserved = false;
        treasureFollowerDoorFollowThroughReached = false;
        treasureFollowerDoorFollowThroughReachedUtc = DateTime.MinValue;
        lastTreasureFollowerDoorFollowThroughRetargetLogKey = string.Empty;
        if (clearCandidateHold)
            dungeonFrontierService.ClearTreasureFollowerCandidateHold("treasure follower door follow-through cleared");

        if (resetStuckTracking)
            ResetTreasureRouteStuckTracking();
    }

    private bool TryRetireManualDestinationForNoProgress(
        DungeonFrontierPoint frontierPoint,
        Vector3 playerPosition,
        bool isXyzDestination,
        float targetHorizontalDistance,
        float targetDistance,
        float targetVerticalDelta,
        string prefix)
    {
        var now = DateTime.UtcNow;
        currentManualDestinationTarget = frontierPoint.Name;
        currentManualDestinationDistance = isXyzDestination
            ? targetDistance
            : targetHorizontalDistance;
        manualDestinationNavigationTargetId = BuildFrontierTargetId(frontierPoint);

        if (!string.Equals(manualDestinationNoProgressTargetKey, frontierPoint.Key, StringComparison.Ordinal)
            || !manualDestinationNoProgressBaselinePosition.HasValue)
        {
            StartManualDestinationNoProgressTracking(frontierPoint, playerPosition, now);
            return false;
        }

        var playerMovement = Vector3.Distance(playerPosition, manualDestinationNoProgressBaselinePosition.Value);
        if (playerMovement >= ManualDestinationNoProgressDistance)
        {
            StartManualDestinationNoProgressTracking(frontierPoint, playerPosition, now);
            return false;
        }

        var noProgressAge = now - manualDestinationLastProgressUtc;
        if (noProgressAge < ManualDestinationNoProgressTimeout)
            return false;

        var destinationLabel = isXyzDestination ? "XYZ destination" : "map XZ destination";
        var detail =
            $"because player movement stayed under {ManualDestinationNoProgressDistance:0.0}y for {ManualDestinationNoProgressTimeout.TotalSeconds:0}s while navigating to {destinationLabel}; player {FormatVector(playerPosition)}, target {FormatVector(frontierPoint.Position)}, 3D {targetDistance:0.0}y, XZ {targetHorizontalDistance:0.0}y, Y {targetVerticalDelta:0.0}.";
        StopMovementAssists();
        dungeonFrontierService.RetireManualDestination(frontierPoint, "ManualDestinationNoProgress", detail);
        ClearCommittedForceMarchManualDestinationIfMatches(frontierPoint);
        ResetManualDestinationNoProgressTracking(clearStatus: true);
        SetPhase(
            GetFrontierHintPhase(frontierPoint.IsManualMapXzDestination, frontierPoint.IsManualXyzDestination),
            $"{prefix} Ghosted stuck manual {destinationLabel} {frontierPoint.Name} after {noProgressAge.TotalSeconds:0.0}s without player movement; ADS will replan.");
        return true;
    }

    private void StartManualDestinationNoProgressTracking(DungeonFrontierPoint frontierPoint, Vector3 playerPosition, DateTime now)
    {
        manualDestinationNoProgressTargetKey = frontierPoint.Key;
        manualDestinationNoProgressBaselinePosition = playerPosition;
        manualDestinationLastProgressUtc = now;
    }

    private void ResetManualDestinationNoProgressTracking(bool clearStatus)
    {
        manualDestinationNoProgressTargetKey = null;
        manualDestinationNavigationTargetId = null;
        manualDestinationNoProgressBaselinePosition = null;
        manualDestinationLastProgressUtc = DateTime.MinValue;
        if (!clearStatus)
            return;

        currentManualDestinationTarget = string.Empty;
        currentManualDestinationDistance = null;
    }

    private static Vector3 BuildTreasureRouteNavigationDestination(Vector3 originalDestination, int offsetAttempt)
        => offsetAttempt switch
        {
            1 => new Vector3(originalDestination.X + TreasureRouteStuckNudgeXOffset, originalDestination.Y, originalDestination.Z),
            2 => new Vector3(originalDestination.X - TreasureRouteStuckNudgeXOffset, originalDestination.Y, originalDestination.Z),
            _ => originalDestination,
        };

    private static Vector3 BuildAquapolisRouteWiggleDestination(Vector3 targetPosition, Vector3 playerPosition, int attempt)
    {
        var flatDelta = new Vector3(targetPosition.X - playerPosition.X, 0f, targetPosition.Z - playerPosition.Z);
        var flatDistance = flatDelta.Length();
        var forward = flatDistance <= float.Epsilon
            ? Vector3.UnitZ
            : Vector3.Normalize(flatDelta);
        var side = new Vector3(-forward.Z, 0f, forward.X);
        var sideSign = attempt % 2 == 1 ? 1f : -1f;

        return new Vector3(
            playerPosition.X + (side.X * AquapolisRouteWiggleSideOffset * sideSign),
            playerPosition.Y,
            playerPosition.Z + (side.Z * AquapolisRouteWiggleSideOffset * sideSign));
    }

    private static string FormatAquapolisRouteWiggleSide(int attempt)
        => attempt % 2 == 1 ? "left" : "right";

    private static DungeonFrontierPoint BuildTreasureFollowerDoorFollowThroughPoint(
        DungeonFrontierPoint candidate,
        Vector3 playerPosition)
        => new()
        {
            Key = $"treasure-follower-door-follow-through:{candidate.Key}",
            Name = $"{candidate.Name} follow-through",
            Position = candidate.TreasureClearThroughPosition
                ?? BuildTreasureDoorFollowThroughPosition(playerPosition, candidate.Position),
            LevelRowId = candidate.LevelRowId,
            MapId = candidate.MapId,
            Priority = candidate.Priority,
            ManualDestinationKind = ManualDestinationKind.None,
            ArrivalRadiusXz = TreasureDoorFollowThroughArrivalRange,
            TreasureRouteIndex = candidate.TreasureRouteIndex,
            TreasureRoomIndex = candidate.TreasureRoomIndex,
            TreasurePassageGroup = candidate.TreasurePassageGroup,
            TreasureClearThroughPosition = candidate.TreasureClearThroughPosition,
        };

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

        var now = DateTime.UtcNow;
        if (TryHoldTreasureCofferLockedRetry(context, observedInteractable, prefix, now))
            return;

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

        var cameraBasedInteract = !useCameraIndependentInteract;
        if (TryHoldResetCameraBeforeInteract(observedInteractable, cameraBasedInteract, prefix, now))
            return;

        if (TryInteractWithObject(gameObject, cameraBasedInteract: cameraBasedInteract))
        {
            if (observedInteractable.Classification == InteractableClass.TreasureCoffer)
            {
                if (IsTreasureCofferLockedRetryActive(observedInteractable))
                {
                    ArmTreasureCofferLockedRetry(observedInteractable.Key, now, "retry interact sent");
                    observationMemoryService.ClearTreasureCofferSuppression(observedInteractable.Key, "locked-tight retry interact");
                }
                else
                {
                    observationMemoryService.MarkTreasureInteractionSent(observedInteractable);
                }
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

        var switchingTreasureCofferIdentity = objectiveKind != PlannerObjectiveKind.TreasureCoffer || committedInteractableKey != interactable.Key;
        if (switchingTreasureCofferIdentity)
        {
            ResetTreasureCofferLockedRetry();
            ResetTreasureCofferRouteTracking();
        }

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

    private bool TryAdvanceCommittedTreasureCofferLockedRetry(
        DutyContextSnapshot context,
        ObservedInteractable interactable,
        string prefix)
    {
        if (!IsTreasureCofferLockedRetryActive(interactable))
            return false;

        var now = DateTime.UtcNow;
        if (TryHoldTreasureCofferLockedRetry(context, interactable, prefix, now))
            return true;

        TryAdvanceInteractableObjective(
            context,
            interactable,
            $"{prefix} Retrying locked treasure coffer after {TreasureCofferLockedRetryDelay.TotalSeconds:0}s.");
        return true;
    }

    private bool TryHoldTreasureCofferLockedRetry(
        DutyContextSnapshot context,
        ObservedInteractable interactable,
        string prefix,
        DateTime now)
    {
        if (!IsTreasureCofferLockedRetryActive(interactable))
            return false;

        observationMemoryService.ClearTreasureCofferSuppression(interactable.Key, "locked-tight retry hold");

        if (context.InCombat)
        {
            StopMovementAssists();
            SetPhase(
                ExecutionPhase.CombatHold,
                $"{prefix} Treasure coffer {interactable.Name} is locked tight; ADS is holding this coffer until combat clears.");
            return true;
        }

        if (now < treasureCofferLockedNextRetryUtc)
        {
            StopMovementAssists();
            SetPhase(
                ExecutionPhase.AttemptingInteractableObjective,
                $"{prefix} Treasure coffer {interactable.Name} is locked tight; retry in {(treasureCofferLockedNextRetryUtc - now).TotalSeconds:0.0}s.");
            return true;
        }

        return false;
    }

    private void ArmTreasureCofferLockedRetry(string interactableKey, DateTime now, string reason)
    {
        treasureCofferLockedRetryKey = interactableKey;
        treasureCofferLockedNextRetryUtc = now + TreasureCofferLockedRetryDelay;
        log?.Information($"[ADS] Treasure coffer {interactableKey} reported locked tight; retry armed for {TreasureCofferLockedRetryDelay.TotalSeconds:0}s after {reason}.");
    }

    private bool IsTreasureCofferLockedRetryActive(ObservedInteractable interactable)
        => interactable.Classification == InteractableClass.TreasureCoffer
           && IsTreasureCofferLockedRetryActive(interactable.Key);

    private bool IsTreasureCofferLockedRetryActive(string interactableKey)
        => !string.IsNullOrWhiteSpace(treasureCofferLockedRetryKey)
           && string.Equals(treasureCofferLockedRetryKey, interactableKey, StringComparison.Ordinal);

    private void ResetTreasureCofferLockedRetry()
    {
        treasureCofferLockedRetryKey = null;
        treasureCofferLockedNextRetryUtc = DateTime.MinValue;
    }

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

        if (currentObjectiveKind == PlannerObjectiveKind.TreasureDoor
            && pendingProgressionInteractable is { Classification: InteractableClass.TreasureDoor }
            && pendingTreasureDoorTransitionPoint is not null)
        {
            if (ShouldSwitchFromStaleTreasureDoorFollowThrough(
                    plannerInteractable,
                    plannerObjectiveKind,
                    playerPosition,
                    out var staleReason))
            {
                reason = $"Switching from stale treasure door follow-through on {currentInteractable.Name} to same-floor planner target {plannerInteractable.Name}. {staleReason}";
                return true;
            }

            reason = $"Holding committed treasure door follow-through on {currentInteractable.Name}; ADS will not switch to {plannerInteractable.Name} until the opened-door follow-through resolves.";
            return false;
        }

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

    private bool ShouldSwitchFromStaleTreasureDoorFollowThrough(
        ObservedInteractable plannerInteractable,
        PlannerObjectiveKind plannerObjectiveKind,
        Vector3? playerPosition,
        out string reason)
    {
        reason = string.Empty;
        var transitionPoint = pendingTreasureDoorTransitionPoint;
        if (!playerPosition.HasValue || transitionPoint is null)
            return false;

        if (plannerObjectiveKind is not (PlannerObjectiveKind.TreasureDoor or PlannerObjectiveKind.TreasureCoffer)
            || plannerInteractable.Classification is not (InteractableClass.TreasureDoor or InteractableClass.TreasureCoffer))
        {
            return false;
        }

        var followThroughVerticalDelta = MathF.Abs(transitionPoint.Position.Y - playerPosition.Value.Y);
        var plannerVerticalDelta = MathF.Abs(plannerInteractable.Position.Y - playerPosition.Value.Y);
        if (followThroughVerticalDelta <= TreasureDoorFollowThroughStaleVerticalDelta
            || plannerVerticalDelta > TreasureDoorPlannerSameFloorVerticalDelta)
        {
            return false;
        }

        reason = $"Pending follow-through {transitionPoint.Name} at {FormatVector(transitionPoint.Position)} is Y {followThroughVerticalDelta:0.0}y from player {FormatVector(playerPosition.Value)}, while planner target is Y {plannerVerticalDelta:0.0}y from player.";
        return true;
    }

    private void PrepareInteractableSwitch(
        DutyContextSnapshot context,
        ObservedInteractable currentInteractable,
        PlannerObjectiveKind currentObjectiveKind,
        ObservedInteractable plannerInteractable,
        PlannerObjectiveKind plannerObjectiveKind,
        string switchReason)
    {
        if (currentObjectiveKind != PlannerObjectiveKind.TreasureDoor
            || currentInteractable.Classification != InteractableClass.TreasureDoor)
        {
            return;
        }

        if (!ShouldSwitchFromStaleTreasureDoorFollowThrough(
                plannerInteractable,
                plannerObjectiveKind,
                objectTable.LocalPlayer?.Position,
                out var staleReason))
        {
            return;
        }

        observationMemoryService.MarkProgressionInteractionSent(context, currentInteractable);
        TryRetirePendingSatisfiedManualDestination(currentInteractable, objectTable.LocalPlayer?.Position);
        log?.Information($"[ADS] Retired stale treasure door follow-through on {currentInteractable.Name} before planner switch. {staleReason} {switchReason}");
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

    private bool ShouldBypassCombatHold(DutyContextSnapshot context, PlannerSnapshot planner, ObservationSnapshot observation)
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
            && planner.ObjectiveKind == PlannerObjectiveKind.BossFightMonster)
        {
            return ResolveBossFightCombatGhost(planner, observation) is null;
        }

        if (planner.Mode == PlannerMode.Progression
            && planner.ObjectiveKind is PlannerObjectiveKind.CombatFriendlyInteractable
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
        ResetTreasureCofferLockedRetry();
        ResetTreasureCofferRouteTracking();
        ResetCloseRangeInteractFallbackTracking();
        ResetCameraBeforeInteractTracking();
        ClearPendingProgressionInteractResult();
        ResetInteractArrivalWait();
    }

    private bool TrySkipStuckTreasureCoffer(ObservedInteractable observedInteractable, float targetHorizontalDistance, string prefix)
    {
        if (observedInteractable.Classification != InteractableClass.TreasureCoffer)
            return false;

        if (IsTreasureCofferLockedRetryActive(observedInteractable))
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

    private bool TryHoldResetCameraBeforeInteract(ObservedInteractable interactable, bool cameraBasedInteract, string prefix, DateTime now)
    {
        if (!ShouldResetCameraBeforeInteract(interactable, cameraBasedInteract))
        {
            ResetCameraBeforeInteractTracking();
            return false;
        }

        var retryKey = BuildInteractableRetryIdentity(interactable);
        if (!string.Equals(resetCameraBeforeInteractKey, retryKey, StringComparison.Ordinal))
        {
            if (!TryRequestCameraResetBeforeInteract(interactable))
                return false;

            resetCameraBeforeInteractKey = retryKey;
            resetCameraBeforeInteractReadyUtc = now + ResetCameraBeforeInteractDelay;
            if (!string.Equals(lastResetCameraBeforeInteractLogKey, retryKey, StringComparison.Ordinal))
            {
                lastResetCameraBeforeInteractLogKey = retryKey;
                log?.Information($"[ADS] Requested camera reset before interacting with {interactable.Name} ({interactable.Classification}).");
            }

            SetPhase(
                ExecutionPhase.AttemptingInteractableObjective,
                $"{prefix} Resetting camera before interacting with {interactable.Name}.");
            return true;
        }

        if (now < resetCameraBeforeInteractReadyUtc)
        {
            SetPhase(
                ExecutionPhase.AttemptingInteractableObjective,
                $"{prefix} Waiting briefly for camera reset before interacting with {interactable.Name}.");
            return true;
        }

        ResetCameraBeforeInteractTracking();
        return false;
    }

    private bool ShouldResetCameraBeforeInteract(ObservedInteractable interactable, bool cameraBasedInteract)
    {
        return configuration.ResetCameraBeforeInteractEnabled
               && cameraBasedInteract
               && interactable.Classification is InteractableClass.Required or InteractableClass.TreasureDoor;
    }

    private unsafe bool TryRequestCameraResetBeforeInteract(ObservedInteractable interactable)
    {
        try
        {
            var cameraManager = CameraManager.Instance();
            if (cameraManager == null || cameraManager->Camera == null)
                return false;

            cameraManager->Camera->ShouldResetAngles = true;
            return true;
        }
        catch (Exception ex)
        {
            log?.Warning(ex, $"[ADS] Failed to request camera reset before interacting with {interactable.Name}.");
            return false;
        }
    }

    private void ResetCameraBeforeInteractTracking()
    {
        resetCameraBeforeInteractKey = null;
        resetCameraBeforeInteractReadyUtc = DateTime.MinValue;
    }

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

        if (GameInteractionHelper.IsAddonVisible("SelectYesno"))
        {
            StopMovementAssistsForSelectYesno(planner);
            SetPhase(
                ExecutionPhase.AttemptingInteractableObjective,
                $"{prefix} {BuildSelectYesnoHoldStatus($"SelectYesno is visible during {pendingInteractable.Name} follow-through; ADS is holding movement while dialog automation resolves the prompt.")}");
            return true;
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
                PrepareInteractableSwitch(
                    context,
                    pendingInteractable,
                    GetObjectiveKindForInteractable(pendingInteractable.Classification),
                    plannerInteractable,
                    planner.ObjectiveKind,
                    switchReason);
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
            if (treasureDoorReadyForFollowThrough)
            {
                if (TryAdvanceTreasureDoorFollowThrough(prefix, out var reachedTreasureDoorTransitionPoint, out var staleTreasureDoorFollowThrough))
                {
                    if (reachedTreasureDoorTransitionPoint)
                    {
                        observationMemoryService.MarkProgressionInteractionSent(context, pendingInteractable);
                        TryRetirePendingSatisfiedManualDestination(pendingInteractable, playerPosition);
                        ClearInteractableCommitment();
                    }

                    return true;
                }

                if (staleTreasureDoorFollowThrough)
                {
                    ClearStaleTreasureDoorFollowThrough(context, pendingInteractable, playerPosition, prefix);
                    return true;
                }
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

        if (treasureDoorReadyForFollowThrough)
        {
            if (TryAdvanceTreasureDoorFollowThrough(prefix, out var reachedTreasureDoorTransitionPointAfterWindow, out var staleTreasureDoorFollowThroughAfterWindow))
            {
                if (reachedTreasureDoorTransitionPointAfterWindow)
                {
                    observationMemoryService.MarkProgressionInteractionSent(context, pendingInteractable);
                    TryRetirePendingSatisfiedManualDestination(pendingInteractable, playerPosition);
                    ClearInteractableCommitment();
                }

                return true;
            }

            if (staleTreasureDoorFollowThroughAfterWindow)
            {
                ClearStaleTreasureDoorFollowThrough(context, pendingInteractable, playerPosition, prefix);
                return true;
            }
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

    private void ClearStaleTreasureDoorFollowThrough(
        DutyContextSnapshot context,
        ObservedInteractable pendingInteractable,
        Vector3? playerPosition,
        string prefix)
    {
        var transitionPoint = pendingTreasureDoorTransitionPoint;
        var staleSummary = transitionPoint is not null && playerPosition.HasValue
            ? $"target {transitionPoint.Name} at {FormatVector(transitionPoint.Position)} was Y {MathF.Abs(transitionPoint.Position.Y - playerPosition.Value.Y):0.0}y from player {FormatVector(playerPosition.Value)}"
            : "target no longer had enough live position truth";

        observationMemoryService.MarkProgressionInteractionSent(context, pendingInteractable);
        TryRetirePendingSatisfiedManualDestination(pendingInteractable, playerPosition);
        ClearInteractableCommitment();
        StopMovementAssists();
        log?.Information($"[ADS] Cleared stale treasure door follow-through for {pendingInteractable.Name}; {staleSummary}. ADS will replan from refreshed live duty truth.");
        SetPhase(
            ExecutionPhase.AttemptingInteractableObjective,
            $"{prefix} Treasure door follow-through for {pendingInteractable.Name} became stale across floors; ADS marked the opened door consumed and is replanning from live duty truth.");
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
        ResetTreasureDoorJiggleTracking(releaseKeys: true);
    }

    private bool TryAdvanceTreasureDoorFollowThrough(string prefix, out bool reachedTransitionPoint, out bool staleCrossFloorTarget)
        => TryAdvanceTreasureDoorFollowThroughPoint(
            pendingTreasureDoorTransitionPoint,
            prefix,
            "Treasure door",
            "after the interact",
            pauseForSelectYesno: true,
            stopMovementOnReach: true,
            maxNudgeAttempts: null,
            out reachedTransitionPoint,
            out staleCrossFloorTarget,
            out _);

    private bool TryAdvanceTreasureDoorFollowThroughPoint(
        DungeonFrontierPoint? frontierPoint,
        string prefix,
        string label,
        string contextText,
        bool pauseForSelectYesno,
        bool stopMovementOnReach,
        int? maxNudgeAttempts,
        out bool reachedTransitionPoint,
        out bool staleCrossFloorTarget,
        out bool stuckRecoveryExhausted)
    {
        reachedTransitionPoint = false;
        staleCrossFloorTarget = false;
        stuckRecoveryExhausted = false;
        var playerPosition = objectTable.LocalPlayer?.Position;
        if (frontierPoint is null || !playerPosition.HasValue)
        {
            ResetTreasureDoorJiggleTracking(releaseKeys: true);
            return false;
        }

        if (pauseForSelectYesno && GameInteractionHelper.IsAddonVisible("SelectYesno"))
        {
            StopMovementAssistsForSelectYesno();
            SetPhase(
                ExecutionPhase.AttemptingInteractableObjective,
                $"{prefix} {BuildSelectYesnoHoldStatus($"{label} follow-through is paused because SelectYesno is visible; ADS is waiting for dialog automation.")}");
            return true;
        }

        var targetHorizontalDistance = GetHorizontalDistance(frontierPoint.Position, playerPosition.Value);
        var targetDistance = Vector3.Distance(frontierPoint.Position, playerPosition.Value);
        var targetVerticalDelta = MathF.Abs(frontierPoint.Position.Y - playerPosition.Value.Y);
        if (targetVerticalDelta > TreasureDoorFollowThroughStaleVerticalDelta)
        {
            staleCrossFloorTarget = true;
            ResetTreasureDoorJiggleTracking(releaseKeys: true);
            return false;
        }

        var arrivalRange = frontierPoint.ArrivalRadiusXz > 0f
            ? frontierPoint.ArrivalRadiusXz
            : PreferredFrontierArrivalRange;
        if (targetHorizontalDistance > arrivalRange)
        {
            var navigationDecision = ResolveTreasureDoorFollowThroughNavigation(frontierPoint, playerPosition.Value, maxNudgeAttempts);
            if (navigationDecision.StuckRecoveryExhausted)
            {
                stuckRecoveryExhausted = true;
                ResetTreasureDoorJiggleTracking(releaseKeys: true);
                return false;
            }

            TryBeginNavigation(navigationDecision.NavigationTargetId, navigationDecision.Destination);
            SetPhase(
                ExecutionPhase.AttemptingInteractableObjective,
                $"{prefix} {label} follow-through is advancing through {frontierPoint.Name} {contextText} (XZ {targetHorizontalDistance:0.0}y, 3D {targetDistance:0.0}y, Y {targetVerticalDelta:0.0}y).{navigationDecision.StatusText}");
            return true;
        }

        reachedTransitionPoint = true;
        ResetTreasureDoorJiggleTracking(releaseKeys: true);
        if (stopMovementOnReach)
            StopMovementAssists();
        else
            StopNavigationForTreasureRouteNudge();
        SetPhase(
            ExecutionPhase.AttemptingInteractableObjective,
            $"{prefix} {label} follow-through reached {frontierPoint.Name} (XZ {targetHorizontalDistance:0.0}y, 3D {targetDistance:0.0}y, Y {targetVerticalDelta:0.0}y) and is waiting for refreshed duty truth.");
        return true;
    }

    private TreasureDoorNavigationDecision ResolveTreasureDoorFollowThroughNavigation(
        DungeonFrontierPoint frontierPoint,
        Vector3 playerPosition,
        int? maxNudgeAttempts)
    {
        var originalTargetId = BuildFrontierTargetId(frontierPoint);
        if (!configuration.TreasureDoorJiggleRecoveryEnabled)
        {
            ResetTreasureDoorJiggleTracking(releaseKeys: true);
            return new TreasureDoorNavigationDecision(frontierPoint.Position, originalTargetId, string.Empty, StuckRecoveryExhausted: false);
        }

        var now = DateTime.UtcNow;
        if (!string.Equals(treasureDoorNudgeTargetKey, frontierPoint.Key, StringComparison.Ordinal)
            || !treasureDoorNudgeBaselinePosition.HasValue)
        {
            StartTreasureDoorJiggleTracking(frontierPoint, playerPosition, now);
            return new TreasureDoorNavigationDecision(frontierPoint.Position, originalTargetId, string.Empty, StuckRecoveryExhausted: false);
        }

        if (now < treasureDoorNudgeUntilUtc)
        {
            return new TreasureDoorNavigationDecision(
                treasureDoorNudgeDestination,
                BuildTreasureDoorNudgeTargetId(frontierPoint),
                $" Door-frame recovery is moving to vnav side-nudge attempt {treasureDoorNudgeAttempt} at {FormatVector(treasureDoorNudgeDestination)}.",
                StuckRecoveryExhausted: false);
        }

        if (treasureDoorNudgeUntilUtc != DateTime.MinValue)
        {
            ResumeTreasureDoorJiggleTrackingAfterNudge(frontierPoint, playerPosition, now);
            return new TreasureDoorNavigationDecision(frontierPoint.Position, originalTargetId, string.Empty, StuckRecoveryExhausted: false);
        }

        if (GetHorizontalDistance(treasureDoorNudgeBaselinePosition.Value, playerPosition) > TreasureDoorNudgeProgressDistance)
        {
            StartTreasureDoorJiggleTracking(frontierPoint, playerPosition, now);
            return new TreasureDoorNavigationDecision(frontierPoint.Position, originalTargetId, string.Empty, StuckRecoveryExhausted: false);
        }

        if (now - treasureDoorNudgeLastProgressUtc < TreasureDoorNudgeStuckTimeout)
            return new TreasureDoorNavigationDecision(frontierPoint.Position, originalTargetId, string.Empty, StuckRecoveryExhausted: false);

        if (maxNudgeAttempts.HasValue && treasureDoorNudgeAttempt >= maxNudgeAttempts.Value)
        {
            return new TreasureDoorNavigationDecision(
                frontierPoint.Position,
                originalTargetId,
                string.Empty,
                StuckRecoveryExhausted: true);
        }

        treasureDoorNudgeAttempt = treasureDoorNudgeAttempt >= 2 ? 1 : treasureDoorNudgeAttempt + 1;
        treasureDoorNudgeDestination = BuildTreasureDoorSideNudgeDestination(frontierPoint.Position, playerPosition, treasureDoorNudgeAttempt);
        treasureDoorNudgeUntilUtc = now + TreasureDoorNudgeHoldDuration;
        treasureDoorNudgeBaselinePosition = playerPosition;
        treasureDoorNudgeLastProgressUtc = now;
        log?.Information(
            $"[ADS] Treasure door follow-through vnav side-nudge attempt {treasureDoorNudgeAttempt} started for {frontierPoint.Name}; XZ movement stayed under {TreasureDoorNudgeProgressDistance:0.0}y for {TreasureDoorNudgeStuckTimeout.TotalSeconds:0}s. Destination {FormatVector(treasureDoorNudgeDestination)}.");

        return new TreasureDoorNavigationDecision(
            treasureDoorNudgeDestination,
            BuildTreasureDoorNudgeTargetId(frontierPoint),
            $" Door-frame recovery is moving to vnav side-nudge attempt {treasureDoorNudgeAttempt} at {FormatVector(treasureDoorNudgeDestination)}.",
            StuckRecoveryExhausted: false);
    }

    private void StartTreasureDoorJiggleTracking(DungeonFrontierPoint frontierPoint, Vector3 playerPosition, DateTime now)
    {
        ResetTreasureDoorJiggleTracking(releaseKeys: true);
        treasureDoorNudgeTargetKey = frontierPoint.Key;
        treasureDoorNudgeBaselinePosition = playerPosition;
        treasureDoorNudgeLastProgressUtc = now;
    }

    private void ResumeTreasureDoorJiggleTrackingAfterNudge(DungeonFrontierPoint frontierPoint, Vector3 playerPosition, DateTime now)
    {
        treasureDoorNudgeTargetKey = frontierPoint.Key;
        treasureDoorNudgeBaselinePosition = playerPosition;
        treasureDoorNudgeLastProgressUtc = now;
        treasureDoorNudgeUntilUtc = DateTime.MinValue;
        treasureDoorNudgeDestination = Vector3.Zero;
    }

    private ulong BuildTreasureDoorNudgeTargetId(DungeonFrontierPoint frontierPoint)
        => BuildFrontierTargetId(frontierPoint) ^ (0x0100000000000000UL + (ulong)Math.Max(1, treasureDoorNudgeAttempt));

    private static Vector3 BuildTreasureDoorSideNudgeDestination(Vector3 targetPosition, Vector3 playerPosition, int attempt)
    {
        var flatDelta = new Vector3(targetPosition.X - playerPosition.X, 0f, targetPosition.Z - playerPosition.Z);
        var flatDistance = flatDelta.Length();
        var forward = flatDistance <= float.Epsilon
            ? Vector3.UnitZ
            : Vector3.Normalize(flatDelta);
        var side = new Vector3(-forward.Z, 0f, forward.X);
        var sideSign = attempt % 2 == 1 ? 1f : -1f;

        return new Vector3(
            playerPosition.X + (forward.X * TreasureDoorNudgeForwardOffset) + (side.X * TreasureDoorNudgeSideOffset * sideSign),
            playerPosition.Y,
            playerPosition.Z + (forward.Z * TreasureDoorNudgeForwardOffset) + (side.Z * TreasureDoorNudgeSideOffset * sideSign));
    }

    private void ResetTreasureDoorJiggleTracking(bool releaseKeys)
    {
        treasureDoorNudgeTargetKey = null;
        treasureDoorNudgeBaselinePosition = null;
        treasureDoorNudgeLastProgressUtc = DateTime.MinValue;
        treasureDoorNudgeAttempt = 0;
        treasureDoorNudgeUntilUtc = DateTime.MinValue;
        treasureDoorNudgeDestination = Vector3.Zero;
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

    private string BuildSelectYesnoHoldStatus(string fallback)
        => string.IsNullOrWhiteSpace(currentDialogAutomationStatus)
            ? fallback
            : $"{fallback} Dialog automation status: {currentDialogAutomationStatus}";

    private void StopMovementAssistsForSelectYesno(PlannerSnapshot? planner = null)
    {
        var hadPendingTreasureFollowerDoorFollowThrough = HasPendingTreasureFollowerDoorFollowThrough();
        var preserveTreasureFollowerDoorFollowThrough = ShouldPreserveTreasureFollowerDoorFollowThroughForSelectYesno(planner);
        StopMovementAssists(preserveTreasureFollowerDoorFollowThrough);
        if (preserveTreasureFollowerDoorFollowThrough && !hadPendingTreasureFollowerDoorFollowThrough)
            ResetTreasureRouteStuckTracking();

        if (preserveTreasureFollowerDoorFollowThrough)
            LogTreasureFollowerSelectYesnoPreserveOnce();
        else
            lastTreasureFollowerSelectYesnoPreserveLogKey = string.Empty;
    }

    private bool ShouldPreserveTreasureFollowerDoorFollowThroughForSelectYesno(PlannerSnapshot? planner)
    {
        if (EffectiveTreasureDungeonRole != ADS.Models.TreasureDungeonRole.Follower)
            return false;

        if (HasPendingTreasureFollowerDoorFollowThrough())
            return true;

        if (dungeonFrontierService.CurrentMode != FrontierMode.TreasureDungeon
            || dungeonFrontierService.CurrentTarget is not { IsTreasurePassageCandidate: true })
        {
            return false;
        }

        return planner is null
               || (planner.Mode == PlannerMode.Progression
                   && planner.ObjectiveKind == PlannerObjectiveKind.Frontier);
    }

    private void LogTreasureFollowerSelectYesnoPreserveOnce()
    {
        var key = treasureFollowerDoorFollowThroughCandidateKey
                  ?? dungeonFrontierService.CurrentTarget?.Key
                  ?? "unknown";
        if (string.Equals(key, lastTreasureFollowerSelectYesnoPreserveLogKey, StringComparison.Ordinal))
            return;

        lastTreasureFollowerSelectYesnoPreserveLogKey = key;
        log?.Information("[ADS] treasure follower selectyesno action=preserve-door-follow-through");
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

    private void UpdateLeaveDuty(DutyContextSnapshot context, ObservationSnapshot observation, bool considerTreasureCoffers)
    {
        var now = DateTime.UtcNow;
        if (considerTreasureCoffers && !leaveTreasureSweepStarted)
            BeginLeaveTreasureSweep(now, "leave state");

        if (higherLowerBlocksDutyExit)
        {
            HoldLeaveDutyForHigherLower(now, considerTreasureCoffers);
            return;
        }

        if (context.InCombat)
        {
            StopMovementAssists();
            leaveTreasureSweepClearSinceUtc = DateTime.MinValue;
            SetPhase(ExecutionPhase.LeavingDuty, "Leave requested. Waiting for combat to clear before duty exit.");
            return;
        }

        if (considerTreasureCoffers
            && !leaveDutyExitArmed
            && GameInteractionHelper.IsAddonVisible("SelectYesno"))
        {
            StopMovementAssists();
            SetPhase(
                ExecutionPhase.LeavingDuty,
                BuildSelectYesnoHoldStatus("Leave requested. SelectYesno is visible before duty exit is armed; ADS is waiting for dialog automation before continuing the treasure sweep."));
            return;
        }

        if (considerTreasureCoffers && leaveTreasureInteractionSent && now < nextInteractAttemptUtc)
        {
            StopMovementAssists();
            SetPhase(ExecutionPhase.LeavingDuty, "Leave requested. Final treasure sweep is waiting for the treasure interaction result.");
            return;
        }

        var playerPosition = objectTable.LocalPlayer?.Position;
        if (considerTreasureCoffers && playerPosition.HasValue)
        {
            var nearbyTreasure = FindNearestLeaveSweepTreasureCoffer(context, observation, playerPosition.Value);
            if (nearbyTreasure is not null)
            {
                leaveTreasureSweepClearSinceUtc = DateTime.MinValue;
                leaveLootDistributionWaitUntilUtc = DateTime.MinValue;
                leaveTreasureSweepClearLogged = false;
                leaveLootDistributionWaitLogged = false;
                LogLeaveTreasureFound(nearbyTreasure);
                var previousInteractGameObjectId = lastInteractGameObjectId;
                var previousInteractAttemptUtc = nextInteractAttemptUtc;
                TryAdvanceInteractableObjective(
                    context,
                    nearbyTreasure,
                    "Leave requested. Final treasure sweep is clearing nearby treasure before duty exit.");
                if (lastInteractGameObjectId == nearbyTreasure.GameObjectId
                    && (previousInteractGameObjectId != lastInteractGameObjectId
                        || nextInteractAttemptUtc > previousInteractAttemptUtc))
                {
                    leaveTreasureInteractionSent = true;
                    log?.Information($"[ADS] Final treasure sweep interacted with {nearbyTreasure.Name}; waiting for dialog automation and loot distribution before duty exit.");
                }
                return;
            }
        }

        if (considerTreasureCoffers)
        {
            if (now < leaveTreasureSweepGraceUntilUtc)
            {
                StopMovementAssists();
                LogLeaveTreasureGraceActive(now);
                SetPhase(
                    ExecutionPhase.LeavingDuty,
                    $"Leave requested. Final coffer grace active for {(leaveTreasureSweepGraceUntilUtc - now).TotalSeconds:0.0}s before sweep-clear.");
                return;
            }

            if (leaveTreasureSweepClearSinceUtc == DateTime.MinValue)
            {
                leaveTreasureSweepClearSinceUtc = now;
                LogLeaveTreasureSweepClear();
            }

            if (now - leaveTreasureSweepClearSinceUtc < LeaveTreasureSweepSettleDelay)
            {
                StopMovementAssists();
                SetPhase(ExecutionPhase.LeavingDuty, "Leave requested. Final treasure sweep is clear; waiting for the nearby treasure list to settle before duty exit.");
                return;
            }

            if (leaveTreasureInteractionSent)
            {
                if (leaveLootDistributionWaitUntilUtc == DateTime.MinValue)
                {
                    leaveLootDistributionWaitUntilUtc = now + LeaveLootDistributionDelay;
                    LogLeaveLootDistributionWait();
                }

                if (now < leaveLootDistributionWaitUntilUtc)
                {
                    StopMovementAssists();
                    SetPhase(
                        ExecutionPhase.LeavingDuty,
                        $"Leave requested. Final treasure sweep is clear, waiting {(leaveLootDistributionWaitUntilUtc - now).TotalSeconds:0.0}s for loot distribution before duty exit.");
                    return;
                }
            }
        }

        ArmLeaveDutyExit(considerTreasureCoffers ? "final treasure sweep complete" : "treasure sweep disabled");
        StopMovementAssists();
        TrySendLeaveDutyUi();
    }

    private ObservedInteractable? FindNearestLeaveSweepTreasureCoffer(
        DutyContextSnapshot context,
        ObservationSnapshot observation,
        Vector3 playerPosition)
        => observation.LiveInteractables
            .Where(x => IsLeaveSweepTreasureCoffer(x)
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

    private static bool IsLeaveSweepTreasureCoffer(ObservedInteractable interactable)
        => interactable.Classification == InteractableClass.TreasureCoffer
           || LooksLikeLeaveSweepTreasureCofferName(interactable.Name);

    private static bool LooksLikeLeaveSweepTreasureCofferName(string name)
    {
        var normalized = name.Trim();
        return normalized.Contains("coffer", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("leather sack", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("treasure chest", StringComparison.OrdinalIgnoreCase)
               || normalized.Equals("chest", StringComparison.OrdinalIgnoreCase)
               || normalized.EndsWith(" chest", StringComparison.OrdinalIgnoreCase);
    }

    private void BeginLeaveTreasureSweep(DateTime now, string reason)
    {
        leaveTreasureSweepStarted = true;
        leaveTreasureSweepGraceUntilUtc = now + LeaveFinalCofferSpawnGrace;
        leaveDutyExitArmed = false;
        log?.Information($"[ADS] Final treasure sweep started ({reason}); final coffer grace active for {LeaveFinalCofferSpawnGrace.TotalSeconds:0.0}s.");
    }

    private void HoldLeaveDutyForHigherLower(DateTime now, bool considerTreasureCoffers)
    {
        if (higherLowerAutomationHold)
            PauseMovementAssistsForHigherLower();
        else
            StopMovementAssists();

        leaveDutyExitArmed = false;
        nextLeaveUiAttemptUtc = DateTime.MinValue;
        if (considerTreasureCoffers)
        {
            leaveTreasureSweepClearSinceUtc = DateTime.MinValue;
            leaveTreasureSweepGraceUntilUtc = now + LeaveFinalCofferSpawnGrace;
            leaveTreasureSweepGraceLogged = false;
            leaveTreasureSweepClearLogged = false;
            leaveLootDistributionWaitUntilUtc = DateTime.MinValue;
            leaveLootDistributionWaitLogged = false;
        }

        LogHigherLowerDutyExitHoldOnce();
        SetPhase(
            ExecutionPhase.LeavingDuty,
            $"Leave requested. Higher/Lower activity is blocking duty exit; ADS is holding leave UI and will re-run final treasure sweep after the quiet grace. {higherLowerAutomationStatus}");
    }

    private void LogLeaveTreasureGraceActive(DateTime now)
    {
        if (leaveTreasureSweepGraceLogged)
            return;

        leaveTreasureSweepGraceLogged = true;
        log?.Information($"[ADS] Final coffer grace active; ADS will keep scanning until {(leaveTreasureSweepGraceUntilUtc - now).TotalSeconds:0.0}s elapse.");
    }

    private void LogLeaveTreasureFound(ObservedInteractable treasure)
    {
        if (string.Equals(lastLoggedLeaveTreasureKey, treasure.Key, StringComparison.Ordinal))
            return;

        lastLoggedLeaveTreasureKey = treasure.Key;
        log?.Information($"[ADS] Final treasure sweep found {treasure.Name} at {FormatVector(treasure.Position)}; clearing it before duty exit.");
    }

    private void LogLeaveTreasureSweepClear()
    {
        if (leaveTreasureSweepClearLogged)
            return;

        leaveTreasureSweepClearLogged = true;
        log?.Information($"[ADS] Final treasure sweep clear; settling for {LeaveTreasureSweepSettleDelay.TotalSeconds:0.0}s before duty exit.");
    }

    private void LogLeaveLootDistributionWait()
    {
        if (leaveLootDistributionWaitLogged)
            return;

        leaveLootDistributionWaitLogged = true;
        log?.Information($"[ADS] Final treasure sweep loot wait started for {LeaveLootDistributionDelay.TotalSeconds:0.0}s before duty exit.");
    }

    private void ArmLeaveDutyExit(string reason)
    {
        if (leaveDutyExitArmed)
            return;

        leaveDutyExitArmed = true;
        log?.Information($"[ADS] Final treasure sweep safe; duty exit armed ({reason}).");
    }

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
        leaveTreasureSweepGraceUntilUtc = DateTime.MinValue;
        leaveTreasureInteractionSent = false;
        leaveTreasureSweepStarted = false;
        leaveTreasureSweepGraceLogged = false;
        leaveTreasureSweepClearLogged = false;
        leaveLootDistributionWaitLogged = false;
        leaveDutyExitArmed = false;
        higherLowerDutyExitHoldLogged = false;
        lastLoggedLeaveTreasureKey = string.Empty;
        lastLoggedLeavePromptKey = string.Empty;
        lastLoggedLeavePromptAtUtc = DateTime.MinValue;
    }

    private string BuildCombatHoldStatus(string prefix, PlannerSnapshot planner, ObservationSnapshot observation)
    {
        var ghostedBoss = ResolveBossFightCombatGhost(planner, observation);
        if (ghostedBoss is not null)
        {
            return $"{prefix} BossFight target {ghostedBoss.Name} already reached {BossFightCombatGhostRange:0.0}y in this combat. ADS is holding progression and combat owns movement until combat clears.";
        }

        return $"{prefix} Combat is active, so ADS is holding progression until combat clears.";
    }

    private void ArmBossFightCombatGhost(ObservedMonster observedMonster, float targetDistance)
    {
        if (IsBossFightCombatGhosted(observedMonster))
            return;

        bossFightCombatGhostKey = observedMonster.Key;
        bossFightCombatGhostName = observedMonster.Name;
        bossFightCombatGhostMapId = observedMonster.MapId;
        log?.Information(
            $"[ADS] BossFight combat ghost armed for {observedMonster.Name} at {targetDistance:0.0}y (key {observedMonster.Key}). ADS will hold combat movement for this live boss until combat clears.");
    }

    private void ClearBossFightCombatGhost(string reason)
    {
        if (string.IsNullOrWhiteSpace(bossFightCombatGhostKey))
            return;

        log?.Information($"[ADS] Cleared BossFight combat ghost for {bossFightCombatGhostName} during {reason}.");
        bossFightCombatGhostKey = null;
        bossFightCombatGhostName = string.Empty;
        bossFightCombatGhostMapId = 0;
    }

    private ObservedMonster? ResolveBossFightCombatGhost(PlannerSnapshot planner, ObservationSnapshot observation)
    {
        if (string.IsNullOrWhiteSpace(bossFightCombatGhostKey)
            || planner.ObjectiveKind != PlannerObjectiveKind.BossFightMonster)
        {
            return null;
        }

        var observedMonster = ResolveObservedMonster(planner, observation);
        return observedMonster is not null && IsBossFightCombatGhosted(observedMonster)
            ? observedMonster
            : null;
    }

    private bool IsBossFightCombatGhosted(ObservedMonster observedMonster)
    {
        if (string.IsNullOrWhiteSpace(bossFightCombatGhostKey))
            return false;

        return string.Equals(observedMonster.Key, bossFightCombatGhostKey, StringComparison.Ordinal)
            && (bossFightCombatGhostMapId == 0
                || observedMonster.MapId == 0
                || observedMonster.MapId == bossFightCombatGhostMapId);
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
            if (obj.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Pc
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

    private bool TryBeginNavigation(ulong gameObjectId, Vector3 destination)
    {
        if (IsTreasureFollowerPostTransitSettleActive())
        {
            StopNavigationForTreasureFollowerPostTransitSettle();
            return false;
        }

        if (manualDestinationNavigationTargetId.HasValue
            && manualDestinationNavigationTargetId.Value != gameObjectId)
        {
            ResetManualDestinationNoProgressTracking(clearStatus: true);
        }

        var now = DateTime.UtcNow;
        if (navigationActive && !mapFlagNavigationActive && movementTargetGameObjectId == gameObjectId && now < nextNavigationCommandUtc)
            return true;

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
            return true;
        }

        return false;
    }

    private bool TryBeginMapFlagNavigation(DutyContextSnapshot context, ulong targetId, string targetName, Vector3 destination)
    {
        if (IsTreasureFollowerPostTransitSettleActive())
        {
            StopNavigationForTreasureFollowerPostTransitSettle();
            return false;
        }

        if (manualDestinationNavigationTargetId.HasValue
            && manualDestinationNavigationTargetId.Value != targetId)
        {
            ResetManualDestinationNoProgressTracking(clearStatus: true);
        }

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
        IReadOnlyCollection<MountedCombatTarget> targets,
        MountedCombatAction action,
        out MountedCombatClusterTarget selectedTarget)
    {
        var clusterRadiusSquared = MountedCombatClusterRadius * MountedCombatClusterRadius;

        var bestTarget = default(MountedCombatClusterTarget);
        var found = false;
        foreach (var target in targets)
        {
            if (!IsMountedCombatTargetWithinActionRange(action, target))
                continue;

            var clusterCount = 0;
            foreach (var otherTarget in targets)
            {
                if (Vector3.DistanceSquared(target.GameObject.Position, otherTarget.GameObject.Position) <= clusterRadiusSquared)
                    clusterCount++;
            }

            var candidate = new MountedCombatClusterTarget(target.Observed, target.GameObject, target.HorizontalDistance, target.Distance3d, target.VerticalDelta, clusterCount);
            if (!found
                || candidate.ClusterCount > bestTarget.ClusterCount
                || (candidate.ClusterCount == bestTarget.ClusterCount && candidate.HorizontalDistance < bestTarget.HorizontalDistance))
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
        foreach (var target in targets.OrderBy(x => x.HorizontalDistance))
        {
            if (IsMountedCombatTargetWithinActionRange(action, target))
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
        foreach (var target in targets.OrderBy(x => x.HorizontalDistance))
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

    private static bool TryGetNearestMountedCombatTarget(
        IEnumerable<MountedCombatTarget> targets,
        out MountedCombatTarget selectedTarget)
    {
        foreach (var target in targets.OrderBy(x => x.HorizontalDistance))
        {
            selectedTarget = target;
            return true;
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
        return IsMountedCombatTargetWithinActionRange(action, target);
    }

    private static bool TryGetNearestMountedCombatTargetInRange(
        IEnumerable<MountedCombatTarget> targets,
        MountedCombatAction action,
        out MountedCombatTarget selectedTarget)
    {
        foreach (var target in targets.OrderBy(x => x.HorizontalDistance))
        {
            if (IsMountedCombatTargetWithinActionRange(action, target))
            {
                selectedTarget = target;
                return true;
            }
        }

        selectedTarget = default;
        return false;
    }

    private static float GetMountedCombatActionRange(MountedCombatAction action)
        => action.Range > 0f ? action.Range : DefaultMountedCombatActionRange;

    private static bool IsMountedCombatTargetWithinActionRange(MountedCombatAction action, MountedCombatTarget target)
        => target.HorizontalDistance <= GetMountedCombatActionRange(action)
           && target.VerticalDelta <= PraetoriumRearTargetMaxVerticalDelta;

    private static Vector3 BuildMountedCombatBlindRearFallbackPosition(Vector3 localPlayerPosition, float localPlayerRotation)
    {
        var forward = new Vector2(MathF.Sin(localPlayerRotation), MathF.Cos(localPlayerRotation));
        var fallbackPosition = new Vector2(localPlayerPosition.X, localPlayerPosition.Z) - (forward * PraetoriumBlindRearFallbackDistance);
        return new Vector3(fallbackPosition.X, localPlayerPosition.Y, fallbackPosition.Y);
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
        if (GameInteractionHelper.TryGetSelectYesNoPromptText(Plugin.GameGui, out var selectYesnoPrompt))
        {
            if (!IsLeaveDutyConfirmationPrompt(selectYesnoPrompt))
            {
                LogLeavePromptRejected(selectYesnoPrompt, now);
                SetPhase(
                    ExecutionPhase.LeavingDuty,
                    $"Leave requested. SelectYesno is visible but is not a leave-duty prompt, so ADS is waiting for dialog automation: {selectYesnoPrompt}");
                return;
            }

            if (GameInteractionHelper.TrySelectYesNo(true, Plugin.GameGui, log))
            {
                nextLeaveUiAttemptUtc = now + LeaveUiRetryCooldown;
                LogLeavePromptClicked(selectYesnoPrompt, now);
                SetPhase(ExecutionPhase.LeavingDuty, "Leave requested. Confirmed the duty-exit prompt after final treasure sweep; waiting for the zone-out.");
                return;
            }

            LogLeavePromptClickFailed(selectYesnoPrompt, now);
            SetPhase(ExecutionPhase.LeavingDuty, "Leave requested. Leave-duty prompt was visible, but ADS could not confirm it yet.");
            return;
        }

        if (GameInteractionHelper.IsAddonVisible("SelectYesno"))
        {
            SetPhase(ExecutionPhase.LeavingDuty, "Leave requested. SelectYesno is visible, but ADS could not read the prompt; waiting before duty-exit confirmation.");
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

    private static bool IsLeaveDutyConfirmationPrompt(string prompt)
    {
        var normalized = string.Join(
                ' ',
                prompt.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToLowerInvariant();
        if (normalized.Length == 0)
            return false;

        var asksToLeave = normalized.Contains("leave", StringComparison.Ordinal)
                          || normalized.Contains("exit", StringComparison.Ordinal)
                          || normalized.Contains("abandon", StringComparison.Ordinal);
        var dutyScoped = normalized.Contains("duty", StringComparison.Ordinal)
                         || normalized.Contains("instance", StringComparison.Ordinal)
                         || normalized.Contains("instanced", StringComparison.Ordinal);
        return asksToLeave && dutyScoped;
    }

    private void LogLeavePromptClicked(string prompt, DateTime now)
        => LogLeavePrompt(
            "clicked",
            prompt,
            now,
            static promptText => $"[ADS] Leave SelectYesno prompt recognized and clicked: {promptText}");

    private void LogLeavePromptClickFailed(string prompt, DateTime now)
        => LogLeavePrompt(
            "click-failed",
            prompt,
            now,
            static promptText => $"[ADS] Leave SelectYesno prompt recognized but Yes click failed: {promptText}",
            warning: true);

    private void LogLeavePromptRejected(string prompt, DateTime now)
        => LogLeavePrompt(
            "rejected",
            prompt,
            now,
            static promptText => $"[ADS] SelectYesno visible during ADS leave but rejected as non-leave prompt: {promptText}");

    private void LogLeavePrompt(
        string outcome,
        string prompt,
        DateTime now,
        Func<string, string> buildMessage,
        bool warning = false)
    {
        var logKey = $"{outcome}|{prompt}";
        if (string.Equals(lastLoggedLeavePromptKey, logKey, StringComparison.Ordinal)
            && now - lastLoggedLeavePromptAtUtc < LeavePromptLogCooldown)
        {
            return;
        }

        lastLoggedLeavePromptKey = logKey;
        lastLoggedLeavePromptAtUtc = now;

        var message = buildMessage(prompt);
        if (warning)
            log?.Warning(message);
        else
            log?.Information(message);
    }

    private void LogHigherLowerOwnedMovementHoldOnce()
    {
        if (higherLowerOwnedMovementHoldLogged)
            return;

        higherLowerOwnedMovementHoldLogged = true;
        var mode = CurrentMode == OwnershipMode.Leaving ? "leaving" : "owned";
        log?.Information($"[ADS][HLAUTO] hlauto movement-hold mode={mode} action=preempt-progression");
    }

    private void LogHigherLowerDutyExitHoldOnce()
    {
        if (higherLowerDutyExitHoldLogged)
            return;

        higherLowerDutyExitHoldLogged = true;
        var lastActivityAge = higherLowerLastActivityUtc == DateTime.MinValue
            ? "none"
            : Math.Max(0, (DateTime.UtcNow - higherLowerLastActivityUtc).TotalSeconds).ToString("0.0", CultureInfo.InvariantCulture);
        log?.Information($"[ADS][HLAUTO] hlauto duty-exit-hold mode=leaving lastActivityAge={lastActivityAge}s");
    }

    private void StopMovementAssists(bool preserveTreasureFollowerDoorFollowThrough = false)
    {
        StopNavigationIfNeeded();
        movementTargetGameObjectId = 0;
        mapFlagNavigationActive = false;
        nextNavigationCommandUtc = DateTime.MinValue;
        if (!preserveTreasureFollowerDoorFollowThrough)
        {
            ClearTreasureFollowerDoorFollowThrough(resetStuckTracking: true);
        }
        else
        {
            PauseTreasureFollowerDoorFollowThrough();
        }
        ResetManualDestinationNoProgressTracking(clearStatus: true);
        ResetTreasureDoorJiggleTracking(releaseKeys: true);
    }

    private void PauseMovementAssistsForHigherLower()
    {
        navigationActive = false;
        movementTargetGameObjectId = 0;
        mapFlagNavigationActive = false;
        nextNavigationCommandUtc = DateTime.MinValue;
        PauseTreasureFollowerDoorFollowThrough();
        ResetAquapolisRouteWiggleTracking();
        ResetManualDestinationNoProgressTracking(clearStatus: true);
        ResetTreasureDoorJiggleTracking(releaseKeys: true);
    }

    private void UpdateUnsafeTransitionNavigationStop(DutyContextSnapshot context)
    {
        if (context.IsUnsafeTransition)
        {
            ObserveTreasureFollowerRouteTransit(context, "unsafe transition navigation stop");
            StopNavigationForUnsafeTransition(context);
            return;
        }

        if (!unsafeTransitionNavigationStopLatched)
            return;

        unsafeTransitionNavigationStopLatched = false;
        log?.Information("[ADS] Unsafe transition cleared; objective handling can resume.");
    }

    private void UpdateTreasureFollowerRouteTransitClearStop(DutyContextSnapshot context, PlannerSnapshot planner)
    {
        if (ShouldHoldTreasureFollowerRouteTransit(context, planner))
        {
            ObserveTreasureFollowerRouteTransit(context, "route transit latch");
            treasureFollowerRouteTransitHoldLatched = true;
            if (context.OccupiedInCutSceneEvent || context.WatchingCutscene)
                treasureFollowerRouteTransitCutsceneSeen = true;
            return;
        }

        if (!treasureFollowerRouteTransitHoldLatched)
            return;

        treasureFollowerRouteTransitHoldLatched = false;
        if (TreasureDungeonRole != ADS.Models.TreasureDungeonRole.Follower)
        {
            treasureFollowerRouteTransitCutsceneSeen = false;
            return;
        }

        TrySendCommand("/vnav stop");
        navigationActive = false;
        movementTargetGameObjectId = 0;
        mapFlagNavigationActive = false;
        nextNavigationCommandUtc = DateTime.MinValue;
        ResetTreasureRouteStuckTracking();
        ResetAquapolisRouteWiggleTracking();
        ResetManualDestinationNoProgressTracking(clearStatus: true);
        ResetTreasureDoorJiggleTracking(releaseKeys: true);
        PauseTreasureFollowerDoorFollowThrough();
        dungeonFrontierService.SuppressTreasureFollowerHeadingScoutForPostTransitSettle("route transit hold clear");
        StartTreasureFollowerPostTransitSettle(
            treasureFollowerRouteTransitCutsceneSeen ? "cutscene route transit hold clear" : "route transit hold clear",
            treasureFollowerRouteTransitCutsceneSeen);
        treasureFollowerRouteTransitCutsceneSeen = false;
    }

    private void StartTreasureFollowerPostTransitSettle(string reason, bool cutsceneSeen)
    {
        var now = DateTime.UtcNow;
        var delay = cutsceneSeen ? TreasureFollowerPostCutsceneSettleDelay : TreasureFollowerPostTransitSettleDelay;
        treasureFollowerPostTransitSettleUntilUtc = now + delay;
        treasureFollowerPostTransitSettleReason = reason;
        treasureFollowerPostTransitSettleStopSent = false;
        StopNavigationForTreasureFollowerPostTransitSettle();
        log?.Information(
            $"[ADS] Treasure follower post-transit settle started for {delay.TotalSeconds:0.0}s after {reason}; vnav stopped and follower door follow-through preserved.");
    }

    private bool TryHoldTreasureFollowerPostTransitSettle(string prefix)
    {
        if (treasureFollowerPostTransitSettleUntilUtc == DateTime.MinValue)
            return false;

        var now = DateTime.UtcNow;
        if (now >= treasureFollowerPostTransitSettleUntilUtc)
        {
            var reason = treasureFollowerPostTransitSettleReason;
            ClearTreasureFollowerPostTransitSettle("settle complete");
            log?.Information($"[ADS] Treasure follower post-transit settle ended after {reason}; movement may resume.");
            return false;
        }

        StopNavigationForTreasureFollowerPostTransitSettle();
        var remaining = Math.Max(0, (treasureFollowerPostTransitSettleUntilUtc - now).TotalSeconds);
        SetPhase(
            ExecutionPhase.TransitionHold,
            $"{prefix} Waiting {remaining:0.0}s for treasure follower post-transit settle before sending new movement.");
        return true;
    }

    private bool IsTreasureFollowerPostTransitSettleActive()
        => treasureFollowerPostTransitSettleUntilUtc != DateTime.MinValue
           && DateTime.UtcNow < treasureFollowerPostTransitSettleUntilUtc;

    private void StopNavigationForTreasureFollowerPostTransitSettle()
    {
        if (!treasureFollowerPostTransitSettleStopSent || navigationActive || mapFlagNavigationActive || movementTargetGameObjectId != 0)
            TrySendCommand("/vnav stop");

        treasureFollowerPostTransitSettleStopSent = true;
        navigationActive = false;
        movementTargetGameObjectId = 0;
        mapFlagNavigationActive = false;
        nextNavigationCommandUtc = DateTime.MinValue;
        ResetTreasureRouteStuckTracking();
        ResetAquapolisRouteWiggleTracking();
        ResetManualDestinationNoProgressTracking(clearStatus: true);
        ResetTreasureDoorJiggleTracking(releaseKeys: true);
        PauseTreasureFollowerDoorFollowThrough();
    }

    private void ClearTreasureFollowerPostTransitSettle(string reason)
    {
        if (treasureFollowerPostTransitSettleUntilUtc == DateTime.MinValue)
            return;

        treasureFollowerPostTransitSettleUntilUtc = DateTime.MinValue;
        treasureFollowerPostTransitSettleReason = string.Empty;
        treasureFollowerPostTransitSettleStopSent = false;
        treasureFollowerRouteTransitCutsceneSeen = false;
    }

    private void ResetTreasureDungeonCombatNavigationStopLatchIfClear(DutyContextSnapshot context, PlannerSnapshot planner)
    {
        if (IsTreasureFollowerCombatSignal(context, planner))
            return;

        treasureDungeonCombatStopLatched = false;
    }

    private bool TryHoldTreasureFollowerCombatYield(
        DutyContextSnapshot context,
        PlannerSnapshot planner,
        ObservationSnapshot observation,
        string prefix)
    {
        if (!IsTreasureFollowerCombatSignal(context, planner)
            || !IsTreasureFollowerDuty(context)
            || ShouldBypassCombatHold(context, planner, observation))
        {
            return false;
        }

        ResetRecoveryHold();
        StopMovementAssistsForTreasureFollowerCombatYield(planner);
        SetPhase(ExecutionPhase.CombatHold, BuildCombatHoldStatus(prefix, planner, observation));
        return true;
    }

    private static bool IsTreasureFollowerCombatSignal(DutyContextSnapshot context, PlannerSnapshot planner)
        => context.InCombat || planner.Mode == PlannerMode.Combat;

    private bool IsTreasureFollowerDuty(DutyContextSnapshot context)
        => context.InInstancedDuty
           && TreasureDungeonData.IsSupportedDutyTerritory(context.TerritoryTypeId)
           && EffectiveTreasureDungeonRole == ADS.Models.TreasureDungeonRole.Follower;

    private void StopMovementAssistsForTreasureFollowerCombatYield(PlannerSnapshot planner)
    {
        var recordedFreshMovement = navigationActive || mapFlagNavigationActive || movementTargetGameObjectId != 0;
        var hadPendingDoorFollowThrough = HasPendingTreasureFollowerDoorFollowThrough();
        var shouldSendStop = !treasureDungeonCombatStopLatched || recordedFreshMovement;

        if (shouldSendStop)
            TrySendCommand("/vnav stop");

        navigationActive = false;
        movementTargetGameObjectId = 0;
        mapFlagNavigationActive = false;
        nextNavigationCommandUtc = DateTime.MinValue;
        ClearTreasureFollowerDoorFollowThrough(resetStuckTracking: true, clearCandidateHold: false);
        ResetManualDestinationNoProgressTracking(clearStatus: true);
        ResetTreasureDoorJiggleTracking(releaseKeys: true);
        treasureDungeonCombatStopLatched = true;

        if (shouldSendStop || hadPendingDoorFollowThrough)
        {
            log?.Information(
                $"[ADS] Treasure-dungeon follower combat yield active ({planner.Mode}/{planner.ObjectiveKind}); " +
                "ADS stopped vnav and cleared follower door follow-through so combat owns movement.");
        }
    }

    private void StopNavigationForUnsafeTransition(DutyContextSnapshot context)
    {
        if (unsafeTransitionNavigationStopLatched)
            return;

        TrySendCommand("/vnav stop");
        navigationActive = false;
        movementTargetGameObjectId = 0;
        mapFlagNavigationActive = false;
        nextNavigationCommandUtc = DateTime.MinValue;
        nextInteractAttemptUtc = DateTime.MinValue;
        nextMountedCombatAttemptUtc = DateTime.MinValue;
        lastInteractGameObjectId = 0;
        if (!ShouldPreserveTreasureFollowerDoorFollowThroughForTransit(context))
        {
            ClearTreasureFollowerDoorFollowThrough(resetStuckTracking: true);
        }
        else
        {
            PauseTreasureFollowerDoorFollowThrough();
        }
        ResetManualDestinationNoProgressTracking(clearStatus: true);
        ResetAquapolisRouteWiggleTracking();
        ResetTreasureDoorJiggleTracking(releaseKeys: true);
        unsafeTransitionNavigationStopLatched = true;

        log?.Information(
            "[ADS] Unsafe transition observed ({Flags}); forced /vnav stop before waiting for stable duty truth.",
            FormatUnsafeTransitionFlags(context));
    }

    private void StopNavigationIfNeeded()
    {
        if (!navigationActive)
            return;

        TrySendCommand("/vnav stop");
        navigationActive = false;
    }

    private void StopNavigationForTreasureRouteNudge()
    {
        if (navigationActive)
            TrySendCommand("/vnav stop");

        navigationActive = false;
        movementTargetGameObjectId = 0;
        mapFlagNavigationActive = false;
        nextNavigationCommandUtc = DateTime.MinValue;
    }

    private void StopNavigationForTreasureRouteTransit()
    {
        StopNavigationForTreasureRouteNudge();
        ResetTreasureRouteStuckTracking();
        PauseTreasureFollowerDoorFollowThrough();
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

    private static string FormatUnsafeTransitionFlags(DutyContextSnapshot context)
    {
        var flags = new List<string>();
        if (context.BetweenAreas)
            flags.Add("BetweenAreas");
        if (context.BetweenAreas51)
            flags.Add("BetweenAreas51");

        return flags.Count == 0 ? "none" : string.Join(", ", flags);
    }

    private static double GetRemainingSeconds(DateTime untilUtc)
        => untilUtc == DateTime.MinValue
            ? 0
            : Math.Max(0, (untilUtc - DateTime.UtcNow).TotalSeconds);

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
