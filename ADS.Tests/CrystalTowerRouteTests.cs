using System.Numerics;
using System.Reflection;
using ADS.Models;
using ADS.Services;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using static ADS.Services.ExecutionService;
using ServerEventKind = ADS.Services.HigherLowerServerEventTraceService.ServerEventKind;
using ServerEventRow = ADS.Services.HigherLowerServerEventTraceService.ServerEventRow;

namespace ADS.Tests;

public sealed class CrystalTowerRouteTests
{
    [Theory]
    [InlineData("A", "SYNC")]
    [InlineData("B", "SYNC")]
    [InlineData("C", "SYNC")]
    [InlineData("A", "UNSYNC")]
    [InlineData("B", "UNSYNC")]
    [InlineData("C", "UNSYNC")]
    public void RootDispatchWaitsForTheWholeGarmWaveThenRoutesWithAuthoredHeight(string alliance, string mode)
    {
        using var f = new Fixture(new(-77, 16, 383), alliance, mode);
        f.Actors = Garm(true);
        Assert.True(f.Tick());
        Assert.Equal("Garm", f.Stage);
        Assert.DoesNotContain(f.Native.Commands, IsMove);
        f.Actors = [.. f.Actors, Npc(20, 0xD9D, f.Local.Position, true)];
        Assert.True(f.Tick());
        Assert.Equal("Angra", f.Stage);
        Assert.True(f.Tick());
        Assert.Contains("/vnav moveto -146.87 33.72 330.04", f.Native.Commands);
        Assert.Equal(0, f.Waypoint);
    }

    [Fact]
    public void EntranceAdmissionUsesOnlyTheNearbyAuthoredFloorAndDoesNotRequireLoadedEnemies()
    {
        using var f = new Fixture(new(-40, 16, 403.41f));
        Assert.True(f.Tick());
        Assert.Equal("Garm", f.Stage);
        Assert.Contains("/vnav moveto -58.02 16.00 403.41", f.Native.Commands);
        f.Local = f.Local with { Position = new(-58.02f, 16, 403.41f) };
        Assert.True(f.Tick());
        Assert.Equal(1, f.Waypoint);
        Assert.True(f.Tick()); // Arrival at an empty room is not a wave clear.
        Assert.Equal("Garm", f.Stage);
    }

    [Theory]
    [InlineData("wrong-floor")]
    [InlineData("other-room")]
    [InlineData("combat")]
    [InlineData("unknown-position")]
    public void StaleDistantActorsCannotAdmitAnUnobservedRoom(string invalid)
    {
        var position = invalid switch
        {
            "wrong-floor" => new Vector3(-77, 100, 383),
            "other-room" => new Vector3(0, 16, 0),
            "unknown-position" => new Vector3(float.NaN, 16, 403),
            _ => new Vector3(-40, 16, 403.41f),
        };
        using var f = new Fixture(position);
        if (invalid == "combat") f.Context = Context(inCombat: true);
        f.Actors = [Npc(1, 0xE00, new(-147, 34, 297), true)];
        f.Tick();
        Assert.Null(f.Stage);
        Assert.DoesNotContain(f.Native.Commands, IsMove);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("wrong-kind")]
    [InlineData("invalid-id")]
    [InlineData("unknown-position")]
    public void ColdRoomAdmissionHoldsAnAmbiguousBossInsteadOfFallingThroughToGenericProgression(string invalid)
    {
        using var f = new Fixture(new(-147, 34, 297));
        var boss = Npc(1, 0xE00, f.Local.Position);
        f.Actors = invalid switch
        {
            "duplicate" => [boss, boss with { GameObjectId = 2, EntityId = 2 }],
            "wrong-kind" => [boss with { ObjectKind = ObjectKind.EventObj }],
            "invalid-id" => [boss with { EntityId = 0 }],
            _ => [boss with { Position = new(float.NaN, 34, 297) }],
        };
        Assert.True(f.Tick());
        Assert.Equal("Angra", f.Stage);
        Assert.Null(f.Native.Target);
        Assert.DoesNotContain(f.Native.Commands, IsMove);
        Assert.Contains("verified alliance mechanic state", f.Native.Execution.LastStatus);
    }

