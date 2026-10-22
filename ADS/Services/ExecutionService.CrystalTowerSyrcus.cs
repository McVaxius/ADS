using System.Numerics;
using ADS.Models;
using Dalamud.Game.ClientState.Objects.Enums;

namespace ADS.Services;

public sealed partial class ExecutionService
{
    // Native identities and geometry: FFXIV-CombatReborn/BossmodReborn
    // ae64e3309905dafad0952dbe72680add167591f9,
    // BossMod/Modules/RealmReborn/Alliance/{A21Scylla,A23Amon,A24Xande}/*Enums.cs,
    // A23Amon/A23Amon.cs, A24Xande/A24Xande.cs and BossMod/Components/LineOfSightAOE.cs.
    // Strategy semantics: https://ffxiv.consolegameswiki.com/wiki/Syrcus_Tower
    // The modules are WIP. Their IDs do not establish the missing orb/pillar,
    // outer-platform, toad-action or gravity-field interaction contracts.
    internal const uint SyrcusScylla = 0xBC1;
    // Wright/boss: BMR A22Glasya enums. Squire/rook: Sapphire aab9cb2
    // l1r2.json LVD_mboss_02, name rows2812/2810; 0xBD8 is Moonring Blade.
    internal const uint SyrcusGlasya = 0xBD7;
    internal const uint SyrcusClockworkWright = 0xBD9;
    internal const uint SyrcusClockworkSquire = 0xBDA;
    internal const uint SyrcusSpannerRook = 0xC06;
    internal const uint SyrcusAmon = 0xBE0;
    internal const uint SyrcusIceCage = 0xBE1;
    internal const uint SyrcusCurtainCall = 2441;
    internal const uint SyrcusXande = 0xBDB;
    internal const uint SyrcusStonefallCircle = 0xBDD;
    internal const uint SyrcusStarfallCircle = 0xBDC;
    private const uint SyrcusAncientFlare = 2317;
    private const uint SyrcusAncientQuaga = 2361;
    private const uint SyrcusFireResistanceUp = 520;
    private const uint SyrcusLevitation = 12;
    private const uint SyrcusFireToad = 511;
    private const uint SyrcusDeepFreeze = 487;
    private const float SyrcusCurtainRange = 60;
    private static readonly Vector2 SyrcusAmonCenter = new(0, -200);
    private static readonly Vector2 SyrcusXandeCenter = new(-400, -200);

    private ulong crystalTowerCurtainBoss;
    private bool crystalTowerCurtainActive;
    private ulong crystalTowerCurtainCover;

    // Called by the common Crystal Tower owner reset. There is no encounter-clear
    // counter or completion inferred from actor absence, HP or a movement request.
    private void ResetCrystalTowerSyrcus()
    {
        crystalTowerCurtainBoss = 0;
        crystalTowerCurtainActive = false;
        crystalTowerCurtainCover = 0;
    }

