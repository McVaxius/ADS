using System.Numerics;
using ADS.Models;
using Dalamud.Game.ClientState.Objects.Enums;

namespace ADS.Services;

public sealed partial class ExecutionService
{
    // Sapphire aab9cb2347eb0ec42e85ee3e117a0c177b9b3070,
    // data/bnpcs/l1r1/l1r1.json, LVD_bnpc_11: exact placed LayoutId/BaseId pairs.
    // Independently reviewed guide revision1579412 has 8/10/6 enemies in these pools.
    // Layout identity is the SDK d6397a76 GameObject.LayoutId, not an entity ID,
    // species count, proximity match, or synthetic encounter completion event.
    private static readonly (uint Layout, uint Base, int Pack)[] LabyrinthTrashMembership =
    [
        (4494643, 2398, 0), (4494661, 2396, 0), (4494665, 2394, 0), (4494667, 2395, 0),
        (4494671, 2392, 0), (4494679, 2397, 0), (4494681, 2393, 0), (4494693, 2398, 0),
        (4494658, 2399, 1), (4494659, 2399, 1), (4494660, 2399, 1), (4494662, 2399, 1),
        (4494663, 2399, 1), (4494668, 2399, 1), (4494695, 2399, 1), (4494701, 2399, 1),
        (4494686, 2400, 1), (4494692, 2400, 1),
        (4494649, 2402, 2), (4494702, 2401, 2), (4494703, 2401, 2), (4494706, 2401, 2),
        (4534403, 2402, 2), (4534404, 2402, 2),
    ];

    // Full world XYZ from AutoDuty53f4e740's reviewed Labyrinth path.
    private static readonly Vector3[] LabyrinthTrashWaypoints =
    [
        new(-353.5f, 41.6f, 279.17f), new(-371.92f, 42, 273.24f),
        new(-423.9f, 41.6f, 279.38f), new(-441.7f, 42, 218.71f),
        new(-452.03f, 25.53f, 69.85f),
    ];
    private static readonly Vector3 LabyrinthEntrance = new(-110.032799f, 30.37269f, 417.61499f);
    private readonly record struct LabyrinthTrashActorState(CrystalTowerActorObservation Actor, bool SawAlive, bool Dead);
    private readonly Dictionary<uint, LabyrinthTrashActorState> labyrinthTrashActors = [];
    private bool labyrinthTrashActive;
    private bool labyrinthTrashRetired;
    private bool labyrinthTrashUncertain;
    private int labyrinthTrashWaypoint;
    private bool labyrinthTrashArrived;

    private void ResetCrystalTowerLabyrinthTrash()
    {
        labyrinthTrashActors?.Clear();
        labyrinthTrashActive = labyrinthTrashRetired = labyrinthTrashUncertain = labyrinthTrashArrived = false;
        labyrinthTrashWaypoint = 0;
    }

    // Root calls the regular route observer first. A real later-room native join
    // hands execution to that route without inventing clears for unseen trash.
    private void ObserveCrystalTowerLabyrinthTrash(DutyContextSnapshot context, CrystalTowerObservation snapshot)
    {
        if (!LabyrinthRouteContext(context, snapshot)) return;
        if (labyrinthRouteStage is not null)
        {
            labyrinthTrashActive = false;
            labyrinthTrashRetired = true;
            return;
        }
        if (labyrinthTrashRetired) return;
        if (!labyrinthTrashActive)
        {
            var entrances = snapshot.Actors.Where(actor => actor.LayoutId == 4303363 || actor.DataId == 2000182).Take(2).ToArray();
            var atEntrance = entrances is [{ ObjectKind: ObjectKind.EventObj, DataId: 2000182, LayoutId: 4303363 } entrance]
                && Vector3.Distance(entrance.Position, LabyrinthEntrance) <= CrystalTowerRouteArrival
                && Vector3.Distance(snapshot.LocalActor!.Position, LabyrinthEntrance) <= 12
                && JoinLabyrinthNative(snapshot, entrance) is not null;
            // These are admission tolerances around the authored first combat
            // point, not a claim that the room has a native circular boundary.
            var atFirstPool = MathF.Abs(snapshot.LocalActor!.Position.Y - LabyrinthTrashWaypoints[1].Y) <= 3
                && Vector2.Distance(new(snapshot.LocalActor.Position.X, snapshot.LocalActor.Position.Z),
                    new(LabyrinthTrashWaypoints[1].X, LabyrinthTrashWaypoints[1].Z)) <= 45
                && snapshot.Actors.Any(actor => LabyrinthTrashMembership.Any(member => member.Pack == 0
                    && actor.LayoutId == member.Layout && actor.DataId == member.Base)
                    && actor.ObjectKind == ObjectKind.BattleNpc && JoinLabyrinthNative(snapshot, actor) is not null);
            if (!atEntrance && !atFirstPool) return;
            labyrinthTrashActive = true;
            labyrinthTrashWaypoint = atEntrance ? 0 : 1;
            labyrinthTrashArrived = false;
        }

        labyrinthTrashUncertain = false;
        // An observed actor that changed/omitted its layout while retaining a
        // tracked full object identity contradicts that receipt; it is not absence.
        foreach (var member in LabyrinthTrashMembership)
        {
            if (!labyrinthTrashActors.TryGetValue(member.Layout, out var state)) continue;
            if (snapshot.Actors.Any(actor => actor.GameObjectId == state.Actor.GameObjectId && actor.LayoutId != member.Layout))
                InvalidateLabyrinthTrashActor(member.Layout, member.Pack);
        }
        foreach (var member in LabyrinthTrashMembership)
        {
            var candidates = snapshot.Actors.Where(actor => actor.LayoutId == member.Layout).Take(2).ToArray();
            if (candidates.Length == 0) continue; // Preserve positive deaths, never turn a missing live actor into death.
            if (candidates is not [var actor] || actor.ObjectKind != ObjectKind.BattleNpc || actor.DataId != member.Base
                || !IsCrystalTowerEntityId(actor.EntityId) || JoinLabyrinthNative(snapshot, actor) is null || actor.Dead is null)
            {
                InvalidateLabyrinthTrashActor(member.Layout, member.Pack);
                continue;
            }
            var prior = labyrinthTrashActors.TryGetValue(member.Layout, out var retained) ? retained : (LabyrinthTrashActorState?)null;
            if (prior is { } known && (known.Actor.GameObjectId != actor.GameObjectId || known.Actor.EntityId != actor.EntityId
                    || known.Actor.Dead == true && actor.Dead == false))
            {
                // A replacement/revival starts a fresh attempt for this pack and
                // all later packs. Loaded old corpses need new positive live reads.
                foreach (var reset in LabyrinthTrashMembership.Where(item => item.Pack >= member.Pack))
                    if (labyrinthTrashActors.TryGetValue(reset.Layout, out var old))
                        labyrinthTrashActors[reset.Layout] = old with { SawAlive = false, Dead = false };
                RewindLabyrinthTrash(member.Pack);
                prior = labyrinthTrashActors[member.Layout];
            }
            var sameIdentity = prior is { } previous && previous.Actor.GameObjectId == actor.GameObjectId
                && previous.Actor.EntityId == actor.EntityId;
            var sawAlive = actor.Dead == false || sameIdentity && prior!.Value.SawAlive;
            labyrinthTrashActors[member.Layout] = new(actor, sawAlive, sawAlive && actor.Dead == true);
        }
    }

