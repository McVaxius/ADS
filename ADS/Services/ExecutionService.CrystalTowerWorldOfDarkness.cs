using System.Numerics;
using ADS.Models;
using Dalamud.Game.ClientState.Objects.Enums;
using ServerEventKind = ADS.Services.HigherLowerServerEventTraceService.ServerEventKind;

namespace ADS.Services;

public sealed partial class ExecutionService
{
    // Native mechanics/geometry: awgil/ffxiv_bossmod d409d336edb909b71b25fa8761f88667f5bdce1b,
    // BossMod.Modules/RealmReborn/Alliance/{A32AngraMainyu,A34FiveHeadedDragon,A35Atomos,A36Cerberus,A37CloudOfDarkness}.
    // All fixed Vector2 values are authored world X/Z. Retain observed floor height through the CT move owner.
    private readonly Dictionary<CrystalTowerAlliance, CrystalTowerActorObservation> worldOfDarknessAtomos = [];
    private ulong worldOfDarknessStateCursor;
    private CrystalTowerActorObservation? worldOfDarknessDoomPad;
    private ulong worldOfDarknessTower;
    private bool worldOfDarknessMovementOwned;
    private bool? worldOfDarknessOnRing;

    private void ResetCrystalTowerWorldOfDarkness()
    {
        worldOfDarknessAtomos?.Clear();
        worldOfDarknessStateCursor = worldOfDarknessTower = 0;
        worldOfDarknessDoomPad = null;
        worldOfDarknessMovementOwned = false;
        worldOfDarknessOnRing = null;
    }

    private bool TryHandleCrystalTowerWorldOfDarkness(DutyContextSnapshot context, CrystalTowerObservation snapshot,
        CrystalTowerAlliance alliance, ObservationSnapshot observation)
    {
        if (context.TerritoryTypeId != 151 || context.ContentFinderConditionId != 111)
            return false;
        var local = snapshot.LocalActor;
        if (local is not { Dead: false, StatusIds: not null })
            return HoldWorldOfDarkness(context, snapshot, "Crystal Tower: waiting for verified alliance mechanic state.");
        CaptureWorldOfDarknessDoomPad(context, snapshot);

        if (local.StatusIds.Contains(210u))
        {
            var pads = snapshot.Actors.Where(actor => worldOfDarknessDoomPad is { } owned
                && actor.GameObjectId == owned.GameObjectId && actor.EntityId == owned.EntityId && actor.DataId == owned.DataId).ToArray();
            var pad = pads.Length == 1 ? pads[0] : null;
            return pad is not null
                ? MoveWorldOfDarkness(context, snapshot, pad.GameObjectId, WorldOfDarknessXz(pad), 2)
                : HoldWorldOfDarkness(context, snapshot, "Crystal Tower: waiting for the active Doom-cleansing platform.");
        }

        // The stomach is a separate observed floor, and the outside boss may be unloaded.
        // Do not let loaded actors from another room win before that floor is handled.
        if (local.Position.Y < -100)
            return TryWorldOfDarknessCerberus(context, snapshot, alliance, observation);
        if (TryHandleCrystalTowerWaves(context, snapshot, observation)) return true;
        if (TryWorldOfDarknessFiveHeadedAdds(context, snapshot, observation)) return true;
        if (TryWorldOfDarknessAtomos(context, snapshot, alliance, observation)) return true;
        if (TryWorldOfDarknessCerberus(context, snapshot, alliance, observation)) return true;
        if (TryWorldOfDarknessCloud(context, snapshot, alliance, observation)) return true;
        EndWorldOfDarknessMovement(context);
        return false;
    }

