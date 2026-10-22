using System.Numerics;
using ADS.Models;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;

namespace ADS.Services;

public sealed partial class ExecutionService
{
    // AutoDuty 53f4e740, AutoDuty/Paths/(174) The Labyrinth of the Ancients.json.
    // Post-entry only: its early unnamed Boss actions do not identify a trash-clear contract.
    private enum LabyrinthRouteStage
    { Bone, BoneShortcut, Atomos, Thanatos, ThanatosShortcut, Bomb, Behemoth, BehemothShortcut, FinalTransfer, Phlegethon }
    private LabyrinthRouteStage? labyrinthRouteStage;
    private int labyrinthRouteWaypoint;
    private ulong labyrinthRouteSample;
    private ulong labyrinthRouteEnteredSample;
    private bool labyrinthShortcutAttempted;
    private CrystalTowerActorObservation? labyrinthRouteBoss;
    private CrystalTowerActorObservation? labyrinthBoneIdentity;
    private bool labyrinthBoneReceipt;
    private LabyrinthRouteStage? labyrinthLastClear;
    private CrystalTowerActorObservation? labyrinthLastClearBoss;

    private static readonly Vector3[][] LabyrinthRouteApproaches =
    [
        [],
        [new(-450.51f, 24.74f, 12.31f)], // lines 70-79: native shortcut 2002804
        [new(10.37f, 48.54f, 278.98f), new(126.25f, 62.4f, 269.16f)], // 96-114: shared bridges
        [new(353.65f, 57.95f, 279.33f), new(411.06f, 66.21f, 280.41f)], // 135-153
        [new(451.2f, 66.16f, 288.54f)], // 161-170: native shortcut 2002805
        [new(-109.21f, 52.9f, 88.15f), new(-109.39f, 42.02f, 76.57f),
            new(-109.45f, 41.98f, 72.5f), new(-115.67f, 44.17f, -136.46f)], // 187-231
        [new(-109.91f, 68.04f, -342.31f), new(-109.99f, 68.05f, -338.71f)], // 239-257
        [new(-113.87f, 68.16f, -376.17f)], // 265-274: native shortcut 2002806
        [new(-110.57f, 48.22f, 300.28f)], // 291-296: final departure, not a teleport contract
        [],
    ];
    private static readonly Vector3 LabyrinthFinalLanding = new(-112.49f, 650.13f, 225.52f); // 304-309

    // Root skips only the premature XZ-only Atomos handler while this full XYZ
    // approach is pending. Once measured on the native own-lane pad, mechanics win.
    private bool CrystalTowerLabyrinthAtomosApproachPending
        => labyrinthRouteStage == LabyrinthRouteStage.Atomos && labyrinthRouteWaypoint < 3;

    private void ResetCrystalTowerLabyrinthRoute()
    {
        labyrinthRouteStage = labyrinthLastClear = null;
        labyrinthRouteWaypoint = 0;
        labyrinthRouteSample = labyrinthRouteEnteredSample = 0;
        labyrinthShortcutAttempted = labyrinthBoneReceipt = false;
        labyrinthRouteBoss = labyrinthBoneIdentity = labyrinthLastClearBoss = null;
    }

