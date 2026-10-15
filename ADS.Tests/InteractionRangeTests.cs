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
        typeof(DungeonFrontierService).GetMethod("RefreshCutsceneHold", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(frontier, [normal]);
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

    private static void ArmSentInteraction(ExecutionService execution, DutyContextSnapshot context)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(ExecutionService).GetField("interactionAttemptDispatched", flags)!.SetValue(execution, true);
        typeof(ExecutionService).GetField("interactionDispatchSettleUntilUtc", flags)!.SetValue(execution, DateTime.UtcNow.AddSeconds(-30));
        typeof(ExecutionService).GetField("interactionAttemptDuty", flags)!.SetValue(execution, (context.TerritoryTypeId, context.ContentFinderConditionId));
        typeof(ExecutionService).GetField("interactionAttemptOwnerMode", flags)!.SetValue(execution, execution.CurrentMode);
    }
}
