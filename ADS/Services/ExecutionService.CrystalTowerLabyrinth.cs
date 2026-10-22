using System.Numerics;
using ADS.Models;

namespace ADS.Services;

public sealed partial class ExecutionService
{
    private bool crystalTowerFlareCasting;
    private readonly Dictionary<CrystalTowerAlliance, ulong> crystalTowerVassagoAssignments = [];
    private ulong crystalTowerBoneDragon;
    private bool crystalTowerBoneComplete;
    private CrystalTowerActorObservation? crystalTowerBoneActor;
    private ulong crystalTowerBoneAttemptBoundary;
    private CrystalTowerActorObservation? crystalTowerBombActor;
    private readonly HashSet<(ulong ObjectId, uint EntityId)> crystalTowerPreviousVassago = [];
    private ulong crystalTowerMeteorBoss;
    private bool crystalTowerMeteorActive;

    private void ResetCrystalTowerLabyrinthAttempts()
    {
        crystalTowerBoneActor = crystalTowerBombActor = null;
        crystalTowerBoneAttemptBoundary = 0;
        crystalTowerPreviousVassago?.Clear();
    }

    private bool TryHandleCrystalTowerBoneDragon(DutyContextSnapshot context, CrystalTowerObservation snapshot,
        ObservationSnapshot observation)
    {
        // awgil d409d336 A11BoneDragon: the first two deaths revive. DirectorUpdate
        // 0x80000001 / param1 0xB4 ends the encounter, not an arbitrary corpse/timeout.
        var dragons = snapshot.Actors.Where(actor => actor.DataId == 0x92B).ToArray();
        if (!crystalTowerBoneComplete && Vector2.Distance(
                new(snapshot.LocalActor!.Position.X, snapshot.LocalActor.Position.Z), new(-451.2f, 23.93f)) > 49.4f)
            return false;
        if (dragons.Length > 1)
            return HoldCrystalTower("Crystal Tower: waiting for verified alliance mechanic state.");
        if (dragons.Length == 0 && crystalTowerBoneDragon == 0) return false;
        var boss = dragons.SingleOrDefault();
        if (boss is not null)
        {
            var previous = crystalTowerBoneActor;
            var replaced = previous is not null
                && (previous.GameObjectId != boss.GameObjectId || previous.EntityId != boss.EntityId);
            var revived = previous?.Dead == true && boss.Dead == false;
            if (replaced || revived)
            {
                crystalTowerBoneComplete = false;
                // Events delivered with the replacement/revival sample cannot
                // reapply the previous attempt's B4. A later receipt is required.
                crystalTowerBoneAttemptBoundary = snapshot.SequenceWatermark;
                foreach (var update in snapshot.Directors ?? [])
                    crystalTowerBoneAttemptBoundary = Math.Max(crystalTowerBoneAttemptBoundary, update.Sequence);
            }
            crystalTowerBoneDragon = boss.GameObjectId;
            // Retain a positive death across an unreadable sample so a subsequent
            // living read still detects the same native identity's revival.
            if (previous is null || replaced || boss.Dead.HasValue) crystalTowerBoneActor = boss;
        }
        if (snapshot.Directors?.Any(update => update.Sequence > crystalTowerBoneAttemptBoundary
                && update.UpdateId == 0x80000001 && update.Param1 == 0xB4) == true)
            crystalTowerBoneComplete = true;
        if (crystalTowerBoneComplete) return false;
        var add = snapshot.Actors.Where(actor => actor.DataId is 0x92D or 0x982
                && actor.Dead == false && actor.Targetable == true)
            .OrderBy(actor => actor.DataId == 0x92D ? 0 : 1)
            .ThenBy(actor => Vector3.DistanceSquared(actor.Position, snapshot.LocalActor!.Position)).FirstOrDefault();
        if (add is not null) return TryCrystalTowerTarget(context, snapshot, add, observation);
        return boss is { Dead: false, Targetable: true }
            ? TryCrystalTowerTarget(context, snapshot, boss, observation)
            : HoldCrystalTower("Crystal Tower: waiting for verified alliance mechanic state.");
    }