    private bool TryHandleCrystalTowerSyrcus(DutyContextSnapshot context, CrystalTowerObservation snapshot,
        ObservationSnapshot observation)
    {
        if ((context.TerritoryTypeId, context.ContentFinderConditionId) != (372, 102)) return false;
        if (!snapshot.Valid || snapshot.LocalActor is not { Dead: false } local
            || !CrystalTowerOwnerStillCurrent(context, snapshot))
            return HoldCrystalTower("Crystal Tower: waiting for verified alliance mechanic state.");

        // Repeated singleton identities are ambiguity, not a choice of a convenient actor.
        if (snapshot.Actors.Where(actor => actor.DataId is SyrcusAmon or SyrcusXande or SyrcusScylla or SyrcusGlasya)
            .GroupBy(actor => actor.DataId).Any(group => group.Count() > 1))
            return HoldCrystalTower("Crystal Tower: waiting for verified alliance mechanic state.");

        if (TryHandleCrystalTowerCurtain(context, snapshot)) return true;

        var glasya = SingleCrystalTowerActor(snapshot, SyrcusGlasya);
        if (context.InCombat && InCrystalTowerGlasyaMainArena(local.Position))
        {
            var fettered = local.StatusIds?.Contains(510u) == true;
            var add = glasya is null ? null : SelectCrystalTowerGlasyaAdd(snapshot, glasya);
            if (add is not null)
                return TryCrystalTowerTarget(context, snapshot, add, observation, keepPosition: fettered);
            // Fetters disables actions, not movement. Keep an already intercepted
            // Wright tether in place; no node/pad activation is inferred from it.
            if (fettered && glasya?.Dead != true)
                return HoldCrystalTowerSyrcusPosition(context, snapshot, local.GameObjectId);
        }

        var scylla = SingleCrystalTowerActor(snapshot, SyrcusScylla);
        if (scylla is { Dead: false, IsCasting: true, CastActionId: SyrcusAncientFlare }
            && Vector3.DistanceSquared(local.Position, scylla.Position) <= 81 * 81)
        {
            // The status is a native protective-state readback. It is not a water
            // field identity and does not authorize guessing a puddle destination.
            return local.StatusIds?.Contains(SyrcusFireResistanceUp) == true
                ? HoldCrystalTowerSyrcusPosition(context, snapshot, scylla.GameObjectId)
                : HoldCrystalTower("Crystal Tower: waiting for verified alliance mechanic state.");
        }

        var xande = SingleCrystalTowerActor(snapshot, SyrcusXande);
        var inXandeArena = Vector2.DistanceSquared(new(local.Position.X, local.Position.Z), SyrcusXandeCenter) <= 35 * 35;
        if (inXandeArena && xande is { Dead: false, IsCasting: true, CastActionId: SyrcusAncientQuaga })
        {
            // Preserve an observed Levitation position through the actual cast.
            // Missing status/field knowledge remains a hold; no field OID is invented.
            return local.StatusIds?.Contains(SyrcusLevitation) == true
                ? HoldCrystalTowerSyrcusPosition(context, snapshot, xande.GameObjectId)
                : HoldCrystalTower("Crystal Tower: waiting for verified alliance mechanic state.");
        }
        if (inXandeArena)
        {
            var meteor = SelectCrystalTowerXandeMeteor(snapshot);
            if (meteor is not null) return TryCrystalTowerTarget(context, snapshot, meteor, observation);
        }

        var amon = SingleCrystalTowerActor(snapshot, SyrcusAmon);
        if (context.InCombat && amon is { Dead: false }
            && Vector3.DistanceSquared(local.Position, amon.Position) <= SyrcusCurtainRange * SyrcusCurtainRange)
        {
            // No transformed ability input is implemented. In particular, this path
            // never uses Flame Breath against the ice needed for Curtain Call.
            if (local.StatusIds is null || local.StatusIds.Contains(SyrcusFireToad) || local.StatusIds.Contains(SyrcusDeepFreeze))
                return HoldCrystalTower("Crystal Tower: waiting for verified alliance mechanic state.");
            var add = SelectCrystalTowerAmonAdd(snapshot, amon);
            if (add is not null) return TryCrystalTowerTarget(context, snapshot, add, observation);
        }
        return false;
    }

    private bool TryHandleCrystalTowerCurtain(DutyContextSnapshot context, CrystalTowerObservation snapshot)
    {
        var boss = SingleCrystalTowerActor(snapshot, SyrcusAmon);
        if (boss is null)
            return crystalTowerCurtainActive && HoldCrystalTower("Crystal Tower: waiting for verified alliance mechanic state.");
        if (crystalTowerCurtainBoss != boss.GameObjectId)
        {
            if (crystalTowerCurtainActive)
            {
                StopMovementAssists();
                ReleaseCrystalTowerMovement(context, restore: true);
            }
            ResetCrystalTowerSyrcus();
            crystalTowerCurtainBoss = boss.GameObjectId;
        }
        if (boss.Dead == true)
        {
            var wasActive = crystalTowerCurtainActive;
            ResetCrystalTowerSyrcus();
            if (wasActive)
            {
                StopMovementAssists();
                ReleaseCrystalTowerMovement(context, restore: true);
            }
            return false;
        }
        if (boss.Dead != false)
            return crystalTowerCurtainActive && HoldCrystalTower("Crystal Tower: waiting for verified alliance mechanic state.");

        if (boss.IsCasting == true && boss.CastActionId == SyrcusCurtainCall)
        {
            if (Vector3.DistanceSquared(snapshot.LocalActor!.Position, boss.Position) > SyrcusCurtainRange * SyrcusCurtainRange)
                return crystalTowerCurtainActive && HoldCrystalTower("Crystal Tower: waiting for verified alliance mechanic state.");
            crystalTowerCurtainActive = true;
        }
        else if (crystalTowerCurtainActive)
        {
            // Pinned CastLineOfSightAOE removes cover on the native cast-finished
            // transition. An explicit non-casting/other-cast sample ends only our cast hold;
            // an unavailable cast read or missing boss does not. No clear is credited.
            if (boss.IsCasting != false && !(boss.IsCasting == true && boss.CastActionId is > 0 and not SyrcusCurtainCall))
                return HoldCrystalTower("Crystal Tower: waiting for verified alliance mechanic state.");
            ResetCrystalTowerSyrcus();
            StopMovementAssists();
            ReleaseCrystalTowerMovement(context, restore: true);
            return false;
        }
        if (!crystalTowerCurtainActive) return false;
        if (snapshot.LocalActor?.StatusIds is not { } localStatuses || localStatuses.Contains(SyrcusDeepFreeze))
            return HoldCrystalTower("Crystal Tower: waiting for verified alliance mechanic state.");
        var cover = SelectCrystalTowerCurtainCover(snapshot, boss, crystalTowerCurtainCover);
        if (cover is null)
            return HoldCrystalTower("Crystal Tower: waiting for verified alliance mechanic state.");
        crystalTowerCurtainCover = cover.Value.CageId;
        return TryCrystalTowerMove(context, snapshot, cover.Value.CageId, cover.Value.DestinationXZ, cover.Value.ArrivalRadius);
    }

