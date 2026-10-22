using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using ADS.Models;
using ADS.Services;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using NativeGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;
using NativeGroupManager = FFXIVClientStructs.FFXIV.Client.Game.Group.GroupManager;
using NativeBattleChara = FFXIVClientStructs.FFXIV.Client.Game.Character.BattleChara;
using NativeCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;
using NativeVfx = FFXIVClientStructs.FFXIV.Client.Game.Character.VfxContainer;
using NativeCastInfo = FFXIVClientStructs.FFXIV.Client.Game.Character.CastInfo;
using Actor = ADS.Services.ExecutionService.CrystalTowerActorObservation;
using RosterEntry = ADS.Services.ExecutionService.CrystalTowerRosterEntry;
using ServerEventRow = ADS.Services.HigherLowerServerEventTraceService.ServerEventRow;
using ServerEventKind = ADS.Services.HigherLowerServerEventTraceService.ServerEventKind;

namespace ADS.Tests;

public sealed class CrystalTowerObservationTests
{
    private static readonly DateTime Started = new(2026, 10, 22, 1, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void LoadedNativeSdkKeepsEventStateAsASeparateByteAtReviewedOffset()
    {
        Assert.Equal(0x70, Marshal.OffsetOf<NativeGameObject>(nameof(NativeGameObject.EventState)).ToInt32());
        Assert.Equal(0x78, Marshal.OffsetOf<NativeGameObject>(nameof(NativeGameObject.EntityId)).ToInt32());
        Assert.Equal(0x7C, Marshal.OffsetOf<NativeGameObject>(nameof(NativeGameObject.LayoutId)).ToInt32());
        Assert.Equal(typeof(byte), typeof(NativeGameObject).GetField(nameof(NativeGameObject.EventState))!.FieldType);
    }

    [Theory]
    [InlineData(19u, CrystalTowerRole.Tank)]
    [InlineData(37u, CrystalTowerRole.Tank)]
    [InlineData(24u, CrystalTowerRole.Healer)]
    [InlineData(40u, CrystalTowerRole.Healer)]
    [InlineData(41u, CrystalTowerRole.Melee)]
    [InlineData(43u, CrystalTowerRole.Melee)]
    [InlineData(23u, CrystalTowerRole.Ranged)]
    [InlineData(27u, CrystalTowerRole.Ranged)]
    [InlineData(36u, CrystalTowerRole.Ranged)]
    [InlineData(42u, CrystalTowerRole.Ranged)]
    [InlineData(8u, CrystalTowerRole.Unknown)]
    [InlineData(0u, CrystalTowerRole.Unknown)]
    [InlineData(999u, CrystalTowerRole.Unknown)]
    public void RolesUseReviewedNativeClassIdsIncludingCastersAndUnknownFailsClosed(uint job, CrystalTowerRole role)
        => Assert.Equal(role, ExecutionService.ResolveCrystalTowerRole(job));

    [Fact]
    public void RosterUsesEntityIdentityAndLiveWorldXzIndependentOfClientListOrder()
    {
        RosterEntry[] entries = [Entry(201, 19), Entry(202, 24), Entry(203, 31), Entry(204, 27)];
        var first = ExecutionService.ProjectCrystalTowerRoster(4, 203, entries);
        var second = ExecutionService.ProjectCrystalTowerRoster(4, 203, entries.Reverse().ToArray());
        Assert.True(first.Complete);
        Assert.True(second.Complete);
        Assert.Equal(first.Members.OrderBy(member => member.Id), second.Members.OrderBy(member => member.Id));
        Assert.Equal(new ulong[] { 201, 202, 203, 204 }, first.Members.Select(member => member.Id));
        Assert.All(first.Members, member => Assert.Equal(new Vector2(12, 34), member.Position));
        Assert.All(first.Members, member => Assert.True(member.Alive));
    }

    [Fact]
    public void IncompleteMissingDuplicateWrongIdentityAndUnresolvedRolesCannotProduceCompleteRoster()
    {
        var known = Entry(201, 24);
        var other = Entry(202, 19);
        var cases = new RosterEntry[][]
        {
            [known],
            [known, new(202, 19, null)],
            [known, known],
            [known, other with { Actor = other.Actor! with { EntityId = 299 } }],
            [known, other with { ClassJobId = 8 }],
            [known, other with { Actor = other.Actor! with { ClassJobId = 24 } }],
            [known, other with { Actor = other.Actor! with { Dead = null } }],
            [known, other with { Actor = other.Actor! with { Position = new Vector3(float.NaN, 0, 0) } }],
            [known, other with { EntityId = 0xE0000000 }],
        };
        Assert.All(cases, entries => Assert.False(ExecutionService.ProjectCrystalTowerRoster(2, 201, entries).Complete));
        Assert.False(ExecutionService.ProjectCrystalTowerRoster(2, 299, [known, other]).Complete);
        Assert.False(ExecutionService.ProjectCrystalTowerRoster(0, 201, []).Complete);
        Assert.False(ExecutionService.ProjectCrystalTowerRoster(2, 201, null).Complete);
    }

    [Fact]
    public void KnownDeadMemberRemainsInRosterAsDeadInsteadOfBeingRemovedOrReplaced()
    {
        var entry = Entry(201, 24);
        entry = entry with { Actor = entry.Actor! with { Dead = true } };
        var roster = ExecutionService.ProjectCrystalTowerRoster(1, 201, [entry]);
        Assert.True(roster.Complete);
        var member = Assert.Single(roster.Members);
        Assert.Equal(201UL, member.Id);
        Assert.False(member.Alive);
    }

    [Fact]
    public void EAnimUsesTypedHalvesHighFirstAndNeverNativeEventStateOrDiagnosticText()
    {
        var actor = EventActor();
        var row = Event() with { P1 = 0xFFFF0440, P2 = 0xAAAA0880, EventState = 7, StateData = "not parsed" };
        var result = Assert.Single(Project([row], [actor]));
        Assert.Equal(0x04400880u, result.State);
        Assert.Equal(actor.GameObjectId, result.GameObjectId);
        Assert.Equal(row.Sequence, result.Sequence);
    }

    [Fact]
    public void EAnimHasNoAgeExpiryWithinOwnerLifetimeAndCursorIncludesRejectedRows()
    {
        var row = Event();
        var oldButOwned = ExecutionService.ProjectCrystalTowerEAnims([row], [EventActor()], 174, 100,
            0, Started, Started.AddHours(12));
        Assert.Single(oldButOwned);
        var rejected = row with { Sequence = 900, Replaying = 1 };
        Assert.Empty(Project([rejected], [EventActor()]));
        Assert.Equal(900UL, ExecutionService.CrystalTowerSequenceWatermark([row, rejected], 0));
        Assert.Equal(999UL, ExecutionService.CrystalTowerSequenceWatermark([row], 999));
    }

    [Fact]
    public void ReplayedStaleFutureWrongDutyWrongMapAndPreviouslyConsumedEventsAreRejected()
    {
        var row = Event();
        ServerEventRow[] rejected =
        [
            row with { Replaying = 1 },
            row with { TimestampUtc = Started.AddTicks(-1) },
            row with { TimestampUtc = Started.AddMinutes(2) },
            row with { TerritoryId = 372 },
            row with { MapId = 101 },
            row with { Kind = ServerEventKind.EObjState },
            row with { Category = 409 },
            row with { ActorId = 0 },
            row with { ActorId = 0xE0000000 },
        ];
        Assert.All(rejected, value => Assert.Empty(Project([value], [EventActor()])));
        Assert.Empty(ExecutionService.ProjectCrystalTowerEAnims([row], [EventActor()], 174, 100,
            row.Sequence, Started, Started.AddMinutes(1)));
        Assert.Single(Project([row with { TimestampUtc = Started }], [EventActor()]));
        Assert.Empty(ExecutionService.ProjectCrystalTowerEAnims([row], [EventActor()], 174, 100,
            0, DateTime.MinValue, Started.AddMinutes(1)));
    }

    [Fact]
    public void MissingReusedOrAmbiguousActorIdentityNeverSuppliesAState()
    {
        var actor = EventActor();
        var row = Event();
        Actor[][] wrong =
        [
            [],
            [actor with { GameObjectId = actor.GameObjectId + 1 }],
            [actor with { EntityId = actor.EntityId + 1 }],
            [actor with { DataId = actor.DataId + 1 }],
            [actor, actor],
        ];
        Assert.All(wrong, actors => Assert.Empty(Project([row], actors)));
        Assert.Empty(Project([row with { GameObjectId = 0 }], [actor]));
        Assert.Empty(Project([row with { BaseId = 0 }], [actor]));
        Assert.Empty(Project([row with { ActorId = 999 }], [actor]));
    }

    [Fact]
    public void UnrelatedFailedAndDuplicateActorsDoNotSuppressAValidEventJoin()
    {
        var known = EventActor();
        var ambiguous = known with { GameObjectId = 700, EntityId = 700 };
        var failed = known with { GameObjectId = 800, EntityId = 800 };
        var actors = ExecutionService.ProjectUsableCrystalTowerActors(
            [known, null, ambiguous, ambiguous with { Dead = true }, failed], [failed.GameObjectId]);
        Assert.Equal(known, Assert.Single(actors));
        Assert.Single(Project([Event()], actors));
        Assert.DoesNotContain(actors, actor => actor.GameObjectId == ambiguous.GameObjectId);
        Assert.DoesNotContain(actors, actor => actor.GameObjectId == failed.GameObjectId);
        var failedRequiredEvent = Event() with { Sequence = 11, ActorId = 800, EntityId = 800, GameObjectId = 800 };
        Assert.Empty(Project([failedRequiredEvent], actors));
        Assert.Equal(11UL, ExecutionService.CrystalTowerSequenceWatermark([Event(), failedRequiredEvent], 0));
        Assert.Empty(ExecutionService.ProjectCrystalTowerEAnims([failedRequiredEvent], [failed], 174, 100,
            11, Started, Started.AddMinutes(1)));
    }

    [Fact]
    public void NonnetworkObjectsJoinByFullTypedObjectIdentityDespiteSharedEntitySentinel()
    {
        const uint firstBaseId = 0x1E8F7D;
        const uint secondBaseId = 0x1E8F7E;
        var first = EventActor() with { EntityId = 0xE0000000, GameObjectId = (1UL << 32) | firstBaseId };
        var second = first with { GameObjectId = (1UL << 32) | secondBaseId, DataId = secondBaseId };
        var actors = ExecutionService.ProjectUsableCrystalTowerActors([first, second]);
        Assert.Equal(2, actors.Count);
        var row = Event() with
        {
            ActorId = firstBaseId,
            EntityId = 0xE0000000,
            GameObjectId = first.GameObjectId,
            BaseId = firstBaseId,
        };
        var animation = Assert.Single(Project([row], actors));
        Assert.Equal(first.GameObjectId, animation.GameObjectId);
        Assert.Equal(0x04400880u, animation.State);
        Assert.Empty(Project([row with { GameObjectId = firstBaseId }], actors));
        Assert.Empty(Project([row with { GameObjectId = second.GameObjectId }], actors));
        Assert.Empty(Project([row with { ActorId = 0xE0000000 }], actors));
    }

    [Fact]
    public unsafe void NativeProjectionReadsEventStateByteWithoutFabricatingBattleState()
    {
        NativeGameObject native = default;
        native.EventState = 7;
        native.LayoutId = 4303358;
        var gameObject = DispatchProxy.Create<IGameObject, NativeObjectProxy>();
        var proxy = (NativeObjectProxy)(object)gameObject;
        proxy.Address = (nint)(&native);
        Assert.True(ExecutionService.TryCaptureCrystalTowerActor(gameObject, out var actor));
        Assert.NotNull(actor);
        Assert.Equal((byte)7, actor.EventState);
        Assert.Equal(4303358u, actor.LayoutId);
        Assert.Equal(new Vector3(12, 999, 34), actor.Position);
        Assert.Null(actor.IsCasting);
        Assert.Null(actor.CastActionId);
        Assert.Null(actor.StatusIds);
        Assert.Null(actor.Tether);
        proxy.Address = 0;
        Assert.False(ExecutionService.TryCaptureCrystalTowerActor(gameObject, out _));
        Assert.False(ExecutionService.TryCaptureCrystalTowerActor(null, out _));
    }

    [Fact]
    public unsafe void NativeLayoutChangesDuringCaptureRejectTheWholeActorSample()
    {
        NativeGameObject native = default;
        native.LayoutId = 4303358;
        var address = (nint)(&native);
        var gameObject = DispatchProxy.Create<IGameObject, NativeObjectProxy>();
        var proxy = (NativeObjectProxy)(object)gameObject;
        proxy.Address = address;
        proxy.AfterHitboxRead = () => ((NativeGameObject*)address)->LayoutId++;
        Assert.False(ExecutionService.TryCaptureCrystalTowerActor(gameObject, out var actor));
        Assert.Null(actor);
    }

    private static RosterEntry Entry(uint id, uint classJob)
        => new(id, classJob, new Actor(id, id, 0, ObjectKind.Pc, new Vector3(12, 999, 34),
            false, true, false, 0, [], 0, classJob));

    private static Actor EventActor()
        => new(500, 500, 0x1E8F7D, ObjectKind.EventObj, new Vector3(12, 999, 34),
            false, false, null, null, null, 7, null);

    private static IReadOnlyList<ExecutionService.CrystalTowerEAnimObservation> Project(
        IReadOnlyList<ServerEventRow> rows, IReadOnlyList<Actor> actors)
        => ExecutionService.ProjectCrystalTowerEAnims(rows, actors, 174, 100, 0, Started, Started.AddMinutes(1));

    private static ServerEventRow Event() => new(
        Sequence: 10, TimestampUtc: Started.AddSeconds(1), Kind: ServerEventKind.EObjAnim, BossModKind: "EANM",
        TerritoryId: 174, MapId: 100, ActorId: 500, TargetId: 0, Category: 413,
        P1: 0x0440, P2: 0x0880, P3: 0, P4: 0, P5: 0, P6: 0, P7: 0, P8: 0, Replaying: 0,
        ObjectName: string.Empty, ObjectKind: "EventObj", GameObjectId: 500, EntityId: 500,
        BaseId: 0x1E8F7D, LayoutId: 0, GimmickId: 0, EventState: 7, EventId: 0, Targetable: false,
        Position: new Vector3(12, 999, 34), Distance: 0, StateData: string.Empty, SourceParams: string.Empty,
        DataHex: string.Empty, HigherLowerRelevant: false);

    [Fact]
    public void DirectorUpdatesUseNativeParameterOwnershipAndRejectReplayStaleAndWrongScope()
    {
        var row = Event() with { Kind = ServerEventKind.DirectorUpdate, Category = 109, P1 = 0x80030000,
            P2 = 0x80000001, P3 = 0xB4, P4 = 2, P5 = 3, P6 = 4 };
        var update = Assert.Single(ExecutionService.ProjectCrystalTowerDirectorUpdates([row], 174, 100, 0, Started, Started.AddMinutes(1)));
        Assert.Equal(0x80030000u, update.DirectorId);
        Assert.Equal(0x80000001u, update.UpdateId);
        Assert.Equal(0xB4u, update.Param1);
        Assert.Equal((2u, 3u, 4u), (update.Param2, update.Param3, update.Param4));
        var rejected = new[] { row with { Replaying = 1 }, row with { Sequence = 0 },
            row with { TimestampUtc = Started.AddTicks(-1) }, row with { TimestampUtc = Started.AddMinutes(2) },
            row with { TerritoryId = 151 }, row with { MapId = 99 }, row with { Category = 413 },
            row with { Kind = ServerEventKind.EObjAnim }, row with { P1 = 0 } };
        foreach (var invalid in rejected)
            Assert.Empty(ExecutionService.ProjectCrystalTowerDirectorUpdates([invalid], 174, 100, 0, Started, Started.AddMinutes(1)));
        Assert.Empty(ExecutionService.ProjectCrystalTowerDirectorUpdates([row], 174, 100, 10, Started, Started.AddMinutes(1)));
    }

    [Fact]
    public unsafe void FullAllianceProjectionUsesValidNativeSlotsAndLiveActorsWithoutInventingATwentyFourPlayerMinimum()
    {
        NativeGroupManager manager = default;
        var group = &manager.MainGroup;
        group->AllianceFlags = 1;
        group->PartyId = 9;
        group->MemberCount = 2;
        group->PartyMembers[0].EntityId = 201;
        group->PartyMembers[0].ClassJob = 24;
        group->PartyMembers[1].EntityId = 202;
        group->PartyMembers[1].ClassJob = 19;
        group->AllianceMembers[0].Flags = 1;
        group->AllianceMembers[0].EntityId = 203;
        group->AllianceMembers[0].ClassJob = 31;
        group->AllianceMembers[1].Flags = 1;
        group->AllianceMembers[1].EntityId = 204;
        group->AllianceMembers[1].ClassJob = 27;
        var party = DispatchProxy.Create<IPartyList, AllianceRuleScopeTests.NativePartyProxy>();
        ((AllianceRuleScopeTests.NativePartyProxy)(object)party).Address = (nint)(&manager);
        RosterEntry[] own = [Entry(201, 24), Entry(202, 19)];
        Actor[] actors = [own[0].Actor!, own[1].Actor!, Entry(203, 31).Actor!, Entry(204, 27).Actor!];
        var actual = ExecutionService.CaptureCrystalTowerAllianceRoster(party, 201, actors, own);
        Assert.True(actual.Complete);
        Assert.Equal(new ulong[] { 201, 202, 203, 204 }, actual.Members.Select(member => member.Id));
        Assert.False(ExecutionService.CaptureCrystalTowerAllianceRoster(party, 201, actors[..3], own).Complete);
        Assert.False(ExecutionService.CaptureCrystalTowerAllianceRoster(party, 201, [.. actors, actors[2]], own).Complete);
        group->AllianceMembers[1].Flags = 0; // A native vacant slot is different from an unresolved valid member.
        Assert.True(ExecutionService.CaptureCrystalTowerAllianceRoster(party, 201, actors[..3], own).Complete);
        group->AllianceMembers[0].EntityId = 201;
        Assert.False(ExecutionService.CaptureCrystalTowerAllianceRoster(party, 201, actors, own).Complete);
        group->AllianceMembers[0].EntityId = 203;
        group->AllianceFlags = 3;
        Assert.False(ExecutionService.CaptureCrystalTowerAllianceRoster(party, 201, actors, own).Complete);
        group->AllianceFlags = 1;
        group->MemberCount = 1;
        Assert.False(ExecutionService.CaptureCrystalTowerAllianceRoster(party, 201, actors, own).Complete);
        ((AllianceRuleScopeTests.NativePartyProxy)(object)party).Address = 0;
        Assert.False(ExecutionService.CaptureCrystalTowerAllianceRoster(party, 201, actors, own).Complete);
    }

    [Fact]
    public unsafe void ProductionCaptureRetainsVisiblePlayersSoTheNativeAllianceJoinCanActuallyComplete()
    {
        NativeGroupManager manager = default;
        manager.MainGroup.AllianceFlags = 1;
        manager.MainGroup.MemberCount = 2;
        manager.MainGroup.PartyId = 9;
        NativeBattleChara* native = stackalloc NativeBattleChara[4];
        new Span<byte>(native, sizeof(NativeBattleChara) * 4).Clear();
        uint[] jobs = [24, 19, 31, 27];
        var players = new List<IGameObject>();
        for (var i = 0; i < 4; i++)
        {
            var player = DispatchProxy.Create<IPlayerCharacter, CapturePlayerProxy>();
            var proxy = (CapturePlayerProxy)(object)player;
            proxy.Address = (nint)(native + i);
            proxy.EntityId = ((NativeGameObject*)(native + i))->EntityId = 201u + (uint)i;
            proxy.Job = jobs[i];
            players.Add(player);
            if (i < 2)
            {
                manager.MainGroup.PartyMembers[i].EntityId = proxy.EntityId;
                manager.MainGroup.PartyMembers[i].ClassJob = (byte)jobs[i];
            }
            else
            {
                manager.MainGroup.AllianceMembers[i - 2].Flags = 1;
                manager.MainGroup.AllianceMembers[i - 2].EntityId = proxy.EntityId;
                manager.MainGroup.AllianceMembers[i - 2].ClassJob = (byte)jobs[i];
            }
        }
        var table = DispatchProxy.Create<IObjectTable, CaptureTableProxy>();
        ((CaptureTableProxy)(object)table).Players = players;
        var party = DispatchProxy.Create<IPartyList, CapturePartyProxy>();
        ((CapturePartyProxy)(object)party).Address = (nint)(&manager);
        ((CapturePartyProxy)(object)party).Players = players.Take(2).ToList();
        var execution = new ExecutionService(null!, table, null!, null!, null!, null!, null!, null!, null!, null!, null!, new());
        var context = TestDutyContextFactory.Create(DutyCategory.AllianceRaid, territoryId: 174, cfcId: 92, alliance: "A");
        var snapshot = execution.CaptureCrystalTowerObservation(party, context, [], 0, Started, Started.AddSeconds(1));
        Assert.True(snapshot.Valid, snapshot.Reason);
        Assert.True(snapshot.RosterComplete, snapshot.Reason);
        Assert.True(snapshot.AllianceRosterComplete);
        Assert.Equal(4, snapshot.Actors.Count(actor => actor.ObjectKind == ObjectKind.Pc));
        Assert.Equal(new ulong[] { 201, 202, 203, 204 }, snapshot.AllianceMembers!.Select(member => member.Id));
        ((CaptureTableProxy)(object)table).Players = players.Take(3).ToList();
        snapshot = execution.CaptureCrystalTowerObservation(party, context, [], 0, Started, Started.AddSeconds(1));
        Assert.True(snapshot.Valid);
        Assert.False(snapshot.AllianceRosterComplete); // An unresolved valid remote slot must stay unknown.
    }

    [Fact]
    public unsafe void CastFacingComesFromTheReviewedNativeCastAndNeverFromCurrentActorFacing()
    {
        Assert.Equal(0x2790, Marshal.OffsetOf<NativeBattleChara>(nameof(NativeBattleChara.CastInfo)).ToInt32());
        Assert.Equal(0x30, Marshal.OffsetOf<NativeCastInfo>(nameof(NativeCastInfo.Rotation)).ToInt32());
        NativeBattleChara native = default;
        native.CastInfo.Flags = 1;
        native.CastInfo.ActionId = 3415;
        native.CastInfo.Rotation = 0.75f;
        var player = DispatchProxy.Create<IPlayerCharacter, CapturePlayerProxy>();
        var proxy = (CapturePlayerProxy)(object)player;
        proxy.Address = (nint)(&native);
        proxy.EntityId = 201;
        proxy.Job = 24;
        proxy.Casting = true;
        proxy.CastActionId = 3415;
        proxy.Rotation = -1.25f;
        proxy.TargetId = 202;
        Assert.True(ExecutionService.TryCaptureCrystalTowerActor(player, out var actor));
        Assert.Equal(0.75f, actor!.CastRotation);
        Assert.Equal(-1.25f, actor.Rotation);
        Assert.Equal(202ul, actor.TargetObjectId);
        Assert.True(actor.IsCasting);
        Assert.Null(actor.StatusIds); // An independent unavailable list cannot erase the cast observation.
        proxy.CastActionId = 3416;
        Assert.True(ExecutionService.TryCaptureCrystalTowerActor(player, out actor));
        Assert.Null(actor!.CastRotation);
        Assert.Null(actor.IsCasting);
        native.CastInfo.Rotation = float.NaN;
        proxy.CastActionId = 3415;
        Assert.True(ExecutionService.TryCaptureCrystalTowerActor(player, out actor));
        Assert.Null(actor!.CastRotation);
        Assert.True(actor.IsCasting);
        Assert.Equal(3415u, actor.CastActionId);
        native.CastInfo.ActionId = proxy.CastActionId = 1730;
        Assert.True(ExecutionService.TryCaptureCrystalTowerActor(player, out actor));
        Assert.Null(actor!.CastRotation);
        Assert.True(actor.IsCasting);
        Assert.Equal(1730u, actor.CastActionId); // Flare admission does not need optional cone-facing data.
        proxy.Casting = false;
        proxy.TargetId = 0xE0000000;
        Assert.True(ExecutionService.TryCaptureCrystalTowerActor(player, out actor));
        Assert.Null(actor!.CastRotation);
        Assert.Null(actor.TargetObjectId);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(8)]
    [InlineData(22)]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(34)]
    public unsafe void NativeTetherUsesTheReviewedCurrentSlotAndIsIndependentOfOrdinaryTarget(int id)
    {
        Assert.Equal(0x1988, Marshal.OffsetOf<NativeCharacter>(nameof(NativeCharacter.Vfx)).ToInt32());
        Assert.Equal(0xA0, Marshal.OffsetOf<NativeVfx>("_tethers").ToInt32());
        Assert.Equal(0x10, Marshal.OffsetOf<NativeVfx.Tether>(nameof(NativeVfx.Tether.TargetId)).ToInt32());
        NativeBattleChara native = default;
        var character = (NativeCharacter*)(&native);
        character->Vfx.Tethers[0].Id = (ushort)id;
        character->Vfx.Tethers[0].TargetId = 0x1000000CAul;
        var player = DispatchProxy.Create<IPlayerCharacter, CapturePlayerProxy>();
        var proxy = (CapturePlayerProxy)(object)player;
        proxy.Address = (nint)(&native);
        proxy.EntityId = 201;
        proxy.Job = 24;
        proxy.TargetId = 999;
        Assert.True(ExecutionService.TryCaptureCrystalTowerActor(player, out var actor));
        Assert.Equal(new ExecutionService.CrystalTowerTetherObservation((ushort)id, 0x1000000CAul), actor!.Tether);
        Assert.Equal(999ul, actor.TargetObjectId);
    }