    private void CaptureWorldOfDarknessDoomPad(DutyContextSnapshot context, CrystalTowerObservation snapshot)
    {
        // Consume the existing trace's native EObjSetState; the byte GameObject.EventState is not
        // substituted for this packet. Preserve the most recent glowing pad, matching DoomPads.
        foreach (var row in ReadCrystalTowerEvents().Where(row => row.Sequence > worldOfDarknessStateCursor
                     && row.Sequence <= snapshot.SequenceWatermark).OrderBy(row => row.Sequence))
        {
            if (row.Kind != ServerEventKind.EObjState || row.Category != 409 || row.Replaying != 0
                || row.TimestampUtc < crystalTowerOwnerSinceUtc || row.TimestampUtc > ReadUtcNow()
                || row.TerritoryId != context.TerritoryTypeId || row.MapId != context.MapId
                || row.BaseId is not (0x1E9711 or 0x1E9712 or 0x1E9713 or 0x1E9714)) continue;
            var matches = snapshot.Actors.Where(actor => actor.GameObjectId == row.GameObjectId
                && actor.EntityId == row.EntityId && actor.DataId == row.BaseId
                && (row.ActorId == actor.EntityId || row.ActorId == (uint)actor.GameObjectId)).ToArray();
            if (matches.Length != 1) continue;
            if (row.P1 == 4) worldOfDarknessDoomPad = matches[0];
            else if (worldOfDarknessDoomPad?.GameObjectId == row.GameObjectId) worldOfDarknessDoomPad = null;
        }
        worldOfDarknessStateCursor = Math.Max(worldOfDarknessStateCursor, snapshot.SequenceWatermark);
        if (worldOfDarknessDoomPad is { } pad && !snapshot.Actors.Any(actor => actor.GameObjectId == pad.GameObjectId
                && actor.EntityId == pad.EntityId && actor.DataId == pad.DataId)) worldOfDarknessDoomPad = null;
    }

    private bool TryWorldOfDarknessFiveHeadedAdds(DutyContextSnapshot context, CrystalTowerObservation snapshot,
        ObservationSnapshot observation)
    {
        if (!InWorldOfDarknessFiveHeadedRoom(snapshot.LocalActor!.Position)) return false;
        // Pinned A34FiveHeadedDragon: Prominence priority 2, four Heads priority 1.
        // These are actual eligible actors, not an inferred phase or a scripted hazard path.
        var add = snapshot.Actors.Where(actor => actor.ObjectKind == ObjectKind.BattleNpc
                && actor.DataId is 0xDFD or 0xE13 or 0xE14 or 0xE15 or 0xE16
                && actor.Dead == false && actor.Targetable == true
                && IsCrystalTowerEntityId(actor.EntityId) && IsCrystalTowerObjectId(actor.GameObjectId)
                && InWorldOfDarknessFiveHeadedRoom(actor.Position))
            .OrderBy(actor => actor.DataId == 0xDFD ? 0 : 1)
            .ThenBy(actor => Vector3.DistanceSquared(actor.Position, snapshot.LocalActor!.Position))
            .ThenBy(actor => actor.GameObjectId).FirstOrDefault();
        return add is not null && TargetWorldOfDarkness(context, snapshot, add, observation);
    }

