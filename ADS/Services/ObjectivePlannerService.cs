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
    private const float TreasureCofferMaxHorizontalDistance = 30f;
    private const float TreasureCofferVerticalCap = 5f;
    private const float BattleNpcVerticalSanityCap = 100f;

    private readonly IObjectTable objectTable;
    private readonly ObjectPriorityRuleService objectPriorityRuleService;
    private readonly DungeonFrontierService dungeonFrontierService;

    public ObjectivePlannerService(
        IObjectTable objectTable,
        ObjectPriorityRuleService objectPriorityRuleService,
        DungeonFrontierService dungeonFrontierService)
    {
        this.objectTable = objectTable;
        this.objectPriorityRuleService = objectPriorityRuleService;
        this.dungeonFrontierService = dungeonFrontierService;
        Current = new PlannerSnapshot
        {
            Mode = PlannerMode.IdleObserve,
            ObjectiveKind = PlannerObjectiveKind.None,
            Objective = "Await supported duty",
            Explanation = "ADS is idle until a supported dungeon context appears.",
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

        if (!context.InDuty)
        {
            Current = new PlannerSnapshot
            {
                Mode = PlannerMode.IdleObserve,
                ObjectiveKind = PlannerObjectiveKind.None,
                Objective = "Await supported duty",
                Explanation = "ADS is outside duty. Ownership can be queued from outside, but observation starts only after duty entry.",
                CapturedAtUtc = now,
            };
            return;
        }

        if (!context.IsSupportedDuty)
        {
            Current = new PlannerSnapshot
            {
                Mode = PlannerMode.UnsupportedDuty,
                ObjectiveKind = PlannerObjectiveKind.None,
                Objective = "Catalog visible, execution unavailable",
                Explanation = "This 4-man dungeon is visible in the ADS catalog, but it does not have a supported observer/execution profile yet.",
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

        var playerPosition = objectTable.LocalPlayer?.Position;
        var nearestBossFightMonster = GetBestBossFightBattleNpc(observation.LiveMonsters, playerPosition, context);
        var nearestMonster = GetBestBattleNpc(observation.LiveMonsters, playerPosition, context);
        var nearestFollowTarget = GetBestBattleNpc(observation.LiveFollowTargets, playerPosition, context);
        var nearestRequiredInteractable = GetBestInteractable(
            observation.LiveInteractables.Where(IsProgressionInteractable),
            playerPosition,
            context);
        var nearestCombatFriendlyInteractable = GetBestInteractable(
            observation.LiveInteractables.Where(x => x.Classification == InteractableClass.CombatFriendly),
            playerPosition,
            context);
        var nearestTreasureCoffer = considerTreasureCoffers
            ? GetBestInteractable(
                observation.LiveInteractables.Where(x => x.Classification == InteractableClass.TreasureCoffer),
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
                .Where(IsProgressionInteractable),
            playerPosition,
            context);

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
            var nearestRequiredDistance = nearestRequiredInteractable is not null
                ? GetHorizontalDistance(playerPosition, nearestRequiredInteractable.Position)
                : (float?)null;

            if (ShouldSelectTreasureCoffer(
                    treasureHorizontalDistance,
                    treasureVerticalDelta,
                    nearestMonsterDistance,
                    nearestRequiredDistance))
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
                        nearestMonsterDistance,
                        nearestRequiredDistance),
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

        if (nearestMonster is null
            && nearestFollowTarget is null
            && dungeonFrontierService.CurrentMode is FrontierMode.MapXzDestination or FrontierMode.XyzDestination
            && dungeonFrontierService.CurrentTarget is { } manualDestinationTarget
            && ShouldPrioritizeManualDestinationBeforeProgression(context, manualDestinationTarget, nearestRequiredInteractable, playerPosition))
        {
            var distance = playerPosition.HasValue
                ? GetManualDestinationDistance(playerPosition.Value, manualDestinationTarget)
                : (float?)null;
            var verticalDelta = playerPosition.HasValue ? MathF.Abs(manualDestinationTarget.Position.Y - playerPosition.Value.Y) : (float?)null;
            var isXyzDestination = manualDestinationTarget.IsManualXyzDestination;
            Current = new PlannerSnapshot
            {
                Mode = PlannerMode.Progression,
                ObjectiveKind = isXyzDestination
                    ? PlannerObjectiveKind.XyzDestination
                    : PlannerObjectiveKind.MapXzDestination,
                Objective = isXyzDestination
                    ? $"Advance toward XYZ destination: {manualDestinationTarget.Name}"
                    : $"Advance toward map XZ destination: {manualDestinationTarget.Name}",
                Explanation = BuildManualDestinationPriorityExplanation(context, manualDestinationTarget, nearestRequiredInteractable, playerPosition),
                TargetName = manualDestinationTarget.Name,
                TargetDistance = distance,
                TargetVerticalDelta = verticalDelta,
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
            && dungeonFrontierService.CurrentTarget is { } frontierPoint)
        {
            var distance = playerPosition.HasValue
                ? GetManualOrFrontierDistance(playerPosition.Value, frontierPoint)
                : (float?)null;
            var verticalDelta = playerPosition.HasValue ? MathF.Abs(frontierPoint.Position.Y - playerPosition.Value.Y) : (float?)null;
            var isMapXzDestination = dungeonFrontierService.CurrentMode == FrontierMode.MapXzDestination;
            var isXyzDestination = dungeonFrontierService.CurrentMode == FrontierMode.XyzDestination;
            Current = new PlannerSnapshot
            {
                Mode = PlannerMode.Progression,
                ObjectiveKind = isXyzDestination
                    ? PlannerObjectiveKind.XyzDestination
                    : isMapXzDestination
                    ? PlannerObjectiveKind.MapXzDestination
                    : PlannerObjectiveKind.Frontier,
                Objective = isXyzDestination
                    ? $"Advance toward XYZ destination: {frontierPoint.Name}"
                    : isMapXzDestination
                    ? $"Advance toward map XZ destination: {frontierPoint.Name}"
                    : $"Advance toward map frontier: {frontierPoint.Name}",
                Explanation = BuildFrontierExplanation(context, frontierPoint, observation),
                TargetName = frontierPoint.Name,
                TargetDistance = distance,
                TargetVerticalDelta = verticalDelta,
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

        Current = new PlannerSnapshot
        {
            Mode = ownershipMode == OwnershipMode.Observing ? PlannerMode.IdleObserve : PlannerMode.Progression,
            ObjectiveKind = PlannerObjectiveKind.None,
            Objective = "Await new duty state",
            Explanation = observation.LiveMonsters.Any(x =>
                objectPriorityRuleService.IsBattleNpcSuppressedByRuleGates(
                    context,
                    x,
                    GetDistance(playerPosition, x.Position),
                    GetVerticalDelta(playerPosition, x.Position)))
                ? "Live monsters are visible, but all currently fail the active BattleNpc distance/Y rule gates, so ADS is holding until one becomes eligible again."
                : observation.LiveInteractables.Any(IsProgressionInteractable)
                ? "Live progression interactables are visible, but all currently fail the active distance/Y rule gates, so ADS is holding until a manual destination, live monster, or eligible interactable becomes available."
                : "No live monsters or interactables are currently visible. ADS is holding until the duty state changes again.",
            CapturedAtUtc = now,
        };
    }

    private ObservedMonster? GetBestBattleNpc(
        IEnumerable<ObservedMonster> monsters,
        Vector3? playerPosition,
        DutyContextSnapshot context)
    {
        return monsters
            .Select(x => new
            {
                Monster = x,
                Distance = GetDistance(playerPosition, x.Position),
                VerticalDelta = GetVerticalDelta(playerPosition, x.Position),
            })
            .Select(x => new
            {
                x.Monster,
                x.Distance,
                x.VerticalDelta,
                Rule = objectPriorityRuleService.GetEffectiveBattleNpcRule(
                    context,
                    x.Monster,
                    x.Distance,
                    x.VerticalDelta),
                SuppressedByRuleGates = objectPriorityRuleService.IsBattleNpcSuppressedByRuleGates(
                    context,
                    x.Monster,
                    x.Distance,
                    x.VerticalDelta),
            })
            .Where(x => !x.VerticalDelta.HasValue || x.VerticalDelta.Value <= BattleNpcVerticalSanityCap)
            .Where(x => !x.SuppressedByRuleGates)
            .OrderBy(x => x.Rule?.Priority ?? ObjectPriorityRuleService.DefaultPriority)
            .ThenBy(x => x.Rule is null ? 1 : 0)
            .ThenBy(x => x.Distance ?? float.MaxValue)
            .ThenBy(x => x.VerticalDelta ?? float.MaxValue)
            .Select(x => x.Monster)
            .FirstOrDefault();
    }

    private ObservedMonster? GetBestBossFightBattleNpc(
        IEnumerable<ObservedMonster> monsters,
        Vector3? playerPosition,
        DutyContextSnapshot context)
    {
        return monsters
            .Select(x => new
            {
                Monster = x,
                Distance = GetDistance(playerPosition, x.Position),
                VerticalDelta = GetVerticalDelta(playerPosition, x.Position),
            })
            .Select(x => new
            {
                x.Monster,
                x.Distance,
                x.VerticalDelta,
                Rule = objectPriorityRuleService.GetEffectiveBattleNpcRule(
                    context,
                    x.Monster,
                    x.Distance,
                    x.VerticalDelta),
                Classification = objectPriorityRuleService.GetEffectiveBattleNpcClassification(
                    context,
                    x.Monster,
                    x.Distance,
                    x.VerticalDelta),
                SuppressedByRuleGates = objectPriorityRuleService.IsBattleNpcSuppressedByRuleGates(
                    context,
                    x.Monster,
                    x.Distance,
                    x.VerticalDelta),
            })
            .Where(x => !x.VerticalDelta.HasValue || x.VerticalDelta.Value <= BattleNpcVerticalSanityCap)
            .Where(x => !x.SuppressedByRuleGates)
            .Where(x => x.Classification == InteractableClass.BossFight)
            .OrderBy(x => x.Rule?.Priority ?? ObjectPriorityRuleService.DefaultPriority)
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
            .OrderBy(x => x.Priority)
            .ThenBy(x => x.Distance ?? float.MaxValue)
            .ThenBy(x => x.VerticalDelta ?? float.MaxValue)
            .Select(x => x.Interactable)
            .FirstOrDefault();
    }

    private static bool IsProgressionInteractable(ObservedInteractable interactable)
        => interactable.Classification is InteractableClass.Required
            or InteractableClass.CombatFriendly
            or InteractableClass.Expendable;

    private static PlannerObjectiveKind GetProgressionInteractableObjectiveKind(ObservedInteractable interactable)
        => interactable.Classification switch
        {
            InteractableClass.CombatFriendly => PlannerObjectiveKind.CombatFriendlyInteractable,
            InteractableClass.Expendable => PlannerObjectiveKind.ExpendableInteractable,
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

    private bool ShouldPrioritizeManualDestinationBeforeProgression(
        DutyContextSnapshot context,
        DungeonFrontierPoint manualDestinationTarget,
        ObservedInteractable? nearestRequiredInteractable,
        Vector3? playerPosition)
    {
        if (!manualDestinationTarget.IsManualDestination)
            return false;

        if (nearestRequiredInteractable is null)
            return false;

        var distance = GetDistance(playerPosition, nearestRequiredInteractable.Position);
        var verticalDelta = GetVerticalDelta(playerPosition, nearestRequiredInteractable.Position);
        var nearestRequiredPriority = objectPriorityRuleService.GetEffectivePriority(
            context,
            nearestRequiredInteractable,
            distance,
            verticalDelta);
        return ShouldManualDestinationBeatProgressionInteractable(
            manualDestinationTarget.Priority,
            nearestRequiredInteractable,
            nearestRequiredPriority);
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

    private string BuildManualDestinationPriorityExplanation(
        DutyContextSnapshot context,
        DungeonFrontierPoint frontierPoint,
        ObservedInteractable? deferredInteractable,
        Vector3? playerPosition)
    {
        if (deferredInteractable is null)
            return BuildFrontierExplanation(context, frontierPoint, ObservationSnapshot.Empty);

        var distance = GetDistance(playerPosition, deferredInteractable.Position);
        var verticalDelta = GetVerticalDelta(playerPosition, deferredInteractable.Position);
        var deferredPriority = objectPriorityRuleService.GetEffectivePriority(
            context,
            deferredInteractable,
            distance,
            verticalDelta);
        var manualLabel = frontierPoint.IsManualXyzDestination ? "XYZ destination" : "Map XZ destination";
        if (frontierPoint.Priority == deferredPriority
            && deferredInteractable.Classification is InteractableClass.Expendable or InteractableClass.Optional)
        {
            return $"Human-authored {manualLabel} {frontierPoint.Name} ties the currently visible {deferredInteractable.Classification} progression interactable {deferredInteractable.Name} at priority ({deferredPriority}), so ADS spends the tie on the staging waypoint instead of the lower-value live interactable.";
        }

        return $"Human-authored {manualLabel} {frontierPoint.Name} has better priority ({frontierPoint.Priority}) than the best currently visible progression interactable {deferredInteractable.Name} ({deferredPriority}), so ADS is using the staging waypoint first instead of going directly to that interactable.";
    }

    private static bool ShouldManualDestinationBeatProgressionInteractable(
        int manualPriority,
        ObservedInteractable interactable,
        int interactablePriority)
    {
        if (manualPriority < interactablePriority)
            return true;

        if (manualPriority > interactablePriority)
            return false;

        return interactable.Classification is InteractableClass.Expendable or InteractableClass.Optional;
    }

    private static bool ShouldSelectTreasureCoffer(
        float treasureHorizontalDistance,
        float treasureVerticalDelta,
        float? monsterDistance,
        float? requiredDistance)
    {
        if (treasureVerticalDelta > TreasureCofferVerticalCap)
            return false;

        if (treasureHorizontalDistance > TreasureCofferMaxHorizontalDistance)
            return false;

        if (monsterDistance is not null && treasureHorizontalDistance + TreasureCofferMaterialLead >= monsterDistance.Value)
            return false;

        return requiredDistance is null || treasureHorizontalDistance + TreasureCofferMaterialLead < requiredDistance.Value;
    }

    private static string BuildTreasureCofferExplanation(
        float treasureDistance,
        float treasureHorizontalDistance,
        float treasureVerticalDelta,
        float? monsterDistance,
        float? requiredDistance)
    {
        var monsterText = monsterDistance.HasValue ? $"{monsterDistance.Value:0.0} XZ" : "none";
        var requiredText = requiredDistance.HasValue ? $"{requiredDistance.Value:0.0} XZ" : "none";

        return $"Treasure-coffer scan is enabled, and the nearest coffer stayed inside the optional-coffer gate at XZ {treasureHorizontalDistance:0.0}/{TreasureCofferMaxHorizontalDistance:0.0}, 3D {treasureDistance:0.0}, Y {treasureVerticalDelta:0.0}. It beat the nearest live monster ({monsterText}) and required interactable ({requiredText}) by the coffer lead margin.";
    }

    private string BuildFrontierExplanation(
        DutyContextSnapshot context,
        DungeonFrontierPoint frontierPoint,
        ObservationSnapshot observation)
    {
        return dungeonFrontierService.CurrentMode switch
        {
            FrontierMode.MapXzDestination
                => $"No live monsters, follow anchors, or eligible progression interactables are currently visible in {context.CurrentDuty?.EnglishName}, so ADS is using the next unvisited human-authored Map XZ destination {frontierPoint.Name} ({FormatMapCoordinates(frontierPoint)}) before stale ghost recovery. It navigates on the current player Y plane and ghosts the destination once the player is within 1y on X/Z.",
            FrontierMode.XyzDestination
                => $"No live monsters, follow anchors, or eligible progression interactables are currently visible in {context.CurrentDuty?.EnglishName}, so ADS is using the next unvisited human-authored XYZ destination {frontierPoint.Name} ({FormatWorldCoordinates(frontierPoint)}) before stale ghost recovery. It navigates to the authored world X/Y/Z point directly and ghosts the destination once execution reaches the 1y 3D arrival rule.",
            FrontierMode.HeadingScout
                => $"No live monsters, follow anchors, or eligible progression interactables are currently visible in {context.CurrentDuty?.EnglishName}, and Lumina produced 0 usable frontier labels for this territory. ADS is projecting a synthetic forward scout waypoint ({frontierPoint.Name}) from the last live-truth movement heading instead of backtracking to stale ghosts. Cached ghost counts remain {observation.MonsterGhosts.Count} monster / {observation.InteractableGhosts.Count} interactable.",
            _ => $"No live monsters, follow anchors, or eligible progression interactables are currently visible in {context.CurrentDuty?.EnglishName}, so ADS is using the next unvisited map label ({frontierPoint.Name}) as a forward frontier waypoint instead of backtracking to stale ghosts. Cached ghost counts remain {observation.MonsterGhosts.Count} monster / {observation.InteractableGhosts.Count} interactable. Frontier progress: {dungeonFrontierService.VisitedPoints}/{dungeonFrontierService.TotalPoints}.",
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

    private static float GetManualOrFrontierDistance(Vector3 playerPosition, DungeonFrontierPoint frontierPoint)
        => frontierPoint.IsManualDestination
            ? GetManualDestinationDistance(playerPosition, frontierPoint)
            : GetHorizontalDistance(playerPosition, frontierPoint.Position) ?? 0f;

    private static bool MatchesCurrentMap(uint activeMapId, uint candidateMapId)
        => activeMapId == 0
           || candidateMapId == 0
           || activeMapId == candidateMapId;

}
