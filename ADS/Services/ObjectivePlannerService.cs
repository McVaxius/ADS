using System.Numerics;
using ADS.Models;
using Dalamud.Plugin.Services;

namespace ADS.Services;

public sealed class ObjectivePlannerService
{
    private const float RequiredInteractableMaterialLead = 5f;
    private const float RequiredInteractableVerticalSlack = 1.5f;
    private const float RequiredInteractableDistanceTieWindow = 6f;
    private const float CombatFriendlyInteractableDistanceCap = 25f;
    private const float CombatFriendlyVerticalCap = 6f;
    private const float TreasureCofferMaterialLead = 8f;
    private const float TreasureCofferMaxHorizontalDistance = 100f;
    private const float TreasureCofferVerticalCap = 5f;
    private readonly IObjectTable objectTable;
    private readonly ObjectPriorityRuleService objectPriorityRuleService;
    private readonly DungeonFrontierService dungeonFrontierService;
    private readonly ObservationMemoryService observationMemoryService;

    public ObjectivePlannerService(
        IObjectTable objectTable,
        ObjectPriorityRuleService objectPriorityRuleService,
        DungeonFrontierService dungeonFrontierService,
        ObservationMemoryService observationMemoryService)
    {
        this.objectTable = objectTable;
        this.objectPriorityRuleService = objectPriorityRuleService;
        this.dungeonFrontierService = dungeonFrontierService;
        this.observationMemoryService = observationMemoryService;
        Current = new PlannerSnapshot
        {
            Mode = PlannerMode.IdleObserve,
            ObjectiveKind = PlannerObjectiveKind.None,
            Objective = "Await instanced duty",
            Explanation = "ADS is idle until an instanced-duty context appears.",
            CapturedAtUtc = DateTime.UtcNow,
        };
    }

    public PlannerSnapshot Current { get; private set; }

