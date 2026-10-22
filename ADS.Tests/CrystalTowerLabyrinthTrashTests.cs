using System.Collections;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using ADS.Models;
using ADS.Services;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using static ADS.Services.ExecutionService;
using NativeGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace ADS.Tests;

public sealed class CrystalTowerLabyrinthTrashTests
{
    // Independently transcribed from pinned Sapphire LVD_bnpc_11 placement rows.
    private static readonly (uint Layout, uint Base)[][] Packs =
    [
        [(4494643, 2398), (4494661, 2396), (4494665, 2394), (4494667, 2395),
            (4494671, 2392), (4494679, 2397), (4494681, 2393), (4494693, 2398)],
        [(4494658, 2399), (4494659, 2399), (4494660, 2399), (4494662, 2399),
            (4494663, 2399), (4494668, 2399), (4494695, 2399), (4494701, 2399), (4494686, 2400), (4494692, 2400)],
        [(4494649, 2402), (4494702, 2401), (4494703, 2401), (4494706, 2401), (4534403, 2402), (4534404, 2402)],
    ];
    private static readonly Vector3[] Combat = [new(-371.92f, 42, 273.24f), new(-423.9f, 41.6f, 279.38f), new(-441.7f, 42, 218.71f)];
    private static readonly Vector3 FirstApproach = new(-353.5f, 41.6f, 279.17f);
    private static readonly Vector3 BoneApproach = new(-452.03f, 25.53f, 69.85f);
    private static readonly Vector3 EntrancePosition = new(-110.032799f, 30.37269f, 417.61499f);

    [Theory]
    [InlineData("SYNC")]
    [InlineData("UNSYNC")]
    public void ActualEntranceDispatchFollowsAuthoredXyzAndOnlyMeasuredArrivalAdvances(string mode)
    {
        using var f = new Fixture(mode);
        f.Local = f.Local with { Position = EntrancePosition };
        f.Actors = [Entrance(), .. AllAlive()];
        Assert.True(f.Tick());
        Assert.True(f.Active);
        Assert.Equal(0, f.Waypoint);
        Assert.Contains("/vnav moveto -353.50 41.60 279.17", f.Native.Commands);
        f.Local = f.Local with { Position = FirstApproach + new Vector3(0, 10, 0) };
        f.Now += TimeSpan.FromMinutes(3);
        Assert.True(f.Tick());
        Assert.Equal(0, f.Waypoint);
        Assert.False(f.Arrived);
        f.Context = TestDutyContextFactory.Create(DutyCategory.AllianceRaid, territoryId: 174, cfcId: 92, alliance: "A", betweenAreas: true);
        Assert.True(f.Tick());
        Assert.Equal(0, f.Waypoint);
        f.Context = Context();
        f.Local = f.Local with { Position = FirstApproach };
        Assert.True(f.Tick());
        Assert.True(f.Arrived);
        Assert.Equal(0, f.Waypoint);
        Assert.True(f.Tick());
        Assert.Equal(1, f.Waypoint);
        Assert.False(f.Arrived);
        Assert.True(f.Tick());
        Assert.Contains("/vnav moveto -371.92 42.00 273.24", f.Native.Commands);
        f.Local = f.Local with { Position = Combat[0] };
        Assert.True(f.Tick());
        Assert.True(f.Tick());
        Assert.Equal(Pack(0)[0].GameObjectId, f.Native.Target?.GameObjectId);
    }

    [Theory]
    [InlineData(0, 8)]
    [InlineData(1, 10)]
    [InlineData(2, 6)]
    public void ExactPlacedMembershipIsRequiredAndAnExtraSameSpeciesActorCannotReplaceIt(int pack, int count)
    {
        using var f = new Fixture();
        PrimeToPack(f, pack);
        var required = Pack(pack);
        Assert.Equal(count, required.Length);
        var omitted = required[^1];
        f.Actors = f.Actors.Where(actor => actor.LayoutId != omitted.LayoutId)
            .Select(actor => InPack(actor, pack) ? actor with { Dead = true } : actor).ToArray();
        Assert.True(f.Tick());
        Assert.Equal(pack + 1, f.Waypoint);
        Assert.Null(f.Native.Target);
        var unrelated = omitted with { LayoutId = 9999999, GameObjectId = 99999, EntityId = 99999, Dead = true };
        f.Actors = [.. f.Actors, unrelated];
        Assert.True(f.Tick());
        Assert.Equal(pack + 1, f.Waypoint);
        f.Actors = [.. f.Actors, omitted with { Dead = true }];
        Assert.True(f.Tick());
        Assert.Equal(pack + 2, f.Waypoint);
    }

