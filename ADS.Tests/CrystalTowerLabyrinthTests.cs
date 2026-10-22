using System.Numerics;
using System.Reflection;
using ADS.Models;
using ADS.Services;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using static ADS.Services.ExecutionService;

namespace ADS.Tests;

public sealed class CrystalTowerLabyrinthTests
{
    [Theory]
    [InlineData("SYNC")]
    [InlineData("UNSYNC")]
    public void ActualPadDriverKeepsFourPreferredHoldersUntilTheSupportedLaneClears(string strategy)
    {
        using var f = new Fixture(strategy);
        Assert.True(f.Tick());
        Assert.Equal(new ulong[] { 201, 202, 203, 204 }, f.Holders);
        Assert.Same(f.NativeBoss, f.Root.Native.Target);
        Assert.DoesNotContain(f.Root.Native.Commands, command => command.StartsWith("/vnav moveto"));

        f.Bosses[0] = f.Bosses[0] with { Dead = true };
        ((CrystalTowerExecutionTests.ActorProxy)(object)f.NativeBoss).Actor = f.Bosses[0];
        f.Root.Local = f.Root.Local with { Position = f.Root.Local.Position + new Vector3(10, 0, 0) };
        f.Members[0] = f.Members[0] with { Position = new(207, 244) };
        Assert.True(f.Tick());
        Assert.Contains("/vnav moveto 197.00 50.00 244.00", f.Root.Native.Commands);
        Assert.Null(f.Root.Native.Target); // Own boss death cannot release the pad that supports B.
        f.Root.Native.Commands.Clear();
        f.Bosses[1] = f.Bosses[1] with { Dead = true };
        Assert.True(f.Tick());
        Assert.DoesNotContain(f.Root.Native.Commands, command => command.StartsWith("/vnav moveto"));
        Assert.Contains("/vnav stop", f.Root.Native.Commands);
        Assert.Equal(new ulong[] { 201, 202, 203, 204 }, f.Holders);

        f.Bosses[2] = f.Bosses[2] with { Dead = true };
        Assert.False(f.Tick()); // Every lane has actual observed death evidence.
    }

    [Theory]
    [InlineData("SYNC")]
    [InlineData("UNSYNC")]
    public void HolderDeathOrIncompleteRosterHoldsWithoutElectingAReplacement(string strategy)
    {
        using var f = new Fixture(strategy);
        Assert.True(f.Tick());
        f.Members[1] = f.Members[1] with { Alive = false };
        f.Members[4] = new(209, CrystalTowerRole.Healer, true, new(197, 244));
        Assert.True(f.Tick());
        Assert.Equal(new ulong[] { 201, 202, 203, 204 }, f.Holders);
        Assert.Null(f.Root.Native.Target);
        f.RosterComplete = false;
        Assert.True(f.Tick());
        Assert.Equal(new ulong[] { 201, 202, 203, 204 }, f.Holders);
        f.RosterComplete = true;
        f.Members[1] = f.Members[1] with { Alive = true };
        Assert.True(f.Tick());
        Assert.Same(f.NativeBoss, f.Root.Native.Target);
        Assert.Equal(new ulong[] { 201, 202, 203, 204 }, f.Holders);
    }

    [Theory]
    [InlineData("revival")]
    [InlineData("revival-through-unknown")]
    [InlineData("entity")]
    [InlineData("object")]
    public void BoneAttemptChangesInvalidateTheOldDirectorClearAndRequireALaterB4(string change)
    {
        using var root = new CrystalTowerExecutionTests.Fixture(new(-451.2f, 25.53f, 23.93f));
        var boss = new CrystalTowerActorObservation(1, 1, 0x92B, ObjectKind.BattleNpc, root.Local.Position,
            true, false, false, 0, [], null, null);
        CrystalTowerDirectorObservation[] updates = [];
        ulong watermark = 10;
        root.Native.Execution.CrystalTowerObservationOverride = _ => new(true, "Bone attempt fixture", 123, root.Local,
            false, [], [root.Local, boss], [], watermark, updates);
        Assert.True(root.Tick());
        updates = [new(11, DateTime.UtcNow, 1, 0x80000001, 0xB4, 0, 0, 0)];
        watermark = 11;
        root.Tick();
        Assert.True(ReadPrivate<bool>(root.Native.Execution, "crystalTowerBoneComplete"));

        if (change == "revival-through-unknown")
        {
            boss = boss with { Dead = null };
            root.Tick();
        }
        boss = change switch
        {
            "entity" => boss with { EntityId = 2 },
            "object" => boss with { GameObjectId = 2, EntityId = 2 },
            _ => boss with { Dead = false },
        };
        watermark = 12;
        root.Native.Commands.Clear();
        Assert.True(root.Tick());
        Assert.False(ReadPrivate<bool>(root.Native.Execution, "crystalTowerBoneComplete"));
        Assert.Null(root.Native.Target);
        Assert.DoesNotContain(root.Native.Commands, command => command.StartsWith("/vnav moveto"));
        watermark = 13;
        Assert.True(root.Tick()); // Replaying the old attempt's B4 is still insufficient.
        Assert.False(ReadPrivate<bool>(root.Native.Execution, "crystalTowerBoneComplete"));

        boss = boss with { Dead = true };
        updates = [new(14, DateTime.UtcNow, 1, 0x80000001, 0xB4, 0, 0, 0)];
        watermark = 14;
        root.Tick();
        Assert.True(ReadPrivate<bool>(root.Native.Execution, "crystalTowerBoneComplete"));
        root.Native.Execution.CancelCrystalTower(root.Context);
        Assert.False(ReadPrivate<bool>(root.Native.Execution, "crystalTowerBoneComplete"));
        Assert.Null(ReadPrivate<CrystalTowerActorObservation?>(root.Native.Execution, "crystalTowerBoneActor"));
        Assert.Equal(0ul, ReadPrivate<ulong>(root.Native.Execution, "crystalTowerBoneAttemptBoundary"));
    }