    private bool TryHandleCrystalTowerMeteor(DutyContextSnapshot context, CrystalTowerObservation snapshot)
    {
        // awgil d409d336 A15KingBehemoth: living comets outside the boss's overlapping
        // hitboxes block Ecliptic Meteor (1756). Use actual observed radii and positions.
        var bosses = snapshot.Actors.Where(actor => actor.DataId == 0x932).ToArray();
        if (bosses.Length > 1)
            return HoldCrystalTower("Crystal Tower: waiting for verified alliance mechanic state.");
        var boss = bosses.SingleOrDefault();
        if (boss is null)
            return crystalTowerMeteorActive && HoldCrystalTower("Crystal Tower: waiting for verified alliance mechanic state.");
        if (!crystalTowerMeteorActive && Vector3.Distance(boss.Position, snapshot.LocalActor!.Position) > 100f)
            return false; // Pinned Ecliptic Meteor LOS effect range is 100; distant arena actors are not this phase.
        if (crystalTowerMeteorBoss != boss.GameObjectId)
        {
            if (crystalTowerMeteorActive) StopMovementAssists();
            crystalTowerMeteorBoss = boss.GameObjectId;
            crystalTowerMeteorActive = false;
        }
        if (boss.Dead == true)
        {
            if (crystalTowerMeteorActive) StopMovementAssists();
            crystalTowerMeteorActive = false;
            return false;
        }
        var wasActive = crystalTowerMeteorActive;
        if (boss.IsCasting == false) crystalTowerMeteorActive = false;
        else if (boss.IsCasting == true && boss.CastActionId.HasValue)
            crystalTowerMeteorActive = boss.CastActionId == 1756;
        else return HoldCrystalTower("Crystal Tower: waiting for verified alliance mechanic state.");
        if (!crystalTowerMeteorActive)
        {
            if (wasActive) StopMovementAssists();
            return false;
        }
        if (boss.HitboxRadius is not > 0)
            return HoldCrystalTower("Crystal Tower: waiting for verified alliance mechanic state.");
        var source = new Vector2(boss.Position.X, boss.Position.Z);
        var shelters = snapshot.Actors.Where(actor => actor.DataId == 0x936 && actor.Dead == false
                && actor.HitboxRadius is > 0)
            .Select(comet => (Actor: comet, Center: new Vector2(comet.Position.X, comet.Position.Z)))
            .Where(comet => Vector2.Distance(source, comet.Center) > boss.HitboxRadius.Value + comet.Actor.HitboxRadius!.Value)
            .Select(comet => (comet.Actor, Position: comet.Center + Vector2.Normalize(comet.Center - source)
                * (comet.Actor.HitboxRadius!.Value + 0.6f)))
            .Where(comet => Vector2.Distance(comet.Position, new(-110, -368.35f)) < 29.5f)
            .OrderBy(comet => Vector2.DistanceSquared(comet.Position,
                new(snapshot.LocalActor!.Position.X, snapshot.LocalActor.Position.Z))).ToArray();
        return shelters.Length == 0 ? HoldCrystalTower("Crystal Tower: waiting for verified alliance mechanic state.")
            : TryCrystalTowerMove(context, snapshot, shelters[0].Actor.GameObjectId, shelters[0].Position,
                Math.Min(0.2f, shelters[0].Actor.HitboxRadius!.Value * 0.1f));
    }