    private void ObserveCrystalTowerLabyrinthRoute(DutyContextSnapshot context, CrystalTowerObservation snapshot)
    {
        if (!LabyrinthRouteContext(context, snapshot)) return;
        ++labyrinthRouteSample;
        if (labyrinthRouteStage is null)
        {
            foreach (var stage in new[] { LabyrinthRouteStage.Bone, LabyrinthRouteStage.Atomos,
                LabyrinthRouteStage.Thanatos, LabyrinthRouteStage.Bomb, LabyrinthRouteStage.Behemoth, LabyrinthRouteStage.Phlegethon })
            {
                if (!InLabyrinthRouteRoom(snapshot.LocalActor!.Position, stage)
                    || !snapshot.Actors.Any(actor => actor.DataId == LabyrinthRouteBossId(stage))) continue;
                // A cold Atomos admission needs the actual native own-pad join. Existing
                // mechanics-only observations need not supply a traversal contract.
                if (stage == LabyrinthRouteStage.Atomos && LabyrinthOwnPad(context, snapshot) is null) continue;
                EnterLabyrinthRouteStage(stage, cold: true);
                break;
            }
        }
        if (labyrinthRouteStage is not { } current) return;
        // Capture a real final-room landing before an urgent Flare handler can
        // return early and move away from that arrival point on the next sample.
        if (current == LabyrinthRouteStage.FinalTransfer && labyrinthRouteSample > labyrinthRouteEnteredSample
            && Vector3.Distance(snapshot.LocalActor!.Position, LabyrinthFinalLanding) <= CrystalTowerRouteArrival
            && LabyrinthRouteSingleton(snapshot, LabyrinthRouteStage.Phlegethon) is not null)
        {
            EnterLabyrinthRouteStage(LabyrinthRouteStage.Phlegethon, cold: true);
            current = LabyrinthRouteStage.Phlegethon;
        }
        if (labyrinthLastClear is { } prior && InLabyrinthRouteRoom(snapshot.LocalActor!.Position, prior))
        {
            var present = snapshot.Actors.Where(actor => actor.DataId == LabyrinthRouteBossId(prior)).ToArray();
            var actor = LabyrinthRouteSingleton(snapshot, prior);
            var invalidated = prior switch
            {
                LabyrinthRouteStage.Atomos => crystalTowerAtomos.Count != 3 || crystalTowerAtomos.Values.Any(boss => boss.Dead != true),
                // The director is the clear authority; do not downgrade its final
                // receipt because a native corpse flag is late or unavailable.
                LabyrinthRouteStage.Bone => !crystalTowerBoneComplete || actor is not null
                    && labyrinthLastClearBoss is { } priorBone && (actor.GameObjectId != priorBone.GameObjectId || actor.EntityId != priorBone.EntityId),
                _ => present.Length > 0 && (actor is null || actor.Dead != true || labyrinthLastClearBoss is not { } receipt
                    || actor.GameObjectId != receipt.GameObjectId || actor.EntityId != receipt.EntityId),
            };
            if (invalidated)
            {
                EnterLabyrinthRouteStage(prior, cold: true);
                labyrinthLastClear = null;
                labyrinthLastClearBoss = null;
                if (prior == LabyrinthRouteStage.Bone) labyrinthBoneReceipt = false;
                current = prior;
            }
        }
        labyrinthRouteBoss = LabyrinthRouteSingleton(snapshot, current);
        if (current != LabyrinthRouteStage.Bone) return;
        if (labyrinthRouteBoss is { } bone)
        {
            if (!crystalTowerBoneComplete || labyrinthBoneIdentity is { } previous
                && (previous.GameObjectId != bone.GameObjectId || previous.EntityId != bone.EntityId))
                labyrinthBoneReceipt = false;
            labyrinthBoneIdentity = bone;
        }
        else if (snapshot.Actors.Any(actor => actor.DataId == 0x92B)) labyrinthBoneReceipt = false;
    }

    private void EnterLabyrinthRouteStage(LabyrinthRouteStage stage, bool cold = false)
    {
        labyrinthRouteStage = stage;
        labyrinthRouteWaypoint = cold ? stage == LabyrinthRouteStage.Atomos ? 2 : LabyrinthRouteApproaches[(int)stage].Length : 0;
        labyrinthRouteEnteredSample = labyrinthRouteSample;
        labyrinthShortcutAttempted = false;
        labyrinthRouteBoss = null;
    }