    private bool TryWorldOfDarknessAtomos(DutyContextSnapshot context, CrystalTowerObservation snapshot,
        CrystalTowerAlliance alliance, ObservationSnapshot observation)
    {
        if (!InWorldOfDarknessAtomosRoom(snapshot.LocalActor!.Position)) return false;
        var seen = snapshot.Actors.Where(actor => actor.DataId == 0xDA4).ToArray();
        if (seen.Length == 0 && worldOfDarknessAtomos.Count == 0) return false;
        var lanes = seen.GroupBy(WorldOfDarknessAtomosLane).ToArray();
        if (lanes.Any(group => group.Key == CrystalTowerAlliance.Unknown || group.Count() != 1))
            return HoldWorldOfDarkness(context, snapshot, "Crystal Tower: waiting for verified alliance mechanic state.");
        if (lanes.Any(group => worldOfDarknessAtomos.TryGetValue(group.Key, out var previous)
                && (previous.GameObjectId != group.Single().GameObjectId || previous.Dead == true && group.Single().Dead == false)))
            worldOfDarknessAtomos.Clear();
        foreach (var lane in lanes) worldOfDarknessAtomos[lane.Key] = lane.Single();
        if (worldOfDarknessAtomos.Count == 3 && worldOfDarknessAtomos.Values.All(actor => actor.Dead == true)) return false;
        var own = seen.SingleOrDefault(actor => WorldOfDarknessAtomosLane(actor) == alliance);
        if (own is not { Dead: false })
            return HoldWorldOfDarkness(context, snapshot, "Crystal Tower: waiting for the assigned Atomos platform.");
        var local = snapshot.LocalActor!;
        var onRing = local.Position.Y < 93;
        if (worldOfDarknessOnRing is { } previousRing && previousRing != onRing) StopMovementAssists();
        worldOfDarknessOnRing = onRing;
        var upperPads = snapshot.Actors.Where(actor => actor.DataId is >= 0x1E9738 and <= 0x1E9743
            && Vector2.DistanceSquared(WorldOfDarknessXz(actor), WorldOfDarknessXz(own)) <= 16 * 16).ToArray();
        if (onRing)
        {
            // Source JumpPads.LowerPadsNear: observed upper-pad direction, rotated +45 degrees,
            // projected to the authored 26y lower ring. A lower player never walks to an upper Y.
            var returns = upperPads.Select(pad =>
            {
                var direction = Vector2.Normalize(WorldOfDarknessXz(pad) - WorldOfDarknessXz(own));
                return WorldOfDarknessXz(own) + new Vector2(direction.X + direction.Y, direction.Y - direction.X)
                    * (26 / MathF.Sqrt(2));
            }).Where(point => float.IsFinite(point.X) && float.IsFinite(point.Y))
                .OrderBy(point => Vector2.DistanceSquared(point, WorldOfDarknessXz(local))).ToArray();
            return returns.Length != 0
                ? MoveWorldOfDarkness(context, snapshot, own.GameObjectId, returns[0], 2.6f)
                : HoldWorldOfDarkness(context, snapshot, "Crystal Tower: waiting for a verified Atomos return pad.");
        }
        if (own.IsCasting == true && own.CastActionId == 3416)
            return MoveWorldOfDarkness(context, snapshot, own.GameObjectId, WorldOfDarknessXz(own), 1.5f);
        if (TryAvoidWorldOfDarknessAtomosCone(context, snapshot, own, out var cone)) return true;
        if (Vector2.DistanceSquared(WorldOfDarknessXz(local), WorldOfDarknessXz(own)) > 18 * 18)
            return MoveWorldOfDarkness(context, snapshot, own.GameObjectId, WorldOfDarknessXz(own), 18);
        var glowing = upperPads.Where(pad => CrystalTowerAnimation(pad) == 0x00800100)
            .OrderBy(pad => Vector2.DistanceSquared(WorldOfDarknessXz(pad), WorldOfDarknessXz(local))).ThenBy(pad => pad.GameObjectId).FirstOrDefault();
        var add = snapshot.Actors.Where(actor => actor.DataId == 0xDA5 && actor.Dead == false && actor.Targetable == true
                && Vector2.DistanceSquared(WorldOfDarknessXz(actor), WorldOfDarknessXz(own)) <= 18 * 18)
            .OrderBy(actor => Vector3.DistanceSquared(actor.Position, local.Position)).FirstOrDefault();
        if (glowing is not null)
        {
            if (!snapshot.RosterComplete || snapshot.Members.Any(member => member.Alive == true && member.Position is null))
                return HoldWorldOfDarkness(context, snapshot, "Crystal Tower: waiting for verified tower assignments.");
            // Members project X/Z only: a fallen healer can share the upper platform's X/Z.
            // Join actual PC observations before preferring a holder, never invent a floor height.
            var candidates = snapshot.Members.Where(member => member.Alive == true && member.Position is { } position
                    && Vector2.DistanceSquared(position, WorldOfDarknessXz(own)) <= 18 * 18)
                .Select(member => (Member: member, Actors: member.Id == snapshot.LocalId
                    ? new[] { local }
                    : snapshot.Actors.Where(actor => actor.ObjectKind == ObjectKind.Pc
                        && actor.EntityId == member.Id).Take(2).ToArray())).ToArray();
            if (candidates.Any(candidate => candidate.Actors.Length != 1 || candidate.Actors[0].Dead != false
                    || !IsFiniteCrystalTowerPosition(candidate.Actors[0].Position)))
                return HoldWorldOfDarkness(context, snapshot, "Crystal Tower: waiting for verified tower assignments.");
            var holder = candidates.Where(candidate => candidate.Actors[0].Position.Y >= 93
                    && Vector2.DistanceSquared(WorldOfDarknessXz(candidate.Actors[0]), WorldOfDarknessXz(own)) <= 18 * 18)
                .OrderBy(candidate => candidate.Member.Role is CrystalTowerRole.Healer or CrystalTowerRole.Ranged ? 0 : 1)
                .ThenBy(candidate => candidate.Member.Id).Select(candidate => candidate.Member).FirstOrDefault();
            if (holder.Id == snapshot.LocalId)
            {
                if (Vector2.DistanceSquared(WorldOfDarknessXz(local), WorldOfDarknessXz(glowing)) > 2.6f * 2.6f)
                {
                    if (cone is { } active && !WorldOfDarknessAtomosConeApproachClear(active,
                            WorldOfDarknessXz(local), WorldOfDarknessXz(glowing)))
                        return TargetWorldOfDarkness(context, snapshot, add ?? own, observation, keepPosition: true);
                    return MoveWorldOfDarkness(context, snapshot, glowing.GameObjectId, WorldOfDarknessXz(glowing), cone?.ArrivalRadius ?? 2.6f);
                }
                return TargetWorldOfDarkness(context, snapshot, add ?? own, observation, keepPosition: true);
            }
        }
        return TargetWorldOfDarkness(context, snapshot, add ?? own, observation, keepPosition: cone is not null);
    }