    [Theory]
    [InlineData(0ul)]
    [InlineData(0xE0000000ul)]
    public unsafe void UnresolvedTetherTargetRemainsUnknownAndDoesNotFallBackToOrdinaryTarget(ulong target)
    {
        NativeBattleChara native = default;
        var character = (NativeCharacter*)(&native);
        character->Vfx.Tethers[0].Id = 5;
        character->Vfx.Tethers[0].TargetId = target;
        var player = DispatchProxy.Create<IPlayerCharacter, CapturePlayerProxy>();
        var proxy = (CapturePlayerProxy)(object)player;
        proxy.Address = (nint)(&native);
        proxy.EntityId = 201;
        proxy.Job = 24;
        proxy.TargetId = 202;
        Assert.True(ExecutionService.TryCaptureCrystalTowerActor(player, out var actor));
        Assert.Null(actor!.Tether);
        Assert.Equal(202ul, actor.TargetObjectId);
    }

    [Fact]
    public unsafe void ClearedCurrentTetherIsPositiveStateAndNeverAdoptsTheSecondarySlot()
    {
        NativeBattleChara native = default;
        var character = (NativeCharacter*)(&native);
        character->Vfx.Tethers[0].Id = 0;
        character->Vfx.Tethers[0].TargetId = 999ul;
        character->Vfx.Tethers[1].Id = 5;
        character->Vfx.Tethers[1].TargetId = 202ul;
        var player = DispatchProxy.Create<IPlayerCharacter, CapturePlayerProxy>();
        var proxy = (CapturePlayerProxy)(object)player;
        proxy.Address = (nint)(&native);
        proxy.EntityId = 201;
        proxy.Job = 24;
        Assert.True(ExecutionService.TryCaptureCrystalTowerActor(player, out var actor));
        Assert.Equal(new ExecutionService.CrystalTowerTetherObservation(0, 0), actor!.Tether);
    }

