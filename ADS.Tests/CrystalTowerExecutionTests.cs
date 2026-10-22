using System.Numerics;
using System.Reflection;
using ADS.Models;
using ADS.Services;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using static ADS.Services.ExecutionService;

namespace ADS.Tests;

public sealed class CrystalTowerExecutionTests
{
    [Fact]
    public void FlareRetreatKeepsObservedHeightAndRequiresAClearAfterTheCastBoundary()
    {
        using var fixture = new Fixture();
        Assert.True(fixture.Tick());
        Assert.Contains("/vnav moveto -148.65 42.00 191.98", fixture.Native.Commands);
        fixture.Boss = fixture.Boss with { IsCasting = false, CastActionId = 0 };
        Assert.True(fixture.Tick()); // The cast disappearing is not a release.
        fixture.ClearSequence = 10;
        Assert.True(fixture.Tick()); // A clear at the start boundary is stale.
        fixture.ClearSequence = 11;
        Assert.False(fixture.Tick());
        Assert.Contains("/vnav stop", fixture.Native.Commands);
        Assert.False(Get<bool>(fixture.Native.Execution, "crystalTowerFlareActive"));
    }

    [Theory]
    [InlineData("alliance")]
    [InlineData("preset")]
    [InlineData("mode")]
    [InlineData("disabled")]
    public void OwnerBoundaryLossStopsTheOwnedPathAndClearsTheRetreat(string change)
    {
        using var fixture = new Fixture();
        Assert.True(fixture.Tick());
        switch (change)
        {
            case "alliance": fixture.Context = Context("B"); break;
            case "preset":
                var rules = Get<ObjectPriorityRuleService>(fixture.Native.Execution, "objectPriorityRuleService");
                typeof(ObjectPriorityRuleService).GetField("activePresetName", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(rules, "Other preset");
                break;
            case "mode":
                typeof(ExecutionService).GetProperty(nameof(ExecutionService.CurrentMode))!
                    .SetValue(fixture.Native.Execution, OwnershipMode.Observing);
                break;
        }
        typeof(ExecutionService).GetMethod("RefreshCrystalTowerLifetime", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(fixture.Native.Execution, [fixture.Context, change != "disabled"]);
        Assert.Contains("/vnav stop", fixture.Native.Commands);
        Assert.False(Get<bool>(fixture.Native.Execution, "navigationActive"));
        Assert.False(Get<bool>(fixture.Native.Execution, "crystalTowerFlareActive"));
        Assert.Null(Get<object?>(fixture.Native.Execution, "crystalTowerOwner"));
    }

    [Fact]
    public void CancellationClearsOwnedTransientStateAndDoesNotReplayOldAnimations()
    {
        using var fixture = new Fixture();
        Assert.True(fixture.Tick());
        fixture.Native.Execution.CancelCrystalTower(fixture.Context);
        Assert.Contains("/vnav stop", fixture.Native.Commands);
        Assert.False(Get<bool>(fixture.Native.Execution, "navigationActive"));
        Assert.False(Get<bool>(fixture.Native.Execution, "crystalTowerFlareActive"));
        Assert.Equal(0ul, Get<ulong>(fixture.Native.Execution, "crystalTowerEventCursor"));
        fixture.Boss = fixture.Boss with { IsCasting = false, CastActionId = 0 };
        Assert.False(fixture.Tick());
    }

    [Theory]
    [InlineData(0x92Eu)]
    [InlineData(0x938u)]
    [InlineData(0x967u)]
    public void AmbiguousBossIdentityHoldsInsteadOfFallingThroughToGenericTargeting(uint dataId)
    {
        using var fixture = new Fixture(dataId switch { 0x92E => new(440.4f, 42, 280), 0x967 => new(-110, 42, -165.6f),
            _ => new(-110, 42, 181.6f) });
        fixture.Boss = fixture.Boss with { DataId = dataId, IsCasting = false };
        fixture.Native.Execution.CrystalTowerObservationOverride = _ => new(true, "fixture", 123, fixture.Local,
            false, [], [fixture.Local, fixture.Boss, fixture.Boss with { GameObjectId = 3, EntityId = 3 }], [], 10);
        Assert.True(fixture.Tick());
        Assert.Equal(ExecutionPhase.CombatHold, fixture.Native.Execution.CurrentPhase);
        Assert.DoesNotContain(fixture.Native.Commands, command => command.StartsWith("/vnav moveto"));
    }

    [Fact]
    public void OrdinaryCloseCombatTargetDoesNotAcquireAMovementPause()
    {
        using var fixture = new Fixture(new(440.4f, 42, 280));
        fixture.SetProvider();
        Assert.False(fixture.Native.Execution.TryAcquireCrystalTowerMovement(fixture.Context, 123, CrystalTowerStrategyMode.Sync, out _));
        fixture.Provider = fixture.Provider! with { FrameTime = fixture.Provider.FrameTime.AddMilliseconds(16) };
        Assert.True(fixture.Native.Execution.TryAcquireCrystalTowerMovement(fixture.Context, 123, CrystalTowerStrategyMode.Sync, out _));
        fixture.Local = fixture.Local with { StatusIds = [398] };
        fixture.Boss = fixture.Boss with { DataId = 0x92E, Position = fixture.Local.Position + Vector3.UnitX, IsCasting = false };
        var native = DispatchProxy.Create<IGameObject, ActorProxy>();
        ((ActorProxy)(object)native).Actor = fixture.Boss;
        var monster = new ObservedMonster { Key = "fixture", GameObjectId = 1, DataId = 0x92E, MapId = 1,
            Name = "fixture boss", Position = fixture.Boss.Position, LastSeenUtc = DateTime.UtcNow };
        var table = Get<object>(fixture.Native.Execution, "objectTable");
        ((HyperFixatedTargetExecutionTests.ObjectTableProxy)table).Objects.Add(new(native, monster));
        Assert.True(fixture.Tick(new ObservationSnapshot { LiveMonsters = [monster], LiveFollowTargets = [],
            MonsterGhosts = [], LiveInteractables = [], InteractableGhosts = [] }));
        Assert.Same(native, fixture.Native.Target);
        Assert.Equal(new[] { true, false }, fixture.Writes);
        Assert.False(fixture.Provider.ForbidMovement);
        Assert.False(fixture.Native.Execution.IsCrystalTowerMovementLeaseActive);
        Assert.Equal(ExecutionPhase.ReadyForMonsterObjective, fixture.Native.Execution.CurrentPhase);
    }

    [Fact]
    public void SharedPhoenixRecoveryEntryReleasesThePauseBeforeThePluginEarlyReturn()
    {
        using var fixture = new Fixture();
        fixture.SetProvider();
        Assert.True(fixture.Tick()); // Pause requested; a fresh provider frame is still needed.
        Assert.True(fixture.Provider!.ForbidMovement);
        fixture.Native.Execution.QueryPhoenixDownRecoveryHold = () => true;
        Assert.True(fixture.Native.Execution.RefreshPhoenixDownRecoveryHold(fixture.Context, true));
        Assert.False(fixture.Provider.ForbidMovement);
        Assert.Equal(new[] { true, false }, fixture.Writes);
        Assert.False(fixture.Native.Execution.IsCrystalTowerMovementLeaseActive);
        Assert.True(fixture.Native.Execution.RefreshPhoenixDownRecoveryHold(fixture.Context, true));
        Assert.Equal(new[] { true, false }, fixture.Writes);
    }

    [Fact]
    public void BoneDragonCorpseAndMissingActorDoNotCompleteTheEncounterBeforeTheVerifiedDirectorEvent()
    {
        using var fixture = new Fixture();
        fixture.Local = fixture.Local with { Position = new(-451.2f, 42, 23.93f) };
        fixture.Boss = fixture.Boss with { DataId = 0x92B, Dead = true, IsCasting = false };
        var actors = new[] { fixture.Local, fixture.Boss };
        CrystalTowerDirectorObservation[] updates = [];
        fixture.Native.Execution.CrystalTowerObservationOverride = _ => new(true, "fixture", 123, fixture.Local,
            false, [], actors, [], 10, updates);
        Assert.True(fixture.Tick());
        Assert.False(Get<bool>(fixture.Native.Execution, "crystalTowerBoneComplete"));
        actors = [fixture.Local]; // Absence cannot distinguish destruction from an unreadable actor.
        Assert.True(fixture.Tick());
        updates = [new(11, DateTime.UtcNow, 1, 0x80000001, 0xB5, 0, 0, 0)];
        Assert.True(fixture.Tick());
        updates = [new(12, DateTime.UtcNow, 1, 0x80000001, 0xB4, 0, 0, 0)];
        Assert.False(fixture.Tick());
        Assert.True(Get<bool>(fixture.Native.Execution, "crystalTowerBoneComplete"));
        fixture.Native.Execution.CancelCrystalTower(fixture.Context);
        Assert.False(Get<bool>(fixture.Native.Execution, "crystalTowerBoneComplete"));
    }

    [Theory]
    [InlineData(15f, 2.4f, true)]
    [InlineData(8f, 2.4f, false)]
    [InlineData(15f, 0f, false)]
    public void MeteorShelterUsesObservedNonoverlappingCometsAndRetainsTheObservedFloor(float offset, float radius, bool canMove)
    {
        using var fixture = new Fixture();
        fixture.Local = fixture.Local with { Position = new(-110, 42, -368.35f) };
        fixture.Boss = fixture.Boss with { DataId = 0x932, Position = new(-110, 42, -368.35f),
            CastActionId = 1756, HitboxRadius = 8.7f };
        fixture.ClearActor = fixture.ClearActor with { DataId = 0x936, Position = fixture.Boss.Position + new Vector3(offset, 0, 0),
            Dead = false, HitboxRadius = radius > 0 ? radius : null };
        Assert.True(fixture.Tick());
        Assert.Equal(canMove, fixture.Native.Commands.Any(command => command.StartsWith("/vnav moveto")));
        if (!canMove) return;
        Assert.Contains("/vnav moveto -92.00 42.00 -368.35", fixture.Native.Commands);
        fixture.Boss = fixture.Boss with { IsCasting = null, CastActionId = null };
        Assert.True(fixture.Tick());
        Assert.True(Get<bool>(fixture.Native.Execution, "crystalTowerMeteorActive"));
        fixture.Boss = fixture.Boss with { IsCasting = false, CastActionId = 0 };
        Assert.False(fixture.Tick());
        Assert.Contains("/vnav stop", fixture.Native.Commands);
        Assert.False(Get<bool>(fixture.Native.Execution, "navigationActive"));
    }

    [Fact]
    public void ReplacementMeteorBossDoesNotRetainTheOldCometRoute()
    {
        using var fixture = new Fixture();
        fixture.Local = fixture.Local with { Position = new(-110, 42, -368.35f) };
        fixture.Boss = fixture.Boss with { DataId = 0x932, Position = fixture.Local.Position, CastActionId = 1756, HitboxRadius = 8.7f };
        fixture.ClearActor = fixture.ClearActor with { DataId = 0x936, Position = fixture.Boss.Position + new Vector3(15, 0, 0),
            Dead = false, HitboxRadius = 2.4f };
        Assert.True(fixture.Tick());
        Assert.True(Get<bool>(fixture.Native.Execution, "navigationActive"));
        fixture.Boss = fixture.Boss with { GameObjectId = 99, EntityId = 99, IsCasting = false, CastActionId = 0 };
        Assert.False(fixture.Tick());
        Assert.Contains("/vnav stop", fixture.Native.Commands);
        Assert.False(Get<bool>(fixture.Native.Execution, "navigationActive"));
    }

    [Theory]
    [InlineData(0x963u)]
    [InlineData(0x92Eu)]
    [InlineData(0x967u)]
    [InlineData(0x938u)]
    public void ActorsObservedInAnotherLabyrinthRoomCannotSeizeTheCurrentProgression(uint dataId)
    {
        using var fixture = new Fixture(Vector3.Zero);
        fixture.Boss = fixture.Boss with { DataId = dataId };
        Assert.False(fixture.Tick());
        Assert.Empty(fixture.Native.Commands);
        Assert.Null(fixture.Native.Target);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    public void PyreticPausesActionsAndMovementThenRestoresOnlyTheUnchangedOwnedProvider(int provider, bool cancel)
    {
        using var fixture = new Fixture();
        fixture.Context = TestDutyContextFactory.Create(DutyCategory.AllianceRaid, territoryId: 151, cfcId: 111, alliance: "A");
        var rules = Get<ObjectPriorityRuleService>(fixture.Native.Execution, "objectPriorityRuleService");
        rules.Current.Rules.Single().TerritoryTypeId = 151;
        rules.Current.Rules.Single().ContentFinderConditionId = 111;
        fixture.Local = fixture.Local with { StatusIds = [639] };
        fixture.Native.Execution.CrystalTowerObservationOverride = _ => new(true, "fixture", 123, fixture.Local,
            false, [], [fixture.Local], [], 10);
        fixture.SetProvider((CrystalTowerMovementProvider)provider);
        var original = fixture.Provider!;
        var writes = new List<CrystalTowerActionSetting>();
        fixture.Native.Execution.WriteCrystalTowerActionOverride = (_, setting, value, presets) =>
        {
            writes.Add(setting);
            fixture.Provider = setting == CrystalTowerActionSetting.ForbidActions
                ? fixture.Provider! with { ForbidActions = value, LastModified = fixture.Provider.LastModified.AddTicks(1) }
                : fixture.Provider! with { ForceDisabled = value, ActivePresets = value ? [string.Empty] : presets.ToArray() };
            return true;
        };
        Assert.True(fixture.Tick());
        Assert.True(fixture.Provider!.ForbidMovement);
        Assert.True(fixture.Provider.ForbidActions);
        Assert.True(fixture.Provider.ForceDisabled);
        Assert.Equal(new[] { string.Empty }, fixture.Provider.ActivePresets);
        Assert.DoesNotContain("Pyretic", fixture.Native.Execution.LastStatus); // A fresh provider frame is required.
        fixture.Provider = fixture.Provider with { FrameTime = fixture.Provider.FrameTime.AddMilliseconds(16) };
        Assert.True(fixture.Tick());
        Assert.Contains("Pyretic", fixture.Native.Execution.LastStatus);
        var before = writes.Count;
        Assert.True(fixture.Tick());
        Assert.Equal(before, writes.Count);
        if (cancel) fixture.Native.Execution.CancelCrystalTower(fixture.Context);
        else
        {
            fixture.Local = fixture.Local with { StatusIds = [] };
            Assert.False(fixture.Tick());
        }
        Assert.Equal(original.AiEnabled, fixture.Provider.AiEnabled);
        Assert.False(fixture.Provider.ForbidMovement);
        Assert.False(fixture.Provider.ForbidActions);
        Assert.False(fixture.Provider.ForceDisabled);
        Assert.Equal(original.ActivePresets, fixture.Provider.ActivePresets);
    }

    public class ActorProxy : DispatchProxy
    {
        internal CrystalTowerActorObservation Actor = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
        {
            "get_GameObjectId" => Actor.GameObjectId,
            "get_EntityId" => Actor.EntityId,
            "get_BaseId" => Actor.DataId,
            "get_Position" => Actor.Position,
            "get_IsDead" => Actor.Dead ?? false,
            "get_IsTargetable" => Actor.Targetable ?? false,
            _ => method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null,
        };
    }

    private static T Get<T>(ExecutionService service, string name)
        => (T)typeof(ExecutionService).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;

    private static DutyContextSnapshot Context(string alliance = "A")
        => TestDutyContextFactory.Create(DutyCategory.AllianceRaid, territoryId: 174, cfcId: 92, alliance: alliance);

    internal sealed class Fixture : IDisposable
    {
        internal Fixture(Vector3? localPosition = null, string strategy = "SYNC")
        {
            Local = Local with { Position = localPosition ?? new(-110, 42, 181.6f) };
            Native = new(Local.Position, initialRules: new ObjectPriorityRuleManifest
            {
                Rules = [new() { TerritoryTypeId = 174, ContentFinderConditionId = 92,
                    ObjectKind = "BattleNpc", BaseId = 0x938, Classification = "Required", CrystalTowerStrategy = strategy }],
            });
            Native.Execution.CrystalTowerObservationOverride = _ => new(true, "fixture", 123, Local, false, [],
                [Local, Boss, ClearActor], ClearSequence is { } sequence
                    ? [new(sequence, DateTime.UtcNow, ClearActor.GameObjectId, ClearActor.EntityId, ClearActor.DataId, 0x00110022)] : [],
                ClearSequence ?? 10);
            Native.Execution.CrystalTowerMovementCharacterIdOverride = () => 1;
            Native.Execution.CrystalTowerMovementLocalEntityIdOverride = () => Local.EntityId;
            Native.Execution.ReadCrystalTowerMovementOverride = () => new(false, null, "fixture: no provider");
        }

        internal HyperFixatedTargetExecutionTests.Fixture Native { get; }
        internal DutyContextSnapshot Context = CrystalTowerExecutionTests.Context();
        internal CrystalTowerActorObservation Local = new(999, 123, 0, ObjectKind.Pc, new(0, 42, 0),
            false, true, false, 0, [], null, 19);
        internal CrystalTowerActorObservation Boss = new(1, 1, 0x938, ObjectKind.BattleNpc, Vector3.Zero,
            false, true, true, 1730, [], null, null);
        internal CrystalTowerActorObservation ClearActor = new(2, 0xE0000000, 0x1E8885, ObjectKind.EventObj, Vector3.Zero,
            null, null, null, null, null, 0, null);
        internal ulong? ClearSequence;
        internal CrystalTowerMovementSnapshot? Provider;
        internal List<bool> Writes = [];
        internal void SetProvider(CrystalTowerMovementProvider provider = CrystalTowerMovementProvider.BossMod)
        {
            var time = DateTime.UtcNow;
            Provider = new(provider, new(), new(), new(), new(), new(),
                provider == CrystalTowerMovementProvider.BossModReborn ? new() : null,
                provider == CrystalTowerMovementProvider.BossModReborn ? new() : null,
                false, provider == CrystalTowerMovementProvider.BossMod ? true : null, false, false,
                ["Damage"], time, time, true, false, false, false, false, true, false);
            Native.Execution.ReadCrystalTowerMovementOverride = () => new(true, Provider, "fixture");
            Native.Execution.WriteCrystalTowerMovementOverride = (_, forbid) =>
            {
                Writes.Add(forbid);
                Provider = Provider! with { ForbidMovement = forbid,
                    LastModified = provider == CrystalTowerMovementProvider.BossMod ? Provider.LastModified.AddTicks(1) : Provider.LastModified };
                return true;
            };
        }
        internal bool Tick(ObservationSnapshot? observation = null)
            => (bool)typeof(ExecutionService).GetMethod("TryHandleCrystalTower", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(Native.Execution, [Context, observation ?? ObservationSnapshot.Empty])!;
        public void Dispose() => Native.Dispose();
    }
}
