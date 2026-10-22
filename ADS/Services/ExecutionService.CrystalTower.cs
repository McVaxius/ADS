using System.Numerics;
using ADS.Models;
using ServerEventRow = ADS.Services.HigherLowerServerEventTraceService.ServerEventRow;

namespace ADS.Services;

public sealed partial class ExecutionService
{
    private readonly record struct CrystalTowerOwner(uint EntityId, ulong ObjectId, uint Territory, uint Cfc,
        uint Map, string Alliance, OwnershipMode Ownership, CrystalTowerStrategyMode Strategy, string Preset);

    private CrystalTowerOwner? crystalTowerOwner;
    private DateTime crystalTowerOwnerSinceUtc;
    private ulong crystalTowerEventCursor;
    private readonly Dictionary<ulong, CrystalTowerEAnimObservation> crystalTowerAnimations = [];
    private readonly Dictionary<CrystalTowerAlliance, CrystalTowerActorObservation> crystalTowerAtomos = [];
    private readonly Dictionary<ulong, CrystalTowerActorObservation> crystalTowerVassago = [];
    private IReadOnlyList<ulong> crystalTowerHolders = [];
    private ulong crystalTowerFlareBoss;
    private bool crystalTowerFlareActive;
    private ulong crystalTowerFlareBoundary;
    private ulong crystalTowerTarget;
    private DutyContextSnapshot? crystalTowerLastContext;

    internal Func<IReadOnlyList<ServerEventRow>> ReadCrystalTowerEvents { get; set; } = () => [];
    internal Func<DutyContextSnapshot, CrystalTowerObservation>? CrystalTowerObservationOverride { get; set; }

    private CrystalTowerStrategyMode GetCrystalTowerStrategy(DutyContextSnapshot context)
        => objectPriorityRuleService is null ? CrystalTowerStrategyMode.None
            : CrystalTowerStrategy.Resolve(objectPriorityRuleService.Current.Rules, context);

    private void ResetCrystalTowerRuntime(DutyContextSnapshot? context, bool restoreMovement)
    {
        if (crystalTowerOwner is not null) StopMovementAssists();
        ReleaseCrystalTowerPyretic(context, discard: true);
        ReleaseCrystalTowerMovement(context, restoreMovement);
        ReleaseCrystalTowerMovement(context, restore: false);
        if (crystalTowerTarget != 0 && targetManager?.Target?.GameObjectId == crystalTowerTarget)
            targetManager.Target = null;
        crystalTowerTarget = 0;
        crystalTowerOwner = null;
        crystalTowerOwnerSinceUtc = default;
        crystalTowerEventCursor = 0;
        crystalTowerAnimations?.Clear();
        crystalTowerAtomos?.Clear();
        crystalTowerVassago?.Clear();
        crystalTowerVassagoAssignments?.Clear();
        crystalTowerHolders = [];
        crystalTowerFlareBoss = 0;
        crystalTowerFlareActive = false;
        crystalTowerFlareBoundary = 0;
        crystalTowerFlareCasting = false;
        crystalTowerBoneDragon = 0;
        crystalTowerBoneComplete = false;
        crystalTowerMeteorBoss = 0;
        crystalTowerMeteorActive = false;
        ResetCrystalTowerLabyrinthAttempts();
        ResetCrystalTowerSyrcus();
        ResetCrystalTowerWorldOfDarkness();
        ResetCrystalTowerWaves();
        ResetCrystalTowerRoute();
        ResetCrystalTowerLabyrinthRoute();
        ResetCrystalTowerLabyrinthTrash();
        crystalTowerLastContext = null;
    }

    internal void CancelCrystalTower(DutyContextSnapshot context)
    {
        ResetCrystalTowerRuntime(context, restoreMovement: true);
    }

    private void RefreshCrystalTowerLifetime(DutyContextSnapshot context, bool enabled)
    {
        if (crystalTowerOwner is not { } owner)
            return;
        if (!enabled || !context.IsLoggedIn || !context.InInstancedDuty || !IsActiveOwnedDutyMode()
            || owner.Territory != context.TerritoryTypeId || owner.Cfc != context.ContentFinderConditionId
            || owner.Map != context.MapId || owner.Alliance != context.Alliance
            || owner.Ownership != CurrentMode || owner.Strategy != GetCrystalTowerStrategy(context)
            || owner.Preset != objectPriorityRuleService.ActivePresetName)
            ResetCrystalTowerRuntime(context, restoreMovement: true);
    }

    private bool HoldCrystalTower(string status)
    {
        StopMovementAssists();
        if (crystalTowerTarget != 0 && targetManager.Target?.GameObjectId == crystalTowerTarget)
            targetManager.Target = null;
        crystalTowerTarget = 0;
        SetPhase(ExecutionPhase.CombatHold, status);
        return true;
    }