    public void Update(
        DutyContextSnapshot context,
        ObservationSnapshot observation,
        OwnershipMode ownershipMode,
        bool considerTreasureCoffers)
    {
        var now = DateTime.UtcNow;
        if (!context.PluginEnabled)
        {
            Current = new PlannerSnapshot
            {
                Mode = PlannerMode.IdleObserve,
                ObjectiveKind = PlannerObjectiveKind.None,
                Objective = "ADS disabled",
                Explanation = "The plugin is disabled, so observer and execution decisions are suppressed.",
                CapturedAtUtc = now,
            };
            return;
        }

        if (!context.IsLoggedIn)
        {
            Current = new PlannerSnapshot
            {
                Mode = PlannerMode.IdleObserve,
                ObjectiveKind = PlannerObjectiveKind.None,
                Objective = "Await login",
                Explanation = "ADS has no character context yet.",
                CapturedAtUtc = now,
            };
            return;
        }

        if (context.IsUnsafeTransition)
        {
            Current = new PlannerSnapshot
            {
                Mode = PlannerMode.UnsafeTransition,
                ObjectiveKind = PlannerObjectiveKind.None,
                Objective = "Hold during transition",
                Explanation = "BetweenAreas is active, so ADS suppresses object truth and action attempts until the duty state settles again.",
                CapturedAtUtc = now,
            };
            return;
        }

        if (!context.InInstancedDuty)
        {
            Current = new PlannerSnapshot
            {
                Mode = PlannerMode.IdleObserve,
                ObjectiveKind = PlannerObjectiveKind.None,
                Objective = "Await instanced duty",
                Explanation = "ADS is outside instanced duty. Ownership can be queued from outside, but observation starts only after duty entry.",
                CapturedAtUtc = now,
            };
            return;
        }

        var playerPosition = objectTable.LocalPlayer?.Position;
        var isRawTreasureFollower = dungeonFrontierService.TreasureDungeonRole == ADS.Models.TreasureDungeonRole.Follower;
        var liveMonsterEligibility = objectPriorityRuleService.EvaluateBattleNpcPlanningEligibility(context, observation.LiveMonsters, playerPosition);
        var liveFollowTargetEligibility = objectPriorityRuleService.EvaluateBattleNpcPlanningEligibility(context, observation.LiveFollowTargets, playerPosition);
        var nearestBossFightMonster = GetBestBossFightBattleNpc(liveMonsterEligibility);
        var nearestMonster = GetBestBattleNpc(liveMonsterEligibility);
        var nearestFollowTarget = GetBestBattleNpc(liveFollowTargetEligibility);

        var nearestRequiredInteractable = GetBestInteractable(
            observation.LiveInteractables.Where(x => IsProgressionInteractable(context, x, playerPosition)),
            playerPosition,
            context);
        var nearestCombatFriendlyInteractable = GetBestInteractable(
            observation.LiveInteractables.Where(x => x.Classification == InteractableClass.CombatFriendly),
                playerPosition,
                context);
        var nearestTreasureCoffer = considerTreasureCoffers
            && !isRawTreasureFollower
            ? GetBestInteractable(
                observation.LiveInteractables.Where(x =>
                    x.Classification == InteractableClass.TreasureCoffer
                    && !LooksLikeTreasureFollowerLootSackName(x.Name)),
                playerPosition,
                context)
            : null;
        var nearestOptionalInteractable = GetBestInteractable(
            observation.LiveInteractables.Where(x => x.Classification == InteractableClass.Optional),
            playerPosition,
            context);
        var nearestRuleBackedInteractableGhost = GetBestRuleBackedInteractableGhost(
            observation.InteractableGhosts
                .Where(x => MatchesCurrentMap(context.MapId, x.MapId))
                .Where(x => IsProgressionInteractable(context, x, playerPosition)),
            playerPosition,
            context);
        var currentForceMarchManualDestination = dungeonFrontierService.CurrentMode is FrontierMode.MapXzDestination or FrontierMode.XyzDestination
            && dungeonFrontierService.CurrentTarget is { IsForceMarchManualDestination: true } forceMarchManualDestination
                ? forceMarchManualDestination
                : null;

        if (currentForceMarchManualDestination is not null)
        {
            Current = BuildForceMarchMonsterBypassSnapshot(
                context,
                currentForceMarchManualDestination,
                nearestMonster,
                nearestRequiredInteractable,
                playerPosition,
                now,
                inCombat: context.InCombat);
            return;
        }

        if (context.InCombat)
        {
            var combatFriendlyDistance = GetDistance(playerPosition, nearestCombatFriendlyInteractable?.Position);
            var combatFriendlyVerticalDelta = GetVerticalDelta(playerPosition, nearestCombatFriendlyInteractable?.Position);
            if (nearestCombatFriendlyInteractable is not null
                && combatFriendlyDistance.HasValue
                && combatFriendlyDistance.Value <= CombatFriendlyInteractableDistanceCap
                && (!combatFriendlyVerticalDelta.HasValue || combatFriendlyVerticalDelta.Value <= CombatFriendlyVerticalCap))
            {
                Current = new PlannerSnapshot
                {
                    Mode = PlannerMode.Progression,
                    ObjectiveKind = PlannerObjectiveKind.CombatFriendlyInteractable,
                    Objective = $"Use combat-friendly interactable: {nearestCombatFriendlyInteractable.Name}",
                    Explanation = $"Combat is active, but {nearestCombatFriendlyInteractable.Name} is marked combat-friendly and remains within the safe combat interact window ({combatFriendlyDistance:0.0}).",
                    TargetName = nearestCombatFriendlyInteractable.Name,
                    TargetDistance = combatFriendlyDistance,
                    TargetVerticalDelta = combatFriendlyVerticalDelta,
                    CapturedAtUtc = now,
                };
                return;
            }

            if (nearestBossFightMonster is not null)
            {
                var distance = GetDistance(playerPosition, nearestBossFightMonster.Position);
                var verticalDelta = GetVerticalDelta(playerPosition, nearestBossFightMonster.Position);
                Current = new PlannerSnapshot
                {
                    Mode = PlannerMode.Progression,
                    ObjectiveKind = PlannerObjectiveKind.BossFightMonster,
                    Objective = $"Engage boss-fight target: {nearestBossFightMonster.Name}",
                    Explanation = BuildBossFightExplanation(context, nearestBossFightMonster, playerPosition, true),
                    TargetName = nearestBossFightMonster.Name,
                    TargetDistance = distance,
                    TargetVerticalDelta = verticalDelta,
                    CapturedAtUtc = now,
                };
                return;
            }

            Current = new PlannerSnapshot
            {
                Mode = PlannerMode.Combat,
                ObjectiveKind = PlannerObjectiveKind.None,
                Objective = "Combat owns the next decision",
                Explanation = "ADS stays monster-first but does not become the combat brain; it waits for combat to clear before progression decisions resume.",
                CapturedAtUtc = now,
            };
            return;
        }

        if (TryBuildTreasureFollowerLootSnapshot(context, observation, playerPosition, now, considerTreasureCoffers, out var treasureFollowerLootSnapshot))
        {
            Current = treasureFollowerLootSnapshot;
            return;
        }

        if (TryBuildTreasureFollowerRouteOwnershipSnapshot(context, observation, playerPosition, now, out var treasureFollowerHoldSnapshot))
        {
            Current = treasureFollowerHoldSnapshot;
            return;
        }

        if (nearestBossFightMonster is not null)
        {
            var distance = GetDistance(playerPosition, nearestBossFightMonster.Position);
            var verticalDelta = GetVerticalDelta(playerPosition, nearestBossFightMonster.Position);
            Current = new PlannerSnapshot
            {
                Mode = PlannerMode.Progression,
                ObjectiveKind = PlannerObjectiveKind.BossFightMonster,
                Objective = $"Engage boss-fight target: {nearestBossFightMonster.Name}",
                Explanation = BuildBossFightExplanation(context, nearestBossFightMonster, playerPosition, false),
                TargetName = nearestBossFightMonster.Name,
                TargetDistance = distance,
                TargetVerticalDelta = verticalDelta,
                CapturedAtUtc = now,
            };
            return;
        }

        if (nearestTreasureCoffer is not null && playerPosition.HasValue)
        {
            var treasureDistance = Vector3.Distance(nearestTreasureCoffer.Position, playerPosition.Value);
            var treasureHorizontalDistance = GetHorizontalDistance(playerPosition, nearestTreasureCoffer.Position) ?? treasureDistance;
            var treasureVerticalDelta = MathF.Abs(nearestTreasureCoffer.Position.Y - playerPosition.Value.Y);
            var nearestMonsterDistance = nearestMonster is not null
                ? GetHorizontalDistance(playerPosition, nearestMonster.Position)
                : (float?)null;
            var nearestMonsterRuleDistance = nearestMonster is not null
                ? GetDistance(playerPosition, nearestMonster.Position)
                : (float?)null;
            var nearestMonsterVerticalDelta = nearestMonster is not null
                ? GetVerticalDelta(playerPosition, nearestMonster.Position)
                : (float?)null;
            var nearestMonsterPriority = nearestMonster is not null
                ? objectPriorityRuleService.GetEffectiveBattleNpcPriority(
                    context,
                    nearestMonster,
                    nearestMonsterRuleDistance,
                    nearestMonsterVerticalDelta)
                : (int?)null;
            var nearestRequiredDistance = nearestRequiredInteractable is not null
                ? GetHorizontalDistance(playerPosition, nearestRequiredInteractable.Position)
                : (float?)null;
            var nearestRequiredRuleDistance = nearestRequiredInteractable is not null
                ? GetDistance(playerPosition, nearestRequiredInteractable.Position)
                : (float?)null;
            var nearestRequiredVerticalDelta = nearestRequiredInteractable is not null
                ? GetVerticalDelta(playerPosition, nearestRequiredInteractable.Position)
                : (float?)null;
            var nearestRequiredPriority = nearestRequiredInteractable is not null
                ? objectPriorityRuleService.GetEffectivePriority(
                    context,
                    nearestRequiredInteractable,
                    nearestRequiredRuleDistance,
                    nearestRequiredVerticalDelta)
                : (int?)null;
            var treasurePriority = objectPriorityRuleService.GetEffectivePriority(
                context,
                nearestTreasureCoffer,
                treasureDistance,
                treasureVerticalDelta);

            if (ShouldSelectTreasureCoffer(
                    treasureHorizontalDistance,
                    treasureVerticalDelta,
                    treasurePriority,
                    nearestMonsterDistance,
                    nearestMonsterPriority,
                    nearestRequiredDistance,
                    nearestRequiredPriority))
            {
                Current = new PlannerSnapshot
                {
                    Mode = PlannerMode.Progression,
                    ObjectiveKind = PlannerObjectiveKind.TreasureCoffer,
                    Objective = $"Open treasure coffer: {nearestTreasureCoffer.Name}",
                    Explanation = BuildTreasureCofferExplanation(
                        treasureDistance,
                        treasureHorizontalDistance,
                        treasureVerticalDelta,
                        treasurePriority,
                        nearestMonsterDistance,
                        nearestMonsterPriority,
                        nearestRequiredDistance,
                        nearestRequiredPriority),
                    TargetName = nearestTreasureCoffer.Name,
                    TargetDistance = treasureDistance,
                    TargetVerticalDelta = treasureVerticalDelta,
                    CapturedAtUtc = now,
                };
                return;
            }
        }

        if (nearestMonster is not null && nearestRequiredInteractable is not null && playerPosition.HasValue)
        {
            var monsterDistance = Vector3.Distance(nearestMonster.Position, playerPosition.Value);
            var interactableDistance = Vector3.Distance(nearestRequiredInteractable.Position, playerPosition.Value);
            var monsterVerticalDelta = MathF.Abs(nearestMonster.Position.Y - playerPosition.Value.Y);
            var interactableVerticalDelta = MathF.Abs(nearestRequiredInteractable.Position.Y - playerPosition.Value.Y);
            if (ShouldPrioritizeRequiredInteractable(
                    context,
                    nearestRequiredInteractable,
                    nearestMonster,
                    interactableDistance,
                    monsterDistance,
                    interactableVerticalDelta,
                    monsterVerticalDelta))
            {
                var explanation = BuildRequiredInteractableExplanation(
                    context,
                    nearestRequiredInteractable,
                    nearestMonster,
                    interactableDistance,
                    monsterDistance,
                    interactableVerticalDelta,
                    monsterVerticalDelta);
                Current = new PlannerSnapshot
                {
                    Mode = PlannerMode.Progression,
                    ObjectiveKind = GetProgressionInteractableObjectiveKind(nearestRequiredInteractable),
                    Objective = $"Use progression interactable: {nearestRequiredInteractable.Name}",
                    Explanation = explanation,
                    TargetName = nearestRequiredInteractable.Name,
                    TargetDistance = interactableDistance,
                    TargetVerticalDelta = interactableVerticalDelta,
                    CapturedAtUtc = now,
                };
                return;
            }
        }

        if (nearestMonster is not null)
        {
            var distance = playerPosition.HasValue
                ? Vector3.Distance(nearestMonster.Position, playerPosition.Value)
                : (float?)null;
            Current = new PlannerSnapshot
            {
                Mode = PlannerMode.Progression,
                ObjectiveKind = PlannerObjectiveKind.Monster,
                Objective = $"Clear nearby monster pack: {nearestMonster.Name}",
                Explanation = BuildMonsterExplanation(context, observation, nearestMonster, playerPosition),
                TargetName = nearestMonster.Name,
                TargetDistance = distance,
                TargetVerticalDelta = playerPosition.HasValue ? MathF.Abs(nearestMonster.Position.Y - playerPosition.Value.Y) : null,
                CapturedAtUtc = now,
            };
            return;
        }

        if (nearestRequiredInteractable is not null)
        {
            var distance = playerPosition.HasValue
                ? Vector3.Distance(nearestRequiredInteractable.Position, playerPosition.Value)
                : (float?)null;
            Current = new PlannerSnapshot
            {
                Mode = PlannerMode.Progression,
                ObjectiveKind = GetProgressionInteractableObjectiveKind(nearestRequiredInteractable),
                Objective = $"Use progression interactable: {nearestRequiredInteractable.Name}",
                Explanation = $"No live monsters remain, so ADS can promote the nearest {nearestRequiredInteractable.Classification} progression interactable.",
                TargetName = nearestRequiredInteractable.Name,
                TargetDistance = distance,
                TargetVerticalDelta = playerPosition.HasValue ? MathF.Abs(nearestRequiredInteractable.Position.Y - playerPosition.Value.Y) : null,
                CapturedAtUtc = now,
            };
            return;
        }

        if (nearestMonster is null
            && nearestRequiredInteractable is null
            && nearestFollowTarget is not null)
        {
            var distance = playerPosition.HasValue
                ? Vector3.Distance(nearestFollowTarget.Position, playerPosition.Value)
                : (float?)null;
            Current = new PlannerSnapshot
            {
                Mode = PlannerMode.Progression,
                ObjectiveKind = PlannerObjectiveKind.FollowTarget,
                Objective = $"Follow live anchor: {nearestFollowTarget.Name}",
                Explanation = BuildFollowTargetExplanation(context, nearestFollowTarget, playerPosition),
                TargetName = nearestFollowTarget.Name,
                TargetDistance = distance,
                TargetVerticalDelta = playerPosition.HasValue ? MathF.Abs(nearestFollowTarget.Position.Y - playerPosition.Value.Y) : null,
                CapturedAtUtc = now,
            };
            return;
        }

        if (nearestMonster is null
            && nearestFollowTarget is null
            && nearestRequiredInteractable is null
            && dungeonFrontierService.CurrentTarget is { } frontierPoint
            && IsAuthoredFrontierTarget(frontierPoint))
        {
            Current = BuildFrontierSnapshot(context, observation, frontierPoint, playerPosition, now);
            return;
        }

        if (nearestMonster is null
            && nearestFollowTarget is null
            && nearestRequiredInteractable is null
            && dungeonFrontierService.TreasureFollowerDoorChaseHoldActive)
        {
            Current = new PlannerSnapshot
            {
                Mode = PlannerMode.Progression,
                ObjectiveKind = PlannerObjectiveKind.None,
                Objective = "Await treasure follower door chase gate",
                Explanation = string.IsNullOrWhiteSpace(dungeonFrontierService.CurrentLabelStatus)
                    ? "Treasure follower routing is waiting for the door-chase gate before selecting a passage door."
                    : dungeonFrontierService.CurrentLabelStatus,
                CapturedAtUtc = now,
            };
            return;
        }

        if (nearestMonster is null
            && nearestFollowTarget is null
            && nearestRequiredInteractable is null
            && dungeonFrontierService.HasRemainingManualDestinations)
        {
            Current = new PlannerSnapshot
            {
                Mode = PlannerMode.Progression,
                ObjectiveKind = PlannerObjectiveKind.None,
                Objective = "Await manual destination resolution",
                Explanation = $"Unvisited human-authored Map XZ / XYZ destinations still remain in {context.CurrentDuty?.EnglishName}, so ADS is refusing to backtrack to stale ghosts until frontier/manual resolution surfaces the next stage.",
                CapturedAtUtc = now,
            };
            return;
        }

        if (nearestMonster is null
            && nearestFollowTarget is null
            && nearestRequiredInteractable is null
            && nearestRuleBackedInteractableGhost is not null
            && playerPosition.HasValue)
        {
            var distance = Vector3.Distance(nearestRuleBackedInteractableGhost.Position, playerPosition.Value);
            var verticalDelta = MathF.Abs(nearestRuleBackedInteractableGhost.Position.Y - playerPosition.Value.Y);
            Current = new PlannerSnapshot
            {
                Mode = PlannerMode.Recovery,
                ObjectiveKind = PlannerObjectiveKind.InteractableGhost,
                Objective = $"Recover toward priority interactable ghost: {nearestRuleBackedInteractableGhost.Name}",
                Explanation = BuildRuleBackedInteractableGhostExplanation(
                    context,
                    nearestRuleBackedInteractableGhost,
                    distance,
                    verticalDelta),
                TargetName = nearestRuleBackedInteractableGhost.Name,
                TargetDistance = distance,
                TargetVerticalDelta = verticalDelta,
                CapturedAtUtc = now,
            };
            return;
        }

        var nearestMonsterGhost = playerPosition.HasValue
            ? observation.MonsterGhosts
                .Where(x => MatchesCurrentMap(context.MapId, x.MapId))
                .OrderBy(x => Vector3.Distance(x.Position, playerPosition.Value))
                .FirstOrDefault()
            : observation.MonsterGhosts.FirstOrDefault(x => MatchesCurrentMap(context.MapId, x.MapId));
        if (nearestMonsterGhost is not null)
        {
            Current = new PlannerSnapshot
            {
                Mode = PlannerMode.Recovery,
                ObjectiveKind = PlannerObjectiveKind.MonsterGhost,
                Objective = $"Advance toward monster ghost: {nearestMonsterGhost.Name}",
                Explanation = "No live monsters remain in range, so ADS is using the last seen monster ghost as a navigation assist.",
                TargetName = nearestMonsterGhost.Name,
                TargetDistance = playerPosition.HasValue ? Vector3.Distance(nearestMonsterGhost.Position, playerPosition.Value) : null,
                TargetVerticalDelta = playerPosition.HasValue ? MathF.Abs(nearestMonsterGhost.Position.Y - playerPosition.Value.Y) : null,
                CapturedAtUtc = now,
            };
            return;
        }

        if (nearestOptionalInteractable is not null)
        {
            Current = new PlannerSnapshot
            {
                Mode = PlannerMode.Progression,
                ObjectiveKind = PlannerObjectiveKind.OptionalInteractable,
                Objective = $"Check interactable: {nearestOptionalInteractable.Name}",
                Explanation = "Only live optional interactables remain, so ADS is holding them as a weak hint while stronger monster, progression, and frontier logic stays in control.",
                TargetName = nearestOptionalInteractable.Name,
                TargetDistance = playerPosition.HasValue ? Vector3.Distance(nearestOptionalInteractable.Position, playerPosition.Value) : null,
                TargetVerticalDelta = playerPosition.HasValue ? MathF.Abs(nearestOptionalInteractable.Position.Y - playerPosition.Value.Y) : null,
                CapturedAtUtc = now,
            };
            return;
        }

        var nearestInteractableGhost = playerPosition.HasValue
            ? observation.InteractableGhosts
                .Where(x => MatchesCurrentMap(context.MapId, x.MapId))
                .OrderBy(x => Vector3.Distance(x.Position, playerPosition.Value))
                .FirstOrDefault()
            : observation.InteractableGhosts.FirstOrDefault(x => MatchesCurrentMap(context.MapId, x.MapId));
        if (nearestInteractableGhost is not null)
        {
            Current = new PlannerSnapshot
            {
                Mode = PlannerMode.Recovery,
                ObjectiveKind = PlannerObjectiveKind.InteractableGhost,
                Objective = $"Revisit interactable ghost: {nearestInteractableGhost.Name}",
                Explanation = "Live truth is gone, so ADS is keeping the last interactable ghost as a recovery hint instead of treating it as authoritative truth.",
                TargetName = nearestInteractableGhost.Name,
                TargetDistance = playerPosition.HasValue ? Vector3.Distance(nearestInteractableGhost.Position, playerPosition.Value) : null,
                TargetVerticalDelta = playerPosition.HasValue ? MathF.Abs(nearestInteractableGhost.Position.Y - playerPosition.Value.Y) : null,
                CapturedAtUtc = now,
            };
            return;
        }

        if (nearestMonster is null
            && nearestFollowTarget is null
            && nearestRequiredInteractable is null
            && dungeonFrontierService.CurrentTarget is { } generatedFrontierPoint)
        {
            Current = BuildFrontierSnapshot(context, observation, generatedFrontierPoint, playerPosition, now);
            return;
        }

        Current = new PlannerSnapshot
        {
            Mode = ownershipMode == OwnershipMode.Observing ? PlannerMode.IdleObserve : PlannerMode.Progression,
            ObjectiveKind = PlannerObjectiveKind.None,
            Objective = "Await new duty state",
            Explanation = liveMonsterEligibility.Any(x => x.SuppressedByRuleGates)
                ? "Live monsters are visible, but all currently fail the active BattleNpc distance/Y rule gates, so ADS is holding until one becomes eligible again."
                : observation.LiveInteractables.Any(x => IsProgressionInteractable(context, x, playerPosition))
                ? "Live progression interactables are visible, but all currently fail the active distance/Y rule gates, so ADS is holding until a manual destination, live monster, or eligible interactable becomes available."
                : "No live monsters or interactables are currently visible. ADS is holding until the duty state changes again.",
            CapturedAtUtc = now,
        };
    }