    private bool TryHandleCrystalTowerLabyrinthRoute(DutyContextSnapshot context, CrystalTowerObservation snapshot,
        ObservationSnapshot observation)
    {
        if (!LabyrinthRouteContext(context, snapshot) || labyrinthRouteStage is not { } stage) return false;
        if (completedDuty == (174u, 92u)) return false;
        // A real later landing outranks an unfinished departure approach. Never
        // navigate back across floors after the native destination is observed.
        if (LabyrinthShortcut(stage) is { } arrivedShortcut && labyrinthRouteSample > labyrinthRouteEnteredSample
            && Vector3.Distance(snapshot.LocalActor!.Position, arrivedShortcut.Landing) <= CrystalTowerRouteArrival)
            return TryLabyrinthShortcut(context, snapshot, observation, arrivedShortcut);
        if (stage == LabyrinthRouteStage.FinalTransfer && labyrinthRouteSample > labyrinthRouteEnteredSample
            && Vector3.Distance(snapshot.LocalActor!.Position, LabyrinthFinalLanding) <= CrystalTowerRouteArrival
            && LabyrinthRouteSingleton(snapshot, LabyrinthRouteStage.Phlegethon) is not null)
        {
            EnterLabyrinthRouteStage(LabyrinthRouteStage.Phlegethon, cold: true);
            return HoldCrystalTower("Crystal Tower: waiting for verified alliance mechanic state.");
        }
        var waypoints = LabyrinthRouteApproaches[(int)stage];
        if (labyrinthRouteWaypoint < waypoints.Length)
            return MoveLabyrinthRoute(context, snapshot, waypoints[labyrinthRouteWaypoint], (ulong)labyrinthRouteWaypoint);
        if (stage == LabyrinthRouteStage.Atomos && labyrinthRouteWaypoint == 2)
        {
            var pad = LabyrinthOwnPad(context, snapshot);
            return pad is null ? HoldCrystalTower("Crystal Tower: waiting for the assigned Atomos platform.")
                : MoveLabyrinthRoute(context, snapshot, pad.Position, pad.GameObjectId);
        }
        if (LabyrinthShortcut(stage) is { } shortcut)
            return TryLabyrinthShortcut(context, snapshot, observation, shortcut);
        if (stage == LabyrinthRouteStage.FinalTransfer)
        {
            // Y48 -> Y650 is not a navmesh leg. Only a later real destination read
            // plus its native boss can acknowledge arrival; no guessed portal or prompt.
            return HoldCrystalTower("Crystal Tower: waiting for verified alliance mechanic state.");
        }
        if (stage == LabyrinthRouteStage.Phlegethon)
            return labyrinthRouteBoss is { Dead: false, Targetable: true } ? false
                : HoldCrystalTower("Crystal Tower: waiting for verified alliance mechanic state.");

        if (stage == LabyrinthRouteStage.Bone && crystalTowerBoneComplete && labyrinthBoneIdentity is { } identity
            && identity.GameObjectId == crystalTowerBoneDragon
            && crystalTowerBoneActor is { } attempt && identity.GameObjectId == attempt.GameObjectId && identity.EntityId == attempt.EntityId
            && (!snapshot.Actors.Any(actor => actor.DataId == 0x92B)
                || labyrinthRouteBoss is { } currentBone && currentBone.GameObjectId == identity.GameObjectId
                    && currentBone.EntityId == identity.EntityId)
            && snapshot.Directors?.Any(update => update.Sequence > crystalTowerBoneAttemptBoundary
                && update.UpdateId == 0x80000001 && update.Param1 == 0xB4) == true)
            labyrinthBoneReceipt = true;
        var complete = stage switch
        {
            LabyrinthRouteStage.Bone => crystalTowerBoneComplete && labyrinthBoneReceipt,
            LabyrinthRouteStage.Atomos => crystalTowerAtomos.Count == 3 && crystalTowerAtomos.Values.All(boss => boss.Dead == true),
            _ => labyrinthRouteBoss is { Dead: true } boss && LabyrinthRouteSingleton(snapshot, stage) is { Dead: true } fresh
                && boss.GameObjectId == fresh.GameObjectId && boss.EntityId == fresh.EntityId,
        };
        if (!complete)
            return labyrinthRouteBoss is { Dead: false, Targetable: true } ? false
                : HoldCrystalTower("Crystal Tower: waiting for verified alliance mechanic state.");
        labyrinthLastClear = stage;
        labyrinthLastClearBoss = stage == LabyrinthRouteStage.Bone ? labyrinthBoneIdentity : labyrinthRouteBoss;
        EnterLabyrinthRouteStage(stage switch
        {
            LabyrinthRouteStage.Bone => LabyrinthRouteStage.BoneShortcut,
            LabyrinthRouteStage.Atomos => LabyrinthRouteStage.Thanatos,
            LabyrinthRouteStage.Thanatos => LabyrinthRouteStage.ThanatosShortcut,
            LabyrinthRouteStage.Bomb => LabyrinthRouteStage.Behemoth,
            _ => LabyrinthRouteStage.BehemothShortcut,
        });
        return HoldCrystalTower("Crystal Tower: waiting for verified alliance mechanic state.");
    }