    private bool TryHandleCrystalTowerFlare(DutyContextSnapshot context, CrystalTowerObservation snapshot, CrystalTowerAlliance alliance)
    {
        // Pinned A16Phlegethon center/outer bound. Observing another room's actor is not phase admission.
        if (!crystalTowerFlareActive && !InCrystalTowerArena(snapshot, new(-110, 181.6f), 44.5f)) return false;
        var bosses = snapshot.Actors.Where(actor => actor.DataId == CrystalTowerMechanicPolicy.PhlegethonBossDataId).ToArray();
        if (bosses.Length > 1) return HoldCrystalTower("Crystal Tower: waiting for verified alliance mechanic state.");
        var boss = bosses.SingleOrDefault();
        if (boss is null)
            return crystalTowerFlareActive && HoldCrystalTower("Crystal Tower: waiting for verified alliance mechanic state.");
        if (crystalTowerFlareBoss != boss.GameObjectId)
        {
            crystalTowerFlareBoss = boss.GameObjectId;
            crystalTowerFlareActive = false;
            crystalTowerFlareCasting = false;
        }
        if (boss.Dead == true)
        {
            crystalTowerFlareActive = crystalTowerFlareCasting = false;
            return false;
        }
        if (boss.IsCasting.HasValue)
        {
            var casting = boss.IsCasting == true && boss.CastActionId == CrystalTowerMechanicPolicy.AncientFlareCastActionId;
            if (casting && !crystalTowerFlareCasting)
            {
                crystalTowerFlareActive = true;
                crystalTowerFlareBoundary = crystalTowerEventCursor;
            }
            crystalTowerFlareCasting = casting;
        }
        if (!crystalTowerFlareActive) return false;
        // Only the pinned EAnim clear after this cast's boundary can release a living-boss retreat.
        var clear = snapshot.Actors.Where(actor => actor.DataId == 0x1E8885)
            .Any(actor => crystalTowerAnimations.TryGetValue(actor.GameObjectId, out var animation)
                && animation.EntityId == actor.EntityId && animation.DataId == actor.DataId
                && animation.Sequence > crystalTowerFlareBoundary && animation.State == 0x00110022);
        var retreat = CrystalTowerMechanicPolicy.EvaluatePhlegethon(alliance, snapshot.LocalActor?.Dead == false,
            crystalTowerFlareActive, clear);
        if (retreat.Action == CrystalTowerRetreatAction.Released)
        {
            crystalTowerFlareActive = false;
            StopMovementAssists();
            ReleaseCrystalTowerMovement(context, restore: true);
            return false;
        }
        return retreat is { Action: CrystalTowerRetreatAction.Retreat, DestinationXZ: { } destination }
            ? TryCrystalTowerMove(context, snapshot, boss.GameObjectId, destination, 4.2f)
            : HoldCrystalTower("Crystal Tower: waiting for verified alliance mechanic state.");
    }

    private bool TryHandleCrystalTowerAtomos(DutyContextSnapshot context, CrystalTowerObservation snapshot,
        CrystalTowerAlliance alliance, ObservationSnapshot observation)
    {
        // Pinned A12Atomos custom bound: center232.5/280, platform half-width37.65,
        // combined three-lane half-height47.5. Distant future-room actors do not seize progression.
        var localPosition = snapshot.LocalActor!.Position;
        if (Math.Abs(localPosition.X - 232.5f) > 37.65f || Math.Abs(localPosition.Z - 280) > 47.5f) return false;
        var seen = snapshot.Actors.Where(actor => actor.DataId == CrystalTowerMechanicPolicy.AtomosBossDataId).ToArray();
        if (seen.Length == 0 && crystalTowerAtomos.Count == 0) return false;
        var byLane = seen.GroupBy(actor => CrystalTowerMechanicPolicy.GetAtomosLane(new(actor.Position.X, actor.Position.Z))).ToArray();
        if (byLane.Any(group => group.Key == CrystalTowerAlliance.Unknown || group.Count() != 1))
            return HoldCrystalTower("Crystal Tower: waiting for verified alliance mechanic state.");
        if (byLane.Any(group => crystalTowerAtomos.TryGetValue(group.Key, out var old)
                && (old.GameObjectId != group.Single().GameObjectId || old.Dead == true && group.Single().Dead == false)))
        {
            crystalTowerAtomos.Clear();
            crystalTowerHolders = [];
        }
        foreach (var group in byLane) crystalTowerAtomos[group.Key] = group.Single();
        if (crystalTowerAtomos.Count == 3 && crystalTowerAtomos.Values.All(actor => actor.Dead == true))
            return false;
        if (crystalTowerHolders.Count == 0)
            crystalTowerHolders = CrystalTowerMechanicPolicy.SelectAtomosHolders(snapshot.RosterComplete, snapshot.Members);
        var padId = CrystalTowerMechanicPolicy.GetAtomosPadDataId(alliance);
        var pad = padId.HasValue ? SingleCrystalTowerActor(snapshot, padId.Value) : null;
        var ringId = alliance switch { CrystalTowerAlliance.A => 0x1E8F7Du, CrystalTowerAlliance.B => 0x1E8F7Eu, _ => 0x1E8F7Fu };
        var ringState = CrystalTowerAnimation(SingleCrystalTowerActor(snapshot, ringId));
        bool? vulnerable = ringState switch { 0x04400880 => true, 0x10042200 => false, _ => null };
        var states = new[] { CrystalTowerAlliance.A, CrystalTowerAlliance.B, CrystalTowerAlliance.C }
            .Select(lane => new CrystalTowerBossState(lane, crystalTowerAtomos.TryGetValue(lane, out var actor)
                ? actor.Dead.HasValue ? !actor.Dead.Value : null : null)).ToArray();
        var decision = CrystalTowerMechanicPolicy.EvaluateAtomos(alliance, snapshot.LocalId, snapshot.RosterComplete,
            snapshot.Members, crystalTowerHolders, pad is null ? null : new(pad.Position.X, pad.Position.Z), states, vulnerable);
        if (!decision.CanAttackAtomos && crystalTowerTarget != 0
            && targetManager.Target?.GameObjectId == crystalTowerTarget
            && targetManager.Target?.BaseId == CrystalTowerMechanicPolicy.AtomosBossDataId)
        {
            targetManager.Target = null;
            crystalTowerTarget = 0;
        }
        if (decision.Action == CrystalTowerAtomosAction.HoldPad && decision.PadXZ is { } position && pad is not null)
        {
            TryCrystalTowerMove(context, snapshot, pad.GameObjectId, position, CrystalTowerMechanicPolicy.AtomosPadRadius);
            if (!IsCrystalTowerMovementQuiescent || snapshot.LocalActor is not { } local
                || Vector2.Distance(new(local.Position.X, local.Position.Z), position) > CrystalTowerMechanicPolicy.AtomosPadRadius)
                return true;
            var standingTarget = SelectCrystalTowerAtomosTarget(snapshot, alliance, decision.CanAttackAtomos);
            return standingTarget is not null
                ? TryCrystalTowerTarget(context, snapshot, standingTarget, observation, keepPosition: true) : true;
        }
        if (decision.Action is CrystalTowerAtomosAction.AttackAtomos or CrystalTowerAtomosAction.AttackAdds or CrystalTowerAtomosAction.Released)
        {
            var target = SelectCrystalTowerAtomosTarget(snapshot, alliance, decision.CanAttackAtomos);
            if (target is not null) return TryCrystalTowerTarget(context, snapshot, target, observation);
        }
        return HoldCrystalTower("Crystal Tower: waiting for verified alliance mechanic state.");
    }

