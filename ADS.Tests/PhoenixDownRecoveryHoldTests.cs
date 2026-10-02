using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using ADS.Models;
using ADS.Services;
using FrenRider.Models;
using FrenRider.Services;

namespace ADS.Tests;

public sealed class PhoenixDownRecoveryHoldTests
{
    [Fact]
    public void AccountsOneThroughFourRecoverWithoutSelectingAnotherPullOrCyclingOwnership()
    {
        using var fixture = new HyperFixatedTargetExecutionTests.Fixture(Vector3.Zero);
        var context = TestDutyContextFactory.Create(DutyCategory.FourMan);
        var oldPull = fixture.AddMonster(51, "Current pull", new Vector3(8, 0, 0));
        var nextPull = fixture.AddMonster(52, "Next pull", new Vector3(12, 0, 0));
        fixture.Update(HyperFixatedTargetExecutionTests.HyperPlanner(oldPull), context);

        // Four synthetic accounts: two dead (including the healer), an empty-item
        // survivor, and a ready survivor. Use the real Fren Rider coordinator and
        // ADS execution gate with replaceable native boundaries.
        var actors = new List<PhoenixActor>
        {
            new(1, 100, true, false, false, Vector3.Zero),
            new(2, 200, true, true, true, new Vector3(30, 0, 0)),
            new(3, 300, true, false, false, Vector3.Zero),
            new(4, 400, true, true, false, new Vector3(10, 0, 0)),
        };
        var emptyRuntime = new Runtime(100, 1, actors, fixture.Execution) { HasItem = false };
        var readyRuntime = new Runtime(300, 3, actors, fixture.Execution);
        using var empty = new PhoenixDownRecoveryService(emptyRuntime);
        using var ready = new PhoenixDownRecoveryService(readyRuntime);
        fixture.Execution.QueryPhoenixDownRecoveryHold = ready.ShouldPauseDutyProgression;
        empty.Update(1000);
        ready.Update(1000); // Waiting for ADS acknowledgement, no approach yet.
        Assert.Equal(0, readyRuntime.Approaches);
        Assert.Empty(readyRuntime.Attempts);

        fixture.Update(HyperFixatedTargetExecutionTests.HyperPlanner(nextPull), context);
        Assert.True(fixture.Execution.IsPhoenixDownRecoveryHoldActive);
        Assert.Equal(OwnershipMode.OwnedStartInside, fixture.Execution.CurrentMode);
        Assert.DoesNotContain("target:52", fixture.Events);
        Assert.Single(fixture.Commands, command => command == "/vnav stop");
        var heldCommands = fixture.Commands.Count;

        ready.Update(1500);
        Assert.Equal(1, readyRuntime.Approaches);
        fixture.Update(HyperFixatedTargetExecutionTests.HyperPlanner(nextPull), context);
        Assert.Equal(heldCommands, fixture.Commands.Count); // ADS must not stop the rescuer's path.
        actors[2] = actors[2] with { Position = new Vector3(16, 0, 0) };
        ready.Update(2000); // CID rank 2: the 1000ms slot has elapsed.
        Assert.Equal(2ul, Assert.Single(readyRuntime.Attempts));
        Assert.True(readyRuntime.ActionsHeld);
        Assert.Empty(emptyRuntime.Attempts);

        actors[2] = actors[2] with { PhoenixCast = true, CastTarget = 2 };
        ready.Update(2100);
        Assert.Equal("Casting Phoenix Down", ready.StatusText);
        fixture.Update(HyperFixatedTargetExecutionTests.HyperPlanner(nextPull), context);
        Assert.DoesNotContain("target:52", fixture.Events);

        actors[1] = actors[1] with { PendingRaise = true };
        actors[2] = actors[2] with { PhoenixCast = false, CastTarget = 0 };
        ready.Update(3000);
        actors[1] = actors[1] with { Dead = false, PendingRaise = false };
        // The living healer now blocks item use for the remaining corpse.
        ready.Update(4000);
        Assert.Single(readyRuntime.Attempts);
        Assert.True(ready.PartyRecoveryActive);
        actors[3] = actors[3] with { Dead = false };
        ready.Update(5000);
        Assert.False(ready.HoldMovement);
        Assert.False(readyRuntime.ActionsHeld);

        fixture.Update(HyperFixatedTargetExecutionTests.HyperPlanner(nextPull), context);
        Assert.False(fixture.Execution.IsPhoenixDownRecoveryHoldActive);
        Assert.Equal(OwnershipMode.OwnedStartInside, fixture.Execution.CurrentMode);
        Assert.Same(nextPull.GameObject, fixture.Target);
        Assert.Contains("target:52", fixture.Events);
        Assert.Equal(ExecutionPhase.NavigatingToMonsterObjective, fixture.Execution.CurrentPhase);
    }