    private ObservedMonster? GetBestBattleNpc(
        IEnumerable<BattleNpcPlanningEligibility> candidates)
    {
        return candidates
            .Where(x => x.IsEligibleBlocker)
            .OrderBy(x => x.EffectiveRule?.Priority ?? ObjectPriorityRuleService.DefaultPriority)
            .ThenBy(x => x.EffectiveRule is null ? 1 : 0)
            .ThenBy(x => x.Distance ?? float.MaxValue)
            .ThenBy(x => x.VerticalDelta ?? float.MaxValue)
            .Select(x => x.Monster)
            .FirstOrDefault();
    }

    private ObservedMonster? GetBestBossFightBattleNpc(
        IEnumerable<BattleNpcPlanningEligibility> candidates)
    {
        return candidates
            .Where(x => x.IsEligibleBlocker)
            .Where(x => x.EffectiveClassification == InteractableClass.BossFight)
            .OrderBy(x => x.EffectiveRule?.Priority ?? ObjectPriorityRuleService.DefaultPriority)
            .ThenBy(x => x.Distance ?? float.MaxValue)
            .ThenBy(x => x.VerticalDelta ?? float.MaxValue)
            .Select(x => x.Monster)
            .FirstOrDefault();
    }

    private string BuildMonsterExplanation(
        DutyContextSnapshot context,
        ObservationSnapshot observation,
        ObservedMonster monster,
        Vector3? playerPosition)
    {
        var distance = GetDistance(playerPosition, monster.Position);
        var verticalDelta = GetVerticalDelta(playerPosition, monster.Position);
        var rule = objectPriorityRuleService.GetEffectiveBattleNpcRule(context, monster, distance, verticalDelta);

        if (rule is not null)
        {
            return $"BattleNpc priority rule {rule.Priority} selected {monster.Name}. Monster-first bias remains active, but human-authored Required/priority monster rules now choose the kill target before distance tie-breaks.";
        }

        return $"Monster-first bias is active. {observation.LiveMonsters.Count} live monsters outrank optional interactables in {context.CurrentDuty?.EnglishName}.";
    }

