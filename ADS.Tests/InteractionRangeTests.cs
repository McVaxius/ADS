using ADS.Services;
using ADS.Models;
using System.Reflection;
using System.Numerics;

namespace ADS.Tests;

public sealed class InteractionRangeTests
{
    [Theory]
    [InlineData(5, 20)]
    [InlineData(2, 60)]
    public void CutsceneResumesTheExactRemainingInteractionBudget(int remainingSeconds, int cutsceneSeconds)
    {
        using var fixture = new HyperFixatedTargetExecutionTests.Fixture(Vector3.Zero);
        var now = DateTime.UtcNow;
        fixture.Execution.ReadUtcNow = () => now;
        var normal = TestDutyContextFactory.Create();
        ArmSentInteraction(fixture.Execution, normal);
        WritePrivateField(fixture.Execution, "interactionDispatchSettleUntilUtc", now.AddSeconds(remainingSeconds));
        WritePrivateField(fixture.Execution, "navigationActive", true);
        var nextPull = fixture.AddMonster(81, "Next pull", new Vector3(20, 0, 0));
        var planner = HyperFixatedTargetExecutionTests.HyperPlanner(nextPull);

        fixture.Update(planner, TestDutyContextFactory.Create(cutscene: true));
        now = now.AddSeconds(cutsceneSeconds);
        fixture.Update(planner, TestDutyContextFactory.Create(watchingCutscene: true));

        Assert.Single(fixture.Commands, command => command == "/vnav stop");
        Assert.Equal(0, fixture.AcquireCalls);
        Assert.False(fixture.Execution.RefreshCutsceneHold(normal, true));
        Assert.True(fixture.Execution.RefreshInteractionActionHold(normal, true, false, now));
        now = now.AddSeconds(remainingSeconds).AddTicks(-1);
        Assert.True(fixture.Execution.RefreshInteractionActionHold(normal, true, false, now));
        now = now.AddTicks(1);
        Assert.False(fixture.Execution.RefreshInteractionActionHold(normal, true, false, now));

        fixture.Update(planner, normal);
        Assert.Equal(1, fixture.AcquireCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LongCutsceneDoesNotSpendOptionalCofferBudgets(bool routeMaximum)
    {
        using var fixture = new HyperFixatedTargetExecutionTests.Fixture(Vector3.Zero, includeObservationMemory: true);
        var now = DateTime.UtcNow;
        fixture.Execution.ReadUtcNow = () => now;
        var coffer = new ObservedInteractable
        {
            Key = "coffer:82", GameObjectId = 82, DataId = 100, MapId = 1,
            ObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Treasure,
            Name = "Treasure Coffer", Position = new Vector3(40, 0, 0), LastSeenUtc = now,
            Classification = InteractableClass.TreasureCoffer, GhostReason = GhostReason.SeenPreviously,
        };
        WritePrivateField(fixture.Execution, "treasureCofferRouteKey", coffer.Key);
        WritePrivateField(fixture.Execution, "treasureCofferRouteStartedUtc", now.AddSeconds(routeMaximum ? -74 : 0));
        WritePrivateField(fixture.Execution, "treasureCofferLastProgressUtc", now.AddSeconds(routeMaximum ? 0 : -29));
        WritePrivateField(fixture.Execution, "treasureCofferBestHorizontalDistance", 40f);

        Assert.True(fixture.Execution.RefreshCutsceneHold(TestDutyContextFactory.Create(cutscene: true), true));
        now = now.AddSeconds(120);
        Assert.True(fixture.Execution.RefreshCutsceneHold(TestDutyContextFactory.Create(watchingCutscene: true), true));
        Assert.False(fixture.Execution.RefreshCutsceneHold(TestDutyContextFactory.Create(), true));

        bool TrySkip() => (bool)typeof(ExecutionService)
            .GetMethod("TrySkipStuckTreasureCoffer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(fixture.Execution, [coffer, 40f, "Test."])!;
        Assert.False(TrySkip());
        now = now.AddSeconds(1).AddTicks(-1);
        Assert.False(TrySkip());
        now = now.AddTicks(2);
        Assert.True(TrySkip());
        Assert.True(fixture.Memory!.ClearTreasureCofferSuppression(coffer.Key, "test verifies the skip"));
    }

    [Fact]
    public void StopDuringCutsceneDoesNotResumeOwnedWork()
    {
        using var fixture = new HyperFixatedTargetExecutionTests.Fixture(Vector3.Zero);
        var now = DateTime.UtcNow;
        fixture.Execution.ReadUtcNow = () => now;
        var normal = TestDutyContextFactory.Create();
        ArmSentInteraction(fixture.Execution, normal);
        var target = fixture.AddMonster(83, "Next pull", new Vector3(20, 0, 0));
        var planner = HyperFixatedTargetExecutionTests.HyperPlanner(target);
        fixture.Update(planner, TestDutyContextFactory.Create(cutscene: true));
        now = now.AddSeconds(10);

        fixture.Execution.Stop(TestDutyContextFactory.Create(cutscene: true));
        now = now.AddMinutes(1);
        fixture.Update(planner, normal);

        Assert.Equal(OwnershipMode.Observing, fixture.Execution.CurrentMode);
        Assert.Equal(0, fixture.AcquireCalls);
        Assert.False(ReadPrivateField<bool>(fixture.Execution, "interactionAttemptDispatched"));
        Assert.Null(fixture.Target);
    }

    [Fact]
    public void CompletionTimerCreatedDuringCutsceneGetsItsFullBudgetAfterResume()
    {
        using var fixture = new HyperFixatedTargetExecutionTests.Fixture(Vector3.Zero);
        var now = DateTime.UtcNow;
        fixture.Execution.ReadUtcNow = () => now;
        var cutscene = TestDutyContextFactory.Create(cutscene: true);
        Assert.True(fixture.Execution.RefreshCutsceneHold(cutscene, true));
        now = now.AddSeconds(10);

        Assert.True(fixture.Execution.BeginDutyCompletionTreasureSweep(cutscene, "Test Duty"));
        now = now.AddSeconds(30);
        Assert.True(fixture.Execution.RefreshCutsceneHold(cutscene, true));
        Assert.False(fixture.Execution.RefreshCutsceneHold(TestDutyContextFactory.Create(), true));
        var graceUntil = ReadPrivateField<DateTime>(fixture.Execution, "leaveTreasureSweepGraceUntilUtc");
        Assert.Equal(TimeSpan.FromSeconds(2), graceUntil - now);
        now = now.AddSeconds(1);
        Assert.True(fixture.Execution.BeginDutyCompletionTreasureSweep(TestDutyContextFactory.Create(), "Test Duty"));
        Assert.Equal(graceUntil, ReadPrivateField<DateTime>(fixture.Execution, "leaveTreasureSweepGraceUntilUtc"));
    }

    [Fact]
    public void RealZoningStillUsesItsExistingTransitionPath()
    {
        using var fixture = new HyperFixatedTargetExecutionTests.Fixture(Vector3.Zero);
        var now = DateTime.UtcNow;
        fixture.Execution.ReadUtcNow = () => now;
        Assert.True(fixture.Execution.RefreshCutsceneHold(TestDutyContextFactory.Create(cutscene: true), true));
        now = now.AddSeconds(10);
        var zoning = TestDutyContextFactory.Create(cutscene: true, betweenAreas: true);

        Assert.True(zoning.IsUnsafeTransition);
        Assert.False(fixture.Execution.RefreshCutsceneHold(zoning, true));
    }

    [Fact]
    public void CutsceneReleasesDirectionalInputWithoutConsumingItsRule()
    {
        using var fixture = new HyperFixatedTargetExecutionTests.Fixture(Vector3.Zero);
        var now = DateTime.UtcNow;
        fixture.Execution.ReadUtcNow = () => now;
        var rule = new ObjectPriorityRule
        {
            Classification = "CardinalHoldNorth", WorldCoordinates = "0,0", MaxDistance = 5,
            WaitAtDestinationSeconds = 5,
        };
        Assert.True(CardinalHoldPolicy.TryParse(rule, out var hold));
        WritePrivateField(fixture.Execution, "activeCardinalHold", (CardinalHoldRule?)hold);
        WritePrivateField(fixture.Execution, "activeCardinalHoldUntilUtc", now.AddSeconds(5));
        var cardinal = ReadPrivateField<CardinalHoldInputService>(fixture.Execution, "cardinalHoldInputService");
        var strafe = ReadPrivateField<TreasureDoorStrafeInputService>(fixture.Execution, "treasureDoorStrafeInputService");
        var diagnosticWrites = new List<(string Key, int Flags)>();
        strafe.WriteDiagnosticInputOverride = (key, state) => diagnosticWrites.Add((key.ToString(), (int)state));
        WriteEnumPrivateField(cardinal, "heldDirection", "Forward");
        WriteEnumPrivateField(cardinal, "heldKey", "W");
        WriteEnumPrivateField(strafe, "heldDirection", "Left");
        WriteEnumPrivateField(strafe, "heldKey", "A");
        var tracker = ReadPrivateField<CardinalHoldGhostTracker>(fixture.Execution, "cardinalHoldGhosts");

        Assert.True(fixture.Execution.RefreshCutsceneHold(TestDutyContextFactory.Create(cutscene: true), true));
        now = now.AddSeconds(30);
        Assert.True(fixture.Execution.RefreshCutsceneHold(TestDutyContextFactory.Create(watchingCutscene: true), true));
        Assert.Equal(RelativeMovementDirection.None, ReadPrivateField<RelativeMovementDirection>(cardinal, "heldDirection"));
        Assert.Equal("None", ReadPrivateField<object>(strafe, "heldDirection").ToString());
        Assert.Equal("NO_KEY", ReadPrivateField<object>(cardinal, "heldKey").ToString());
        Assert.Equal("NO_KEY", ReadPrivateField<object>(strafe, "heldKey").ToString());
        Assert.Equal(("A", 0), Assert.Single(diagnosticWrites));
        Assert.False(tracker.IsGhosted(hold.Key));
        Assert.NotNull(ReadPrivateField<CardinalHoldRule?>(fixture.Execution, "activeCardinalHold"));
        Assert.False(fixture.Execution.RefreshCutsceneHold(TestDutyContextFactory.Create(), true));
        Assert.Equal(TimeSpan.FromSeconds(5), ReadPrivateField<DateTime>(fixture.Execution, "activeCardinalHoldUntilUtc") - now);

        now = now.AddSeconds(5);
        typeof(ExecutionService).GetMethod("TryUpdateCardinalHold", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(fixture.Execution, [TestDutyContextFactory.Create()]);
        Assert.True(tracker.IsGhosted(hold.Key));
    }

    [Fact]
    public void FrontierCofferStagingKeepsItsRemainingNoProgressTime()
    {
        using var fixture = new HyperFixatedTargetExecutionTests.Fixture(Vector3.Zero);
        var now = DateTime.UtcNow;
        var frontier = fixture.Frontier;
        frontier.ReadUtcNow = () => now;
        var normal = TestDutyContextFactory.Create();
        var dutyKey = typeof(DungeonFrontierService).GetMethod("GetDutyKey", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [normal]);
        WritePrivateField(frontier, "activeDutyKey", dutyKey);
        WritePrivateField(frontier, "treasureFollowerActiveStagingRoomIndex", 1);
        WritePrivateField(frontier, "treasureFollowerActiveStagingTargetKey", "stage:1");
        WritePrivateField(frontier, "treasureFollowerActiveStagingTargetName", "Staging coffer");
        WritePrivateField(frontier, "treasureFollowerActiveStagingTargetPosition", (Vector3?)new Vector3(40, 0, 0));
        WritePrivateField(frontier, "treasureFollowerCofferSeekStartedUtc", now.AddSeconds(-12));
        WritePrivateField(frontier, "treasureFollowerCofferSeekLastProgressUtc", now.AddSeconds(-12));
        WritePrivateField(frontier, "treasureFollowerCofferSeekBestHorizontalDistance", 40f);
        WriteEnumPrivateField(frontier, "treasureFollowerCofferSeekState", "Seeking");

        frontier.Update(TestDutyContextFactory.Create(cutscene: true), ObservationSnapshot.Empty);
        now = now.AddSeconds(30);
        frontier.Update(TestDutyContextFactory.Create(watchingCutscene: true), ObservationSnapshot.Empty);
        Assert.Equal("Seeking", frontier.TreasureFollowerCofferSeekStateName);
        frontier.RefreshCutsceneHold(normal);
        void Progress() => typeof(DungeonFrontierService)
            .GetMethod("UpdateTreasureFollowerCofferSeekProgress", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(frontier, [1, (Vector3?)Vector3.Zero, now]);

        Progress();
        now = now.AddSeconds(3).AddTicks(-1);
        Progress();
        Assert.Equal("Seeking", frontier.TreasureFollowerCofferSeekStateName);
        now = now.AddTicks(1);
        Progress();
        Assert.Equal("TimedOut", frontier.TreasureFollowerCofferSeekStateName);
    }

    [Theory]
    [InlineData(false, false, "Latched")]
    [InlineData(true, false, "Seeking")]
    [InlineData(false, true, "Reached")]
    [InlineData(true, true, "Reached")]
    public void OffClearsLatchedStagingBeforeCutsceneReturnsWithoutResettingProgression(
        bool fastPath, bool preserveDoorTarget, string seekState)
    {
        using var fixture = new HyperFixatedTargetExecutionTests.Fixture(Vector3.Zero);
        var frontier = fixture.Frontier;
        var context = TestDutyContextFactory.Create(cutscene: true);
        var target = new DungeonFrontierPoint
        {
            Key = preserveDoorTarget ? "door:1" : "stage:1", Name = "Current route target",
            Position = new Vector3(40, 0, 0), LevelRowId = 1, MapId = 1,
            TreasureRouteIndex = 1, TreasureRoomIndex = 1, TreasurePassageGroup = "Left",
            IsTreasureFollowerStagingPoint = !preserveDoorTarget,
        };
        typeof(DungeonFrontierService).GetProperty(nameof(DungeonFrontierService.CurrentTarget))!
            .SetValue(frontier, target);
        WritePrivateField(frontier, "activeDutyKey", context.ContentFinderConditionId);
        WritePrivateField(frontier, "treasureFollowerActiveDoorCycleRoomIndex", 1);
        WritePrivateField(frontier, "highestTreasureFollowerRoomReached", 1);
        WritePrivateField(frontier, "heldTreasureFollowerCandidateKey", "door:held");
        WritePrivateField(frontier, "lastValidManualDestination", target);
        var visited = ReadPrivateField<HashSet<string>>(frontier, "visitedFrontierKeys");
        visited.Add("manual:visited");
        WritePrivateField(frontier, "treasureFollowerActiveStagingRoomIndex", 1);
        WritePrivateField(frontier, "treasureFollowerActiveStagingTargetKey", "stage:1");
        WritePrivateField(frontier, "treasureFollowerActiveStagingTargetPosition", (Vector3?)target.Position);
        WritePrivateField(frontier, "treasureFollowerCofferSeekStartedUtc", DateTime.UtcNow);
        WritePrivateField(frontier, "treasureFollowerCofferSeekLastProgressUtc", DateTime.UtcNow);
        WritePrivateField(frontier, "treasureFollowerStagingReached", seekState == "Reached");
        WritePrivateField(frontier, "treasureFollowerStagingAttempted", true);
        WritePrivateField(frontier, "treasureFollowerDoorChaseGateState", "StagedAtTreasureCoffer");
        WriteEnumPrivateField(frontier, "treasureFollowerCofferSeekState", seekState);

        if (fastPath) Assert.True(frontier.RefreshCutsceneHold(context, considerTreasureCoffers: false));
        else frontier.Update(context, ObservationSnapshot.Empty, considerTreasureCoffers: false);

        Assert.Equal("Inactive", frontier.TreasureFollowerCofferSeekStateName);
        Assert.Equal(0, ReadPrivateField<int>(frontier, "treasureFollowerActiveStagingRoomIndex"));
        Assert.Null(ReadPrivateField<string?>(frontier, "treasureFollowerActiveStagingTargetKey"));
        Assert.Null(ReadPrivateField<Vector3?>(frontier, "treasureFollowerActiveStagingTargetPosition"));
        Assert.False(ReadPrivateField<bool>(frontier, "treasureFollowerStagingReached"));
        Assert.False(ReadPrivateField<bool>(frontier, "treasureFollowerStagingAttempted"));
        Assert.Equal(DateTime.MinValue, ReadPrivateField<DateTime>(frontier, "treasureFollowerCofferSeekStartedUtc"));
        Assert.Equal(DateTime.MinValue, ReadPrivateField<DateTime>(frontier, "treasureFollowerCofferSeekLastProgressUtc"));
        Assert.Equal(1, ReadPrivateField<int>(frontier, "treasureFollowerActiveDoorCycleRoomIndex"));
        Assert.Equal(1, ReadPrivateField<int>(frontier, "highestTreasureFollowerRoomReached"));
        Assert.Equal("door:held", ReadPrivateField<string>(frontier, "heldTreasureFollowerCandidateKey"));
        Assert.Same(target, ReadPrivateField<DungeonFrontierPoint>(frontier, "lastValidManualDestination"));
        Assert.Contains("manual:visited", visited);
        if (preserveDoorTarget) Assert.Same(target, frontier.CurrentTarget);
        else Assert.Null(frontier.CurrentTarget);
    }

    [Fact]
    public void OffOmitsCofferRoutesAndStagingButPreservesDoorsAndReenabledStaging()
    {
        var playerPosition = new Vector3(0, 99.4f, 330);
        using var fixture = new HyperFixatedTargetExecutionTests.Fixture(playerPosition, includeObservationMemory: true);
        var frontier = fixture.Frontier;
        var context = TestDutyContextFactory.Create(DutyCategory.TreasureDungeon, territoryId: 712);
        frontier.SetTreasureDungeonRole(new(TreasureDungeonRole.Follower, "Test", "Test", "fabricated",
            true, true, false, false, false));
        WritePrivateField(frontier, "activeDutyKey", context.ContentFinderConditionId);
        WritePrivateField(frontier, "treasureFollowerEntryProofDutyKey", context.ContentFinderConditionId);
        var coffer = Observed("coffer:84", 84, "Treasure Coffer", new Vector3(0, 99.4f, 300));
        var door = Observed("door:85", 85, "Vault Door", new Vector3(22, 99.4f, 277), InteractableClass.TreasureDoor);
        var observation = new ObservationSnapshot
        {
            LiveMonsters = [], LiveFollowTargets = [], MonsterGhosts = [],
            LiveInteractables = [coffer, door], InteractableGhosts = [],
        };
        IReadOnlyList<DungeonFrontierPoint> Build(bool enabled) => (IReadOnlyList<DungeonFrontierPoint>)
            typeof(DungeonFrontierService).GetMethod("BuildTreasureRoutePoints", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(frontier, [context, default(Lumina.Excel.Sheets.Map), observation,
                    Array.Empty<DungeonFrontierPoint>(), (Vector3?)playerPosition, enabled])!;
        DungeonFrontierPoint? Select(IReadOnlyList<DungeonFrontierPoint> points, bool enabled)
            => (DungeonFrontierPoint?)typeof(DungeonFrontierService)
                .GetMethod("SelectTreasureFollowerTarget", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(frontier, [context, points, (Vector3?)playerPosition, null, enabled, false]);

        var enabledPoints = Build(true);
        Assert.Contains(enabledPoints, point => point.IsTreasureFollowerStagingPoint);
        Assert.True(Select(enabledPoints, true)!.IsTreasureFollowerStagingPoint);
        Assert.Equal("Seeking", frontier.TreasureFollowerCofferSeekStateName);

        frontier.RefreshCutsceneHold(context, considerTreasureCoffers: false);
        var disabledPoints = Build(false);
        Assert.DoesNotContain(disabledPoints, point => point.IsTreasureFollowerStagingPoint);
        Assert.Contains(disabledPoints, point => point.IsLiveTreasureDoorCandidate);
        Assert.Equal(enabledPoints.Where(point => !point.IsTreasureFollowerStagingPoint).Select(point => point.Key),
            disabledPoints.Select(point => point.Key));
        Assert.Null(Select(enabledPoints, false)); // A stale snapshot cannot select its coffer.
        Assert.Equal("Inactive", frontier.TreasureFollowerCofferSeekStateName);
        Assert.Equal("WaitingForRoomCombat", ReadPrivateField<string>(frontier, "treasureFollowerDoorChaseGateState"));
        Assert.DoesNotContain("coffer", frontier.CurrentLabelStatus, StringComparison.OrdinalIgnoreCase);

        var reenabledPoints = Build(true);
        Assert.True(Select(reenabledPoints, true)!.IsTreasureFollowerStagingPoint);
        Assert.Equal("Seeking", frontier.TreasureFollowerCofferSeekStateName);
    }

    private static ObservedInteractable Observed(string key, ulong id, string name, Vector3 position,
        InteractableClass classification = InteractableClass.TreasureCoffer,
        Dalamud.Game.ClientState.Objects.Enums.ObjectKind kind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventObj)
        => new()
        {
            Key = key, GameObjectId = id, DataId = 100, MapId = 0, ObjectKind = kind, Name = name,
            Position = position, LastSeenUtc = DateTime.UtcNow, Classification = classification,
            GhostReason = GhostReason.SeenPreviously,
        };

    [Theory]
    [InlineData("navigation")]
    [InlineData("arrival")]
    [InlineData("sack")]
    [InlineData("one-shot")]
    public void OffReleasesOwnedCofferWorkBeforeGenericHoldsAndAllowsTheNextObjective(string state)
    {
        using var fixture = new HyperFixatedTargetExecutionTests.Fixture(Vector3.Zero, includeObservationMemory: true);
        var context = TestDutyContextFactory.Create();
        var position = state == "navigation" ? new Vector3(30, 0, 0) : Vector3.Zero;
        var name = state == "sack" ? "Leather Sack" : "Treasure Coffer";
        fixture.AddMonster(86, name, position);
        var coffer = Observed("coffer:86", 86, name, position);
        var follower = state is "sack" or "one-shot";
        Commit(fixture.Execution, coffer, follower ? PlannerObjectiveKind.TreasureFollowerLoot : PlannerObjectiveKind.TreasureCoffer);
        fixture.Configuration.ConsiderTreasureCoffers = true;
        fixture.Execution.QueryInteractionDialog = () => false;
        var dispatches = 0;
        fixture.Execution.DispatchInteractionOverride = _ => { ++dispatches; return true; };
        if (state == "arrival") WritePrivateField(fixture.Execution, "navigationActive", true);

        Advance(fixture.Execution, context, coffer);

        if (state == "navigation") Assert.True(ReadPrivateField<bool>(fixture.Execution, "navigationActive"));
        if (state == "arrival") Assert.NotNull(ReadPrivateField<string?>(fixture.Execution, "interactArrivalWaitKey"));
        if (state == "sack") Assert.Same(coffer, ReadPrivateField<ObservedInteractable>(fixture.Execution, "pendingProgressionInteractable"));
        if (state == "one-shot")
        {
            Assert.Null(ReadPrivateField<string?>(fixture.Execution, "committedInteractableKey"));
            Assert.True(ReadPrivateField<bool>(fixture.Execution, "interactionAttemptDispatched"));
            Assert.True(fixture.Memory!.IsTreasureFollowerContainerChecked(coffer));
        }
        if (state == "sack")
        {
            WritePrivateField(fixture.Execution, "pendingProgressionInteractAfterWaitUntilUtc", DateTime.UtcNow.AddMinutes(1));
            WritePrivateField(fixture.Execution, "pendingCloseRecoveryInteractable", coffer);
            WritePrivateField(fixture.Execution, "treasureCofferLockedRetryKey", coffer.Key);
            WritePrivateField(fixture.Execution, "treasureCofferLockedNextRetryUtc", DateTime.UtcNow.AddMinutes(1));
        }
        var dispatchesBeforeDisable = dispatches;
        var nextPull = fixture.AddMonster(87, "Next pull", new Vector3(20, 0, 0));
        fixture.Configuration.ConsiderTreasureCoffers = false;
        fixture.Execution.QueryInteractionDialog = () => true;

        fixture.Update(HyperFixatedTargetExecutionTests.HyperPlanner(nextPull), context);

        Assert.Equal(dispatchesBeforeDisable, dispatches);
        Assert.False(ReadPrivateField<bool>(fixture.Execution, "interactionAttemptDispatched"));
        Assert.False(fixture.Execution.IsInteractionVbmPauseActive);
        Assert.Null(ReadPrivateField<string?>(fixture.Execution, "committedInteractableKey"));
        Assert.Null(ReadPrivateField<ObservedInteractable?>(fixture.Execution, "pendingProgressionInteractable"));
        Assert.Null(ReadPrivateField<ObservedInteractable?>(fixture.Execution, "pendingCloseRecoveryInteractable"));
        Assert.Null(ReadPrivateField<string?>(fixture.Execution, "interactArrivalWaitKey"));
        Assert.Null(ReadPrivateField<string?>(fixture.Execution, "treasureCofferRouteKey"));
        Assert.Null(ReadPrivateField<string?>(fixture.Execution, "treasureCofferLockedRetryKey"));
        Assert.Equal(0ul, ReadPrivateField<ulong>(fixture.Execution, "lastInteractGameObjectId"));
        Assert.Equal(DateTime.MinValue, ReadPrivateField<DateTime>(fixture.Execution, "nextInteractAttemptUtc"));
        Assert.Equal(DateTime.MinValue, ReadPrivateField<DateTime>(fixture.Execution, "pendingProgressionInteractAfterWaitUntilUtc"));
        Assert.Same(nextPull.GameObject, fixture.Target);
        Assert.Equal(ExecutionPhase.NavigatingToMonsterObjective, fixture.Execution.CurrentPhase);
        Assert.True(fixture.Execution.IsOwned);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OffCancelsOnlyCofferStagingNavigation(bool staging)
    {
        using var fixture = new HyperFixatedTargetExecutionTests.Fixture(Vector3.Zero);
        var context = TestDutyContextFactory.Create();
        var point = new DungeonFrontierPoint
        {
            Key = staging ? "stage:94" : "door:94", Name = staging ? "Staging coffer" : "Passage door",
            Position = new Vector3(40, 0, 0), LevelRowId = 1, MapId = 1,
            TreasureRouteIndex = 1, TreasureRoomIndex = 1,
            TreasurePassageGroup = staging ? "StagingCoffer" : "Left", IsTreasureFollowerStagingPoint = staging,
        };
        fixture.Configuration.ConsiderTreasureCoffers = true;
        typeof(DungeonFrontierService).GetProperty(nameof(DungeonFrontierService.CurrentTarget))!.SetValue(fixture.Frontier, point);
        typeof(DungeonFrontierService).GetProperty(nameof(DungeonFrontierService.CurrentMode))!
            .SetValue(fixture.Frontier, FrontierMode.TreasureDungeon);
        typeof(ExecutionService).GetMethod("TryAdvanceFrontierPoint", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(fixture.Execution, [context, point, false, false, "Test."]);
        Assert.True(ReadPrivateField<bool>(fixture.Execution, "navigationActive"));
        fixture.Commands.Clear();
        fixture.Configuration.ConsiderTreasureCoffers = false;
        fixture.Frontier.RefreshCutsceneHold(context, considerTreasureCoffers: false);
        if (staging) Assert.Null(fixture.Frontier.CurrentTarget);
        else Assert.Same(point, fixture.Frontier.CurrentTarget);

        Assert.Equal(staging, fixture.Execution.CancelDisabledTreasureCofferWork(context));

        Assert.Equal(!staging, ReadPrivateField<bool>(fixture.Execution, "navigationActive"));
        if (staging) Assert.Equal("/vnav stop", Assert.Single(fixture.Commands));
        else Assert.Empty(fixture.Commands);
        Assert.True(fixture.Execution.IsOwned);
    }

    [Theory]
    [InlineData("Treasure Coffer", InteractableClass.TreasureCoffer, Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventObj)]
    [InlineData("Treasure Coffer", InteractableClass.Required, Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventObj)]
    [InlineData("Mysterious Cache", InteractableClass.TreasureCoffer, Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Treasure)]
    [InlineData("Leather Sack", InteractableClass.Expendable, Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventNpc)]
    public void OffRejectsNewCofferWorkBeforeNativeDialogWaitOrDispatch(
        string name, InteractableClass classification, Dalamud.Game.ClientState.Objects.Enums.ObjectKind kind)
    {
        using var fixture = new HyperFixatedTargetExecutionTests.Fixture(Vector3.Zero, includeObservationMemory: true);
        var context = TestDutyContextFactory.Create(DutyCategory.TreasureDungeon, territoryId: 712);
        fixture.AddMonster(88, name, Vector3.Zero);
        var coffer = Observed("coffer:88", 88, name, Vector3.Zero, classification, kind);
        fixture.Configuration.ConsiderTreasureCoffers = false;
        fixture.Execution.QueryInteractionDialog = () => true;
        fixture.Execution.DispatchInteractionOverride = _ => { Assert.Fail("Disabled coffer dispatched"); return true; };

        Advance(fixture.Execution, context, coffer);

        Assert.False(ReadPrivateField<bool>(fixture.Execution, "interactionAttemptDispatched"));
        Assert.False(fixture.Execution.IsInteractionVbmPauseActive);
        Assert.Null(fixture.Target);
        Assert.Empty(fixture.Commands);
        Assert.NotEqual(ExecutionPhase.AttemptingInteractableObjective, fixture.Execution.CurrentPhase);

        fixture.Configuration.ConsiderTreasureCoffers = true;
        fixture.Execution.QueryInteractionDialog = () => false;
        fixture.Execution.DispatchInteractionOverride = _ => true;
        Advance(fixture.Execution, context, coffer);
        Assert.True(ReadPrivateField<bool>(fixture.Execution, "interactionAttemptDispatched"));
    }

    [Theory]
    [InlineData(InteractableClass.Optional, Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventObj)]
    [InlineData(InteractableClass.Required, Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventObj)]
    [InlineData(InteractableClass.TreasureDoor, Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Treasure)]
    public void OffPreservesUnrelatedDispatchPendingResultAndNativeDialogHold(
        InteractableClass classification, Dalamud.Game.ClientState.Objects.Enums.ObjectKind kind)
    {
        using var fixture = new HyperFixatedTargetExecutionTests.Fixture(Vector3.Zero, includeObservationMemory: true);
        var context = TestDutyContextFactory.Create(DutyCategory.TreasureDungeon, territoryId: 558);
        var name = classification == InteractableClass.TreasureDoor ? "Vault Door" : "Terminal";
        fixture.AddMonster(89, name, Vector3.Zero);
        var observed = Observed("ordinary:89", 89, name, Vector3.Zero, classification, kind);
        fixture.Configuration.ConsiderTreasureCoffers = false;
        fixture.Execution.QueryInteractionDialog = () => false;
        fixture.Execution.DispatchInteractionOverride = _ => true;
        Commit(fixture.Execution, observed, PlannerObjectiveKind.RequiredInteractable);

        Advance(fixture.Execution, context, observed);
        fixture.Execution.QueryInteractionDialog = () => true;
        fixture.Update(HyperFixatedTargetExecutionTests.HyperPlanner(
            fixture.AddMonster(90, "Next pull", new Vector3(20, 0, 0))), context);

        Assert.True(ReadPrivateField<bool>(fixture.Execution, "interactionAttemptDispatched"));
        Assert.Same(observed, ReadPrivateField<ObservedInteractable>(fixture.Execution, "pendingProgressionInteractable"));
        Assert.Equal(ExecutionPhase.AttemptingInteractableObjective, fixture.Execution.CurrentPhase);
        Assert.DoesNotContain("target:90", fixture.Events);
    }

    [Fact]
    public void OffPreservesAuthoredGenericTreasureProgressionAndStillRejectsExplicitCoffers()
    {
        using var fixture = new HyperFixatedTargetExecutionTests.Fixture(Vector3.Zero, includeObservationMemory: true,
            initialRules: new ObjectPriorityRuleManifest
            {
                Rules = [new()
                {
                    ObjectKind = "Treasure", ObjectName = "Transit Beacon", NameMatchMode = "Exact",
                    Classification = "Required", Priority = 0,
                }],
            });
        var context = TestDutyContextFactory.Create(); // Outside the treasure progression catalog.
        fixture.AddMonster(95, "Transit Beacon", Vector3.Zero);
        fixture.AddMonster(96, "Treasure Coffer", Vector3.Zero);
        var beacon = Observed("beacon:95", 95, "Transit Beacon", Vector3.Zero, InteractableClass.Required,
            Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Treasure);
        var coffer = Observed("coffer:96", 96, "Treasure Coffer", Vector3.Zero, InteractableClass.Required,
            Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Treasure);
        fixture.Configuration.ConsiderTreasureCoffers = false;
        fixture.Execution.QueryInteractionDialog = () => false;
        var dispatched = new List<ulong>();
        fixture.Execution.DispatchInteractionOverride = gameObject => { dispatched.Add(gameObject.GameObjectId); return true; };
        Commit(fixture.Execution, beacon, PlannerObjectiveKind.RequiredInteractable);

        Advance(fixture.Execution, context, beacon);

        Assert.Equal(new[] { 95ul }, dispatched);
        Assert.Same(beacon, ReadPrivateField<ObservedInteractable>(fixture.Execution, "pendingProgressionInteractable"));
        Assert.False(fixture.Execution.CancelDisabledTreasureCofferWork(context));
        Assert.True(fixture.Execution.RefreshInteractionActionHold(context, true, true, DateTime.UtcNow));
        Assert.Same(beacon, ReadPrivateField<ObservedInteractable>(fixture.Execution, "pendingProgressionInteractable"));

        fixture.Execution.CancelInteractionPause();
        typeof(ExecutionService).GetMethod("ClearInteractableCommitment", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(fixture.Execution, null);
        Commit(fixture.Execution, coffer, PlannerObjectiveKind.RequiredInteractable);
        Advance(fixture.Execution, context, coffer);
        Assert.Equal(new[] { 95ul }, dispatched);
        Assert.Null(ReadPrivateField<string?>(fixture.Execution, "committedInteractableKey"));
        Assert.Null(ReadPrivateField<ObservedInteractable?>(fixture.Execution, "pendingProgressionInteractable"));
        Assert.False(ReadPrivateField<bool>(fixture.Execution, "interactionAttemptDispatched"));
    }

    [Fact]
    public void TargetSetterDisablingCoffersBlocksDispatchWithoutAnExplicitCancellation()
    {
        using var fixture = new HyperFixatedTargetExecutionTests.Fixture(Vector3.Zero, includeObservationMemory: true);
        var context = TestDutyContextFactory.Create();
        var cofferObject = fixture.AddMonster(97, "Treasure Coffer", Vector3.Zero);
        var coffer = Observed("coffer:97", 97, "Treasure Coffer", Vector3.Zero);
        fixture.Configuration.ConsiderTreasureCoffers = true;
        fixture.Execution.QueryInteractionDialog = () => false;
        var dispatches = 0;
        fixture.Execution.DispatchInteractionOverride = _ => { ++dispatches; return true; };
        Commit(fixture.Execution, coffer, PlannerObjectiveKind.TreasureFollowerLoot);
        var manager = DispatchProxy.Create<Dalamud.Plugin.Services.ITargetManager, CallbackTargetManagerProxy>();
        var managerState = (CallbackTargetManagerProxy)(object)manager;
        managerState.Events = fixture.Events;
        var callbacks = 0;
        ulong generationBeforeDisable = 0, generationAfterDisable = 0;
        managerState.AfterTargetSet = () =>
        {
            ++callbacks;
            generationBeforeDisable = ReadPrivateField<ulong>(fixture.Execution, "interactionCancellationGeneration");
            fixture.Configuration.ConsiderTreasureCoffers = false;
            generationAfterDisable = ReadPrivateField<ulong>(fixture.Execution, "interactionCancellationGeneration");
        };
        WritePrivateField(fixture.Execution, "targetManager", manager);

        Advance(fixture.Execution, context, coffer);

        Assert.Equal(1, callbacks);
        Assert.Equal(0, dispatches);
        Assert.Equal(generationBeforeDisable, generationAfterDisable);
        Assert.Same(cofferObject.GameObject, managerState.Target);
        Assert.False(ReadPrivateField<bool>(fixture.Execution, "interactionAttemptDispatched"));
        Assert.False(fixture.Execution.IsInteractionVbmPauseActive);
        Assert.Null(ReadPrivateField<string?>(fixture.Execution, "committedInteractableKey"));
        Assert.Null(ReadPrivateField<ObservedInteractable?>(fixture.Execution, "pendingProgressionInteractable"));
        Assert.False(fixture.Memory!.IsTreasureFollowerContainerChecked(coffer));
        Assert.DoesNotContain(fixture.Commands, command => command.StartsWith("/vnav moveto", StringComparison.Ordinal));
    }

    public class CallbackTargetManagerProxy : HyperFixatedTargetExecutionTests.TargetManagerProxy
    {
        public Action? AfterTargetSet { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var result = base.Invoke(targetMethod, args);
            if (targetMethod?.Name == "set_Target") AfterTargetSet?.Invoke();
            return result;
        }
    }

    private static void Advance(ExecutionService execution, DutyContextSnapshot context, ObservedInteractable observed)
        => typeof(ExecutionService).GetMethod("TryAdvanceInteractableObjective", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(execution, [context, observed, "Test."]);

    private static void Commit(ExecutionService execution, ObservedInteractable observed, PlannerObjectiveKind kind)
        => typeof(ExecutionService).GetMethod("CommitInteractable", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(execution, [observed, kind]);

    private static T ReadPrivateField<T>(object target, string name)
        => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    private static void WritePrivateField<T>(object target, string name, T value)
        => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private static void WriteEnumPrivateField(object target, string name, string value)
    {
        var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(target, Enum.Parse(field.FieldType, value));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CutsceneHoldsOwnedExecutionBeforeNewTargetOrMovementDispatch(bool watchingCutscene)
    {
        using var fixture = new HyperFixatedTargetExecutionTests.Fixture(Vector3.Zero);
        var context = TestDutyContextFactory.Create(cutscene: !watchingCutscene, watchingCutscene: watchingCutscene);
        var nextPull = fixture.AddMonster(80, "Next pull", new Vector3(20, 0, 0));

        fixture.Update(HyperFixatedTargetExecutionTests.HyperPlanner(nextPull), context);

        Assert.Equal(OwnershipMode.OwnedStartInside, fixture.Execution.CurrentMode);
        Assert.Equal(ExecutionPhase.TransitionHold, fixture.Execution.CurrentPhase);
        Assert.Equal(0, fixture.AcquireCalls);
        Assert.Null(fixture.Target);
        Assert.DoesNotContain(fixture.Commands, command => command.StartsWith("/vnav moveto", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(3f, 4f, 0.5f, 3f, 1.5f, true, true)]
    [InlineData(3f, 4f, 0.5f, 2.75f, 1.75f, true, false)]
    [InlineData(3f, 4f, 0.5f, 2.5f, 2f, true, false)]
    [InlineData(3f, 4f, 0.5f, 2.49f, 2.01f, false, false)]
    [InlineData(0f, 1.5f, 0f, 0f, 1.5f, true, true)]
    [InlineData(0f, 2f, 0f, 0f, 2f, true, false)]
    [InlineData(0f, 4f, 0f, 0f, 4f, false, false)]
    [InlineData(2.4f, 0f, 0.5f, 0f, 1.9f, true, false)]
    [InlineData(0f, 4f, 0f, 3f, 1f, true, true)]
    [InlineData(0f, 1f, 0.5f, 2.5f, 0f, true, true)]
    public void InteractionReachAccountsForHitboxesAndKeepsRetriesStricter(
        float horizontalDistance,
        float verticalDistance,
        float playerHitboxRadius,
        float targetHitboxRadius,
        float expectedReach,
        bool expectedInitialAttempt,
        bool expectedRetryAttempt)
    {
        var centerDistance = Vector3.Distance(Vector3.Zero, new Vector3(horizontalDistance, verticalDistance, 0f));
        var reach = ExecutionService.GetInteractionReach(centerDistance, playerHitboxRadius, targetHitboxRadius);
        var initialPolicy = ExecutionService.GetInteractionAttemptPolicy(closeRecoveryArmed: false);
        var retryPolicy = ExecutionService.GetInteractionAttemptPolicy(closeRecoveryArmed: true);

        Assert.Equal(expectedReach, reach, precision: 5);
        Assert.Equal(expectedInitialAttempt, reach <= initialPolicy.AttemptRange);
        Assert.Equal(expectedRetryAttempt, reach <= retryPolicy.AttemptRange);
    }

    [Fact]
    public void InitialAttemptUsesTwoYalmsAndAllowsCloseXzFallback()
    {
        var policy = ExecutionService.GetInteractionAttemptPolicy(closeRecoveryArmed: false);

        Assert.Equal(2.0f, policy.AttemptRange);
        Assert.True(policy.AllowCloseXzFallback);
    }

    [Fact]
    public void UnconfirmedRetryAllowsVnavStoppingToleranceAndDisablesCloseXzFallback()
    {
        var policy = ExecutionService.GetInteractionAttemptPolicy(closeRecoveryArmed: true);

        Assert.Equal(1.5f, policy.AttemptRange);
        Assert.False(policy.AllowCloseXzFallback);
    }

    [Fact]
    public void RetryIdentityRangeRemainsTwoYalms()
    {
        Assert.Equal(2.0f, ExecutionService.InteractableIdentityMatchRange);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void ActiveActionOrDialogPreventsRetryMovementBeyondTheResultTimer(bool casting, bool occupied39, bool dialog)
    {
        using var fixture = new HyperFixatedTargetExecutionTests.Fixture(Vector3.Zero);
        var context = TestDutyContextFactory.Create(casting: casting, occupied39: occupied39);
        ArmSentInteraction(fixture.Execution, context);
        fixture.Execution.QueryInteractionDialog = () => dialog;
        var nextPull = fixture.AddMonster(60, "Next pull", new Vector3(20, 0, 0));

        fixture.Update(HyperFixatedTargetExecutionTests.HyperPlanner(nextPull), context);

        Assert.Equal(ExecutionPhase.AttemptingInteractableObjective, fixture.Execution.CurrentPhase);
        Assert.Null(fixture.Target);
        Assert.DoesNotContain("target:60", fixture.Events);
        Assert.DoesNotContain(fixture.Commands, command => command.StartsWith("/vnav moveto", StringComparison.Ordinal));
    }

    [Fact]
    public void ClosedActionReleasesHoldAndKeepsNormalProgression()
    {
        using var fixture = new HyperFixatedTargetExecutionTests.Fixture(Vector3.Zero);
        var context = TestDutyContextFactory.Create();
        ArmSentInteraction(fixture.Execution, context);
        fixture.Execution.QueryInteractionDialog = () => false;
        var nextPull = fixture.AddMonster(61, "Next pull", new Vector3(20, 0, 0));

        fixture.Update(HyperFixatedTargetExecutionTests.HyperPlanner(nextPull), context);

        Assert.Same(nextPull.GameObject, fixture.Target);
        Assert.Equal(ExecutionPhase.NavigatingToMonsterObjective, fixture.Execution.CurrentPhase);
    }

    [Theory]
    [InlineData(false, true, true, false, 777)]
    [InlineData(true, false, true, false, 777)]
    [InlineData(true, true, false, false, 777)]
    [InlineData(true, true, true, true, 777)]
    [InlineData(true, true, true, false, 999)]
    public void DisableLogoutDutyExitTransitionAndReplacementDutyDiscardTheActionHold(
        bool enabled, bool loggedIn, bool inDuty, bool transition, uint territory)
    {
        using var fixture = new HyperFixatedTargetExecutionTests.Fixture(Vector3.Zero);
        ArmSentInteraction(fixture.Execution, TestDutyContextFactory.Create());
        var changed = TestDutyContextFactory.Create(pluginEnabled: enabled, loggedIn: loggedIn,
            inDuty: inDuty, betweenAreas: transition, territoryId: territory);

        Assert.False(fixture.Execution.RefreshInteractionActionHold(changed, enabled, true, DateTime.UtcNow));
        Assert.False(fixture.Execution.RefreshInteractionActionHold(TestDutyContextFactory.Create(), true, true, DateTime.UtcNow));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NativeDispatchFollowsBothVbmPausesAndFailedDispatchRestoresBeforeRecovery(bool dispatched)
    {
        using var fixture = new HyperFixatedTargetExecutionTests.Fixture(Vector3.Zero);
        var context = TestDutyContextFactory.Create();
        var terminal = fixture.AddMonster(70, "Terminal", Vector3.Zero);
        var ai = true;
        var forceDisabled = false;
        var names = new List<string> { "Rotation", "VBM Multibox" };
        var identities = new[] { new object(), new object(), new object(), new object() };
        fixture.Execution.QueryInteractionDialog = () => false;
        fixture.Execution.InteractionVbmCharacterIdOverride = () => 1;
        fixture.Execution.ReadInteractionVbmOverride = () => new(true,
            new(identities[0], identities[1], identities[2], identities[3], ai, forceDisabled, names), string.Empty);
        fixture.Execution.WriteInteractionVbmAiOverride = (_, enabled) =>
        {
            fixture.Events.Add($"ai:{enabled}");
            ai = enabled;
            names.Remove("VBM Multibox");
            if (enabled) names.Add("VBM Multibox");
            return true;
        };
        fixture.Execution.WriteInteractionVbmPresetsOverride = (_, disabled, selected) =>
        {
            fixture.Events.Add($"presets:{disabled}");
            forceDisabled = disabled;
            names = selected.ToList();
            return true;
        };
        fixture.Execution.DispatchInteractionOverride = _ =>
        {
            Assert.False(ai);
            Assert.True(forceDisabled);
            fixture.Events.Add("dispatch");
            return dispatched;
        };
        var observed = new ObservedInteractable
        {
            Key = "terminal:70", GameObjectId = 70, DataId = 100, MapId = 1,
            ObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventObj,
            Name = "Terminal", Position = Vector3.Zero, LastSeenUtc = DateTime.UtcNow,
            Classification = InteractableClass.Optional, GhostReason = GhostReason.SeenPreviously,
        };

        typeof(ExecutionService).GetMethod("TryAdvanceInteractableObjective", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(fixture.Execution, new object[] { context, observed, "Test." });

        Assert.Same(terminal.GameObject, fixture.Target);
        Assert.True(fixture.Events.IndexOf("ai:False") < fixture.Events.IndexOf("target:70"));
        Assert.True(fixture.Events.IndexOf("presets:True") < fixture.Events.IndexOf("target:70"));
        Assert.True(fixture.Events.IndexOf("target:70") < fixture.Events.IndexOf("dispatch"));
        Assert.Equal(dispatched, fixture.Execution.IsInteractionVbmPauseActive);
        if (!dispatched)
        {
            Assert.True(ai);
            Assert.False(forceDisabled);
            Assert.Equal(new[] { "Rotation", "VBM Multibox" }, names);
            Assert.True(fixture.Events.IndexOf("dispatch") < fixture.Events.IndexOf("ai:True"));
        }
    }

    [Theory]
    [InlineData("ai", true)]
    [InlineData("presets", true)]
    [InlineData("dispatch", true)]
    [InlineData("dispatch", false)]
    [InlineData("restore", false)]
    public void StopInsideAnExternalCallbackCannotRearmTheInteraction(string stopAt, bool dispatched)
    {
        using var fixture = new HyperFixatedTargetExecutionTests.Fixture(Vector3.Zero);
        var context = TestDutyContextFactory.Create();
        fixture.AddMonster(71, "Terminal", Vector3.Zero);
        var ai = true;
        var forceDisabled = false;
        var names = new List<string> { "Rotation" };
        var identities = new[] { new object(), new object(), new object(), new object() };
        fixture.Execution.QueryInteractionDialog = () => false;
        fixture.Execution.InteractionVbmCharacterIdOverride = () => 1;
        fixture.Execution.ReadInteractionVbmOverride = () => new(true,
            new(identities[0], identities[1], identities[2], identities[3], ai, forceDisabled, names), string.Empty);
        fixture.Execution.WriteInteractionVbmAiOverride = (_, enabled) =>
        {
            ai = enabled;
            if ((!enabled && stopAt == "ai") || (enabled && stopAt == "restore"))
                fixture.Execution.Stop(context);
            return true;
        };
        fixture.Execution.WriteInteractionVbmPresetsOverride = (_, disabled, selected) =>
        {
            forceDisabled = disabled;
            names = selected.ToList();
            if (stopAt == "presets") fixture.Execution.Stop(context);
            return true;
        };
        fixture.Execution.DispatchInteractionOverride = _ =>
        {
            fixture.Events.Add("dispatch");
            if (stopAt == "dispatch") fixture.Execution.Stop(context);
            return dispatched;
        };
        var observed = new ObservedInteractable
        {
            Key = "terminal:71", GameObjectId = 71, DataId = 100, MapId = 1,
            ObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventObj,
            Name = "Terminal", Position = Vector3.Zero, LastSeenUtc = DateTime.UtcNow,
            Classification = InteractableClass.Optional, GhostReason = GhostReason.SeenPreviously,
        };

        typeof(ExecutionService).GetMethod("TryAdvanceInteractableObjective", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(fixture.Execution, new object[] { context, observed, "Test." });

        Assert.Equal(OwnershipMode.Observing, fixture.Execution.CurrentMode);
        Assert.Equal(ExecutionPhase.ObservingOnly, fixture.Execution.CurrentPhase);
        Assert.False(fixture.Execution.IsInteractionVbmPauseActive);
        Assert.False((bool)typeof(ExecutionService).GetField("interactionAttemptDispatched", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(fixture.Execution)!);
        Assert.DoesNotContain(fixture.Commands, command => command.StartsWith("/vnav moveto", StringComparison.Ordinal));
        if (stopAt is "ai" or "presets") Assert.DoesNotContain("dispatch", fixture.Events);
    }

    [Theory]
    [InlineData("ai", true)]
    [InlineData("presets", true)]
    [InlineData("dispatch", true)]
    [InlineData("dispatch", false)]
    [InlineData("restore", false)]
    public void DisablingCoffersInsideAnExternalCallbackCannotRearmTheInteraction(string disableAt, bool dispatched)
    {
        using var fixture = new HyperFixatedTargetExecutionTests.Fixture(Vector3.Zero, includeObservationMemory: true);
        var context = TestDutyContextFactory.Create();
        fixture.Configuration.ConsiderTreasureCoffers = true;
        fixture.AddMonster(91, "Treasure Coffer", Vector3.Zero);
        var coffer = Observed("coffer:91", 91, "Treasure Coffer", Vector3.Zero);
        Commit(fixture.Execution, coffer, PlannerObjectiveKind.TreasureFollowerLoot);
        var ai = true;
        var forceDisabled = false;
        var names = new List<string> { "Rotation" };
        var identities = new[] { new object(), new object(), new object(), new object() };
        fixture.Execution.QueryInteractionDialog = () => false;
        fixture.Execution.InteractionVbmCharacterIdOverride = () => 1;
        fixture.Execution.ReadInteractionVbmOverride = () => new(true,
            new(identities[0], identities[1], identities[2], identities[3], ai, forceDisabled, names), string.Empty);
        fixture.Execution.WriteInteractionVbmAiOverride = (_, enabled) =>
        {
            ai = enabled;
            if ((!enabled && disableAt == "ai") || (enabled && disableAt == "restore"))
                fixture.Configuration.ConsiderTreasureCoffers = false;
            return true;
        };
        fixture.Execution.WriteInteractionVbmPresetsOverride = (_, disabled, selected) =>
        {
            forceDisabled = disabled;
            names = selected.ToList();
            if (disabled && disableAt == "presets") fixture.Configuration.ConsiderTreasureCoffers = false;
            return true;
        };
        fixture.Execution.DispatchInteractionOverride = _ =>
        {
            fixture.Events.Add("dispatch");
            if (disableAt == "dispatch") fixture.Configuration.ConsiderTreasureCoffers = false;
            return dispatched;
        };

        Advance(fixture.Execution, context, coffer);

        Assert.False(fixture.Configuration.ConsiderTreasureCoffers);
        Assert.False(fixture.Execution.IsInteractionVbmPauseActive);
        Assert.False(ReadPrivateField<bool>(fixture.Execution, "interactionAttemptDispatched"));
        Assert.Null(ReadPrivateField<string?>(fixture.Execution, "committedInteractableKey"));
        Assert.Null(ReadPrivateField<ObservedInteractable?>(fixture.Execution, "pendingProgressionInteractable"));
        Assert.False(fixture.Memory!.IsTreasureFollowerContainerChecked(coffer));
        Assert.DoesNotContain(fixture.Commands, command => command.StartsWith("/vnav moveto", StringComparison.Ordinal));
        if (disableAt is "ai" or "presets") Assert.DoesNotContain("dispatch", fixture.Events);

        fixture.Execution.QueryInteractionDialog = () => true;
        var next = fixture.AddMonster(92, "Next pull", new Vector3(20, 0, 0));
        fixture.Update(HyperFixatedTargetExecutionTests.HyperPlanner(next), context);
        Assert.Same(next.GameObject, fixture.Target);
        Assert.Equal(ExecutionPhase.NavigatingToMonsterObjective, fixture.Execution.CurrentPhase);
    }

    private static void ArmSentInteraction(ExecutionService execution, DutyContextSnapshot context)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(ExecutionService).GetField("interactionAttemptDispatched", flags)!.SetValue(execution, true);
        typeof(ExecutionService).GetField("interactionDispatchSettleUntilUtc", flags)!.SetValue(execution, DateTime.UtcNow.AddSeconds(-30));
        typeof(ExecutionService).GetField("interactionAttemptDuty", flags)!.SetValue(execution, (context.TerritoryTypeId, context.ContentFinderConditionId));
        typeof(ExecutionService).GetField("interactionAttemptOwnerMode", flags)!.SetValue(execution, execution.CurrentMode);
    }
}