    private readonly record struct WorldOfDarknessAtomosCone(Vector2 Origin, Vector2 Direction, float HalfAngle,
        float PlayerRadius, float BossRadius, float ArrivalRadius);

    private bool TryAvoidWorldOfDarknessAtomosCone(DutyContextSnapshot context, CrystalTowerObservation snapshot,
        CrystalTowerActorObservation boss, out WorldOfDarknessAtomosCone? cone)
    {
        cone = null;
        var local = snapshot.LocalActor!;
        Vector2 direction;
        float halfAngle;
        if (boss.IsCasting == true && boss.CastActionId == 3415)
        {
            // StandardAOEs uses CastInfo.Rotation, never the caster's current facing.
            if (boss.CastRotation is not { } rotation || !float.IsFinite(rotation))
                return HoldWorldOfDarkness(context, snapshot, "Crystal Tower: waiting for verified alliance mechanic state.");
            direction = new(MathF.Sin(rotation), MathF.Cos(rotation));
            halfAngle = MathF.PI / 3;
        }
        else if (boss.IsCasting == false)
        {
            if (boss.Targetable == false) return false;
            if (boss.Targetable != true || boss.TargetObjectId is not { } targetId || !IsCrystalTowerObjectId(targetId))
                return HoldWorldOfDarkness(context, snapshot, "Crystal Tower: waiting for verified alliance mechanic state.");
            // Cleave explicitly excludes its actual target, identified by full object ID.
            if (targetId == local.GameObjectId) return false;
            var targets = snapshot.Actors.Where(actor => actor.GameObjectId == targetId
                && actor.ObjectKind is ObjectKind.Pc or ObjectKind.BattleNpc).Take(2).ToArray();
            if (targets is not [var target] || target.Dead != false || !IsFiniteCrystalTowerPosition(target.Position))
                return HoldWorldOfDarkness(context, snapshot, "Crystal Tower: waiting for verified alliance mechanic state.");
            var offset = WorldOfDarknessXz(target) - WorldOfDarknessXz(boss);
            if (!float.IsFinite(offset.LengthSquared()) || offset.LengthSquared() == 0)
                return HoldWorldOfDarkness(context, snapshot, "Crystal Tower: waiting for verified alliance mechanic state.");
            direction = Vector2.Normalize(offset);
            halfAngle = MathF.PI / 4;
        }
        else if (boss.IsCasting != true || boss.CastActionId is null or 0)
            return HoldWorldOfDarkness(context, snapshot, "Crystal Tower: waiting for verified alliance mechanic state.");
        else return false; // Black Hole is inactive while any other known cast is active.

        if (local.HitboxRadius is not { } playerRadius || !float.IsFinite(playerRadius) || playerRadius <= 0
            || boss.HitboxRadius is not { } bossRadius || !float.IsFinite(bossRadius) || bossRadius <= 0
            || !IsFiniteCrystalTowerPosition(boss.Position) || !IsFiniteCrystalTowerPosition(local.Position))
            return HoldWorldOfDarkness(context, snapshot, "Crystal Tower: waiting for verified alliance mechanic state.");
        var arrival = Math.Clamp(playerRadius / 4, 0.05f, 0.2f);
        var active = new WorldOfDarknessAtomosCone(WorldOfDarknessXz(boss), direction, halfAngle, playerRadius, bossRadius, arrival);
        cone = active;
        var player = WorldOfDarknessXz(local);
        if (WorldOfDarknessAtomosConePointClear(active, player, playerRadius)
            && Vector2.Distance(player, active.Origin) + playerRadius <= 18)
            return false;

        // The reviewed shape is already 19y (15+boss R); do not add the boss radius
        // to its range again. Native radii keep the destination clear of the body/edge.
        var clearance = playerRadius + 2 * arrival;
        var inner = bossRadius + clearance;
        var outer = 18 - clearance;
        if (inner >= outer)
            return HoldWorldOfDarkness(context, snapshot, "Crystal Tower: waiting for verified alliance mechanic state.");
        var radius = Math.Clamp(Vector2.Distance(player, active.Origin), inner, outer);
        var angle = halfAngle + MathF.Asin(clearance / radius);
        var destinations = new[] { -angle, angle }.Select(turn => active.Origin
                + RotateWorldOfDarknessDirection(direction, turn) * radius)
            .Where(point => WorldOfDarknessAtomosConePointClear(active, point, playerRadius + arrival)
                && Vector2.Distance(point, active.Origin) + playerRadius + arrival <= 18
                && Vector2.Distance(point, active.Origin) >= bossRadius + playerRadius + arrival)
            .OrderBy(point => Vector2.DistanceSquared(point, player)).ThenBy(point => point.X).ThenBy(point => point.Y).ToArray();
        return destinations.Length != 0
            ? MoveWorldOfDarkness(context, snapshot, boss.GameObjectId, destinations[0], arrival)
            : HoldWorldOfDarkness(context, snapshot, "Crystal Tower: waiting for verified alliance mechanic state.");
    }