    private bool MoveLabyrinthRoute(DutyContextSnapshot context, CrystalTowerObservation snapshot, Vector3 destination, ulong identity)
    {
        if (Vector3.Distance(snapshot.LocalActor!.Position, destination) <= CrystalTowerRouteArrival)
        {
            StopMovementAssists();
            ++labyrinthRouteWaypoint;
            return HoldCrystalTower("Crystal Tower: waiting for verified alliance mechanic state.");
        }
        if (!TryAcquireCrystalTowerMovement(context, (uint)snapshot.LocalId, GetCrystalTowerStrategy(context), out _))
            return HoldCrystalTower("Crystal Tower: waiting for the movement provider.");
        if (!CrystalTowerOwnerStillCurrent(context, snapshot))
            return HoldCrystalTower("Crystal Tower: waiting for verified alliance mechanic state.");
        var key = identity ^ (0x4C4F540000000000ul + ((ulong)labyrinthRouteStage!.Value << 32));
        if (!TryBeginNavigation(key, destination)) return HoldCrystalTower("Crystal Tower: waiting for the movement provider.");
        SetPhase(ExecutionPhase.NavigatingToXyzDestination, "Crystal Tower: following the verified raid route.");
        return true;
    }

    // Object placements are separate from AutoDuty's player approach points.
    // Sapphire aab9cb2347eb0ec42e85ee3e117a0c177b9b3070, TheLabyrinthoftheAncients.cpp.
    private readonly record struct LabyrinthShortcutContract(uint DataId, Vector3 ObjectPosition, Vector3 Landing, LabyrinthRouteStage Next);
    private static LabyrinthShortcutContract? LabyrinthShortcut(LabyrinthRouteStage stage) => stage switch
    {
        LabyrinthRouteStage.BoneShortcut => new(2002804, new(-451.17691f, 25.50491f, 18.912251f), new(-110.22f, 45.51f, 318.52f), LabyrinthRouteStage.Atomos),
        LabyrinthRouteStage.ThanatosShortcut => new(2002805, new(455.122894f, 66.249268f, 279.987701f), new(-110.36f, 45.51f, 316.1f), LabyrinthRouteStage.Bomb),
        LabyrinthRouteStage.BehemothShortcut => new(2002806, new(-109.976601f, 68.215019f, -378.115906f), new(-109.55f, 45.51f, 315.78f), LabyrinthRouteStage.FinalTransfer),
        _ => null,
    };

