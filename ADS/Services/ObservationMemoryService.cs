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
    private readonly IPartyList partyList;
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

    public ObservationMemoryService(IObjectTable objectTable, IPartyList partyList, IPluginLog log, ObjectPriorityRuleService objectPriorityRuleService)
    {
        this.objectTable = objectTable;
        this.partyList = partyList;
        this.log = log;
        this.objectPriorityRuleService = objectPriorityRuleService;
        Current = ObservationSnapshot.Empty;
    }

    public ObservationSnapshot Current { get; private set; }

    public void Update(DutyContextSnapshot context, bool considerTreasureCoffers)
    {
        if (!context.PluginEnabled || !context.IsLoggedIn || !context.InInstancedDuty)
        {
            if (!loggedReset && (knownMonsters.Count > 0 || knownInteractables.Count > 0))
            {
                log.Debug("[ADS] Observation memory parked because ADS is outside instanced duty.");
            }

            if (activeDutyKey != 0
                || knownMonsters.Count > 0
                || knownInteractables.Count > 0
                || usedProgressionInteractables.Count > 0
                || retiredMonsterGhostClusters.Count > 0
                || retiredInteractableGhostClusters.Count > 0
                || lastPlayerPosition.HasValue)
            {
                Reset();
                activeDutyKey = 0;
            }

            loggedReset = true;
            Current = ObservationSnapshot.Empty;
            return;
        }

        var dutyKey = context.ContentFinderConditionId != 0 ? context.ContentFinderConditionId : context.TerritoryTypeId;
        if (dutyKey != activeDutyKey)
        {
            Reset();
            activeDutyKey = dutyKey;
        }

        loggedReset = false;
        var partyMembers = BuildPartyMemberSnapshot();
        HandleRecoveryClusterReset(context);
        if (context.IsUnsafeTransition)
        {
            Current = new ObservationSnapshot
            {
                LiveMonsters = [],
                LiveFollowTargets = [],
                MonsterGhosts = knownMonsters.Values
                    .Where(x => !IsPartyMemberObservation(x, partyMembers))
                    .Where(x => !objectPriorityRuleService.ShouldIgnoreObject(context, ObjectKind.BattleNpc, x.DataId, x.Name, x.Position, x.MapId))
                    .Where(x => !objectPriorityRuleService.ShouldFollowObject(context, ObjectKind.BattleNpc, x.DataId, x.Name, x.Position, x.MapId))
                    .Where(x => !IsSuppressedByRetiredMonsterCluster(x.Position))
                    .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                LiveInteractables = [],
                InteractableGhosts = knownInteractables.Values
                    .Where(x => !IsPartyMemberObservation(x, partyMembers))
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
            var objectKey = BuildKey(gameObject);
            if (IsPartyMemberGameObject(gameObject, name, partyMembers))
            {
                knownMonsters.Remove(objectKey);
                knownInteractables.Remove(objectKey);
                treasureSuppressionUntil.Remove(objectKey);
                continue;
            }

            if (string.IsNullOrWhiteSpace(name))
                continue;

            switch (gameObject.ObjectKind)
            {
                case ObjectKind.BattleNpc:
                    {
                        if (objectPriorityRuleService.ShouldSuppressOffLayerBattleNpcTruth(
                                context,
                                gameObject.BaseId,
                                name,
                                gameObject.Position))
                        {
                            knownMonsters.Remove(objectKey);
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
                            knownMonsters.Remove(objectKey);
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
                            knownMonsters.Remove(objectKey);
                            if (!gameObject.IsTargetable || IsDeadMonster(gameObject))
                                break;

                            var followTarget = CreateMonster(gameObject, name, context.MapId, now);
                            liveFollowTargets[followTarget.Key] = followTarget;
                            break;
                        }

                        if (TryGetBattleNpcDirectInteractClassification(context, gameObject, name, out var battleNpcInteractClassification))
                        {
                            knownMonsters.Remove(objectKey);
                            if (!gameObject.IsTargetable)
                                break;

                            if (ShouldDurablySuppressInteractable(context, name, battleNpcInteractClassification)
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
                case ObjectKind.EventNpc:
                    {
                        if (!gameObject.IsTargetable)
                            break;

                        var classification = ClassifyInteractable(gameObject, name, context);
                        if (classification == InteractableClass.Ignored)
                        {
                            knownInteractables.Remove(objectKey);
                            break;
                        }

                        if (ShouldDurablySuppressInteractable(context, name, classification)
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
                        if (objectPriorityRuleService.ShouldIgnoreInteractable(context, gameObject.ObjectKind, gameObject.BaseId, name, gameObject.Position, context.MapId))
                        {
                            knownInteractables.Remove(objectKey);
                            treasureSuppressionUntil.Remove(objectKey);
                            break;
                        }

                        var classification = ClassifyTreasureInteractable(gameObject, name, context);
                        if (classification == InteractableClass.Ignored)
                        {
                            knownInteractables.Remove(objectKey);
                            treasureSuppressionUntil.Remove(objectKey);
                            break;
                        }

                        if (classification == InteractableClass.TreasureCoffer
                            && treasureSuppressionUntil.TryGetValue(objectKey, out var suppressedUntil)
                            && suppressedUntil > now)
                        {
                            knownInteractables[objectKey] = CreateInteractable(gameObject, name, context.MapId, classification, now, GhostReason.Consumed);
                            break;
                        }

                        if (!gameObject.IsTargetable)
                            break;

                        if (classification != InteractableClass.TreasureCoffer)
                            treasureSuppressionUntil.Remove(objectKey);

                        if (ShouldDurablySuppressInteractable(context, name, classification)
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
            }
        }

        var monsterGhosts = knownMonsters.Values
            .Where(x => !liveMonsters.ContainsKey(x.Key))
            .Where(x => !IsPartyMemberObservation(x, partyMembers))
            .Where(x => !objectPriorityRuleService.ShouldIgnoreObject(context, ObjectKind.BattleNpc, x.DataId, x.Name, x.Position, x.MapId))
            .Where(x => !objectPriorityRuleService.ShouldFollowObject(context, ObjectKind.BattleNpc, x.DataId, x.Name, x.Position, x.MapId))
            .Where(x => !IsSuppressedByRetiredMonsterCluster(x.Position))
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var interactableGhosts = knownInteractables.Values
            .Where(x => !liveInteractables.ContainsKey(x.Key))
            .Where(x => !IsPartyMemberObservation(x, partyMembers))
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

        if (!ShouldDurablySuppressInteractable(context, interactable.Name, interactable.Classification))
        {
            var consumedInteractable = CreateConsumedInteractable(interactable);
            knownInteractables[consumedInteractable.Key] = consumedInteractable;
            log.Information($"[ADS] Marked repeatable progression interactable {interactable.Name} at {Quantize(interactable.Position)} as consumed without durable spatial suppression.");
            return;
        }

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

    private static ObservedInteractable CreateInteractable(
        IGameObject gameObject,
        string name,
        uint mapId,
        InteractableClass classification,
        DateTime now,
        GhostReason ghostReason = GhostReason.SeenPreviously)
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
            GhostReason = ghostReason,
        };

    private static ObservedInteractable CreateTreasureCoffer(IGameObject gameObject, string name, uint mapId, DateTime now, GhostReason ghostReason = GhostReason.SeenPreviously)
        => CreateInteractable(gameObject, name, mapId, InteractableClass.TreasureCoffer, now, ghostReason);

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

    private static ObservedInteractable CreateConsumedInteractable(ObservedInteractable interactable)
        => new()
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
            if (overrideClassification is InteractableClass.Follow
                or InteractableClass.MapXzDestination
                or InteractableClass.MapXzForceMarch
                or InteractableClass.XYZ
                or InteractableClass.XYZForceMarch
                or InteractableClass.BossFight)
                return InteractableClass.Ignored;

            return overrideClassification;
        }

        var loweredName = name.ToLowerInvariant();
        if (TreasureDungeonData.TryGetInteractableClassification(context.TerritoryTypeId, name, out var treasureClassification))
            return treasureClassification;

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

    private InteractableClass ClassifyTreasureInteractable(IGameObject gameObject, string name, DutyContextSnapshot context)
    {
        if (objectPriorityRuleService.TryGetClassificationOverride(context, gameObject.ObjectKind, gameObject.BaseId, name, out var overrideClassification, gameObject.Position, context.MapId))
        {
            if (overrideClassification is InteractableClass.Follow
                or InteractableClass.MapXzDestination
                or InteractableClass.MapXzForceMarch
                or InteractableClass.XYZ
                or InteractableClass.XYZForceMarch
                or InteractableClass.BossFight)
            {
                return InteractableClass.Ignored;
            }

            return overrideClassification;
        }

        if (TreasureDungeonData.TryGetInteractableClassification(context.TerritoryTypeId, name, out var treasureClassification))
            return treasureClassification;

        return InteractableClass.TreasureCoffer;
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
        => classification is InteractableClass.Required or InteractableClass.CombatFriendly or InteractableClass.Expendable or InteractableClass.TreasureDoor;

    private static bool ShouldDurablySuppressInteractable(DutyContextSnapshot context, string name, InteractableClass classification)
        => IsDurablySuppressedInteractableClass(classification)
            && !TreasureDungeonData.IsRepeatableProgressionInteractable(context.TerritoryTypeId, name);

    private static bool IsSuppressedInteractionGhost(ObservedInteractable interactable)
        => interactable.GhostReason is GhostReason.Consumed or GhostReason.TransitionUsed;

    private static float? GetDistance(Vector3? playerPosition, Vector3 targetPosition)
        => playerPosition.HasValue ? Vector3.Distance(playerPosition.Value, targetPosition) : null;

    private static float? GetVerticalDelta(Vector3? playerPosition, Vector3 targetPosition)
        => playerPosition.HasValue ? MathF.Abs(targetPosition.Y - playerPosition.Value.Y) : null;

    private PartyMemberSnapshot BuildPartyMemberSnapshot()
    {
        var snapshot = new PartyMemberSnapshot();
        foreach (var member in partyList)
        {
            var gameObject = member.GameObject;
            if (gameObject is not null && IsValidGameObjectId(gameObject.GameObjectId))
                snapshot.GameObjectIds.Add(gameObject.GameObjectId);

            if (IsValidGameObjectId(member.EntityId))
                snapshot.GameObjectIds.Add(member.EntityId);

            var name = NormalizePartyMemberName(member.Name.TextValue);
            if (!string.IsNullOrWhiteSpace(name))
                snapshot.Names.Add(name);
        }

        return snapshot;
    }

    private static bool IsPartyMemberGameObject(IGameObject gameObject, string name, PartyMemberSnapshot partyMembers)
        => (IsValidGameObjectId(gameObject.GameObjectId) && partyMembers.GameObjectIds.Contains(gameObject.GameObjectId))
           || IsPartyMemberName(name, partyMembers);

    private static bool IsPartyMemberObservation(ObservedMonster monster, PartyMemberSnapshot partyMembers)
        => (IsValidGameObjectId(monster.GameObjectId) && partyMembers.GameObjectIds.Contains(monster.GameObjectId))
           || IsPartyMemberName(monster.Name, partyMembers);

    private static bool IsPartyMemberObservation(ObservedInteractable interactable, PartyMemberSnapshot partyMembers)
        => (IsValidGameObjectId(interactable.GameObjectId) && partyMembers.GameObjectIds.Contains(interactable.GameObjectId))
           || IsPartyMemberName(interactable.Name, partyMembers);

    private static bool IsPartyMemberName(string name, PartyMemberSnapshot partyMembers)
    {
        var normalizedName = NormalizePartyMemberName(name);
        return !string.IsNullOrWhiteSpace(normalizedName)
               && partyMembers.Names.Contains(normalizedName);
    }

    private static bool IsValidGameObjectId(ulong gameObjectId)
        => gameObjectId != 0 && gameObjectId != 0xE0000000UL;

    private static string NormalizePartyMemberName(string value)
        => string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private sealed class PartyMemberSnapshot
    {
        public HashSet<ulong> GameObjectIds { get; } = [];

        public HashSet<string> Names { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

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