    [Theory]
    [InlineData("unknown-death")]
    [InlineData("missing-layout")]
    [InlineData("duplicate-layout")]
    [InlineData("wrong-base")]
    [InlineData("native-base")]
    [InlineData("native-entity")]
    [InlineData("native-position")]
    [InlineData("native-death")]
    [InlineData("native-layout")]
    [InlineData("no-address")]
    public void UnknownOrMismatchedNativeMembershipCannotClearOrSelectAnArbitraryIdentity(string invalid)
    {
        using var f = new Fixture();
        PrimeToPack(f, 0);
        var first = Pack(0)[0];
        var correct = f.Actors.Select(actor => InPack(actor, 0) ? actor with { Dead = true } : actor).ToArray();
        f.Actors = correct.Select(actor => actor.LayoutId != first.LayoutId ? actor : invalid switch
        {
            "unknown-death" => actor with { Dead = null },
            "missing-layout" => actor with { LayoutId = null },
            "wrong-base" => actor with { DataId = 1234 },
            _ => actor,
        }).ToArray();
        if (invalid == "duplicate-layout") f.Actors = [.. f.Actors, first with { GameObjectId = 999, EntityId = 999, Dead = true }];
        f.NativeOverride = actor => actor.GameObjectId != first.GameObjectId ? actor : invalid switch
        {
            "native-base" => actor with { DataId = 1234 },
            "native-entity" => actor with { EntityId = 999 },
            "native-position" => actor with { Position = actor.Position + Vector3.UnitX },
            "native-death" => actor with { Dead = false },
            _ => actor,
        };
        if (invalid == "native-layout") f.LayoutOverride = actor => actor.GameObjectId == first.GameObjectId ? 999999u : actor.LayoutId ?? 0;
        f.NoAddress = invalid == "no-address";
        Assert.True(f.Tick());
        Assert.Equal(1, f.Waypoint);
        Assert.Null(f.Native.Target);
        Assert.Contains("verified alliance mechanic state", f.Native.Execution.LastStatus);
        f.Actors = correct;
        f.NativeOverride = null;
        f.LayoutOverride = null;
        f.NoAddress = false;
        Assert.True(f.Tick());
        Assert.Equal(2, f.Waypoint);
    }

    [Fact]
    public void PositiveDeathsSurviveDisappearanceButAMissingLivingActorDoesNotBecomeDead()
    {
        using var f = new Fixture();
        PrimeToPack(f, 0);
        var last = Pack(0)[^1];
        f.Actors = Pack(0).Select(actor => actor.GameObjectId == last.GameObjectId ? actor : actor with { Dead = true }).ToArray();
        Assert.True(f.Tick());
        Assert.Equal(last.GameObjectId, f.Native.Target?.GameObjectId);
        f.Actors = [];
        Assert.True(f.Tick());
        Assert.Equal(1, f.Waypoint);
        Assert.Null(f.Native.Target);
        f.Actors = [last with { Dead = true }];
        Assert.True(f.Tick());
        Assert.Equal(2, f.Waypoint);
    }

