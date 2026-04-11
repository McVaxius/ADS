using System.Globalization;
using System.Numerics;
using System.Text.Json;
using ADS.Models;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace ADS.Services;

public sealed class ObjectPriorityRuleService
{
    internal const int DefaultPriority = 1000;
    private const string FileName = "duty-object-rules.json";
    private const string MapXzDestinationType = "MapXZ";
    private const string XyzDestinationType = "XYZ";
    private const float DefaultObjectMatchRadius = 6f;
    private static readonly TimeSpan ReloadPollInterval = TimeSpan.FromSeconds(1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    private readonly IPluginLog log;
    private readonly IDataManager dataManager;
    private readonly string configPath;
    private readonly string bundledPath;
    private readonly HashSet<string> loggedInvalidObjectSpatialRules = new(StringComparer.Ordinal);
    private DateTime lastObservedRulesWriteUtc;
    private DateTime nextReloadPollUtc;

    public ObjectPriorityRuleService(IPluginLog log, IDataManager dataManager, string configDirectory, string? assemblyDirectory)
    {
        this.log = log;
        this.dataManager = dataManager;
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

    public bool MatchesCurrentDutyScopeForEditor(ObjectPriorityRule rule, DutyContextSnapshot context)
        => MatchesDutyScope(rule, context, includeLayerScope: false);

    public ObjectPriorityRule? MatchObjectRule(
        DutyContextSnapshot context,
        ObjectKind objectKind,
        uint baseId,
        string objectName,
        Vector3? objectPosition = null,
        uint objectMapId = 0)
    {
        return Current.Rules
            .Where(x => x.Enabled)
            .Where(x => !IsManualDestinationRule(x))
            .Where(x => Matches(x, context, objectKind, baseId, objectName, objectPosition, objectMapId))
            .OrderByDescending(x => GetSpecificityScore(x))
            .ThenBy(x => x.Priority)
            .FirstOrDefault();
    }

    public IReadOnlyList<ObjectPriorityRule> GetMapXzDestinationRules(DutyContextSnapshot context)
        => Current.Rules
            .Where(x => x.Enabled)
            .Where(IsMapXzDestinationRule)
            .Where(x => MatchesDutyScope(x, context, includeLayerScope: true))
            .Where(x => !string.IsNullOrWhiteSpace(x.MapCoordinates))
            .OrderByDescending(GetSpecificityScore)
            .ThenBy(x => x.Priority)
            .ToList();

    public IReadOnlyList<ObjectPriorityRule> GetXyzDestinationRules(DutyContextSnapshot context)
        => Current.Rules
            .Where(x => x.Enabled)
            .Where(IsXyzDestinationRule)
            .Where(x => MatchesDutyScope(x, context, includeLayerScope: true))
            .Where(x => !string.IsNullOrWhiteSpace(x.WorldCoordinates))
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
        out InteractableClass classification,
        Vector3? objectPosition = null,
        uint objectMapId = 0)
    {
        classification = default;
        var rule = MatchObjectRule(context, objectKind, baseId, objectName, objectPosition, objectMapId);
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
        var rule = MatchObjectRule(context, interactable.ObjectKind, interactable.DataId, interactable.Name, interactable.Position, interactable.MapId);
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
        var rule = MatchObjectRule(context, ObjectKind.BattleNpc, monster.DataId, monster.Name, monster.Position, monster.MapId);
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

    public InteractableClass? GetEffectiveBattleNpcClassification(
        DutyContextSnapshot context,
        ObservedMonster monster,
        float? distance,
        float? verticalDelta)
    {
        var rule = GetEffectiveBattleNpcRule(context, monster, distance, verticalDelta);
        return rule is not null && TryParseClassification(rule.Classification, out var classification)
            ? classification
            : null;
    }

    public bool IsBattleNpcSuppressedByRuleGates(
        DutyContextSnapshot context,
        ObservedMonster monster,
        float? distance,
        float? verticalDelta)
    {
        var rule = MatchObjectRule(context, ObjectKind.BattleNpc, monster.DataId, monster.Name, monster.Position, monster.MapId);
        return rule is not null && !RulePassesDistanceGates(rule, distance, verticalDelta);
    }

    public bool IsSuppressedByRuleGates(
        DutyContextSnapshot context,
        ObservedInteractable interactable,
        float? distance,
        float? verticalDelta)
    {
        var rule = MatchObjectRule(context, interactable.ObjectKind, interactable.DataId, interactable.Name, interactable.Position, interactable.MapId);
        return rule is not null && !RulePassesDistanceGates(rule, distance, verticalDelta);
    }

    public ObjectPriorityRule? GetMatchedRule(DutyContextSnapshot context, ObservedInteractable interactable)
        => MatchObjectRule(context, interactable.ObjectKind, interactable.DataId, interactable.Name, interactable.Position, interactable.MapId);

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

    public bool ShouldIgnoreObject(
        DutyContextSnapshot context,
        ObjectKind objectKind,
        uint baseId,
        string objectName,
        Vector3 objectPosition,
        uint objectMapId = 0,
        float? distance = null,
        float? verticalDelta = null)
    {
        var rule = MatchObjectRule(context, objectKind, baseId, objectName, objectPosition, objectMapId);
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
        ObjectKind objectKind,
        uint baseId,
        string objectName,
        Vector3 objectPosition,
        uint objectMapId = 0,
        float? distance = null,
        float? verticalDelta = null)
        => ShouldIgnoreObject(context, objectKind, baseId, objectName, objectPosition, objectMapId, distance, verticalDelta);

    public bool ShouldIgnoreInteractable(
        DutyContextSnapshot context,
        ObservedInteractable interactable,
        float? distance = null,
        float? verticalDelta = null)
        => ShouldIgnoreInteractable(context, interactable.ObjectKind, interactable.DataId, interactable.Name, interactable.Position, interactable.MapId, distance, verticalDelta);

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

    public bool ShouldFollowObject(
        DutyContextSnapshot context,
        ObjectKind objectKind,
        uint baseId,
        string objectName,
        Vector3 objectPosition,
        uint objectMapId = 0,
        float? distance = null,
        float? verticalDelta = null)
    {
        if (objectKind != ObjectKind.BattleNpc)
            return false;

        var rule = MatchObjectRule(context, objectKind, baseId, objectName, objectPosition, objectMapId);
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

    private bool Matches(
        ObjectPriorityRule rule,
        DutyContextSnapshot context,
        ObjectKind objectKind,
        uint baseId,
        string objectName,
        Vector3? objectPosition,
        uint objectMapId)
    {
        if (!MatchesDutyScope(rule, context, includeLayerScope: true))
            return false;

        if (!string.IsNullOrWhiteSpace(rule.ObjectKind)
            && !string.Equals(rule.ObjectKind, objectKind.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (rule.BaseId != 0 && rule.BaseId != baseId)
            return false;

        if (!MatchesObjectSpatialScope(rule, context, objectPosition, objectMapId))
            return false;

        if (string.IsNullOrWhiteSpace(rule.ObjectName))
            return true;

        return rule.NameMatchMode.Equals("Contains", StringComparison.OrdinalIgnoreCase)
            ? objectName.Contains(rule.ObjectName, StringComparison.OrdinalIgnoreCase)
            : string.Equals(objectName, rule.ObjectName, StringComparison.OrdinalIgnoreCase);
    }

    private bool MatchesDutyScope(ObjectPriorityRule rule, DutyContextSnapshot context, bool includeLayerScope)
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

        if (includeLayerScope && !MatchesLayerScope(rule, context))
            return false;

        return true;
    }

    private bool MatchesLayerScope(ObjectPriorityRule rule, DutyContextSnapshot context)
    {
        var selector = GetLayerSelector(rule);
        if (string.IsNullOrWhiteSpace(selector))
            return true;

        if (context.MapId == 0)
            return false;

        if (uint.TryParse(selector, out var mapId))
            return mapId == context.MapId;

        var mapSheet = dataManager.GetExcelSheet<Map>();
        if (mapSheet is null || !mapSheet.TryGetRow(context.MapId, out var map))
            return false;

        var activeMapName = BuildMapName(map);
        if (string.Equals(selector, activeMapName, StringComparison.OrdinalIgnoreCase))
            return true;

        var placeName = NormalizeName(map.PlaceName.ValueNullable?.Name.ToString() ?? string.Empty);
        if (string.Equals(selector, placeName, StringComparison.OrdinalIgnoreCase))
            return true;

        var placeNameSub = NormalizeName(map.PlaceNameSub.ValueNullable?.Name.ToString() ?? string.Empty);
        return string.Equals(selector, placeNameSub, StringComparison.OrdinalIgnoreCase);
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
        if (!string.IsNullOrWhiteSpace(rule.ObjectMapCoordinates))
            score += 15;
        if (!string.IsNullOrWhiteSpace(rule.ObjectWorldCoordinates))
            score += 20;

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

    private static string NormalizeName(string value)
        => string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string NormalizeLayerSelector(string? value)
        => NormalizeName(value ?? string.Empty);

    private static string GetLayerSelector(ObjectPriorityRule rule)
    {
        var explicitLayer = NormalizeLayerSelector(rule.Layer);
        if (!string.IsNullOrWhiteSpace(explicitLayer))
            return explicitLayer;

        var legacyLayer = NormalizeLayerSelector(rule.DestinationType);
        return string.Equals(legacyLayer, MapXzDestinationType, StringComparison.OrdinalIgnoreCase)
               || string.Equals(legacyLayer, XyzDestinationType, StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : legacyLayer;
    }

    private static string BuildMapName(Map map)
    {
        var placeName = NormalizeName(map.PlaceName.ValueNullable?.Name.ToString() ?? string.Empty);
        var placeNameSub = NormalizeName(map.PlaceNameSub.ValueNullable?.Name.ToString() ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(placeNameSub))
            return placeNameSub;

        if (!string.IsNullOrWhiteSpace(placeName))
            return placeName;

        return $"Map {map.RowId}";
    }

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

    private static bool IsXyzDestinationRule(ObjectPriorityRule rule)
        => string.Equals(rule.DestinationType, XyzDestinationType, StringComparison.OrdinalIgnoreCase)
           || (TryParseClassification(rule.Classification, out var classification)
               && classification == InteractableClass.XYZ);

    private static bool IsManualDestinationRule(ObjectPriorityRule rule)
        => IsMapXzDestinationRule(rule) || IsXyzDestinationRule(rule);

    private bool MatchesObjectSpatialScope(
        ObjectPriorityRule rule,
        DutyContextSnapshot context,
        Vector3? objectPosition,
        uint objectMapId)
    {
        var hasObjectMapCoordinates = !string.IsNullOrWhiteSpace(rule.ObjectMapCoordinates);
        var hasObjectWorldCoordinates = !string.IsNullOrWhiteSpace(rule.ObjectWorldCoordinates);
        if (!hasObjectMapCoordinates && !hasObjectWorldCoordinates)
            return true;

        if (!objectPosition.HasValue)
            return false;

        var ruleKey = BuildObjectSpatialRuleKey(context, rule);
        var matchRadius = GetObjectMatchRadius(rule);
        if (hasObjectWorldCoordinates)
        {
            if (!TryParseWorldCoordinates(rule.ObjectWorldCoordinates, out var worldCoordinates))
            {
                LogObjectSpatialWarning(
                    $"bad-world:{ruleKey}",
                    $"[ADS] Ignoring positional object-match rule {rule.ObjectName}: could not parse objectWorldCoordinates '{rule.ObjectWorldCoordinates}'. Use a value like 154.1,101.9,-34.2.");
                return false;
            }

            return Vector3.Distance(objectPosition.Value, worldCoordinates) <= matchRadius;
        }

        if (!TryParseMapCoordinates(rule.ObjectMapCoordinates, out var mapCoordinates))
        {
            LogObjectSpatialWarning(
                $"bad-map:{ruleKey}",
                $"[ADS] Ignoring positional object-match rule {rule.ObjectName}: could not parse objectMapCoordinates '{rule.ObjectMapCoordinates}'. Use a value like 11.3,10.4.");
            return false;
        }

        var resolvedMapId = objectMapId != 0 ? objectMapId : context.MapId;
        if (resolvedMapId == 0 || !TryResolveMapRow(resolvedMapId, context.TerritoryTypeId, out var map))
            return false;

        var worldPosition = ConvertMapCoordinatesToWorld(mapCoordinates, map, objectPosition.Value.Y);
        return GetHorizontalDistance(objectPosition.Value, worldPosition) <= matchRadius;
    }

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
            Layer = rule.Layer,
            MapCoordinates = rule.MapCoordinates,
            WorldCoordinates = rule.WorldCoordinates,
            ObjectMapCoordinates = rule.ObjectMapCoordinates,
            ObjectWorldCoordinates = rule.ObjectWorldCoordinates,
            ObjectMatchRadius = rule.ObjectMatchRadius,
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
            if (rule.ObjectMatchRadius.HasValue && rule.ObjectMatchRadius.Value <= 0f)
            {
                rule.ObjectMatchRadius = null;
                changed = true;
            }

            var legacyLayer = NormalizeLayerSelector(rule.DestinationType);
            if (string.IsNullOrWhiteSpace(rule.Layer)
                && !string.IsNullOrWhiteSpace(legacyLayer)
                && !string.Equals(legacyLayer, MapXzDestinationType, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(legacyLayer, XyzDestinationType, StringComparison.OrdinalIgnoreCase))
            {
                rule.Layer = rule.DestinationType;
                rule.DestinationType = string.Empty;
                changed = true;
            }

            if (!string.IsNullOrWhiteSpace(rule.Layer)
                && !string.IsNullOrWhiteSpace(legacyLayer)
                && !string.Equals(legacyLayer, MapXzDestinationType, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(legacyLayer, XyzDestinationType, StringComparison.OrdinalIgnoreCase))
            {
                rule.DestinationType = string.Empty;
                changed = true;
            }

            if (TryParseClassification(rule.Classification, out var ruleClassification)
                && ruleClassification == InteractableClass.MapXzDestination
                && string.Equals(rule.DestinationType, MapXzDestinationType, StringComparison.OrdinalIgnoreCase))
            {
                rule.DestinationType = string.Empty;
                changed = true;
            }

            if (TryParseClassification(rule.Classification, out ruleClassification)
                && ruleClassification == InteractableClass.XYZ
                && string.Equals(rule.DestinationType, XyzDestinationType, StringComparison.OrdinalIgnoreCase))
            {
                rule.DestinationType = string.Empty;
                changed = true;
            }

            if (TryParseClassification(rule.Classification, out ruleClassification)
                && ruleClassification == InteractableClass.XYZ
                && string.IsNullOrWhiteSpace(rule.WorldCoordinates)
                && TryNormalizeLegacyWorldCoordinates(rule.MapCoordinates, out var migratedWorldCoordinates))
            {
                rule.WorldCoordinates = migratedWorldCoordinates;
                rule.MapCoordinates = string.Empty;

                var migrationNote = "XYZ destinations must author world X,Y,Z in WorldCoordinates; migrated a stale 3-value MapCoordinates payload.";
                rule.Notes = string.IsNullOrWhiteSpace(rule.Notes)
                    ? migrationNote
                    : rule.Notes.Contains(migrationNote, StringComparison.OrdinalIgnoreCase)
                        ? rule.Notes
                        : $"{rule.Notes} {migrationNote}";
                changed = true;
            }

            if (string.Equals(rule.DutyEnglishName, "Brayflox's Longstop", StringComparison.OrdinalIgnoreCase)
                && string.Equals(rule.ObjectName, "Goblin Pathfinder", StringComparison.OrdinalIgnoreCase)
                && string.Equals(rule.ObjectKind, "EventNpc", StringComparison.OrdinalIgnoreCase))
            {
                rule.ObjectKind = ObjectKind.BattleNpc.ToString();
                if (string.Equals(rule.Classification, InteractableClass.Required.ToString(), StringComparison.OrdinalIgnoreCase))
                    rule.Classification = InteractableClass.CombatFriendly.ToString();

                var migrationNote = "Goblin Pathfinder is a BattleNpc talk target; migrated from EventNpc into the CombatFriendly BattleNpc direct-interact path.";
                rule.Notes = string.IsNullOrWhiteSpace(rule.Notes)
                    ? migrationNote
                    : rule.Notes.Contains(migrationNote, StringComparison.OrdinalIgnoreCase)
                        ? rule.Notes
                        : $"{rule.Notes} {migrationNote}";
                changed = true;
            }

            if (string.Equals(rule.DutyEnglishName, "the Praetorium", StringComparison.OrdinalIgnoreCase)
                && string.Equals(rule.Layer, "Castrum Defense", StringComparison.OrdinalIgnoreCase)
                && string.Equals(rule.ObjectName, "Magitek Terminal", StringComparison.OrdinalIgnoreCase)
                && string.Equals(rule.Classification, InteractableClass.Ignored.ToString(), StringComparison.OrdinalIgnoreCase)
                && string.Equals(rule.ObjectKind, "EventNpc", StringComparison.OrdinalIgnoreCase))
            {
                rule.ObjectKind = string.Empty;
                rule.MaxDistance = null;
                if (rule.Priority == 0)
                    rule.Priority = DefaultPriority;

                var migrationNote = "Castrum Defense Magitek Terminal is observed as EventObj, so this stale EventNpc ignore row was widened to wildcard kind.";
                rule.Notes = string.IsNullOrWhiteSpace(rule.Notes)
                    ? migrationNote
                    : rule.Notes.Contains(migrationNote, StringComparison.OrdinalIgnoreCase)
                        ? rule.Notes
                        : $"{rule.Notes} {migrationNote}";
                changed = true;
            }

            if (!string.Equals(rule.ObjectKind, ObjectKind.BattleNpc.ToString(), StringComparison.OrdinalIgnoreCase)
                && TryParseClassification(rule.Classification, out var classification)
                && classification is InteractableClass.Follow or InteractableClass.BossFight)
            {
                rule.Classification = InteractableClass.Ignored.ToString();
                var migrationNote = $"{classification} is BattleNpc-only; migrated to Ignored to keep this object out of planner truth.";
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

    private static bool TryNormalizeLegacyWorldCoordinates(string value, out string normalizedCoordinates)
    {
        normalizedCoordinates = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var parts = value.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 3)
            return false;

        if (!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
            || !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
        {
            return false;
        }

        normalizedCoordinates = string.Create(
            CultureInfo.InvariantCulture,
            $"{x:0.###},{y:0.###},{z:0.###}");
        return true;
    }

    private bool TryResolveMapRow(uint mapId, uint territoryTypeId, out Map map)
    {
        map = default;
        var mapSheet = dataManager.GetExcelSheet<Map>();
        if (mapSheet is null || !mapSheet.TryGetRow(mapId, out map))
            return false;

        return territoryTypeId == 0 || map.TerritoryType.RowId == territoryTypeId;
    }

    private void LogObjectSpatialWarning(string key, string message)
    {
        if (loggedInvalidObjectSpatialRules.Add(key))
            log.Warning(message);
    }

    private static float GetObjectMatchRadius(ObjectPriorityRule rule)
        => rule.ObjectMatchRadius.HasValue && rule.ObjectMatchRadius.Value > 0f
            ? rule.ObjectMatchRadius.Value
            : DefaultObjectMatchRadius;

    private static string BuildObjectSpatialRuleKey(DutyContextSnapshot context, ObjectPriorityRule rule)
        => $"obj:{context.ContentFinderConditionId}:{context.TerritoryTypeId}:{context.MapId}:{rule.ObjectKind}:{rule.BaseId}:{rule.ObjectName}:{rule.Layer}:{rule.ObjectMapCoordinates}:{rule.ObjectWorldCoordinates}";

    private static bool TryParseMapCoordinates(string value, out Vector2 coordinates)
    {
        coordinates = default;
        var parts = value.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
            return false;

        if (!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
        {
            return false;
        }

        coordinates = new Vector2(x, z);
        return true;
    }

    private static bool TryParseWorldCoordinates(string value, out Vector3 coordinates)
    {
        coordinates = default;
        var parts = value.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 3)
            return false;

        if (!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
            || !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
        {
            return false;
        }

        coordinates = new Vector3(x, y, z);
        return true;
    }

    private static Vector3 ConvertMapCoordinatesToWorld(Vector2 mapCoordinates, Map map, float currentObjectY)
        => new(
            ConvertMapCoordinateToWorld(mapCoordinates.X, map.SizeFactor, map.OffsetX),
            currentObjectY,
            ConvertMapCoordinateToWorld(mapCoordinates.Y, map.SizeFactor, map.OffsetY));

    private static float ConvertMapCoordinateToWorld(float mapCoordinate, uint scale, int offset)
    {
        var mapScale = scale / 100f;
        if (mapScale <= float.Epsilon)
            return 0f;

        var textureCoordinate = (((mapCoordinate - 1f) * mapScale) / 41f) * 2048f;
        return (textureCoordinate - (offset + 1024f)) / mapScale;
    }

    private static float GetHorizontalDistance(Vector3 a, Vector3 b)
    {
        var x = a.X - b.X;
        var z = a.Z - b.Z;
        return MathF.Sqrt((x * x) + (z * z));
    }

    private static string GetDefaultJson()
        => """
{
  "schemaVersion": 1,
  "description": "Human-edited ADS duty object rules. Lower priority wins. Zero numeric ids mean global. Use dutyEnglishName while scouting, then tighten to contentFinderConditionId or territoryTypeId later if needed. classification supports Ignored for sticky non-progression objects, Required for BattleNpc kill priority, BattleNpc-only Follow for live movement anchors such as Cid, BattleNpc-only BossFight for live boss targets that should beat nearby trash/objectives once the rule gates pass, BattleNpc CombatFriendly for direct-interact talk targets such as Goblin Pathfinder, MapXzDestination with mapCoordinates like 11.3,10.4 for manual sub-area waypoints, and XYZ with worldCoordinates like 154.1,101.9,-34.2 for precise world-space manual staging. objectMapCoordinates or objectWorldCoordinates can pin an ordinary same-name row to one physical object instance, and objectMatchRadius defaults to 6y when left blank on a positional row. layer now means the optional live-map/sub-area selector for any rule: leave it blank for any active layer, or set it to a live map name / map row id to restrict that row to one layer. Legacy destinationType layer rows auto-migrate on load; MapXzDestination rows no longer need destinationType set to MapXZ, and XYZ rows no longer need destinationType set to XYZ. Manual destination rows can also intentionally beat worse live progression interactables when their authored priority is better and no live monsters/follow anchors remain. Non-BattleNpc Follow and BossFight rules are ignored. waitAtDestinationSeconds is included now for future execution timing.",
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