    private bool HoldCrystalTowerSyrcusPosition(DutyContextSnapshot context, CrystalTowerObservation snapshot, ulong actorId)
    {
        var position = snapshot.LocalActor!.Position;
        return TryCrystalTowerMove(context, snapshot, actorId, new(position.X, position.Z), 0);
    }

    internal readonly record struct CrystalTowerIceCover(ulong CageId, Vector2 DestinationXZ, float ArrivalRadius = 0.2f);

    internal static CrystalTowerIceCover? SelectCrystalTowerCurtainCover(CrystalTowerObservation snapshot,
        CrystalTowerActorObservation boss, ulong preferredCageId = 0)
    {
        if (!snapshot.Valid || snapshot.LocalActor is not { Dead: false } local || boss is not { DataId: SyrcusAmon, Dead: false }
            || !IsFiniteCrystalTowerPosition(local.Position) || !IsFiniteCrystalTowerPosition(boss.Position)
            || boss.HitboxRadius is not { } bossRadius || !float.IsFinite(bossRadius) || bossRadius <= 0)
            return null;
        var origin = new Vector2(boss.Position.X, boss.Position.Z);
        var player = new Vector2(local.Position.X, local.Position.Z);
        var choices = new List<CrystalTowerIceCover>();
        foreach (var cage in snapshot.Actors.Where(actor => actor.DataId == SyrcusIceCage))
        {
            if (cage is not { Dead: false, ObjectKind: ObjectKind.BattleNpc, HitboxRadius: { } radius }
                || !IsFiniteCrystalTowerPosition(cage.Position) || !float.IsFinite(radius) || radius <= 0
                || !IsCrystalTowerObjectId(cage.GameObjectId)
                || snapshot.Actors.Count(actor => actor.GameObjectId == cage.GameObjectId) != 1)
                continue;
            var center = new Vector2(cage.Position.X, cage.Position.Z);
            var offset = center - origin;
            var distance = offset.Length();
            // Strategy guide: ice overlapping Amon's body is not usable cover.
            // Reject native hitbox overlap rather than guessing a minimum distance.
            if (distance <= bossRadius + radius) continue;
            var alreadyCovered = IsCrystalTowerCurtainCovered(origin, center, radius, player);
            var destination = alreadyCovered ? player : center + offset / distance * (radius + 0.5f);
            // A generic arrival tolerance must not be wider than a small native
            // ice shadow. Existing protection is held exactly where observed.
            var arrivalRadius = alreadyCovered ? 0 : Math.Min(0.2f, radius * 0.25f);
            // BMR's authored Amon arena is XZ (0,-200), radius 30. Candidate
            // height is deliberately absent: the shared mover retains live Y.
            if (!float.IsFinite(destination.X) || !float.IsFinite(destination.Y)
                || Vector2.Distance(destination, SyrcusAmonCenter) + arrivalRadius > 30
                || !IsCrystalTowerCurtainCovered(origin, center, radius, destination))
                continue;
            choices.Add(new(cage.GameObjectId, destination, arrivalRadius));
        }
        // Any already verified protection takes precedence over a retained cage;
        // do not walk out of one shadow merely to reach the earlier selection.
        return choices.OrderBy(choice => choice.DestinationXZ == player ? 0 : 1)
            .ThenBy(choice => choice.CageId == preferredCageId ? 0 : 1)
            .ThenBy(choice => Vector2.DistanceSquared(player, choice.DestinationXZ))
            .ThenBy(choice => choice.CageId).Select(choice => (CrystalTowerIceCover?)choice).FirstOrDefault();
    }

    internal static bool IsCrystalTowerCurtainCovered(Vector2 caster, Vector2 cage, float radius, Vector2 player)
    {
        if (!float.IsFinite(caster.X) || !float.IsFinite(caster.Y) || !float.IsFinite(cage.X) || !float.IsFinite(cage.Y)
            || !float.IsFinite(player.X) || !float.IsFinite(player.Y) || !float.IsFinite(radius) || radius <= 0)
            return false;
        var obstacle = cage - caster;
        var position = player - caster;
        var distance = obstacle.Length();
        var playerDistance = position.Length();
        if (distance <= radius || playerDistance <= distance + 0.2f || playerDistance > SyrcusCurtainRange
            || Vector2.Dot(obstacle, position) <= 0)
            return false;
        var cross = obstacle.X * position.Y - obstacle.Y * position.X;
        // Equivalent to the pinned asin(radius / distance) visibility cone.
        return cross * cross <= radius * radius * position.LengthSquared();
    }

