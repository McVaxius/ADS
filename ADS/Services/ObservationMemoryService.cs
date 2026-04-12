using System.Numerics;
using ADS.Models;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;

namespace ADS.Services;

public sealed class ObservationMemoryService
{
    private static readonly TimeSpan TreasureSuppressionDuration = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SkippedTreasureSuppressionDuration = TimeSpan.FromMinutes(5);
    private const float RetiredRecoveryClusterSuppressRadius = 12.0f;
    private const float RecoveryResetTeleportDistance = 40.0f;

    private static readonly string[] GenericRequiredTokens =
    [
        "gate",
        "lever",
        "switch",
        "portal",
        "device",
        "terminal",
        "key",
        "seal",
        "photocell",
        "cannon",
        "orb",
    ];

    private static readonly string[] CombatFriendlyTokens =
    [
        "cannon",
        "searchlight",
        "photocell",
        "device",
    ];

    private static readonly string[] ExpendableTokens =
    [
        "shortcut",
        "return",
        "teleporter",
    ];

    private static readonly Dictionary<string, string[]> DutySpecificRequiredTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        ["The Tam-Tara Deepcroft"] = ["orb", "seal", "gate", "portal"],
        ["The Thousand Maws of Toto-Rak"] = ["photocell", "magitek", "switch", "barrier", "web"],
        ["Brayflox's Longstop"] = ["key", "device", "gate"],
        ["The Stone Vigil"] = ["cannon", "switch", "gate", "key"],
    };

    private readonly IObjectTable objectTable;
    private readonly IPluginLog log;
    private readonly ObjectPriorityRuleService objectPriorityRuleService;
    private readonly Dictionary<string, ObservedMonster> knownMonsters = [];
    private readonly Dictionary<string, ObservedInteractable> knownInteractables = [];
    private readonly Dictionary<string, DateTime> treasureSuppressionUntil = [];
    private readonly Dictionary<string, ObservedInteractable> usedProgressionInteractables = [];
    private readonly List<Vector3> retiredMonsterGhostClusters = [];
    private readonly List<Vector3> retiredInteractableGhostClusters = [];
    private uint activeDutyKey;
    private bool loggedReset;
    private Vector3? lastPlayerPosition;

    public ObservationMemoryService(IObjectTable objectTable, IPluginLog log, ObjectPriorityRuleService objectPriorityRuleService)
    {
        this.objectTable = objectTable;
        this.log = log;
        this.objectPriorityRuleService = objectPriorityRuleService;
        Current = ObservationSnapshot.Empty;
    }

    public ObservationSnapshot Current { get; private set; }

    public void Update(DutyContextSnapshot context, bool considerTreasureCoffers)
    {
        var dutyKey = context.ContentFinderConditionId != 0 ? context.ContentFinderConditionId : context.TerritoryTypeId;
        if (dutyKey != activeDutyKey)
        {
            Reset();
            activeDutyKey = dutyKey;
        }

        if (!context.PluginEnabled || !context.IsLoggedIn || !context.InDuty || !context.IsSupportedDuty)
        {
            if (!loggedReset && (knownMonsters.Count > 0 || knownInteractables.Count > 0))
            {
                log.Debug("[ADS] Observation memory parked because ADS is outside a supported observation context.");
                loggedReset = true;
            }

            Current = ObservationSnapshot.Empty;
            return;
        }

        loggedReset = false;
        HandleRecoveryClusterReset(context);
        if (context.IsUnsafeTransition)
        {
            Current = new ObservationSnapshot
            {
                LiveMonsters = [],
                LiveFollowTargets = [],
                MonsterGhosts = knownMonsters.Values
                    .Where(x => !objectPriorityRuleService.ShouldIgnoreObject(context, ObjectKind.BattleNpc, x.DataId, x.Name, x.Position, x.MapId))
                    .Where(x => !objectPriorityRuleService.ShouldFollowObject(context, ObjectKind.BattleNpc, x.DataId, x.Name, x.Position, x.MapId))
                    .Where(x => !IsSuppressedByRetiredMonsterCluster(x.Position))
                    .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                LiveInteractables = [],
                InteractableGhosts = knownInteractables.Values
                    .Where(x => !objectPriorityRuleService.ShouldIgnoreInteractable(context, x))
                    .Where(x => !IsSuppressedInteractionGhost(x))
                    .Where(x => !IsSuppressedByRetiredInteractableCluster(x.Position))
                    .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
            };
            return;
        }

        var liveMonsters = new Dictionary<string, ObservedMonster>(StringComparer.Ordinal);
        var liveFollowTargets = new Dictionary<string, ObservedMonster>(StringComparer.Ordinal);
        var liveInteractables = new Dictionary<string, ObservedInteractable>(StringComparer.Ordinal);
        var now = DateTime.UtcNow;
        var playerPosition = objectTable.LocalPlayer?.Position;
        CleanupExpiredTreasureSuppressions(now);

        foreach (var gameObject in objectTable)
        {
            if (gameObject is null)
                continue;

            var name = gameObject.Name.TextValue.Trim();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            switch (gameObject.ObjectKind)
            {
                case ObjectKind.BattleNpc:
                    {
                        var monsterKey = BuildKey(gameObject);
                        if (objectPriorityRuleService.ShouldSuppressOffLayerBattleNpcTruth(
                                context,
                                gameObject.BaseId,
                                name,
                                gameObject.Position))
                        {
                            knownMonsters.Remove(monsterKey);
                            break;
                        }

                        if (objectPriorityRuleService.ShouldIgnoreObject(
                                context,
                                gameObject.ObjectKind,
                                gameObject.BaseId,
                                name,
                                gameObject.Position,
                                context.MapId,
                                GetDistance(playerPosition, gameObject.Position),
                                GetVerticalDelta(playerPosition, gameObject.Position)))
                        {
                            knownMonsters.Remove(monsterKey);
                            break;
                        }

                        if (objectPriorityRuleService.ShouldFollowObject(
                                context,
                                gameObject.ObjectKind,
                                gameObject.BaseId,
                                name,
                                gameObject.Position,
                                context.MapId,
                                GetDistance(playerPosition, gameObject.Position),
                                GetVerticalDelta(playerPosition, gameObject.Position)))
                        {
                            knownMonsters.Remove(monsterKey);
                            if (!gameObject.IsTargetable || IsDeadMonster(gameObject))
                                break;

                            var followTarget = CreateMonster(gameObject, name, context.MapId, now);
                            liveFollowTargets[followTarget.Key] = followTarget;
                            break;
                        }

                        if (TryGetBattleNpcDirectInteractClassification(context, gameObject, name, out var battleNpcInteractClassification))
                        {
                            knownMonsters.Remove(monsterKey);
                            if (!gameObject.IsTargetable)
                                break;

                            if (IsDurablySuppressedInteractableClass(battleNpcInteractClassification)
                                && TryCreateUsedProgressionInteractable(context, gameObject, name, battleNpcInteractClassification, now, out var usedBattleNpcInteractable))
                            {
                                knownInteractables[usedBattleNpcInteractable!.Key] = usedBattleNpcInteractable;
                                break;
                            }

                            var battleNpcInteractable = CreateInteractable(gameObject, name, context.MapId, battleNpcInteractClassification, now);
                            liveInteractables[battleNpcInteractable.Key] = battleNpcInteractable;
                            knownInteractables[battleNpcInteractable.Key] = battleNpcInteractable;
                            break;
                        }

                        if (IsDeadMonster(gameObject))
                        {
                            var deadMonster = CreateMonster(gameObject, name, context.MapId, now);
                            knownMonsters[deadMonster.Key] = deadMonster;
                            break;
                        }

                        if (!gameObject.IsTargetable)
                            break;

                        var monster = CreateMonster(gameObject, name, context.MapId, now);
                        liveMonsters[monster.Key] = monster;
                        knownMonsters[monster.Key] = monster;
                        break;
                    }
                case ObjectKind.EventObj:
                    {
                        if (!gameObject.IsTargetable)
                            break;

                        var interactableKey = BuildKey(gameObject);
                        var classification = ClassifyInteractable(gameObject, name, context);
                        if (classification == InteractableClass.Ignored)
                        {
                            knownInteractables.Remove(interactableKey);
                            break;
                        }

                        if (IsDurablySuppressedInteractableClass(classification)
                            && TryCreateUsedProgressionInteractable(context, gameObject, name, classification, now, out var usedInteractable))
                        {
                            knownInteractables[usedInteractable!.Key] = usedInteractable;
                            break;
                        }

                        var interactable = CreateInteractable(gameObject, name, context.MapId, classification, now);
                        liveInteractables[interactable.Key] = interactable;
                        knownInteractables[interactable.Key] = interactable;
                        break;
                    }
                case ObjectKind.Treasure when considerTreasureCoffers:
                    {
                        var treasureKey = BuildKey(gameObject);
                        if (objectPriorityRuleService.ShouldIgnoreInteractable(context, gameObject.ObjectKind, gameObject.BaseId, name, gameObject.Position, context.MapId))
                        {
                            knownInteractables.Remove(treasureKey);
                            treasureSuppressionUntil.Remove(treasureKey);
                            break;
                        }

                        if (treasureSuppressionUntil.TryGetValue(treasureKey, out var suppressedUntil) && suppressedUntil > now)
                        {
                            knownInteractables[treasureKey] = CreateTreasureCoffer(gameObject, name, context.MapId, now, GhostReason.Consumed);
                            break;
                        }

                        if (!gameObject.IsTargetable)
                            break;

                        var interactable = CreateTreasureCoffer(gameObject, name, context.MapId, now);
                        liveInteractables[interactable.Key] = interactable;
                        knownInteractables[interactable.Key] = interactable;
                        break;
                    }
            }
        }

        var monsterGhosts = knownMonsters.Values
            .Where(x => !liveMonsters.ContainsKey(x.Key))
            .Where(x => !objectPriorityRuleService.ShouldIgnoreObject(context, ObjectKind.BattleNpc, x.DataId, x.Name, x.Position, x.MapId))
            .Where(x => !objectPriorityRuleService.ShouldFollowObject(context, ObjectKind.BattleNpc, x.DataId, x.Name, x.Position, x.MapId))
            .Where(x => !IsSuppressedByRetiredMonsterCluster(x.Position))
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var interactableGhosts = knownInteractables.Values
            .Where(x => !liveInteractables.ContainsKey(x.Key))
            .Where(x => !objectPriorityRuleService.ShouldIgnoreInteractable(context, x))
            .Where(x => !IsSuppressedInteractionGhost(x))
            .Where(x => !IsSuppressedByRetiredInteractableCluster(x.Position))
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Current = new ObservationSnapshot
        {
            LiveMonsters = liveMonsters.Values.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            LiveFollowTargets = liveFollowTargets.Values.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            MonsterGhosts = monsterGhosts,
            LiveInteractables = liveInteractables.Values.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            InteractableGhosts = interactableGhosts,
        };
    }

    public void Reset()
    {
        knownMonsters.Clear();
        knownInteractables.Clear();
        treasureSuppressionUntil.Clear();
        usedProgressionInteractables.Clear();
        retiredMonsterGhostClusters.Clear();
        retiredInteractableGhostClusters.Clear();
        lastPlayerPosition = null;
        Current = ObservationSnapshot.Empty;
    }

    public void MarkTreasureInteractionSent(ObservedInteractable interactable)
        => MarkTreasureSuppressed(interactable, TreasureSuppressionDuration, "interaction sent");

    public void MarkTreasureCofferSkipped(ObservedInteractable interactable, string reason)
        => MarkTreasureSuppressed(interactable, SkippedTreasureSuppressionDuration, reason);

    private void MarkTreasureSuppressed(ObservedInteractable interactable, TimeSpan duration, string reason)
    {
        if (interactable.Classification != InteractableClass.TreasureCoffer)
            return;

        treasureSuppressionUntil[interactable.Key] = DateTime.UtcNow + duration;
        knownInteractables[interactable.Key] = new ObservedInteractable
        {
            Key = interactable.Key,
            GameObjectId = interactable.GameObjectId,
            DataId = interactable.DataId,
            MapId = interactable.MapId,
            ObjectKind = interactable.ObjectKind,
            Name = interactable.Name,
            Position = interactable.Position,
            LastSeenUtc = DateTime.UtcNow,
            Classification = interactable.Classification,
            GhostReason = GhostReason.Consumed,
        };
        log.Information($"[ADS] Suppressing treasure coffer {interactable.Name} for {duration.TotalSeconds:0}s after {reason}.");
    }

    public void MarkProgressionInteractionSent(DutyContextSnapshot context, ObservedInteractable interactable)
    {
        if (!IsDurablySuppressedInteractableClass(interactable.Classification))
            return;

        var spatialKey = BuildProgressionUseKey(context, interactable.ObjectKind, interactable.DataId, interactable.Name, interactable.Position);
        if (usedProgressionInteractables.ContainsKey(spatialKey))
            return;

        var usedInteractable = CreateTransitionUsedInteractable(spatialKey, interactable);
        usedProgressionInteractables[spatialKey] = usedInteractable;
        knownInteractables.Remove(interactable.Key);
        knownInteractables[usedInteractable.Key] = usedInteractable;
        log.Information($"[ADS] Marked {interactable.Name} at {Quantize(interactable.Position)} as used; suppressing this interactable position until duty reset.");
    }

    public int RetireNearbyRecoveryGhosts(PlannerObjectiveKind objectiveKind, Vector3 center, float radius)
    {
        return objectiveKind switch
        {
            PlannerObjectiveKind.MonsterGhost => RetireNearbyMonsterGhosts(center, radius),
            PlannerObjectiveKind.InteractableGhost => RetireNearbyInteractableGhosts(center, radius),
            _ => 0,
        };
    }

    private static ObservedMonster CreateMonster(IGameObject gameObject, string name, uint mapId, DateTime now)
        => new()
        {
            Key = BuildKey(gameObject),
            GameObjectId = gameObject.GameObjectId,
            DataId = gameObject.BaseId,
            MapId = mapId,
            Name = name,
            Position = gameObject.Position,
            LastSeenUtc = now,
        };

    private static ObservedInteractable CreateInteractable(IGameObject gameObject, string name, uint mapId, InteractableClass classification, DateTime now)
        => new()
        {
            Key = BuildKey(gameObject),
            GameObjectId = gameObject.GameObjectId,
            DataId = gameObject.BaseId,
            MapId = mapId,
            ObjectKind = gameObject.ObjectKind,
            Name = name,
            Position = gameObject.Position,
            LastSeenUtc = now,
            Classification = classification,
            GhostReason = GhostReason.SeenPreviously,
        };

    private static ObservedInteractable CreateTreasureCoffer(IGameObject gameObject, string name, uint mapId, DateTime now, GhostReason ghostReason = GhostReason.SeenPreviously)
        => new()
        {
            Key = BuildKey(gameObject),
            GameObjectId = gameObject.GameObjectId,
            DataId = gameObject.BaseId,
            MapId = mapId,
            ObjectKind = gameObject.ObjectKind,
            Name = name,
            Position = gameObject.Position,
            LastSeenUtc = now,
            Classification = InteractableClass.TreasureCoffer,
            GhostReason = ghostReason,
        };

    private static ObservedInteractable CreateTransitionUsedInteractable(string spatialKey, ObservedInteractable interactable)
        => new()
        {
            Key = $"used:{spatialKey}",
            GameObjectId = interactable.GameObjectId,
            DataId = interactable.DataId,
            MapId = interactable.MapId,
            ObjectKind = interactable.ObjectKind,
            Name = interactable.Name,
            Position = interactable.Position,
            LastSeenUtc = DateTime.UtcNow,
            Classification = interactable.Classification,
            GhostReason = GhostReason.TransitionUsed,
        };

    private static ObservedInteractable CreateTransitionUsedInteractable(string spatialKey, IGameObject gameObject, uint mapId, string name, InteractableClass classification, DateTime now)
        => new()
        {
            Key = $"used:{spatialKey}",
            GameObjectId = gameObject.GameObjectId,
            DataId = gameObject.BaseId,
            MapId = mapId,
            ObjectKind = gameObject.ObjectKind,
            Name = name,
            Position = gameObject.Position,
            LastSeenUtc = now,
            Classification = classification,
            GhostReason = GhostReason.TransitionUsed,
        };

    private bool TryCreateUsedProgressionInteractable(
        DutyContextSnapshot context,
        IGameObject gameObject,
        string name,
        InteractableClass classification,
        DateTime now,
        out ObservedInteractable? interactable)
    {
        var spatialKey = BuildProgressionUseKey(context, gameObject.ObjectKind, gameObject.BaseId, name, gameObject.Position);
        if (!usedProgressionInteractables.ContainsKey(spatialKey))
        {
            interactable = null;
            return false;
        }

        interactable = CreateTransitionUsedInteractable(spatialKey, gameObject, context.MapId, name, classification, now);
        return true;
    }

    private static string BuildKey(IGameObject gameObject)
    {
        if (gameObject.GameObjectId != 0)
            return $"{gameObject.ObjectKind}:{gameObject.GameObjectId}";

        return $"{gameObject.ObjectKind}:{gameObject.BaseId}:{Quantize(gameObject.Position)}";
    }

    private static string Quantize(Vector3 value)
        => $"{MathF.Round(value.X, 0):0},{MathF.Round(value.Y, 0):0},{MathF.Round(value.Z, 0):0}";

    private static string BuildProgressionUseKey(DutyContextSnapshot context, ObjectKind objectKind, uint baseId, string name, Vector3 position)
        => $"{context.ContentFinderConditionId}:{context.TerritoryTypeId}:{objectKind}:{baseId}:{name}:{Quantize(position)}";

    private InteractableClass ClassifyInteractable(IGameObject gameObject, string name, DutyContextSnapshot context)
    {
        if (objectPriorityRuleService.TryGetClassificationOverride(context, gameObject.ObjectKind, gameObject.BaseId, name, out var overrideClassification, gameObject.Position, context.MapId))
        {
            if (overrideClassification is InteractableClass.Follow or InteractableClass.MapXzDestination or InteractableClass.BossFight)
                return InteractableClass.Ignored;

            return overrideClassification;
        }

        var loweredName = name.ToLowerInvariant();
        if (ExpendableTokens.Any(loweredName.Contains))
            return InteractableClass.Expendable;

        if (CombatFriendlyTokens.Any(loweredName.Contains))
            return InteractableClass.CombatFriendly;

        if (context.CurrentDuty is not null
            && DutySpecificRequiredTokens.TryGetValue(context.CurrentDuty.EnglishName, out var dutyTokens)
            && dutyTokens.Any(loweredName.Contains))
        {
            return InteractableClass.Required;
        }

        return GenericRequiredTokens.Any(loweredName.Contains)
            ? InteractableClass.Required
            : InteractableClass.Optional;
    }

    // Keep the BattleNpc direct-interact seam intentionally narrow so
    // existing Required/BossFight kill-priority rules continue to behave as monsters.
    private bool TryGetBattleNpcDirectInteractClassification(
        DutyContextSnapshot context,
        IGameObject gameObject,
        string name,
        out InteractableClass classification)
    {
        classification = default;
        return objectPriorityRuleService.TryGetClassificationOverride(context, gameObject.ObjectKind, gameObject.BaseId, name, out classification, gameObject.Position, context.MapId)
               && classification == InteractableClass.CombatFriendly;
    }

    private static bool IsDurablySuppressedInteractableClass(InteractableClass classification)
        => classification is InteractableClass.Required or InteractableClass.CombatFriendly or InteractableClass.Expendable;

    private static bool IsSuppressedInteractionGhost(ObservedInteractable interactable)
        => interactable.GhostReason is GhostReason.Consumed or GhostReason.TransitionUsed;

    private static float? GetDistance(Vector3? playerPosition, Vector3 targetPosition)
        => playerPosition.HasValue ? Vector3.Distance(playerPosition.Value, targetPosition) : null;

    private static float? GetVerticalDelta(Vector3? playerPosition, Vector3 targetPosition)
        => playerPosition.HasValue ? MathF.Abs(targetPosition.Y - playerPosition.Value.Y) : null;

    private void CleanupExpiredTreasureSuppressions(DateTime now)
    {
        foreach (var key in treasureSuppressionUntil
                     .Where(x => x.Value <= now)
                     .Select(x => x.Key)
                     .ToList())
        {
            treasureSuppressionUntil.Remove(key);
        }
    }

    private static bool IsDeadMonster(IGameObject gameObject)
        => gameObject is IBattleNpc battleNpc && battleNpc.CurrentHp <= 1;

    private int RetireNearbyMonsterGhosts(Vector3 center, float radius)
    {
        AddRetiredMonsterCluster(center);
        var keys = knownMonsters.Values
            .Where(x => Vector3.Distance(x.Position, center) <= radius)
            .Select(x => x.Key)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        foreach (var key in keys)
            knownMonsters.Remove(key);

        if (keys.Count > 0)
        {
            log.Information($"[ADS] Retired {keys.Count} nearby monster ghost hint(s) around recovery area {Quantize(center)}.");
        }

        return keys.Count;
    }

    private int RetireNearbyInteractableGhosts(Vector3 center, float radius)
    {
        AddRetiredInteractableCluster(center);
        var keys = knownInteractables.Values
            .Where(x => Vector3.Distance(x.Position, center) <= radius)
            .Select(x => x.Key)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        foreach (var key in keys)
            knownInteractables.Remove(key);

        if (keys.Count > 0)
        {
            log.Information($"[ADS] Retired {keys.Count} nearby interactable ghost hint(s) around recovery area {Quantize(center)}.");
        }

        return keys.Count;
    }

    private void HandleRecoveryClusterReset(DutyContextSnapshot context)
    {
        var playerPosition = objectTable.LocalPlayer?.Position;
        if (!playerPosition.HasValue)
            return;

        if (lastPlayerPosition.HasValue
            && !context.IsUnsafeTransition
            && Vector3.Distance(lastPlayerPosition.Value, playerPosition.Value) >= RecoveryResetTeleportDistance)
        {
            var retiredCount = retiredMonsterGhostClusters.Count + retiredInteractableGhostClusters.Count;
            if (retiredCount > 0)
            {
                retiredMonsterGhostClusters.Clear();
                retiredInteractableGhostClusters.Clear();
                log.Information($"[ADS] Cleared {retiredCount} retired recovery ghost cluster(s) after a large duty relocation.");
            }

        }

        lastPlayerPosition = playerPosition.Value;
    }

    private bool IsSuppressedByRetiredMonsterCluster(Vector3 position)
        => retiredMonsterGhostClusters.Any(x => Vector3.Distance(x, position) <= RetiredRecoveryClusterSuppressRadius);

    private bool IsSuppressedByRetiredInteractableCluster(Vector3 position)
        => retiredInteractableGhostClusters.Any(x => Vector3.Distance(x, position) <= RetiredRecoveryClusterSuppressRadius);

    private void AddRetiredMonsterCluster(Vector3 center)
    {
        if (retiredMonsterGhostClusters.Any(x => Vector3.Distance(x, center) <= RetiredRecoveryClusterSuppressRadius))
            return;

        retiredMonsterGhostClusters.Add(center);
    }

    private void AddRetiredInteractableCluster(Vector3 center)
    {
        if (retiredInteractableGhostClusters.Any(x => Vector3.Distance(x, center) <= RetiredRecoveryClusterSuppressRadius))
            return;

        retiredInteractableGhostClusters.Add(center);
    }
}
