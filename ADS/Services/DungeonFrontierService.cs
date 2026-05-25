using System.Globalization;
using System.Numerics;
using ADS.Models;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using Lumina.Excel.Sheets;

namespace ADS.Services;

public sealed class DungeonFrontierService
{
    private const uint PraetoriumTerritoryTypeId = 1044;
    private const float FrontierVisitRadius = 8f;
    private const float FrontierVisitVerticalCap = 12f;
    private const float FrontierBlockingVerticalSanityCap = 100f;
    private const float ManualMapXzDestinationVisitRadius = 1f;
    private const float ManualXyzDestinationVisitRadius = 2.5f;
    private const float HeadingSampleMinDistance = 6f;
    private const float HeadingScoutProjectionDistance = 18f;
    private const float HeadingScoutAdvanceRadius = 4f;
    private const float HeadingScoutMinForwardDot = 0.25f;
    private const float LabelFrontierMinForwardDot = 0.25f;
    private const float LabelFrontierRetargetBacktrackDot = -0.10f;
    private const float TreasureFollowerCandidateVerticalCap = 5f;
    private const float TreasureFollowerStagingArrivalRadius = 3f;
    private const float TreasureFollowerStagingCofferRoomMatchRange = 70f;
    private const float TreasureFollowerStagingProgressDistance = 0.5f;
    private static readonly TimeSpan TreasureFollowerStagingNoProgressTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan TreasureFollowerDoorChaseSettleDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan TreasureFollowerRoomRetryCooldown = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan TreasureFollowerCatchUpLogCooldown = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan TreasureFollowerRetryCycleLogCooldown = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan TreasureFollowerHeadingScoutSuccessSuppressDuration = TimeSpan.FromSeconds(3);

    private enum TreasureFollowerCofferSeekState
    {
        Inactive,
        Latched,
        Seeking,
        Reached,
        NoStagingCofferSeen,
        TimedOut,
        Released,
    }

    private readonly IDataManager dataManager;
    private readonly IObjectTable objectTable;
    private readonly IPluginLog log;
    private readonly ObjectPriorityRuleService objectPriorityRuleService;
    private readonly Dictionary<ulong, IReadOnlyList<DungeonFrontierPoint>> frontierCache = [];
    private readonly Dictionary<ulong, IReadOnlyList<MapLabelMarker>> labelMarkerCache = [];
    private readonly Dictionary<ulong, string> labelMarkerStatusCache = [];
    private readonly HashSet<string> visitedFrontierKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> loggedActiveMapSelections = new(StringComparer.Ordinal);
    private readonly HashSet<string> loggedInvalidMapXzDestinationRules = new(StringComparer.Ordinal);
    private readonly HashSet<string> loggedResolvedMapXzDestinationRules = new(StringComparer.Ordinal);
    private readonly HashSet<string> loggedInvalidXyzDestinationRules = new(StringComparer.Ordinal);
    private readonly HashSet<string> loggedResolvedXyzDestinationRules = new(StringComparer.Ordinal);
    private readonly HashSet<string> loggedCombatBypassManualSelections = new(StringComparer.Ordinal);
    private uint activeDutyKey;
    private Vector3? lastProgressSamplePosition;
    private Vector3? currentHeading;
    private DungeonFrontierPoint? headingScoutTarget;
    private DungeonFrontierPoint? lastValidManualDestination;
    private string? treasureFollowerStartGateTargetKey;
    private string treasureFollowerStartGateTargetName = string.Empty;
    private string treasureFollowerStartGateTargetSource = "None";
    private bool treasureFollowerStartGateTransitionConsumed;
    private string treasureFollowerRouteHoldReason = string.Empty;
    private uint treasureFollowerEntryProofDutyKey;
    private string? heldTreasureFollowerCandidateKey;
    private DungeonFrontierPoint? heldTreasureFollowerCandidatePoint;
    private bool heldTreasureFollowerCandidateTransitObserved;
    private string treasureFollowerLastFailedCandidateKey = string.Empty;
    private string treasureFollowerLastFailedCandidateReason = string.Empty;
    private int treasureFollowerActiveStagingRoomIndex;
    private string? treasureFollowerActiveStagingTargetKey;
    private string treasureFollowerActiveStagingTargetName = string.Empty;
    private Vector3? treasureFollowerActiveStagingTargetPosition;
    private uint treasureFollowerActiveStagingTargetMapId;
    private TreasureFollowerCofferSeekState treasureFollowerCofferSeekState = TreasureFollowerCofferSeekState.Inactive;
    private bool treasureFollowerStagingReached;
    private bool treasureFollowerStagingAttempted;
    private string treasureFollowerCofferSeekLastReason = string.Empty;
    private DateTime treasureFollowerCofferSeekStartedUtc = DateTime.MinValue;
    private DateTime treasureFollowerCofferSeekLastProgressUtc = DateTime.MinValue;
    private float treasureFollowerCofferSeekBestHorizontalDistance = float.MaxValue;
    private int treasureFollowerActiveDoorCycleRoomIndex;
    private int treasureFollowerDoorChaseGateRoomIndex;
    private bool treasureFollowerDoorChaseGateTransitionSeenActive;
    private DateTime treasureFollowerDoorChaseGateSettleUntilUtc = DateTime.MinValue;
    private string treasureFollowerDoorChaseGateState = "Inactive";
    private bool treasureFollowerDoorChaseHoldActive;
    private int treasureFollowerDoorSeekCombatGateRoomIndex;
    private bool treasureFollowerDoorSeekCombatStarted;
    private bool treasureFollowerDoorSeekCombatEnded;
    private DateTime treasureFollowerDoorSeekCombatGateSettleUntilUtc = DateTime.MinValue;
    private int treasureFollowerRoomRetryCooldownRoomIndex;
    private DateTime treasureFollowerRoomRetryCooldownUntilUtc = DateTime.MinValue;
    private int treasureFollowerTransitConsumedRoomIndex;
    private bool heldTreasureFollowerCandidateReached;
    private int highestTreasureFollowerRoomReached;
    private DateTime nextTreasureFollowerCatchUpLogUtc;
    private DateTime nextTreasureFollowerRetryCycleLogUtc;
    private DateTime treasureFollowerSuppressHeadingScoutUntilUtc = DateTime.MinValue;
    private readonly HashSet<string> treasureFollowerFailedPassageGroups = new(StringComparer.Ordinal);
    private readonly HashSet<string> loggedTreasureFollowerBacktrackSkips = new(StringComparer.Ordinal);
    private readonly HashSet<string> loggedTreasureFollowerHeldCandidatePreserves = new(StringComparer.Ordinal);
    private string treasureFollowerLastCofferStagingScanLogKey = string.Empty;
    private int headingScoutSequence;

    private readonly record struct TreasureFollowerRoomRouteFloor(int RoomIndex, Vector3 DoorCenter);

    private readonly record struct TreasureFollowerCofferStagingCandidate(
        ObservedInteractable Coffer,
        int RoomIndex,
        float HorizontalDistance,
        float VerticalDelta);

    private readonly record struct TreasureFollowerCofferStagingScan(
        int Total,
        int MapMatched,
        int MapMismatch,
        int YMismatch,
        int RoomMatchMiss,
        int Suppressed,
        int NextRoomIndex,
        IReadOnlyList<TreasureFollowerCofferStagingCandidate> Accepted,
        string Detail);

    public DungeonFrontierService(
        IDataManager dataManager,
        IObjectTable objectTable,
        IPluginLog log,
        ObjectPriorityRuleService objectPriorityRuleService)
    {
        this.dataManager = dataManager;
        this.objectTable = objectTable;
        this.log = log;
        this.objectPriorityRuleService = objectPriorityRuleService;
    }

    public DungeonFrontierPoint? CurrentTarget { get; private set; }

    public FrontierMode CurrentMode { get; private set; }

    public Vector3? CurrentHeading
        => currentHeading;

    public IReadOnlyList<MapLabelMarker> CurrentLabelMarkers { get; private set; } = [];

    public string CurrentLabelStatus { get; private set; } = "No current map labels loaded.";

    public uint ActiveMapId { get; private set; }

    public string ActiveMapName { get; private set; } = "No current map resolved.";

    public int TotalPoints { get; private set; }

    public int VisitedPoints { get; private set; }

    public int ManualMapXzDestinationCount { get; private set; }

    public int VisitedManualMapXzDestinations { get; private set; }

    public int ManualXyzDestinationCount { get; private set; }

    public int VisitedManualXyzDestinations { get; private set; }

    public bool HasRemainingManualDestinations
        => ManualMapXzDestinationCount > VisitedManualMapXzDestinations
           || ManualXyzDestinationCount > VisitedManualXyzDestinations;

    public DungeonFrontierPoint? LastGhostedManualDestination { get; private set; }

    public DateTime? LastGhostedManualDestinationUtc { get; private set; }

    public string LastGhostedManualDestinationReason { get; private set; } = string.Empty;

    public TreasureDungeonRole TreasureDungeonRole { get; private set; } = ADS.Models.TreasureDungeonRole.MapOpener;

    public string TreasureDungeonRoleSource { get; private set; } = "Default";

    public string TreasureDungeonRoleDetail { get; private set; } = "No external treasure-role source was active; ADS keeps map-opener behavior.";

    public TreasureDungeonRole EffectiveTreasureDungeonRole
        => TreasureFollowerEntryMapOpenerRoleActive
            ? ADS.Models.TreasureDungeonRole.MapOpener
            : TreasureDungeonRole;

    public bool TreasureFollowerEntryMapOpenerRoleActive
        => TreasureDungeonRole == ADS.Models.TreasureDungeonRole.Follower
           && !HasCurrentDutyTreasureFollowerEntryProof();

    public int TreasureFollowerRetryCycle { get; private set; }

    public int LiveTreasureDoorCandidateCount { get; private set; }

    public string CurrentTreasureRouteSource
        => CurrentTarget?.TreasureRouteSource ?? "None";

    public bool TreasureFollowerStartGateActive { get; private set; }

    public string TreasureFollowerStartGateTarget
        => TreasureFollowerStartGateActive ? treasureFollowerStartGateTargetName : string.Empty;

    public string TreasureFollowerStartGateSource
        => TreasureFollowerStartGateActive ? treasureFollowerStartGateTargetSource : "None";

    public string TreasureFollowerStartGateTargetKey
        => TreasureFollowerStartGateActive ? treasureFollowerStartGateTargetKey ?? string.Empty : string.Empty;

    public bool TreasureFollowerStartGateTransitionConsumed
        => treasureFollowerStartGateTransitionConsumed;

    public string TreasureFollowerRouteHoldReason
        => treasureFollowerRouteHoldReason;

    public bool IsTreasureFollowerStartGateTarget(DungeonFrontierPoint point)
        => TreasureFollowerStartGateActive
           && treasureFollowerStartGateTargetKey is not null
           && string.Equals(treasureFollowerStartGateTargetKey, point.Key, StringComparison.Ordinal);

    public string TreasureFollowerHeldCandidateKey
        => heldTreasureFollowerCandidateKey ?? string.Empty;

    public string TreasureFollowerHeldCandidateName
        => heldTreasureFollowerCandidatePoint?.Name ?? string.Empty;

    public bool TreasureFollowerHeldCandidateTransitObserved
        => heldTreasureFollowerCandidateTransitObserved;

    public string TreasureFollowerLastFailedCandidateKey
        => treasureFollowerLastFailedCandidateKey;

    public string TreasureFollowerLastFailedCandidateReason
        => treasureFollowerLastFailedCandidateReason;

    public uint TreasureFollowerEntryProofDutyKey
        => treasureFollowerEntryProofDutyKey;

    public string TreasureFollowerDoorChaseGateState
        => treasureFollowerDoorChaseGateState;

    public int TreasureFollowerDoorChaseGateRoomIndex
        => treasureFollowerDoorChaseGateRoomIndex;

    public bool TreasureFollowerDoorChaseGateTransitionSeenActive
        => treasureFollowerDoorChaseGateTransitionSeenActive;

    public double TreasureFollowerDoorChaseGateSettleRemainingSeconds
        => Math.Max(
            GetRemainingSeconds(treasureFollowerDoorChaseGateSettleUntilUtc),
            GetRemainingSeconds(treasureFollowerDoorSeekCombatGateSettleUntilUtc));

    public bool TreasureFollowerDoorChaseHoldActive
        => treasureFollowerDoorChaseHoldActive;

    public bool TreasureFollowerRouteOwnershipActive
        => IsTreasureFollowerPostEntryDoorCycleEnabled()
           && (CurrentMode == FrontierMode.TreasureDungeon
               || CurrentTarget is { IsTreasureRoutePoint: true }
               || treasureFollowerDoorChaseHoldActive
               || TreasureFollowerCofferSeekActive
               || (heldTreasureFollowerCandidateKey is not null && heldTreasureFollowerCandidateTransitObserved)
               || DateTime.UtcNow < treasureFollowerRoomRetryCooldownUntilUtc);

    public double TreasureFollowerRoomRetryCooldownRemainingSeconds
        => treasureFollowerRoomRetryCooldownUntilUtc == DateTime.MinValue
            ? 0
            : Math.Max(0, (treasureFollowerRoomRetryCooldownUntilUtc - DateTime.UtcNow).TotalSeconds);

    public int TreasureFollowerCofferSeekRoomIndex
        => treasureFollowerActiveStagingRoomIndex;

    public string TreasureFollowerCofferSeekStateName
        => treasureFollowerCofferSeekState.ToString();

    public string TreasureFollowerCofferSeekTargetKey
        => treasureFollowerActiveStagingTargetKey ?? string.Empty;

    public string TreasureFollowerCofferSeekTargetName
        => treasureFollowerActiveStagingTargetName;

    public Vector3? TreasureFollowerCofferSeekTargetPosition
        => treasureFollowerActiveStagingTargetPosition;

    public bool TreasureFollowerCofferSeekReached
        => treasureFollowerStagingReached;

    public bool TreasureFollowerCofferSeekAttempted
        => treasureFollowerStagingAttempted;

    public string TreasureFollowerCofferSeekLastReason
        => treasureFollowerCofferSeekLastReason;

    public bool TreasureFollowerCofferSeekActive
        => IsTreasureFollowerCofferSeekActive();

    public void SetTreasureDungeonRole(TreasureDungeonRoleInference inference)
    {
        var roleChanged = TreasureDungeonRole != inference.Role;
        TreasureDungeonRole = inference.Role;
        TreasureDungeonRoleSource = inference.Source;
        TreasureDungeonRoleDetail = inference.Detail;
        ClearTreasureFollowerCandidateHold("treasure role changed");
        TreasureFollowerRetryCycle = 0;
        if (roleChanged)
        {
            ResetTreasureFollowerProgress();
        }
    }

    public void HoldUnsafeTransition(DutyContextSnapshot context)
    {
        if (!context.PluginEnabled || !context.IsLoggedIn)
        {
            if (activeDutyKey != 0)
            {
                Reset();
                activeDutyKey = 0;
            }

            return;
        }

        var dutyKey = GetDutyKey(context);
        if (dutyKey != 0 && dutyKey != activeDutyKey)
        {
            Reset();
            activeDutyKey = dutyKey;
        }

        var previousTarget = CurrentTarget;
        RememberManualDestination(previousTarget);
        var playerPosition = objectTable.LocalPlayer?.Position;
        ObserveTreasureFollowerCandidateTransit(context);
        ObserveTreasureFollowerStartGateTransit(context, previousTarget);
        ObserveTreasureFollowerEntryProofTransit(context, previousTarget);
        ObserveTreasureFollowerDoorSeekCombatGate(context);
        ObserveTreasureFollowerDoorChaseGateTransition(context);
        ClearTreasureFollowerStartGateIfComplete();
        if (IsTreasureFollowerCofferSeekActive() && IsTreasureFollowerDoorChaseGateTransitionActive(context))
            ReleaseTreasureFollowerCofferSeek(treasureFollowerActiveStagingRoomIndex, "RouteTransitionStarted");
        if (!ShouldPreserveTreasureFollowerCandidateHoldForTransit(context))
            ClearTreasureFollowerCandidateHold("unsafe transition hold");
        CurrentTarget = null;
        CurrentMode = FrontierMode.None;
        CurrentLabelMarkers = [];
        CurrentLabelStatus = "Holding frontier updates during unsafe transition.";
        ActiveMapId = 0;
        ActiveMapName = "Unsafe transition in progress.";
        TotalPoints = 0;
        VisitedPoints = 0;
        LiveTreasureDoorCandidateCount = 0;
        ManualMapXzDestinationCount = 0;
        VisitedManualMapXzDestinations = 0;
        ManualXyzDestinationCount = 0;
        VisitedManualXyzDestinations = 0;

        if (context.BetweenAreas || context.BetweenAreas51)
            GhostCurrentOrLastManualDestination(previousTarget, FormatUnsafeTransitionFlags(context));
    }

    public void Update(DutyContextSnapshot context, ObservationSnapshot observation)
    {
        if (!context.PluginEnabled || !context.IsLoggedIn || !context.InInstancedDuty || context.TerritoryTypeId == 0)
        {
            if (activeDutyKey != 0)
            {
                Reset();
                activeDutyKey = 0;
            }

            return;
        }

        var dutyKey = GetDutyKey(context);
        if (dutyKey != activeDutyKey)
        {
            Reset();
            activeDutyKey = dutyKey;
        }

        var previousTarget = CurrentTarget;
        RememberManualDestination(previousTarget);
        CurrentTarget = null;
        CurrentMode = FrontierMode.None;
        CurrentLabelMarkers = [];
        CurrentLabelStatus = "No current map labels loaded.";
        treasureFollowerRouteHoldReason = string.Empty;
        ActiveMapId = 0;
        ActiveMapName = "No current map resolved.";
        TotalPoints = 0;
        VisitedPoints = 0;
        LiveTreasureDoorCandidateCount = 0;
        ManualMapXzDestinationCount = 0;
        VisitedManualMapXzDestinations = 0;
        ManualXyzDestinationCount = 0;
        VisitedManualXyzDestinations = 0;
        treasureFollowerDoorChaseHoldActive = false;
        if (!IsTreasureFollowerPostEntryDoorCycleEnabled())
            treasureFollowerDoorChaseGateState = "Inactive";

        var playerPosition = objectTable.LocalPlayer?.Position;
        ObserveTreasureFollowerCandidateTransit(context);
        ObserveTreasureFollowerStartGateTransit(context, previousTarget);
        ObserveTreasureFollowerEntryProofTransit(context, previousTarget);
        ObserveTreasureFollowerDoorSeekCombatGate(context);
        ObserveTreasureFollowerDoorChaseGateTransition(context);
        ClearTreasureFollowerStartGateIfComplete();
        if (IsTreasureFollowerCofferSeekActive() && IsTreasureFollowerDoorChaseGateTransitionActive(context))
            ReleaseTreasureFollowerCofferSeek(treasureFollowerActiveStagingRoomIndex, "RouteTransitionStarted");
        if (context.IsUnsafeTransition)
        {
            if (!ShouldPreserveTreasureFollowerCandidateHoldForTransit(context))
                ClearTreasureFollowerCandidateHold("unsafe transition");

            if (context.BetweenAreas || context.BetweenAreas51)
                GhostCurrentOrLastManualDestination(previousTarget, FormatUnsafeTransitionFlags(context));

            return;
        }

        var hasActiveMap = TryResolveActiveMap(context, out var activeMap, out var activeMapStatus);
        CurrentLabelStatus = activeMapStatus;
        if (hasActiveMap)
        {
            ActiveMapId = activeMap.RowId;
            ActiveMapName = BuildMapName(activeMap);
        }

        UpdateHeadingMemory(playerPosition, observation);
        if (hasActiveMap)
        {
            CurrentLabelMarkers = GetLabelMarkers(context.TerritoryTypeId, activeMap.RowId);
            var labelCacheKey = BuildCacheKey(context.TerritoryTypeId, activeMap.RowId);
            if (labelMarkerStatusCache.TryGetValue(labelCacheKey, out var labelStatus))
                CurrentLabelStatus = labelStatus;
        }

        var manualMapXzDestinations = hasActiveMap && playerPosition.HasValue
            ? BuildMapXzDestinationPoints(context, activeMap, playerPosition.Value)
            : [];
        var manualXyzDestinations = hasActiveMap && playerPosition.HasValue
            ? BuildXyzDestinationPoints(context, activeMap, playerPosition.Value)
            : [];
        ManualMapXzDestinationCount = manualMapXzDestinations.Count;
        VisitedManualMapXzDestinations = manualMapXzDestinations.Count(x => visitedFrontierKeys.Contains(x.Key));
        ManualXyzDestinationCount = manualXyzDestinations.Count;
        VisitedManualXyzDestinations = manualXyzDestinations.Count(x => visitedFrontierKeys.Contains(x.Key));
        var manualDestinations = manualMapXzDestinations
            .Concat(manualXyzDestinations)
            .ToList();

        if (TryApplyTreasureFollowerStartGate(
                context,
                hasActiveMap,
                activeMap,
                manualXyzDestinations,
                playerPosition))
        {
            return;
        }

        UpdateTreasureFollowerEntryMapOpenerRole();
        var noFrontierBlockingLiveObjects = HasNoFrontierBlockingLiveObjects(context, observation, playerPosition);
        if (hasActiveMap
            && context.InCombat
            && heldTreasureFollowerCandidateKey is not null
            && IsTreasureFollowerPostEntryDoorCycleEnabled())
        {
            var combatTreasureRoutePoints = BuildTreasureRoutePoints(context, activeMap, observation, manualDestinations, playerPosition);
            ConsumeHeldTreasureFollowerCandidateAfterCombatStarted(context, combatTreasureRoutePoints);
        }
        var manualDestinationCanBeatLiveProgression = ShouldPrioritizeManualDestinationBeforeLiveProgression(
            context,
            observation,
            playerPosition,
            manualDestinations);
        var selectedCombatBypassManualDestination = SelectPraetoriumOnFootCombatBypassManualDestination(
                context,
                observation,
                playerPosition,
                manualDestinations,
                previousTarget)
            ?? SelectCombatBypassManualDestinationAgainstLiveBlockers(
                context,
                observation,
                playerPosition,
                manualDestinations)
            ?? SelectPraetoriumMountedCombatBypassManualDestination(
                context,
                observation,
                playerPosition,
                manualDestinations);
        if (noFrontierBlockingLiveObjects
            || manualDestinationCanBeatLiveProgression
            || selectedCombatBypassManualDestination is not null)
        {
            CurrentTarget = selectedCombatBypassManualDestination
                ?? SelectCurrentManualDestination(manualDestinations, playerPosition);
            if (CurrentTarget is not null)
            {
                CurrentMode = CurrentTarget.IsManualXyzDestination
                    ? FrontierMode.XyzDestination
                    : FrontierMode.MapXzDestination;
                ClearTreasureFollowerCandidateHold("manual destination selected");
                RememberManualDestination(CurrentTarget);
                return;
            }
        }

        var treasureFollowerRouteCanOverrideLiveProgression = IsTreasureFollowerPostEntryDoorCycleEnabled()
                                                            && (!context.InCombat || ShouldEvaluateTreasureFollowerCofferSeekDuringCombat());
        if (hasActiveMap && (noFrontierBlockingLiveObjects || treasureFollowerRouteCanOverrideLiveProgression))
        {
            var treasureRoutePoints = BuildTreasureRoutePoints(context, activeMap, observation, manualDestinations, playerPosition);
            if (TryApplyTreasureRouteTarget(
                    context,
                    treasureRoutePoints,
                    playerPosition,
                    previousTarget))
                return;
        }

        if (hasActiveMap)
        {
            var points = GetFrontierPoints(context.TerritoryTypeId, activeMap.RowId);
            TotalPoints = points.Count;
            if (points.Count > 0)
            {
                headingScoutTarget = null;

                if (playerPosition.HasValue)
                    MarkVisitedPoints(points, playerPosition.Value, FrontierVisitRadius, FrontierVisitVerticalCap);

                VisitedPoints = points.Count(x => visitedFrontierKeys.Contains(x.Key));
                if (!noFrontierBlockingLiveObjects)
                    return;

                CurrentTarget = SelectCurrentTarget(points, playerPosition, previousTarget);
                if (CurrentTarget is not null)
                {
                    ClearTreasureFollowerCandidateHold("label frontier selected");
                    CurrentMode = FrontierMode.Label;
                }

                return;
            }
        }

        if (!playerPosition.HasValue || !noFrontierBlockingLiveObjects)
            return;

        if (IsTreasureFollowerPostEntryDoorCycleEnabled())
        {
            treasureFollowerRouteHoldReason = string.IsNullOrWhiteSpace(treasureFollowerRouteHoldReason)
                ? "NoTreasureFollowerRouteTarget"
                : treasureFollowerRouteHoldReason;
            CurrentLabelStatus = "Treasure follower route did not resolve a live/static passage target; holding instead of heading scout.";
            return;
        }

        CurrentTarget = SelectHeadingScoutTarget(context, playerPosition.Value);
        if (CurrentTarget is not null)
        {
            ClearTreasureFollowerCandidateHold("heading scout selected");
            CurrentMode = FrontierMode.HeadingScout;
        }
    }

