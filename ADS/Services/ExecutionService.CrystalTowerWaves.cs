using System.Numerics;
using ADS.Models;
using Dalamud.Game.ClientState.Objects.Enums;

namespace ADS.Services;

public sealed partial class ExecutionService
{
    // awgil/ffxiv_bossmod d409d336edb909b71b25fa8761f88667f5bdce1b:
    // A31Garm/A31Garm.cs:58-65 and A33QueenScylla/A33QueenScylla.cs:85-103.
    // ADS has no destroyed-actor observation. Retain each admitted identity until an
    // explicit dead read; disappearance is never the upstream IsDeadOrDestroyed predicate.
    private readonly Dictionary<ulong, CrystalTowerActorObservation> crystalTowerGarmWave = [];
    private readonly Dictionary<ulong, CrystalTowerActorObservation> crystalTowerQueenWave = [];
    private bool crystalTowerGarmWaveAdmitted;
    private bool crystalTowerQueenWaveAdmitted;

    internal bool CrystalTowerGarmWaveComplete => crystalTowerGarmWaveAdmitted
        && crystalTowerGarmWave.Values.Count(actor => actor.DataId == 0xD9C) == 3
        && crystalTowerGarmWave.Values.Any(actor => actor.DataId != 0xD9C)
        && crystalTowerGarmWave.Values.All(actor => actor.Dead == true);

    internal bool CrystalTowerQueenWaveComplete
    {
        get
        {
            if (!crystalTowerQueenWaveAdmitted) return false;
            var bosses = crystalTowerQueenWave.Values.Where(actor => actor.DataId == 0xDA0).ToArray();
            var clones = crystalTowerQueenWave.Values.Where(actor => actor.DataId == 0xDA3).ToArray();
            var gates = crystalTowerQueenWave.Values.Where(actor => actor.DataId is 0xE73 or 0xE74).ToArray();
            var adds = crystalTowerQueenWave.Values.Where(actor => actor.DataId is 0xDA1 or 0xDA2).ToArray();
            var gatesCleared = gates.Length > 0 && gates.All(actor => actor.Dead == true);
            var addsCleared = adds.Length > 0 && adds.All(actor => actor.Dead == true);
            return bosses is [{ Dead: true }] && clones.Length > 0 && clones.All(actor => actor.Dead == true)
                && (gatesCleared || addsCleared) && (adds.Length == 0 || addsCleared);
        }
    }

    private void ResetCrystalTowerWaves()
    {
        crystalTowerGarmWave?.Clear();
        crystalTowerQueenWave?.Clear();
        crystalTowerGarmWaveAdmitted = crystalTowerQueenWaveAdmitted = false;
    }

    private bool TryHandleCrystalTowerWaves(DutyContextSnapshot context, CrystalTowerObservation snapshot,
        ObservationSnapshot observation)
    {
        if ((context.TerritoryTypeId, context.ContentFinderConditionId) != (151, 111)) return false;
        if (!snapshot.Valid || snapshot.LocalActor is not { Dead: false } local
            || !IsFiniteCrystalTowerPosition(local.Position) || !CrystalTowerOwnerStillCurrent(context, snapshot))
            return HoldWorldOfDarkness(context, snapshot, "Crystal Tower: waiting for verified alliance mechanic state.");

        // Source-authored 30y arenas admit only their own encounter. Seeing actors
        // in a different room does not let them take over current progression.
        if (TryHandleCrystalTowerGarmWave(context, snapshot, observation)) return true;
        return TryHandleCrystalTowerQueenWave(context, snapshot, observation);
    }