    [Theory]
    [InlineData("object", false)]
    [InlineData("object", true)]
    [InlineData("entity", false)]
    [InlineData("entity", true)]
    [InlineData("revival", false)]
    [InlineData("revival", true)]
    [InlineData("revival-through-unknown", false)]
    [InlineData("revival-through-unknown", true)]
    public void BombAttemptChangesCannotInheritDeadVassagoEvenWhenTheirCorpsesRemainLoaded(string change, bool keepCorpses)
    {
        using var root = new CrystalTowerExecutionTests.Fixture(new(-110, 44.17f, -165.6f));
        var bomb = new CrystalTowerActorObservation(1, 1, 0x967, ObjectKind.BattleNpc,
            root.Local.Position + Vector3.UnitX, false, true, false, 0, [], null, null);
        var deadAdds = new Vector3[] { new(-130, 44.17f, -165.6f), new(-110, 44.17f, -190), new(-90, 44.17f, -165.6f) }
            .Select((position, index) => new CrystalTowerActorObservation((ulong)index + 2, (uint)index + 2,
                0x96A, ObjectKind.BattleNpc, position, true, false, false, 0, [], null, null)).ToArray();
        CrystalTowerActorObservation[] actors = [bomb, .. deadAdds];
        root.Native.Execution.CrystalTowerObservationOverride = _ => new(true, "Bomb attempt fixture", 123, root.Local,
            false, [], [root.Local, .. actors], [], 10);
        Assert.True(root.Tick(RebuildNativeMonsters(root, actors)));
        Assert.Equal(bomb.GameObjectId, root.Native.Target?.GameObjectId);

        if (change.StartsWith("revival"))
        {
            bomb = bomb with { Dead = true };
            actors = [bomb];
            root.Tick(RebuildNativeMonsters(root, actors));
            if (change == "revival-through-unknown")
            {
                bomb = bomb with { Dead = null };
                actors = [bomb];
                root.Tick(RebuildNativeMonsters(root, actors));
            }
        }
        bomb = change switch
        {
            "object" => bomb with { GameObjectId = 99, EntityId = 99 },
            "entity" => bomb with { EntityId = 99 },
            _ => bomb with { Dead = false },
        };
        actors = keepCorpses ? [bomb, .. deadAdds] : [bomb];
        Assert.True(root.Tick(RebuildNativeMonsters(root, actors)));
        Assert.Null(root.Native.Target);
        Assert.Empty(ReadPrivate<Dictionary<ulong, CrystalTowerActorObservation>>(root.Native.Execution, "crystalTowerVassago"));
        Assert.Empty(ReadPrivate<Dictionary<CrystalTowerAlliance, ulong>>(root.Native.Execution, "crystalTowerVassagoAssignments"));
        Assert.True(root.Tick(RebuildNativeMonsters(root, actors))); // Repeated old corpse reads do not re-admit them.
        Assert.Null(root.Native.Target);

        if (change.StartsWith("revival"))
        {
            actors = [bomb, .. deadAdds.Select(actor => actor with { Dead = false })];
            Assert.True(root.Tick(RebuildNativeMonsters(root, actors)));
            Assert.Null(root.Native.Target); // Positive living reads admit the revived wave, not its deaths.
            actors = [bomb, .. deadAdds];
        }
        else
        {
            actors = [bomb, .. deadAdds.Select(actor => actor with { GameObjectId = actor.GameObjectId + 100,
                EntityId = actor.EntityId + 100 })];
        }
        Assert.True(root.Tick(RebuildNativeMonsters(root, actors)));
        Assert.Equal(bomb.GameObjectId, root.Native.Target?.GameObjectId);
        Assert.Equal(bomb.EntityId, root.Native.Target?.EntityId);
        root.Native.Execution.CancelCrystalTower(root.Context);
        Assert.Null(ReadPrivate<CrystalTowerActorObservation?>(root.Native.Execution, "crystalTowerBombActor"));
        Assert.Empty(ReadPrivate<HashSet<(ulong, uint)>>(root.Native.Execution, "crystalTowerPreviousVassago"));
    }