    public void Reset()
    {
        visitedFrontierKeys.Clear();
        CurrentTarget = null;
        CurrentMode = FrontierMode.None;
        TotalPoints = 0;
        VisitedPoints = 0;
        ActiveMapId = 0;
        ActiveMapName = "No current map resolved.";
        lastProgressSamplePosition = null;
        currentHeading = null;
        headingScoutTarget = null;
        lastValidManualDestination = null;
        headingScoutSequence = 0;
        CurrentLabelMarkers = [];
        CurrentLabelStatus = "No current map labels loaded.";
        ManualMapXzDestinationCount = 0;
        VisitedManualMapXzDestinations = 0;
        ManualXyzDestinationCount = 0;
        VisitedManualXyzDestinations = 0;
        LastGhostedManualDestination = null;
        LastGhostedManualDestinationUtc = null;
        LastGhostedManualDestinationReason = string.Empty;
        LiveTreasureDoorCandidateCount = 0;
        ResetTreasureFollowerProgress();
        loggedCombatBypassManualSelections.Clear();
    }

    private void ResetTreasureFollowerProgress()
    {
        ClearTreasureFollowerStartGate(resetTransitionConsumed: true);
        treasureFollowerRouteHoldReason = string.Empty;
        treasureFollowerEntryProofDutyKey = 0;
        heldTreasureFollowerCandidateKey = null;
        heldTreasureFollowerCandidatePoint = null;
        heldTreasureFollowerCandidateTransitObserved = false;
        treasureFollowerLastFailedCandidateKey = string.Empty;
        treasureFollowerLastFailedCandidateReason = string.Empty;
        ResetTreasureFollowerStagingState();
        treasureFollowerActiveDoorCycleRoomIndex = 0;
        ResetTreasureFollowerDoorChaseGate();
        ClearTreasureFollowerRoomRetryCooldown();
        treasureFollowerTransitConsumedRoomIndex = 0;
        heldTreasureFollowerCandidateReached = false;
        highestTreasureFollowerRoomReached = 0;
        TreasureFollowerRetryCycle = 0;
        nextTreasureFollowerCatchUpLogUtc = DateTime.MinValue;
        nextTreasureFollowerRetryCycleLogUtc = DateTime.MinValue;
        treasureFollowerSuppressHeadingScoutUntilUtc = DateTime.MinValue;
        treasureFollowerFailedPassageGroups.Clear();
        loggedTreasureFollowerBacktrackSkips.Clear();
        loggedTreasureFollowerHeldCandidatePreserves.Clear();
        treasureFollowerLastCofferStagingScanLogKey = string.Empty;
    }

    private void ActivateTreasureFollowerStartGate(DungeonFrontierPoint point)
    {
        if (!IsEligibleTreasureFollowerStartGateTarget(point))
            return;

        TreasureFollowerStartGateActive = true;
        treasureFollowerStartGateTargetKey = point.Key;
        treasureFollowerStartGateTargetName = point.Name;
        treasureFollowerStartGateTargetSource = point.TreasureRouteSource;
        treasureFollowerStartGateTransitionConsumed = false;
    }

    private void ClearTreasureFollowerStartGateIfComplete()
    {
        if (!TreasureFollowerStartGateActive)
            return;

        if (TreasureDungeonRole != ADS.Models.TreasureDungeonRole.Follower
            || treasureFollowerStartGateTargetKey is null
            || visitedFrontierKeys.Contains(treasureFollowerStartGateTargetKey)
            || HasCurrentDutyTreasureFollowerEntryProof()
            || GetTreasureFollowerReachedFloor() > 0)
        {
            ClearTreasureFollowerStartGate();
        }
    }

    private void ClearTreasureFollowerStartGate(bool resetTransitionConsumed = false)
    {
        TreasureFollowerStartGateActive = false;
        treasureFollowerStartGateTargetKey = null;
        treasureFollowerStartGateTargetName = string.Empty;
        treasureFollowerStartGateTargetSource = "None";
        if (resetTransitionConsumed)
            treasureFollowerStartGateTransitionConsumed = false;
    }

    private bool IsEligibleTreasureFollowerStartGateTarget(DungeonFrontierPoint point)
        => TreasureDungeonRole == ADS.Models.TreasureDungeonRole.Follower
           && GetTreasureFollowerReachedFloor() == 0
           && !HasCurrentDutyTreasureFollowerEntryProof()
           && !visitedFrontierKeys.Contains(point.Key)
           && (point.IsManualXyzDestination || IsStaticTreasureRouteStartPoint(point));

    private static bool IsStaticTreasureRouteStartPoint(DungeonFrontierPoint point)
        => point.IsTreasureRoutePoint
           && point.TreasureRouteIndex == 0
           && !point.IsLiveTreasureDoorCandidate;

    private void UpdateTreasureFollowerEntryMapOpenerRole()
    {
        if (!TreasureFollowerEntryMapOpenerRoleActive)
            return;

        ClearTreasureFollowerCandidateHold("entry effective map-opener role");
    }

    public void HoldTreasureFollowerCandidate(DungeonFrontierPoint point)
    {
        if (!IsTreasureFollowerPostEntryDoorCycleEnabled() || !point.IsTreasurePassageCandidate)
            return;

        var sameHeldCandidate = heldTreasureFollowerCandidatePoint is not null
            && IsSameTreasureFollowerPassageGroup(point, heldTreasureFollowerCandidatePoint);
        heldTreasureFollowerCandidateKey = point.Key;
        heldTreasureFollowerCandidatePoint = point;
        heldTreasureFollowerCandidateTransitObserved = false;
        if (!sameHeldCandidate)
            heldTreasureFollowerCandidateReached = false;
        treasureFollowerActiveDoorCycleRoomIndex = point.TreasureRoomIndex;
        treasureFollowerDoorChaseGateState = "Open";
        treasureFollowerDoorChaseHoldActive = false;
    }

    public void MarkTreasureFollowerCandidateReached(DungeonFrontierPoint point)
    {
        if (!IsTreasureFollowerPostEntryDoorCycleEnabled()
            || !point.IsTreasurePassageCandidate
            || point.TreasureRoomIndex <= 0)
        {
            return;
        }

        HoldTreasureFollowerCandidate(point);
        heldTreasureFollowerCandidateReached = true;
        treasureFollowerActiveDoorCycleRoomIndex = point.TreasureRoomIndex;
        treasureFollowerDoorChaseGateState = "PassageCandidateFollowThroughActive";
        treasureFollowerDoorChaseHoldActive = false;
    }

    public void LatchTreasureFollowerCandidateForRouteTransit(string reason)
    {
        if (!IsTreasureFollowerPostEntryDoorCycleEnabled())
            return;

        var candidate = heldTreasureFollowerCandidatePoint;
        if (candidate is null && CurrentTarget is { IsTreasurePassageCandidate: true } currentTarget)
            candidate = currentTarget;

        if (candidate is null
            || !candidate.IsTreasurePassageCandidate
            || candidate.TreasureRoomIndex <= 0)
        {
            return;
        }

        var sameHeldCandidate = heldTreasureFollowerCandidatePoint is not null
                                && IsSameTreasureFollowerPassageGroup(candidate, heldTreasureFollowerCandidatePoint);
        if (!sameHeldCandidate)
            HoldTreasureFollowerCandidate(candidate);

        var wasTransitObserved = heldTreasureFollowerCandidateTransitObserved;
        heldTreasureFollowerCandidateTransitObserved = true;
        treasureFollowerActiveDoorCycleRoomIndex = candidate.TreasureRoomIndex;
        treasureFollowerDoorChaseGateState = "RouteTransitLatch";
        treasureFollowerDoorChaseHoldActive = true;
        if (!wasTransitObserved)
        {
            log.Information(
                $"[ADS] Treasure follower latched passage candidate {candidate.Name} ({candidate.TreasurePassageGroup}, room {candidate.TreasureRoomIndex}) during route transit after {reason}; ADS will consume this room when transit clears even without door follow-through reach proof.");
        }
    }

    public void ClearTreasureFollowerCandidateHold(string reason)
    {
        if (heldTreasureFollowerCandidateKey is null)
            return;

        heldTreasureFollowerCandidateKey = null;
        heldTreasureFollowerCandidatePoint = null;
        heldTreasureFollowerCandidateTransitObserved = false;
        heldTreasureFollowerCandidateReached = false;
    }

    private bool ShouldPreserveTreasureFollowerCandidateHoldForTransit(DutyContextSnapshot context)
        => IsTreasureFollowerPostEntryDoorCycleEnabled()
           && context.IsTreasureRouteTransitHold
           && heldTreasureFollowerCandidateKey is not null;

    private void ObserveTreasureFollowerCandidateTransit(DutyContextSnapshot context)
    {
        if (!IsTreasureFollowerPostEntryDoorCycleEnabled()
            || !context.IsTreasureRouteTransitHold
            || heldTreasureFollowerCandidateKey is null)
        {
            return;
        }

        heldTreasureFollowerCandidateTransitObserved = true;
    }

    private void ObserveTreasureFollowerStartGateTransit(DutyContextSnapshot context, DungeonFrontierPoint? previousTarget)
    {
        if (!context.IsTreasureRouteTransitHold
            || !TreasureFollowerStartGateActive
            || treasureFollowerStartGateTargetKey is null
            || treasureFollowerStartGateTransitionConsumed)
        {
            return;
        }

        var consumedTargetName = treasureFollowerStartGateTargetName;
        var consumedTargetSource = treasureFollowerStartGateTargetSource;
        var consumedTargetKey = treasureFollowerStartGateTargetKey;
        if (previousTarget is not null
            && !string.Equals(previousTarget.Key, consumedTargetKey, StringComparison.Ordinal))
        {
            return;
        }

        visitedFrontierKeys.Add(consumedTargetKey);
        treasureFollowerStartGateTransitionConsumed = true;
        treasureFollowerEntryProofDutyKey = activeDutyKey;
        ClearTreasureFollowerCandidateHold("start gate transition consumed");
        ResetTreasureFollowerDoorChaseGate();
        ClearTreasureFollowerRoomRetryCooldown();
        ClearTreasureFollowerStartGate();
        log.Information(
            $"[ADS] Treasure follower start gate consumed during route transit ({FormatTreasureRouteTransitFlags(context)}): {consumedTargetName} ({consumedTargetSource}, key {consumedTargetKey}). ADS marked entry proof for duty {activeDutyKey} and will not retarget the entry/start point after transition.");
    }

    private void ObserveTreasureFollowerEntryProofTransit(DutyContextSnapshot context, DungeonFrontierPoint? previousTarget)
    {
        if (!context.IsTreasureRouteTransitHold || previousTarget is null)
            return;

        TryEstablishTreasureFollowerEntryProof(previousTarget, $"route transit ({FormatTreasureRouteTransitFlags(context)})");
    }

    private void ObserveTreasureFollowerDoorChaseGateTransition(DutyContextSnapshot context)
    {
        var now = DateTime.UtcNow;
        if (!IsTreasureFollowerPostEntryDoorCycleEnabled()
            || !IsTreasureFollowerDoorOpenTransitionGateArmed(now)
            || !IsTreasureFollowerDoorChaseGateTransitionActive(context))
        {
            return;
        }

        treasureFollowerDoorChaseGateTransitionSeenActive = true;
        treasureFollowerDoorChaseGateSettleUntilUtc = DateTime.MinValue;
        if (!treasureFollowerDoorChaseHoldActive)
            treasureFollowerDoorChaseGateState = "WaitingForDoorOpenTransitionEnd";
    }

    private static bool IsTreasureFollowerDoorChaseGateTransitionActive(DutyContextSnapshot context)
        => context.Occupied33
           || context.OccupiedInCutSceneEvent
           || context.WatchingCutscene
           || context.BetweenAreas
           || context.BetweenAreas51;

    private void ObserveTreasureFollowerDoorSeekCombatGate(DutyContextSnapshot context)
    {
        if (!IsTreasureFollowerPostEntryDoorCycleEnabled())
            return;

        var roomIndex = treasureFollowerActiveDoorCycleRoomIndex > 0
            ? treasureFollowerActiveDoorCycleRoomIndex
            : treasureFollowerDoorSeekCombatGateRoomIndex > 0
                ? treasureFollowerDoorSeekCombatGateRoomIndex
                : context.InCombat
                    ? GetTreasureFollowerReachedFloor() + 1
                    : 0;
        if (roomIndex <= 0)
            return;

        ObserveTreasureFollowerDoorSeekCombatGate(context, roomIndex, DateTime.UtcNow);
    }

    private void ObserveTreasureFollowerDoorSeekCombatGate(
        DutyContextSnapshot context,
        int roomIndex,
        DateTime now)
    {
        if (!IsTreasureFollowerPostEntryDoorCycleEnabled() || roomIndex <= 0)
            return;

        EnsureTreasureFollowerDoorSeekCombatGateRoom(roomIndex);
        if (context.InCombat)
        {
            treasureFollowerDoorSeekCombatStarted = true;
            treasureFollowerDoorSeekCombatEnded = false;
            treasureFollowerDoorSeekCombatGateSettleUntilUtc = DateTime.MinValue;
            return;
        }

        if (!treasureFollowerDoorSeekCombatStarted || treasureFollowerDoorSeekCombatEnded)
            return;

        treasureFollowerDoorSeekCombatEnded = true;
        treasureFollowerDoorSeekCombatGateSettleUntilUtc = now + TreasureFollowerDoorChaseSettleDelay;
    }

    private void ConsumeHeldTreasureFollowerCandidateAfterObservedTransit(IReadOnlyList<DungeonFrontierPoint> points)
    {
        if (!IsTreasureFollowerPostEntryDoorCycleEnabled() || !heldTreasureFollowerCandidateTransitObserved)
            return;

        var consumedCandidate = heldTreasureFollowerCandidatePoint
            ?? GetHeldTreasureFollowerCandidate(points);
        if (consumedCandidate is null)
            return;

        var consumedRoomIndex = consumedCandidate.TreasureRoomIndex;
        if (consumedRoomIndex <= 0)
        {
            ClearTreasureFollowerCandidateHold("held candidate consumed after transit had no treasure room");
            return;
        }

        var markedCount = CompleteTreasureFollowerSameRoomCandidates(
            consumedCandidate,
            points,
            preserveTransitionSeenActive: true);
        log.Information(
            $"[ADS] Treasure follower consumed passage candidate {consumedCandidate.Name} ({consumedCandidate.TreasurePassageGroup}, room {consumedRoomIndex}) after route transit latch; marked {markedCount} same-room live/static passage candidate(s) visited so ADS will not retarget old-room doors.");
        ClearTreasureFollowerCandidateHold("held candidate consumed after route transit latch");
    }

    private void ConsumeHeldTreasureFollowerCandidateAfterCombatStarted(
        DutyContextSnapshot context,
        IReadOnlyList<DungeonFrontierPoint> points)
    {
        if (!IsTreasureFollowerPostEntryDoorCycleEnabled()
            || !context.InCombat
            || heldTreasureFollowerCandidateKey is null)
        {
            return;
        }

        var consumedCandidate = heldTreasureFollowerCandidatePoint
            ?? GetHeldTreasureFollowerCandidate(points);
        if (consumedCandidate is null || consumedCandidate.TreasureRoomIndex <= 0)
            return;

        if (!heldTreasureFollowerCandidateReached)
        {
            treasureFollowerDoorChaseGateState = "HoldingCombatBeforeCandidateReach";
            treasureFollowerDoorChaseHoldActive = true;
            return;
        }

        var markedCount = CompleteTreasureFollowerSameRoomCandidates(
            consumedCandidate,
            points,
            preserveTransitionSeenActive: treasureFollowerDoorChaseGateTransitionSeenActive);
        log.Information(
            $"[ADS] Treasure follower consumed passage candidate {consumedCandidate.Name} ({consumedCandidate.TreasurePassageGroup}, room {consumedCandidate.TreasureRoomIndex}) after combat started during follower door cycle; marked {markedCount} same-room live/static passage candidate(s) visited so ADS will not retarget old-room doors.");
        ClearTreasureFollowerCandidateHold("held candidate consumed after combat started");
    }

    private int CompleteTreasureFollowerSameRoomCandidates(
        DungeonFrontierPoint consumedCandidate,
        IReadOnlyList<DungeonFrontierPoint> points,
        bool preserveTransitionSeenActive)
    {
        var roomIndex = consumedCandidate.TreasureRoomIndex;
        var markedCount = 0;
        foreach (var point in points.Where(point => point.IsTreasurePassageCandidate && point.TreasureRoomIndex == roomIndex))
        {
            if (visitedFrontierKeys.Add(point.Key))
                markedCount++;
        }

        if (visitedFrontierKeys.Add(consumedCandidate.Key))
            markedCount++;

        treasureFollowerTransitConsumedRoomIndex = Math.Max(treasureFollowerTransitConsumedRoomIndex, roomIndex);
        AdvanceTreasureFollowerRoomReached(roomIndex);
        ClearTreasureFollowerFailedPassageGroups(roomIndex);
        ClearTreasureFollowerActiveDoorCycleRoom(roomIndex);
        ResetTreasureFollowerDoorChaseGate(preserveTransitionSeenActive);
        ClearTreasureFollowerRoomRetryCooldown();
        SuppressTreasureFollowerHeadingScoutAfterSuccess();
        return markedCount;
    }

    public void ConsumeTreasureFollowerCandidate(DungeonFrontierPoint point, string reason, string detail)
    {
        if (!IsTreasureFollowerPostEntryDoorCycleEnabled()
            || !point.IsTreasurePassageCandidate
            || point.TreasureRoomIndex <= 0)
        {
            return;
        }

        if (visitedFrontierKeys.Contains(point.Key)
            && treasureFollowerTransitConsumedRoomIndex >= point.TreasureRoomIndex)
        {
            return;
        }

        visitedFrontierKeys.Add(point.Key);
        treasureFollowerTransitConsumedRoomIndex = Math.Max(treasureFollowerTransitConsumedRoomIndex, point.TreasureRoomIndex);
        AdvanceTreasureFollowerRoomReached(point.TreasureRoomIndex);
        ClearTreasureFollowerFailedPassageGroups(point.TreasureRoomIndex);
        ClearTreasureFollowerActiveDoorCycleRoom(point.TreasureRoomIndex);
        ResetTreasureFollowerDoorChaseGate(preserveTransitionSeenActive: treasureFollowerDoorChaseGateTransitionSeenActive);
        ClearTreasureFollowerRoomRetryCooldown();
        SuppressTreasureFollowerHeadingScoutAfterSuccess();
        ClearTreasureFollowerCandidateHold($"candidate consumed after {reason}");
        log.Information(
            $"[ADS] Treasure follower consumed passage candidate {point.Name} ({point.TreasurePassageGroup}, room {point.TreasureRoomIndex}) after {reason}; ADS will not retarget this room. {detail}");
    }

    public void MarkVisited(DungeonFrontierPoint point, Vector3 playerPosition)
    {
        if (TreasureDungeonRole == ADS.Models.TreasureDungeonRole.Follower
            && point.IsTreasureFollowerStagingPoint)
        {
            MarkTreasureFollowerCofferSeekReached(point, playerPosition, "FrontierArrival");
            return;
        }

        if (TryEstablishTreasureFollowerEntryProof(point, "route point reached"))
            return;

        if (!visitedFrontierKeys.Add(point.Key))
            return;

        ClearTreasureFollowerStartGateIfComplete();
        ClearRememberedManualDestination(point);
        if (IsHeldTreasureFollowerCandidate(point))
            ClearTreasureFollowerCandidateHold("held candidate visited");

        if (IsTreasureFollowerPostEntryDoorCycleEnabled() && point.IsTreasurePassageCandidate)
        {
            log.Information(
                $"[ADS] Treasure follower reached passage candidate {point.Name} ({point.TreasurePassageGroup}, room {point.TreasureRoomIndex}) without a transition on this tick; ADS will try another current-room candidate.");
        }

        if (point.IsManualDestination)
        {
            var ghostReason = point.IsManualXyzDestination ? "ReachedXyzArrival" : "ReachedXzArrival";
            RememberGhostedManualDestination(point, ghostReason);
            var reachText = point.IsManualXyzDestination
                ? $"after reaching 3D {Vector3.Distance(playerPosition, point.Position):0.0}y"
                : $"after reaching XZ {GetHorizontalDistance(playerPosition, point.Position):0.0}y";
            log.Information($"[ADS] Ghosted {GetManualDestinationLabel(point)} {point.Name} at {FormatVector(point.Position)} {reachText}.");
        }
    }