    [Theory]
    [InlineData("object")]
    [InlineData("entity")]
    [InlineData("revival")]
    public void SameLayoutReplacementOrRevivalRewindsAndCannotReuseTheOldPacksCorpses(string change)
    {
        using var f = new Fixture();
        PrimeToPack(f, 0);
        f.Actors = f.Actors.Select(actor => InPack(actor, 0) ? actor with { Dead = true } : actor).ToArray();
        Assert.True(f.Tick());
        Assert.Equal(2, f.Waypoint);
        var first = Pack(0)[0];
        f.Actors = f.Actors.Select(actor => actor.LayoutId != first.LayoutId ? actor : change switch
        {
            "object" => actor with { GameObjectId = 9001, EntityId = 9001 },
            "entity" => actor with { EntityId = 9001 },
            _ => actor with { Dead = false },
        }).ToArray();
        Assert.True(f.Tick());
        Assert.Equal(1, f.Waypoint);
        Assert.True(f.Tick());
        Assert.Equal(1, f.Waypoint);
        f.Actors = f.Actors.Select(actor => InPack(actor, 0) ? actor with { Dead = true } : actor).ToArray();
        Assert.True(f.Tick());
        Assert.Equal(1, f.Waypoint); // A newly dead replacement alone cannot re-credit seven older corpses.
        f.Actors = f.Actors.Select(actor => InPack(actor, 0) ? actor with { Dead = false } : actor).ToArray();
        Assert.True(f.Tick());
        Assert.True(f.Tick());
        f.Actors = f.Actors.Select(actor => InPack(actor, 0) ? actor with { Dead = true } : actor).ToArray();
        Assert.True(f.Tick());
        Assert.Equal(2, f.Waypoint);
    }

    [Fact]
    public void CancellationClearsAll24SlotsAndLoadedOldCorpsesCannotCompleteANewOwner()
    {
        using var f = new Fixture();
        PrimeToPack(f, 0);
        Assert.Equal(24, f.KnownActors);
        f.Actors = f.Actors.Select(actor => InPack(actor, 0) ? actor with { Dead = true } : actor).ToArray();
        Assert.True(f.Tick());
        Assert.Equal(2, f.Waypoint);
        f.Native.Execution.CancelCrystalTower(f.Context);
        Assert.False(f.Active);
        Assert.Equal(0, f.KnownActors);
        Assert.True(f.Tick());
        Assert.True(f.Tick());
        Assert.Equal(1, f.Waypoint);
        Assert.Null(f.Native.Target);
    }

    [Fact]
    public void ThreeVerifiedPacksLeadToTheBoneXyzApproachAndWaitForActualNativeRoomAdmission()
    {
        using var f = new Fixture();
        PrimeToPack(f, 2);
        f.Actors = f.Actors.Select(actor => InPack(actor, 2) ? actor with { Dead = true } : actor).ToArray();
        Assert.True(f.Tick());
        Assert.Equal(4, f.Waypoint);
        Assert.True(f.Tick());
        Assert.Contains("/vnav moveto -452.03 25.53 69.85", f.Native.Commands);
        f.Local = f.Local with { Position = BoneApproach + new Vector3(0, 16, 0) };
        Assert.True(f.Tick());
        Assert.False(f.Arrived);
        f.Local = f.Local with { Position = BoneApproach };
        Assert.True(f.Tick());
        Assert.True(f.Arrived);
        f.Native.Commands.Clear();
        Assert.True(f.Tick());
        Assert.DoesNotContain(f.Native.Commands, IsMove);
        Assert.Null(f.RegularStage);
        Assert.False(Get<bool>(f.Native.Execution, "crystalTowerBoneComplete"));
        var bone = new CrystalTowerActorObservation(6000, 6000, 0x92B, ObjectKind.BattleNpc,
            BoneApproach + Vector3.UnitX, false, true, false, 0, [], null, null);
        f.Actors = [.. f.Actors, bone];
        Assert.True(f.Tick());
        Assert.Equal("Bone", f.RegularStage);
        Assert.False(f.Active);
        Assert.True(f.Retired);
        Assert.False(Get<bool>(f.Native.Execution, "crystalTowerBoneComplete"));
    }

    [Theory]
    [InlineData("floor")]
    [InlineData("later-room")]
    [InlineData("species-only")]
    [InlineData("wrong-entrance")]
    public void LoadedTrashOrAnUnverifiedEntranceCannotSeizeAnotherSituation(string invalid)
    {
        using var f = new Fixture();
        f.Actors = AllAlive();
        if (invalid == "floor") f.Local = f.Local with { Position = Combat[0] + new Vector3(0, 100, 0) };
        if (invalid == "later-room") f.Local = f.Local with { Position = new(440.4f, 66.21f, 280) };
        if (invalid == "species-only") f.Actors = f.Actors.Select(actor => actor with { LayoutId = 9999999 }).ToArray();
        if (invalid == "wrong-entrance")
        {
            f.Local = f.Local with { Position = EntrancePosition };
            f.Actors = [Entrance() with { LayoutId = 9999999 }, .. f.Actors];
        }
        Assert.False(f.Tick());
        Assert.False(f.Active);
        Assert.Empty(f.Native.Commands);
    }