    internal static CrystalTowerActorObservation? SelectCrystalTowerXandeMeteor(CrystalTowerObservation snapshot)
        => !snapshot.Valid || snapshot.LocalActor is not { Dead: false } local || !IsFiniteCrystalTowerPosition(local.Position) ? null
            : snapshot.Actors.Where(actor => actor.DataId is SyrcusStonefallCircle or SyrcusStarfallCircle
                    && actor.ObjectKind == ObjectKind.BattleNpc && actor.Dead == false && actor.Targetable == true
                    && IsCrystalTowerObjectId(actor.GameObjectId) && IsCrystalTowerEntityId(actor.EntityId)
                    && IsFiniteCrystalTowerPosition(actor.Position)
                    && snapshot.Actors.Count(other => other.GameObjectId == actor.GameObjectId) == 1)
                // Native target identity is known. No OID-name inference is used
                // to fabricate A/B/C meteor assignments or an intermission clear.
                .OrderBy(actor => Vector3.DistanceSquared(local.Position, actor.Position))
                .ThenBy(actor => actor.GameObjectId).FirstOrDefault();

    internal static CrystalTowerActorObservation? SelectCrystalTowerAmonAdd(CrystalTowerObservation snapshot,
        CrystalTowerActorObservation boss)
        => !snapshot.Valid || snapshot.LocalActor is not { Dead: false } local || !IsFiniteCrystalTowerPosition(local.Position)
            || boss is not { DataId: SyrcusAmon, Dead: false } ? null
            : snapshot.Actors.Where(actor => actor.DataId is 0xBE3 or 0xBE4 or 0xBE6 or 0xBE7
                    && actor.ObjectKind == ObjectKind.BattleNpc && actor.Dead == false && actor.Targetable == true
                    && IsCrystalTowerObjectId(actor.GameObjectId) && IsCrystalTowerEntityId(actor.EntityId)
                    && IsFiniteCrystalTowerPosition(actor.Position)
                    && Vector3.DistanceSquared(boss.Position, actor.Position) <= SyrcusCurtainRange * SyrcusCurtainRange
                    && snapshot.Actors.Count(other => other.GameObjectId == actor.GameObjectId) == 1)
                .OrderBy(actor => Vector3.DistanceSquared(local.Position, actor.Position))
                .ThenBy(actor => actor.GameObjectId).FirstOrDefault();

    private static bool InCrystalTowerGlasyaMainArena(Vector3 position)
        // BMR ae64e330: center(0,-200), radius35. Sapphire aab9cb2
        // LVD_mboss_02 places this floor at Y0, distinct from Amon's Y600.
        => IsFiniteCrystalTowerPosition(position) && MathF.Abs(position.Y) <= 3
            && Vector2.DistanceSquared(new(position.X, position.Z), new(0, -200)) <= 35 * 35;

    internal static CrystalTowerActorObservation? SelectCrystalTowerGlasyaAdd(CrystalTowerObservation snapshot,
        CrystalTowerActorObservation boss)
        => !snapshot.Valid || snapshot.LocalActor is not { Dead: false } local
            || !InCrystalTowerGlasyaMainArena(local.Position)
            || boss is not { DataId: SyrcusGlasya, ObjectKind: ObjectKind.BattleNpc, Dead: false }
            || !InCrystalTowerGlasyaMainArena(boss.Position)
            || snapshot.Actors.Count(actor => actor.DataId == SyrcusGlasya) != 1 ? null
            : snapshot.Actors.Where(actor => actor.DataId is SyrcusClockworkWright or SyrcusClockworkSquire or SyrcusSpannerRook
                    && actor.ObjectKind == ObjectKind.BattleNpc && actor.Dead == false && actor.Targetable == true
                    && IsCrystalTowerObjectId(actor.GameObjectId) && IsCrystalTowerEntityId(actor.EntityId)
                    && InCrystalTowerGlasyaMainArena(actor.Position)
                    && snapshot.Actors.Count(other => other.GameObjectId == actor.GameObjectId) == 1
                    && snapshot.Actors.Count(other => other.EntityId == actor.EntityId) == 1)
                .OrderBy(actor => Vector3.DistanceSquared(local.Position, actor.Position))
                .ThenBy(actor => actor.GameObjectId).FirstOrDefault();
}