    private static T ReadPrivate<T>(ExecutionService execution, string name)
        => (T)typeof(ExecutionService).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(execution)!;

    private static ObservationSnapshot RebuildNativeMonsters(CrystalTowerExecutionTests.Fixture root,
        CrystalTowerActorObservation[] actors)
    {
        var table = (HyperFixatedTargetExecutionTests.ObjectTableProxy)typeof(ExecutionService)
            .GetField("objectTable", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(root.Native.Execution)!;
        table.Objects.Clear();
        var monsters = new List<ObservedMonster>();
        foreach (var actor in actors)
        {
            var gameObject = DispatchProxy.Create<IGameObject, CrystalTowerExecutionTests.ActorProxy>();
            ((CrystalTowerExecutionTests.ActorProxy)(object)gameObject).Actor = actor;
            var observed = new ObservedMonster { Key = $"fixture:{actor.GameObjectId}", GameObjectId = actor.GameObjectId,
                DataId = actor.DataId, MapId = root.Context.MapId, Name = "fixture", Position = actor.Position, LastSeenUtc = DateTime.UtcNow };
            table.Objects.Add(new(gameObject, observed));
            monsters.Add(observed);
        }
        return new() { LiveMonsters = monsters, LiveFollowTargets = [], LiveInteractables = [], MonsterGhosts = [], InteractableGhosts = [] };
    }

    private sealed class Fixture : IDisposable
    {
        internal Fixture(string strategy)
        {
            Root = new(new(197, 50, 244), strategy);
            Root.Local = Root.Local with { EntityId = 201, ClassJobId = 24 };
            Root.Native.Execution.CrystalTowerObservationOverride = _ => new(true, "fabricated Labyrinth fixture", 201, Root.Local,
                RosterComplete, Members, [Root.Local, .. Bosses, Pad, Ring],
                [new(10, DateTime.UtcNow, Ring.GameObjectId, Ring.EntityId, Ring.DataId, 0x04400880)], 10);
            NativeBoss = DispatchProxy.Create<IGameObject, CrystalTowerExecutionTests.ActorProxy>();
            ((CrystalTowerExecutionTests.ActorProxy)(object)NativeBoss).Actor = Bosses[0];
            var monster = new ObservedMonster { Key = "fabricated Atomos A", GameObjectId = Bosses[0].GameObjectId,
                DataId = 0x963, MapId = 1, Name = "fabricated Atomos A", Position = Bosses[0].Position, LastSeenUtc = DateTime.UtcNow };
            var table = typeof(ExecutionService).GetField("objectTable", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(Root.Native.Execution)!;
            ((HyperFixatedTargetExecutionTests.ObjectTableProxy)table).Objects.Add(new(NativeBoss, monster));
            Observation = new() { LiveMonsters = [monster], LiveFollowTargets = [], MonsterGhosts = [],
                LiveInteractables = [], InteractableGhosts = [] };
        }
        internal CrystalTowerExecutionTests.Fixture Root;
        internal IGameObject NativeBoss;
        internal ObservationSnapshot Observation;
        internal bool RosterComplete = true;
        internal CrystalTowerMember[] Members = Enumerable.Range(0, 8).Select(index => new CrystalTowerMember(
            201ul + (ulong)index, index < 2 ? CrystalTowerRole.Healer : index < 4 ? CrystalTowerRole.Ranged
                : index == 4 ? CrystalTowerRole.Tank : CrystalTowerRole.Melee,
            true, index < 4 ? new(197, 244) : new(250, 244))).ToArray();
        internal CrystalTowerActorObservation[] Bosses = new[] { 244f, 279f, 315f }.Select((z, index) =>
            new CrystalTowerActorObservation((ulong)index + 1, (uint)index + 1, 0x963, ObjectKind.BattleNpc,
                new(253, 50, z), false, true, false, 0, [], null, null, 4)).ToArray();
        internal CrystalTowerActorObservation Pad = new(0x1001E8F80UL, 0xE0000000, 0x1E8F80, ObjectKind.EventObj,
            new(197, 50, 244), null, false, null, null, null, 0, null);
        internal CrystalTowerActorObservation Ring = new(0x1001E8F7DUL, 0xE0000000, 0x1E8F7D, ObjectKind.EventObj,
            new(253, 50, 244), null, false, null, null, null, 0, null);
        internal IReadOnlyList<ulong> Holders => (IReadOnlyList<ulong>)typeof(ExecutionService)
            .GetField("crystalTowerHolders", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Root.Native.Execution)!;
        internal bool Tick() => Root.Tick(Observation);
        public void Dispose() => Root.Dispose();
    }
}