    [Fact]
    public void WaypointNeedsFreshThreeDimensionalArrivalAndDoesNotAdvanceOnStallOrTransition()
    {
        using var f = ClearedGarm();
        Assert.True(f.Tick());
        var target = new Vector3(-146.87f, 33.72f, 330.04f);
        f.Local = f.Local with { Position = new(target.X, 16, target.Z) };
        f.Actors = [];
        f.Now += TimeSpan.FromMinutes(2);
        Assert.True(f.Tick());
        Assert.Equal(0, f.Waypoint); // Matching X/Z and idle time do not arrive.
        f.Context = Context(betweenAreas: true);
        Assert.True(f.Tick());
        Assert.Equal("Angra", f.Stage);
        Assert.Equal(0, f.Waypoint);
        Assert.Contains("/vnav stop", f.Native.Commands);
        f.Context = Context();
        f.Local = f.Local with { Position = target + new Vector3(0, 2.6f, 0) };
        Assert.True(f.Tick());
        Assert.Equal(0, f.Waypoint);
        f.Local = f.Local with { Position = target + new Vector3(0, 2.4f, 0) };
        Assert.True(f.Tick());
        Assert.Equal(1, f.Waypoint);
        Assert.True(f.Tick());
        Assert.Contains("/vnav moveto -147.10 34.00 323.95", f.Native.Commands);
        f.Local = f.Local with { Position = new(-147.1f, 34, 323.95f) };
        Assert.True(f.Tick());
        Assert.Equal(2, f.Waypoint);
        Assert.Equal("Angra", f.Stage);
    }

    [Theory]
    [InlineData("Angra", 0xE00u, -147f, 34f, 297f, "Queen")]
    [InlineData("FiveHeaded", 0xDFCu, 200f, 82.01f, 179f, "Atomos")]
    [InlineData("Cerberus", 0xDF6u, 0f, 129.3f, -198f, "Transfer")]
    public void SingletonClearRequiresFreshUnambiguousNativeDeathAndRevivalInvalidatesIt(string stage,
        uint dataId, float x, float y, float z, string next)
    {
        using var f = new Fixture(new(x, y, z));
        var boss = Npc(1, dataId, f.Local.Position);
        f.Actors = [boss];
        f.Tick();
        Assert.Equal(stage, f.Stage);
        f.Actors = [];
        Assert.True(f.Tick());
        Assert.Equal(stage, f.Stage);
        f.Actors = [boss with { Dead = null }];
        Assert.True(f.Tick());
        Assert.Equal(stage, f.Stage);
        f.Actors = [boss with { Dead = true }, boss with { GameObjectId = 2, EntityId = 2, Dead = true }];
        Assert.True(f.Tick());
        Assert.Equal(stage, f.Stage);
        f.Actors = [boss with { Dead = true }];
        Assert.True(f.Tick());
        Assert.Equal(next, f.Stage);
        f.Native.Commands.Clear();
        f.Actors = [boss with { GameObjectId = 3, EntityId = 3 }];
        f.Tick();
        Assert.Equal(stage, f.Stage);
        Assert.DoesNotContain(f.Native.Commands, IsMove);
    }

    [Fact]
    public void ADeadQueenCannotAdvanceUntilCloneAndSecondaryWavesActuallyClear()
    {
        using var f = new Fixture(new(130, 63.99f, 265));
        var queen = Npc(1, 0xDA0, f.Local.Position, true);
        f.Actors = [queen];
        Assert.True(f.Tick());
        Assert.Equal("Queen", f.Stage);
        f.Actors = [queen, Npc(2, 0xDA3, f.Local.Position, true)];
        Assert.True(f.Tick());
        Assert.Equal("Queen", f.Stage);
        f.Actors = [.. f.Actors, Npc(3, 0xE73, f.Local.Position, true)];
        Assert.True(f.Tick());
        Assert.Equal("FiveHeaded", f.Stage);
        Assert.True(f.Tick());
        Assert.Contains("/vnav moveto 200.18 81.91 211.35", f.Native.Commands);
    }