    private static CrystalTowerActorObservation? SelectCrystalTowerAtomosTarget(CrystalTowerObservation snapshot,
        CrystalTowerAlliance alliance, bool canAttackBoss)
        => snapshot.Actors.Where(actor => actor.Dead == false && actor.Targetable == true
                && (actor.DataId is 0x964 or 0x965 or 0x966 || canAttackBoss && actor.DataId == CrystalTowerMechanicPolicy.AtomosBossDataId)
                && CrystalTowerMechanicPolicy.GetAtomosLane(new(actor.Position.X, actor.Position.Z)) == alliance)
            .OrderBy(actor => actor.DataId == CrystalTowerMechanicPolicy.AtomosBossDataId ? 1 : 0)
            .ThenBy(actor => Vector3.DistanceSquared(actor.Position, snapshot.LocalActor!.Position))
            .FirstOrDefault();

    private bool TryHandleCrystalTowerTargetGates(DutyContextSnapshot context, CrystalTowerObservation snapshot,
        ObservationSnapshot observation)
    {
        // ThanatosBounds: circle27.5 plus three rotated34 +/-10 by +/-8 rectangles.
        // The calculated outer radius encloses every authored rectangle, including its corners.
        var thanatosActors = InCrystalTowerArena(snapshot, new(440.4f, 280), MathF.Sqrt(44 * 44 + 8 * 8))
            ? snapshot.Actors.Where(actor => actor.DataId == CrystalTowerMechanicPolicy.ThanatosBossDataId).ToArray() : [];
        if (thanatosActors.Length > 1) return HoldCrystalTower("Crystal Tower: waiting for verified alliance mechanic state.");
        var thanatos = thanatosActors.SingleOrDefault();
        if (thanatos is { Dead: false })
        {
            var eligible = CrystalTowerMechanicPolicy.CanAttackThanatos(snapshot.LocalActor?.Dead == false,
                thanatos.Dead == false, snapshot.LocalActor?.StatusIds);
            var target = eligible && thanatos.Targetable == true ? thanatos
                : snapshot.Actors.Where(actor => actor.DataId is 0x930 or 0x983 && actor.Dead == false && actor.Targetable == true)
                    .OrderBy(actor => Vector3.DistanceSquared(actor.Position, snapshot.LocalActor!.Position)).FirstOrDefault();
            return target is not null ? TryCrystalTowerTarget(context, snapshot, target, observation)
                : HoldCrystalTower("Crystal Tower: assigned target is not eligible.");
        }
        if (!InCrystalTowerArena(snapshot, new(-110, -165.6f), 33.2f)) return false;
        var bombs = snapshot.Actors.Where(actor => actor.DataId == CrystalTowerMechanicPolicy.AllaganBombDataId).ToArray();
        if (bombs.Length > 1) return HoldCrystalTower("Crystal Tower: waiting for verified alliance mechanic state.");
        var bomb = bombs.SingleOrDefault();
        if (bomb is null) return false;
        var previousBomb = crystalTowerBombActor;
        var bombReplaced = previousBomb is not null
            && (previousBomb.GameObjectId != bomb.GameObjectId || previousBomb.EntityId != bomb.EntityId);
        if (previousBomb is null || bombReplaced || previousBomb.Dead == true && bomb.Dead == false)
        {
            foreach (var actor in crystalTowerVassago.Values)
                crystalTowerPreviousVassago.Add((actor.GameObjectId, actor.EntityId));
            crystalTowerVassago.Clear();
            crystalTowerVassagoAssignments.Clear();
        }
        if (previousBomb is null || bombReplaced || bomb.Dead.HasValue) crystalTowerBombActor = bomb;
        if (bomb.Dead != false) return false;
        // Corpses still loaded from an older Bomb attempt cannot immediately
        // re-establish its add deaths. A new identity or positive living read can.
        var vassago = snapshot.Actors.Where(actor => actor.DataId == CrystalTowerMechanicPolicy.VassagoDataId
            && (actor.Dead == false || !crystalTowerPreviousVassago.Contains((actor.GameObjectId, actor.EntityId)))).ToArray();
        foreach (var actor in vassago.Where(actor => actor.Dead == false))
            crystalTowerPreviousVassago.Remove((actor.GameObjectId, actor.EntityId));
        if (vassago.Any(actor => !crystalTowerVassago.ContainsKey(actor.GameObjectId)
                && crystalTowerVassago.Count > 0 || crystalTowerVassago.TryGetValue(actor.GameObjectId, out var old)
                    && old.Dead == true && actor.Dead == false))
        {
            crystalTowerVassago.Clear();
            crystalTowerVassagoAssignments.Clear();
        }
        foreach (var actor in vassago) crystalTowerVassago[actor.GameObjectId] = actor;
        if (CrystalTowerMechanicPolicy.CanAttackBomb(snapshot.LocalActor?.Dead == false, true,
                crystalTowerVassago.Values.Select(actor => new CrystalTowerActorState(actor.GameObjectId,
                    actor.Dead.HasValue ? !actor.Dead.Value : null)).ToArray()))
            return TryCrystalTowerTarget(context, snapshot, bomb, observation);

        var add = snapshot.Actors.Where(actor => actor.DataId is 0x968 or 0x969 && actor.Dead == false && actor.Targetable == true)
            .OrderBy(actor => actor.DataId == 0x969 ? 0 : 1)
            .ThenBy(actor => Vector3.DistanceSquared(actor.Position, snapshot.LocalActor!.Position)).FirstOrDefault();
        if (add is not null) return TryCrystalTowerTarget(context, snapshot, add, observation);
        if (crystalTowerVassagoAssignments.Count == 0 && vassago.Length == 3)
        {
            // Guide convention: A west/left, B north, C east/right. Freeze observed actor identities.
            var north = vassago.MinBy(actor => actor.Position.Z)!;
            var west = vassago.MinBy(actor => actor.Position.X)!;
            var east = vassago.MaxBy(actor => actor.Position.X)!;
            if (new[] { north.GameObjectId, west.GameObjectId, east.GameObjectId }.Distinct().Count() == 3
                && north.Position.Z < -165.6f && west.Position.X < -110f && east.Position.X > -110f)
            {
                crystalTowerVassagoAssignments[CrystalTowerAlliance.A] = west.GameObjectId;
                crystalTowerVassagoAssignments[CrystalTowerAlliance.B] = north.GameObjectId;
                crystalTowerVassagoAssignments[CrystalTowerAlliance.C] = east.GameObjectId;
            }
        }
        var alliance = Enum.Parse<CrystalTowerAlliance>(context.Alliance!);
        var selected = crystalTowerVassagoAssignments.TryGetValue(alliance, out var id)
            ? vassago.SingleOrDefault(actor => actor.GameObjectId == id && actor.Dead == false && actor.Targetable == true) : null;
        return selected is not null ? TryCrystalTowerTarget(context, snapshot, selected, observation)
            : HoldCrystalTower("Crystal Tower: assigned target is not eligible.");
    }
}