    private void InvalidateLabyrinthTrashActor(uint layout, int pack)
    {
        if (labyrinthTrashWaypoint > 0 && pack + 1 <= labyrinthTrashWaypoint) labyrinthTrashUncertain = true;
        if (labyrinthTrashActors.TryGetValue(layout, out var state))
            labyrinthTrashActors[layout] = state with { Dead = false };
        RewindLabyrinthTrash(pack);
    }

    private void RewindLabyrinthTrash(int pack)
    {
        if (labyrinthTrashWaypoint <= pack + 1) return;
        labyrinthTrashWaypoint = pack + 1;
        labyrinthTrashArrived = false;
    }

    private bool TryHandleCrystalTowerLabyrinthTrash(DutyContextSnapshot context, CrystalTowerObservation snapshot,
        ObservationSnapshot observation)
    {
        if (!LabyrinthRouteContext(context, snapshot) || !labyrinthTrashActive || labyrinthRouteStage is not null) return false;
        if (!labyrinthTrashArrived)
        {
            var destination = LabyrinthTrashWaypoints[labyrinthTrashWaypoint];
            if (Vector3.Distance(snapshot.LocalActor!.Position, destination) <= CrystalTowerRouteArrival)
            {
                labyrinthTrashArrived = true;
                return HoldCrystalTower("Crystal Tower: waiting for verified alliance mechanic state.");
            }
            if (!TryAcquireCrystalTowerMovement(context, (uint)snapshot.LocalId, GetCrystalTowerStrategy(context), out _))
                return HoldCrystalTower("Crystal Tower: waiting for the movement provider.");
            if (!CrystalTowerOwnerStillCurrent(context, snapshot))
                return HoldCrystalTower("Crystal Tower: waiting for verified alliance mechanic state.");
            if (!TryBeginNavigation(0x4C54524153480000ul + (uint)labyrinthTrashWaypoint, destination))
                return HoldCrystalTower("Crystal Tower: waiting for the movement provider.");
            SetPhase(ExecutionPhase.NavigatingToXyzDestination, "Crystal Tower: following the verified raid route.");
            return true;
        }
        if (labyrinthTrashUncertain)
            return HoldCrystalTower("Crystal Tower: waiting for verified alliance mechanic state.");
        if (labyrinthTrashWaypoint == 0)
        {
            labyrinthTrashWaypoint = 1;
            labyrinthTrashArrived = false;
            return HoldCrystalTower("Crystal Tower: waiting for verified alliance mechanic state.");
        }
        if (labyrinthTrashWaypoint == 4)
            return HoldCrystalTower("Crystal Tower: waiting for verified alliance mechanic state.");

        var pack = labyrinthTrashWaypoint - 1;
        var required = LabyrinthTrashMembership.Where(member => member.Pack == pack).ToArray();
        if (required.All(member => labyrinthTrashActors.TryGetValue(member.Layout, out var state) && state.Dead))
        {
            ++labyrinthTrashWaypoint;
            labyrinthTrashArrived = false;
            return HoldCrystalTower("Crystal Tower: waiting for verified alliance mechanic state.");
        }
        var target = snapshot.Actors.Where(actor => actor.Dead == false && actor.Targetable == true
                && required.Any(member => member.Layout == actor.LayoutId && member.Base == actor.DataId)
                && JoinLabyrinthNative(snapshot, actor) is not null)
            .OrderBy(actor => Vector3.DistanceSquared(actor.Position, snapshot.LocalActor!.Position))
            .ThenBy(actor => actor.GameObjectId).FirstOrDefault();
        return target is not null ? TryCrystalTowerTarget(context, snapshot, target, observation)
            : HoldCrystalTower("Crystal Tower: waiting for verified alliance mechanic state.");
    }
}