    [Fact]
    public void AtomosRequiresThreePositivelyObservedDeadLanesBeforeTheCerberusApproach()
    {
        using var f = new Fixture(new(-31, 95.2f, 49));
        var lanes = new[] { Npc(1, 0xDA4, new(-31, 95.2f, 49), true),
            Npc(2, 0xDA4, new(0, 95.2f, -5), true), Npc(3, 0xDA4, new(31, 95.2f, 49), true) };
        f.Actors = lanes[..2];
        Assert.True(f.Tick());
        Assert.Equal("Atomos", f.Stage);
        f.Actors = [.. lanes[..2], lanes[2] with { Dead = null }];
        Assert.True(f.Tick());
        Assert.Equal("Atomos", f.Stage);
        f.Actors = lanes;
        Assert.True(f.Tick());
        Assert.Equal("Cerberus", f.Stage);
        Assert.True(f.Tick());
        Assert.Contains("/vnav moveto -0.01 129.09 -150.90", f.Native.Commands);
    }

    [Fact]
    public void UnknownFinalTransferHoldsAfterTwoMeasuredApproachesAndNeverNavigatesAcrossFloors()
    {
        using var f = new Fixture(new(0, 129.3f, -198));
        f.Actors = [Npc(1, 0xDF6, f.Local.Position, true)];
        Assert.True(f.Tick());
        Assert.Equal("Transfer", f.Stage);
        Assert.True(f.Tick());
        Assert.Contains("/vnav moveto 0.25 142.01 -311.52", f.Native.Commands);
        f.Local = f.Local with { Position = new(0.25f, 142.01f, -311.52f) };
        f.Actors = [];
        Assert.True(f.Tick());
        Assert.True(f.Tick());
        Assert.Contains("/vnav moveto 0.21 143.65 -320.49", f.Native.Commands);
        f.Local = f.Local with { Position = new(0.21f, 143.65f, -320.49f) };
        Assert.True(f.Tick());
        Assert.Equal(2, f.Waypoint);
        f.Native.Commands.Clear();
        f.Now += TimeSpan.FromMinutes(3);
        Assert.True(f.Tick());
        Assert.Equal("Transfer", f.Stage);
        Assert.DoesNotContain(f.Native.Commands, IsMove);
        f.Local = f.Local with { Position = new(-321.26f, 143.65f, -404.83f) };
        f.Actors = [Npc(2, 0xCFA, new(-300, 280.05f, -400))];
        Assert.True(f.Tick());
        Assert.Equal("Transfer", f.Stage);
        f.Local = f.Local with { Position = new(-321.26f, 280.05f, -404.83f) };
        f.Actors = [];
        Assert.True(f.Tick());
        Assert.Equal("Transfer", f.Stage); // A bare position cannot invent the destination encounter.
        f.Actors = [Npc(2, 0xCFA, new(-300, 280.05f, -400))];
        Assert.True(f.Tick());
        Assert.Equal("Cloud", f.Stage);
        Assert.DoesNotContain(f.Native.Commands, IsMove);
        f.Actors = [f.Actors[0] with { Dead = true, IsCasting = false }];
        Assert.True(f.Tick());
        f.Actors = [];
        Assert.True(f.Tick());
        f.Native.Execution.MarkDutyCompleted(f.Context, 151, 111);
        Assert.False(f.Tick()); // Existing typed completion finally releases route ownership.
    }