    [Fact]
    public void LaterNativeRoomColdAdmissionRetiresOpeningWithoutClaimingItsMissingDeaths()
    {
        using var f = new Fixture();
        Assert.True(f.Tick());
        Assert.True(f.Active);
        f.Local = f.Local with { Position = new(440.4f, 66.21f, 280) };
        f.Actors = [.. AllAlive(), new CrystalTowerActorObservation(6000, 6000, 0x92E, ObjectKind.BattleNpc,
            f.Local.Position, false, true, false, 0, [], null, null)];
        Assert.True(f.Tick());
        Assert.Equal("Thanatos", f.RegularStage);
        Assert.True(f.Retired);
        Assert.False(f.Active);
        Assert.Equal(1, f.Waypoint);
    }

    private static void PrimeToPack(Fixture f, int pack)
    {
        for (var current = 0; current < pack; ++current)
        {
            f.Local = f.Local with { Position = Combat[current] };
            Assert.True(f.Tick());
            f.Actors = f.Actors.Select(actor => InPack(actor, current) ? actor with { Dead = true } : actor).ToArray();
            Assert.True(f.Tick());
            Assert.Equal(current + 2, f.Waypoint);
        }
        f.Local = f.Local with { Position = Combat[pack] };
        Assert.True(f.Tick());
        Assert.True(f.Arrived);
        Assert.Equal(pack + 1, f.Waypoint);
    }
    private static bool InPack(CrystalTowerActorObservation actor, int pack) => Packs[pack].Any(member => member.Layout == actor.LayoutId);
    private static CrystalTowerActorObservation[] AllAlive() => Enumerable.Range(0, 3).SelectMany(pack => Pack(pack)).ToArray();
    private static CrystalTowerActorObservation[] Pack(int pack) => Packs[pack].Select((member, index) =>
        new CrystalTowerActorObservation((ulong)(100 + pack * 100 + index), (uint)(500 + pack * 100 + index), member.Base,
            ObjectKind.BattleNpc, Combat[pack] + Vector3.UnitX * (1 + index * 0.1f), false, true, false, 0, [], null, null,
            LayoutId: member.Layout)).ToArray();
    private static CrystalTowerActorObservation Entrance() => new(10000, 0xE0000000, 2000182, ObjectKind.EventObj,
        EntrancePosition, null, false, false, 0, null, 0, null, LayoutId: 4303363);
    private static DutyContextSnapshot Context() => TestDutyContextFactory.Create(DutyCategory.AllianceRaid,
        territoryId: 174, cfcId: 92, alliance: "A");
    private static bool IsMove(string command) => command.StartsWith("/vnav moveto", StringComparison.Ordinal);
    private static T Get<T>(ExecutionService service, string name) => (T)typeof(ExecutionService)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;

    public class ActorProxy : DispatchProxy
    {
        internal CrystalTowerActorObservation Actor = null!;
        internal nint Address;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
        {
            "get_GameObjectId" => Actor.GameObjectId,
            "get_EntityId" => Actor.EntityId,
            "get_BaseId" => Actor.DataId,
            "get_ObjectKind" => Actor.ObjectKind,
            "get_Position" => Actor.Position,
            "get_IsDead" => Actor.Dead ?? false,
            "get_IsTargetable" => Actor.Targetable ?? false,
            "get_Address" => Address,
            _ => method.ReturnType == typeof(void) || !method.ReturnType.IsValueType ? null : Activator.CreateInstance(method.ReturnType),
        };
    }