    private bool TryHandleCrystalTower(DutyContextSnapshot context, ObservationSnapshot observation)
    {
        var mode = GetCrystalTowerStrategy(context);
        if (!IsActiveOwnedDutyMode() || mode == CrystalTowerStrategyMode.None)
            return false;
        if (mode == CrystalTowerStrategyMode.Invalid || !context.IsLoggedIn || !context.InInstancedDuty
            || context.IsUnsafeTransition || context.IsCutscene || context.Alliance is not ("A" or "B" or "C"))
            return HoldCrystalTower("Crystal Tower: waiting for verified alliance mechanic state.");

        var now = ReadUtcNow();
        if (crystalTowerOwnerSinceUtc == default) crystalTowerOwnerSinceUtc = now;
        var snapshot = CrystalTowerObservationOverride?.Invoke(context)
            ?? CaptureCrystalTowerObservation(Plugin.PartyList, context, ReadCrystalTowerEvents(),
                crystalTowerEventCursor, crystalTowerOwnerSinceUtc, now);
        if (!snapshot.Valid || snapshot.LocalActor is not { } local || local.Dead != false)
        {
            if (crystalTowerPyreticActive && snapshot.LocalActor?.Dead != true)
                return HoldCrystalTower("Crystal Tower: waiting for verified alliance mechanic state.");
            if (snapshot.LocalActor?.Dead == true) ReleaseCrystalTowerPyretic(context, discard: true);
            ReleaseCrystalTowerMovement(context, restore: true);
            return HoldCrystalTower("Crystal Tower: waiting for verified alliance mechanic state.");
        }
        var owner = new CrystalTowerOwner((uint)snapshot.LocalId, local.GameObjectId, context.TerritoryTypeId,
            context.ContentFinderConditionId, context.MapId, context.Alliance, CurrentMode, mode,
            objectPriorityRuleService.ActivePresetName);
        if (crystalTowerOwner is { } prior && prior != owner)
        {
            ResetCrystalTowerRuntime(context, restoreMovement: true);
            crystalTowerOwnerSinceUtc = now;
            crystalTowerOwner = owner;
            return HoldCrystalTower("Crystal Tower: waiting for verified alliance mechanic state.");
        }
        crystalTowerOwner = owner;
        crystalTowerLastContext = context;
        if (context.TerritoryTypeId == 151 && local.StatusIds?.Contains(639u) == true)
            return TryHoldCrystalTowerPyretic(context);
        if (crystalTowerPyreticActive)
        {
            if (local.StatusIds is null)
                return HoldCrystalTower("Crystal Tower: waiting for verified alliance mechanic state.");
            if (!ReleaseCrystalTowerPyretic(context))
                return HoldCrystalTower("Crystal Tower: waiting for the movement provider.");
        }
        crystalTowerEventCursor = Math.Max(crystalTowerEventCursor, snapshot.SequenceWatermark);
        // Full object identity matters: many EventObjs share the nonnetwork EntityId sentinel.
        foreach (var animation in snapshot.EAnims)
            crystalTowerAnimations[animation.GameObjectId] = animation;
        foreach (var id in crystalTowerAnimations.Keys.Where(id => !snapshot.Actors.Any(actor => actor.GameObjectId == id)).ToArray())
            crystalTowerAnimations.Remove(id);

        ObserveCrystalTowerRoute(context, snapshot);
        ObserveCrystalTowerLabyrinthRoute(context, snapshot);
        ObserveCrystalTowerLabyrinthTrash(context, snapshot);
        var alliance = Enum.Parse<CrystalTowerAlliance>(context.Alliance);
        if (context.TerritoryTypeId == 174)
        {
            if (TryHandleCrystalTowerBoneDragon(context, snapshot, observation)) return true;
            if (TryHandleCrystalTowerMeteor(context, snapshot)) return true;
            if (TryHandleCrystalTowerFlare(context, snapshot, alliance)) return true;
            if (!CrystalTowerLabyrinthAtomosApproachPending
                && TryHandleCrystalTowerAtomos(context, snapshot, alliance, observation)) return true;
            if (TryHandleCrystalTowerTargetGates(context, snapshot, observation)) return true;
        }
        else if (context.TerritoryTypeId == 372)
        {
            if (TryHandleCrystalTowerSyrcus(context, snapshot, observation)) return true;
        }
        else if (context.TerritoryTypeId == 151)
        {
            if (TryHandleCrystalTowerWorldOfDarkness(context, snapshot, alliance, observation)) return true;
        }
        if (TryHandleCrystalTowerRoute(context, snapshot)) return true;
        if (TryHandleCrystalTowerLabyrinthTrash(context, snapshot, observation)) return true;
        if (TryHandleCrystalTowerLabyrinthRoute(context, snapshot, observation)) return true;
        ReleaseCrystalTowerMovement(context, restore: true);
        return false;
    }

