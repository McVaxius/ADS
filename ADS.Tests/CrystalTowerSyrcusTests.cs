using System.Numerics;
using System.Reflection;
using ADS.Models;
using ADS.Services;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using static ADS.Services.ExecutionService;

namespace ADS.Tests;

public sealed class CrystalTowerSyrcusTests
{
    [Fact]
    public void CurtainCoverIsDerivedBehindTheLivingNativeCageAndKeepsObservedFloorForTheMover()
    {
        var boss = Boss();
        var cage = Cage();
        var snapshot = Snapshot(Local(), boss, cage);
        var decision = SelectCrystalTowerCurtainCover(snapshot, boss);
        Assert.NotNull(decision);
        Assert.Equal(cage.GameObjectId, decision.Value.CageId);
        Assert.Equal(new Vector2(12.3f, -200), decision.Value.DestinationXZ);
        Assert.True(IsCrystalTowerCurtainCovered(new(0, -200), new(10, -200), 1.8f, decision.Value.DestinationXZ));
    }

    [Fact]
    public void CurtainCoverRetainsAnAlreadyProtectedPositionAndTheChosenCage()
    {
        var boss = Boss();
        var right = Cage();
        var left = Cage(3, new(-10, 42, -200));
        var local = Local(new(15, 42, -200));
        var covered = SelectCrystalTowerCurtainCover(Snapshot(local, boss, right, left), boss);
        Assert.Equal(new CrystalTowerIceCover(right.GameObjectId, new(15, -200), 0), covered);
        Assert.Equal(right.GameObjectId,
            SelectCrystalTowerCurtainCover(Snapshot(local, boss, right, left), boss, left.GameObjectId)!.Value.CageId);
        var preferred = SelectCrystalTowerCurtainCover(Snapshot(Local(), boss, right, left), boss, left.GameObjectId);
        Assert.Equal(left.GameObjectId, preferred!.Value.CageId);
    }

    [Fact]
    public void AChangedSmallNativeRadiusCannotInheritAnArrivalToleranceWiderThanItsShadow()
    {
        var boss = Boss();
        var cage = Cage() with { HitboxRadius = 0.01f };
        var cover = SelectCrystalTowerCurtainCover(Snapshot(Local(), boss, cage), boss);
        Assert.NotNull(cover);
        Assert.Equal(0.0025f, cover.Value.ArrivalRadius);
        Assert.True(IsCrystalTowerCurtainCovered(new(0, -200), new(10, -200), .01f, cover.Value.DestinationXZ));
    }

    [Fact]
    public void DeadUnknownOverlappingOffArenaAndAmbiguousIceCannotBecomeCover()
    {
        var boss = Boss();
        var cage = Cage();
        foreach (var invalid in new[]
        {
            cage with { Dead = true }, cage with { Dead = null }, cage with { HitboxRadius = null },
            cage with { HitboxRadius = float.NaN }, cage with { HitboxRadius = 0 },
            cage with { Position = new(3, 42, -200) }, cage with { Position = new(29, 42, -200) },
            cage with { Position = new(float.PositiveInfinity, 42, -200) },
            cage with { ObjectKind = ObjectKind.EventObj }, cage with { GameObjectId = 0 },
        }) Assert.Null(SelectCrystalTowerCurtainCover(Snapshot(Local(), boss, invalid), boss));
        Assert.Null(SelectCrystalTowerCurtainCover(Snapshot(Local(), boss, cage, cage), boss));
        Assert.Null(SelectCrystalTowerCurtainCover(Snapshot(Local(), boss, cage), boss with { HitboxRadius = null }));
        Assert.Null(SelectCrystalTowerCurtainCover(Snapshot(Local(), boss, cage) with { Valid = false }, boss));
    }

    [Theory]
    [InlineData(15f, -200f, true)]
    [InlineData(5f, -200f, false)]
    [InlineData(10.1f, -200f, false)]
    [InlineData(15f, -195f, false)]
    [InlineData(-15f, -200f, false)]
    [InlineData(65f, -200f, false)]
    public void CurtainShadowUsesThePinnedConeAndPositiveBeyondBlockerBoundary(float x, float z, bool expected)
        => Assert.Equal(expected, IsCrystalTowerCurtainCovered(new(0, -200), new(10, -200), 1.8f, new(x, z)));

