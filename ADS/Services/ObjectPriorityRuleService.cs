using System.Text.Json;
using ADS.Models;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Plugin.Services;

namespace ADS.Services;

public sealed class ObjectPriorityRuleService
{
    internal const int DefaultPriority = 1000;
    private const string FileName = "duty-object-rules.json";
    private const string MapXzDestinationType = "MapXZ";
    private static readonly TimeSpan ReloadPollInterval = TimeSpan.FromSeconds(1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    private readonly IPluginLog log;
    private readonly string configPath;
    private readonly string bundledPath;
    private DateTime lastObservedRulesWriteUtc;
    private DateTime nextReloadPollUtc;

    public ObjectPriorityRuleService(IPluginLog log, string configDirectory, string? assemblyDirectory)
    {
        this.log = log;
        Directory.CreateDirectory(configDirectory);
        configPath = Path.Combine(configDirectory, FileName);
        bundledPath = string.IsNullOrWhiteSpace(assemblyDirectory)
            ? string.Empty
            : Path.Combine(assemblyDirectory, FileName);

        EnsureSeeded();
        Reload();
    }

    public string ConfigPath
        => configPath;

    public string LastLoadStatus { get; private set; } = "Rules not loaded yet.";

    public ObjectPriorityRuleManifest Current { get; private set; } = new();

    public int ActiveRuleCount
        => Current.Rules.Count(x => x.Enabled);

    public ObjectPriorityRuleManifest CreateEditableCopy()
        => new()
        {
            SchemaVersion = Current.SchemaVersion,
            Description = Current.Description,
            Rules = Current.Rules.Select(CloneRule).ToList(),
        };

    public ObjectPriorityRule CreateBlankRule()
        => new();

    public bool Reload()
    {
        try
        {
            EnsureSeeded();
            var json = File.ReadAllText(configPath);
            var manifest = JsonSerializer.Deserialize<ObjectPriorityRuleManifest>(json, JsonOptions) ?? new ObjectPriorityRuleManifest();
            manifest.Rules ??= [];
            var migrated = ApplyBuiltInRuleMigrations(manifest);
            if (migrated)
                File.WriteAllText(configPath, JsonSerializer.Serialize(manifest, JsonOptions));

            Current = manifest;
            lastObservedRulesWriteUtc = File.GetLastWriteTimeUtc(configPath);
            LastLoadStatus = migrated
                ? $"Loaded {ActiveRuleCount} active rule(s) from {configPath}; applied built-in rule migration(s)."
                : $"Loaded {ActiveRuleCount} active rule(s) from {configPath}.";
            log.Information($"[ADS] {LastLoadStatus}");
            return true;
        }
        catch (Exception ex)
        {
            RememberCurrentRulesWriteTime();
            Current = new ObjectPriorityRuleManifest();
            LastLoadStatus = $"Failed to load {FileName}: {ex.Message}";
            log.Warning(ex, $"[ADS] {LastLoadStatus}");
            return false;
        }
    }

    public bool ReloadIfChanged()
    {
        var now = DateTime.UtcNow;
        if (now < nextReloadPollUtc)
            return false;

        nextReloadPollUtc = now + ReloadPollInterval;

        try
        {
            EnsureSeeded();
            var currentWriteUtc = File.GetLastWriteTimeUtc(configPath);
            if (currentWriteUtc == lastObservedRulesWriteUtc)
                return false;

            return Reload();
        }
        catch (Exception ex)
        {
            RememberCurrentRulesWriteTime();
            LastLoadStatus = $"Failed to check {FileName}: {ex.Message}";
            log.Warning(ex, $"[ADS] {LastLoadStatus}");
            return false;
        }
    }

    public bool SaveManifest(ObjectPriorityRuleManifest manifest)
    {
        try
        {
            manifest.Rules ??= [];
            var json = JsonSerializer.Serialize(manifest, JsonOptions);
            File.WriteAllText(configPath, json);
            return Reload();
        }
        catch (Exception ex)
        {
            LastLoadStatus = $"Failed to save {FileName}: {ex.Message}";
            log.Warning(ex, $"[ADS] {LastLoadStatus}");
            return false;
        }
    }

    public ObjectPriorityRule? MatchObjectRule(DutyContextSnapshot context, ObjectKind objectKind, uint baseId, string objectName)
    {
        return Current.Rules
            .Where(x => x.Enabled)
            .Where(x => !IsMapXzDestinationRule(x))
            .Where(x => Matches(x, context, objectKind, baseId, objectName))
            .OrderByDescending(x => GetSpecificityScore(x))
            .ThenBy(x => x.Priority)
            .FirstOrDefault();
    }

    public IReadOnlyList<ObjectPriorityRule> GetMapXzDestinationRules(DutyContextSnapshot context)
        => Current.Rules
            .Where(x => x.Enabled)
            .Where(IsMapXzDestinationRule)
            .Where(x => MatchesDutyScope(x, context))
            .Where(x => !string.IsNullOrWhiteSpace(x.MapCoordinates))
            .OrderByDescending(GetSpecificityScore)
            .ThenBy(x => x.Priority)
            .ToList();

    public ObjectPriorityRule? MatchInteractableRule(DutyContextSnapshot context, ObjectKind objectKind, uint baseId, string objectName)
        => MatchObjectRule(context, objectKind, baseId, objectName);

    public bool TryGetClassificationOverride(
        DutyContextSnapshot context,
        ObjectKind objectKind,
        uint baseId,
        string objectName,
        out InteractableClass classification)
    {
        classification = default;
        var rule = MatchObjectRule(context, objectKind, baseId, objectName);
        return rule is not null
            && TryParseClassification(rule.Classification, out classification);
    }

    public int GetEffectivePriority(
        DutyContextSnapshot context,
        ObservedInteractable interactable,
        float? distance,
        float? verticalDelta)
    {
        var rule = GetEffectiveRule(context, interactable, distance, verticalDelta);
        return rule?.Priority ?? DefaultPriority;
    }

    public ObjectPriorityRule? GetEffectiveRule(
        DutyContextSnapshot context,
        ObservedInteractable interactable,
        float? distance,
        float? verticalDelta)
    {
        var rule = MatchObjectRule(context, interactable.ObjectKind, interactable.DataId, interactable.Name);
        if (rule is null)
            return null;

        return RulePassesDistanceGates(rule, distance, verticalDelta) ? rule : null;
    }

    public ObjectPriorityRule? GetEffectiveBattleNpcRule(
        DutyContextSnapshot context,
        ObservedMonster monster,
        float? distance,
        float? verticalDelta)
    {
        var rule = MatchObjectRule(context, ObjectKind.BattleNpc, monster.DataId, monster.Name);
        if (rule is null)
            return null;

        return RulePassesDistanceGates(rule, distance, verticalDelta) ? rule : null;
    }

    public int GetEffectiveBattleNpcPriority(
        DutyContextSnapshot context,
        ObservedMonster monster,
        float? distance,
        float? verticalDelta)
    {
        var rule = GetEffectiveBattleNpcRule(context, monster, distance, verticalDelta);
        return rule?.Priority ?? DefaultPriority;
    }

    public bool IsBattleNpcSuppressedByRuleGates(
        DutyContextSnapshot context,
        ObservedMonster monster,
        float? distance,
        float? verticalDelta)
    {
        var rule = MatchObjectRule(context, ObjectKind.BattleNpc, monster.DataId, monster.Name);
        return rule is not null && !RulePassesDistanceGates(rule, distance, verticalDelta);
    }

    public bool IsSuppressedByRuleGates(
        DutyContextSnapshot context,
        ObservedInteractable interactable,
        float? distance,
        float? verticalDelta)
    {
        var rule = MatchObjectRule(context, interactable.ObjectKind, interactable.DataId, interactable.Name);
        return rule is not null && !RulePassesDistanceGates(rule, distance, verticalDelta);
    }

    public ObjectPriorityRule? GetMatchedRule(DutyContextSnapshot context, ObservedInteractable interactable)
        => MatchObjectRule(context, interactable.ObjectKind, interactable.DataId, interactable.Name);

    public bool ShouldIgnoreObject(
        DutyContextSnapshot context,
        ObjectKind objectKind,
        uint baseId,
        string objectName,
        float? distance = null,
        float? verticalDelta = null)
    {
        var rule = MatchObjectRule(context, objectKind, baseId, objectName);
        return rule is not null
            && IsIgnoredRule(rule)
            && RulePassesDistanceGates(rule, distance, verticalDelta);
    }

    public bool ShouldIgnoreInteractable(
        DutyContextSnapshot context,
        ObjectKind objectKind,
        uint baseId,
        string objectName,
        float? distance = null,
        float? verticalDelta = null)
        => ShouldIgnoreObject(context, objectKind, baseId, objectName, distance, verticalDelta);

    public bool ShouldIgnoreInteractable(
        DutyContextSnapshot context,
        ObservedInteractable interactable,
        float? distance = null,
        float? verticalDelta = null)
        => ShouldIgnoreInteractable(context, interactable.ObjectKind, interactable.DataId, interactable.Name, distance, verticalDelta);

    public bool ShouldFollowObject(
        DutyContextSnapshot context,
        ObjectKind objectKind,
        uint baseId,
        string objectName,
        float? distance = null,
        float? verticalDelta = null)
    {
        if (objectKind != ObjectKind.BattleNpc)
            return false;

        var rule = MatchObjectRule(context, objectKind, baseId, objectName);
        return rule is not null
            && IsFollowRule(rule)
            && RulePassesDistanceGates(rule, distance, verticalDelta);
    }

    private static bool RulePassesDistanceGates(ObjectPriorityRule rule, float? distance, float? verticalDelta)
    {
        if (rule.PriorityVerticalRadius > 0f && verticalDelta.HasValue && verticalDelta.Value > rule.PriorityVerticalRadius)
            return false;

        if (rule.MaxDistance.HasValue && distance.HasValue && distance.Value > rule.MaxDistance.Value)
            return false;

        return true;
    }

    private void EnsureSeeded()
    {
        if (File.Exists(configPath))
            return;

        if (!string.IsNullOrWhiteSpace(bundledPath) && File.Exists(bundledPath))
        {
            File.Copy(bundledPath, configPath, overwrite: false);
            return;
        }

        File.WriteAllText(configPath, GetDefaultJson());
    }

    private void RememberCurrentRulesWriteTime()
    {
        try
        {
            if (File.Exists(configPath))
                lastObservedRulesWriteUtc = File.GetLastWriteTimeUtc(configPath);
        }
        catch
        {
            // Best effort only; the caller logs the actionable load/check failure.
        }
    }

    private static bool Matches(
        ObjectPriorityRule rule,
        DutyContextSnapshot context,
        ObjectKind objectKind,
        uint baseId,
        string objectName)
    {
        if (!MatchesDutyScope(rule, context))
            return false;

        if (!string.IsNullOrWhiteSpace(rule.ObjectKind)
            && !string.Equals(rule.ObjectKind, objectKind.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (rule.BaseId != 0 && rule.BaseId != baseId)
            return false;

        if (string.IsNullOrWhiteSpace(rule.ObjectName))
            return true;

        return rule.NameMatchMode.Equals("Contains", StringComparison.OrdinalIgnoreCase)
            ? objectName.Contains(rule.ObjectName, StringComparison.OrdinalIgnoreCase)
            : string.Equals(objectName, rule.ObjectName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesDutyScope(ObjectPriorityRule rule, DutyContextSnapshot context)
    {
        if (rule.ContentFinderConditionId != 0 && rule.ContentFinderConditionId != context.ContentFinderConditionId)
            return false;

        if (rule.TerritoryTypeId != 0 && rule.TerritoryTypeId != context.TerritoryTypeId)
            return false;

        if (!string.IsNullOrWhiteSpace(rule.DutyEnglishName)
            && !DutyNamesMatch(rule.DutyEnglishName, context.CurrentDuty?.EnglishName))
        {
            return false;
        }

        return true;
    }

    private static int GetSpecificityScore(ObjectPriorityRule rule)
    {
        var score = 0;
        if (rule.ContentFinderConditionId != 0)
            score += 100;
        if (rule.TerritoryTypeId != 0)
            score += 50;
        if (!string.IsNullOrWhiteSpace(rule.DutyEnglishName))
            score += 30;
        if (!string.IsNullOrWhiteSpace(rule.ObjectKind))
            score += 20;
        if (rule.BaseId != 0)
            score += 15;
        if (!string.IsNullOrWhiteSpace(rule.ObjectName))
            score += rule.NameMatchMode.Equals("Contains", StringComparison.OrdinalIgnoreCase) ? 5 : 10;

        return score;
    }

    private static bool DutyNamesMatch(string configuredName, string? currentName)
    {
        if (string.IsNullOrWhiteSpace(currentName))
            return false;

        var configured = NormalizeDutyName(configuredName);
        var current = NormalizeDutyName(currentName);
        return string.Equals(configured, current, StringComparison.OrdinalIgnoreCase)
            || string.Equals(TrimLeadingArticle(configured), TrimLeadingArticle(current), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeDutyName(string value)
        => string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string TrimLeadingArticle(string value)
        => value.StartsWith("the ", StringComparison.OrdinalIgnoreCase)
            ? value[4..]
            : value;

    private static bool TryParseClassification(string value, out InteractableClass classification)
        => Enum.TryParse(value, ignoreCase: true, out classification);

    private static bool IsIgnoredRule(ObjectPriorityRule rule)
        => TryParseClassification(rule.Classification, out var classification)
           && classification == InteractableClass.Ignored;

    private static bool IsFollowRule(ObjectPriorityRule rule)
        => TryParseClassification(rule.Classification, out var classification)
           && classification == InteractableClass.Follow;

    private static bool IsMapXzDestinationRule(ObjectPriorityRule rule)
        => string.Equals(rule.DestinationType, MapXzDestinationType, StringComparison.OrdinalIgnoreCase)
           || (TryParseClassification(rule.Classification, out var classification)
               && classification == InteractableClass.MapXzDestination);

    private static ObjectPriorityRule CloneRule(ObjectPriorityRule rule)
        => new()
        {
            Enabled = rule.Enabled,
            TerritoryTypeId = rule.TerritoryTypeId,
            ContentFinderConditionId = rule.ContentFinderConditionId,
            DutyEnglishName = rule.DutyEnglishName,
            ObjectKind = rule.ObjectKind,
            BaseId = rule.BaseId,
            ObjectName = rule.ObjectName,
            NameMatchMode = rule.NameMatchMode,
            Classification = rule.Classification,
            DestinationType = rule.DestinationType,
            MapCoordinates = rule.MapCoordinates,
            Priority = rule.Priority,
            PriorityVerticalRadius = rule.PriorityVerticalRadius,
            MaxDistance = rule.MaxDistance,
            WaitAtDestinationSeconds = rule.WaitAtDestinationSeconds,
            Notes = rule.Notes,
        };

    private static bool ApplyBuiltInRuleMigrations(ObjectPriorityRuleManifest manifest)
    {
        var changed = false;
        foreach (var rule in manifest.Rules)
        {
            if (!string.Equals(rule.ObjectKind, ObjectKind.BattleNpc.ToString(), StringComparison.OrdinalIgnoreCase)
                && string.Equals(rule.Classification, InteractableClass.Follow.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                rule.Classification = InteractableClass.Ignored.ToString();
                const string migrationNote = "Follow is BattleNpc-only; migrated to Ignored to keep this object out of planner truth.";
                rule.Notes = string.IsNullOrWhiteSpace(rule.Notes)
                    ? migrationNote
                    : rule.Notes.Contains(migrationNote, StringComparison.OrdinalIgnoreCase)
                        ? rule.Notes
                        : $"{rule.Notes} {migrationNote}";
                changed = true;
                continue;
            }

            if (!string.Equals(rule.DutyEnglishName, "Castrum Meridianum", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(rule.ObjectKind, ObjectKind.BattleNpc.ToString(), StringComparison.OrdinalIgnoreCase)
                || !string.Equals(rule.ObjectName, "Cid", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(rule.Classification, InteractableClass.Ignored.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            rule.Classification = InteractableClass.Follow.ToString();
            if (string.IsNullOrWhiteSpace(rule.Notes))
            {
                rule.Notes = "Follow Cid as a live-only anchor only when no other live monster or interactable is present; never promote Cid as a ghost target.";
            }

            changed = true;
        }

        return changed;
    }

    private static string GetDefaultJson()
        => """
{
  "schemaVersion": 1,
  "description": "Human-edited ADS duty object rules. Lower priority wins. Zero numeric ids mean global. Use dutyEnglishName while scouting, then tighten to contentFinderConditionId or territoryTypeId later if needed. classification supports Ignored for sticky non-progression objects, Required for BattleNpc kill priority, BattleNpc-only Follow for live movement anchors such as Cid, and MapXzDestination with destinationType MapXZ plus mapCoordinates like 11.3,10.4 for manual sub-area waypoints. Non-BattleNpc Follow rules are ignored. waitAtDestinationSeconds is included now for future execution timing.",
  "rules": [
    {
      "enabled": true,
      "territoryTypeId": 0,
      "contentFinderConditionId": 0,
      "dutyEnglishName": "The Tam-Tara Deepcroft",
      "objectKind": "EventObj",
      "baseId": 0,
      "objectName": "Cultist Rosary",
      "nameMatchMode": "Exact",
      "classification": "Required",
      "priority": 10,
      "priorityVerticalRadius": 100.0,
      "maxDistance": 100.0,
      "waitAtDestinationSeconds": 0.0,
      "notes": "Key object should beat the nearby Sealed Barrier."
    },
    {
      "enabled": true,
      "territoryTypeId": 0,
      "contentFinderConditionId": 0,
      "dutyEnglishName": "The Tam-Tara Deepcroft",
      "objectKind": "EventObj",
      "baseId": 0,
      "objectName": "Sealed Barrier",
      "nameMatchMode": "Exact",
      "classification": "Required",
      "priority": 100,
      "priorityVerticalRadius": 1.0,
      "maxDistance": 20.0,
      "waitAtDestinationSeconds": 0.0,
      "notes": "Barrier stays below the rosary until the key is handled."
    },
    {
      "enabled": true,
      "territoryTypeId": 1039,
      "contentFinderConditionId": 1,
      "dutyEnglishName": "The Thousand Maws of Toto-Rak",
      "objectKind": "EventObj",
      "baseId": 0,
      "objectName": "Water-stained Note",
      "nameMatchMode": "Exact",
      "classification": "Ignored",
      "priority": 1000,
      "priorityVerticalRadius": 0.0,
      "maxDistance": null,
      "waitAtDestinationSeconds": 0.0,
      "notes": "Sticky optional note remains targetable after use and should not hold planner truth."
    }
  ]
}
""";
}