    [Fact]
    public unsafe void ChangedNativeTetherDuringCaptureStaysUnknownWithoutErasingOtherActorState()
    {
        NativeBattleChara native = default;
        var address = (nint)(&native);
        var character = (NativeCharacter*)address;
        character->Vfx.Tethers[0].Id = 5;
        character->Vfx.Tethers[0].TargetId = 202ul;
        var player = DispatchProxy.Create<IPlayerCharacter, CapturePlayerProxy>();
        var proxy = (CapturePlayerProxy)(object)player;
        proxy.Address = address;
        proxy.EntityId = 201;
        proxy.Job = 24;
        proxy.TargetId = 999;
        proxy.AfterHitboxRead = () => ((NativeCharacter*)address)->Vfx.Tethers[0].TargetId = 203ul;
        Assert.True(ExecutionService.TryCaptureCrystalTowerActor(player, out var actor));
        Assert.Null(actor!.Tether);
        Assert.Equal(999ul, actor.TargetObjectId);
        Assert.False(actor.Dead);
    }

    private static object JobReference(Type type, uint rowId)
    {
        var boxed = Activator.CreateInstance(type)!;
        var field = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(candidate => candidate.FieldType == typeof(uint));
        field.SetValue(boxed, rowId);
        return boxed;
    }

