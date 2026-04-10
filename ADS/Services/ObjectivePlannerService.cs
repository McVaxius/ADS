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

    private readonly IObjectTable objectTable;
    private readonly ObjectPriorityRuleService objectPriorityRuleService;

    public ObjectivePlannerService(IObjectTable objectTable, ObjectPriorityRuleService objectPriorityRuleService)
    {
        this.objectTable = objectTable;
        this.objectPriorityRuleService = objectPriorityRuleService;
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
        var nearestMonster = GetNearestMonster(observation.LiveMonsters, playerPosition);
        var nearestRequiredInteractable = GetBestInteractable(
            observation.LiveInteractables.Where(x => x.Classification is InteractableClass.Required or InteractableClass.CombatFriendly),
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
        var nearestRuleBackedInteractableGhost = GetBestRuleBackedInteractableGhost(
            observation.InteractableGhosts.Where(x => x.Classification is InteractableClass.Required or InteractableClass.CombatFriendly),
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

        if (nearestTreasureCoffer is not null && playerPosition.HasValue)
        {
            var treasureDistance = Vector3.Distance(nearestTreasureCoffer.Position, playerPosition.Value);
            var treasureVerticalDelta = MathF.Abs(nearestTreasureCoffer.Position.Y - playerPosition.Value.Y);
            var nearestMonsterDistance = nearestMonster is not null
                ? Vector3.Distance(nearestMonster.Position, playerPosition.Value)
                : (float?)null;
            var nearestRequiredDistance = nearestRequiredInteractable is not null
                ? Vector3.Distance(nearestRequiredInteractable.Position, playerPosition.Value)
                : (float?)null;

            if (ShouldSelectTreasureCoffer(
                    treasureDistance,
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
                    interactableDistance,
                    monsterDistance,
                    interactableVerticalDelta,
                    monsterVerticalDelta))
            {
                var explanation = BuildRequiredInteractableExplanation(
                    context,
                    nearestRequiredInteractable,
                    interactableDistance,
                    monsterDistance,
                    interactableVerticalDelta,
                    monsterVerticalDelta);
                Current = new PlannerSnapshot
                {
                    Mode = PlannerMode.Progression,
                    ObjectiveKind = nearestRequiredInteractable.Classification == InteractableClass.CombatFriendly
                        ? PlannerObjectiveKind.CombatFriendlyInteractable
                        : PlannerObjectiveKind.RequiredInteractable,
                    Objective = $"Use required interactable: {nearestRequiredInteractable.Name}",
                    Explanation = explanation,
                    TargetName = nearestRequiredInteractable.Name,
                    TargetDistance = interactableDistance,
                    TargetVerticalDelta = interactableVerticalDelta,
                    CapturedAtUtc = now,
                };
                return;
            }
        }

        if (nearestRuleBackedInteractableGhost is not null && playerPosition.HasValue)
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
                    verticalDelta,
                    nearestMonster is not null ? Vector3.Distance(nearestMonster.Position, playerPosition.Value) : null),
                TargetName = nearestRuleBackedInteractableGhost.Name,
                TargetDistance = distance,
                TargetVerticalDelta = verticalDelta,
                CapturedAtUtc = now,
            };
            return;
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
                Explanation = $"Monster-first bias is active. {observation.LiveMonsters.Count} live monsters outrank optional interactables in {context.CurrentDuty?.EnglishName}.",
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
                ObjectiveKind = nearestRequiredInteractable.Classification == InteractableClass.CombatFriendly
                    ? PlannerObjectiveKind.CombatFriendlyInteractable
                    : PlannerObjectiveKind.RequiredInteractable,
                Objective = $"Use required interactable: {nearestRequiredInteractable.Name}",
                Explanation = "No live monsters remain, so ADS can promote the nearest required progression interactable.",
                TargetName = nearestRequiredInteractable.Name,
                TargetDistance = distance,
                TargetVerticalDelta = playerPosition.HasValue ? MathF.Abs(nearestRequiredInteractable.Position.Y - playerPosition.Value.Y) : null,
                CapturedAtUtc = now,
            };
            return;
        }

        var nearestMonsterGhost = playerPosition.HasValue
            ? observation.MonsterGhosts.OrderBy(x => Vector3.Distance(x.Position, playerPosition.Value)).FirstOrDefault()
            : observation.MonsterGhosts.FirstOrDefault();
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

        var nearestLiveInteractable = playerPosition.HasValue
            ? observation.LiveInteractables.OrderBy(x => Vector3.Distance(x.Position, playerPosition.Value)).FirstOrDefault()
            : observation.LiveInteractables.FirstOrDefault();
        if (nearestLiveInteractable is not null)
        {
            Current = new PlannerSnapshot
            {
                Mode = PlannerMode.Progression,
                ObjectiveKind = PlannerObjectiveKind.OptionalInteractable,
                Objective = $"Check interactable: {nearestLiveInteractable.Name}",
                Explanation = "Only live interactables remain, so ADS is ready to advance progression state once execution logic expands.",
                TargetName = nearestLiveInteractable.Name,
                TargetDistance = playerPosition.HasValue ? Vector3.Distance(nearestLiveInteractable.Position, playerPosition.Value) : null,
                TargetVerticalDelta = playerPosition.HasValue ? MathF.Abs(nearestLiveInteractable.Position.Y - playerPosition.Value.Y) : null,
                CapturedAtUtc = now,
            };
            return;
        }

        var nearestInteractableGhost = playerPosition.HasValue
            ? observation.InteractableGhosts.OrderBy(x => Vector3.Distance(x.Position, playerPosition.Value)).FirstOrDefault()
            : observation.InteractableGhosts.FirstOrDefault();
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
            Explanation = "No live monsters or interactables are currently visible. ADS is holding until the duty state changes again.",
            CapturedAtUtc = now,
        };
    }

    private static ObservedMonster? GetNearestMonster(IEnumerable<ObservedMonster> monsters, Vector3? playerPosition)
        => playerPosition.HasValue
            ? monsters.OrderBy(x => Vector3.Distance(x.Position, playerPosition.Value)).FirstOrDefault()
            : monsters.FirstOrDefault();

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
            .Where(x => x.Rule is not null && x.Rule.Priority < DefaultPriorityForExplanation)
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

    private bool ShouldPrioritizeRequiredInteractable(
        DutyContextSnapshot context,
        ObservedInteractable interactable,
        float interactableDistance,
        float monsterDistance,
        float interactableVerticalDelta,
        float monsterVerticalDelta)
    {
        var effectiveRule = objectPriorityRuleService.GetEffectiveRule(context, interactable, interactableDistance, interactableVerticalDelta);
        if (effectiveRule is not null && effectiveRule.Priority < DefaultPriorityForExplanation)
            return true;

        if (interactableDistance + RequiredInteractableMaterialLead < monsterDistance)
            return true;

        if (interactableVerticalDelta + RequiredInteractableVerticalSlack < monsterVerticalDelta
            && interactableDistance <= monsterDistance + RequiredInteractableDistanceTieWindow)
        {
            return true;
        }

        if (interactable.Classification != InteractableClass.CombatFriendly)
            return false;

        return effectiveRule is not null && effectiveRule.Priority < DefaultPriorityForExplanation;
    }

    private string BuildRequiredInteractableExplanation(
        DutyContextSnapshot context,
        ObservedInteractable interactable,
        float interactableDistance,
        float monsterDistance,
        float interactableVerticalDelta,
        float monsterVerticalDelta)
    {
        var effectiveRule = objectPriorityRuleService.GetEffectiveRule(context, interactable, interactableDistance, interactableVerticalDelta);
        var rulePrefix = effectiveRule is not null && effectiveRule.Priority < DefaultPriorityForExplanation
            ? $"Priority rule {effectiveRule.Priority} selected {interactable.Name} before other interactables in this duty. "
            : string.Empty;

        if (effectiveRule is not null && effectiveRule.Priority < DefaultPriorityForExplanation)
        {
            return $"{rulePrefix}Monster-first bias was overridden because this interactable has an explicit human-authored duty rule.";
        }

        if (interactableDistance + RequiredInteractableMaterialLead < monsterDistance)
        {
            return $"{rulePrefix}Monster-first bias was overridden because the marked {interactable.Classification} interactable is materially closer ({interactableDistance:0.0}) than the nearest live monster ({monsterDistance:0.0}).";
        }

        if (interactableVerticalDelta + RequiredInteractableVerticalSlack < monsterVerticalDelta
            && interactableDistance <= monsterDistance + RequiredInteractableDistanceTieWindow)
        {
            return $"{rulePrefix}Monster-first bias was overridden because the marked {interactable.Classification} interactable is closer in Y-space ({interactableVerticalDelta:0.0} vs {monsterVerticalDelta:0.0}) while staying inside the distance tie window.";
        }

        return $"{rulePrefix}Combat-friendly classification keeps this interactable eligible to beat nearby monsters in the current progression step.";
    }

    private string BuildRuleBackedInteractableGhostExplanation(
        DutyContextSnapshot context,
        ObservedInteractable interactable,
        float distance,
        float verticalDelta,
        float? monsterDistance)
    {
        var effectiveRule = objectPriorityRuleService.GetEffectiveRule(context, interactable, distance, verticalDelta);
        var priorityText = effectiveRule is not null
            ? $"priority rule {effectiveRule.Priority}"
            : "a priority rule";
        var monsterText = monsterDistance.HasValue ? monsterDistance.Value.ToString("0.0") : "none";

        return $"Live truth no longer contains {interactable.Name}, but {priorityText} still matches its ghost within the configured distance/Y gates ({distance:0.0}y, Y {verticalDelta:0.0}). ADS is using that ghost as a recovery objective before continuing monster-first flow. Nearest live monster distance: {monsterText}.";
    }

    private static bool ShouldSelectTreasureCoffer(
        float treasureDistance,
        float? monsterDistance,
        float? requiredDistance)
    {
        if (monsterDistance is not null && treasureDistance >= monsterDistance.Value)
            return false;

        return requiredDistance is null || treasureDistance < requiredDistance.Value;
    }

    private static string BuildTreasureCofferExplanation(
        float treasureDistance,
        float? monsterDistance,
        float? requiredDistance)
    {
        var monsterText = monsterDistance.HasValue ? monsterDistance.Value.ToString("0.0") : "none";
        var requiredText = requiredDistance.HasValue ? requiredDistance.Value.ToString("0.0") : "none";

        return $"Treasure-coffer scan is enabled, and the nearest coffer is the closest current target at {treasureDistance:0.0}, ahead of the nearest live monster ({monsterText}) and required interactable ({requiredText}).";
    }

    private const int DefaultPriorityForExplanation = 1000;
}