    private bool TryLabyrinthShortcut(DutyContextSnapshot context, CrystalTowerObservation snapshot,
        ObservationSnapshot observation, LabyrinthShortcutContract shortcut)
    {
        if (labyrinthRouteSample > labyrinthRouteEnteredSample
            && Vector3.Distance(snapshot.LocalActor!.Position, shortcut.Landing) <= CrystalTowerRouteArrival)
        {
            if (pendingProgressionInteractable?.DataId == shortcut.DataId) ClearInteractableCommitment();
            EnterLabyrinthRouteStage(shortcut.Next);
            return HoldCrystalTower("Crystal Tower: waiting for verified alliance mechanic state.");
        }
        if (labyrinthShortcutAttempted || IsNativeInteractionActive(context))
            return HoldCrystalTower("Crystal Tower: waiting for verified shortcut activation and arrival.");
        var candidates = snapshot.Actors.Where(actor => actor.DataId == shortcut.DataId).Take(2).ToArray();
        var actor = candidates is [var single] ? single : null;
        if (actor is not { ObjectKind: ObjectKind.EventObj, Targetable: true }
            || Vector3.Distance(actor.Position, shortcut.ObjectPosition) > CrystalTowerRouteArrival
            || JoinLabyrinthNative(snapshot, actor) is null)
            return HoldCrystalTower("Crystal Tower: waiting for verified shortcut activation and arrival.");
        var matches = observation.LiveInteractables.Where(item => item.GameObjectId == actor.GameObjectId
            && item.DataId == actor.DataId && item.ObjectKind == ObjectKind.EventObj && item.MapId == context.MapId
            && item.Classification != InteractableClass.Ignored && item.Position == actor.Position).Take(2).ToArray();
        if (matches is not [var observed])
            return HoldCrystalTower("Crystal Tower: waiting for verified shortcut activation and arrival.");
        if (!TryAcquireCrystalTowerMovement(context, (uint)snapshot.LocalId, GetCrystalTowerStrategy(context), out _))
            return HoldCrystalTower("Crystal Tower: waiting for the movement provider.");
        if (!CrystalTowerOwnerStillCurrent(context, snapshot))
            return HoldCrystalTower("Crystal Tower: waiting for verified alliance mechanic state.");
        var priorDispatch = interactionAttemptDispatched;
        var priorTarget = lastInteractGameObjectId;
        var priorCooldown = nextInteractAttemptUtc;
        TryAdvanceInteractableObjective(context, observed, string.Empty);
        if (CrystalTowerOwnerStillCurrent(context, snapshot) && interactionAttemptDispatched
            && lastInteractGameObjectId == actor.GameObjectId && interactionAttemptDuty == (174u, 92u)
            && interactionAttemptOwnerMode == CurrentMode
            && (!priorDispatch || priorTarget != lastInteractGameObjectId || priorCooldown != nextInteractAttemptUtc))
            labyrinthShortcutAttempted = true;
        return true;
    }

    private bool LabyrinthRouteContext(DutyContextSnapshot context, CrystalTowerObservation snapshot)
        => (context.TerritoryTypeId, context.ContentFinderConditionId) == (174, 92)
            && !context.IsUnsafeTransition && !context.IsCutscene && snapshot.Valid
            && snapshot.LocalActor is { Dead: false } local && IsFiniteCrystalTowerPosition(local.Position)
            && CrystalTowerOwnerStillCurrent(context, snapshot);

    private static uint LabyrinthRouteBossId(LabyrinthRouteStage stage) => stage switch
    {
        LabyrinthRouteStage.Bone => 0x92B, LabyrinthRouteStage.Atomos => 0x963,
        LabyrinthRouteStage.Thanatos => 0x92E, LabyrinthRouteStage.Bomb => 0x967,
        LabyrinthRouteStage.Behemoth => 0x932, LabyrinthRouteStage.Phlegethon => 0x938, _ => 0,
    };