    public void MarkTreasureFollowerCandidateFailed(DungeonFrontierPoint point, string reason, string detail)
    {
        if (!IsTreasureFollowerPostEntryDoorCycleEnabled() || !point.IsTreasurePassageCandidate)
            return;

        var markedVisited = visitedFrontierKeys.Add(point.Key);
        var markedFailedGroup = MarkTreasureFollowerPassageGroupFailed(point);
        if (IsHeldTreasureFollowerCandidate(point))
            ClearTreasureFollowerCandidateHold($"held candidate failed after {reason}");

        if (!markedVisited && !markedFailedGroup)
            return;

        treasureFollowerLastFailedCandidateKey = point.Key;
        treasureFollowerLastFailedCandidateReason = reason;
        log.Information(
            $"[ADS] Treasure follower marked passage group {point.TreasurePassageGroup} candidate {point.Name} (room {point.TreasureRoomIndex}) failed after {reason}; ADS will try the next same-room candidate before room retry cooldown. {detail}");
    }

    public void RetireManualDestination(DungeonFrontierPoint point, string reason, string detail)
    {
        if (!visitedFrontierKeys.Add(point.Key))
            return;

        ClearRememberedManualDestination(point);
        if (!point.IsManualDestination)
            return;

        RememberGhostedManualDestination(point, reason);
        log.Information($"[ADS] Ghosted {GetManualDestinationLabel(point)} {point.Name} at {FormatVector(point.Position)} {detail}.");
    }

    public DungeonFrontierPoint? GetCurrentOrRememberedManualDestination(Vector3? playerPosition)
    {
        var point = CurrentTarget is { IsManualDestination: true }
            ? CurrentTarget
            : lastValidManualDestination;
        if (point is null)
            return null;

        if (visitedFrontierKeys.Contains(point.Key))
        {
            ClearRememberedManualDestination(point);
            return null;
        }

        return BuildNavigationPoint(point, playerPosition);
    }

    public DungeonFrontierPoint? GetCurrentOrRememberedManualMapXzDestination(Vector3? playerPosition)
        => GetCurrentOrRememberedManualDestination(playerPosition);

    public DungeonFrontierPoint? FindNearbyUnvisitedManualDestination(
        DutyContextSnapshot context,
        Vector3 playerPosition,
        Vector3 anchorPosition,
        float radius,
        bool requirePlayerNear = false)
    {
        if (!context.PluginEnabled || !context.IsLoggedIn || !context.InInstancedDuty || context.TerritoryTypeId == 0)
            return null;

        if (!TryResolveActiveMap(context, out var activeMap, out _))
            return null;

        var manualMapXzDestinations = BuildMapXzDestinationPoints(context, activeMap, playerPosition);
        var manualXyzDestinations = BuildXyzDestinationPoints(context, activeMap, playerPosition);
        return manualMapXzDestinations
            .Concat(manualXyzDestinations)
            .Where(point => !visitedFrontierKeys.Contains(point.Key))
            .Select(point => new
            {
                Point = point,
                AnchorDistance = GetManualDestinationDistance(anchorPosition, point),
                PlayerDistance = GetManualDestinationDistance(playerPosition, point),
            })
            .Where(x => x.AnchorDistance <= radius && (!requirePlayerNear || x.PlayerDistance <= radius))
            .OrderBy(x => x.Point.Priority)
            .ThenBy(x => x.AnchorDistance)
            .ThenBy(x => x.PlayerDistance)
            .ThenBy(x => x.Point.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Point)
            .FirstOrDefault();
    }

    public DungeonFrontierPoint? FindNextForceMarchManualDestinationAfter(
        DutyContextSnapshot context,
        Vector3 playerPosition,
        DungeonFrontierPoint completedPoint)
    {
        if (!completedPoint.IsManualDestination)
            return null;

        if (!context.PluginEnabled || !context.IsLoggedIn || !context.InInstancedDuty || context.TerritoryTypeId == 0)
            return null;

        if (!TryResolveActiveMap(context, out var activeMap, out _))
            return null;

        var manualDestinations = BuildMapXzDestinationPoints(context, activeMap, playerPosition)
            .Concat(BuildXyzDestinationPoints(context, activeMap, playerPosition))
            .ToList();
        if (manualDestinations.Count == 0)
            return null;

        var anchorIndex = manualDestinations.FindIndex(point => string.Equals(point.Key, completedPoint.Key, StringComparison.Ordinal));
        if (anchorIndex < 0)
            return null;

        var nextUnvisitedManualDestination = manualDestinations
            .Skip(anchorIndex + 1)
            .FirstOrDefault(point => !visitedFrontierKeys.Contains(point.Key));
        return nextUnvisitedManualDestination is { AllowCombatBypass: true } nextForceMarchDestination
            ? BuildNavigationPoint(nextForceMarchDestination, playerPosition)
            : null;
    }