    private static Vector2 RotateWorldOfDarknessDirection(Vector2 direction, float angle)
        => new(direction.X * MathF.Cos(angle) + direction.Y * MathF.Sin(angle),
            direction.Y * MathF.Cos(angle) - direction.X * MathF.Sin(angle));

    private static bool WorldOfDarknessAtomosConePointClear(WorldOfDarknessAtomosCone cone, Vector2 point, float radius)
    {
        var offset = point - cone.Origin;
        var distance = offset.Length();
        var inAngle = Vector2.Dot(offset, cone.Direction) >= distance * MathF.Cos(cone.HalfAngle);
        if (inAngle && distance <= 19) return false;
        var left = RotateWorldOfDarknessDirection(cone.Direction, -cone.HalfAngle) * 19;
        var right = RotateWorldOfDarknessDirection(cone.Direction, cone.HalfAngle) * 19;
        var edgeDistance = Math.Min(WorldOfDarknessSegmentDistanceSquared(Vector2.Zero, left, offset),
            WorldOfDarknessSegmentDistanceSquared(Vector2.Zero, right, offset));
        if (inAngle) edgeDistance = Math.Min(edgeDistance, (distance - 19) * (distance - 19));
        return edgeDistance > radius * radius;
    }

    private static bool WorldOfDarknessAtomosConeApproachClear(WorldOfDarknessAtomosCone cone, Vector2 start, Vector2 destination)
    {
        var clearance = cone.PlayerRadius + cone.ArrivalRadius;
        if (!WorldOfDarknessAtomosConePointClear(cone, destination, clearance)
            || Vector2.Distance(destination, cone.Origin) + clearance > 18
            || Vector2.Distance(destination, cone.Origin) < cone.BossRadius + clearance)
            return false;
        var left = RotateWorldOfDarknessDirection(cone.Direction, -cone.HalfAngle);
        var right = RotateWorldOfDarknessDirection(cone.Direction, cone.HalfAngle);
        // A common separating half-plane proves that the whole direct approach is
        // outside the cone; safe endpoints alone cannot establish that for a pad route.
        return new[] { -cone.Direction, new Vector2(-left.Y, left.X), new Vector2(right.Y, -right.X) }
            .Any(normal => Vector2.Dot(start - cone.Origin, normal) > cone.PlayerRadius
                && Vector2.Dot(destination - cone.Origin, normal) > clearance)
            && WorldOfDarknessSegmentDistanceSquared(start, destination, cone.Origin)
                >= (cone.BossRadius + cone.PlayerRadius) * (cone.BossRadius + cone.PlayerRadius);
    }

    private static CrystalTowerAlliance WorldOfDarknessAtomosLane(CrystalTowerActorObservation actor)
    {
        // Source arena center (0,31) + offsets (-31,18), (0,-36), (31,18).
        var lanes = new[] { (CrystalTowerAlliance.A, new Vector2(-31, 49)),
            (CrystalTowerAlliance.B, new Vector2(0, -5)), (CrystalTowerAlliance.C, new Vector2(31, 49)) };
        var matches = lanes.Where(lane => Vector2.DistanceSquared(WorldOfDarknessXz(actor), lane.Item2) <= 18 * 18).ToArray();
        return matches.Length == 1 ? matches[0].Item1 : CrystalTowerAlliance.Unknown;
    }