    public class CapturePlayerProxy : DispatchProxy
    {
        internal nint Address;
        internal uint EntityId, Job;
        internal bool Casting;
        internal uint CastActionId;
        internal float Rotation;
        internal ulong TargetId;
        internal Action? AfterHitboxRead;
        private float Hitbox() { AfterHitboxRead?.Invoke(); return 0.5f; }
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
        {
            "get_Address" => Address,
            "get_GameObjectId" => (ulong)EntityId,
            "get_EntityId" => EntityId,
            "get_ObjectKind" => ObjectKind.Pc,
            "get_ClassJob" => JobReference(method.ReturnType, Job),
            "get_Position" => new Vector3(12, 100, 34),
            "get_HitboxRadius" => Hitbox(),
            "get_IsCasting" => Casting,
            "get_CastActionId" => CastActionId,
            "get_Rotation" => Rotation,
            "get_TargetObjectId" => TargetId,
            "get_IsDead" => false,
            "IsValid" => true,
            _ => method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null,
        };
    }

    public class CaptureTableProxy : DispatchProxy
    {
        internal List<IGameObject> Players = [];
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
        {
            "get_LocalPlayer" => Players[0],
            "GetEnumerator" => ((IEnumerable<IGameObject>)Players).GetEnumerator(),
            _ => method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null,
        };
    }