    private string BuildBossFightExplanation(
        DutyContextSnapshot context,
        ObservedMonster monster,
        Vector3? playerPosition,
        bool combatActive)
    {
        var distance = GetDistance(playerPosition, monster.Position);
        var verticalDelta = GetVerticalDelta(playerPosition, monster.Position);
        var rule = objectPriorityRuleService.GetEffectiveBattleNpcRule(context, monster, distance, verticalDelta);
        var priorityText = rule is not null
            ? $"rule priority {rule.Priority}"
            : "its matched boss-fight rule";
        var combatText = combatActive
            ? "Combat is already active, but "
            : string.Empty;

        return $"{combatText}ADS selected live boss-fight target {monster.Name} via {priorityText}. BossFight BattleNpc rules beat nearby trash, treasure, ghosts, and frontier/manual follow-through once the rule distance/Y gates pass.";
    }

    private string BuildFollowTargetExplanation(
        DutyContextSnapshot context,
        ObservedMonster followTarget,
        Vector3? playerPosition)
    {
        var distance = GetDistance(playerPosition, followTarget.Position);
        var verticalDelta = GetVerticalDelta(playerPosition, followTarget.Position);
        var rule = objectPriorityRuleService.GetEffectiveBattleNpcRule(context, followTarget, distance, verticalDelta);
        var priorityText = rule is not null
            ? $" priority rule {rule.Priority}"
            : " a follow rule";

        return $"No live monsters or interactables are currently visible in {context.CurrentDuty?.EnglishName}, so ADS is following live rule-backed anchor {followTarget.Name} via{priorityText} instead of using ghost memory.";
    }

    private ObservedInteractable? GetBestInteractable(
        IEnumerable<ObservedInteractable> interactables,
        Vector3? playerPosition,
        DutyContextSnapshot context)
    {
        return interactables
            .Select(x => new
            {
                Interactable = x,
                Distance = GetDistance(playerPosition, x.Position),
                VerticalDelta = GetVerticalDelta(playerPosition, x.Position),
                AuthoredRule = objectPriorityRuleService.GetEligibleAuthoredLiveObjectRule(
                    context,
                    x,
                    GetDistance(playerPosition, x.Position),
                    GetVerticalDelta(playerPosition, x.Position)),
                Priority = objectPriorityRuleService.GetEffectivePriority(
                    context,
                    x,
                    GetDistance(playerPosition, x.Position),
                    GetVerticalDelta(playerPosition, x.Position)),
            })
            .Where(x => !objectPriorityRuleService.IsSuppressedByRuleGates(
                context,
                x.Interactable,
                x.Distance,
                x.VerticalDelta))
            .OrderBy(x => x.AuthoredRule is not null ? 0 : 1)
            .ThenBy(x => x.Priority)
            .ThenBy(x => x.Distance ?? float.MaxValue)
            .ThenBy(x => x.VerticalDelta ?? float.MaxValue)
            .Select(x => x.Interactable)
            .FirstOrDefault();
    }

    private bool IsProgressionInteractable(
        DutyContextSnapshot context,
        ObservedInteractable interactable,
        Vector3? playerPosition)
    {
        if (interactable.Classification is InteractableClass.Required
            or InteractableClass.CombatFriendly
            or InteractableClass.Expendable
            || (interactable.Classification == InteractableClass.TreasureDoor
                && dungeonFrontierService.TreasureDungeonRole != ADS.Models.TreasureDungeonRole.Follower))
        {
            return true;
        }

        if (interactable.Classification != InteractableClass.Optional)
            return false;

        var distance = GetDistance(playerPosition, interactable.Position);
        var verticalDelta = GetVerticalDelta(playerPosition, interactable.Position);
        return objectPriorityRuleService.GetEligibleAuthoredLiveObjectRule(context, interactable, distance, verticalDelta) is not null;
    }