    private bool TryWorldOfDarknessCerberus(DutyContextSnapshot context, CrystalTowerObservation snapshot,
        CrystalTowerAlliance alliance, ObservationSnapshot observation)
    {
        var local = snapshot.LocalActor!;
        if (local.Position.Y < -100)
        {
            var stomach = snapshot.Actors.Where(actor => actor.DataId is 0xDFA or 0xDF7
                    && actor.Dead == false && actor.Targetable == true && actor.Position.Y < -100)
                .OrderBy(actor => actor.DataId == 0xDFA ? 0 : 1)
                .ThenBy(actor => Vector3.DistanceSquared(actor.Position, local.Position)).FirstOrDefault();
            return stomach is not null ? TargetWorldOfDarkness(context, snapshot, stomach, observation)
                : HoldWorldOfDarkness(context, snapshot, "Crystal Tower: waiting for the stomach exit transition.");
        }
        if (!InWorldOfDarknessCerberusRoom(local.Position)) return false;
        var bosses = snapshot.Actors.Where(actor => actor.DataId == 0xDF6).ToArray();
        if (bosses.Length > 1)
            return HoldWorldOfDarkness(context, snapshot, "Crystal Tower: waiting for verified alliance mechanic state.");
        var boss = bosses.SingleOrDefault();
        if (boss is not { Dead: false }) return false;
        var outsideAdd = snapshot.Actors.Where(actor => actor.DataId == 0xDF8 && actor.Dead == false && actor.Targetable == true
                && actor.Position.Y >= -100).OrderBy(actor => Vector3.DistanceSquared(actor.Position, local.Position)).FirstOrDefault();
        if (boss.IsCasting == true && boss.CastActionId == 3507)
        {
            if (alliance == CrystalTowerAlliance.A && outsideAdd is not null)
                return TargetWorldOfDarkness(context, snapshot, outsideAdd, observation, keepPosition: true);
            return HoldWorldOfDarkness(context, snapshot, "Crystal Tower: automatic chain handling is unavailable.");
        }
        var juices = snapshot.Actors.Where(actor => actor.DataId == 0xDF9 && actor.Dead == false).ToArray();
        var puddles = snapshot.Actors.Where(actor => actor.DataId == 0x1E968C && actor.EventState is not null and not 7).ToArray();
        // No observed belly phase means no belly/chain detour, including normal UNSYNC burst kills.
        if (juices.Length == 0 && puddles.Length == 0)
            return GetCrystalTowerStrategy(context) == CrystalTowerStrategyMode.Sync
                && TargetWorldOfDarkness(context, snapshot,
                    alliance == CrystalTowerAlliance.A && outsideAdd is not null ? outsideAdd : boss, observation);
        var mini = local.StatusIds!.Contains(438u);
        if (alliance == CrystalTowerAlliance.B)
        {
            if (!mini && TryAvoidWorldOfDarknessCircles(context, snapshot, puddles.Select(actor => (actor, 8f)))) return true;
            var destination = mini ? puddles.OrderBy(actor => Vector3.DistanceSquared(actor.Position, local.Position)).FirstOrDefault()
                : juices.Where(actor => actor.IsCasting == true && actor.CastActionId == 3249)
                    .OrderBy(actor => Vector3.DistanceSquared(actor.Position, local.Position)).FirstOrDefault();
            if (!mini && destination is not null && puddles.Any(puddle => WorldOfDarknessSegmentDistanceSquared(
                    WorldOfDarknessXz(local), WorldOfDarknessXz(destination), WorldOfDarknessXz(puddle)) <= 8 * 8))
                return HoldWorldOfDarkness(context, snapshot, "Crystal Tower: waiting for a verified safe approach to Mini.");
            return destination is not null
                ? MoveWorldOfDarkness(context, snapshot, destination.GameObjectId, WorldOfDarknessXz(destination), mini ? 8 : 9)
                : HoldWorldOfDarkness(context, snapshot, "Crystal Tower: waiting for Mini before entering Cerberus's puddle.");
        }
        // Keep A/C out of the provider's DPS-belly strategy while the actual entry phase exists.
        // No guessed safe coordinate or chain callback is substituted for missing native contracts.
        if (TryAvoidWorldOfDarknessCircles(context, snapshot, puddles.Select(actor => (actor, 8f))
                .Concat(juices.Where(actor => actor.IsCasting == true && actor.CastActionId == 3249).Select(actor => (actor, 9f)))))
            return true;
        var outsideTarget = alliance == CrystalTowerAlliance.A ? outsideAdd : null;
        return TargetWorldOfDarkness(context, snapshot, outsideTarget ?? boss, observation, keepPosition: true);
    }

