using System.Numerics;
using ADS.Models;
using Dalamud.Game.ClientState.Objects.Enums;

namespace ADS.Services;

public sealed partial class ExecutionService
{
    // ffxivcode/AutoDuty 53f4e740, AutoDuty/Paths/(151) The World of Darkness.json.
    // These are complete world XYZ approaches, not the XZ-only local mechanic mover.
    private enum CrystalTowerRouteStage { Garm, Angra, Queen, FiveHeaded, Atomos, Cerberus, Transfer, Cloud }
    private CrystalTowerRouteStage? crystalTowerRouteStage;
    private int crystalTowerRouteWaypoint;
    private CrystalTowerActorObservation? crystalTowerRouteBoss;
    private CrystalTowerRouteStage? crystalTowerRouteLastClear;
    private CrystalTowerActorObservation? crystalTowerRouteLastClearBoss;
    private const float CrystalTowerRouteArrival = 2.5f;
    private static readonly Vector3 CrystalTowerCloudLanding = new(-321.26f, 280.05f, -404.83f);

    private static readonly Vector3[][] CrystalTowerRouteApproaches =
    [
        [new(-58.02f, 16, 403.41f)], // lines 5-23
        [new(-146.87f, 33.72f, 330.04f), new(-147.1f, 34, 323.95f)], // 31-49
        [new(97.11f, 63.94f, 265.26f), new(109.61f, 63.99f, 264.87f)], // 57-75
        [new(200.18f, 81.91f, 211.35f), new(200.22f, 82.01f, 204.99f)], // 83-101
        [new(-0.17f, 95.2f, 96.99f)], // 109-114
        [new(-0.01f, 129.09f, -150.9f), new(0.18f, 129.3f, -162.62f)], // 122-140
        [new(0.25f, 142.01f, -311.52f), new(0.21f, 143.65f, -320.49f)], // 148-166
        [], // 174-179 is a remote destination, not a navigable transfer contract.
    ];

    private void ResetCrystalTowerRoute()
    {
        crystalTowerRouteStage = crystalTowerRouteLastClear = null;
        crystalTowerRouteBoss = crystalTowerRouteLastClearBoss = null;
        crystalTowerRouteWaypoint = 0;
    }

    // Call after owner admission and before mechanics, so an early-returning mechanic
    // still observes entry. No movement, completion or synthetic catch-up happens here.
    private void ObserveCrystalTowerRoute(DutyContextSnapshot context, CrystalTowerObservation snapshot)
    {
        if (!CrystalTowerRouteContext(context, snapshot)) return;
        if (crystalTowerRouteStage is null)
        {
            foreach (var stage in Enum.GetValues<CrystalTowerRouteStage>())
            {
                if (stage == CrystalTowerRouteStage.Transfer || !InCrystalTowerRouteRoom(snapshot, stage)
                    || !snapshot.Actors.Any(actor => actor.DataId == CrystalTowerRouteBossId(stage))) continue;
                crystalTowerRouteStage = stage;
                // Cold admission starts in a positively observed room, rather than
                // walking backwards to its approach. Active waypoints are never skipped.
                // An ambiguous/invalid candidate retains the room's hold; only a
                // separately validated actor can authorize combat or a clear.
                crystalTowerRouteWaypoint = CrystalTowerRouteApproaches[(int)stage].Length;
                break;
            }
            // The first authored approach bounds entrance admission. An empty
            // object table is not an encounter-clear receipt or permission to catch up.
            if (crystalTowerRouteStage is null && !context.InCombat
                && MathF.Abs(snapshot.LocalActor!.Position.Y - 16) <= 3
                && Vector3.Distance(snapshot.LocalActor.Position, CrystalTowerRouteApproaches[0][0]) <= 45)
            {
                crystalTowerRouteStage = CrystalTowerRouteStage.Garm;
                crystalTowerRouteWaypoint = 0;
            }
        }
        if (crystalTowerRouteStage is not { } current) return;

        // A new attempt in the room we just left invalidates its previous receipt.
        // Distant loaded actors cannot rewind the route.
        if (crystalTowerRouteLastClear is { } prior && InCrystalTowerRouteRoom(snapshot, prior))
        {
            var invalidated = prior switch
            {
                CrystalTowerRouteStage.Garm => !CrystalTowerGarmWaveComplete,
                CrystalTowerRouteStage.Queen => !CrystalTowerQueenWaveComplete,
                CrystalTowerRouteStage.Atomos => worldOfDarknessAtomos.Count != 3
                    || worldOfDarknessAtomos.Values.Any(actor => actor.Dead != true),
                _ => snapshot.Actors.Any(actor => actor.DataId == CrystalTowerRouteBossId(prior))
                    && (CrystalTowerRouteSingleton(snapshot, prior) is not { } actor || actor.Dead != true
                        || crystalTowerRouteLastClearBoss is not { } receipt
                        || actor.GameObjectId != receipt.GameObjectId || actor.EntityId != receipt.EntityId),
            };
            if (invalidated)
            {
                crystalTowerRouteStage = current = prior;
                crystalTowerRouteWaypoint = CrystalTowerRouteApproaches[(int)prior].Length;
                crystalTowerRouteLastClear = null;
                crystalTowerRouteLastClearBoss = null;
            }
        }
        crystalTowerRouteBoss = CrystalTowerRouteSingleton(snapshot, current);
    }