    private static PlannerObjectiveKind GetProgressionInteractableObjectiveKind(ObservedInteractable interactable)
        => interactable.Classification switch
        {
            InteractableClass.CombatFriendly => PlannerObjectiveKind.CombatFriendlyInteractable,
            InteractableClass.Optional => PlannerObjectiveKind.OptionalInteractable,
            InteractableClass.Expendable => PlannerObjectiveKind.ExpendableInteractable,
            InteractableClass.TreasureDoor => PlannerObjectiveKind.TreasureDoor,
            _ => PlannerObjectiveKind.RequiredInteractable,
        };

    private ObservedInteractable? GetBestRuleBackedInteractableGhost(
        IEnumerable<ObservedInteractable> interactables,
        Vector3? playerPosition,
        DutyContextSnapshot context)
    {
        return interactables
            .Select(x => new
            {
                Interactable = x,
                Distance = GetDistance(playerPosition, x.Position),
                VerticalDelta = GetVerticalDelta(playerPosition, x.Position),
                Rule = objectPriorityRuleService.GetEffectiveRule(
                    context,
                    x,
                    GetDistance(playerPosition, x.Position),
                    GetVerticalDelta(playerPosition, x.Position)),
            })
            .Where(x => x.Rule is not null)
            .OrderBy(x => x.Rule!.Priority)
            .ThenBy(x => x.Distance ?? float.MaxValue)
            .ThenBy(x => x.VerticalDelta ?? float.MaxValue)
            .Select(x => x.Interactable)
            .FirstOrDefault();
    }

    private static float? GetDistance(Vector3? playerPosition, Vector3? targetPosition)
        => playerPosition.HasValue && targetPosition.HasValue
            ? Vector3.Distance(playerPosition.Value, targetPosition.Value)
            : null;

    private static float? GetVerticalDelta(Vector3? playerPosition, Vector3? targetPosition)
        => playerPosition.HasValue && targetPosition.HasValue
            ? MathF.Abs(targetPosition.Value.Y - playerPosition.Value.Y)
            : null;

    private static float? GetHorizontalDistance(Vector3? playerPosition, Vector3? targetPosition)
        => playerPosition.HasValue && targetPosition.HasValue
            ? MathF.Sqrt(
                ((targetPosition.Value.X - playerPosition.Value.X) * (targetPosition.Value.X - playerPosition.Value.X))
                + ((targetPosition.Value.Z - playerPosition.Value.Z) * (targetPosition.Value.Z - playerPosition.Value.Z)))
            : null;

    private bool ShouldPrioritizeRequiredInteractable(
        DutyContextSnapshot context,
        ObservedInteractable interactable,
        ObservedMonster nearestMonster,
        float interactableDistance,
        float monsterDistance,
        float interactableVerticalDelta,
        float monsterVerticalDelta)
    {
        var effectiveRule = objectPriorityRuleService.GetEffectiveRule(context, interactable, interactableDistance, interactableVerticalDelta);
        var nearestMonsterRule = objectPriorityRuleService.GetEffectiveBattleNpcRule(context, nearestMonster, monsterDistance, monsterVerticalDelta);
        if (effectiveRule is not null && nearestMonsterRule is not null)
        {
            if (effectiveRule.Priority < nearestMonsterRule.Priority)
                return true;

            if (effectiveRule.Priority > nearestMonsterRule.Priority)
                return false;
        }

        if (interactableDistance + RequiredInteractableMaterialLead < monsterDistance)
            return true;

        if (interactableVerticalDelta + RequiredInteractableVerticalSlack < monsterVerticalDelta
            && interactableDistance <= monsterDistance + RequiredInteractableDistanceTieWindow)
        {
            return true;
        }

        return interactable.Classification == InteractableClass.CombatFriendly && effectiveRule is not null;
    }

    private string BuildRequiredInteractableExplanation(
        DutyContextSnapshot context,
        ObservedInteractable interactable,
        ObservedMonster nearestMonster,
        float interactableDistance,
        float monsterDistance,
        float interactableVerticalDelta,
        float monsterVerticalDelta)
    {
        var effectiveRule = objectPriorityRuleService.GetEffectiveRule(context, interactable, interactableDistance, interactableVerticalDelta);
        var nearestMonsterRule = objectPriorityRuleService.GetEffectiveBattleNpcRule(context, nearestMonster, monsterDistance, monsterVerticalDelta);
        var nearestMonsterPriority = nearestMonsterRule?.Priority ?? ObjectPriorityRuleService.DefaultPriority;
        var rulePrefix = effectiveRule is not null
            ? $"Priority rule {effectiveRule.Priority} selected {interactable.Name} as the best live interactable in this duty. "
            : string.Empty;

        if (effectiveRule is not null
            && nearestMonsterRule is not null
            && effectiveRule.Priority < nearestMonsterPriority)
        {
            return $"{rulePrefix}Monster-first bias was overridden because this interactable's effective priority ({effectiveRule.Priority}) beats the nearest live monster's effective priority ({nearestMonsterPriority}).";
        }

        if (interactableDistance + RequiredInteractableMaterialLead < monsterDistance)
        {
            if (effectiveRule is not null && nearestMonsterRule is not null && effectiveRule.Priority == nearestMonsterPriority)
            {
                return $"{rulePrefix}Both sides have active priority rules at {effectiveRule.Priority}, so ADS spent the tie on closeness: the marked {interactable.Classification} interactable is materially closer ({interactableDistance:0.0}) than the nearest live monster ({monsterDistance:0.0}).";
            }

            return $"{rulePrefix}Monster-first bias was overridden because the marked {interactable.Classification} interactable is materially closer ({interactableDistance:0.0}) than the nearest live monster ({monsterDistance:0.0}).";
        }

        if (interactableVerticalDelta + RequiredInteractableVerticalSlack < monsterVerticalDelta
            && interactableDistance <= monsterDistance + RequiredInteractableDistanceTieWindow)
        {
            if (effectiveRule is not null && nearestMonsterRule is not null && effectiveRule.Priority == nearestMonsterPriority)
            {
                return $"{rulePrefix}Both sides have active priority rules at {effectiveRule.Priority}, so ADS spent the tie on Y-space: the marked {interactable.Classification} interactable is closer in Y-space ({interactableVerticalDelta:0.0} vs {monsterVerticalDelta:0.0}) while staying inside the distance tie window.";
            }

            return $"{rulePrefix}Monster-first bias was overridden because the marked {interactable.Classification} interactable is closer in Y-space ({interactableVerticalDelta:0.0} vs {monsterVerticalDelta:0.0}) while staying inside the distance tie window.";
        }

        return $"{rulePrefix}Combat-friendly classification keeps this interactable eligible to beat nearby monsters in the current progression step.";
    }

    private string BuildRuleBackedInteractableGhostExplanation(
        DutyContextSnapshot context,
        ObservedInteractable interactable,
        float distance,
        float verticalDelta)
    {
        var effectiveRule = objectPriorityRuleService.GetEffectiveRule(context, interactable, distance, verticalDelta);
        var priorityText = effectiveRule is not null
            ? $"priority rule {effectiveRule.Priority}"
            : "a priority rule";

        return $"No live monsters, follow anchors, or progression interactables are currently visible, but {priorityText} still matches the ghost of {interactable.Name} within the configured distance/Y gates ({distance:0.0}y, Y {verticalDelta:0.0}). ADS is using that rule-backed ghost as a recovery objective instead of toggling away from stronger live truth.";
    }