    [Fact]
    public void ARealDoomMechanicRetainsPriorityOverRouteProgression()
    {
        using var f = new Fixture(new(-147, 34, 297));
        f.Local = f.Local with { StatusIds = [210] };
        var pad = Npc(20, 0x1E9711, new(-135, 34, 297)) with { ObjectKind = ObjectKind.EventObj };
        f.Actors = [Npc(1, 0xE00, f.Local.Position), pad];
        f.Rows = [new(11, f.Now, ServerEventKind.EObjState, "ESTA", 151, f.Context.MapId,
            (uint)pad.GameObjectId, 0, 409, 4, 0, 0, 0, 0, 0, 0, 0, 0, "fixture", "EventObj",
            pad.GameObjectId, pad.EntityId, pad.DataId, 0, 0, 0, 0, null, pad.Position, null, "", "", "", false)];
        Assert.True(f.Tick());
        Assert.Equal("Angra", f.Stage);
        Assert.Contains("/vnav moveto -135.00 34.00 297.00", f.Native.Commands);
        Assert.DoesNotContain("verified raid route", f.Native.Execution.LastStatus);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("disable")]
    [InlineData("alliance")]
    [InlineData("preset")]
    [InlineData("map")]
    public void OwnershipLossClearsRouteReceiptsAndStopsTheOwnedPath(string loss)
    {
        using var f = ClearedGarm();
        Assert.True(f.Tick());
        Assert.Contains(f.Native.Commands, IsMove);
        if (loss == "cancel") f.Native.Execution.CancelCrystalTower(f.Context);
        else
        {
            if (loss == "alliance") f.Context = Context("B");
            if (loss == "preset")
            {
                var rules = Get<ObjectPriorityRuleService>(f.Native.Execution, "objectPriorityRuleService");
                typeof(ObjectPriorityRuleService).GetField("activePresetName", Flags)!.SetValue(rules, "Other preset");
            }
            if (loss == "map") typeof(DutyContextSnapshot).GetProperty(nameof(DutyContextSnapshot.MapId))!.SetValue(f.Context, 2u);
            typeof(ExecutionService).GetMethod("RefreshCrystalTowerLifetime", Flags)!
                .Invoke(f.Native.Execution, [f.Context, loss != "disable"]);
        }
        Assert.Null(f.Stage);
        Assert.Equal(0, f.Waypoint);
        Assert.Contains("/vnav stop", f.Native.Commands);
        Assert.False(Get<bool>(f.Native.Execution, "navigationActive"));
    }

    [Fact]
    public void AProviderReadThatChangesOwnershipCannotDispatchTheNextRouteMove()
    {
        using var f = new Fixture(new(-40, 16, 403.41f));
        f.Native.Execution.ReadCrystalTowerMovementOverride = () =>
        {
            typeof(ExecutionService).GetProperty(nameof(ExecutionService.CurrentMode))!
                .SetValue(f.Native.Execution, OwnershipMode.Observing);
            return new(false, null, "fixture: no provider");
        };
        Assert.True(f.Tick());
        Assert.Equal(0, f.Waypoint);
        Assert.DoesNotContain(f.Native.Commands, IsMove);
    }

    private static bool IsMove(string command) => command.StartsWith("/vnav moveto", StringComparison.Ordinal);
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    private static T Get<T>(ExecutionService service, string name)
        => (T)typeof(ExecutionService).GetField(name, Flags)!.GetValue(service)!;
    private static DutyContextSnapshot Context(string alliance = "A", bool betweenAreas = false, bool inCombat = false)
        => TestDutyContextFactory.Create(DutyCategory.AllianceRaid, territoryId: 151, cfcId: 111,
            alliance: alliance, betweenAreas: betweenAreas, inCombat: inCombat);
    private static CrystalTowerActorObservation Npc(ulong id, uint dataId, Vector3 position, bool? dead = false)
        => new(id, (uint)id, dataId, ObjectKind.BattleNpc, position, dead, true, false, 0, [], null, null, 1);
    private static CrystalTowerActorObservation[] Garm(bool dead)
        => [Npc(1, 0xD9C, new(-80, 16, 383), dead), Npc(2, 0xD9C, new(-77, 16, 383), dead),
            Npc(3, 0xD9C, new(-74, 16, 383), dead)];
    private static Fixture ClearedGarm()
    {
        var f = new Fixture(new(-77, 16, 383));
        f.Actors = [.. Garm(true), Npc(20, 0xD9D, f.Local.Position, true)];
        Assert.True(f.Tick());
        Assert.Equal("Angra", f.Stage);
        return f;
    }