    // Call after the encounter handlers. They retain target/interaction/mechanic priority.
    private bool TryHandleCrystalTowerRoute(DutyContextSnapshot context, CrystalTowerObservation snapshot)
    {
        if (!CrystalTowerRouteContext(context, snapshot) || crystalTowerRouteStage is not { } stage)
            return false;
        if (completedDuty == (151u, 111u)) return false;

        var approach = CrystalTowerRouteApproaches[(int)stage];
        if (crystalTowerRouteWaypoint < approach.Length)
        {
            var destination = approach[crystalTowerRouteWaypoint];
            if (Vector3.Distance(snapshot.LocalActor!.Position, destination) <= CrystalTowerRouteArrival)
            {
                // Only this fresh, owner-checked 3D position advances a waypoint.
                StopMovementAssists();
                ++crystalTowerRouteWaypoint;
                return HoldCrystalTower("Crystal Tower: waiting for verified alliance mechanic state.");
            }
            if (!TryAcquireCrystalTowerMovement(context, (uint)snapshot.LocalId, GetCrystalTowerStrategy(context), out _))
                return HoldCrystalTower("Crystal Tower: waiting for the movement provider.");
            if (!CrystalTowerOwnerStillCurrent(context, snapshot))
                return HoldCrystalTower("Crystal Tower: waiting for verified alliance mechanic state.");
            var navigationId = 0x4354520000000000ul + ((ulong)stage << 16) + (uint)crystalTowerRouteWaypoint;
            if (!TryBeginNavigation(navigationId, destination))
                return HoldCrystalTower("Crystal Tower: waiting for the movement provider.");
            SetPhase(ExecutionPhase.NavigatingToXyzDestination, "Crystal Tower: following the verified raid route.");
            return true;
        }

        if (stage == CrystalTowerRouteStage.Transfer)
        {
            // No portal ID/interaction/readback contract is established. Never ask
            // navmesh to walk from the Y143 departure to the remote Y280 Cloud room.
            if (Vector3.Distance(snapshot.LocalActor!.Position, CrystalTowerCloudLanding) <= CrystalTowerRouteArrival
                && InCrystalTowerRouteRoom(snapshot, CrystalTowerRouteStage.Cloud)
                && HasCrystalTowerRouteActor(snapshot, CrystalTowerRouteStage.Cloud))
            {
                crystalTowerRouteStage = CrystalTowerRouteStage.Cloud;
                crystalTowerRouteWaypoint = 0;
                crystalTowerRouteBoss = CrystalTowerRouteSingleton(snapshot, CrystalTowerRouteStage.Cloud);
            }
            return HoldCrystalTower("Crystal Tower: waiting for verified alliance mechanic state.");
        }
        if (stage == CrystalTowerRouteStage.Cloud)
        {
            // Only ADS's existing typed DutyCompleted receipt finishes the duty.
            return crystalTowerRouteBoss is { Dead: false, Targetable: true } ? false
                : HoldCrystalTower("Crystal Tower: waiting for verified alliance mechanic state.");
        }

        var complete = stage switch
        {
            CrystalTowerRouteStage.Garm => CrystalTowerGarmWaveComplete,
            CrystalTowerRouteStage.Queen => CrystalTowerQueenWaveComplete,
            CrystalTowerRouteStage.Atomos => worldOfDarknessAtomos.Count == 3
                && worldOfDarknessAtomos.Values.All(actor => actor.Dead == true),
            _ => crystalTowerRouteBoss is { Dead: true } boss
                && CrystalTowerRouteSingleton(snapshot, stage) is { } fresh
                && fresh.GameObjectId == boss.GameObjectId && fresh.EntityId == boss.EntityId && fresh.Dead == true,
        };
        if (!complete)
        {
            // A known live boss yields to ordinary combat. Absence/unknown/ambiguity
            // holds this encounter, rather than allowing generic frontier progression.
            return crystalTowerRouteBoss is { Dead: false, Targetable: true } ? false
                : HoldCrystalTower("Crystal Tower: waiting for verified alliance mechanic state.");
        }
        crystalTowerRouteLastClear = stage;
        crystalTowerRouteLastClearBoss = crystalTowerRouteBoss;
        crystalTowerRouteStage = stage + 1;
        crystalTowerRouteWaypoint = 0;
        crystalTowerRouteBoss = null;
        return HoldCrystalTower("Crystal Tower: waiting for verified alliance mechanic state.");
    }

