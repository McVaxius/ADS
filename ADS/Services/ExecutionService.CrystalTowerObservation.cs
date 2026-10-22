using System.Numerics;
using ADS.Models;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using NativeGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;
using NativeGroupManager = FFXIVClientStructs.FFXIV.Client.Game.Group.GroupManager;
using NativeBattleChara = FFXIVClientStructs.FFXIV.Client.Game.Character.BattleChara;
using NativeCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;
using ServerEventRow = ADS.Services.HigherLowerServerEventTraceService.ServerEventRow;
using ServerEventKind = ADS.Services.HigherLowerServerEventTraceService.ServerEventKind;

namespace ADS.Services;

public sealed partial class ExecutionService
{
    internal sealed record CrystalTowerActorObservation(
        ulong GameObjectId,
        uint EntityId,
        uint DataId,
        ObjectKind ObjectKind,
        Vector3 Position,
        bool? Dead,
        bool? Targetable,
        bool? IsCasting,
        uint? CastActionId,
        IReadOnlyList<uint>? StatusIds,
        byte? EventState,
        uint? ClassJobId,
        float? HitboxRadius = null,
        float? Rotation = null,
        float? CastRotation = null,
        ulong? TargetObjectId = null,
        uint? LayoutId = null,
        CrystalTowerTetherObservation? Tether = null);

    internal readonly record struct CrystalTowerTetherObservation(ushort Id, ulong TargetObjectId);

    internal readonly record struct CrystalTowerDirectorObservation(
        ulong Sequence, DateTime TimestampUtc, uint DirectorId, uint UpdateId,
        uint Param1, uint Param2, uint Param3, uint Param4);

    internal readonly record struct CrystalTowerRosterEntry(
        uint EntityId,
        uint ClassJobId,
        CrystalTowerActorObservation? Actor);

    internal sealed record CrystalTowerRosterObservation(
        bool Complete,
        IReadOnlyList<CrystalTowerMember> Members,
        string Reason);

    internal readonly record struct CrystalTowerEAnimObservation(
        ulong Sequence,
        DateTime TimestampUtc,
        ulong GameObjectId,
        uint EntityId,
        uint DataId,
        uint State);

    internal sealed record CrystalTowerObservation(
        bool Valid,
        string Reason,
        ulong LocalId,
        CrystalTowerActorObservation? LocalActor,
        bool RosterComplete,
        IReadOnlyList<CrystalTowerMember> Members,
        IReadOnlyList<CrystalTowerActorObservation> Actors,
        IReadOnlyList<CrystalTowerEAnimObservation> EAnims,
        ulong SequenceWatermark,
        IReadOnlyList<CrystalTowerDirectorObservation>? Directors = null,
        bool AllianceRosterComplete = false,
        IReadOnlyList<CrystalTowerMember>? AllianceMembers = null);