    private sealed class Fixture : IDisposable
    {
        internal Fixture(Vector3 position, string alliance = "A", string mode = "SYNC")
        {
            Context = CrystalTowerRouteTests.Context(alliance);
            Local = new(1123, 123, 0, ObjectKind.Pc, position, false, true, false, 0, [], null, 25);
            Native = new(position, initialRules: new ObjectPriorityRuleManifest
            {
                Rules = [new() { TerritoryTypeId = 151, ContentFinderConditionId = 111, ObjectKind = "BattleNpc",
                    BaseId = 0xD9C, Classification = "Required", CrystalTowerStrategy = mode }],
            });
            Native.Execution.ReadUtcNow = () => Now;
            Native.Execution.CrystalTowerMovementCharacterIdOverride = () => 1;
            Native.Execution.CrystalTowerMovementLocalEntityIdOverride = () => Local.EntityId;
            Native.Execution.ReadCrystalTowerMovementOverride = () => new(false, null, "fixture: no provider");
            Native.Execution.ReadInteractionVbmOverride = () => new(false, null, "fixture: no provider");
            Native.Execution.QueryInteractionDialog = () => false;
            Native.Execution.ReadCrystalTowerEvents = () => Rows;
            Native.Execution.CrystalTowerObservationOverride = _ => new(true, "fixture", Local.EntityId, Local, false,
                [], Actors, [], Rows.Select(row => row.Sequence).DefaultIfEmpty(10ul).Max());
        }

        internal HyperFixatedTargetExecutionTests.Fixture Native { get; }
        internal DutyContextSnapshot Context;
        internal CrystalTowerActorObservation Local;
        internal CrystalTowerActorObservation[] Actors = [];
        internal ServerEventRow[] Rows = [];
        internal DateTime Now = new(2026, 10, 22, 1, 0, 0, DateTimeKind.Utc);
        internal string? Stage => Get<object?>(Native.Execution, "crystalTowerRouteStage")?.ToString();
        internal int Waypoint => Get<int>(Native.Execution, "crystalTowerRouteWaypoint");
        internal bool Tick()
        {
            var table = Get<HyperFixatedTargetExecutionTests.ObjectTableProxy>(Native.Execution, "objectTable");
            ((HyperFixatedTargetExecutionTests.GameObjectProxy)(object)table.LocalPlayer!).Position = Local.Position;
            table.Objects.Clear();
            var monsters = new List<ObservedMonster>();
            foreach (var actor in Actors.Where(actor => actor.ObjectKind == ObjectKind.BattleNpc))
            {
                var native = DispatchProxy.Create<IGameObject, CrystalTowerExecutionTests.ActorProxy>();
                ((CrystalTowerExecutionTests.ActorProxy)(object)native).Actor = actor;
                var observed = new ObservedMonster { Key = $"route:{actor.GameObjectId}", GameObjectId = actor.GameObjectId,
                    DataId = actor.DataId, MapId = Context.MapId, Name = "fixture", Position = actor.Position, LastSeenUtc = Now };
                table.Objects.Add(new(native, observed));
                monsters.Add(observed);
            }
            // Exercise the common production dispatcher, not the new route helpers directly.
            return (bool)typeof(ExecutionService).GetMethod("TryHandleCrystalTower", Flags)!.Invoke(Native.Execution,
                [Context, new ObservationSnapshot { LiveMonsters = monsters, LiveFollowTargets = [], MonsterGhosts = [],
                    LiveInteractables = [], InteractableGhosts = [] }])!;
        }
        public void Dispose() => Native.Dispose();
    }
}