    [Fact]
    public void OperatorStopAndLeaveRemainAuthoritativeAndMissingFrenRiderDoesNotHold()
    {
        using var fixture = new HyperFixatedTargetExecutionTests.Fixture(Vector3.Zero);
        var context = TestDutyContextFactory.Create();
        fixture.Execution.QueryPhoenixDownRecoveryHold = () => true;
        Assert.True(fixture.Execution.RefreshPhoenixDownRecoveryHold(context, true));
        fixture.Execution.Stop(context);
        Assert.False(fixture.Execution.IsPhoenixDownRecoveryHoldActive);
        Assert.False(fixture.Execution.RefreshPhoenixDownRecoveryHold(context, true));
        Assert.Equal(OwnershipMode.Observing, fixture.Execution.CurrentMode);

        typeof(ExecutionService).GetProperty(nameof(ExecutionService.CurrentMode))!.SetValue(fixture.Execution, OwnershipMode.OwnedStartInside);
        Assert.True(fixture.Execution.RefreshPhoenixDownRecoveryHold(context, true));
        Assert.True(fixture.Execution.LeaveDuty(context, false));
        Assert.False(fixture.Execution.IsPhoenixDownRecoveryHoldActive);
        Assert.False(fixture.Execution.RefreshPhoenixDownRecoveryHold(context, true));
        Assert.Equal(OwnershipMode.Leaving, fixture.Execution.CurrentMode);

        typeof(ExecutionService).GetProperty(nameof(ExecutionService.CurrentMode))!.SetValue(fixture.Execution, OwnershipMode.OwnedStartInside);
        fixture.Execution.QueryPhoenixDownRecoveryHold = () => throw new InvalidOperationException("IPC absent");
        Assert.False(fixture.Execution.RefreshPhoenixDownRecoveryHold(context, true));
    }

    [Fact]
    public void UnsafeTransitionLogoutAndDutyExitClearAcknowledgement()
    {
        using var fixture = new HyperFixatedTargetExecutionTests.Fixture(Vector3.Zero);
        fixture.Execution.QueryPhoenixDownRecoveryHold = () => true;
        foreach (var context in new[] { TestDutyContextFactory.Create(betweenAreas: true),
            TestDutyContextFactory.Create(loggedIn: false), TestDutyContextFactory.Create(inDuty: false) })
        {
            Assert.True(fixture.Execution.RefreshPhoenixDownRecoveryHold(TestDutyContextFactory.Create(), true));
            Assert.False(fixture.Execution.RefreshPhoenixDownRecoveryHold(context, true));
            Assert.False(fixture.Execution.IsPhoenixDownRecoveryHoldActive);
        }
    }