    private static bool ShouldSelectTreasureCoffer(
        float treasureHorizontalDistance,
        float treasureVerticalDelta,
        int treasurePriority,
        float? monsterDistance,
        int? monsterPriority,
        float? requiredDistance,
        int? requiredPriority)
    {
        if (treasureVerticalDelta > TreasureCofferVerticalCap)
            return false;

        if (treasureHorizontalDistance > TreasureCofferMaxHorizontalDistance)
            return false;

        if (monsterPriority.HasValue)
        {
            if (treasurePriority > monsterPriority.Value)
                return false;

            if (treasurePriority == monsterPriority.Value
                && monsterDistance is not null
                && treasureHorizontalDistance + TreasureCofferMaterialLead >= monsterDistance.Value)
            {
                return false;
            }
        }

        if (requiredPriority.HasValue)
        {
            if (treasurePriority < requiredPriority.Value)
                return true;

            if (treasurePriority > requiredPriority.Value)
                return false;
        }

        return requiredDistance is null || treasureHorizontalDistance + TreasureCofferMaterialLead < requiredDistance.Value;
    }

    private static string BuildTreasureCofferExplanation(
        float treasureDistance,
        float treasureHorizontalDistance,
        float treasureVerticalDelta,
        int treasurePriority,
        float? monsterDistance,
        int? monsterPriority,
        float? requiredDistance,
        int? requiredPriority)
    {
        var monsterText = monsterDistance.HasValue
            ? $"{monsterDistance.Value:0.0} XZ @ priority {monsterPriority ?? ObjectPriorityRuleService.DefaultPriority}"
            : "none";
        var requiredText = requiredDistance.HasValue
            ? $"{requiredDistance.Value:0.0} XZ @ priority {requiredPriority ?? ObjectPriorityRuleService.DefaultPriority}"
            : "none";
        var beatMonsterByPriority = monsterPriority.HasValue && treasurePriority < monsterPriority.Value;
        var beatRequiredByPriority = requiredPriority.HasValue && treasurePriority < requiredPriority.Value;

        if (beatMonsterByPriority || beatRequiredByPriority)
        {
            return $"Treasure-coffer scan is enabled, and the nearest coffer stayed inside the optional-coffer gate at XZ {treasureHorizontalDistance:0.0}/{TreasureCofferMaxHorizontalDistance:0.0}, 3D {treasureDistance:0.0}, Y {treasureVerticalDelta:0.0}. Its effective priority ({treasurePriority}) beat the nearest live monster ({monsterText}) and progression interactable ({requiredText}), so ADS selected the coffer before spending any distance tie-breaks.";
        }

        return $"Treasure-coffer scan is enabled, and the nearest coffer stayed inside the optional-coffer gate at XZ {treasureHorizontalDistance:0.0}/{TreasureCofferMaxHorizontalDistance:0.0}, 3D {treasureDistance:0.0}, Y {treasureVerticalDelta:0.0}. Its effective priority ({treasurePriority}) tied the nearest live monster ({monsterText}) and progression interactable ({requiredText}), so ADS spent the tie on the coffer lead margin.";
    }

    private bool TryBuildTreasureFollowerLootSnapshot(
        DutyContextSnapshot context,
        ObservationSnapshot observation,
        Vector3? playerPosition,
        DateTime now,
        bool considerTreasureCoffers,
        out PlannerSnapshot snapshot)
    {
        snapshot = default!;
        if (!considerTreasureCoffers
            || dungeonFrontierService.TreasureDungeonRole != ADS.Models.TreasureDungeonRole.Follower
            || context.InCombat
            || !playerPosition.HasValue
            || IsTreasureFollowerPreCombatStagingActive())
        {
            return false;
        }

        var selected = observation.LiveInteractables
            .Where(x => x.Classification == InteractableClass.TreasureCoffer)
            .Where(x => MatchesCurrentMap(context.MapId, x.MapId))
            .Where(x => !observationMemoryService.IsTreasureFollowerContainerChecked(x))
            .Select(x =>
            {
                var distance = Vector3.Distance(x.Position, playerPosition.Value);
                var horizontalDistance = GetHorizontalDistance(playerPosition.Value, x.Position) ?? distance;
                var verticalDelta = MathF.Abs(x.Position.Y - playerPosition.Value.Y);
                var priority = objectPriorityRuleService.GetEffectivePriority(context, x, distance, verticalDelta);
                return new
                {
                    Interactable = x,
                    Distance = distance,
                    HorizontalDistance = horizontalDistance,
                    VerticalDelta = verticalDelta,
                    Priority = priority,
                    SackRank = GetTreasureFollowerLootSackRank(x.Name),
                    IsSack = LooksLikeTreasureFollowerLootSackName(x.Name),
                };
            })
            .Where(x => x.HorizontalDistance <= TreasureCofferMaxHorizontalDistance)
            .Where(x => x.VerticalDelta <= TreasureCofferVerticalCap)
            .Where(x => !objectPriorityRuleService.IsSuppressedByRuleGates(
                context,
                x.Interactable,
                x.Distance,
                x.VerticalDelta))
            .OrderBy(x => x.SackRank)
            .ThenBy(x => x.Priority)
            .ThenBy(x => x.HorizontalDistance)
            .ThenBy(x => x.VerticalDelta)
            .FirstOrDefault();

        if (selected is null)
            return false;

        snapshot = new PlannerSnapshot
        {
            Mode = PlannerMode.Progression,
            ObjectiveKind = PlannerObjectiveKind.TreasureFollowerLoot,
            Objective = selected.IsSack
                ? $"Collect repeatable treasure follower sack: {selected.Interactable.Name}"
                : $"Check treasure follower container once: {selected.Interactable.Name}",
            Explanation = $"Treasure follower loot sweep is enabled, and {selected.Interactable.Name} stayed inside the follower loot gate at XZ {selected.HorizontalDistance:0.0}/{TreasureCofferMaxHorizontalDistance:0.0}, 3D {selected.Distance:0.0}, Y {selected.VerticalDelta:0.0}/{TreasureCofferVerticalCap:0.0}. Sacks are repeatable and stay eligible until they disappear; non-sack containers are one-shot and ADS ignores them after one successful check until duty reset. Sacks sort before other treasure containers (rank {selected.SackRank}), then priority {selected.Priority}, then distance, so ADS selected this loot before follower route movement.",
            TargetName = selected.Interactable.Name,
            TargetDistance = selected.Distance,
            TargetVerticalDelta = selected.VerticalDelta,
            CapturedAtUtc = now,
        };
        return true;
    }

    private bool IsTreasureFollowerPreCombatStagingActive()
        => dungeonFrontierService.CurrentTarget is { IsTreasureFollowerStagingPoint: true }
           || (dungeonFrontierService.TreasureFollowerDoorChaseHoldActive
               && (string.Equals(dungeonFrontierService.TreasureFollowerDoorChaseGateState, "StagedAtTreasureCoffer", StringComparison.Ordinal)
                   || string.Equals(dungeonFrontierService.TreasureFollowerDoorChaseGateState, "StagingTowardTreasureCoffer", StringComparison.Ordinal)
                   || string.Equals(dungeonFrontierService.TreasureFollowerDoorChaseGateState, "WaitingForRoomCombat", StringComparison.Ordinal)));

    private static int GetTreasureFollowerLootSackRank(string name)
    {
        var normalized = name.Trim();
        if (normalized.Contains("leather sack", StringComparison.OrdinalIgnoreCase))
            return 0;

        return LooksLikeTreasureFollowerLootSackName(normalized) ? 1 : 2;
    }

    private static bool LooksLikeTreasureFollowerLootSackName(string name)
        => name.Trim().Contains("sack", StringComparison.OrdinalIgnoreCase);

