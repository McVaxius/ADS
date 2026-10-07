using ADS.Services;
using ADS.Models;
using System.Reflection;
using System.Numerics;

namespace ADS.Tests;

public sealed class InteractionRangeTests
{
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