    private sealed unsafe class Fixture : IDisposable
    {
        internal Fixture(string mode = "SYNC")
        {
            Native = new(Local.Position, initialRules: new ObjectPriorityRuleManifest { Rules =
                [new() { TerritoryTypeId = 174, ContentFinderConditionId = 92, ObjectKind = "BattleNpc", BaseId = 2398,
                    Classification = "Required", CrystalTowerStrategy = mode }] });
            Native.Execution.ReadUtcNow = () => Now;
            Native.Execution.CrystalTowerMovementCharacterIdOverride = () => 1;
            Native.Execution.CrystalTowerMovementLocalEntityIdOverride = () => Local.EntityId;
            Native.Execution.ReadCrystalTowerMovementOverride = () => new(false, null, "fixture: no provider");
            Native.Execution.ReadInteractionVbmOverride = () => new(false, null, "fixture: no provider");
            Native.Execution.CrystalTowerObservationOverride = _ => new(true, "native opening fixture", Local.EntityId, Local,
                false, [], Actors, [], 10);
        }
        internal HyperFixatedTargetExecutionTests.Fixture Native { get; }
        internal CrystalTowerActorObservation Local = new(1123, 123, 0, ObjectKind.Pc, Combat[0], false, true, false, 0, [], null, 25);
        internal DutyContextSnapshot Context = CrystalTowerLabyrinthTrashTests.Context();
        internal CrystalTowerActorObservation[] Actors = AllAlive();
        internal DateTime Now = new(2026, 10, 22, 1, 0, 0, DateTimeKind.Utc);
        internal Func<CrystalTowerActorObservation, CrystalTowerActorObservation>? NativeOverride;
        internal Func<CrystalTowerActorObservation, uint>? LayoutOverride;
        internal bool NoAddress;
        private readonly List<nint> allocations = [];
        internal int Waypoint => Get<int>(Native.Execution, "labyrinthTrashWaypoint");
        internal bool Arrived => Get<bool>(Native.Execution, "labyrinthTrashArrived");
        internal bool Active => Get<bool>(Native.Execution, "labyrinthTrashActive");
        internal bool Retired => Get<bool>(Native.Execution, "labyrinthTrashRetired");
        internal int KnownActors => Get<IDictionary>(Native.Execution, "labyrinthTrashActors").Count;
        internal string? RegularStage => Get<object?>(Native.Execution, "labyrinthRouteStage")?.ToString();
        internal bool Tick()
        {
            foreach (var allocation in allocations) NativeMemory.Free((void*)allocation);
            allocations.Clear();
            var table = Get<HyperFixatedTargetExecutionTests.ObjectTableProxy>(Native.Execution, "objectTable");
            ((HyperFixatedTargetExecutionTests.GameObjectProxy)(object)table.LocalPlayer!).Position = Local.Position;
            table.Objects.Clear();
            var monsters = new List<ObservedMonster>();
            foreach (var actor in Actors)
            {
                var native = DispatchProxy.Create<IGameObject, ActorProxy>();
                var proxy = (ActorProxy)(object)native;
                proxy.Actor = NativeOverride?.Invoke(actor) ?? actor;
                if (!NoAddress)
                {
                    var memory = (NativeGameObject*)NativeMemory.AllocZeroed((nuint)sizeof(NativeGameObject));
                    memory->LayoutId = LayoutOverride?.Invoke(actor) ?? actor.LayoutId ?? 0;
                    proxy.Address = (nint)memory;
                    allocations.Add(proxy.Address);
                }
                var monster = new ObservedMonster { Key = $"trash:{actor.GameObjectId}", GameObjectId = actor.GameObjectId,
                    DataId = actor.DataId, MapId = Context.MapId, Name = "fixture", Position = actor.Position, LastSeenUtc = Now };
                table.Objects.Add(new(native, monster));
                if (actor.ObjectKind == ObjectKind.BattleNpc) monsters.Add(monster);
            }
            return (bool)typeof(ExecutionService).GetMethod("TryHandleCrystalTower", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(Native.Execution, [Context, new ObservationSnapshot { LiveMonsters = monsters, LiveFollowTargets = [],
                    LiveInteractables = [], MonsterGhosts = [], InteractableGhosts = [] }])!;
        }
        public void Dispose()
        {
            Native.Dispose();
            foreach (var allocation in allocations) NativeMemory.Free((void*)allocation);
        }
    }
}
