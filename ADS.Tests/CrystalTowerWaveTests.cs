using System.Numerics;
using System.Reflection;
using ADS.Models;
using ADS.Services;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using Lumina.Excel;
using static ADS.Services.ExecutionService;

namespace ADS.Tests;

public sealed class CrystalTowerWaveTests
{
    [Theory]
    [InlineData("SYNC")]
    [InlineData("UNSYNC")]
    public void GarmDoesNotClearUntilAnActuallyObservedAddWaveIsExplicitlyDead(string mode)
    {
        using var f = new Fixture(mode: mode);
        f.Actors = Garm(dead: true);
        Assert.True(f.Tick());
        Assert.Null(f.Native.Target);
        var add = Npc(20, 0xD9D, f.Local.Position + Vector3.UnitX);
        f.Actors = [.. f.Actors, add];
        Assert.True(f.Tick());
        Assert.Equal(add.GameObjectId, f.Native.Target?.GameObjectId);
        f.Actors = Garm(dead: true);
        Assert.True(f.Tick()); // The living add disappeared; its death was not observed.
        Assert.Null(f.Native.Target);
        f.Actors = [.. f.Actors, add with { Dead = true }];
        Assert.False(f.Tick());
        f.Actors = [];
        Assert.False(f.Tick()); // Positive prior death evidence may survive disappearance.
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void APartialGarmViewCannotCompleteThreeBosses(int visibleCorpses)
    {
        using var f = new Fixture();
        f.Actors = Garm();
        Assert.True(f.Tick());
        f.Actors = [.. Garm(dead: true).Take(visibleCorpses), Npc(20, 0xD9F, f.Local.Position) with { Dead = true }];
        Assert.True(f.Tick());
        Assert.Contains("verified alliance mechanic state", f.Native.Execution.LastStatus);
    }

    [Theory]
    [InlineData(0xD9Du)]
    [InlineData(0xD9Fu)]
    [InlineData(0xD9Eu)]
    public void EveryObservedGarmAddMustDieAndIsSelectedBeforeLivingBosses(uint addId)
    {
        using var f = new Fixture();
        var add = Npc(20, addId, f.Local.Position + Vector3.UnitX * 2);
        f.Actors = [.. Garm(), add];
        Assert.True(f.Tick());
        Assert.Equal(20ul, f.Native.Target?.GameObjectId);
        f.Actors = [.. Garm(dead: true), add with { Dead = null }];
        Assert.True(f.Tick());
        f.Actors = [.. Garm(dead: true), add with { Dead = true }];
        Assert.False(f.Tick());
    }

    [Theory]
    [InlineData("SYNC", 0xE73u)]
    [InlineData("SYNC", 0xE74u)]
    [InlineData("SYNC", 0xDA1u)]
    [InlineData("SYNC", 0xDA2u)]
    [InlineData("UNSYNC", 0xE73u)]
    [InlineData("UNSYNC", 0xE74u)]
    [InlineData("UNSYNC", 0xDA1u)]
    [InlineData("UNSYNC", 0xDA2u)]
    public void QueenRequiresADeadQueenObservedCloneAndAnObservedSecondaryWave(string mode, uint secondary)
    {
        using var f = new Fixture(queen: true, mode: mode);
        f.Actors = [Queen(dead: true)];
        Assert.True(f.Tick()); // Neither UNSYNC nor a corpse implies skipped phases succeeded.
        f.Actors = [Queen(dead: true), Clone(dead: true)];
        Assert.True(f.Tick());
        f.Actors = [.. f.Actors, Npc(30, secondary, f.Local.Position) with { Dead = true }];
        Assert.False(f.Tick());
    }

    [Fact]
    public void DeadGatesCannotSkipAnAddWaveThatWasActuallyObservedAlive()
    {
        using var f = new Fixture(queen: true);
        var add = Npc(30, 0xDA1, f.Local.Position + Vector3.UnitX);
        f.Actors = [Queen(dead: true), Clone(dead: true), Npc(31, 0xE74, f.Local.Position) with { Dead = true }, add];
        Assert.True(f.Tick());
        Assert.Equal(add.GameObjectId, f.Native.Target?.GameObjectId);
        f.Actors = f.Actors.Where(actor => actor.GameObjectId != add.GameObjectId).ToArray();
        Assert.True(f.Tick());
        Assert.Null(f.Native.Target);
        f.Actors = [.. f.Actors, add with { Dead = true }];
        Assert.False(f.Tick());
    }

    [Fact]
    public void AVerifiedDeadAddWaveCanFinishQueenEvenWhenEarlierGatesRemainUncleared()
    {
        using var f = new Fixture(queen: true);
        f.Actors = [Queen(dead: true), Clone(dead: true), Npc(31, 0xE73, f.Local.Position) with { Targetable = false },
            Npc(30, 0xDA2, f.Local.Position) with { Dead = true }];
        Assert.False(f.Tick()); // Exact upstream gatesCleared OR addsCleared predicate.
    }

    [Theory]
    [InlineData(0xDA0u)]
    [InlineData(0xDA3u)]
    public void MissingOrUnknownPrimaryAndCloneDeathDoNotClearQueen(uint unresolved)
    {
        using var f = new Fixture(queen: true);
        var actors = new[] { Queen(dead: true), Clone(dead: true), Npc(30, 0xE73, f.Local.Position) with { Dead = true } };
        f.Actors = actors.Select(actor => actor.DataId == unresolved ? actor with { Dead = null } : actor).ToArray();
        Assert.True(f.Tick());
        f.Actors = f.Actors.Where(actor => actor.DataId != unresolved).ToArray();
        Assert.True(f.Tick());
        f.Actors = actors;
        Assert.False(f.Tick());
    }

    [Theory]
    [InlineData(0xE73u, 0xDA1u, 0xDA3u)]
    [InlineData(0xE74u, 0xDA2u, 0xDA0u)]
    public void QueenTargetsNativeGateThenAddThenBossInReviewedPriorityOrder(uint gateId, uint addId, uint bossId)
    {
        using var f = new Fixture(queen: true);
        var gate = Npc(30, gateId, f.Local.Position + Vector3.UnitX * 3);
        var add = Npc(31, addId, f.Local.Position + Vector3.UnitX * 2);
        var boss = bossId == 0xDA0 ? Queen() : Clone();
        f.Actors = bossId == 0xDA0 ? [gate, add, boss] : [gate, add, boss, Queen(dead: true)];
        Assert.True(f.Tick());
        Assert.Equal(gate.GameObjectId, f.Native.Target?.GameObjectId);
        f.Actors = f.Actors.Select(actor => actor.GameObjectId == gate.GameObjectId ? actor with { Dead = true } : actor).ToArray();
        Assert.True(f.Tick());
        Assert.Equal(add.GameObjectId, f.Native.Target?.GameObjectId);
        f.Actors = f.Actors.Select(actor => actor.GameObjectId == add.GameObjectId ? actor with { Dead = true } : actor).ToArray();
        Assert.True(f.Tick());
        Assert.Equal(boss.GameObjectId, f.Native.Target?.GameObjectId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnObservedRevivalCannotReuseThePreviousAttemptsDeadSecondaryWave(bool queen)
    {
        using var f = new Fixture(queen);
        f.Actors = queen ? [Queen(dead: true), Clone(dead: true), Npc(30, 0xE73, f.Local.Position) with { Dead = true }]
            : [.. Garm(dead: true), Npc(30, 0xD9E, f.Local.Position) with { Dead = true }];
        Assert.False(f.Tick());
        f.Actors = queen ? [Queen()] : Garm();
        Assert.True(f.Tick());
        f.Actors = queen ? [Queen(dead: true)] : Garm(dead: true);
        Assert.True(f.Tick());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OwnerCancellationClearsObservedWaveEvidence(bool queen)
    {
        using var f = new Fixture(queen);
        f.Actors = queen ? [Queen(dead: true), Clone(dead: true), Npc(30, 0xDA1, f.Local.Position) with { Dead = true }]
            : [.. Garm(dead: true), Npc(30, 0xD9F, f.Local.Position) with { Dead = true }];
        Assert.False(f.Tick());
        f.Native.Execution.CancelCrystalTower(f.Context);
        f.Actors = queen ? [Queen(dead: true)] : Garm(dead: true);
        Assert.True(f.Tick());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnknownDeathReadbackAndNewLiveAddsInvalidatePreviouslyPositiveWaveCompletion(bool queen)
    {
        using var f = new Fixture(queen);
        var secondary = Npc(30, queen ? 0xDA1u : 0xD9Fu, f.Local.Position) with { Dead = true };
        CrystalTowerActorObservation[] dead = queen ? [Queen(dead: true), Clone(dead: true), secondary]
            : [.. Garm(dead: true), secondary];
        f.Actors = dead;
        Assert.False(f.Tick());
        Assert.True(queen ? f.Native.Execution.CrystalTowerQueenWaveComplete : f.Native.Execution.CrystalTowerGarmWaveComplete);
        f.Actors = dead.Select(actor => actor.GameObjectId == secondary.GameObjectId ? actor with { Dead = null } : actor).ToArray();
        Assert.True(f.Tick());
        Assert.False(queen ? f.Native.Execution.CrystalTowerQueenWaveComplete : f.Native.Execution.CrystalTowerGarmWaveComplete);
        f.Actors = dead;
        Assert.False(f.Tick());
        f.Actors = [.. dead, secondary with { GameObjectId = 31, EntityId = 31, Dead = false }];
        Assert.True(f.Tick());
        Assert.Equal(31ul, f.Native.Target?.GameObjectId);
        Assert.False(queen ? f.Native.Execution.CrystalTowerQueenWaveComplete : f.Native.Execution.CrystalTowerGarmWaveComplete);
    }

    [Fact]
    public void ReplacementGarmBossIdentitiesCannotReusePreviouslyDeadAdds()
    {
        using var f = new Fixture();
        f.Actors = [.. Garm(dead: true), Npc(30, 0xD9E, f.Local.Position) with { Dead = true }];
        Assert.False(f.Tick());
        f.Actors = Garm(dead: true).Select(actor => actor with { GameObjectId = actor.GameObjectId + 100,
            EntityId = actor.EntityId + 100 }).ToArray();
        Assert.True(f.Tick());
        Assert.False(f.Native.Execution.CrystalTowerGarmWaveComplete);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActorsInAnotherArenaCannotAdmitAWave(bool queen)
    {
        using var f = new Fixture(queen);
        f.Local = f.Local with { Position = Vector3.Zero };
        f.Actors = queen ? [Queen(), Clone()] : Garm();
        Assert.False(f.Tick());
        Assert.Null(f.Native.Target);
        Assert.Empty(f.Native.Commands);
    }

    [Theory]
    [InlineData("duplicate-object")]
    [InlineData("duplicate-entity")]
    [InlineData("wrong-kind")]
    [InlineData("nonfinite")]
    [InlineData("fourth-garm")]
    public void AmbiguousWaveIdentityHoldsWithoutSelectingAnArbitraryActor(string invalid)
    {
        using var f = new Fixture();
        var actors = Garm();
        f.Actors = invalid switch
        {
            "duplicate-object" => [.. actors, actors[0]],
            "duplicate-entity" => [.. actors, actors[0] with { GameObjectId = 90 }],
            "wrong-kind" => [actors[0] with { ObjectKind = ObjectKind.Pc }, .. actors.Skip(1)],
            "nonfinite" => [actors[0] with { Position = new(float.NaN, 100, 383) }, .. actors.Skip(1)],
            _ => [.. actors, actors[0] with { GameObjectId = 90, EntityId = 90 }],
        };
        Assert.True(f.Tick());
        Assert.Null(f.Native.Target);
        Assert.DoesNotContain(f.Native.Commands, command => command.StartsWith("/vnav moveto"));
    }

    [Fact]
    public void QueenFalseChestUsesExistingNativeInteractionAndStillWaitsForTheClone()
    {
        using var f = new Fixture(queen: true);
        var chest = new CrystalTowerActorObservation(40, 0xE0000000, 0x1E9709, ObjectKind.EventObj,
            f.Local.Position + Vector3.UnitX, null, true, null, null, null, 0, null);
        f.Actors = [Queen(dead: true), chest];
        Assert.True(f.Tick());
        Assert.Equal(new[] { 40ul }, f.Interactions);
        f.Actors = [Queen(dead: true)];
        Assert.True(f.Tick());
        Assert.Single(f.Interactions);
        f.Actors = [Queen(dead: true), Clone(dead: true), Npc(30, 0xE73, f.Local.Position) with { Dead = true }];
        Assert.False(f.Tick());
    }

    [Theory]
    [InlineData("unmatched")]
    [InlineData("wrong-base")]
    [InlineData("not-targetable")]
    public void FalseChestRequiresMatchingLiveNativeIdentity(string invalid)
    {
        using var f = new Fixture(queen: true);
        var chest = new CrystalTowerActorObservation(40, 0xE0000000, 0x1E9709, ObjectKind.EventObj,
            f.Local.Position + Vector3.UnitX, null, true, null, null, null, 0, null);
        f.Actors = [Queen(dead: true), chest];
        f.ExcludeInteractableObservation = invalid == "unmatched";
        f.NativeOverride = actor => actor.GameObjectId != chest.GameObjectId ? actor : invalid switch
        {
            "wrong-base" => actor with { DataId = 123 },
            "not-targetable" => actor with { Targetable = false },
            _ => actor,
        };
        Assert.True(f.Tick());
        Assert.Empty(f.Interactions);
        Assert.Null(f.Native.Target);
    }

    private static CrystalTowerActorObservation Npc(ulong id, uint dataId, Vector3 position)
        => new(id, (uint)id, dataId, ObjectKind.BattleNpc, position, false, true, false, 0, [], 0, null);
    private static CrystalTowerActorObservation[] Garm(bool dead = false)
        => [Npc(10, 0xD9C, new(-76, 100, 383)) with { Dead = dead },
            Npc(11, 0xD9C, new(-77, 100, 384)) with { Dead = dead },
            Npc(12, 0xD9C, new(-78, 100, 383)) with { Dead = dead }];
    private static CrystalTowerActorObservation Queen(bool dead = false)
        => Npc(10, 0xDA0, new(130, 100, 265)) with { Dead = dead };
    private static CrystalTowerActorObservation Clone(bool dead = false)
        => Npc(20, 0xDA3, new(131, 100, 265)) with { Dead = dead };

    public class ActorProxy : DispatchProxy
    {
        internal CrystalTowerActorObservation Actor = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
        {
            "get_GameObjectId" => Actor.GameObjectId,
            "get_EntityId" => Actor.EntityId,
            "get_BaseId" => Actor.DataId,
            "get_ObjectKind" => Actor.ObjectKind,
            "get_Position" => Actor.Position,
            "get_IsDead" => Actor.Dead ?? false,
            "get_IsTargetable" => Actor.Targetable ?? false,
            _ => method.ReturnType == typeof(void) || !method.ReturnType.IsValueType ? null : Activator.CreateInstance(method.ReturnType),
        };
    }

    // Preserve the SDK's generic row constraints; DispatchProxy cannot represent
    // this GetExcelSheet<T> contract. Optional map sheets are absent in this case.
    internal sealed class MaplessData : IDataManager
    {
        public Dalamud.Game.ClientLanguage Language => Dalamud.Game.ClientLanguage.English;
        public Lumina.GameData GameData => null!;
        public ExcelModule Excel => null!;
        public bool HasModifiedGameDataFiles => false;
        public ExcelSheet<T> GetExcelSheet<T>(Dalamud.Game.ClientLanguage? language = null, string? name = null)
            where T : struct, IExcelRow<T> => null!;
        public SubrowExcelSheet<T> GetSubrowExcelSheet<T>(Dalamud.Game.ClientLanguage? language = null, string? name = null)
            where T : struct, IExcelSubrow<T> => null!;
        public Lumina.Data.FileResource? GetFile(string path) => null;
        public T? GetFile<T>(string path) where T : Lumina.Data.FileResource => null;
        public Task<T> GetFileAsync<T>(string path, CancellationToken cancellationToken)
            where T : Lumina.Data.FileResource => Task.FromResult<T>(null!);
        public bool FileExists(string path) => false;
    }

    private sealed class Fixture : IDisposable
    {
        internal Fixture(bool queen = false, string mode = "SYNC")
        {
            Local = Local with { Position = queen ? new(130, 100, 265) : new(-77, 100, 383) };
            Native = new(Local.Position, initialRules: new ObjectPriorityRuleManifest
            {
                Rules = [new() { TerritoryTypeId = 151, ContentFinderConditionId = 111, ObjectKind = "BattleNpc",
                    BaseId = queen ? 0xDA0u : 0xD9Cu, Classification = "Required", CrystalTowerStrategy = mode }],
            });
            typeof(DungeonFrontierService).GetField("dataManager", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(Native.Frontier, new MaplessData());
            Native.Execution.CrystalTowerMovementCharacterIdOverride = () => 1;
            Native.Execution.CrystalTowerMovementLocalEntityIdOverride = () => Local.EntityId;
            Native.Execution.ReadCrystalTowerMovementOverride = () => new(false, null, "fixture: no provider");
            Native.Execution.ReadInteractionVbmOverride = () => new(false, null, "fixture: no provider");
            Native.Execution.QueryInteractionDialog = () => false;
            Native.Execution.DispatchInteractionOverride = actor => { Interactions.Add(actor.GameObjectId); return true; };
            Native.Execution.CrystalTowerObservationOverride = _ => new(true, "fixture", Local.EntityId, Local,
                false, [], Actors, [], 10);
        }

        internal HyperFixatedTargetExecutionTests.Fixture Native { get; }
        internal DutyContextSnapshot Context = TestDutyContextFactory.Create(DutyCategory.AllianceRaid,
            territoryId: 151, cfcId: 111, alliance: "A");
        internal CrystalTowerActorObservation Local = new(1123, 123, 0, ObjectKind.Pc, Vector3.Zero,
            false, true, false, 0, [], null, 25);
        internal CrystalTowerActorObservation[] Actors = [];
        internal Func<CrystalTowerActorObservation, CrystalTowerActorObservation>? NativeOverride;
        internal bool ExcludeInteractableObservation;
        internal List<ulong> Interactions = [];

        internal bool Tick()
        {
            var table = (HyperFixatedTargetExecutionTests.ObjectTableProxy)typeof(ExecutionService)
                .GetField("objectTable", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Native.Execution)!;
            ((HyperFixatedTargetExecutionTests.GameObjectProxy)(object)table.LocalPlayer!).Position = Local.Position;
            table.Objects.Clear();
            var monsters = new List<ObservedMonster>();
            var interactables = new List<ObservedInteractable>();
            foreach (var actor in Actors)
            {
                var gameObject = DispatchProxy.Create<IGameObject, ActorProxy>();
                ((ActorProxy)(object)gameObject).Actor = NativeOverride?.Invoke(actor) ?? actor;
                var monster = new ObservedMonster { Key = $"fixture:{actor.GameObjectId}", GameObjectId = actor.GameObjectId,
                    DataId = actor.DataId, MapId = Context.MapId, Name = "fixture", Position = actor.Position, LastSeenUtc = DateTime.UtcNow };
                table.Objects.Add(new(gameObject, monster));
                if (actor.ObjectKind == ObjectKind.BattleNpc) monsters.Add(monster);
                if (actor.ObjectKind == ObjectKind.EventObj && !ExcludeInteractableObservation)
                    interactables.Add(new() { Key = monster.Key, GameObjectId = actor.GameObjectId, DataId = actor.DataId,
                        MapId = Context.MapId, ObjectKind = actor.ObjectKind, Name = "fixture false chest", Position = actor.Position,
                        LastSeenUtc = DateTime.UtcNow, Classification = InteractableClass.Required, GhostReason = GhostReason.SeenPreviously });
            }
            return (bool)typeof(ExecutionService).GetMethod("TryHandleCrystalTower", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(Native.Execution, [Context, new ObservationSnapshot { LiveMonsters = monsters,
                    LiveInteractables = interactables, LiveFollowTargets = [], MonsterGhosts = [], InteractableGhosts = [] }])!;
        }
        public void Dispose() => Native.Dispose();
    }
}