    private static bool InLabyrinthRouteRoom(Vector3 position, LabyrinthRouteStage stage)
    {
        if (!IsFiniteCrystalTowerPosition(position)) return false;
        if (stage == LabyrinthRouteStage.Atomos)
            return MathF.Abs(position.Y - 50.56f) <= 3 && MathF.Abs(position.X - 232.5f) <= 37.65f
                && MathF.Abs(position.Z - 280) <= 47.5f;
        var (center, floor, radius) = stage switch
        {
            LabyrinthRouteStage.Bone => (new Vector2(-451.2f, 23.93f), 25.53f, 49.4f),
            LabyrinthRouteStage.Thanatos => (new Vector2(440.4f, 280), 66.21f, MathF.Sqrt(44 * 44 + 8 * 8)),
            LabyrinthRouteStage.Bomb => (new Vector2(-110, -165.6f), 44.17f, 33.2f),
            LabyrinthRouteStage.Behemoth => (new Vector2(-110, -368.35f), 68.05f, 29.5f),
            LabyrinthRouteStage.Phlegethon => (new Vector2(-110, 181.6f), 650.13f, 44.5f),
            _ => (Vector2.Zero, float.NaN, 0f),
        };
        return MathF.Abs(position.Y - floor) <= 3
            && Vector2.DistanceSquared(new(position.X, position.Z), center) <= radius * radius;
    }

    private CrystalTowerActorObservation? LabyrinthRouteSingleton(CrystalTowerObservation snapshot, LabyrinthRouteStage stage)
    {
        if (LabyrinthRouteBossId(stage) == 0 || stage == LabyrinthRouteStage.Atomos
            || !InLabyrinthRouteRoom(snapshot.LocalActor!.Position, stage)) return null;
        var actors = snapshot.Actors.Where(actor => actor.DataId == LabyrinthRouteBossId(stage)).Take(2).ToArray();
        return actors is [var actor] && actor.ObjectKind == ObjectKind.BattleNpc && IsCrystalTowerEntityId(actor.EntityId)
            && InLabyrinthRouteRoom(actor.Position, stage) && JoinLabyrinthNative(snapshot, actor) is not null ? actor : null;
    }

    private CrystalTowerActorObservation? LabyrinthOwnPad(DutyContextSnapshot context, CrystalTowerObservation snapshot)
    {
        var alliance = Enum.Parse<CrystalTowerAlliance>(context.Alliance!);
        var id = CrystalTowerMechanicPolicy.GetAtomosPadDataId(alliance);
        var pads = snapshot.Actors.Where(actor => actor.DataId == id).Take(2).ToArray();
        return pads is [var pad] && pad.ObjectKind == ObjectKind.EventObj && InLabyrinthRouteRoom(pad.Position, LabyrinthRouteStage.Atomos)
            && CrystalTowerMechanicPolicy.GetAtomosLane(new(pad.Position.X, pad.Position.Z)) == alliance
            && JoinLabyrinthNative(snapshot, pad) is not null ? pad : null;
    }

    private unsafe IGameObject? JoinLabyrinthNative(CrystalTowerObservation snapshot, CrystalTowerActorObservation actor)
    {
        if (!IsCrystalTowerObjectId(actor.GameObjectId) || !IsFiniteCrystalTowerPosition(actor.Position)
            || snapshot.Actors.Count(other => other.GameObjectId == actor.GameObjectId) != 1
            || actor.ObjectKind == ObjectKind.BattleNpc && snapshot.Actors.Count(other => other.EntityId == actor.EntityId) != 1) return null;
        try
        {
            var matches = objectTable.Where(candidate => candidate.GameObjectId == actor.GameObjectId).Take(2).ToArray();
            if (matches is not [var native] || native.EntityId != actor.EntityId || native.BaseId != actor.DataId
                || native.ObjectKind != actor.ObjectKind || !IsFiniteCrystalTowerPosition(native.Position)
                || native.Position != actor.Position || actor.Dead.HasValue && native.IsDead != actor.Dead.Value
                || actor.Targetable.HasValue && native.IsTargetable != actor.Targetable.Value) return null;
        if (actor.LayoutId is { } layoutId && (native.Address == 0
                || ((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)native.Address)->LayoutId != layoutId)) return null;
            return native;
        }
        catch { return null; }
    }
}