    private string BuildFrontierExplanation(
        DutyContextSnapshot context,
        DungeonFrontierPoint frontierPoint,
        ObservationSnapshot observation)
    {
        return dungeonFrontierService.CurrentMode switch
        {
            FrontierMode.MapXzDestination
                => frontierPoint.AllowCombatBypass
                    ? $"No live monsters, follow anchors, or eligible progression interactables are currently visible in {context.CurrentDuty?.EnglishName}, so ADS is using the next unvisited human-authored force-march Map XZ destination {frontierPoint.Name} ({FormatMapCoordinates(frontierPoint)}) before stale ghost recovery. This authored bypass waypoint keeps advancing even while incidental local combat is active and ghosts once the player is within 1y on X/Z."
                    : $"No live monsters, follow anchors, or eligible progression interactables are currently visible in {context.CurrentDuty?.EnglishName}, so ADS is using the next unvisited human-authored Map XZ destination {frontierPoint.Name} ({FormatMapCoordinates(frontierPoint)}) before stale ghost recovery. It navigates on the current player Y plane and ghosts the destination once the player is within 1y on X/Z.",
            FrontierMode.XyzDestination
                => frontierPoint.AllowCombatBypass
                    ? $"No live monsters, follow anchors, or eligible progression interactables are currently visible in {context.CurrentDuty?.EnglishName}, so ADS is using the next unvisited human-authored force-march XYZ destination {frontierPoint.Name} ({FormatWorldCoordinates(frontierPoint)}) before stale ghost recovery. This authored bypass waypoint keeps advancing even while incidental local combat is active and ghosts once execution reaches the 1y 3D arrival rule."
                    : $"No live monsters, follow anchors, or eligible progression interactables are currently visible in {context.CurrentDuty?.EnglishName}, so ADS is using the next unvisited human-authored XYZ destination {frontierPoint.Name} ({FormatWorldCoordinates(frontierPoint)}) before stale ghost recovery. It navigates to the authored world X/Y/Z point directly and ghosts the destination once execution reaches the 1y 3D arrival rule.",
            FrontierMode.TreasureDungeon
                => dungeonFrontierService.EffectiveTreasureDungeonRole == ADS.Models.TreasureDungeonRole.Follower
                    ? frontierPoint.IsTreasureFollowerStagingPoint
                        ? frontierPoint.TreasurePassageGroup == "StagingCoffer"
                            ? $"ADS inferred treasure follower mode from {dungeonFrontierService.TreasureDungeonRoleSource}. ADS is staging near the live treasure coffer for room {frontierPoint.TreasureRoomIndex} without interacting with it, then waits for room combat before any passage-door chase. Non-combat live targets do not preempt follower staging."
                            : $"ADS inferred treasure follower mode from {dungeonFrontierService.TreasureDungeonRoleSource}. ADS is staging near the inferred center of room {frontierPoint.TreasureRoomIndex}, then waits for room combat before any passage-door chase. Non-combat live targets do not preempt follower staging."
                    : frontierPoint.IsTreasurePassageCandidate
                        ? $"ADS inferred treasure follower mode from {dungeonFrontierService.TreasureDungeonRoleSource}. ADS is sweeping {frontierPoint.TreasureRouteSource} passage candidate {frontierPoint.Name} ({frontierPoint.TreasurePassageGroup}, room {frontierPoint.TreasureRoomIndex}) after staging and room-combat gates. Post-entry follower passage routing prefers same-floor live TreasureDoor targets, falls back to static same-floor route points, requires Y <= 5y, reaches candidates at XZ <= 4y, then uses treasure-door follow-through/jiggle without interacting. Retry cycle: {dungeonFrontierService.TreasureFollowerRetryCycle}."
                        : $"ADS inferred treasure follower mode from {dungeonFrontierService.TreasureDungeonRoleSource}. ADS is using static treasure route point {frontierPoint.Name} before current-room passage sweeps. Treasure route reach is XZ <= 4y, not true 3D XYZ."
                    : $"No live monsters, follow anchors, or eligible progression interactables are currently visible in {context.CurrentDuty?.EnglishName}, and ADS inferred map-opener/default treasure role from {dungeonFrontierService.TreasureDungeonRoleSource}. ADS is using the authored treasure-dungeon opener route point {frontierPoint.Name}. Treasure route reach is XZ <= 4y, not true 3D XYZ; frontier pre-sweep currently uses XZ <= 6y.",
            FrontierMode.HeadingScout
                => $"No live monsters, follow anchors, or eligible progression interactables are currently visible in {context.CurrentDuty?.EnglishName}, and Lumina produced 0 usable frontier labels for this territory. ADS is projecting a synthetic forward scout waypoint ({frontierPoint.Name}) from the last live-truth movement heading instead of backtracking to stale ghosts. Cached ghost counts remain {observation.MonsterGhosts.Count} monster / {observation.InteractableGhosts.Count} interactable.",
            _ => $"No live monsters, follow anchors, or eligible progression interactables are currently visible in {context.CurrentDuty?.EnglishName}, so ADS is using the next unvisited map label ({frontierPoint.Name}) as a forward frontier waypoint instead of backtracking to stale ghosts. Cached ghost counts remain {observation.MonsterGhosts.Count} monster / {observation.InteractableGhosts.Count} interactable. Frontier progress: {dungeonFrontierService.VisitedPoints}/{dungeonFrontierService.TotalPoints}.",
        };
    }

    private bool TryBuildTreasureFollowerRouteOwnershipSnapshot(
        DutyContextSnapshot context,
        ObservationSnapshot observation,
        Vector3? playerPosition,
        DateTime now,
        out PlannerSnapshot snapshot)
    {
        snapshot = default!;
        if (dungeonFrontierService.TreasureDungeonRole != ADS.Models.TreasureDungeonRole.Follower
            || context.InCombat)
        {
            return false;
        }

        if (dungeonFrontierService.CurrentMode == FrontierMode.TreasureDungeon
            && dungeonFrontierService.CurrentTarget is { } frontierPoint
            && (frontierPoint.IsTreasureRoutePoint
                || frontierPoint.IsTreasurePassageCandidate
                || frontierPoint.IsTreasureFollowerStagingPoint))
        {
            snapshot = BuildFrontierSnapshot(context, observation, frontierPoint, playerPosition, now);
            return true;
        }

        if (!dungeonFrontierService.TreasureFollowerRouteOwnershipActive)
            return false;

        var holdReason = string.IsNullOrWhiteSpace(dungeonFrontierService.TreasureFollowerRouteHoldReason)
            ? "DoorChaseGate"
            : dungeonFrontierService.TreasureFollowerRouteHoldReason;
        snapshot = new PlannerSnapshot
        {
            Mode = PlannerMode.Progression,
            ObjectiveKind = PlannerObjectiveKind.None,
            Objective = $"Await treasure follower route hold: {holdReason}",
            Explanation = string.IsNullOrWhiteSpace(dungeonFrontierService.CurrentLabelStatus)
                ? $"Treasure follower routing owns non-combat movement and is holding before selecting a passage door ({holdReason})."
                : $"{dungeonFrontierService.CurrentLabelStatus} Hold reason: {holdReason}.",
            CapturedAtUtc = now,
        };
        return true;
    }