    private bool TryAvoidWorldOfDarknessCircles(DutyContextSnapshot context, CrystalTowerObservation snapshot,
        IEnumerable<(CrystalTowerActorObservation Actor, float Radius)> circles)
    {
        var local = snapshot.LocalActor!;
        foreach (var circle in circles)
        {
            var offset = WorldOfDarknessXz(local) - WorldOfDarknessXz(circle.Actor);
            if (offset.LengthSquared() > circle.Radius * circle.Radius) continue;
            if (local.HitboxRadius is not > 0 || offset.LengthSquared() == 0)
                return HoldWorldOfDarkness(context, snapshot, "Crystal Tower: waiting for a verified safe approach to Mini.");
            var destination = WorldOfDarknessXz(circle.Actor) + Vector2.Normalize(offset)
                * (circle.Radius + local.HitboxRadius.Value);
            return MoveWorldOfDarkness(context, snapshot, circle.Actor.GameObjectId, destination,
                Math.Min(0.5f, local.HitboxRadius.Value / 2));
        }
        return false;
    }

    private static float WorldOfDarknessSegmentDistanceSquared(Vector2 start, Vector2 end, Vector2 point)
    {
        var delta = end - start;
        var t = delta.LengthSquared() == 0 ? 0 : Math.Clamp(Vector2.Dot(point - start, delta) / delta.LengthSquared(), 0, 1);
        return Vector2.DistanceSquared(start + delta * t, point);
    }

    private bool TryWorldOfDarknessCloud(DutyContextSnapshot context, CrystalTowerObservation snapshot,
        CrystalTowerAlliance alliance, ObservationSnapshot observation)
    {
        if (!InWorldOfDarknessCloudRoom(snapshot.LocalActor!.Position)) return false;
        var activeHexes = snapshot.Actors.Where(actor => actor.DataId is 0x1E9752 or 0x1E9750 or 0x1E9751
            && CrystalTowerAnimation(actor) == 0x00040008).ToArray();
        if (activeHexes.Length != 0)
        {
            var ownId = alliance switch { CrystalTowerAlliance.A => 0x1E9752u, CrystalTowerAlliance.B => 0x1E9750u, _ => 0x1E9751u };
            var own = activeHexes.Where(actor => actor.DataId == ownId).ToArray();
            if (own.Length != 1)
                return HoldWorldOfDarkness(context, snapshot, "Crystal Tower: waiting for the assigned hypercharged-cloud region.");
            var hex = own[0];
            if (Vector2.DistanceSquared(WorldOfDarknessXz(snapshot.LocalActor!), WorldOfDarknessXz(hex)) > 8.5f * 8.5f)
                return MoveWorldOfDarkness(context, snapshot, hex.GameObjectId, WorldOfDarknessXz(hex), 8.5f);
            var cloud = snapshot.Actors.Where(actor => actor.DataId == 0xE03 && actor.Dead == false && actor.Targetable == true
                && Vector2.DistanceSquared(WorldOfDarknessXz(actor), WorldOfDarknessXz(hex)) <= 8.5f * 8.5f).ToArray();
            return cloud.Length == 1 ? TargetWorldOfDarkness(context, snapshot, cloud[0], observation, keepPosition: true)
                : HoldWorldOfDarkness(context, snapshot, "Crystal Tower: holding the assigned hypercharged-cloud region until it clears.");
        }
        var towers = snapshot.Actors.Where(actor => actor.DataId == 0x1E974E).OrderBy(actor => actor.GameObjectId).ToArray();
        if (towers.Length != 0)
        {
            if (!snapshot.AllianceRosterComplete || snapshot.AllianceMembers is not { Count: > 0 and <= 24 } members
                || members.Count(member => member.Id == snapshot.LocalId) != 1
                || members.Select(member => member.Id).Distinct().Count() != members.Count
                || members.Any(member => member.Id == 0 || member.Alive is null
                    || member.Alive == true && (member.Position is not { } position
                        || !float.IsFinite(position.X) || !float.IsFinite(position.Y))))
            {
                worldOfDarknessTower = 0;
                return HoldWorldOfDarkness(context, snapshot, "Crystal Tower: waiting for verified tower assignments.");
            }
            worldOfDarknessTower = AssignWorldOfDarknessTower(snapshot, towers);
            var tower = towers.SingleOrDefault(actor => actor.GameObjectId == worldOfDarknessTower);
            if (tower is not null) return MoveWorldOfDarkness(context, snapshot, tower.GameObjectId, WorldOfDarknessXz(tower), 5);
        }
        else worldOfDarknessTower = 0;
        var adds = snapshot.Actors.Where(actor => actor.DataId is 0xE1C or 0xE1D or 0xCFB
                && actor.Dead == false && actor.Targetable == true)
            .OrderBy(actor => actor.DataId == 0xE1D ? 0 : actor.DataId == 0xE1C ? 1 : 2)
            .ThenBy(actor => Vector3.DistanceSquared(actor.Position, snapshot.LocalActor!.Position)).FirstOrDefault();
        return adds is not null && TargetWorldOfDarkness(context, snapshot, adds, observation);
    }

