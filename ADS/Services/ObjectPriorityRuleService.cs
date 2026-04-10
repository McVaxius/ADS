using System.Text.Json;
using ADS.Models;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Plugin.Services;

namespace ADS.Services;

public sealed class ObjectPriorityRuleService
{
    private const int DefaultPriority = 1000;
    private const string FileName = "duty-object-rules.json";

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

    public bool Reload()
    {
        try
        {
            EnsureSeeded();
            var json = File.ReadAllText(configPath);
            var manifest = JsonSerializer.Deserialize<ObjectPriorityRuleManifest>(json, JsonOptions) ?? new ObjectPriorityRuleManifest();
            manifest.Rules ??= [];
            Current = manifest;
            LastLoadStatus = $"Loaded {ActiveRuleCount} active rule(s) from {configPath}.";
            log.Information($"[ADS] {LastLoadStatus}");
            return true;
        }
        catch (Exception ex)
        {
            Current = new ObjectPriorityRuleManifest();
            LastLoadStatus = $"Failed to load {FileName}: {ex.Message}";
            log.Warning(ex, $"[ADS] {LastLoadStatus}");
            return false;
        }
    }

    public ObjectPriorityRule? MatchInteractableRule(DutyContextSnapshot context, ObjectKind objectKind, uint baseId, string objectName)
    {
        return Current.Rules
            .Where(x => x.Enabled)
            .Where(x => Matches(x, context, objectKind, baseId, objectName))
            .OrderByDescending(x => GetSpecificityScore(x))
            .ThenBy(x => x.Priority)
            .FirstOrDefault();
    }

    public bool TryGetClassificationOverride(
        DutyContextSnapshot context,
        ObjectKind objectKind,
        uint baseId,
        string objectName,
        out InteractableClass classification)
    {
        classification = default;
        var rule = MatchInteractableRule(context, objectKind, baseId, objectName);
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
        var rule = MatchInteractableRule(context, interactable.ObjectKind, interactable.DataId, interactable.Name);
        if (rule is null)
            return null;

        return RulePassesDistanceGates(rule, distance, verticalDelta) ? rule : null;
    }

    public bool IsSuppressedByRuleGates(
        DutyContextSnapshot context,
        ObservedInteractable interactable,
        float? distance,
        float? verticalDelta)
    {
        var rule = MatchInteractableRule(context, interactable.ObjectKind, interactable.DataId, interactable.Name);
        return rule is not null && !RulePassesDistanceGates(rule, distance, verticalDelta);
    }

    public ObjectPriorityRule? GetMatchedRule(DutyContextSnapshot context, ObservedInteractable interactable)
        => MatchInteractableRule(context, interactable.ObjectKind, interactable.DataId, interactable.Name);

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

    private static bool Matches(
        ObjectPriorityRule rule,
        DutyContextSnapshot context,
        ObjectKind objectKind,
        uint baseId,
        string objectName)
    {
        if (rule.ContentFinderConditionId != 0 && rule.ContentFinderConditionId != context.ContentFinderConditionId)
            return false;

        if (rule.TerritoryTypeId != 0 && rule.TerritoryTypeId != context.TerritoryTypeId)
            return false;

        if (!string.IsNullOrWhiteSpace(rule.DutyEnglishName)
            && !string.Equals(rule.DutyEnglishName, context.CurrentDuty?.EnglishName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

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

    private static bool TryParseClassification(string value, out InteractableClass classification)
        => Enum.TryParse(value, ignoreCase: true, out classification);

    private static string GetDefaultJson()
        => """
{
  "schemaVersion": 1,
  "description": "Human-edited ADS duty object rules. Lower priority wins. Zero numeric ids mean global. Use dutyEnglishName while scouting, then tighten to contentFinderConditionId or territoryTypeId later if needed. waitAtDestinationSeconds is included now for future execution timing.",
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
    }
  ]
}
""";
}