    private IReadOnlyList<DungeonFrontierPoint> GetFrontierPoints(uint territoryTypeId, uint mapId)
    {
        var cacheKey = BuildCacheKey(territoryTypeId, mapId);
        if (frontierCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var built = BuildFrontierPoints(territoryTypeId, mapId);
        frontierCache[cacheKey] = built;
        return built;
    }

    private IReadOnlyList<MapLabelMarker> GetLabelMarkers(uint territoryTypeId, uint mapId)
    {
        var cacheKey = BuildCacheKey(territoryTypeId, mapId);
        if (labelMarkerCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var built = BuildLabelMarkers(territoryTypeId, mapId);
        labelMarkerCache[cacheKey] = built;
        return built;
    }

    private IReadOnlyList<DungeonFrontierPoint> BuildMapXzDestinationPoints(DutyContextSnapshot context, Map map, Vector3 playerPosition)
    {
        var destinationRules = objectPriorityRuleService.GetMapXzDestinationRules(context);
        if (destinationRules.Count == 0)
            return [];

        var points = new List<DungeonFrontierPoint>();
        foreach (var rule in destinationRules)
        {
            var ruleKey = BuildMapXzDestinationRuleKey(context, rule);
            if (!DoesManualDestinationRuleMatchActiveMap(rule, map))
                continue;

            if (!TryParseMapCoordinates(rule.MapCoordinates, out var mapCoordinates))
            {
                LogMapXzDestinationWarning(
                    $"bad-coordinates:{ruleKey}",
                    $"[ADS] Ignoring Map XZ destination rule {rule.ObjectName}: could not parse mapCoordinates '{rule.MapCoordinates}'. Use a value like 11.3,10.4.");
                continue;
            }

            var name = string.IsNullOrWhiteSpace(rule.ObjectName)
                ? $"Map XZ {mapCoordinates.X:0.0}, {mapCoordinates.Y:0.0}"
                : rule.ObjectName;
            var worldPosition = ConvertMapCoordinatesToWorld(mapCoordinates, map, playerPosition.Y);
            if (!objectPriorityRuleService.DestinationRulePassesDistanceGates(rule, playerPosition, worldPosition))
                continue;

            if (loggedResolvedMapXzDestinationRules.Add(ruleKey))
            {
                log.Information(
                    $"[ADS] Resolved Map XZ destination {name} ({mapCoordinates.X:0.0}, {mapCoordinates.Y:0.0}) on map row {map.RowId} to world {FormatVector(worldPosition)} using sizeFactor {map.SizeFactor} and offsets {map.OffsetX},{map.OffsetY}.");
            }

            points.Add(new DungeonFrontierPoint
            {
                Key = $"{ruleKey}:{map.RowId}:{mapCoordinates.X:0.###},{mapCoordinates.Y:0.###}",
                Name = name,
                Position = worldPosition,
                LevelRowId = 0,
                MapId = map.RowId,
                Priority = rule.Priority,
                MapCoordinates = mapCoordinates,
                UsePlayerYForNavigation = true,
                ManualDestinationKind = ManualDestinationKind.MapXz,
                AllowCombatBypass = AllowsCombatBypass(rule),
                ArrivalRadiusXz = ManualMapXzDestinationVisitRadius,
            });
        }

        return points;
    }

    private IReadOnlyList<DungeonFrontierPoint> BuildXyzDestinationPoints(DutyContextSnapshot context, Map map, Vector3 playerPosition)
    {
        var destinationRules = objectPriorityRuleService.GetXyzDestinationRules(context);
        if (destinationRules.Count == 0)
            return [];

        var points = new List<DungeonFrontierPoint>();
        foreach (var rule in destinationRules)
        {
            var ruleKey = BuildXyzDestinationRuleKey(context, rule);
            if (!DoesManualDestinationRuleMatchActiveMap(rule, map))
                continue;

            if (!TryParseWorldCoordinates(rule.WorldCoordinates, out var worldCoordinates))
            {
                LogXyzDestinationWarning(
                    $"bad-coordinates:{ruleKey}",
                    $"[ADS] Ignoring XYZ destination rule {rule.ObjectName}: could not parse worldCoordinates '{rule.WorldCoordinates}'. Use a value like 154.1,101.9,-34.2.");
                continue;
            }

            var name = string.IsNullOrWhiteSpace(rule.ObjectName)
                ? $"XYZ {worldCoordinates.X:0.0}, {worldCoordinates.Y:0.0}, {worldCoordinates.Z:0.0}"
                : rule.ObjectName;
            if (!objectPriorityRuleService.DestinationRulePassesDistanceGates(rule, playerPosition, worldCoordinates))
                continue;

            if (loggedResolvedXyzDestinationRules.Add(ruleKey))
            {
                log.Information(
                    $"[ADS] Resolved XYZ destination {name} on map row {map.RowId} to world {FormatVector(worldCoordinates)}.");
            }

            var allowCombatBypass = AllowsCombatBypass(rule);
            points.Add(new DungeonFrontierPoint
            {
                Key = $"{ruleKey}:{map.RowId}:{worldCoordinates.X:0.###},{worldCoordinates.Y:0.###},{worldCoordinates.Z:0.###}",
                Name = name,
                Position = worldCoordinates,
                LevelRowId = 0,
                MapId = map.RowId,
                Priority = rule.Priority,
                ManualDestinationKind = ManualDestinationKind.Xyz,
                AllowCombatBypass = allowCombatBypass,
                ArrivalRadius3d = allowCombatBypass
                    ? ManualMapXzDestinationVisitRadius
                    : ManualXyzDestinationVisitRadius,
            });
        }

        return points;
    }

    private IReadOnlyList<DungeonFrontierPoint> BuildTreasureRoutePoints(
        DutyContextSnapshot context,
        Map map,
        ObservationSnapshot observation,
        IReadOnlyList<DungeonFrontierPoint> manualDestinations,
        Vector3? playerPosition)
    {
        if (!TreasureDungeonData.HasRoute(context.TerritoryTypeId))
        {
            LiveTreasureDoorCandidateCount = 0;
            return [];
        }

        if (manualDestinations.Any(point => !visitedFrontierKeys.Contains(point.Key)))
        {
            LiveTreasureDoorCandidateCount = 0;
            return [];
        }

        var staticRoutePoints = TreasureDungeonData.BuildRoutePoints(context.TerritoryTypeId, map.RowId);
        var liveDoorCandidates = EffectiveTreasureDungeonRole == ADS.Models.TreasureDungeonRole.Follower
            ? BuildLiveTreasureDoorRoutePoints(context, map, observation, staticRoutePoints, playerPosition)
            : [];
        var followerStagingPoints = EffectiveTreasureDungeonRole == ADS.Models.TreasureDungeonRole.Follower
            ? BuildTreasureFollowerStagingRoutePoints(context, map, observation, staticRoutePoints)
            : [];
        LiveTreasureDoorCandidateCount = liveDoorCandidates.Count;

        if (staticRoutePoints.Count == 0)
            return liveDoorCandidates.Concat(followerStagingPoints).ToList();

        var routePoints = liveDoorCandidates
            .Concat(staticRoutePoints)
            .Concat(followerStagingPoints)
            .OrderBy(point => point.TreasureRouteIndex < 0 ? int.MaxValue : point.TreasureRouteIndex)
            .ThenBy(point => point.IsLiveTreasureDoorCandidate ? 0 : 1)
            .ThenBy(point => point.IsTreasureFollowerStagingPoint ? 0 : 1)
            .ThenBy(point => point.Priority)
            .ThenBy(point => point.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (playerPosition.HasValue)
            MarkTreasureFollowerEntryCatchUp(routePoints, playerPosition.Value);

        return routePoints;
    }

    private IReadOnlyList<DungeonFrontierPoint> BuildTreasureFollowerStagingRoutePoints(
        DutyContextSnapshot context,
        Map map,
        ObservationSnapshot observation,
        IReadOnlyList<DungeonFrontierPoint> staticRoutePoints)
    {
        if (!IsTreasureFollowerPostEntryDoorCycleEnabled())
            return [];

        if (staticRoutePoints.Count == 0)
        {
            LogTreasureFollowerCofferStagingScan(
                map,
                new TreasureFollowerCofferStagingScan(0, 0, 0, 0, 0, 0, 0, [], "suppression=static route unavailable"));
            return [];
        }

        var roomFloors = BuildTreasureFollowerRoomRouteFloors(staticRoutePoints);
        if (roomFloors.Count == 0)
        {
            LogTreasureFollowerCofferStagingScan(
                map,
                new TreasureFollowerCofferStagingScan(0, 0, 0, 0, 0, 0, 0, [], "suppression=no passage-door room floors"));
            return [];
        }

        var nextRoomIndex = treasureFollowerActiveDoorCycleRoomIndex > 0
                            && !IsTreasureFollowerRoomDisallowed(treasureFollowerActiveDoorCycleRoomIndex)
            ? treasureFollowerActiveDoorCycleRoomIndex
            : roomFloors
                .Select(room => room.RoomIndex)
                .Where(roomIndex => !IsTreasureFollowerRoomDisallowed(roomIndex))
                .OrderBy(roomIndex => roomIndex)
                .FirstOrDefault();
        var scan = ScanTreasureFollowerStagingCoffers(map, observation, roomFloors, nextRoomIndex);
        LogTreasureFollowerCofferStagingScan(map, scan);

        var routePoints = scan.Accepted
            .Select(candidate => BuildTreasureFollowerCofferStagingPoint(context, map, candidate.RoomIndex, candidate.Coffer))
            .ToList();
        if (TryBuildTreasureFollowerLatchedCofferStagingPoint(context, map, out var latchedPoint)
            && routePoints.All(point => !string.Equals(point.Key, latchedPoint.Key, StringComparison.Ordinal)))
        {
            routePoints.Insert(0, latchedPoint);
        }

        return routePoints;
    }

    private static IReadOnlyList<TreasureFollowerRoomRouteFloor> BuildTreasureFollowerRoomRouteFloors(
        IReadOnlyList<DungeonFrontierPoint> staticRoutePoints)
    {
        return staticRoutePoints
            .Where(point => point.IsTreasurePassageCandidate)
            .GroupBy(point => point.TreasureRoomIndex)
            .OrderBy(group => group.Key)
            .Select(group => new TreasureFollowerRoomRouteFloor(group.Key, AveragePosition(group.Select(point => point.Position))))
            .ToList();
    }

    private TreasureFollowerCofferStagingScan ScanTreasureFollowerStagingCoffers(
        Map map,
        ObservationSnapshot observation,
        IReadOnlyList<TreasureFollowerRoomRouteFloor> roomFloors,
        int nextRoomIndex)
    {
        var coffers = observation.LiveInteractables
            .Where(interactable => interactable.Classification == InteractableClass.TreasureCoffer)
            .ToList();
        var mapMatched = 0;
        var mapMismatch = 0;
        var yMismatch = 0;
        var roomMatchMiss = 0;
        var suppressed = 0;
        var accepted = new List<TreasureFollowerCofferStagingCandidate>();
        var details = new List<string>();

        foreach (var coffer in coffers)
        {
            if (!MatchesCurrentMap(map.RowId, coffer.MapId))
            {
                mapMismatch++;
                details.Add($"{EscapeLogText(coffer.Name)} map mismatch active={map.RowId} candidate={coffer.MapId}");
                continue;
            }

            mapMatched++;
            var nearestFloor = roomFloors
                .Select(room => new
                {
                    Room = room,
                    HorizontalDistance = GetHorizontalDistance(room.DoorCenter, coffer.Position),
                    VerticalDelta = MathF.Abs(room.DoorCenter.Y - coffer.Position.Y),
                })
                .OrderBy(x => x.VerticalDelta <= TreasureFollowerCandidateVerticalCap ? 0 : 1)
                .ThenBy(x => x.HorizontalDistance)
                .ThenBy(x => x.VerticalDelta)
                .ThenBy(x => x.Room.RoomIndex)
                .FirstOrDefault();
            if (nearestFloor is null)
            {
                suppressed++;
                details.Add($"{EscapeLogText(coffer.Name)} suppressed because no room floor metadata exists");
                continue;
            }

            if (nearestFloor.VerticalDelta > TreasureFollowerCandidateVerticalCap)
            {
                yMismatch++;
                details.Add($"{EscapeLogText(coffer.Name)} Y mismatch nearestRoom={nearestFloor.Room.RoomIndex} y={nearestFloor.VerticalDelta:0.0}");
                continue;
            }

            if (nearestFloor.HorizontalDistance > TreasureFollowerStagingCofferRoomMatchRange)
            {
                roomMatchMiss++;
                details.Add($"{EscapeLogText(coffer.Name)} room match miss nearestRoom={nearestFloor.Room.RoomIndex} xz={nearestFloor.HorizontalDistance:0.0}");
                continue;
            }

            if (IsTreasureFollowerRoomDisallowed(nearestFloor.Room.RoomIndex))
            {
                suppressed++;
                details.Add($"{EscapeLogText(coffer.Name)} suppressed oldRoom={nearestFloor.Room.RoomIndex} reached={GetTreasureFollowerReachedFloor()} consumed={treasureFollowerTransitConsumedRoomIndex}");
                continue;
            }

            accepted.Add(new TreasureFollowerCofferStagingCandidate(
                coffer,
                nearestFloor.Room.RoomIndex,
                nearestFloor.HorizontalDistance,
                nearestFloor.VerticalDelta));
        }

        accepted = accepted
            .OrderBy(candidate => candidate.RoomIndex == nextRoomIndex ? 0 : 1)
            .ThenBy(candidate => candidate.RoomIndex)
            .ThenBy(candidate => candidate.HorizontalDistance)
            .ThenBy(candidate => candidate.VerticalDelta)
            .ThenBy(candidate => candidate.Coffer.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var detail = details.Count == 0
            ? "rejections=none"
            : $"rejections={string.Join("; ", details.Take(4))}{(details.Count > 4 ? $"; +{details.Count - 4} more" : string.Empty)}";
        if (accepted.Count == 0 && coffers.Count == 0)
            detail = "hold=no live Treasure Coffer visible";
        else if (accepted.Count == 0)
            detail = $"hold=no usable live Treasure Coffer for room {nextRoomIndex}; {detail}";

        return new TreasureFollowerCofferStagingScan(
            coffers.Count,
            mapMatched,
            mapMismatch,
            yMismatch,
            roomMatchMiss,
            suppressed,
            nextRoomIndex,
            accepted,
            detail);
    }

    private void LogTreasureFollowerCofferStagingScan(Map map, TreasureFollowerCofferStagingScan scan)
    {
        var acceptedSummary = scan.Accepted.Count == 0
            ? "none"
            : string.Join(
                "; ",
                scan.Accepted
                    .Take(3)
                    .Select(candidate => $"{EscapeLogText(candidate.Coffer.Name)}->room{candidate.RoomIndex}/xz{candidate.HorizontalDistance:0.0}/y{candidate.VerticalDelta:0.0}"));
        if (scan.Accepted.Count > 3)
            acceptedSummary += $"; +{scan.Accepted.Count - 3} more";

        var logKey = $"{activeDutyKey}:{map.RowId}:{scan.NextRoomIndex}:{scan.Total}:{scan.MapMatched}:{scan.MapMismatch}:{scan.YMismatch}:{scan.RoomMatchMiss}:{scan.Suppressed}:{acceptedSummary}:{scan.Detail}";
        if (string.Equals(treasureFollowerLastCofferStagingScanLogKey, logKey, StringComparison.Ordinal))
            return;

        treasureFollowerLastCofferStagingScanLogKey = logKey;
        log.Information(
            $"[ADS] Treasure follower coffer staging scan map={map.RowId} nextRoom={scan.NextRoomIndex} total={scan.Total} mapMatched={scan.MapMatched} mapMismatch={scan.MapMismatch} yMismatch={scan.YMismatch} roomMatchMiss={scan.RoomMatchMiss} suppression={scan.Suppressed} accepted={scan.Accepted.Count} acceptedTargets={acceptedSummary}; {scan.Detail}.");
    }

    private static DungeonFrontierPoint BuildTreasureFollowerCofferStagingPoint(
        DutyContextSnapshot context,
        Map map,
        int roomIndex,
        ObservedInteractable coffer)
        => new()
        {
            Key = $"treasure-follower-stage-coffer:{context.TerritoryTypeId}:{map.RowId}:{roomIndex}:{coffer.Key}",
            Name = $"Room {roomIndex} staging coffer: {coffer.Name}",
            Position = coffer.Position,
            LevelRowId = 0,
            MapId = coffer.MapId == 0 ? map.RowId : coffer.MapId,
            Priority = 0,
            ArrivalRadiusXz = TreasureFollowerStagingArrivalRadius,
            TreasureRoomIndex = roomIndex,
            TreasurePassageGroup = "StagingCoffer",
            IsTreasureFollowerStagingPoint = true,
        };

    private bool TryBuildTreasureFollowerLatchedCofferStagingPoint(
        DutyContextSnapshot context,
        Map map,
        out DungeonFrontierPoint point)
    {
        point = default!;
        if (!IsTreasureFollowerCofferSeekActive()
            || treasureFollowerActiveStagingRoomIndex <= 0
            || treasureFollowerActiveStagingTargetKey is null
            || !treasureFollowerActiveStagingTargetPosition.HasValue
            || IsTreasureFollowerRoomDisallowed(treasureFollowerActiveStagingRoomIndex))
        {
            return false;
        }

        point = new DungeonFrontierPoint
        {
            Key = treasureFollowerActiveStagingTargetKey,
            Name = string.IsNullOrWhiteSpace(treasureFollowerActiveStagingTargetName)
                ? $"Room {treasureFollowerActiveStagingRoomIndex} staging coffer"
                : treasureFollowerActiveStagingTargetName,
            Position = treasureFollowerActiveStagingTargetPosition.Value,
            LevelRowId = 0,
            MapId = treasureFollowerActiveStagingTargetMapId == 0
                ? map.RowId
                : treasureFollowerActiveStagingTargetMapId,
            Priority = 0,
            ArrivalRadiusXz = TreasureFollowerStagingArrivalRadius,
            TreasureRoomIndex = treasureFollowerActiveStagingRoomIndex,
            TreasurePassageGroup = "StagingCoffer",
            IsTreasureFollowerStagingPoint = true,
        };
        return true;
    }

    private IReadOnlyList<DungeonFrontierPoint> BuildLiveTreasureDoorRoutePoints(
        DutyContextSnapshot context,
        Map map,
        ObservationSnapshot observation,
        IReadOnlyList<DungeonFrontierPoint> staticRoutePoints,
        Vector3? playerPosition)
    {
        if (staticRoutePoints.Count == 0)
            return [];

        var passageMetadata = staticRoutePoints
            .Where(point => point.IsTreasurePassageCandidate)
            .ToList();
        if (passageMetadata.Count == 0)
            return [];

        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        return observation.LiveInteractables
            .Where(interactable => interactable.Classification == InteractableClass.TreasureDoor)
            .Where(interactable => MatchesCurrentMap(map.RowId, interactable.MapId))
            .Where(interactable => !playerPosition.HasValue || MathF.Abs(interactable.Position.Y - playerPosition.Value.Y) <= TreasureFollowerCandidateVerticalCap)
            .Select(interactable => new
            {
                Interactable = interactable,
                Metadata = SelectNearestTreasureDoorMetadata(passageMetadata, interactable.Position),
            })
            .Where(x => x.Metadata is not null)
            .Select(x => BuildLiveTreasureDoorRoutePoint(context, map, x.Interactable, x.Metadata!))
            .Where(point => seenKeys.Add(point.Key))
            .ToList();
    }

    private static DungeonFrontierPoint? SelectNearestTreasureDoorMetadata(
        IReadOnlyList<DungeonFrontierPoint> staticPassagePoints,
        Vector3 liveDoorPosition)
    {
        return staticPassagePoints
            .Select(point => new
            {
                Point = point,
                Distance = Vector3.Distance(liveDoorPosition, point.Position),
            })
            .OrderBy(x => x.Distance)
            .ThenBy(x => x.Point.Priority)
            .ThenBy(x => x.Point.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Point)
            .FirstOrDefault();
    }

    private static DungeonFrontierPoint BuildLiveTreasureDoorRoutePoint(
        DutyContextSnapshot context,
        Map map,
        ObservedInteractable interactable,
        DungeonFrontierPoint metadata)
        => new()
        {
            Key = BuildLiveTreasureDoorRouteKey(context, map, interactable, metadata),
            Name = $"{interactable.Name} ({metadata.Name})",
            Position = interactable.Position,
            LevelRowId = metadata.LevelRowId,
            MapId = interactable.MapId == 0 ? map.RowId : interactable.MapId,
            Priority = metadata.Priority,
            ManualDestinationKind = ManualDestinationKind.None,
            ArrivalRadiusXz = metadata.ArrivalRadiusXz,
            TreasureRouteIndex = metadata.TreasureRouteIndex,
            TreasureRoomIndex = metadata.TreasureRoomIndex,
            TreasurePassageGroup = metadata.TreasurePassageGroup,
            TreasureClearThroughPosition = metadata.TreasureClearThroughPosition,
            IsLiveTreasureDoorCandidate = true,
        };

    private static string BuildLiveTreasureDoorRouteKey(
        DutyContextSnapshot context,
        Map map,
        ObservedInteractable interactable,
        DungeonFrontierPoint metadata)
        => $"live-treasure-door:{context.TerritoryTypeId}:{map.RowId}:{metadata.TreasureRouteIndex}:{metadata.TreasureRoomIndex}:{metadata.TreasurePassageGroup}:{Quantize(interactable.Position)}";

    private bool TryApplyTreasureRouteTarget(
        DutyContextSnapshot context,
        IReadOnlyList<DungeonFrontierPoint> treasureRoutePoints,
        Vector3? playerPosition,
        DungeonFrontierPoint? previousTarget)
    {
        if (treasureRoutePoints.Count == 0)
            return false;

        var isTreasureFollower = EffectiveTreasureDungeonRole == ADS.Models.TreasureDungeonRole.Follower;
        var treasureFollowerTransitHold = isTreasureFollower && context.IsTreasureRouteTransitHold;
        if (isTreasureFollower && !treasureFollowerTransitHold)
            ConsumeHeldTreasureFollowerCandidateAfterObservedTransit(treasureRoutePoints);

        if (isTreasureFollower && !treasureFollowerTransitHold)
            ConsumeHeldTreasureFollowerCandidateAfterCombatStarted(context, treasureRoutePoints);

        if (playerPosition.HasValue && !treasureFollowerTransitHold)
        {
            MarkVisitedPoints(treasureRoutePoints, playerPosition.Value, FrontierVisitRadius, FrontierVisitVerticalCap);
            MarkTreasureRouteCatchUp(treasureRoutePoints, playerPosition.Value);
        }

        if (treasureFollowerTransitHold)
        {
            CurrentTarget = previousTarget is { IsTreasureRoutePoint: true }
                ? BuildNavigationPoint(previousTarget, playerPosition)
                : null;
            CurrentMode = CurrentTarget is not null ? FrontierMode.TreasureDungeon : FrontierMode.None;
            treasureFollowerRouteHoldReason = "RouteTransitHold";
            CurrentLabelStatus = "Holding treasure follower route updates during passage movement.";
            return true;
        }

        isTreasureFollower = EffectiveTreasureDungeonRole == ADS.Models.TreasureDungeonRole.Follower;
        var treasureFollowerRouteHoldActive = false;
        CurrentTarget = isTreasureFollower
            ? SelectTreasureFollowerTarget(context, treasureRoutePoints, playerPosition, previousTarget, out treasureFollowerRouteHoldActive)
            : SelectCurrentTarget(treasureRoutePoints, playerPosition, previousTarget);
        if (CurrentTarget is not null)
        {
            CurrentMode = FrontierMode.TreasureDungeon;
            return true;
        }

        if (treasureFollowerRouteHoldActive)
            return true;

        return false;
    }

    private bool TryApplyTreasureFollowerStartGate(
        DutyContextSnapshot context,
        bool hasActiveMap,
        Map activeMap,
        IReadOnlyList<DungeonFrontierPoint> manualXyzDestinations,
        Vector3? playerPosition)
    {
        if (TreasureDungeonRole != ADS.Models.TreasureDungeonRole.Follower
            || GetTreasureFollowerReachedFloor() != 0
            || HasCurrentDutyTreasureFollowerEntryProof())
        {
            return false;
        }

        if (TreasureFollowerStartGateActive
            && treasureFollowerStartGateTargetKey is not null)
        {
            var activeTarget = manualXyzDestinations
                .FirstOrDefault(point => string.Equals(point.Key, treasureFollowerStartGateTargetKey, StringComparison.Ordinal));
            if (activeTarget is null
                && hasActiveMap
                && TreasureDungeonData.HasRoute(context.TerritoryTypeId))
            {
                activeTarget = TreasureDungeonData
                    .BuildRoutePoints(context.TerritoryTypeId, activeMap.RowId)
                    .FirstOrDefault(point => string.Equals(point.Key, treasureFollowerStartGateTargetKey, StringComparison.Ordinal));
            }

            if (activeTarget is not null && !visitedFrontierKeys.Contains(activeTarget.Key))
            {
                CurrentTarget = BuildNavigationPoint(activeTarget, playerPosition);
                CurrentMode = activeTarget.IsManualXyzDestination
                    ? FrontierMode.XyzDestination
                    : FrontierMode.TreasureDungeon;
                RememberManualDestination(CurrentTarget);
                return true;
            }

            ClearTreasureFollowerStartGateIfComplete();
            if (TreasureFollowerStartGateActive)
                ClearTreasureFollowerStartGate();
        }

        CurrentTarget = SelectCurrentManualDestination(manualXyzDestinations, playerPosition);
        if (CurrentTarget is not null)
        {
            CurrentMode = FrontierMode.XyzDestination;
            ActivateTreasureFollowerStartGate(CurrentTarget);
            ClearTreasureFollowerCandidateHold("entry XYZ destination selected");
            RememberManualDestination(CurrentTarget);
            return true;
        }

        if (VisitedManualXyzDestinations > 0
            || !hasActiveMap
            || !TreasureDungeonData.HasRoute(context.TerritoryTypeId))
        {
            return false;
        }

        var treasureRouteStart = TreasureDungeonData
            .BuildRoutePoints(context.TerritoryTypeId, activeMap.RowId)
            .FirstOrDefault(IsStaticTreasureRouteStartPoint);
        if (treasureRouteStart is null || visitedFrontierKeys.Contains(treasureRouteStart.Key))
            return false;

        CurrentTarget = BuildNavigationPoint(treasureRouteStart, playerPosition);
        CurrentMode = FrontierMode.TreasureDungeon;
        ActivateTreasureFollowerStartGate(CurrentTarget);
        ClearTreasureFollowerCandidateHold("static treasure start selected");
        return true;
    }

    private void LogTreasureFollowerBacktrackSkip(DungeonFrontierPoint target, int reachedRoom)
    {
        var key = $"{activeDutyKey}:{target.Key}:{target.TreasureRoomIndex}:{reachedRoom}";
        if (!loggedTreasureFollowerBacktrackSkips.Add(key))
            return;

        log.Information(
            $"[ADS] treasure follower route action=skip-backtrack target='{EscapeLogText(target.Name)}' room={target.TreasureRoomIndex} reached={reachedRoom}");
    }

    private IReadOnlyList<DungeonFrontierPoint> BuildFrontierPoints(uint territoryTypeId, uint mapId)
    {
        try
        {
            var territorySheet = dataManager.GetExcelSheet<TerritoryType>();
            var levelSheet = dataManager.GetExcelSheet<Level>();
            var mapMarkerSheet = dataManager.GetSubrowExcelSheet<MapMarker>();
            if (territorySheet is null || levelSheet is null || mapMarkerSheet is null)
            {
                return BuildMapMarkerRangeFrontierPoints(territoryTypeId, mapId, "level-backed sheet lookup failed");
            }

            if (!territorySheet.TryGetRow(territoryTypeId, out var territory))
            {
                LogUnavailableFrontierLabels(territoryTypeId, mapId, "territory row was missing");
                return [];
            }

            var levelsByRowId = levelSheet
                .Where(level => level.Territory.RowId == territoryTypeId && level.Map.RowId == mapId)
                .ToDictionary(level => level.RowId, level => level);
            if (levelsByRowId.Count == 0)
            {
                return BuildMapMarkerRangeFrontierPoints(territoryTypeId, mapId, $"no matching Level rows for map {mapId}");
            }

            var points = new List<DungeonFrontierPoint>();
            var seenKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var subrowCollection in mapMarkerSheet)
            {
                foreach (var marker in subrowCollection)
                {
                    if (marker.PlaceNameSubtext.ValueNullable is null)
                        continue;

                    var name = NormalizeName(marker.PlaceNameSubtext.Value.Name.ToString());
                    if (string.IsNullOrWhiteSpace(name))
                        continue;

                    if (marker.DataKey.RowId == 0 || !levelsByRowId.TryGetValue(marker.DataKey.RowId, out var level))
                        continue;

                    var position = new Vector3(level.X, level.Y, level.Z);
                    var dedupeKey = $"{territoryTypeId}:{mapId}:{name}:{Quantize(position)}";
                    if (!seenKeys.Add(dedupeKey))
                        continue;

                    points.Add(new DungeonFrontierPoint
                    {
                        Key = dedupeKey,
                        Name = name,
                        Position = position,
                        LevelRowId = level.RowId,
                        MapId = mapId,
                    });
                }
            }

            points.Sort(static (left, right) =>
            {
                var byLevel = left.LevelRowId.CompareTo(right.LevelRowId);
                if (byLevel != 0)
                    return byLevel;

                return string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
            });

            if (points.Count > 0)
                log.Information($"[ADS] Built {points.Count} frontier label point(s) for territory {territoryTypeId} on map {mapId}.");

            return points.Count > 0
                ? points
                : BuildMapMarkerRangeFrontierPoints(territoryTypeId, mapId, $"matched {levelsByRowId.Count} Level row(s) but found no DataKey-backed MapMarker labels");
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"[ADS] Failed to build frontier label points for territory {territoryTypeId} on map {mapId}.");
            return BuildMapMarkerRangeFrontierPoints(territoryTypeId, mapId, $"level-backed lookup threw {ex.GetType().Name}");
        }
    }

    private IReadOnlyList<DungeonFrontierPoint> BuildMapMarkerRangeFrontierPoints(uint territoryTypeId, uint mapId, string levelBackedFailureReason)
    {
        var labels = GetLabelMarkers(territoryTypeId, mapId);
        if (labels.Count == 0)
        {
            LogUnavailableFrontierLabels(territoryTypeId, mapId, $"{levelBackedFailureReason}; MapMarkerRange fallback produced 0 labels");
            return [];
        }

        var points = labels
            .OrderBy(static label => label.MapId)
            .ThenBy(static label => label.MarkerRangeId)
            .ThenBy(static label => label.SubrowId)
            .ThenBy(static label => label.Name, StringComparer.OrdinalIgnoreCase)
            .Select(static label => new DungeonFrontierPoint
            {
                Key = $"map-marker-range:{label.Key}",
                Name = label.Name,
                Position = label.WorldPosition,
                LevelRowId = BuildLabelSortOrder(label),
                MapId = label.MapId,
                UsePlayerYForNavigation = true,
            })
            .ToList();

        log.Information($"[ADS] Built {points.Count} frontier MapMarkerRange point(s) for territory {territoryTypeId} on map {mapId} after level-backed frontier was unavailable ({levelBackedFailureReason}).");
        return points;
    }

    private IReadOnlyList<MapLabelMarker> BuildLabelMarkers(uint territoryTypeId, uint mapId)
    {
        var cacheKey = BuildCacheKey(territoryTypeId, mapId);
        try
        {
            var territorySheet = dataManager.GetExcelSheet<TerritoryType>();
            var mapSheet = dataManager.GetExcelSheet<Map>();
            var mapMarkerSheet = dataManager.GetSubrowExcelSheet<MapMarker>();
            if (territorySheet is null || mapSheet is null || mapMarkerSheet is null)
            {
                labelMarkerStatusCache[cacheKey] = $"Map-label lookup failed for territory {territoryTypeId} map {mapId}: one or more sheets were unavailable.";
                return [];
            }

            if (!territorySheet.TryGetRow(territoryTypeId, out var territory))
            {
                labelMarkerStatusCache[cacheKey] = $"Map-label lookup failed: territory {territoryTypeId} was not found.";
                return [];
            }

            if (!mapSheet.TryGetRow(mapId, out var map) || map.TerritoryType.RowId != territoryTypeId)
            {
                labelMarkerStatusCache[cacheKey] = $"Map-label lookup failed: territory {territoryTypeId} active map {mapId} was not found.";
                return [];
            }

            var points = new List<MapLabelMarker>();
            var seenKeys = new HashSet<string>(StringComparer.Ordinal);
            var mapName = BuildMapName(map);
            var markerRangeId = (uint)map.MapMarkerRange;
            if (markerRangeId == 0)
            {
                labelMarkerStatusCache[cacheKey] = $"Loaded 0 map label marker(s) for territory {territoryTypeId} on active map {mapName} ({mapId}): no MapMarkerRange row was assigned.";
                return [];
            }

            foreach (var subrowCollection in mapMarkerSheet)
            {
                foreach (var marker in subrowCollection)
                {
                    if (marker.RowId != markerRangeId)
                        continue;

                    if (marker.PlaceNameSubtext.ValueNullable is null)
                        continue;

                    var name = NormalizeName(marker.PlaceNameSubtext.Value.Name.ToString());
                    if (string.IsNullOrWhiteSpace(name))
                        continue;

                    var worldPosition = ConvertTextureToWorld(marker.X, marker.Y, map);
                    var mapCoordinates = MapUtil.WorldToMap(new Vector2(worldPosition.X, worldPosition.Z), map);
                    var key = $"{territoryTypeId}:{map.RowId}:{markerRangeId}:{marker.SubrowId}:{name}:{marker.X}:{marker.Y}";
                    if (!seenKeys.Add(key))
                        continue;

                    points.Add(new MapLabelMarker
                    {
                        Key = key,
                        Name = name,
                        MapId = map.RowId,
                        MarkerRangeId = markerRangeId,
                        MapName = mapName,
                        SubrowId = marker.SubrowId,
                        DataType = marker.DataType,
                        Icon = marker.Icon,
                        TextureX = marker.X,
                        TextureY = marker.Y,
                        WorldPosition = worldPosition,
                        MapCoordinates = mapCoordinates,
                    });
                }
            }

            points.Sort(static (left, right) =>
            {
                var byMap = left.MapId.CompareTo(right.MapId);
                if (byMap != 0)
                    return byMap;

                var byMarkerRange = left.MarkerRangeId.CompareTo(right.MarkerRangeId);
                if (byMarkerRange != 0)
                    return byMarkerRange;

                var bySubrow = left.SubrowId.CompareTo(right.SubrowId);
                if (bySubrow != 0)
                    return bySubrow;

                return string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
            });

            labelMarkerStatusCache[cacheKey] = points.Count > 0
                ? $"Loaded {points.Count} map label marker(s) for territory {territoryTypeId} on active map {mapName} ({mapId})."
                : $"Loaded 0 map label marker(s) for territory {territoryTypeId} on active map {mapName} ({mapId}).";

            log.Information($"[ADS] {labelMarkerStatusCache[cacheKey]}");
            return points;
        }
        catch (Exception ex)
        {
            labelMarkerStatusCache[cacheKey] = $"Map-label lookup failed for territory {territoryTypeId} map {mapId}: {ex.Message}";
            log.Warning(ex, $"[ADS] Failed to build map label markers for territory {territoryTypeId} map {mapId}.");
            return [];
        }
    }

    private void LogUnavailableFrontierLabels(uint territoryTypeId, uint mapId, string reason)
        => log.Information($"[ADS] Built 0 frontier label point(s) for territory {territoryTypeId} on map {mapId}; label frontier unavailable ({reason}).");

    private void UpdateHeadingMemory(Vector3? playerPosition, ObservationSnapshot observation)
    {
        if (!playerPosition.HasValue)
            return;

        if (observation.LiveMonsters.Count == 0 && observation.LiveInteractables.Count == 0)
            return;

        if (!lastProgressSamplePosition.HasValue)
        {
            lastProgressSamplePosition = playerPosition.Value;
            return;
        }

        var flatDelta = new Vector3(
            playerPosition.Value.X - lastProgressSamplePosition.Value.X,
            0f,
            playerPosition.Value.Z - lastProgressSamplePosition.Value.Z);
        var flatDistance = flatDelta.Length();
        if (flatDistance < HeadingSampleMinDistance || flatDistance <= float.Epsilon)
            return;

        currentHeading = Vector3.Normalize(flatDelta);
        lastProgressSamplePosition = playerPosition.Value;
    }

    private DungeonFrontierPoint? SelectHeadingScoutTarget(DutyContextSnapshot context, Vector3 playerPosition)
    {
        if (ShouldSuppressTreasureFollowerHeadingScout())
        {
            headingScoutTarget = null;
            return null;
        }

        if (!currentHeading.HasValue || currentHeading.Value.LengthSquared() <= float.Epsilon)
            return null;

        if (headingScoutTarget is not null && ShouldKeepHeadingScoutTarget(headingScoutTarget, playerPosition, currentHeading.Value))
            return headingScoutTarget;

        headingScoutSequence++;
        var scoutPosition = new Vector3(
            playerPosition.X + (currentHeading.Value.X * HeadingScoutProjectionDistance),
            playerPosition.Y,
            playerPosition.Z + (currentHeading.Value.Z * HeadingScoutProjectionDistance));
        headingScoutTarget = new DungeonFrontierPoint
        {
            Key = $"scout:{context.TerritoryTypeId}:{headingScoutSequence}:{Quantize(scoutPosition)}",
            Name = $"Heading Scout {headingScoutSequence}",
            Position = scoutPosition,
            LevelRowId = 0,
            MapId = context.MapId,
        };
        log.Information(
            $"[ADS] Activated heading frontier scout for territory {context.TerritoryTypeId}: target {FormatVector(scoutPosition)} from heading {FormatVector(currentHeading.Value)}.");
        return headingScoutTarget;
    }

    private void MarkVisitedPoints(IReadOnlyList<DungeonFrontierPoint> points, Vector3 playerPosition, float defaultVisitRadius, float defaultVerticalCap)
    {
        foreach (var point in points)
        {
            if (EffectiveTreasureDungeonRole == ADS.Models.TreasureDungeonRole.Follower
                && (point.IsTreasurePassageCandidate || point.IsTreasureFollowerStagingPoint))
            {
                continue;
            }

            var horizontalDistance = GetHorizontalDistance(playerPosition, point.Position);
            var verticalDelta = point.UsePlayerYForNavigation
                ? 0f
                : MathF.Abs(point.Position.Y - playerPosition.Y);
            var visitRadius = point.ArrivalRadiusXz > 0f ? point.ArrivalRadiusXz : defaultVisitRadius;
            if (horizontalDistance <= visitRadius && verticalDelta <= defaultVerticalCap)
                MarkVisited(point, playerPosition);
        }
    }

    private void MarkTreasureRouteCatchUp(IReadOnlyList<DungeonFrontierPoint> points, Vector3 playerPosition)
    {
        if (points.Count < 2)
            return;

        var verticalCap = EffectiveTreasureDungeonRole == ADS.Models.TreasureDungeonRole.Follower
            ? TreasureFollowerCandidateVerticalCap
            : FrontierVisitVerticalCap;

        var nearest = points
            .Select((point, index) => new
            {
                Point = point,
                Index = index,
                HorizontalDistance = GetHorizontalDistance(playerPosition, point.Position),
                VerticalDelta = MathF.Abs(point.Position.Y - playerPosition.Y),
            })
            .Where(x => EffectiveTreasureDungeonRole != ADS.Models.TreasureDungeonRole.Follower
                        || !x.Point.IsTreasureFollowerStagingPoint)
            .Where(x => x.VerticalDelta <= verticalCap)
            .OrderBy(x => x.HorizontalDistance)
            .ThenBy(x => x.VerticalDelta)
            .ThenBy(x => x.Index)
            .FirstOrDefault();
        if (nearest is null || nearest.Index <= 0)
            return;

        if (EffectiveTreasureDungeonRole == ADS.Models.TreasureDungeonRole.Follower
            && nearest.Point.TreasureRoomIndex > 0
            && IsTreasureFollowerRoomBehindReachedFloor(nearest.Point.TreasureRoomIndex))
        {
            RetireTreasureFollowerBacktrackTarget(nearest.Point);
            return;
        }

        var furthestVisitedIndex = -1;
        for (var index = 0; index < points.Count; index++)
        {
            if (visitedFrontierKeys.Contains(points[index].Key))
                furthestVisitedIndex = index;
        }

        if (nearest.Index <= furthestVisitedIndex)
            return;

        var markedCount = 0;
        for (var index = 0; index < nearest.Index; index++)
        {
            if (EffectiveTreasureDungeonRole == ADS.Models.TreasureDungeonRole.Follower
                && (points[index].IsTreasurePassageCandidate || points[index].IsTreasureFollowerStagingPoint))
            {
                continue;
            }

            if (visitedFrontierKeys.Add(points[index].Key))
                markedCount++;
        }

        if (markedCount > 0)
        {
            var now = DateTime.UtcNow;
            if (now >= nextTreasureFollowerCatchUpLogUtc)
            {
                nextTreasureFollowerCatchUpLogUtc = now + TreasureFollowerCatchUpLogCooldown;
                log.Information(
                    $"[ADS] Treasure route catch-up marked {markedCount} earlier room point(s) visited before same-floor route point {nearest.Point.Name} at {FormatVector(nearest.Point.Position)} (XZ {nearest.HorizontalDistance:0.0}y, Y {nearest.VerticalDelta:0.0}y). This prevents post-door backtracking to prior treasure rooms.");
            }
        }
    }

    private void MarkTreasureFollowerEntryCatchUp(IReadOnlyList<DungeonFrontierPoint> points, Vector3 playerPosition)
    {
        if (TreasureDungeonRole != ADS.Models.TreasureDungeonRole.Follower
            || !HasTreasureFollowerPassedEntry()
            || points.Count == 0)
        {
            return;
        }

        var staleEntryPoints = points
            .Where(point => !point.IsTreasurePassageCandidate)
            .Where(point => !point.IsTreasureFollowerStagingPoint)
            .Where(point => !visitedFrontierKeys.Contains(point.Key))
            .ToList();
        if (staleEntryPoints.Count == 0)
            return;

        var reachedRoom = GetTreasureFollowerReachedFloor();
        var hasSameFloorPassageTruth = points.Any(point =>
            point.IsTreasurePassageCandidate
            && MathF.Abs(point.Position.Y - playerPosition.Y) <= TreasureFollowerCandidateVerticalCap);
        if (reachedRoom == 0 && !hasSameFloorPassageTruth)
            return;

        var markedCount = 0;
        foreach (var point in staleEntryPoints)
        {
            if (visitedFrontierKeys.Add(point.Key))
                markedCount++;
        }

        if (markedCount <= 0)
            return;

        var now = DateTime.UtcNow;
        if (now < nextTreasureFollowerCatchUpLogUtc)
            return;

        nextTreasureFollowerCatchUpLogUtc = now + TreasureFollowerCatchUpLogCooldown;
        var proof = reachedRoom > 0
            ? $"room progress reached {reachedRoom}"
            : "same-floor live/static passage truth appeared after the manual entry XYZ";
        log.Information(
            $"[ADS] Treasure follower catch-up marked {markedCount} stale entry/start route point(s) visited because {proof}. ADS will not backtrack to the landing platform after entry.");
    }

    private int GetTreasureFollowerReachedFloor()
        => Math.Max(highestTreasureFollowerRoomReached, treasureFollowerTransitConsumedRoomIndex);

    private void AdvanceTreasureFollowerRoomReached(int roomIndex)
    {
        if (roomIndex <= 0)
            return;

        highestTreasureFollowerRoomReached = Math.Max(highestTreasureFollowerRoomReached, roomIndex);
    }

    private bool IsTreasureFollowerRoomBehindReachedFloor(int roomIndex)
    {
        var reachedRoom = GetTreasureFollowerReachedFloor();
        return reachedRoom > 0
               && roomIndex > 0
               && roomIndex < reachedRoom;
    }

    private bool IsTreasureFollowerRoomDisallowed(int roomIndex)
        => (treasureFollowerTransitConsumedRoomIndex > 0 && roomIndex <= treasureFollowerTransitConsumedRoomIndex)
           || IsTreasureFollowerRoomBehindReachedFloor(roomIndex);

    private bool HasTreasureFollowerPassedEntry()
        => GetTreasureFollowerReachedFloor() > 0
           || HasCurrentDutyTreasureFollowerEntryProof();

    private bool HasCurrentDutyTreasureFollowerEntryProof()
        => treasureFollowerEntryProofDutyKey != 0
           && treasureFollowerEntryProofDutyKey == activeDutyKey;

    private bool IsTreasureFollowerPostEntryDoorCycleEnabled()
        => TreasureDungeonRole == ADS.Models.TreasureDungeonRole.Follower
           && HasCurrentDutyTreasureFollowerEntryProof();

    private bool TryEstablishTreasureFollowerEntryProof(DungeonFrontierPoint point, string reason)
    {
        if (!IsTreasureFollowerEntryProofPoint(point))
            return false;

        treasureFollowerEntryProofDutyKey = activeDutyKey;
        ClearTreasureFollowerStartGate();
        ClearTreasureFollowerCandidateHold("entry proof established");
        ResetTreasureFollowerDoorChaseGate();
        ClearTreasureFollowerRoomRetryCooldown();
        log.Information(
            $"[ADS] Treasure follower entry proof established after {reason} at {point.Name} ({point.TreasureRouteSource}, key {point.Key}). ADS will resume follower routing without marking room progress consumed.");
        return true;
    }

    private bool IsTreasureFollowerEntryProofPoint(DungeonFrontierPoint point)
        => TreasureDungeonRole == ADS.Models.TreasureDungeonRole.Follower
           && !HasCurrentDutyTreasureFollowerEntryProof()
           && activeDutyKey != 0
           && point.IsTreasureRoutePoint
           && point.TreasureRouteIndex > 0
           && !point.IsLiveTreasureDoorCandidate;

    private void RetireTreasureFollowerBacktrackTarget(DungeonFrontierPoint point)
    {
        if (EffectiveTreasureDungeonRole != ADS.Models.TreasureDungeonRole.Follower
            || point.TreasureRoomIndex <= 0)
        {
            return;
        }

        var reachedRoom = GetTreasureFollowerReachedFloor();
        if (reachedRoom <= 0 || point.TreasureRoomIndex >= reachedRoom)
            return;

        visitedFrontierKeys.Add(point.Key);
        LogTreasureFollowerBacktrackSkip(point, reachedRoom);
    }

    private static List<DungeonFrontierPoint> GetTreasureFollowerVerticalEligibleCandidates(
        IReadOnlyList<DungeonFrontierPoint> candidates,
        Vector3? playerPosition)
    {
        if (!playerPosition.HasValue)
            return candidates.ToList();

        return candidates
            .Where(point => IsWithinTreasureFollowerCandidateVerticalCap(point, playerPosition.Value))
            .ToList();
    }

    private List<DungeonFrontierPoint>? SelectTreasureFollowerCurrentRoomGroup(
        IReadOnlyList<List<DungeonFrontierPoint>> candidateGroups)
    {
        if (treasureFollowerActiveDoorCycleRoomIndex > 0)
        {
            var activeGroup = candidateGroups.FirstOrDefault(group => group[0].TreasureRoomIndex == treasureFollowerActiveDoorCycleRoomIndex);
            if (activeGroup is not null && !IsTreasureFollowerRoomDisallowed(activeGroup[0].TreasureRoomIndex))
                return activeGroup;

            if (activeGroup is not null)
            {
                foreach (var point in activeGroup)
                    RetireTreasureFollowerBacktrackTarget(point);
            }

            treasureFollowerActiveDoorCycleRoomIndex = 0;
        }

        foreach (var group in candidateGroups)
        {
            var roomIndex = group[0].TreasureRoomIndex;
            if (!IsTreasureFollowerRoomDisallowed(roomIndex))
                return group;

            foreach (var point in group)
                RetireTreasureFollowerBacktrackTarget(point);
        }

        return null;
    }

    private DungeonFrontierPoint? SelectTreasureFollowerForwardStaticRouteCandidate(
        IReadOnlyList<DungeonFrontierPoint> points,
        int currentRoomIndex,
        Vector3? playerPosition,
        DungeonFrontierPoint? previousTarget)
    {
        var forwardStaticCandidates = points
            .Where(point => point.IsTreasurePassageCandidate)
            .Where(point => !point.IsLiveTreasureDoorCandidate)
            .Where(point => !point.IsTreasureFollowerStagingPoint)
            .Where(point => point.TreasureRoomIndex > currentRoomIndex)
            .Where(point => !IsTreasureFollowerRoomDisallowed(point.TreasureRoomIndex))
            .Where(point => !IsTreasureFollowerPassageCandidateExhausted(point, points))
            .ToList();
        if (forwardStaticCandidates.Count == 0)
            return null;

        var eligibleCandidates = GetTreasureFollowerVerticalEligibleCandidates(forwardStaticCandidates, playerPosition);
        if (eligibleCandidates.Count == 0)
            return null;

        var selectedRoomCandidates = eligibleCandidates
            .GroupBy(point => point.TreasureRoomIndex)
            .OrderBy(group => group.Key)
            .First()
            .OrderBy(point => point.Priority)
            .ThenBy(point => point.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (selectedRoomCandidates.Count == 0)
            return null;

        treasureFollowerActiveDoorCycleRoomIndex = selectedRoomCandidates[0].TreasureRoomIndex;
        ResetTreasureFollowerStagingState();
        ResetTreasureFollowerDoorChaseGate();
        return SelectTreasureFollowerCandidate(selectedRoomCandidates, playerPosition, previousTarget);
    }

    private bool IsTreasureFollowerPassageCandidateExhausted(
        DungeonFrontierPoint candidate,
        IReadOnlyList<DungeonFrontierPoint> points)
        => IsTreasureFollowerPassageGroupFailed(candidate)
           || IsTreasureFollowerPassageCandidateVisited(candidate, points);

    private DungeonFrontierPoint? SelectTreasureFollowerStagingTarget(
        DutyContextSnapshot context,
        IReadOnlyList<DungeonFrontierPoint> points,
        int roomIndex,
        Vector3? playerPosition,
        DungeonFrontierPoint? previousTarget)
    {
        if (roomIndex <= 0)
        {
            return null;
        }

        var now = DateTime.UtcNow;
        if (IsTreasureFollowerDoorChaseGateTransitionActive(context))
        {
            ReleaseTreasureFollowerCofferSeek(roomIndex, "RouteTransitionStarted");
            return null;
        }

        UpdateTreasureFollowerCofferSeekProgress(roomIndex, playerPosition, now);
        if (treasureFollowerStagingReached
            && treasureFollowerActiveStagingRoomIndex == roomIndex)
        {
            treasureFollowerDoorChaseGateState = "StagedAtTreasureCoffer";
            treasureFollowerDoorChaseHoldActive = true;
            treasureFollowerRouteHoldReason = "StagedAtTreasureCoffer";
            CurrentLabelStatus = $"Staged near treasure coffer in room {roomIndex}; holding for room combat.";
            return null;
        }

        if (IsTreasureFollowerCofferSeekTerminalForRoom(roomIndex))
            return null;

        var stagingCandidates = points
            .Where(point => point.IsTreasureFollowerStagingPoint)
            .Where(point => point.TreasureRoomIndex == roomIndex)
            .Where(point => !visitedFrontierKeys.Contains(point.Key))
            .ToList();
        DungeonFrontierPoint? selected = null;
        if (IsTreasureFollowerCofferSeekActive()
            && treasureFollowerActiveStagingRoomIndex == roomIndex)
        {
            selected = stagingCandidates.FirstOrDefault(point => string.Equals(point.Key, treasureFollowerActiveStagingTargetKey, StringComparison.Ordinal))
                       ?? BuildTreasureFollowerStoredCofferSeekPoint();
        }

        if (selected is null)
        {
            if (stagingCandidates.Count == 0)
            {
                if (context.InCombat || treasureFollowerDoorSeekCombatStarted)
                    MarkTreasureFollowerCofferSeekNoTarget(roomIndex, "NoStagingCofferSeen");

                return null;
            }

            var matchingPrevious = previousTarget is { IsTreasureFollowerStagingPoint: true }
                ? stagingCandidates.FirstOrDefault(point => string.Equals(point.Key, previousTarget.Key, StringComparison.Ordinal))
                : null;
            selected = matchingPrevious
                ?? stagingCandidates
                    .OrderBy(point => point.Priority)
                    .ThenBy(point => playerPosition.HasValue ? GetHorizontalDistance(playerPosition.Value, point.Position) : 0f)
                    .ThenBy(point => point.Name, StringComparer.OrdinalIgnoreCase)
                    .First();

            LatchTreasureFollowerCofferSeek(
                roomIndex,
                selected,
                now,
                context.InCombat || treasureFollowerDoorSeekCombatStarted ? "CombatStart" : "PreCombat");
        }

        if (IsTreasureFollowerStagingPointReached(selected, playerPosition))
            MarkTreasureFollowerCofferSeekReached(selected, playerPosition, "ArrivalRadius");

        if (treasureFollowerStagingReached)
        {
            treasureFollowerDoorChaseGateState = "StagedAtTreasureCoffer";
            treasureFollowerDoorChaseHoldActive = true;
            treasureFollowerRouteHoldReason = "StagedAtTreasureCoffer";
            CurrentLabelStatus = $"Staged near treasure coffer in room {roomIndex}; holding for room combat.";
            return null;
        }

        treasureFollowerDoorChaseGateState = "StagingTowardTreasureCoffer";
        treasureFollowerDoorChaseHoldActive = false;
        treasureFollowerCofferSeekState = TreasureFollowerCofferSeekState.Seeking;
        CurrentLabelStatus = context.InCombat || treasureFollowerDoorSeekCombatStarted
            ? $"Combat started before coffer staging finished; ADS is still seeking one treasure coffer staging point in room {roomIndex}."
            : $"Staging toward live treasure coffer in room {roomIndex} before combat starts.";
        return BuildNavigationPoint(selected, playerPosition);
    }

    private DungeonFrontierPoint? SelectCurrentManualDestination(IReadOnlyList<DungeonFrontierPoint> points, Vector3? playerPosition)
    {
        if (points.Count == 0)
            return null;

        var unvisitedPoints = points
            .Where(point => !visitedFrontierKeys.Contains(point.Key))
            .ToList();
        if (unvisitedPoints.Count == 0)
            return null;

        if (!playerPosition.HasValue)
            return unvisitedPoints
                .OrderBy(point => point.Priority)
                .ThenBy(point => point.Name, StringComparer.OrdinalIgnoreCase)
                .First();

        return BuildNavigationPoint(
            unvisitedPoints
                .Select(point => new
                {
                    Point = point,
                    Distance = GetManualDestinationDistance(playerPosition.Value, point),
                    VerticalDelta = MathF.Abs(point.Position.Y - playerPosition.Value.Y),
                })
                .OrderBy(x => x.Point.Priority)
                .ThenBy(x => x.Distance)
                .ThenBy(x => x.VerticalDelta)
                .ThenBy(x => x.Point.Name, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.Point)
                .First(),
            playerPosition);
    }

    private DungeonFrontierPoint? SelectTreasureFollowerTarget(
        DutyContextSnapshot context,
        IReadOnlyList<DungeonFrontierPoint> points,
        Vector3? playerPosition,
        DungeonFrontierPoint? previousTarget,
        out bool routeHoldActive)
    {
        routeHoldActive = false;
        if (points.Count == 0)
            return null;

        var startPoint = points.FirstOrDefault(point => !point.IsTreasurePassageCandidate);
        var heldCandidate = GetHeldTreasureFollowerCandidate(points);
        if (heldCandidate is not null)
        {
            var heldCandidateOutsideVerticalCap = !heldTreasureFollowerCandidateTransitObserved
                && playerPosition.HasValue
                && !IsWithinTreasureFollowerCandidateVerticalCap(heldCandidate, playerPosition.Value);
            if ((highestTreasureFollowerRoomReached > 0
                 && heldCandidate.TreasureRoomIndex > 0
                 && heldCandidate.TreasureRoomIndex < highestTreasureFollowerRoomReached)
                || (treasureFollowerTransitConsumedRoomIndex > 0
                    && heldCandidate.TreasureRoomIndex > 0
                    && heldCandidate.TreasureRoomIndex <= treasureFollowerTransitConsumedRoomIndex)
                || IsTreasureFollowerPassageCandidateVisited(heldCandidate, points)
                || heldCandidateOutsideVerticalCap)
            {
                RetireTreasureFollowerBacktrackTarget(heldCandidate);
                ClearTreasureFollowerCandidateHold("held candidate superseded by current route truth");
            }
            else
            {
                treasureFollowerActiveDoorCycleRoomIndex = heldCandidate.TreasureRoomIndex;
                if (TryHoldTreasureFollowerDoorChaseGate(context, heldCandidate.TreasureRoomIndex, out var heldGateStatus))
                {
                    routeHoldActive = true;
                    CurrentLabelStatus = heldGateStatus;
                    return null;
                }

                return BuildNavigationPoint(heldCandidate, playerPosition);
            }
        }

        if (startPoint is not null
            && !visitedFrontierKeys.Contains(startPoint.Key)
            && !HasTreasureFollowerPassedEntry())
        {
            return BuildNavigationPoint(startPoint, playerPosition);
        }

        if (!IsTreasureFollowerPostEntryDoorCycleEnabled())
            return SelectCurrentTarget(points, playerPosition, previousTarget);

        var passageCandidates = points
            .Where(point => point.IsTreasurePassageCandidate)
            .ToList();
        if (passageCandidates.Count == 0)
            return SelectCurrentTarget(points, playerPosition, previousTarget);

        var candidateGroups = passageCandidates
            .GroupBy(point => point.TreasureRoomIndex)
            .OrderBy(group => group.Key)
            .Select(group => group.OrderBy(point => point.Priority).ThenBy(point => point.Name, StringComparer.OrdinalIgnoreCase).ToList())
            .ToList();

        var currentRoomGroup = SelectTreasureFollowerCurrentRoomGroup(candidateGroups);
        if (currentRoomGroup is null)
        {
            var forwardStaticTarget = SelectTreasureFollowerForwardStaticRouteCandidate(
                points,
                GetTreasureFollowerReachedFloor(),
                playerPosition,
                previousTarget);
            if (forwardStaticTarget is not null)
            {
                treasureFollowerRouteHoldReason = string.Empty;
                CurrentLabelStatus = $"No current-room treasure follower candidates remained; advancing to forward static route point {forwardStaticTarget.Name}.";
                return forwardStaticTarget;
            }

            return null;
        }

        var roomIndex = currentRoomGroup[0].TreasureRoomIndex;
        treasureFollowerActiveDoorCycleRoomIndex = roomIndex;
        EnsureTreasureFollowerDoorSeekCombatGateRoom(roomIndex);

        var stagingTarget = SelectTreasureFollowerStagingTarget(
            context,
            points,
            roomIndex,
            playerPosition,
            previousTarget);
        if (stagingTarget is not null)
            return stagingTarget;

        if (treasureFollowerStagingReached
            && treasureFollowerActiveStagingRoomIndex == roomIndex
            && !treasureFollowerDoorSeekCombatStarted)
        {
            routeHoldActive = true;
            treasureFollowerRouteHoldReason = "WaitingForRoomCombat";
            return null;
        }

        var roomCandidates = GetTreasureFollowerPreferredRoomCandidates(currentRoomGroup, playerPosition);
        var eligibleCandidates = GetTreasureFollowerVerticalEligibleCandidates(roomCandidates, playerPosition);
        if (eligibleCandidates.Count == 0)
        {
            var forwardStaticTarget = SelectTreasureFollowerForwardStaticRouteCandidate(
                points,
                roomIndex,
                playerPosition,
                previousTarget);
            if (forwardStaticTarget is not null)
            {
                treasureFollowerRouteHoldReason = string.Empty;
                CurrentLabelStatus = $"Room {roomIndex} had no same-floor live/static passage candidate; advancing to forward static route point {forwardStaticTarget.Name}.";
                return forwardStaticTarget;
            }

            if (TryHoldTreasureFollowerRoomRetryCooldown(roomIndex, roomCandidates, out var sameFloorRetryStatus))
            {
                routeHoldActive = true;
                treasureFollowerRouteHoldReason = "NoSameFloorCandidateRetryCooldown";
                CurrentLabelStatus = sameFloorRetryStatus;
                return null;
            }

            StartTreasureFollowerRoomRetryCooldown(roomIndex);
            if (TryHoldTreasureFollowerRoomRetryCooldown(roomIndex, roomCandidates, out sameFloorRetryStatus))
            {
                routeHoldActive = true;
                treasureFollowerRouteHoldReason = "NoSameFloorCandidateRetryCooldown";
                CurrentLabelStatus = sameFloorRetryStatus;
                return null;
            }
        }

        if (TryHoldTreasureFollowerDoorChaseGate(context, roomIndex, out var gateStatus))
        {
            routeHoldActive = true;
            treasureFollowerRouteHoldReason = treasureFollowerDoorChaseGateState;
            CurrentLabelStatus = gateStatus;
            return null;
        }

        if (TryHoldTreasureFollowerRoomRetryCooldown(roomIndex, roomCandidates, out var retryStatus))
        {
            routeHoldActive = true;
            treasureFollowerRouteHoldReason = "RoomRetryCooldown";
            CurrentLabelStatus = retryStatus;
            return null;
        }

        var unvisitedCandidates = roomCandidates
            .Where(point => eligibleCandidates.Contains(point))
            .Where(point => !IsTreasureFollowerPassageCandidateExhausted(point, points))
            .ToList();
        if (unvisitedCandidates.Count > 0)
            return SelectTreasureFollowerCandidate(unvisitedCandidates, playerPosition, previousTarget);

        StartTreasureFollowerRoomRetryCooldown(roomIndex);
        if (TryHoldTreasureFollowerRoomRetryCooldown(roomIndex, roomCandidates, out retryStatus))
        {
            routeHoldActive = true;
            treasureFollowerRouteHoldReason = "RoomRetryCooldown";
            CurrentLabelStatus = retryStatus;
            return null;
        }

        return null;
    }

    private DungeonFrontierPoint SelectTreasureFollowerCandidate(
        IReadOnlyList<DungeonFrontierPoint> candidates,
        Vector3? playerPosition,
        DungeonFrontierPoint? previousTarget)
    {
        candidates = GetTreasureFollowerPreferredRoomCandidates(candidates, playerPosition);
        if (previousTarget is not null)
        {
            var matchingPrevious = candidates.FirstOrDefault(point => string.Equals(point.Key, previousTarget.Key, StringComparison.Ordinal));
            if (matchingPrevious is not null)
            {
                HoldTreasureFollowerCandidate(matchingPrevious);
                return BuildNavigationPoint(matchingPrevious, playerPosition);
            }
        }

        if (!playerPosition.HasValue)
        {
            var selectedWithoutPosition = candidates
                .OrderBy(point => point.IsLiveTreasureDoorCandidate ? 0 : 1)
                .ThenBy(point => point.Priority)
                .ThenBy(point => point.Name, StringComparer.OrdinalIgnoreCase)
                .First();
            HoldTreasureFollowerCandidate(selectedWithoutPosition);
            return selectedWithoutPosition;
        }

        var selected = candidates
            .Select(point => new
            {
                Point = point,
                HorizontalDistance = GetHorizontalDistance(playerPosition.Value, point.Position),
                VerticalDelta = MathF.Abs(point.Position.Y - playerPosition.Value.Y),
            })
            .OrderBy(x => x.Point.IsLiveTreasureDoorCandidate ? 0 : 1)
            .ThenBy(x => x.HorizontalDistance)
            .ThenBy(x => x.VerticalDelta)
            .ThenBy(x => x.Point.Priority)
            .ThenBy(x => x.Point.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Point)
            .First();
        HoldTreasureFollowerCandidate(selected);
        return BuildNavigationPoint(selected, playerPosition);
    }

    private static IReadOnlyList<DungeonFrontierPoint> GetTreasureFollowerPreferredRoomCandidates(
        IReadOnlyList<DungeonFrontierPoint> candidates,
        Vector3? playerPosition)
    {
        if (!playerPosition.HasValue)
        {
            return candidates
                .OrderBy(point => point.IsLiveTreasureDoorCandidate ? 0 : 1)
                .ThenBy(point => point.Priority)
                .ThenBy(point => point.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return candidates
            .Select(point => new
            {
                Point = point,
                HorizontalDistance = GetHorizontalDistance(playerPosition.Value, point.Position),
                VerticalDelta = MathF.Abs(point.Position.Y - playerPosition.Value.Y),
            })
            .OrderBy(x => x.Point.IsLiveTreasureDoorCandidate ? 0 : 1)
            .ThenBy(x => x.VerticalDelta <= TreasureFollowerCandidateVerticalCap ? 0 : 1)
            .ThenBy(x => x.HorizontalDistance)
            .ThenBy(x => x.VerticalDelta)
            .ThenBy(x => x.Point.Priority)
            .ThenBy(x => x.Point.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Point)
            .ToList();
    }

    private static bool IsWithinTreasureFollowerCandidateVerticalCap(DungeonFrontierPoint point, Vector3 playerPosition)
        => MathF.Abs(point.Position.Y - playerPosition.Y) <= TreasureFollowerCandidateVerticalCap;

    private static double GetRemainingSeconds(DateTime untilUtc)
        => untilUtc == DateTime.MinValue
            ? 0
            : Math.Max(0, (untilUtc - DateTime.UtcNow).TotalSeconds);

    private bool TryHoldTreasureFollowerDoorChaseGate(DutyContextSnapshot context, int roomIndex, out string status)
    {
        status = string.Empty;
        if (!IsTreasureFollowerPostEntryDoorCycleEnabled()
            || roomIndex <= 0)
        {
            treasureFollowerDoorChaseHoldActive = false;
            ResetTreasureFollowerDoorSeekCombatGate();
            return false;
        }

        if (treasureFollowerDoorChaseGateRoomIndex != roomIndex)
        {
            treasureFollowerDoorChaseGateRoomIndex = roomIndex;
            treasureFollowerDoorChaseGateTransitionSeenActive = false;
            treasureFollowerDoorChaseGateSettleUntilUtc = DateTime.MinValue;
        }

        var now = DateTime.UtcNow;
        if (TryHoldTreasureFollowerDoorSeekCombatGate(context, roomIndex, now, out status))
            return true;

        if (TryHoldTreasureFollowerDoorOpenTransitionGate(context, roomIndex, now, out status))
            return true;

        treasureFollowerDoorChaseGateState = "Open";
        treasureFollowerDoorChaseHoldActive = false;
        return false;
    }

    private bool TryHoldTreasureFollowerDoorSeekCombatGate(
        DutyContextSnapshot context,
        int roomIndex,
        DateTime now,
        out string status)
    {
        status = string.Empty;
        ObserveTreasureFollowerDoorSeekCombatGate(context, roomIndex, now);

        if (!treasureFollowerDoorSeekCombatStarted)
        {
            treasureFollowerDoorChaseGateState = "WaitingForRoomCombat";
            treasureFollowerDoorChaseHoldActive = true;
            status = $"Waiting for live treasure coffer staging target or room {roomIndex} combat before chasing passage doors.";
            return true;
        }

        if (!treasureFollowerDoorSeekCombatEnded)
        {
            treasureFollowerDoorChaseGateState = "WaitingForRoomCombatEnd";
            treasureFollowerDoorChaseHoldActive = true;
            status = $"Combat active in room {roomIndex}; waiting for combat end before chasing passage doors.";
            return true;
        }

        if (now < treasureFollowerDoorSeekCombatGateSettleUntilUtc)
        {
            var remaining = Math.Max(0, (treasureFollowerDoorSeekCombatGateSettleUntilUtc - now).TotalSeconds);
            treasureFollowerDoorChaseGateState = "SettlingAfterRoomCombat";
            treasureFollowerDoorChaseHoldActive = true;
            status = $"Waiting {remaining:0.0}s after room {roomIndex} combat before chasing passage doors.";
            return true;
        }

        return false;
    }

    private bool TryHoldTreasureFollowerDoorOpenTransitionGate(
        DutyContextSnapshot context,
        int roomIndex,
        DateTime now,
        out string status)
    {
        status = string.Empty;

        if (IsTreasureFollowerDoorChaseGateTransitionActive(context))
        {
            treasureFollowerDoorChaseGateTransitionSeenActive = true;
            treasureFollowerDoorChaseGateSettleUntilUtc = DateTime.MinValue;
            treasureFollowerDoorChaseGateState = "WaitingForDoorOpenTransitionEnd";
            treasureFollowerDoorChaseHoldActive = true;
            status = $"Waiting for opener door-opening transition to finish before chasing room {roomIndex} passage doors ({FormatTreasureRouteTransitFlags(context)} active).";
            return true;
        }

        if (!treasureFollowerDoorChaseGateTransitionSeenActive)
        {
            treasureFollowerDoorChaseGateState = "WaitingForDoorOpenTransitionStart";
            treasureFollowerDoorChaseHoldActive = true;
            status = $"Waiting for opener door-opening transition to start before chasing room {roomIndex} passage doors.";
            return true;
        }

        if (treasureFollowerDoorChaseGateSettleUntilUtc == DateTime.MinValue)
            treasureFollowerDoorChaseGateSettleUntilUtc = now + TreasureFollowerDoorChaseSettleDelay;

        if (now < treasureFollowerDoorChaseGateSettleUntilUtc)
        {
            var remaining = Math.Max(0, (treasureFollowerDoorChaseGateSettleUntilUtc - now).TotalSeconds);
            treasureFollowerDoorChaseGateState = "SettlingAfterDoorOpenTransition";
            treasureFollowerDoorChaseHoldActive = true;
            status = $"Waiting {remaining:0.0}s after opener door-opening transition before chasing room {roomIndex} passage doors.";
            return true;
        }

        return false;
    }

    private bool IsTreasureFollowerDoorOpenTransitionGateArmed(DateTime now)
        => treasureFollowerDoorChaseGateRoomIndex > 0
           && treasureFollowerDoorSeekCombatGateRoomIndex == treasureFollowerDoorChaseGateRoomIndex
           && treasureFollowerDoorSeekCombatStarted
           && treasureFollowerDoorSeekCombatEnded
           && treasureFollowerDoorSeekCombatGateSettleUntilUtc != DateTime.MinValue
           && now >= treasureFollowerDoorSeekCombatGateSettleUntilUtc;

    private bool TryHoldTreasureFollowerRoomRetryCooldown(
        int roomIndex,
        IReadOnlyList<DungeonFrontierPoint> roomCandidates,
        out string status)
    {
        status = string.Empty;
        if (roomIndex <= 0
            || treasureFollowerRoomRetryCooldownRoomIndex != roomIndex
            || treasureFollowerRoomRetryCooldownUntilUtc == DateTime.MinValue)
        {
            return false;
        }

        var now = DateTime.UtcNow;
        if (now >= treasureFollowerRoomRetryCooldownUntilUtc)
        {
            ClearTreasureFollowerRoomRetryCooldown();
            ResetTreasureFollowerRoomCycleCandidates(roomIndex, roomCandidates);
            return false;
        }

        var remaining = Math.Max(0, (treasureFollowerRoomRetryCooldownUntilUtc - now).TotalSeconds);
        treasureFollowerDoorChaseGateState = "RoomRetryCooldown";
        treasureFollowerDoorChaseHoldActive = true;
        status = $"Waiting {remaining:0.0}s before retrying room {roomIndex} passage doors after failed committed candidate {treasureFollowerLastFailedCandidateKey}.";
        return true;
    }

    private void StartTreasureFollowerRoomRetryCooldown(int roomIndex)
    {
        if (roomIndex <= 0)
            return;

        treasureFollowerRoomRetryCooldownRoomIndex = roomIndex;
        treasureFollowerRoomRetryCooldownUntilUtc = DateTime.UtcNow + TreasureFollowerRoomRetryCooldown;
        treasureFollowerDoorChaseGateState = "RoomRetryCooldown";
        treasureFollowerDoorChaseHoldActive = true;
        log.Information(
            $"[ADS] Treasure follower room {roomIndex} exhausted same-room passage groups; starting room retry cooldown {TreasureFollowerRoomRetryCooldown.TotalSeconds:0}s before cycling those candidates again.");
    }

    private void ResetTreasureFollowerDoorChaseGate(bool preserveTransitionSeenActive = false)
    {
        var transitionSeenActive = preserveTransitionSeenActive && treasureFollowerDoorChaseGateTransitionSeenActive;
        treasureFollowerDoorChaseGateRoomIndex = 0;
        treasureFollowerDoorChaseGateTransitionSeenActive = transitionSeenActive;
        treasureFollowerDoorChaseGateSettleUntilUtc = DateTime.MinValue;
        treasureFollowerDoorChaseGateState = transitionSeenActive ? "WaitingForDoorOpenTransitionEnd" : "Inactive";
        treasureFollowerDoorChaseHoldActive = false;
        ResetTreasureFollowerDoorSeekCombatGate();
    }

    private void EnsureTreasureFollowerDoorSeekCombatGateRoom(int roomIndex)
    {
        if (roomIndex <= 0 || treasureFollowerDoorSeekCombatGateRoomIndex == roomIndex)
            return;

        treasureFollowerDoorSeekCombatGateRoomIndex = roomIndex;
        treasureFollowerDoorSeekCombatStarted = false;
        treasureFollowerDoorSeekCombatEnded = false;
        treasureFollowerDoorSeekCombatGateSettleUntilUtc = DateTime.MinValue;
    }

    private void ResetTreasureFollowerDoorSeekCombatGate()
    {
        treasureFollowerDoorSeekCombatGateRoomIndex = 0;
        treasureFollowerDoorSeekCombatStarted = false;
        treasureFollowerDoorSeekCombatEnded = false;
        treasureFollowerDoorSeekCombatGateSettleUntilUtc = DateTime.MinValue;
    }

    private void ClearTreasureFollowerRoomRetryCooldown()
    {
        treasureFollowerRoomRetryCooldownRoomIndex = 0;
        treasureFollowerRoomRetryCooldownUntilUtc = DateTime.MinValue;
    }

    private void ResetTreasureFollowerRoomCycleCandidates(
        int roomIndex,
        IReadOnlyList<DungeonFrontierPoint> roomCandidates)
    {
        foreach (var point in roomCandidates.Where(point => point.TreasureRoomIndex == roomIndex))
            visitedFrontierKeys.Remove(point.Key);

        ClearTreasureFollowerFailedPassageGroups(roomIndex);
        TreasureFollowerRetryCycle++;
        var now = DateTime.UtcNow;
        if (now >= nextTreasureFollowerRetryCycleLogUtc)
        {
            nextTreasureFollowerRetryCycleLogUtc = now + TreasureFollowerRetryCycleLogCooldown;
            log.Information(
                $"[ADS] Treasure follower cycling room {roomIndex} passage candidates again after room retry cooldown; all same-room candidate groups failed without route transit or combat start.");
        }
    }

    private bool MarkTreasureFollowerPassageGroupFailed(DungeonFrontierPoint point)
    {
        if (point.TreasureRoomIndex <= 0)
            return false;

        return treasureFollowerFailedPassageGroups.Add(BuildTreasureFollowerPassageGroupKey(point));
    }

    private bool IsTreasureFollowerPassageGroupFailed(DungeonFrontierPoint point)
        => point.TreasureRoomIndex > 0
           && treasureFollowerFailedPassageGroups.Contains(BuildTreasureFollowerPassageGroupKey(point));

    private void ClearTreasureFollowerFailedPassageGroups(int roomIndex)
    {
        if (roomIndex <= 0)
            return;

        var prefix = $"{activeDutyKey}:{roomIndex}:";
        treasureFollowerFailedPassageGroups.RemoveWhere(key => key.StartsWith(prefix, StringComparison.Ordinal));
    }

    private string BuildTreasureFollowerPassageGroupKey(DungeonFrontierPoint point)
        => $"{activeDutyKey}:{point.TreasureRoomIndex}:{point.TreasurePassageGroup}";

    private void ClearTreasureFollowerActiveDoorCycleRoom(int roomIndex)
    {
        if (treasureFollowerActiveDoorCycleRoomIndex == roomIndex)
            treasureFollowerActiveDoorCycleRoomIndex = 0;
        ResetTreasureFollowerStagingState(roomIndex);
    }

    private void ResetTreasureFollowerStagingState(int roomIndex = 0)
    {
        if (roomIndex > 0 && treasureFollowerActiveStagingRoomIndex != roomIndex)
            return;

        treasureFollowerActiveStagingRoomIndex = 0;
        treasureFollowerActiveStagingTargetKey = null;
        treasureFollowerActiveStagingTargetName = string.Empty;
        treasureFollowerActiveStagingTargetPosition = null;
        treasureFollowerActiveStagingTargetMapId = 0;
        treasureFollowerCofferSeekState = TreasureFollowerCofferSeekState.Inactive;
        treasureFollowerStagingReached = false;
        treasureFollowerStagingAttempted = false;
        treasureFollowerCofferSeekLastReason = string.Empty;
        treasureFollowerCofferSeekStartedUtc = DateTime.MinValue;
        treasureFollowerCofferSeekLastProgressUtc = DateTime.MinValue;
        treasureFollowerCofferSeekBestHorizontalDistance = float.MaxValue;
    }

    private bool ShouldEvaluateTreasureFollowerCofferSeekDuringCombat()
        => IsTreasureFollowerCofferSeekActive()
           || (!treasureFollowerStagingAttempted
               && treasureFollowerCofferSeekState is TreasureFollowerCofferSeekState.Inactive or TreasureFollowerCofferSeekState.Released);

    private bool IsTreasureFollowerCofferSeekActive()
        => treasureFollowerActiveStagingRoomIndex > 0
           && treasureFollowerActiveStagingTargetKey is not null
           && treasureFollowerActiveStagingTargetPosition.HasValue
           && !IsTreasureFollowerRoomDisallowed(treasureFollowerActiveStagingRoomIndex)
           && !treasureFollowerStagingReached
           && treasureFollowerCofferSeekState is TreasureFollowerCofferSeekState.Latched or TreasureFollowerCofferSeekState.Seeking;

    private bool IsTreasureFollowerCofferSeekTerminalForRoom(int roomIndex)
        => roomIndex > 0
           && treasureFollowerActiveStagingRoomIndex == roomIndex
           && treasureFollowerStagingAttempted
           && treasureFollowerCofferSeekState is TreasureFollowerCofferSeekState.Reached
               or TreasureFollowerCofferSeekState.NoStagingCofferSeen
               or TreasureFollowerCofferSeekState.TimedOut;

    private DungeonFrontierPoint? BuildTreasureFollowerStoredCofferSeekPoint()
    {
        if (treasureFollowerActiveStagingRoomIndex <= 0
            || treasureFollowerActiveStagingTargetKey is null
            || !treasureFollowerActiveStagingTargetPosition.HasValue)
        {
            return null;
        }

        return new DungeonFrontierPoint
        {
            Key = treasureFollowerActiveStagingTargetKey,
            Name = string.IsNullOrWhiteSpace(treasureFollowerActiveStagingTargetName)
                ? $"Room {treasureFollowerActiveStagingRoomIndex} staging coffer"
                : treasureFollowerActiveStagingTargetName,
            Position = treasureFollowerActiveStagingTargetPosition.Value,
            LevelRowId = 0,
            MapId = treasureFollowerActiveStagingTargetMapId,
            Priority = 0,
            ArrivalRadiusXz = TreasureFollowerStagingArrivalRadius,
            TreasureRoomIndex = treasureFollowerActiveStagingRoomIndex,
            TreasurePassageGroup = "StagingCoffer",
            IsTreasureFollowerStagingPoint = true,
        };
    }

    private void LatchTreasureFollowerCofferSeek(
        int roomIndex,
        DungeonFrontierPoint target,
        DateTime now,
        string reason)
    {
        if (roomIndex <= 0 || target.TreasureRoomIndex != roomIndex)
            return;

        var sameTarget = treasureFollowerActiveStagingRoomIndex == roomIndex
                         && string.Equals(treasureFollowerActiveStagingTargetKey, target.Key, StringComparison.Ordinal);
        treasureFollowerActiveStagingRoomIndex = roomIndex;
        treasureFollowerActiveStagingTargetKey = target.Key;
        treasureFollowerActiveStagingTargetName = target.Name;
        treasureFollowerActiveStagingTargetPosition = target.Position;
        treasureFollowerActiveStagingTargetMapId = target.MapId;
        treasureFollowerCofferSeekState = TreasureFollowerCofferSeekState.Latched;
        treasureFollowerStagingReached = false;
        treasureFollowerStagingAttempted = true;
        treasureFollowerCofferSeekLastReason = reason;
        treasureFollowerCofferSeekStartedUtc = now;
        treasureFollowerCofferSeekLastProgressUtc = now;
        treasureFollowerCofferSeekBestHorizontalDistance = float.MaxValue;

        if (!sameTarget)
        {
            log.Information(
                $"[ADS] Treasure follower coffer seek latched room {roomIndex} target {target.Name} at {FormatVector(target.Position)} ({reason}); ADS will reach this staging point once before combat owns movement.");
        }
    }

    private void UpdateTreasureFollowerCofferSeekProgress(int roomIndex, Vector3? playerPosition, DateTime now)
    {
        var target = BuildTreasureFollowerStoredCofferSeekPoint();
        if (target is null || !IsTreasureFollowerCofferSeekActive())
            return;

        if (roomIndex != treasureFollowerActiveStagingRoomIndex
            || IsTreasureFollowerRoomDisallowed(treasureFollowerActiveStagingRoomIndex))
        {
            ReleaseTreasureFollowerCofferSeek(treasureFollowerActiveStagingRoomIndex, "RoomAdvanced");
            return;
        }

        if (IsTreasureFollowerStagingPointReached(target, playerPosition))
        {
            MarkTreasureFollowerCofferSeekReached(target, playerPosition, "ArrivalRadius");
            return;
        }

        if (!playerPosition.HasValue)
            return;

        var horizontalDistance = GetHorizontalDistance(playerPosition.Value, target.Position);
        if (treasureFollowerCofferSeekBestHorizontalDistance == float.MaxValue
            || horizontalDistance < treasureFollowerCofferSeekBestHorizontalDistance - TreasureFollowerStagingProgressDistance)
        {
            treasureFollowerCofferSeekBestHorizontalDistance = horizontalDistance;
            treasureFollowerCofferSeekLastProgressUtc = now;
            return;
        }

        if (now - treasureFollowerCofferSeekLastProgressUtc < TreasureFollowerStagingNoProgressTimeout)
            return;

        MarkTreasureFollowerCofferSeekTimedOut(
            target,
            $"NoProgressTimeout after {TreasureFollowerStagingNoProgressTimeout.TotalSeconds:0}s at XZ {horizontalDistance:0.0}y");
    }

    private void MarkTreasureFollowerCofferSeekNoTarget(int roomIndex, string reason)
    {
        if (roomIndex <= 0
            || (treasureFollowerActiveStagingRoomIndex == roomIndex
                && treasureFollowerCofferSeekState == TreasureFollowerCofferSeekState.NoStagingCofferSeen))
        {
            return;
        }

        treasureFollowerActiveStagingRoomIndex = roomIndex;
        treasureFollowerActiveStagingTargetKey = null;
        treasureFollowerActiveStagingTargetName = string.Empty;
        treasureFollowerActiveStagingTargetPosition = null;
        treasureFollowerActiveStagingTargetMapId = 0;
        treasureFollowerCofferSeekState = TreasureFollowerCofferSeekState.NoStagingCofferSeen;
        treasureFollowerStagingReached = false;
        treasureFollowerStagingAttempted = true;
        treasureFollowerCofferSeekLastReason = reason;
        treasureFollowerCofferSeekStartedUtc = DateTime.MinValue;
        treasureFollowerCofferSeekLastProgressUtc = DateTime.MinValue;
        treasureFollowerCofferSeekBestHorizontalDistance = float.MaxValue;
        log.Information(
            $"[ADS] Treasure follower coffer seek room {roomIndex} state=NoStagingCofferSeen; no current-room live TreasureCoffer was visible before combat owned movement.");
    }

    private void MarkTreasureFollowerCofferSeekTimedOut(DungeonFrontierPoint target, string reason)
    {
        if (treasureFollowerCofferSeekState == TreasureFollowerCofferSeekState.TimedOut)
            return;

        treasureFollowerCofferSeekState = TreasureFollowerCofferSeekState.TimedOut;
        treasureFollowerStagingReached = false;
        treasureFollowerStagingAttempted = true;
        treasureFollowerCofferSeekLastReason = reason;
        log.Information(
            $"[ADS] Treasure follower coffer seek timed out room {target.TreasureRoomIndex} target {target.Name} at {FormatVector(target.Position)}; {reason}. Combat may own movement now.");
    }

    public void MarkTreasureFollowerCofferSeekReached(DungeonFrontierPoint target, Vector3? playerPosition, string reason)
    {
        if (target.TreasureRoomIndex <= 0
            || !target.IsTreasureFollowerStagingPoint
            || (treasureFollowerActiveStagingRoomIndex > 0 && treasureFollowerActiveStagingRoomIndex != target.TreasureRoomIndex))
        {
            return;
        }

        var wasReached = treasureFollowerStagingReached
                         && treasureFollowerCofferSeekState == TreasureFollowerCofferSeekState.Reached;
        treasureFollowerActiveStagingRoomIndex = target.TreasureRoomIndex;
        treasureFollowerActiveStagingTargetKey = target.Key;
        treasureFollowerActiveStagingTargetName = target.Name;
        treasureFollowerActiveStagingTargetPosition = target.Position;
        treasureFollowerActiveStagingTargetMapId = target.MapId;
        treasureFollowerCofferSeekState = TreasureFollowerCofferSeekState.Reached;
        treasureFollowerStagingReached = true;
        treasureFollowerStagingAttempted = true;
        treasureFollowerCofferSeekLastReason = reason;
        treasureFollowerCofferSeekLastProgressUtc = DateTime.UtcNow;

        if (!wasReached)
        {
            var distanceText = playerPosition.HasValue
                ? $" XZ {GetHorizontalDistance(playerPosition.Value, target.Position):0.0}y."
                : string.Empty;
            log.Information(
                $"[ADS] Treasure follower coffer seek reached room {target.TreasureRoomIndex} target {target.Name} at {FormatVector(target.Position)} ({reason}).{distanceText}");
        }
    }

    private void ReleaseTreasureFollowerCofferSeek(int roomIndex, string reason)
    {
        if (roomIndex <= 0 || treasureFollowerActiveStagingRoomIndex != roomIndex)
            return;

        if (treasureFollowerCofferSeekState is TreasureFollowerCofferSeekState.Inactive or TreasureFollowerCofferSeekState.Released)
            return;

        treasureFollowerCofferSeekState = TreasureFollowerCofferSeekState.Released;
        treasureFollowerCofferSeekLastReason = reason;
        treasureFollowerStagingReached = false;
        treasureFollowerStagingAttempted = false;
        treasureFollowerActiveStagingTargetKey = null;
        treasureFollowerActiveStagingTargetName = string.Empty;
        treasureFollowerActiveStagingTargetPosition = null;
        treasureFollowerActiveStagingTargetMapId = 0;
        treasureFollowerCofferSeekStartedUtc = DateTime.MinValue;
        treasureFollowerCofferSeekLastProgressUtc = DateTime.MinValue;
        treasureFollowerCofferSeekBestHorizontalDistance = float.MaxValue;
    }

    private static bool IsTreasureFollowerStagingPointReached(DungeonFrontierPoint point, Vector3? playerPosition)
    {
        if (!playerPosition.HasValue)
            return false;

        var visitRadius = point.ArrivalRadiusXz > 0f
            ? point.ArrivalRadiusXz
            : TreasureFollowerStagingArrivalRadius;
        return GetHorizontalDistance(playerPosition.Value, point.Position) <= visitRadius
               && MathF.Abs(point.Position.Y - playerPosition.Value.Y) <= TreasureFollowerCandidateVerticalCap;
    }

    private void SuppressTreasureFollowerHeadingScoutAfterSuccess()
        => treasureFollowerSuppressHeadingScoutUntilUtc = DateTime.UtcNow + TreasureFollowerHeadingScoutSuccessSuppressDuration;

    public void SuppressTreasureFollowerHeadingScoutForPostTransitSettle(string reason)
    {
        if (!IsTreasureFollowerPostEntryDoorCycleEnabled())
            return;

        SuppressTreasureFollowerHeadingScoutAfterSuccess();
        log.Information($"[ADS] Treasure follower suppressed heading scout for post-transit settle after {reason}.");
    }

    private bool ShouldSuppressTreasureFollowerHeadingScout()
    {
        if (!IsTreasureFollowerPostEntryDoorCycleEnabled())
            return false;

        if (treasureFollowerActiveDoorCycleRoomIndex > 0
            || heldTreasureFollowerCandidateKey is not null
            || treasureFollowerDoorChaseHoldActive
            || treasureFollowerRoomRetryCooldownUntilUtc != DateTime.MinValue)
        {
            return true;
        }

        return DateTime.UtcNow < treasureFollowerSuppressHeadingScoutUntilUtc;
    }

    private DungeonFrontierPoint? SelectCurrentTarget(IReadOnlyList<DungeonFrontierPoint> points, Vector3? playerPosition, DungeonFrontierPoint? previousTarget)
    {
        if (points.Count == 0)
            return null;

        var unvisitedPoints = points
            .Where(point => !visitedFrontierKeys.Contains(point.Key))
            .ToList();
        if (unvisitedPoints.Count == 0)
            return null;

        var usesMapMarkerRangeFallback = points.Any(point => point.UsePlayerYForNavigation);
        if (usesMapMarkerRangeFallback && playerPosition.HasValue)
        {
            if (previousTarget is not null
                && unvisitedPoints.Any(point => point.Key == previousTarget.Key)
                && ShouldKeepLabelTarget(previousTarget, playerPosition.Value))
            {
                return BuildNavigationPoint(previousTarget, playerPosition);
            }

            var forwardPoint = SelectForwardLabelPoint(unvisitedPoints, playerPosition.Value);
            if (forwardPoint is not null)
                return BuildNavigationPoint(forwardPoint, playerPosition);
        }

        if (usesMapMarkerRangeFallback)
            return BuildNavigationPoint(SelectNearestPoint(unvisitedPoints, playerPosition), playerPosition);

        var furthestVisitedIndex = -1;
        for (var index = 0; index < points.Count; index++)
        {
            if (visitedFrontierKeys.Contains(points[index].Key))
                furthestVisitedIndex = index;
        }

        if (furthestVisitedIndex >= 0)
        {
            for (var index = furthestVisitedIndex + 1; index < points.Count; index++)
            {
                if (!visitedFrontierKeys.Contains(points[index].Key))
                    return BuildNavigationPoint(points[index], playerPosition);
            }

            return null;
        }

        if (!playerPosition.HasValue)
            return points[0];

        return BuildNavigationPoint(SelectNearestPoint(unvisitedPoints, playerPosition), playerPosition);
    }

    private DungeonFrontierPoint? SelectForwardLabelPoint(IReadOnlyList<DungeonFrontierPoint> points, Vector3 playerPosition)
    {
        if (!currentHeading.HasValue || currentHeading.Value.LengthSquared() <= float.Epsilon)
            return null;

        return points
            .Select(point => new
            {
                Point = point,
                Distance = GetHorizontalDistance(playerPosition, point.Position),
                ForwardDot = GetForwardDot(playerPosition, point.Position, currentHeading.Value),
            })
            .Where(x => x.Distance > FrontierVisitRadius && x.ForwardDot >= LabelFrontierMinForwardDot)
            .OrderBy(x => x.Distance * (2f - x.ForwardDot))
            .ThenBy(x => x.Point.LevelRowId)
            .Select(x => x.Point)
            .FirstOrDefault();
    }

    private bool ShouldKeepLabelTarget(DungeonFrontierPoint point, Vector3 playerPosition)
    {
        if (GetHorizontalDistance(playerPosition, point.Position) <= FrontierVisitRadius)
            return false;

        if (!currentHeading.HasValue || currentHeading.Value.LengthSquared() <= float.Epsilon)
            return true;

        return GetForwardDot(playerPosition, point.Position, currentHeading.Value) >= LabelFrontierRetargetBacktrackDot;
    }

    private static DungeonFrontierPoint SelectNearestPoint(IReadOnlyList<DungeonFrontierPoint> points, Vector3? playerPosition)
    {
        if (!playerPosition.HasValue)
            return points[0];

        return points
            .Select(point => new
            {
                Point = point,
                Distance = GetHorizontalDistance(playerPosition.Value, point.Position),
                VerticalDelta = point.UsePlayerYForNavigation
                    ? 0f
                    : MathF.Abs(point.Position.Y - playerPosition.Value.Y),
            })
            .OrderBy(x => x.VerticalDelta <= FrontierVisitVerticalCap ? 0 : 1)
            .ThenBy(x => x.Distance)
            .ThenBy(x => x.VerticalDelta)
            .ThenBy(x => x.Point.LevelRowId)
            .Select(x => x.Point)
            .First();
    }

    private static bool ShouldKeepHeadingScoutTarget(DungeonFrontierPoint scoutTarget, Vector3 playerPosition, Vector3 heading)
    {
        var flatDelta = new Vector3(
            scoutTarget.Position.X - playerPosition.X,
            0f,
            scoutTarget.Position.Z - playerPosition.Z);
        var flatDistance = flatDelta.Length();
        if (flatDistance <= HeadingScoutAdvanceRadius || flatDistance <= float.Epsilon)
            return false;

        var forwardDot = Vector3.Dot(Vector3.Normalize(flatDelta), heading);
        return forwardDot >= HeadingScoutMinForwardDot;
    }

    private static float GetForwardDot(Vector3 playerPosition, Vector3 targetPosition, Vector3 heading)
    {
        var flatDelta = new Vector3(
            targetPosition.X - playerPosition.X,
            0f,
            targetPosition.Z - playerPosition.Z);
        if (flatDelta.LengthSquared() <= float.Epsilon || heading.LengthSquared() <= float.Epsilon)
            return 0f;

        return Vector3.Dot(Vector3.Normalize(flatDelta), Vector3.Normalize(heading));
    }

    private static string NormalizeName(string value)
        => string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private void GhostCurrentOrLastManualDestination(DungeonFrontierPoint? previousTarget, string reason)
    {
        var pointToGhost = previousTarget is { IsManualDestination: true }
            ? previousTarget
            : lastValidManualDestination;
        if (pointToGhost is null)
            return;

        if (!visitedFrontierKeys.Add(pointToGhost.Key))
        {
            ClearRememberedManualDestination(pointToGhost);
            return;
        }

        ClearRememberedManualDestination(pointToGhost);
        RememberGhostedManualDestination(pointToGhost, reason);
        log.Information($"[ADS] Ghosted {GetManualDestinationLabel(pointToGhost)} {pointToGhost.Name} at {FormatVector(pointToGhost.Position)} after {reason} transition.");
    }

    private static string FormatUnsafeTransitionFlags(DutyContextSnapshot context)
    {
        if (context.BetweenAreas && context.BetweenAreas51)
            return "BetweenAreas/BetweenAreas51";

        if (context.BetweenAreas51)
            return "BetweenAreas51";

        return "BetweenAreas";
    }

    private static string FormatTreasureRouteTransitFlags(DutyContextSnapshot context)
    {
        var flags = new List<string>();
        if (context.BetweenAreas)
            flags.Add("BetweenAreas");
        if (context.BetweenAreas51)
            flags.Add("BetweenAreas51");
        if (context.Jumping)
            flags.Add("Jumping");
        if (context.Jumping61)
            flags.Add("Jumping61");
        if (context.Occupied33)
            flags.Add("Occupied33");
        if (context.OccupiedInCutSceneEvent)
            flags.Add("OccupiedInCutSceneEvent");
        if (context.WatchingCutscene)
            flags.Add("WatchingCutscene");

        return flags.Count == 0 ? "TreasureRouteTransitHold" : string.Join("/", flags);
    }

    private void RememberGhostedManualDestination(DungeonFrontierPoint point, string reason)
    {
        LastGhostedManualDestination = point;
        LastGhostedManualDestinationUtc = DateTime.UtcNow;
        LastGhostedManualDestinationReason = reason;
    }

    private void RememberManualDestination(DungeonFrontierPoint? point)
    {
        if (point is { IsManualDestination: true })
            lastValidManualDestination = point;
    }

    private void ClearRememberedManualDestination(DungeonFrontierPoint point)
    {
        if (!point.IsManualDestination || lastValidManualDestination is null)
            return;

        if (string.Equals(lastValidManualDestination.Key, point.Key, StringComparison.Ordinal))
            lastValidManualDestination = null;
    }

    private bool HasNoFrontierBlockingLiveObjects(DutyContextSnapshot context, ObservationSnapshot observation, Vector3? playerPosition)
        => !observation.LiveMonsters.Any(x => IsSaneVerticalBlocker(playerPosition, x.Position))
           && !observation.LiveFollowTargets.Any(x => IsSaneVerticalBlocker(playerPosition, x.Position))
           && !observation.LiveInteractables.Any(x => IsEligibleFrontierBlockingInteractable(context, x, playerPosition));

    private bool ShouldPrioritizeManualDestinationBeforeLiveProgression(
        DutyContextSnapshot context,
        ObservationSnapshot observation,
        Vector3? playerPosition,
        IReadOnlyList<DungeonFrontierPoint> manualDestinations)
    {
        if (manualDestinations.Count == 0)
            return false;

        if (observation.LiveMonsters.Any(x => IsSaneVerticalBlocker(playerPosition, x.Position))
            || observation.LiveFollowTargets.Any(x => IsSaneVerticalBlocker(playerPosition, x.Position)))
        {
            return false;
        }

        var bestUnvisitedManualDestination = manualDestinations
            .Where(point => !visitedFrontierKeys.Contains(point.Key))
            .Select(point => new
            {
                Point = point,
                Distance = playerPosition.HasValue ? GetManualDestinationDistance(playerPosition.Value, point) : float.MaxValue,
                VerticalDelta = playerPosition.HasValue ? MathF.Abs(point.Position.Y - playerPosition.Value.Y) : float.MaxValue,
            })
            .OrderBy(x => x.Point.Priority)
            .ThenBy(x => x.Distance)
            .ThenBy(x => x.VerticalDelta)
            .ThenBy(x => x.Point.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (bestUnvisitedManualDestination is null)
            return false;

        var bestLiveProgressionInteractable = observation.LiveInteractables
            .Where(x => IsEligibleFrontierBlockingInteractable(context, x, playerPosition))
            .Select(x => new
            {
                Interactable = x,
                Distance = playerPosition.HasValue ? Vector3.Distance(playerPosition.Value, x.Position) : (float?)null,
                VerticalDelta = playerPosition.HasValue ? MathF.Abs(x.Position.Y - playerPosition.Value.Y) : (float?)null,
            })
            .Select(x => new
            {
                x.Interactable,
                x.Distance,
                x.VerticalDelta,
                Priority = objectPriorityRuleService.GetEffectivePriority(context, x.Interactable, x.Distance, x.VerticalDelta),
            })
            .OrderBy(x => x.Priority)
            .ThenBy(x => x.Distance ?? float.MaxValue)
            .ThenBy(x => x.VerticalDelta ?? float.MaxValue)
            .FirstOrDefault();
        return bestLiveProgressionInteractable is not null
               && ShouldManualDestinationBeatProgressionInteractable(
                   bestUnvisitedManualDestination.Point,
                   bestLiveProgressionInteractable.Interactable,
                   bestLiveProgressionInteractable.Priority);
    }

    private DungeonFrontierPoint? SelectCombatBypassManualDestinationAgainstLiveBlockers(
        DutyContextSnapshot context,
        ObservationSnapshot observation,
        Vector3? playerPosition,
        IReadOnlyList<DungeonFrontierPoint> manualDestinations)
    {
        var bestManualDestination = manualDestinations
            .Where(point => point.AllowCombatBypass && !visitedFrontierKeys.Contains(point.Key))
            .Select(point => new
            {
                Point = point,
                Distance = playerPosition.HasValue ? GetManualDestinationDistance(playerPosition.Value, point) : float.MaxValue,
                VerticalDelta = playerPosition.HasValue ? MathF.Abs(point.Position.Y - playerPosition.Value.Y) : float.MaxValue,
            })
            .OrderBy(x => x.Point.Priority)
            .ThenBy(x => x.Distance)
            .ThenBy(x => x.VerticalDelta)
            .ThenBy(x => x.Point.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (bestManualDestination is null)
            return null;

        if (observation.LiveFollowTargets.Any(x => IsSaneVerticalBlocker(playerPosition, x.Position)))
            return null;

        var bestMonster = observation.LiveMonsters
            .Where(x => IsSaneVerticalBlocker(playerPosition, x.Position))
            .Select(x => new
            {
                Monster = x,
                Distance = playerPosition.HasValue ? Vector3.Distance(playerPosition.Value, x.Position) : (float?)null,
                VerticalDelta = playerPosition.HasValue ? MathF.Abs(x.Position.Y - playerPosition.Value.Y) : (float?)null,
            })
            .Select(x => new
            {
                x.Monster,
                x.Distance,
                x.VerticalDelta,
                Priority = objectPriorityRuleService.GetEffectiveBattleNpcPriority(context, x.Monster, x.Distance, x.VerticalDelta),
            })
            .OrderBy(x => x.Priority)
            .ThenBy(x => x.Distance ?? float.MaxValue)
            .ThenBy(x => x.VerticalDelta ?? float.MaxValue)
            .FirstOrDefault();
        if (bestMonster is null)
            return null;

        if (bestManualDestination.Point.Priority > bestMonster.Priority)
            return null;

        var bestLiveProgressionInteractable = observation.LiveInteractables
            .Where(x => IsEligibleFrontierBlockingInteractable(context, x, playerPosition))
            .Select(x => new
            {
                Interactable = x,
                Distance = playerPosition.HasValue ? Vector3.Distance(playerPosition.Value, x.Position) : (float?)null,
                VerticalDelta = playerPosition.HasValue ? MathF.Abs(x.Position.Y - playerPosition.Value.Y) : (float?)null,
            })
            .Select(x => new
            {
                x.Interactable,
                x.Distance,
                x.VerticalDelta,
                Priority = objectPriorityRuleService.GetEffectivePriority(context, x.Interactable, x.Distance, x.VerticalDelta),
            })
            .OrderBy(x => x.Priority)
            .ThenBy(x => x.Distance ?? float.MaxValue)
            .ThenBy(x => x.VerticalDelta ?? float.MaxValue)
            .FirstOrDefault();
        if (bestLiveProgressionInteractable is not null
            && !ShouldManualDestinationBeatProgressionInteractable(
                bestManualDestination.Point,
                bestLiveProgressionInteractable.Interactable,
                bestLiveProgressionInteractable.Priority))
        {
            return null;
        }

        var selectionLogKey = string.Create(
            CultureInfo.InvariantCulture,
            $"{bestManualDestination.Point.Key}:{bestMonster.Monster.Name}:{bestMonster.Priority}:{bestLiveProgressionInteractable?.Interactable.Name}:{bestLiveProgressionInteractable?.Priority}:{bestLiveProgressionInteractable?.Interactable.Classification}");
        if (loggedCombatBypassManualSelections.Add(selectionLogKey))
        {
            var manualLabel = bestManualDestination.Point.IsManualXyzDestination ? "XYZ" : "map XZ";
            var progressSummary = bestLiveProgressionInteractable is null
                ? "no stronger live progression blocker was visible"
                : $"live progression interactable {bestLiveProgressionInteractable.Interactable.Name} ({bestLiveProgressionInteractable.Interactable.Classification}) at {FormatVector(bestLiveProgressionInteractable.Interactable.Position)} on map {bestLiveProgressionInteractable.Interactable.MapId} resolved at priority {bestLiveProgressionInteractable.Priority} and was intentionally ignored because {DescribeManualDestinationProgressionOverride(bestManualDestination.Point, bestLiveProgressionInteractable.Interactable, bestLiveProgressionInteractable.Priority)}";
            log.Information(
                $"[ADS] Selected force-march {manualLabel} destination {bestManualDestination.Point.Name} at {FormatVector(bestManualDestination.Point.Position)} while live monster pressure remains because manual priority {bestManualDestination.Point.Priority} beat/tied live monster {bestMonster.Monster.Name} ({bestMonster.Priority}) and {progressSummary}.");
        }

        return BuildNavigationPoint(bestManualDestination.Point, playerPosition);
    }

    private DungeonFrontierPoint? SelectPraetoriumOnFootCombatBypassManualDestination(
        DutyContextSnapshot context,
        ObservationSnapshot observation,
        Vector3? playerPosition,
        IReadOnlyList<DungeonFrontierPoint> manualDestinations,
        DungeonFrontierPoint? previousTarget)
    {
        //if (context.Mounted || context.TerritoryTypeId != PraetoriumTerritoryTypeId)
        if (context.Mounted) //because the AI was too stupid to understand
            return null;

        if (!context.InCombat
            && !observation.LiveMonsters.Any(x => IsSaneVerticalBlocker(playerPosition, x.Position)))
        {
            return null;
        }

        if (observation.LiveFollowTargets.Any(x => IsSaneVerticalBlocker(playerPosition, x.Position)))
            return null;

        var currentForceMarchAnchor = GetCurrentAuthoredForceMarchAnchor(manualDestinations, previousTarget);
        if (currentForceMarchAnchor is not null)
            return BuildNavigationPoint(currentForceMarchAnchor, playerPosition);

        var inferredAnchorFromPlayerPosition = false;
        if (!TryGetAuthoredManualAnchorIndex(manualDestinations, previousTarget, out var anchorIndex, out var anchorPoint))
        {
            if (!TryInferPraetoriumOnFootSpatialAnchor(manualDestinations, playerPosition, out anchorIndex, out anchorPoint))
                return null;

            inferredAnchorFromPlayerPosition = true;
            if (anchorPoint.AllowCombatBypass && !visitedFrontierKeys.Contains(anchorPoint.Key))
            {
                var spatialSelectionLogKey = $"praetorium-onfoot-spatial:{anchorPoint.Key}";
                if (loggedCombatBypassManualSelections.Add(spatialSelectionLogKey))
                {
                    var manualLabel = anchorPoint.IsManualXyzDestination ? "XYZ" : "map XZ";
                    var liveMonsterSummary = observation.LiveMonsters.Count == 0
                        ? "local combat state was active without visible live monsters"
                        : $"{observation.LiveMonsters.Count} live monster(s) were visible";
                    log.Information(
                        $"[ADS] Praetorium on-foot authored force-march fallback selected force-march {manualLabel} destination {anchorPoint.Name} at {FormatVector(anchorPoint.Position)} by inferring the nearest authored anchor from the current player position because no prior Praetorium manual anchor was remembered; {liveMonsterSummary}. ADS is preserving the immediate authored handoff instead of yielding to the generic combat-bypass selector.");
                }

                return BuildNavigationPoint(anchorPoint, playerPosition);
            }
        }

        var nextUnvisitedManualDestination = manualDestinations
            .Skip(anchorIndex + 1)
            .FirstOrDefault(point => !visitedFrontierKeys.Contains(point.Key));
        if (nextUnvisitedManualDestination is not { AllowCombatBypass: true } nextForceMarchDestination)
            return null;

        var selectionLogKey = inferredAnchorFromPlayerPosition
            ? $"praetorium-onfoot-spatial:{anchorPoint.Key}:{nextForceMarchDestination.Key}"
            : $"praetorium-onfoot:{anchorPoint.Key}:{nextForceMarchDestination.Key}";
        if (loggedCombatBypassManualSelections.Add(selectionLogKey))
        {
            var manualLabel = nextForceMarchDestination.IsManualXyzDestination ? "XYZ" : "map XZ";
            var liveMonsterSummary = observation.LiveMonsters.Count == 0
                ? "local combat state was active without visible live monsters"
                : $"{observation.LiveMonsters.Count} live monster(s) were visible";
            var anchorSummary = inferredAnchorFromPlayerPosition
                ? $"after inferring {anchorPoint.Name} as the nearest authored Praetorium anchor from the current player position because no prior manual anchor was remembered"
                : $"immediately after {anchorPoint.Name} completed";
            log.Information(
                $"[ADS] Praetorium on-foot authored force-march handoff promoted force-march {manualLabel} destination {nextForceMarchDestination.Name} at {FormatVector(nextForceMarchDestination.Position)} {anchorSummary}; {liveMonsterSummary}. ADS is replicating the mounted force-march behavior for the next authored segment instead of yielding to incidental combat.");
        }

        return BuildNavigationPoint(nextForceMarchDestination, playerPosition);
    }

    private DungeonFrontierPoint? SelectPraetoriumMountedCombatBypassManualDestination(
        DutyContextSnapshot context,
        ObservationSnapshot observation,
        Vector3? playerPosition,
        IReadOnlyList<DungeonFrontierPoint> manualDestinations)
    {
        //if (!context.Mounted || context.TerritoryTypeId != PraetoriumTerritoryTypeId)
        if (!context.Mounted) //because the AI was too stupid to understand
            return null;

        if (!context.InCombat
            && !observation.LiveMonsters.Any(x => IsSaneVerticalBlocker(playerPosition, x.Position)))
        {
            return null;
        }

        if (observation.LiveFollowTargets.Any(x => IsSaneVerticalBlocker(playerPosition, x.Position)))
            return null;

        var bestManualDestination = manualDestinations
            .Where(point => point.AllowCombatBypass && !visitedFrontierKeys.Contains(point.Key))
            .Select(point => new
            {
                Point = point,
                Distance = playerPosition.HasValue ? GetManualDestinationDistance(playerPosition.Value, point) : float.MaxValue,
                VerticalDelta = playerPosition.HasValue ? MathF.Abs(point.Position.Y - playerPosition.Value.Y) : float.MaxValue,
            })
            .OrderBy(x => x.Point.Priority)
            .ThenBy(x => x.Distance)
            .ThenBy(x => x.VerticalDelta)
            .ThenBy(x => x.Point.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (bestManualDestination is null)
            return null;

        var selectionLogKey = $"mounted:{bestManualDestination.Point.Key}";
        if (loggedCombatBypassManualSelections.Add(selectionLogKey))
        {
            var manualLabel = bestManualDestination.Point.IsManualXyzDestination ? "XYZ" : "map XZ";
            var liveMonsterSummary = observation.LiveMonsters.Count == 0
                ? "no live monsters were present, but local mounted combat was active"
                : $"{observation.LiveMonsters.Count} live monster(s) were visible";
            var liveInteractableSummary = observation.LiveInteractables.Count == 0
                ? "no live interactables"
                : $"{observation.LiveInteractables.Count} live interactable(s)";
            log.Information(
                $"[ADS] Praetorium mounted combat fallback selected force-march {manualLabel} destination {bestManualDestination.Point.Name} at {FormatVector(bestManualDestination.Point.Position)} with manual priority {bestManualDestination.Point.Priority}; {liveMonsterSummary} and {liveInteractableSummary}. ADS is preserving the broader authored force-march handoff while mounted combat is active.");
        }

        return BuildNavigationPoint(bestManualDestination.Point, playerPosition);
    }

    private DungeonFrontierPoint? GetCurrentAuthoredForceMarchAnchor(
        IReadOnlyList<DungeonFrontierPoint> manualDestinations,
        DungeonFrontierPoint? previousTarget)
    {
        foreach (var candidate in EnumerateManualAnchorCandidates(previousTarget, includeGhosted: false))
        {
            if (candidate is not { AllowCombatBypass: true })
                continue;

            if (visitedFrontierKeys.Contains(candidate.Key))
                continue;

            var currentPoint = manualDestinations.FirstOrDefault(point => string.Equals(point.Key, candidate.Key, StringComparison.Ordinal));
            if (currentPoint is not null)
                return currentPoint;
        }

        return null;
    }

    private bool TryGetAuthoredManualAnchorIndex(
        IReadOnlyList<DungeonFrontierPoint> manualDestinations,
        DungeonFrontierPoint? previousTarget,
        out int anchorIndex,
        out DungeonFrontierPoint anchorPoint)
    {
        foreach (var candidate in EnumerateManualAnchorCandidates(previousTarget, includeGhosted: true))
        {
            anchorIndex = manualDestinations
                .Select((point, index) => new { Point = point, Index = index })
                .Where(x => string.Equals(x.Point.Key, candidate.Key, StringComparison.Ordinal))
                .Select(x => x.Index)
                .FirstOrDefault(-1);
            if (anchorIndex >= 0)
            {
                anchorPoint = manualDestinations[anchorIndex];
                return true;
            }
        }

        anchorIndex = -1;
        anchorPoint = default!;
        return false;
    }

    private bool TryInferPraetoriumOnFootSpatialAnchor(
        IReadOnlyList<DungeonFrontierPoint> manualDestinations,
        Vector3? playerPosition,
        out int anchorIndex,
        out DungeonFrontierPoint anchorPoint)
    {
        anchorIndex = -1;
        anchorPoint = default!;
        if (!playerPosition.HasValue || manualDestinations.Count == 0)
            return false;

        var nearestCandidate = manualDestinations
            .Select((point, index) => new
            {
                Point = point,
                Index = index,
                IsVisited = visitedFrontierKeys.Contains(point.Key),
                Distance = GetManualDestinationDistance(playerPosition.Value, point),
                VerticalDelta = MathF.Abs(point.Position.Y - playerPosition.Value.Y),
            })
            .OrderBy(x => x.IsVisited)
            .ThenBy(x => x.Distance)
            .ThenBy(x => x.VerticalDelta)
            .ThenBy(x => x.Index)
            .FirstOrDefault();
        if (nearestCandidate is null)
            return false;

        anchorIndex = nearestCandidate.Index;
        anchorPoint = nearestCandidate.Point;
        return true;
    }

    private IEnumerable<DungeonFrontierPoint> EnumerateManualAnchorCandidates(DungeonFrontierPoint? previousTarget, bool includeGhosted)
    {
        if (previousTarget is { IsManualDestination: true })
            yield return previousTarget;

        if (lastValidManualDestination is { IsManualDestination: true } rememberedManualDestination
            && !string.Equals(previousTarget?.Key, rememberedManualDestination.Key, StringComparison.Ordinal))
        {
            yield return rememberedManualDestination;
        }

        if (includeGhosted
            && LastGhostedManualDestination is { IsManualDestination: true } ghostedManualDestination
            && !string.Equals(previousTarget?.Key, ghostedManualDestination.Key, StringComparison.Ordinal)
            && !string.Equals(lastValidManualDestination?.Key, ghostedManualDestination.Key, StringComparison.Ordinal))
        {
            yield return ghostedManualDestination;
        }
    }

    private static bool ShouldManualDestinationBeatProgressionInteractable(
        DungeonFrontierPoint manualDestination,
        ObservedInteractable interactable,
        int interactablePriority)
    {
        if (manualDestination.Priority < interactablePriority)
            return true;

        if (manualDestination.Priority > interactablePriority)
            return false;

        return IsLowerValueProgressionInteractable(interactable);
    }

    private static string DescribeManualDestinationProgressionOverride(
        DungeonFrontierPoint manualDestination,
        ObservedInteractable interactable,
        int interactablePriority)
    {
        if (manualDestination.Priority < interactablePriority)
        {
            return $"manual priority {manualDestination.Priority} beat the live interactable priority {interactablePriority}";
        }

        if (interactable.Classification is InteractableClass.Expendable or InteractableClass.Optional)
        {
            return $"the tie at priority {interactablePriority} was spent on the authored manual waypoint instead of the lower-value {interactable.Classification} interactable";
        }

        return $"manual priority {manualDestination.Priority} beat the live {interactable.Classification} interactable priority {interactablePriority}";
    }

    private static bool IsLowerValueProgressionInteractable(ObservedInteractable interactable)
        => interactable.Classification is InteractableClass.Expendable or InteractableClass.Optional;

    private static bool IsSaneVerticalBlocker(Vector3? playerPosition, Vector3 targetPosition)
        => !playerPosition.HasValue
           || MathF.Abs(targetPosition.Y - playerPosition.Value.Y) <= FrontierBlockingVerticalSanityCap;

    private static bool MatchesCurrentMap(uint activeMapId, uint candidateMapId)
        => activeMapId == 0
           || candidateMapId == 0
           || activeMapId == candidateMapId;

    private static string Quantize(Vector3 value)
        => $"{MathF.Round(value.X, 0):0},{MathF.Round(value.Y, 0):0},{MathF.Round(value.Z, 0):0}";

    private static uint BuildLabelSortOrder(MapLabelMarker label)
        => ((label.MapId & 0xFFFF) << 16) | label.SubrowId;

    private static uint GetDutyKey(DutyContextSnapshot context)
        => context.ContentFinderConditionId != 0 ? context.ContentFinderConditionId : context.TerritoryTypeId;

    private static DungeonFrontierPoint BuildNavigationPoint(DungeonFrontierPoint point, Vector3? playerPosition)
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
            UsePlayerYForNavigation = true,
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

    private bool TryResolveActiveMap(DutyContextSnapshot context, out Map map, out string status)
    {
        map = default;
        var mapSheet = dataManager.GetExcelSheet<Map>();
        var territorySheet = dataManager.GetExcelSheet<TerritoryType>();
        if (mapSheet is null || territorySheet is null)
        {
            status = $"Map-row lookup failed for territory {context.TerritoryTypeId}: one or more sheets were unavailable.";
            return false;
        }

        if (context.MapId != 0
            && mapSheet.TryGetRow(context.MapId, out map)
            && map.TerritoryType.RowId == context.TerritoryTypeId)
        {
            var mapSelectionKey = $"{context.TerritoryTypeId}:{map.RowId}";
            if (loggedActiveMapSelections.Add(mapSelectionKey))
            {
                log.Information($"[ADS] Using live map row {map.RowId} ({BuildMapName(map)}) for territory {context.TerritoryTypeId}; frontier labels, map flags, and manual destination resolution are restricted to this sub-area.");
            }

            status = $"Using live map row {map.RowId} ({BuildMapName(map)}) for frontier labels.";
            return true;
        }

        if (territorySheet.TryGetRow(context.TerritoryTypeId, out var territory)
            && territory.Map.RowId != 0
            && mapSheet.TryGetRow(territory.Map.RowId, out map))
        {
            status = $"Live map id {context.MapId} was unavailable; falling back to territory default map row {map.RowId} ({BuildMapName(map)}).";
            return true;
        }

        foreach (var candidate in mapSheet.Where(x => x.TerritoryType.RowId == context.TerritoryTypeId).OrderBy(x => x.RowId))
        {
            map = candidate;
            status = $"Live map id {context.MapId} was unavailable; falling back to first territory map row {map.RowId} ({BuildMapName(map)}).";
            return true;
        }

        status = $"Map-row lookup failed: territory {context.TerritoryTypeId} had no associated map rows.";
        return false;
    }

    private static bool TryParseMapCoordinates(string value, out Vector2 coordinates)
    {
        coordinates = default;
        var parts = value.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
            return false;

        if (!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x))
            return false;

        if (!float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
            return false;

        coordinates = new Vector2(x, z);
        return true;
    }

    private static bool TryParseWorldCoordinates(string value, out Vector3 coordinates)
    {
        coordinates = default;
        var parts = value.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 3)
            return false;

        if (!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x))
            return false;

        if (!float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
            return false;

        if (!float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
            return false;

        coordinates = new Vector3(x, y, z);
        return true;
    }

    private static Vector3 ConvertMapCoordinatesToWorld(Vector2 mapCoordinates, Map map, float currentPlayerY)
        => new(
            ConvertMapCoordinateToWorld(mapCoordinates.X, map.SizeFactor, map.OffsetX),
            currentPlayerY,
            ConvertMapCoordinateToWorld(mapCoordinates.Y, map.SizeFactor, map.OffsetY));

    private static bool DoesManualDestinationRuleMatchActiveMap(ObjectPriorityRule rule, Map map)
    {
        var selector = GetMapLayerSelector(rule);
        if (string.IsNullOrWhiteSpace(selector))
            return true;

        if (uint.TryParse(selector, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mapRowId))
            return mapRowId == map.RowId;

        var activeMapName = BuildMapName(map);
        if (string.Equals(selector, activeMapName, StringComparison.OrdinalIgnoreCase))
            return true;

        var placeName = NormalizeName(map.PlaceName.ValueNullable?.Name.ToString() ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(placeName)
            && string.Equals(selector, placeName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var placeNameSub = NormalizeName(map.PlaceNameSub.ValueNullable?.Name.ToString() ?? string.Empty);
        return !string.IsNullOrWhiteSpace(placeNameSub)
               && string.Equals(selector, placeNameSub, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeMapLayerSelector(string? value)
    {
        var selector = NormalizeName(value ?? string.Empty);
        return string.Equals(selector, "MapXZ", StringComparison.OrdinalIgnoreCase)
               || string.Equals(selector, "XYZ", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : selector;
    }

    private static string GetMapLayerSelector(ObjectPriorityRule rule)
    {
        var explicitLayer = NormalizeMapLayerSelector(rule.Layer);
        if (!string.IsNullOrWhiteSpace(explicitLayer))
            return explicitLayer;

        return NormalizeMapLayerSelector(rule.DestinationType);
    }

    private static float ConvertMapCoordinateToWorld(float mapCoordinate, uint scale, int offset)
    {
        var mapScale = scale / 100f;
        if (mapScale <= float.Epsilon)
            return 0f;

        var textureCoordinate = (((mapCoordinate - 1f) * mapScale) / 41f) * 2048f;
        return (textureCoordinate - (offset + 1024f)) / mapScale;
    }

    private static string BuildMapXzDestinationRuleKey(DutyContextSnapshot context, ObjectPriorityRule rule)
        => $"map-xz:{context.ContentFinderConditionId}:{context.TerritoryTypeId}:{context.MapId}:{rule.Priority}:{rule.ObjectName}:{GetMapLayerSelector(rule)}:{rule.MapCoordinates}";

    private static string BuildXyzDestinationRuleKey(DutyContextSnapshot context, ObjectPriorityRule rule)
        => $"xyz:{context.ContentFinderConditionId}:{context.TerritoryTypeId}:{context.MapId}:{rule.Priority}:{rule.ObjectName}:{GetMapLayerSelector(rule)}:{rule.WorldCoordinates}";

    private static ulong BuildCacheKey(uint territoryTypeId, uint mapId)
        => ((ulong)territoryTypeId << 32) | mapId;

    private void LogMapXzDestinationWarning(string key, string message)
    {
        if (loggedInvalidMapXzDestinationRules.Add(key))
            log.Warning(message);
    }

    private void LogXyzDestinationWarning(string key, string message)
    {
        if (loggedInvalidXyzDestinationRules.Add(key))
            log.Warning(message);
    }

    private static Vector3 ConvertTextureToWorld(short textureX, short textureY, Map map)
    {
        var scale = map.SizeFactor / 100f;
        if (scale <= float.Epsilon)
            return Vector3.Zero;

        var offsetX = map.OffsetX + 1024f;
        var offsetY = map.OffsetY + 1024f;
        return new Vector3(
            (textureX - offsetX) / scale,
            0f,
            (textureY - offsetY) / scale);
    }

    private static string BuildMapName(Map map)
    {
        var placeName = NormalizeName(map.PlaceName.ValueNullable?.Name.ToString() ?? string.Empty);
        var placeNameSub = NormalizeName(map.PlaceNameSub.ValueNullable?.Name.ToString() ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(placeNameSub))
            return placeNameSub;

        if (!string.IsNullOrWhiteSpace(placeName))
            return placeName;

        return $"Map {map.RowId}";
    }

    private static string FormatVector(Vector3 value)
        => string.Create(CultureInfo.InvariantCulture, $"{value.X:0.00},{value.Y:0.00},{value.Z:0.00}");

    private bool IsEligibleFrontierBlockingInteractable(DutyContextSnapshot context, ObservedInteractable interactable, Vector3? playerPosition)
    {
        if (interactable.Classification is not (InteractableClass.Required or InteractableClass.CombatFriendly or InteractableClass.Expendable or InteractableClass.TreasureDoor))
            return false;

        if (TreasureDungeonRole == ADS.Models.TreasureDungeonRole.Follower
            && interactable.Classification == InteractableClass.TreasureDoor)
        {
            return false;
        }

        var distance = playerPosition.HasValue
            ? Vector3.Distance(playerPosition.Value, interactable.Position)
            : (float?)null;
        var verticalDelta = playerPosition.HasValue
            ? MathF.Abs(interactable.Position.Y - playerPosition.Value.Y)
            : (float?)null;
        return !objectPriorityRuleService.IsSuppressedByRuleGates(context, interactable, distance, verticalDelta);
    }

    private DungeonFrontierPoint? GetHeldTreasureFollowerCandidate(IReadOnlyList<DungeonFrontierPoint> points)
    {
        if (heldTreasureFollowerCandidateKey is null)
            return null;

        var heldCandidate = points.FirstOrDefault(point => string.Equals(point.Key, heldTreasureFollowerCandidateKey, StringComparison.Ordinal));
        if (heldCandidate is null)
        {
            var storedHeldCandidate = heldTreasureFollowerCandidatePoint;
            if (storedHeldCandidate is not null
                && IsStoredHeldTreasureFollowerCandidateStillValid(storedHeldCandidate, points))
            {
                LogTreasureFollowerHeldCandidatePreserved(storedHeldCandidate, points);
                return storedHeldCandidate;
            }

            ClearTreasureFollowerCandidateHold("held candidate left active route");
        }

        return heldCandidate;
    }

    private bool IsHeldTreasureFollowerCandidate(DungeonFrontierPoint point)
        => heldTreasureFollowerCandidateKey is not null
           && (string.Equals(point.Key, heldTreasureFollowerCandidateKey, StringComparison.Ordinal)
               || (heldTreasureFollowerCandidatePoint is not null
                   && IsSameTreasureFollowerPassageGroup(point, heldTreasureFollowerCandidatePoint)));

    private bool IsStoredHeldTreasureFollowerCandidateStillValid(
        DungeonFrontierPoint candidate,
        IReadOnlyList<DungeonFrontierPoint> points)
    {
        if (!candidate.IsTreasurePassageCandidate
            || candidate.TreasureRoomIndex <= 0
            || IsTreasureFollowerRoomBehindReachedFloor(candidate.TreasureRoomIndex)
            || (treasureFollowerTransitConsumedRoomIndex > 0 && candidate.TreasureRoomIndex <= treasureFollowerTransitConsumedRoomIndex)
            || IsTreasureFollowerPassageCandidateVisited(candidate, points))
        {
            return false;
        }

        return points.Any(point => IsSameTreasureFollowerPassageGroup(point, candidate));
    }

    private bool IsTreasureFollowerPassageCandidateVisited(
        DungeonFrontierPoint candidate,
        IReadOnlyList<DungeonFrontierPoint> points)
    {
        if (IsTreasureFollowerPassageGroupFailed(candidate))
            return true;

        if (visitedFrontierKeys.Contains(candidate.Key))
            return true;

        return points.Any(point => IsSameTreasureFollowerPassageGroup(point, candidate)
                                   && visitedFrontierKeys.Contains(point.Key));
    }

    private void LogTreasureFollowerHeldCandidatePreserved(
        DungeonFrontierPoint candidate,
        IReadOnlyList<DungeonFrontierPoint> points)
    {
        var logKey = $"{activeDutyKey}:{candidate.Key}:{candidate.TreasureRoomIndex}:{candidate.TreasurePassageGroup}";
        if (!loggedTreasureFollowerHeldCandidatePreserves.Add(logKey))
            return;

        var replacement = points.FirstOrDefault(point => IsSameTreasureFollowerPassageGroup(point, candidate));
        var replacementText = replacement is null
            ? "no same-group route point"
            : $"{replacement.Name} ({replacement.TreasureRouteSource}, key {replacement.Key})";
        var candidateLabel = candidate.IsLiveTreasureDoorCandidate ? "held live" : "held";
        log.Information(
            $"[ADS] Treasure follower preserved {candidateLabel} passage candidate {candidate.Name} ({candidate.TreasurePassageGroup}, room {candidate.TreasureRoomIndex}) across live route refresh; active route replacement is {replacementText}.");
    }

    private static bool IsSameTreasureFollowerPassageGroup(DungeonFrontierPoint left, DungeonFrontierPoint right)
        => left.TreasureRoomIndex > 0
           && right.TreasureRoomIndex > 0
           && left.TreasureRoomIndex == right.TreasureRoomIndex
           && string.Equals(left.TreasurePassageGroup, right.TreasurePassageGroup, StringComparison.Ordinal);

    private static float GetManualDestinationDistance(Vector3 playerPosition, DungeonFrontierPoint point)
        => point.IsManualXyzDestination
            ? Vector3.Distance(playerPosition, point.Position)
            : GetHorizontalDistance(playerPosition, point.Position);

    private static bool AllowsCombatBypass(ObjectPriorityRule rule)
        => Enum.TryParse<InteractableClass>(rule.Classification, ignoreCase: true, out var classification)
           && classification is InteractableClass.MapXzForceMarch or InteractableClass.XYZForceMarch;

    private static string GetManualDestinationLabel(DungeonFrontierPoint point)
        => point.IsManualXyzDestination
            ? point.AllowCombatBypass ? "force-march XYZ destination" : "XYZ destination"
            : point.AllowCombatBypass ? "force-march map XZ destination" : "map XZ destination";

    private static string EscapeLogText(string value)
        => (value ?? string.Empty)
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);

    private static float GetHorizontalDistance(Vector3 a, Vector3 b)
    {
        var x = a.X - b.X;
        var z = a.Z - b.Z;
        return MathF.Sqrt((x * x) + (z * z));
    }

    private static Vector3 AveragePosition(IEnumerable<Vector3> positions)
    {
        var sum = Vector3.Zero;
        var count = 0;
        foreach (var position in positions)
        {
            sum += position;
            count++;
        }

        return count == 0 ? Vector3.Zero : sum / count;
    }
}