    /// <summary>
    /// Framework-thread read only. Valid describes the local identity/context; party completeness is separate.
    /// The owner consumes returned EAnims before advancing SequenceWatermark, including rejected rows.
    /// A failed actor/event join remains unknown and is not retried or reconstructed later.
    /// </summary>
    internal CrystalTowerObservation CaptureCrystalTowerObservation(
        IPartyList party,
        DutyContextSnapshot context,
        IReadOnlyList<ServerEventRow>? rows,
        ulong afterSequence,
        DateTime ownerSinceUtc,
        DateTime nowUtc)
    {
        var watermark = CrystalTowerSequenceWatermark(rows, afterSequence);
        CrystalTowerObservation Invalid(string reason) => new(
            false, reason, 0, null, false, [], [], [], watermark);

        if (!IsCrystalTowerObservationContext(context))
            return Invalid("A stable, logged-in Crystal Tower duty context is required.");
        if (ownerSinceUtc == DateTime.MinValue || ownerSinceUtc > nowUtc)
            return Invalid("The execution owner's observation boundary is unknown.");

        try
        {
            if (!TryCaptureCrystalTowerActor(objectTable.LocalPlayer, out var local)
                || local is null || local.ObjectKind != ObjectKind.Pc
                || !IsCrystalTowerEntityId(local.EntityId))
                return Invalid("The local actor is unavailable.");

            var capturedActors = new List<CrystalTowerActorObservation?>();
            var failedActorIds = new HashSet<ulong>();
            try
            {
                foreach (var actor in objectTable)
                {
                    if (actor is null)
                        continue;
                    ulong candidateId = 0;
                    try
                    {
                        candidateId = actor.GameObjectId;
                        if (actor.ObjectKind is not (ObjectKind.BattleNpc or ObjectKind.EventObj or ObjectKind.Pc))
                            continue;
                        if (TryCaptureCrystalTowerActor(actor, out var captured) && captured is not null)
                            capturedActors.Add(captured);
                        else if (IsCrystalTowerObjectId(candidateId))
                            failedActorIds.Add(candidateId);
                    }
                    catch
                    {
                        if (IsCrystalTowerObjectId(candidateId))
                            failedActorIds.Add(candidateId);
                    }
                }
            }
            catch
            {
                // Preserve successful observations if enumeration ends early. Unread actors remain unknown.
            }
            var actors = ProjectUsableCrystalTowerActors(capturedActors, failedActorIds);

            var expectedCount = 0;
            var countConsistent = false;
            var entries = new List<CrystalTowerRosterEntry>();
            try
            {
                expectedCount = party.Length;
                if (expectedCount is > 0 and <= 8)
                {
                    for (var index = 0; index < expectedCount; index++)
                    {
                        uint entityId = 0;
                        uint classJobId = 0;
                        try
                        {
                            var member = party[index];
                            if (member is null)
                            {
                                entries.Add(default);
                                continue;
                            }
                            entityId = member.EntityId;
                            classJobId = member.ClassJob.RowId;
                            TryCaptureCrystalTowerActor(member.GameObject, out var memberActor);
                            entries.Add(new(entityId, classJobId, memberActor));
                        }
                        catch
                        {
                            entries.Add(new(entityId, classJobId, null));
                        }
                    }
                }
                countConsistent = party.Length == expectedCount;
            }
            catch
            {
                // Party reads govern RosterComplete only, not independent actor/cast/status observations.
            }
            var roster = ProjectCrystalTowerRoster(expectedCount, local.EntityId, entries);
            var rosterComplete = countConsistent && roster.Complete;
            var allianceRoster = CaptureCrystalTowerAllianceRoster(party, local.EntityId, actors, entries);
            var sameLocal = objectTable.LocalPlayer is { } currentLocal
                && currentLocal.EntityId == local.EntityId && currentLocal.GameObjectId == local.GameObjectId;
            var animations = sameLocal
                ? ProjectCrystalTowerEAnims(rows, actors, context.TerritoryTypeId, context.MapId,
                    afterSequence, ownerSinceUtc, nowUtc)
                : Array.Empty<CrystalTowerEAnimObservation>();
            var reason = !sameLocal ? "The local actor identity changed."
                : rosterComplete ? roster.Reason : "Party membership changed or a party observation is incomplete.";
            return new(sameLocal, reason,
                local.EntityId, local, sameLocal && rosterComplete, roster.Members, actors, animations, watermark,
                sameLocal ? ProjectCrystalTowerDirectorUpdates(rows, context.TerritoryTypeId, context.MapId,
                    afterSequence, ownerSinceUtc, nowUtc) : [], sameLocal && allianceRoster.Complete, allianceRoster.Members);
        }
        catch
        {
            // A failed native/API read is unknown, never an empty room, absent status or confirmed death.
            return Invalid("The native Crystal Tower observation is incomplete.");
        }
    }

    internal static IReadOnlyList<CrystalTowerActorObservation> ProjectUsableCrystalTowerActors(
        IReadOnlyList<CrystalTowerActorObservation?> captured,
        IReadOnlyCollection<ulong>? failedActorIds = null)
        => captured.Where(actor => actor is not null && IsCrystalTowerObjectId(actor.GameObjectId))
            .Select(actor => actor!)
            .GroupBy(actor => actor.GameObjectId)
            .Where(group => group.Count() == 1 && failedActorIds?.Contains(group.Key) != true)
            .Select(group => group.Single()).ToArray();