    public class CapturePartyProxy : DispatchProxy
    {
        internal nint Address;
        internal List<IGameObject> Players = [];
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == "get_Length") return Players.Count;
            if (method.Name == "get_GroupManagerAddress") return Address;
            if (method.Name == "get_Item")
            {
                var member = DispatchProxy.Create(method.ReturnType, typeof(CapturePartyMemberProxy));
                ((CapturePartyMemberProxy)member).Player = Players[(int)args![0]!];
                return member;
            }
            return method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null;
        }
    }

    public class CapturePartyMemberProxy : DispatchProxy
    {
        internal IGameObject Player = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
        {
            "get_EntityId" => Player.EntityId,
            "get_ClassJob" => JobReference(method.ReturnType, ((CapturePlayerProxy)(object)Player).Job),
            "get_GameObject" => Player,
            _ => method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null,
        };
    }

    public class NativeObjectProxy : DispatchProxy
    {
        public nint Address { get; set; }
        internal Action? AfterHitboxRead;

        private float Hitbox() { AfterHitboxRead?.Invoke(); return 0; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
        {
            "get_Address" => Address,
            "get_GameObjectId" => 500UL,
            "get_EntityId" => 500u,
            "get_BaseId" => 0x1E8F7Du,
            "get_ObjectKind" => ObjectKind.EventObj,
            "get_Position" => new Vector3(12, 999, 34),
            "get_HitboxRadius" => Hitbox(),
            "get_IsDead" => false,
            "get_IsTargetable" => false,
            "IsValid" => true,
            _ => targetMethod?.ReturnType.IsValueType == true ? Activator.CreateInstance(targetMethod.ReturnType) : null,
        };
    }
}