    private bool CrystalTowerRouteContext(DutyContextSnapshot context, CrystalTowerObservation snapshot)
        => (context.TerritoryTypeId, context.ContentFinderConditionId) == (151, 111)
            && !context.IsUnsafeTransition && !context.IsCutscene && snapshot.Valid
            && snapshot.LocalActor is { Dead: false } local && IsFiniteCrystalTowerPosition(local.Position)
            && CrystalTowerOwnerStillCurrent(context, snapshot);

    private static uint CrystalTowerRouteBossId(CrystalTowerRouteStage stage) => stage switch
    {
        CrystalTowerRouteStage.Garm => 0xD9C, CrystalTowerRouteStage.Angra => 0xE00,
        CrystalTowerRouteStage.Queen => 0xDA0, CrystalTowerRouteStage.FiveHeaded => 0xDFC,
        CrystalTowerRouteStage.Atomos => 0xDA4, CrystalTowerRouteStage.Cerberus => 0xDF6,
        CrystalTowerRouteStage.Cloud => 0xCFA, _ => 0,
    };

    private static bool InCrystalTowerRouteRoom(CrystalTowerObservation snapshot, CrystalTowerRouteStage stage)
    {
        if (snapshot.LocalActor is not { } local) return false;
        if (stage == CrystalTowerRouteStage.Cerberus)
            return MathF.Abs(local.Position.Y - 129.3f) <= 3 && InWorldOfDarknessCerberusRoom(local.Position);
        var (center, floor, radius) = stage switch
        {
            CrystalTowerRouteStage.Garm => (new Vector2(-77, 383), 16f, 30f),
            CrystalTowerRouteStage.Angra => (new Vector2(-147, 297), 34f, 30f),
            CrystalTowerRouteStage.Queen => (new Vector2(130, 265), 63.99f, 30f),
            CrystalTowerRouteStage.FiveHeaded => (new Vector2(200, 179), 82.01f, 30f),
            CrystalTowerRouteStage.Atomos => (new Vector2(0, 31), 95.2f, 66.6f),
            CrystalTowerRouteStage.Cloud => (new Vector2(-300, -400), 280.05f, 30f),
            _ => (Vector2.Zero, float.NaN, 0f),
        };
        return MathF.Abs(local.Position.Y - floor) <= 3
            && Vector2.DistanceSquared(new(local.Position.X, local.Position.Z), center) <= radius * radius;
    }

    private static bool HasCrystalTowerRouteActor(CrystalTowerObservation snapshot, CrystalTowerRouteStage stage)
        => stage is CrystalTowerRouteStage.Garm or CrystalTowerRouteStage.Atomos
            ? snapshot.Actors.Any(actor => actor.DataId == CrystalTowerRouteBossId(stage)
                && ValidCrystalTowerRouteActor(snapshot, actor)
                && (stage == CrystalTowerRouteStage.Atomos
                    ? WorldOfDarknessAtomosLane(actor) != CrystalTowerAlliance.Unknown
                    : InCrystalTowerRouteRoom(snapshot with { LocalActor = actor }, stage)))
            : CrystalTowerRouteSingleton(snapshot, stage) is not null;

    private static CrystalTowerActorObservation? CrystalTowerRouteSingleton(CrystalTowerObservation snapshot,
        CrystalTowerRouteStage stage)
    {
        if (stage is CrystalTowerRouteStage.Garm or CrystalTowerRouteStage.Atomos
            or CrystalTowerRouteStage.Transfer || !InCrystalTowerRouteRoom(snapshot, stage)) return null;
        var seen = snapshot.Actors.Where(actor => actor.DataId == CrystalTowerRouteBossId(stage)).Take(2).ToArray();
        return seen is [var actor] && ValidCrystalTowerRouteActor(snapshot, actor)
            && InCrystalTowerRouteRoom(snapshot with { LocalActor = actor }, stage) ? actor : null;
    }

    private static bool ValidCrystalTowerRouteActor(CrystalTowerObservation snapshot, CrystalTowerActorObservation actor)
        => actor.ObjectKind == ObjectKind.BattleNpc && IsCrystalTowerObjectId(actor.GameObjectId)
            && IsCrystalTowerEntityId(actor.EntityId) && IsFiniteCrystalTowerPosition(actor.Position)
            && snapshot.LocalActor is { } local && MathF.Abs(actor.Position.Y - local.Position.Y) <= 12
            && Vector3.DistanceSquared(actor.Position, local.Position) <= 80 * 80
            && snapshot.Actors.Count(other => other.GameObjectId == actor.GameObjectId) == 1
            && snapshot.Actors.Count(other => other.EntityId == actor.EntityId) == 1;
}