    [Fact]
    public void NativeActionHoldAllowsTheOwnedPhoenixCastToCompleteAndBlocksCompetingActions()
    {
        var native = (NativePhoenixRecoveryRuntime)RuntimeHelpers.GetUninitializedObject(typeof(NativePhoenixRecoveryRuntime));
        SetNativeField(native, "actionsHeld", true);
        SetNativeField(native, "itemTarget", 2ul);
        var gate = typeof(NativePhoenixRecoveryRuntime).GetMethod("ShouldBlock", BindingFlags.Instance | BindingFlags.NonPublic)!;
        bool Blocked(FFXIVClientStructs.FFXIV.Client.Game.ActionType type, uint id, ulong target)
            => (bool)gate.Invoke(native, new object[] { type, id, target })!;
        Assert.False(Blocked(FFXIVClientStructs.FFXIV.Client.Game.ActionType.Item, 4570, 2));
        Assert.True(Blocked(FFXIVClientStructs.FFXIV.Client.Game.ActionType.Item, 4570, 4));
        Assert.True(Blocked(FFXIVClientStructs.FFXIV.Client.Game.ActionType.Item, 4868, 2));
        Assert.True(Blocked(FFXIVClientStructs.FFXIV.Client.Game.ActionType.Action, 7, 51));
        SetNativeField(native, "issuingItem", true);
        Assert.True(Blocked(FFXIVClientStructs.FFXIV.Client.Game.ActionType.Item, 4570, 4));
        Assert.False(Blocked(FFXIVClientStructs.FFXIV.Client.Game.ActionType.Item, 4570, 2));
        SetNativeField(native, "issuingItem", false);
        SetNativeField(native, "actionsHeld", false);
        Assert.False(Blocked(FFXIVClientStructs.FFXIV.Client.Game.ActionType.Item, 4868, 2));
    }

    [Fact]
    public void NativeCleanupRestoresOnlyMovementFieldsThatRecoveryChanged()
    {
        var native = (NativePhoenixRecoveryRuntime)RuntimeHelpers.GetUninitializedObject(typeof(NativePhoenixRecoveryRuntime));
        var changed = new MovementConfig { ForbidMovement = true };
        var userOwned = new MovementConfig { ForbidMovement = true };
        var independentlyRestored = new MovementConfig { ForbidMovement = false };
        var field = typeof(MovementConfig).GetField(nameof(MovementConfig.ForbidMovement))!;
        var changes = new List<(object Node, FieldInfo Field, bool Original)>
        {
            (changed, field, false), (independentlyRestored, field, false),
        };
        SetNativeField(native, "movementChanges", changes);
        native.SetHolds(false, false, false);
        Assert.False(changed.ForbidMovement);
        Assert.True(userOwned.ForbidMovement);
        Assert.False(independentlyRestored.ForbidMovement);
        Assert.Empty(changes);
    }

    private static void SetNativeField(object native, string field, object value)
        => typeof(NativePhoenixRecoveryRuntime).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(native, value);

    private sealed class MovementConfig { public bool ForbidMovement; }

    private sealed class Runtime : IPhoenixRecoveryRuntime
    {
        private readonly ulong characterId, localId;
        private readonly List<PhoenixActor> actors;
        private readonly ExecutionService ads;
        internal Runtime(ulong characterId, ulong localId, List<PhoenixActor> actors, ExecutionService ads)
        { this.characterId = characterId; this.localId = localId; this.actors = actors; this.ads = ads; }
        internal bool HasItem = true;
        internal bool ActionsHeld;
        internal int Approaches;
        internal readonly List<ulong> Attempts = new();
        public bool AdsOwnsDuty => ads.IsOwned;
        public PhoenixRecoveryFrame ReadFrame() => new()
        {
            Config = new CharacterConfig { Enabled = true }, Ready = true, Scope = PhoenixRecoveryScope.Dungeon,
            CharacterId = characterId, LocalId = localId, TerritoryId = 777, DutyId = 888,
            Actors = actors, PartyContentIds = actors.Select(actor => actor.ContentId).ToArray(),
        };
        public PhoenixItemReadiness ReadItem(ulong targetId) => new(HasItem, false, true, true);
        public bool TryGetAdsAcknowledgement(out bool acknowledged) { acknowledged = ads.IsPhoenixDownRecoveryHoldActive; return true; }
        public void SetHolds(bool movement, bool actions, bool preventPulls) => ActionsHeld = actions;
        public PhoenixApproachResult Approach(PhoenixActor target, float stopRange, long now) { Approaches++; return PhoenixApproachResult.Moving; }
        public void StopApproach() { }
        public void Dismount() { }
        public bool UsePhoenixDown(ulong targetId) { Attempts.Add(targetId); return true; }
    }
}