    private bool TryHandleCrystalTowerGarmWave(DutyContextSnapshot context, CrystalTowerObservation snapshot,
        ObservationSnapshot observation)
    {
        if (!crystalTowerGarmWaveAdmitted && !InCrystalTowerArena(snapshot, new(-77, 383), 30)) return false;
        var seen = snapshot.Actors.Where(actor => actor.DataId is 0xD9C or 0xD9D or 0xD9F or 0xD9E).ToArray();
        if (!crystalTowerGarmWaveAdmitted && !seen.Any(actor => actor.DataId == 0xD9C)) return false;
        if (!ValidCrystalTowerWaveActors(seen, snapshot) || seen.Count(actor => actor.DataId == 0xD9C) > 3)
            return HoldWorldOfDarkness(context, snapshot, "Crystal Tower: waiting for verified alliance mechanic state.");

        // A positively observed revival starts a new attempt. Do not carry the
        // previous attempt's dead adds into a fresh set of three Garm.
        if (seen.Any(actor => actor.DataId == 0xD9C && actor.Dead == false
                && crystalTowerGarmWave.TryGetValue(actor.GameObjectId, out var prior) && prior.Dead == true)
            || crystalTowerGarmWave.Values.Count(actor => actor.DataId == 0xD9C) == 3
                && seen.Any(actor => actor.DataId == 0xD9C && !crystalTowerGarmWave.ContainsKey(actor.GameObjectId)))
            crystalTowerGarmWave.Clear();
        if (!CaptureCrystalTowerWaveActors(crystalTowerGarmWave, seen))
            return HoldWorldOfDarkness(context, snapshot, "Crystal Tower: waiting for verified alliance mechanic state.");
        crystalTowerGarmWaveAdmitted = true;

        // The pinned OID declares three Garm. A partial object-table view must
        // not turn All(empty) or one observed corpse into a completed encounter.
        if (CrystalTowerGarmWaveComplete) return false;
        var target = seen.Where(actor => actor.Dead == false && actor.Targetable == true)
            .OrderBy(actor => actor.DataId == 0xD9C ? 1 : 0)
            .ThenBy(actor => Vector3.DistanceSquared(actor.Position, snapshot.LocalActor!.Position))
            .ThenBy(actor => actor.GameObjectId).FirstOrDefault();
        return target is not null ? TargetWorldOfDarkness(context, snapshot, target, observation)
            : HoldWorldOfDarkness(context, snapshot, "Crystal Tower: waiting for verified alliance mechanic state.");
    }

    private bool TryHandleCrystalTowerQueenWave(DutyContextSnapshot context, CrystalTowerObservation snapshot,
        ObservationSnapshot observation)
    {
        if (!crystalTowerQueenWaveAdmitted && !InCrystalTowerArena(snapshot, new(130, 265), 30)) return false;
        var seen = snapshot.Actors.Where(actor => actor.DataId is 0xDA0 or 0xDA3 or 0xE73 or 0xE74 or 0xDA1 or 0xDA2).ToArray();
        if (!crystalTowerQueenWaveAdmitted && !seen.Any(actor => actor.DataId == 0xDA0)) return false;
        if (!ValidCrystalTowerWaveActors(seen, snapshot) || seen.Count(actor => actor.DataId == 0xDA0) > 1)
            return HoldWorldOfDarkness(context, snapshot, "Crystal Tower: waiting for verified alliance mechanic state.");
        var queen = seen.SingleOrDefault(actor => actor.DataId == 0xDA0);
        var priorQueen = crystalTowerQueenWave.Values.SingleOrDefault(actor => actor.DataId == 0xDA0);
        if (queen is not null && priorQueen is not null
            && (queen.GameObjectId != priorQueen.GameObjectId || queen.Dead == false && priorQueen.Dead == true))
            crystalTowerQueenWave.Clear();
        if (!CaptureCrystalTowerWaveActors(crystalTowerQueenWave, seen))
            return HoldWorldOfDarkness(context, snapshot, "Crystal Tower: waiting for verified alliance mechanic state.");
        crystalTowerQueenWaveAdmitted = true;

        var bosses = crystalTowerQueenWave.Values.Where(actor => actor.DataId == 0xDA0).ToArray();
        if (CrystalTowerQueenWaveComplete) return false;

        // Same priority order as the pinned AddsMulti components: gates=2,
        // Thor/Jormungand=1, Queen/Xande=0. Every target is rejoined natively.
        var target = seen.Where(actor => actor.Dead == false && actor.Targetable == true)
            .OrderBy(actor => actor.DataId is 0xE73 or 0xE74 ? 0 : actor.DataId is 0xDA1 or 0xDA2 ? 1 : 2)
            .ThenBy(actor => Vector3.DistanceSquared(actor.Position, snapshot.LocalActor!.Position))
            .ThenBy(actor => actor.GameObjectId).FirstOrDefault();
        if (target is not null) return TargetWorldOfDarkness(context, snapshot, target, observation);
        if (bosses is [{ Dead: true }] && TryHandleCrystalTowerQueenChest(context, snapshot, observation)) return true;
        return HoldWorldOfDarkness(context, snapshot, "Crystal Tower: waiting for verified alliance mechanic state.");
    }