    private bool TryCrystalTowerMove(DutyContextSnapshot context, CrystalTowerObservation snapshot,
        ulong navigationId, Vector2 destination, float radius)
    {
        if (!TryAcquireCrystalTowerMovement(context, (uint)snapshot.LocalId, GetCrystalTowerStrategy(context), out _))
            return HoldCrystalTower("Crystal Tower: waiting for the movement provider.");
        if (!CrystalTowerOwnerStillCurrent(context, snapshot))
            return HoldCrystalTower("Crystal Tower: waiting for verified alliance mechanic state.");
        var player = snapshot.LocalActor!;
        if (Vector2.Distance(new(player.Position.X, player.Position.Z), destination) <= radius)
            return HoldCrystalTower("Crystal Tower: holding the assigned platform.");
        // These authored mechanics supply X/Z only; retain the observed player's floor height.
        if (!TryBeginNavigation(navigationId, new(destination.X, player.Position.Y, destination.Y)))
            return HoldCrystalTower("Crystal Tower: waiting for the movement provider.");
        SetPhase(ExecutionPhase.NavigatingToMapXzDestination, "Crystal Tower: navigating to the assigned platform.");
        return true;
    }

    private bool TryCrystalTowerTarget(DutyContextSnapshot context, CrystalTowerObservation snapshot,
        CrystalTowerActorObservation target, ObservationSnapshot observation, bool keepPosition = false)
    {
        if (!CrystalTowerOwnerStillCurrent(context, snapshot)
            || target.Dead != false || target.Targetable != true)
            return HoldCrystalTower("Crystal Tower: assigned target is not eligible.");
        var matches = observation.LiveMonsters.Where(monster => monster.GameObjectId == target.GameObjectId
            && monster.DataId == target.DataId).Take(2).ToArray();
        var live = matches.Length == 1 ? matches[0] : null;
        var native = live is null ? null : ResolveGameObject(live);
        if (native is null || native.EntityId != target.EntityId || native.BaseId != target.DataId
            || native.IsDead || !native.IsTargetable)
            return HoldCrystalTower("Crystal Tower: assigned target is not eligible.");
        var distance = Vector3.Distance(native.Position, snapshot.LocalActor!.Position);
        var combatOwnsMovement = !keepPosition && (distance <= PreferredMonsterArrivalRange
            || context.InCombat && (distance <= BossFightCombatGhostRange || IsBossFightCombatGhosted(live!)));
        if (combatOwnsMovement)
            ReleaseCrystalTowerMovement(context, restore: true);
        else if (!TryAcquireCrystalTowerMovement(context, (uint)snapshot.LocalId, GetCrystalTowerStrategy(context), out _))
            return HoldCrystalTower("Crystal Tower: waiting for the movement provider.");
        if (!CrystalTowerOwnerStillCurrent(context, snapshot))
            return HoldCrystalTower("Crystal Tower: waiting for verified alliance mechanic state.");
        targetManager.Target = native;
        crystalTowerTarget = native.GameObjectId;
        if (keepPosition)
        {
            StopMovementAssists();
            SetPhase(ExecutionPhase.CombatHold, "Crystal Tower: holding the assigned platform.");
            return true;
        }
        TryAdvanceMonsterObjective(context, new PlannerSnapshot { Mode = PlannerMode.Progression,
            ObjectiveKind = PlannerObjectiveKind.BossFightMonster, Objective = live!.Name, Explanation = string.Empty,
            TargetGameObjectId = live.GameObjectId, TargetName = live.Name, CapturedAtUtc = ReadUtcNow() }, observation, string.Empty);
        return true;
    }

    private bool CrystalTowerOwnerStillCurrent(DutyContextSnapshot context, CrystalTowerObservation snapshot)
    {
        if (crystalTowerOwner is not { } owner || !IsActiveOwnedDutyMode() || owner.Ownership != CurrentMode
            || owner.EntityId != snapshot.LocalId || owner.ObjectId != snapshot.LocalActor?.GameObjectId
            || owner.Territory != context.TerritoryTypeId || owner.Cfc != context.ContentFinderConditionId
            || owner.Map != context.MapId || owner.Alliance != context.Alliance
            || owner.Strategy != GetCrystalTowerStrategy(context) || owner.Preset != objectPriorityRuleService.ActivePresetName)
            return false;
        if (CrystalTowerObservationOverride is not null) return snapshot.LocalActor?.Dead == false;
        return TryCaptureCrystalTowerActor(objectTable.LocalPlayer, out var local)
            && local is { Dead: false } && local.GameObjectId == owner.ObjectId && local.EntityId == owner.EntityId;
    }

    private CrystalTowerActorObservation? SingleCrystalTowerActor(CrystalTowerObservation snapshot, uint dataId)
    {
        var actors = snapshot.Actors.Where(actor => actor.DataId == dataId).ToArray();
        return actors.Length == 1 ? actors[0] : null;
    }

    private uint? CrystalTowerAnimation(CrystalTowerActorObservation? actor)
        => actor is not null && crystalTowerAnimations.TryGetValue(actor.GameObjectId, out var animation)
            && animation.DataId == actor.DataId && animation.EntityId == actor.EntityId ? animation.State : null;

    private static bool InCrystalTowerArena(CrystalTowerObservation snapshot, Vector2 center, float radius)
        => snapshot.LocalActor is { } local
            && Vector2.DistanceSquared(new(local.Position.X, local.Position.Z), center) <= radius * radius;
}