    [Theory]
    [InlineData("SYNC")]
    [InlineData("UNSYNC")]
    public void ActualCurtainCastRetreatsInEitherStrategyAndUnknownCastStateCannotReleaseIt(string strategy)
    {
        using var fixture = new Fixture(strategy);
        fixture.Actors = [Boss(), Cage()];
        Assert.True(fixture.Tick());
        Assert.Contains("/vnav moveto 12.30 42.00 -200.00", fixture.Native.Commands);
        fixture.Actors = [Boss() with { IsCasting = null, CastActionId = null }, Cage()];
        Assert.True(fixture.Tick());
        Assert.True(Get<bool>(fixture.Native.Execution, "crystalTowerCurtainActive"));
        fixture.Actors = [Boss() with { IsCasting = false, CastActionId = 0 }, Cage()];
        Assert.False(fixture.Tick());
        Assert.False(Get<bool>(fixture.Native.Execution, "crystalTowerCurtainActive"));
        Assert.Contains("/vnav stop", fixture.Native.Commands);
    }

    [Fact]
    public void MissingIceOrMissingCasterKeepsAnObservedCurtainActiveWithoutGuessingDestinations()
    {
        using var fixture = new Fixture();
        fixture.Actors = [Boss()];
        Assert.True(fixture.Tick());
        Assert.Equal(ExecutionPhase.CombatHold, fixture.Native.Execution.CurrentPhase);
        Assert.DoesNotContain(fixture.Native.Commands, command => command.StartsWith("/vnav moveto"));
        fixture.Local = fixture.Local with { StatusIds = null };
        fixture.Actors = [Boss(), Cage()];
        Assert.True(fixture.Tick());
        Assert.DoesNotContain(fixture.Native.Commands, command => command.StartsWith("/vnav moveto"));
        fixture.Actors = [];
        Assert.True(fixture.Tick());
        Assert.True(Get<bool>(fixture.Native.Execution, "crystalTowerCurtainActive"));
    }

    [Fact]
    public void PositiveOtherCastReleasesOnlyTheCurtainHoldAndAnOldCorpseDoesNotStopOrdinaryNavigation()
    {
        using var fixture = new Fixture();
        fixture.Actors = [Boss(), Cage()];
        Assert.True(fixture.Tick());
        fixture.Actors = [Boss() with { CastActionId = 2382 }, Cage()];
        Assert.False(fixture.Tick());
        Assert.False(Get<bool>(fixture.Native.Execution, "crystalTowerCurtainActive"));
        fixture.Native.Commands.Clear();
        fixture.Actors = [Boss() with { Dead = true, IsCasting = false, CastActionId = 0 }];
        Assert.False(fixture.Tick());
        Assert.False(fixture.Tick());
        Assert.Empty(fixture.Native.Commands);
    }