    private static bool ValidCrystalTowerWaveActors(CrystalTowerActorObservation[] actors, CrystalTowerObservation snapshot)
        => actors.All(actor => actor.ObjectKind == ObjectKind.BattleNpc
            && IsCrystalTowerObjectId(actor.GameObjectId) && IsCrystalTowerEntityId(actor.EntityId)
            && IsFiniteCrystalTowerPosition(actor.Position)
            && snapshot.Actors.Count(other => other.GameObjectId == actor.GameObjectId) == 1
            && snapshot.Actors.Count(other => other.EntityId == actor.EntityId && other.ObjectKind == ObjectKind.BattleNpc) == 1);

    private static bool CaptureCrystalTowerWaveActors(Dictionary<ulong, CrystalTowerActorObservation> retained,
        CrystalTowerActorObservation[] seen)
    {
        if (seen.Any(actor => retained.TryGetValue(actor.GameObjectId, out var prior)
                && (prior.EntityId != actor.EntityId || prior.DataId != actor.DataId))) return false;
        foreach (var actor in seen) retained[actor.GameObjectId] = actor;
        return true;
    }

    private bool TryHandleCrystalTowerQueenChest(DutyContextSnapshot context, CrystalTowerObservation snapshot,
        ObservationSnapshot observation)
    {
        // Pinned FalseChest: only the live targetable 1E9709 summons Xande.
        // Reuse ADS's native interaction path; sending it never counts as a clear.
        var chests = snapshot.Actors.Where(actor => actor.DataId == 0x1E9709).ToArray();
        if (chests is not [{ ObjectKind: ObjectKind.EventObj, Targetable: true } chest]
            || !IsCrystalTowerObjectId(chest.GameObjectId) || !IsFiniteCrystalTowerPosition(chest.Position)
            || snapshot.Actors.Count(actor => actor.GameObjectId == chest.GameObjectId) != 1) return false;
        var matches = observation.LiveInteractables.Where(actor => actor.GameObjectId == chest.GameObjectId
            && actor.DataId == chest.DataId && actor.ObjectKind == ObjectKind.EventObj && actor.MapId == context.MapId).Take(2).ToArray();
        if (matches is not [var observed]) return false;
        var native = ResolveGameObject(observed);
        if (native is null || native.GameObjectId != chest.GameObjectId || native.EntityId != chest.EntityId
            || native.BaseId != chest.DataId || native.ObjectKind != ObjectKind.EventObj || !native.IsTargetable) return false;
        if (!TryAcquireCrystalTowerMovement(context, (uint)snapshot.LocalId, GetCrystalTowerStrategy(context), out _))
            return HoldWorldOfDarkness(context, snapshot, "Crystal Tower: waiting for the movement provider.");
        if (!CrystalTowerOwnerStillCurrent(context, snapshot))
            return HoldWorldOfDarkness(context, snapshot, "Crystal Tower: waiting for verified alliance mechanic state.");
        worldOfDarknessMovementOwned = true;
        TryAdvanceInteractableObjective(context, observed, string.Empty);
        return true;
    }
}