    internal static unsafe bool TryCaptureCrystalTowerActor(
        IGameObject? actor,
        out CrystalTowerActorObservation? observation)
    {
        observation = null;
        if (actor is null)
            return false;
        try
        {
            // Dalamud f7d06f36 GameObject/BattleChara/Character project these native/API values directly.
            // ClientStructs d6397a76 GameObject.EventState is byte 0x70; it is NOT a 32-bit EAnim state.
            var address = actor.Address;
            var objectId = actor.GameObjectId;
            var entityId = actor.EntityId;
            var dataId = actor.BaseId;
            if (address == 0 || !actor.IsValid() || !IsCrystalTowerObjectId(objectId))
                return false;
            var position = actor.Position;
            if (!IsFiniteCrystalTowerPosition(position))
                return false;
            var dead = actor.IsDead;
            var targetable = actor.IsTargetable;
            var kind = actor.ObjectKind;
            bool? casting = null;
            uint? castAction = null;
            uint[]? statuses = null;
            float? rotation = null, castRotation = null;
            ulong? targetObjectId = null;
            try { var value = actor.Rotation; if (float.IsFinite(value)) rotation = value; } catch { }
            try { var value = actor.TargetObjectId; if (IsCrystalTowerObjectId(value)) targetObjectId = value; } catch { }
            uint? classJob = actor is ICharacter character ? character.ClassJob.RowId : null;
            if (actor is IBattleChara battle)
            {
                try
                {
                    casting = battle.IsCasting;
                    castAction = casting == true ? battle.CastActionId : 0u;
                }
                catch
                {
                    casting = null;
                    castAction = null;
                }
                try
                {
                    statuses = battle.StatusList.Where(status => status is not null && status.StatusId != 0)
                        .Select(status => (uint)status.StatusId).Distinct().ToArray();
                }
                catch { statuses = null; }
                if (casting == true && kind is ObjectKind.BattleNpc or ObjectKind.Pc)
                {
                    // ClientStructs d6397a76: BattleChara.CastInfo @0x2790, Rotation @0x30.
                    // A cast cone uses the cast's facing, which can differ from current actor facing.
                    var info = &((NativeBattleChara*)address)->CastInfo;
                    if (info->IsCasting && info->ActionId == castAction)
                    {
                        if (float.IsFinite(info->Rotation)) castRotation = info->Rotation;
                    }
                    else { casting = null; castAction = null; }
                }
            }
            var eventState = ((NativeGameObject*)address)->EventState;
            // Reviewed ClientStructs d6397a76: LayoutId @0x7C identifies a placed
            // actor independently of its species BaseId and transient EntityId.
            var layoutId = ((NativeGameObject*)address)->LayoutId;
            // Same current slot as pinned awgil d409d336 WorldStateGameSync.
            // A target selection is independent of the native tether target.
            var hasNativeTether = actor is ICharacter && kind is ObjectKind.Pc or ObjectKind.BattleNpc;
            var tether = hasNativeTether ? ReadCrystalTowerNativeTether((NativeCharacter*)address) : null;
            var hitbox = actor.HitboxRadius;
            if (actor.Address != address || actor.GameObjectId != objectId || actor.EntityId != entityId
                || actor.BaseId != dataId || ((NativeGameObject*)address)->LayoutId != layoutId || !actor.IsValid())
                return false;
            if (hasNativeTether && tether != ReadCrystalTowerNativeTether((NativeCharacter*)address)) tether = null;
            observation = new(objectId, entityId, dataId, kind, position, dead, targetable,
                casting, castAction, statuses, eventState, classJob,
                float.IsFinite(hitbox) && hitbox > 0 ? hitbox : null, rotation, castRotation, targetObjectId, layoutId, tether);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static unsafe CrystalTowerTetherObservation? ReadCrystalTowerNativeTether(NativeCharacter* character)
    {
        // ClientStructs d6397a76: Character.Vfx @0x1988, Tethers @0xA0;
        // Tether.Id @0x00 and TargetId @0x10. Id zero is the positive untethered sample.
        var native = character->Vfx.Tethers[0];
        if (native.Id == 0) return new(0, 0);
        return IsCrystalTowerObjectId(native.TargetId) ? new(native.Id, native.TargetId) : null;
    }

    internal static CrystalTowerRosterObservation ProjectCrystalTowerRoster(
        int expectedCount,
        uint localEntityId,
        IReadOnlyList<CrystalTowerRosterEntry>? entries,
        bool includeAlliance = false)
    {
        var projected = (entries ?? []).Select(entry => new CrystalTowerMember(
            entry.EntityId,
            ResolveCrystalTowerRole(entry.ClassJobId),
            entry.Actor is { Dead: { } dead } ? !dead : null,
            entry.Actor is { } actor && IsFiniteCrystalTowerPosition(actor.Position)
                ? new Vector2(actor.Position.X, actor.Position.Z) : null)).ToArray();
        var complete = expectedCount > 0 && expectedCount <= (includeAlliance ? 24 : 8)
            && entries is not null && entries.Count == expectedCount
            && IsCrystalTowerEntityId(localEntityId)
            && entries.Count(entry => entry.EntityId == localEntityId) == 1
            && entries.All(entry => IsCrystalTowerEntityId(entry.EntityId)
                && entry.Actor is { ObjectKind: ObjectKind.Pc, Dead: not null } actor
                && actor.EntityId == entry.EntityId && IsCrystalTowerObjectId(actor.GameObjectId)
                && actor.ClassJobId == entry.ClassJobId && IsFiniteCrystalTowerPosition(actor.Position)
                && ResolveCrystalTowerRole(entry.ClassJobId) != CrystalTowerRole.Unknown)
            && entries.Select(entry => entry.EntityId).Distinct().Count() == expectedCount
            && entries.Select(entry => entry.Actor!.GameObjectId).Distinct().Count() == expectedCount;
        return new(complete, projected,
            complete ? "The complete visible party roster is known." : "Party membership, roles or visible actor states are incomplete.");
    }

    internal static unsafe CrystalTowerRosterObservation CaptureCrystalTowerAllianceRoster(IPartyList party, uint localEntityId,
        IReadOnlyList<CrystalTowerActorObservation> actors, IReadOnlyList<CrystalTowerRosterEntry> ownParty)
    {
        CrystalTowerRosterObservation Unknown() => new(false, [], "The full native alliance roster is unavailable.");
        try
        {
            var address = party.GroupManagerAddress;
            if (address == 0) return Unknown();
            var group = &((NativeGroupManager*)address)->MainGroup;
            if (!group->IsAlliance || group->IsSmallGroupAlliance || group->MemberCount != ownParty.Count
                || group->MemberCount is 0 or > 8) return Unknown();
            var partyId = group->PartyId;
            // Pinned awgil d409d336 UpdatePartyAlliance and ClientStructs d6397a76:
            // normal alliances use the first 16 fixed AllianceMembers slots; Flags bit 0 marks valid entries.
            var native = new List<(uint Id, uint Job)>();
            for (var i = 0; i < group->MemberCount; i++)
                native.Add((group->PartyMembers[i].EntityId, group->PartyMembers[i].ClassJob));
            if (!native.Select(member => member.Id).Order().SequenceEqual(ownParty.Select(member => member.EntityId).Order()))
                return Unknown();
            for (var i = 0; i < 16; i++)
            {
                ref var member = ref group->AllianceMembers[i];
                if ((member.Flags & 1) != 0) native.Add((member.EntityId, member.ClassJob));
            }
            var entries = native.Select(member => new CrystalTowerRosterEntry(member.Id, member.Job,
                actors.Where(actor => actor.EntityId == member.Id && actor.ObjectKind == ObjectKind.Pc)
                    .Take(2).ToArray() is [var captured] ? captured : null)).ToArray();
            var projected = ProjectCrystalTowerRoster(entries.Length, localEntityId, entries, includeAlliance: true);
            if (!projected.Complete || party.GroupManagerAddress != address || group->PartyId != partyId
                || !group->IsAlliance || group->IsSmallGroupAlliance || group->MemberCount != ownParty.Count)
                return Unknown();
            var check = new List<(uint Id, uint Job)>();
            for (var i = 0; i < group->MemberCount; i++)
                check.Add((group->PartyMembers[i].EntityId, group->PartyMembers[i].ClassJob));
            for (var i = 0; i < 16; i++)
            {
                ref var member = ref group->AllianceMembers[i];
                if ((member.Flags & 1) != 0) check.Add((member.EntityId, member.ClassJob));
            }
            return native.SequenceEqual(check) ? projected : Unknown();
        }
        catch { return Unknown(); }
    }

    internal static IReadOnlyList<CrystalTowerEAnimObservation> ProjectCrystalTowerEAnims(
        IReadOnlyList<ServerEventRow>? rows,
        IReadOnlyList<CrystalTowerActorObservation>? actors,
        uint territoryId,
        uint mapId,
        ulong afterSequence,
        DateTime ownerSinceUtc,
        DateTime nowUtc)
    {
        if (rows is null || actors is null || territoryId == 0 || mapId == 0
            || ownerSinceUtc == DateTime.MinValue || ownerSinceUtc > nowUtc)
            return Array.Empty<CrystalTowerEAnimObservation>();
        var result = new List<CrystalTowerEAnimObservation>();
        foreach (var row in rows.OrderBy(row => row.Sequence))
        {
            if (row.Kind != ServerEventKind.EObjAnim || row.Category != 413 || row.Replaying != 0
                || row.Sequence <= afterSequence || row.TimestampUtc < ownerSinceUtc || row.TimestampUtc > nowUtc
                || row.TerritoryId != territoryId || row.MapId != mapId
                || !IsCrystalTowerEntityId(row.ActorId) || !IsCrystalTowerObjectId(row.GameObjectId)
                || row.BaseId == 0)
                continue;
            var matches = actors.Where(actor => actor.GameObjectId == row.GameObjectId
                && actor.EntityId == row.EntityId && actor.DataId == row.BaseId
                && (row.ActorId == actor.EntityId || row.ActorId == (uint)actor.GameObjectId)).ToArray();
            if (matches.Length != 1)
                continue;
            // awgil d409d336 BossMod/BossModule/BossModule.cs:620-624, high half first.
            var state = ((uint)(ushort)row.P1 << 16) | (ushort)row.P2;
            result.Add(new(row.Sequence, row.TimestampUtc, row.GameObjectId, row.EntityId, row.BaseId, state));
        }
        return result;
    }

    internal static ulong CrystalTowerSequenceWatermark(IReadOnlyList<ServerEventRow>? rows, ulong afterSequence)
        => rows is { Count: > 0 } ? Math.Max(afterSequence, rows.Max(row => row.Sequence)) : afterSequence;

    internal static IReadOnlyList<CrystalTowerDirectorObservation> ProjectCrystalTowerDirectorUpdates(
        IReadOnlyList<ServerEventRow>? rows, uint territoryId, uint mapId, ulong afterSequence,
        DateTime ownerSinceUtc, DateTime nowUtc)
    {
        if (rows is null || territoryId == 0 || mapId == 0 || ownerSinceUtc == default || ownerSinceUtc > nowUtc)
            return [];
        // awgil d409d336 Framework/WorldStateGameSync: ActorControl 109 maps P1 to DirectorID,
        // P2 to UpdateID, then P3-P6 to the four parameters. This is a global director event.
        return rows.Where(row => row.Kind == ServerEventKind.DirectorUpdate && row.Category == 109
                && row.Replaying == 0 && row.Sequence > afterSequence
                && row.TimestampUtc >= ownerSinceUtc && row.TimestampUtc <= nowUtc
                && row.TerritoryId == territoryId && row.MapId == mapId && row.P1 != 0)
            .OrderBy(row => row.Sequence)
            .Select(row => new CrystalTowerDirectorObservation(row.Sequence, row.TimestampUtc,
                row.P1, row.P2, row.P3, row.P4, row.P5, row.P6)).ToArray();
    }

    internal static CrystalTowerRole ResolveCrystalTowerRole(uint classJobId) => classJobId switch
    {
        // Verified native class IDs/roles: awgil d409d336 BossMod/Data/ClassRole.cs.
        1 or 3 or 19 or 21 or 32 or 37 => CrystalTowerRole.Tank,
        6 or 24 or 28 or 33 or 40 => CrystalTowerRole.Healer,
        2 or 4 or 20 or 22 or 29 or 30 or 34 or 39 or 41 or 43 => CrystalTowerRole.Melee,
        5 or 7 or 23 or 25 or 26 or 27 or 31 or 35 or 36 or 38 or 42 => CrystalTowerRole.Ranged,
        _ => CrystalTowerRole.Unknown,
    };

    private static bool IsCrystalTowerObservationContext(DutyContextSnapshot context)
        => context.PluginEnabled && context.IsLoggedIn && context.InInstancedDuty
            && !context.IsUnsafeTransition && !context.IsCutscene && context.MapId != 0
            && (context.TerritoryTypeId, context.ContentFinderConditionId) is (174, 92) or (372, 102) or (151, 111);

    private static bool IsCrystalTowerEntityId(uint id) => id != 0 && id != 0xE0000000;
    private static bool IsCrystalTowerObjectId(ulong id) => id != 0 && id != 0xE0000000;
    private static bool IsFiniteCrystalTowerPosition(Vector3 position)
        => float.IsFinite(position.X) && float.IsFinite(position.Y) && float.IsFinite(position.Z);
}