    [Fact]
    public void ResetClearsOnlyTransientSyrcusCastState()
    {
        using var fixture = new Fixture();
        fixture.Actors = [Boss(), Cage()];
        Assert.True(fixture.Tick());
        typeof(ExecutionService).GetMethod("ResetCrystalTowerSyrcus", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(fixture.Native.Execution, null);
        Assert.False(Get<bool>(fixture.Native.Execution, "crystalTowerCurtainActive"));
        Assert.Equal(0ul, Get<ulong>(fixture.Native.Execution, "crystalTowerCurtainCover"));
        Assert.Equal(0ul, Get<ulong>(fixture.Native.Execution, "crystalTowerCurtainBoss"));
    }

    [Theory]
    [InlineData(SyrcusScylla, 2317u, 520u, 0f)]
    [InlineData(SyrcusXande, 2361u, 12u, -400f)]
    public void VerifiedProtectionHoldsTheObservedPositionWhileMissingProtectionNeverInventsAField(
        uint bossId, uint action, uint status, float x)
    {
        using var fixture = new Fixture(local: Local(new(x, 42, -200)) with { StatusIds = [status] });
        fixture.Actors = [Boss() with { DataId = bossId, Position = new(x, 42, -200), CastActionId = action }];
        Assert.True(fixture.Tick());
        Assert.Equal(ExecutionPhase.CombatHold, fixture.Native.Execution.CurrentPhase);
        Assert.DoesNotContain(fixture.Native.Commands, command => command.StartsWith("/vnav moveto"));
        fixture.Local = fixture.Local with { StatusIds = [] };
        Assert.True(fixture.Tick());
        Assert.DoesNotContain(fixture.Native.Commands, command => command.StartsWith("/vnav moveto"));
    }

    [Fact]
    public void MeteorSelectionUsesKnownLivingAttackableIdentitiesAndExcludesMinesAndUnknowns()
    {
        var local = Local(new(-400, 42, -200));
        var star = Actor(2, SyrcusStarfallCircle, new(-398, 42, -200));
        var stone = Actor(3, SyrcusStonefallCircle, new(-399, 42, -200));
        var snapshot = Snapshot(local, star, stone, Actor(4, 0xBDF, local.Position),
            Actor(5, SyrcusStonefallCircle, local.Position) with { Dead = null },
            Actor(6, SyrcusStarfallCircle, local.Position) with { Targetable = false });
        Assert.Same(stone, SelectCrystalTowerXandeMeteor(snapshot));
        Assert.Same(stone, SelectCrystalTowerXandeMeteor(snapshot with { Actors = snapshot.Actors.Reverse().ToArray() }));
        Assert.Null(SelectCrystalTowerXandeMeteor(Snapshot(local, stone, stone)));
        Assert.Null(SelectCrystalTowerXandeMeteor(snapshot with { Valid = false }));
    }

    [Theory]
    [InlineData(false, ExecutionPhase.ReadyForMonsterObjective)]
    [InlineData(true, ExecutionPhase.CombatHold)]
    public void ActualXandeTargetingRevalidatesNativeActorAndDoesNotTargetAnIceCageOrMine(bool inCombat, ExecutionPhase expected)
    {
        using var fixture = new Fixture(local: Local(new(-400, 42, -200)), inCombat: inCombat);
        var meteor = Actor(2, SyrcusStonefallCircle, new(-399, 42, -200));
        fixture.Actors = [meteor, Actor(3, 0xBDF, fixture.Local.Position)];
        var native = fixture.AddNativeTarget(meteor);
        Assert.True(fixture.Tick());
        Assert.Same(native, fixture.Native.Target);
        Assert.Equal(expected, fixture.Native.Execution.CurrentPhase);
        Assert.False(fixture.Native.Execution.IsCrystalTowerMovementLeaseActive);
        Assert.DoesNotContain(fixture.Native.Commands, command => command.StartsWith("/vnav moveto"));
    }

    [Fact]
    public void AmonAddSelectionExcludesIceAndCompressionAndUnknownOrDeadActors()
    {
        var boss = Boss() with { IsCasting = false, CastActionId = 0 };
        var good = Actor(4, 0xBE6, new(0, 42, -199));
        var snapshot = Snapshot(Local(), boss, Cage(), Actor(3, 0xBE2, Local().Position), good,
            Actor(5, 0xBE3, Local().Position) with { Dead = true },
            Actor(6, 0xBE4, Local().Position) with { Targetable = null });
        Assert.Same(good, SelectCrystalTowerAmonAdd(snapshot, boss));
        Assert.Null(SelectCrystalTowerAmonAdd(snapshot, boss with { Dead = true }));
    }

    [Fact]
    public void FireToadHoldsWithoutSendingTransformedAbilityOrTargetInput()
    {
        using var fixture = new Fixture(local: Local() with { StatusIds = [511] });
        var add = Actor(4, 0xBE6, new(0, 42, -199));
        fixture.Actors = [Boss() with { IsCasting = false, CastActionId = 0 }, add];
        fixture.AddNativeTarget(add);
        Assert.True(fixture.Tick());
        Assert.Null(fixture.Native.Target);
        Assert.DoesNotContain(fixture.Native.Commands, command => command.StartsWith("/vnav moveto") || command.StartsWith("/ac"));
    }

    [Theory]
    [InlineData(SyrcusClockworkWright, "SYNC")]
    [InlineData(SyrcusClockworkSquire, "SYNC")]
    [InlineData(SyrcusSpannerRook, "SYNC")]
    [InlineData(SyrcusClockworkWright, "UNSYNC")]
    [InlineData(SyrcusClockworkSquire, "UNSYNC")]
    [InlineData(SyrcusSpannerRook, "UNSYNC")]
    public void ActualGlasyaDispatchSelectsTheVerifiedAddRatherThanMoonringBlade(uint dataId, string mode)
    {
        using var fixture = new Fixture(strategy: mode, local: Local(new(0, 0, -198)));
        var boss = Actor(1, SyrcusGlasya, new(0, 0, -200));
        var add = Actor(2, dataId, new(0, 0, -199));
        fixture.Actors = [boss, add, Actor(3, 0xBD8, fixture.Local.Position)];
        var native = fixture.AddNativeTarget(add);
        Assert.True(fixture.Tick());
        Assert.Same(native, fixture.Native.Target);
        Assert.DoesNotContain(fixture.Native.Commands, command => command.StartsWith("/ac"));
    }

    [Theory]
    [InlineData("unknown-death")]
    [InlineData("dead")]
    [InlineData("untargetable")]
    [InlineData("duplicate-object")]
    [InlineData("duplicate-entity")]
    [InlineData("boss-floor")]
    [InlineData("add-floor")]
    [InlineData("local-floor")]
    [InlineData("outer-platform")]
    [InlineData("duplicate-boss")]
    public void GlasyaSelectionRejectsUnverifiedIdentityAndNeverFlattensAnotherFloorOrInventsAnOuterRoute(string invalid)
    {
        var local = Local(new(0, 0, -198));
        var boss = Actor(1, SyrcusGlasya, new(0, 0, -200));
        var add = Actor(2, SyrcusClockworkSquire, new(0, 0, -199));
        add = invalid switch
        {
            "unknown-death" => add with { Dead = null },
            "dead" => add with { Dead = true },
            "untargetable" => add with { Targetable = false },
            "add-floor" => add with { Position = new(0, 600.94f, -199) },
            "outer-platform" => add with { Position = new(80, 0, -199) },
            _ => add,
        };
        if (invalid == "boss-floor") boss = boss with { Position = new(0, 600.94f, -200) };
        if (invalid == "local-floor") local = local with { Position = new(0, 600.94f, -198) };
        CrystalTowerActorObservation[] actors = [boss, add];
        if (invalid == "duplicate-object") actors = [.. actors, add with { EntityId = 99 }];
        if (invalid == "duplicate-entity") actors = [.. actors, add with { GameObjectId = 99 }];
        if (invalid == "duplicate-boss") actors = [.. actors, boss with { GameObjectId = 99, EntityId = 99 }];
        Assert.Null(SelectCrystalTowerGlasyaAdd(Snapshot(local, actors), boss));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExistingGlasyaInterceptionKeepsPositionEvenWhenItsWrightIsMissing(bool visibleWright)
    {
        using var fixture = new Fixture(local: Local(new(0, 0, -198)) with { StatusIds = [510] });
        var boss = Actor(1, SyrcusGlasya, new(0, 0, -200));
        var add = Actor(2, SyrcusClockworkWright, new(10, 0, -198));
        fixture.Actors = visibleWright ? [boss, add] : [boss];
        if (visibleWright) fixture.AddNativeTarget(add);
        Assert.True(fixture.Tick());
        Assert.Equal(ExecutionPhase.CombatHold, fixture.Native.Execution.CurrentPhase);
        Assert.DoesNotContain(fixture.Native.Commands, command => command.StartsWith("/vnav moveto"));
        Assert.Equal(visibleWright ? add.GameObjectId : (ulong?)null, fixture.Native.Target?.GameObjectId);
    }

    [Fact]
    public void ActualGlasyaDispatchRequiresTheMatchingNativeAddBeforeSelectingIt()
    {
        using var fixture = new Fixture(local: Local(new(0, 0, -198)));
        var boss = Actor(1, SyrcusGlasya, new(0, 0, -200));
        var add = Actor(2, SyrcusClockworkSquire, new(0, 0, -199));
        fixture.Actors = [boss, add];
        fixture.AddNativeTarget(add with { DataId = 0xBD8 });
        Assert.True(fixture.Tick());
        Assert.Null(fixture.Native.Target);
        Assert.Contains("not eligible", fixture.Native.Execution.LastStatus);
    }

    private static CrystalTowerActorObservation Local(Vector3? position = null)
        => new(999, 123, 0, ObjectKind.Pc, position ?? new(0, 42, -190), false, true, false, 0, [], null, 19, .5f);

    private static CrystalTowerActorObservation Boss()
        => new(1, 1, SyrcusAmon, ObjectKind.BattleNpc, new(0, 42, -200), false, true, true,
            SyrcusCurtainCall, [], null, null, 4);

    private static CrystalTowerActorObservation Cage(ulong id = 2, Vector3? position = null)
        => Actor(id, SyrcusIceCage, position ?? new(10, 42, -200)) with { HitboxRadius = 1.8f };

    private static CrystalTowerActorObservation Actor(ulong id, uint dataId, Vector3 position)
        => new(id, (uint)id, dataId, ObjectKind.BattleNpc, position, false, true, false, 0, [], null, null, 1);

    private static CrystalTowerObservation Snapshot(CrystalTowerActorObservation local, params CrystalTowerActorObservation[] actors)
        => new(true, "fabricated Syrcus fixture", local.EntityId, local, false, [], actors, [], 0);

    private static T Get<T>(ExecutionService service, string name)
        => (T)typeof(ExecutionService).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(service)!;

    private sealed class Fixture : IDisposable
    {
        internal Fixture(string strategy = "SYNC", CrystalTowerActorObservation? local = null, bool inCombat = true)
        {
            Context = TestDutyContextFactory.Create(DutyCategory.AllianceRaid,
                territoryId: 372, cfcId: 102, alliance: "A", inCombat: inCombat);
            Local = local ?? CrystalTowerSyrcusTests.Local();
            Native = new(Local.Position, initialRules: new ObjectPriorityRuleManifest
            {
                Rules = [new() { TerritoryTypeId = 372, ContentFinderConditionId = 102, ObjectKind = "BattleNpc",
                    BaseId = SyrcusAmon, Classification = "Required", CrystalTowerStrategy = strategy }],
            });
            Native.Execution.CrystalTowerObservationOverride = _ => CurrentSnapshot;
            Native.Execution.CrystalTowerMovementCharacterIdOverride = () => 1;
            Native.Execution.CrystalTowerMovementLocalEntityIdOverride = () => 123;
            Native.Execution.ReadCrystalTowerMovementOverride = () => new(false, null, "fabricated: no provider");
            // Establish the real common duty owner with no phase observations.
            typeof(ExecutionService).GetMethod("TryHandleCrystalTower", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(Native.Execution, [Context, EmptyObservation]);
        }
        internal HyperFixatedTargetExecutionTests.Fixture Native { get; }
        internal DutyContextSnapshot Context { get; }
        internal CrystalTowerActorObservation Local;
        internal CrystalTowerActorObservation[] Actors = [];
        private readonly List<ObservedMonster> monsters = [];
        private CrystalTowerObservation CurrentSnapshot => Snapshot(Local, Actors);
        private static ObservationSnapshot EmptyObservation => new()
        { LiveMonsters = [], LiveFollowTargets = [], MonsterGhosts = [], LiveInteractables = [], InteractableGhosts = [] };
        internal bool Tick()
        {
            var observation = new ObservationSnapshot { LiveMonsters = monsters, LiveFollowTargets = [],
                MonsterGhosts = [], LiveInteractables = [], InteractableGhosts = [] };
            return (bool)typeof(ExecutionService).GetMethod("TryHandleCrystalTowerSyrcus", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(Native.Execution, [Context, CurrentSnapshot, observation])!;
        }
        internal IGameObject AddNativeTarget(CrystalTowerActorObservation actor)
        {
            var native = DispatchProxy.Create<IGameObject, CrystalTowerExecutionTests.ActorProxy>();
            ((CrystalTowerExecutionTests.ActorProxy)(object)native).Actor = actor;
            var monster = new ObservedMonster { Key = "fabricated", GameObjectId = actor.GameObjectId, DataId = actor.DataId,
                MapId = Context.MapId, Name = "fabricated Syrcus target", Position = actor.Position, LastSeenUtc = DateTime.UtcNow };
            var table = Get<object>(Native.Execution, "objectTable");
            ((HyperFixatedTargetExecutionTests.ObjectTableProxy)table).Objects.Add(new(native, monster));
            monsters.Add(monster);
            return native;
        }
        public void Dispose() => Native.Dispose();
    }
}