    private static ulong AssignWorldOfDarknessTower(CrystalTowerObservation snapshot, CrystalTowerActorObservation[] towers)
    {
        // Every alliance uses the same complete global roster and ID ordering. The local
        // party cannot stand in for the other alliances when their membership is unknown.
        var available = towers.ToList();
        foreach (var member in snapshot.AllianceMembers!.Where(member => member.Alive == true && member.Position.HasValue).OrderBy(member => member.Id))
        {
            var nearest = available.OrderBy(tower => Vector2.DistanceSquared(member.Position!.Value, WorldOfDarknessXz(tower)))
                .ThenBy(tower => tower.GameObjectId).FirstOrDefault();
            if (nearest is null) break;
            if (member.Id == snapshot.LocalId) return nearest.GameObjectId;
            available.Remove(nearest);
        }
        return 0;
    }

    private bool MoveWorldOfDarkness(DutyContextSnapshot context, CrystalTowerObservation snapshot,
        ulong identity, Vector2 destination, float radius)
    {
        worldOfDarknessMovementOwned = true;
        return TryCrystalTowerMove(context, snapshot, identity, destination, radius);
    }

    private bool TargetWorldOfDarkness(DutyContextSnapshot context, CrystalTowerObservation snapshot,
        CrystalTowerActorObservation target, ObservationSnapshot observation, bool keepPosition = false)
    {
        worldOfDarknessMovementOwned = true;
        return TryCrystalTowerTarget(context, snapshot, target, observation, keepPosition);
    }

    private bool HoldWorldOfDarkness(DutyContextSnapshot context, CrystalTowerObservation snapshot, string reason)
    {
        worldOfDarknessMovementOwned = true;
        TryAcquireCrystalTowerMovement(context, (uint)snapshot.LocalId, GetCrystalTowerStrategy(context), out _);
        return HoldCrystalTower(reason);
    }

    private void EndWorldOfDarknessMovement(DutyContextSnapshot context)
    {
        if (!worldOfDarknessMovementOwned) return;
        StopMovementAssists();
        ReleaseCrystalTowerMovement(context, restore: true);
        worldOfDarknessMovementOwned = false;
    }

    // Admission is based on this player's current room, not loaded future actors or a
    // movement lease acquired for Doom/another room. The enclosing Atomos circle includes
    // all authored platform offsets (max 36y), the 26y lower ring and 3.6y jump pads.
    private static bool InWorldOfDarknessAtomosRoom(Vector3 position)
        => IsFiniteCrystalTowerPosition(position)
            && Vector2.DistanceSquared(new(position.X, position.Z), new(0, 31)) <= 66.6f * 66.6f;

    private static bool InWorldOfDarknessFiveHeadedRoom(Vector3 position)
        => IsFiniteCrystalTowerPosition(position)
            && Vector2.DistanceSquared(new(position.X, position.Z), new(200, 179)) <= 30 * 30;

    private static bool InWorldOfDarknessCerberusRoom(Vector3 position)
        => IsFiniteCrystalTowerPosition(position) && MathF.Abs(position.X) <= 20
            && MathF.Abs(position.Z + 198) <= 40;

    private static bool InWorldOfDarknessCloudRoom(Vector3 position)
        => IsFiniteCrystalTowerPosition(position)
            && Vector2.DistanceSquared(new(position.X, position.Z), new(-300, -400)) <= 30 * 30;

    private static Vector2 WorldOfDarknessXz(CrystalTowerActorObservation actor) => new(actor.Position.X, actor.Position.Z);
}