    private PlannerSnapshot BuildFrontierSnapshot(
        DutyContextSnapshot context,
        ObservationSnapshot observation,
        DungeonFrontierPoint frontierPoint,
        Vector3? playerPosition,
        DateTime now)
    {
        var distance = playerPosition.HasValue
            ? GetManualOrFrontierDistance(playerPosition.Value, frontierPoint)
            : (float?)null;
        var verticalDelta = playerPosition.HasValue
            ? MathF.Abs(frontierPoint.Position.Y - playerPosition.Value.Y)
            : (float?)null;
        var isMapXzDestination = dungeonFrontierService.CurrentMode == FrontierMode.MapXzDestination;
        var isXyzDestination = dungeonFrontierService.CurrentMode == FrontierMode.XyzDestination;
        return new PlannerSnapshot
        {
            Mode = PlannerMode.Progression,
            ObjectiveKind = isXyzDestination
                ? GetManualDestinationObjectiveKind(frontierPoint)
                : isMapXzDestination
                    ? GetManualDestinationObjectiveKind(frontierPoint)
                    : PlannerObjectiveKind.Frontier,
            Objective = BuildFrontierObjective(frontierPoint, isMapXzDestination, isXyzDestination),
            Explanation = BuildFrontierExplanation(context, frontierPoint, observation),
            TargetName = frontierPoint.Name,
            TargetDistance = distance,
            TargetVerticalDelta = verticalDelta,
            CapturedAtUtc = now,
        };
    }

    private static string BuildFrontierObjective(
        DungeonFrontierPoint frontierPoint,
        bool isMapXzDestination,
        bool isXyzDestination)
    {
        if (frontierPoint.IsTreasureFollowerStagingPoint)
        {
            return $"Stage near treasure coffer: {frontierPoint.Name}";
        }

        if (isXyzDestination)
        {
            return frontierPoint.AllowCombatBypass
                ? $"Force-march toward XYZ destination: {frontierPoint.Name}"
                : $"Advance toward XYZ destination: {frontierPoint.Name}";
        }

        if (isMapXzDestination)
        {
            return frontierPoint.AllowCombatBypass
                ? $"Force-march toward map XZ destination: {frontierPoint.Name}"
                : $"Advance toward map XZ destination: {frontierPoint.Name}";
        }

        return $"Advance toward map frontier: {frontierPoint.Name}";
    }

    private PlannerSnapshot BuildForceMarchMonsterBypassSnapshot(
        DutyContextSnapshot context,
        DungeonFrontierPoint frontierPoint,
        ObservedMonster? nearestMonster,
        ObservedInteractable? nearestRequiredInteractable,
        Vector3? playerPosition,
        DateTime capturedAtUtc,
        bool inCombat)
    {
        var distance = playerPosition.HasValue
            ? GetManualDestinationDistance(playerPosition.Value, frontierPoint)
            : (float?)null;
        var verticalDelta = playerPosition.HasValue ? MathF.Abs(frontierPoint.Position.Y - playerPosition.Value.Y) : (float?)null;
        var manualLabel = GetManualDestinationLabel(frontierPoint);
        var monsterDistance = nearestMonster is not null && playerPosition.HasValue
            ? Vector3.Distance(nearestMonster.Position, playerPosition.Value)
            : (float?)null;
        var monsterVerticalDelta = nearestMonster is not null
            ? GetVerticalDelta(playerPosition, nearestMonster.Position)
            : null;
        var monsterPriority = nearestMonster is not null
            ? objectPriorityRuleService.GetEffectiveBattleNpcPriority(context, nearestMonster, monsterDistance, monsterVerticalDelta)
            : (int?)null;
        var requiredDistance = nearestRequiredInteractable is not null
            ? GetDistance(playerPosition, nearestRequiredInteractable.Position)
            : null;
        var requiredVerticalDelta = nearestRequiredInteractable is not null
            ? GetVerticalDelta(playerPosition, nearestRequiredInteractable.Position)
            : null;
        var requiredPriority = nearestRequiredInteractable is not null
            ? objectPriorityRuleService.GetEffectivePriority(context, nearestRequiredInteractable, requiredDistance, requiredVerticalDelta)
            : (int?)null;
        var combatContext = inCombat
            ? "Combat is already active"
            : nearestMonster is not null
                ? "Live monster pressure is still visible"
                : "No stronger live objective displaced this authored force-march step";
        var monsterSummary = nearestMonster is null
            ? "no live monster priority was resolved on this tick."
            : $"nearest live monster {nearestMonster.Name} currently resolves at priority {monsterPriority ?? ObjectPriorityRuleService.DefaultPriority} and distance {monsterDistance ?? 0f:0.0}.";
        var requiredSummary = nearestRequiredInteractable is null
            ? "No live progression interactable priority was resolved on this tick."
            : $"Nearest live progression interactable {nearestRequiredInteractable.Name} ({nearestRequiredInteractable.Classification}) currently resolves at priority {requiredPriority ?? ObjectPriorityRuleService.DefaultPriority} and distance {requiredDistance ?? 0f:0.0}.";

        return new PlannerSnapshot
        {
            Mode = PlannerMode.Progression,
            ObjectiveKind = GetManualDestinationObjectiveKind(frontierPoint),
            Objective = frontierPoint.IsManualXyzDestination
                ? $"Force-march toward XYZ destination: {frontierPoint.Name}"
                : $"Force-march toward map XZ destination: {frontierPoint.Name}",
            Explanation = $"{combatContext}, but human-authored {manualLabel} {frontierPoint.Name} is explicitly allowed to keep marching through incidental trash and priority-allowed live progression truth during this bypass segment. {monsterSummary} {requiredSummary}",
            TargetName = frontierPoint.Name,
            TargetDistance = distance,
            TargetVerticalDelta = verticalDelta,
            CapturedAtUtc = capturedAtUtc,
        };
    }

    private static string FormatMapCoordinates(DungeonFrontierPoint frontierPoint)
        => frontierPoint.MapCoordinates.HasValue
            ? $"{frontierPoint.MapCoordinates.Value.X:0.0}, {frontierPoint.MapCoordinates.Value.Y:0.0}"
            : "no map coordinates";

    private static string FormatWorldCoordinates(DungeonFrontierPoint frontierPoint)
        => $"{frontierPoint.Position.X:0.0}, {frontierPoint.Position.Y:0.0}, {frontierPoint.Position.Z:0.0}";

    private static float GetManualDestinationDistance(Vector3 playerPosition, DungeonFrontierPoint frontierPoint)
        => frontierPoint.IsManualXyzDestination
            ? Vector3.Distance(playerPosition, frontierPoint.Position)
            : GetHorizontalDistance(playerPosition, frontierPoint.Position) ?? 0f;

    private static PlannerObjectiveKind GetManualDestinationObjectiveKind(DungeonFrontierPoint frontierPoint)
        => frontierPoint.IsManualXyzDestination
            ? frontierPoint.AllowCombatBypass
                ? PlannerObjectiveKind.XyzForceMarchDestination
                : PlannerObjectiveKind.XyzDestination
            : frontierPoint.AllowCombatBypass
                ? PlannerObjectiveKind.MapXzForceMarchDestination
                : PlannerObjectiveKind.MapXzDestination;

    private static string GetManualDestinationLabel(DungeonFrontierPoint frontierPoint)
        => frontierPoint.IsManualXyzDestination
            ? frontierPoint.AllowCombatBypass ? "force-march XYZ destination" : "XYZ destination"
            : frontierPoint.AllowCombatBypass ? "force-march Map XZ destination" : "Map XZ destination";

    private static float GetManualOrFrontierDistance(Vector3 playerPosition, DungeonFrontierPoint frontierPoint)
        => frontierPoint.IsManualDestination
            ? GetManualDestinationDistance(playerPosition, frontierPoint)
            : GetHorizontalDistance(playerPosition, frontierPoint.Position) ?? 0f;

    private static bool IsAuthoredFrontierTarget(DungeonFrontierPoint frontierPoint)
        => frontierPoint.IsManualDestination
           || frontierPoint.IsTreasureRoutePoint
           || frontierPoint.IsLiveTreasureDoorCandidate
           || frontierPoint.IsTreasureFollowerStagingPoint;

    private static bool MatchesCurrentMap(uint activeMapId, uint candidateMapId)
        => activeMapId == 0
           || candidateMapId == 0
           || activeMapId == candidateMapId;

}
