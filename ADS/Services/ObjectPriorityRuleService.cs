using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using ADS.Models;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace ADS.Services;

public sealed class ObjectPriorityRuleService
{
    internal const int DefaultPriority = 1000;
    public const string DefaultPresetName = "DEFAULT";
    private const string FileName = "duty-object-rules.json";
    private const string PresetDirectoryName = "rule-presets";
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
    private readonly string presetDirectoryPath;
    private readonly string bundledPath;
    private readonly HashSet<string> loggedInvalidObjectSpatialRules = new(StringComparer.Ordinal);
    private readonly HashSet<string> loggedOffLayerBattleNpcSuppressions = new(StringComparer.Ordinal);
    private DateTime lastObservedRulesWriteUtc;
    private DateTime nextReloadPollUtc;

    public ObjectPriorityRuleService(IPluginLog log, IDataManager dataManager, string configDirectory, string? assemblyDirectory)
    {
        this.log = log;
        this.dataManager = dataManager;
        Directory.CreateDirectory(configDirectory);
        configPath = Path.Combine(configDirectory, FileName);
        presetDirectoryPath = Path.Combine(configDirectory, PresetDirectoryName);
        Directory.CreateDirectory(presetDirectoryPath);
        bundledPath = string.IsNullOrWhiteSpace(assemblyDirectory)
            ? string.Empty
            : Path.Combine(assemblyDirectory, FileName);

        EnsureSeeded();
        Reload();
    }

    public string ConfigPath
        => configPath;

    public string PresetDirectoryPath
        => presetDirectoryPath;

    public string BundledPath
        => bundledPath;

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

    public bool IsDefaultPreset(string presetName)
        => string.Equals(presetName, DefaultPresetName, StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<string> GetPresetNames()
    {
        var names = new List<string> { DefaultPresetName };
        if (!Directory.Exists(presetDirectoryPath))
            return names;

        names.AddRange(
            Directory.EnumerateFiles(presetDirectoryPath, "*.json", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileNameWithoutExtension)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Where(x => !string.Equals(x, DefaultPresetName, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)!);
        return names;
    }

    public string GetPresetPath(string presetName)
        => IsDefaultPreset(presetName)
            ? configPath
            : Path.Combine(presetDirectoryPath, $"{SanitizePresetName(presetName)}.json");

    public string SanitizePresetName(string presetName)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var cleaned = new string((presetName ?? string.Empty).Where(ch => !invalidCharacters.Contains(ch)).ToArray());
        cleaned = NormalizeName(cleaned).Trim('.', ' ');
        if (string.IsNullOrWhiteSpace(cleaned))
            cleaned = "Preset";

        if (IsDefaultPreset(cleaned))
            cleaned = $"{DefaultPresetName}-copy";

        return cleaned;
    }

    public bool Reload()
    {
        try
        {
            EnsureSeeded();
            if (!TryLoadManifestFromPath(configPath, out var manifest, out var status, persistMigrations: true))
            {
                Current = new ObjectPriorityRuleManifest();
                LastLoadStatus = status;
                log.Warning($"[ADS] {LastLoadStatus}");
                return false;
            }

            Current = manifest;
            lastObservedRulesWriteUtc = File.GetLastWriteTimeUtc(configPath);
            LastLoadStatus = status;
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
        => SaveManifest(DefaultPresetName, manifest);

    public bool SaveManifest(string presetName, ObjectPriorityRuleManifest manifest)
    {
        try
        {
            manifest.Rules ??= [];
            var path = EnsurePresetSeeded(presetName);
            WriteManifestToPath(path, manifest);
            if (!IsDefaultPreset(presetName))
            {
                LastLoadStatus = $"Saved {manifest.Rules.Count(x => x.Enabled)} active rule(s) to preset {presetName} at {path}.";
                log.Information($"[ADS] {LastLoadStatus}");
                return true;
            }

            return Reload();
        }
        catch (Exception ex)
        {
            LastLoadStatus = $"Failed to save preset {presetName}: {ex.Message}";
            log.Warning(ex, $"[ADS] {LastLoadStatus}");
            return false;
        }
    }

    public bool TryLoadManifest(string presetName, out ObjectPriorityRuleManifest manifest, out string status)
    {
        manifest = new ObjectPriorityRuleManifest();
        status = "Preset was not loaded.";

        try
        {
            var path = EnsurePresetSeeded(presetName);
            return TryLoadManifestFromPath(path, out manifest, out status, persistMigrations: true);
        }
        catch (Exception ex)
        {
            status = $"Failed to load preset {presetName}: {ex.Message}";
            return false;
        }
    }

    public bool TryLoadBundledManifest(out ObjectPriorityRuleManifest manifest, out string status)
    {
        manifest = new ObjectPriorityRuleManifest();
        status = "Bundled preset was not loaded.";

        try
        {
            var sourcePath = !string.IsNullOrWhiteSpace(bundledPath) && File.Exists(bundledPath)
                ? bundledPath
                : string.Empty;
            if (!string.IsNullOrWhiteSpace(sourcePath))
                return TryLoadManifestFromPath(sourcePath, out manifest, out status, persistMigrations: false);

            var json = GetDefaultJson();
            return TryDeserializeManifest(json, "<packaged default>", out manifest, out status);
        }
        catch (Exception ex)
        {
            status = $"Failed to load packaged preset: {ex.Message}";
            return false;
        }
    }

    public bool TryDeletePreset(string presetName, out string status)
    {
        status = "Preset was not deleted.";
        if (IsDefaultPreset(presetName))
        {
            status = "DEFAULT cannot be deleted.";
            return false;
        }

        try
        {
            var path = GetPresetPath(presetName);
            if (!File.Exists(path))
            {
                status = $"Preset {presetName} did not exist on disk.";
                return false;
            }

            File.Delete(path);
            status = $"Deleted preset {presetName}.";
            return true;
        }
        catch (Exception ex)
        {
            status = $"Failed to delete preset {presetName}: {ex.Message}";
            return false;
        }
    }

    public bool TryImportManifestText(string text, out ObjectPriorityRuleManifest manifest, out string status)
    {
        manifest = new ObjectPriorityRuleManifest();
        status = "Clipboard manifest import failed.";

        if (string.IsNullOrWhiteSpace(text))
        {
            status = "Clipboard was empty; no full-manifest import performed.";
            return false;
        }

        var trimmed = text.Trim();
        if (!trimmed.StartsWith('{'))
        {
            try
            {
                trimmed = Encoding.UTF8.GetString(Convert.FromBase64String(trimmed));
            }
            catch
            {
                // Treat clipboard as raw JSON if it is not valid base64.
            }
        }

        return TryDeserializeManifest(trimmed, "<clipboard>", out manifest, out status);
    }

    public bool TryImportManifestFromPath(string path, out ObjectPriorityRuleManifest manifest, out string status)
    {
        manifest = new ObjectPriorityRuleManifest();
        status = "Disk manifest import failed.";

        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                status = "Disk path was blank; no import performed.";
                return false;
            }

            if (!File.Exists(path))
            {
                status = $"Disk import path did not exist: {path}";
                return false;
            }

            return TryLoadManifestFromPath(path, out manifest, out status, persistMigrations: false);
        }
        catch (Exception ex)
        {
            status = $"Failed to import manifest from {path}: {ex.Message}";
            return false;
        }
    }

    public bool TryExportManifestToPath(string path, ObjectPriorityRuleManifest manifest, out string status)
    {
        status = "Disk manifest export failed.";

        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                status = "Disk path was blank; no export performed.";
                return false;
            }

            WriteManifestToPath(path, manifest);
            status = $"Exported manifest to {path}.";
            return true;
        }
        catch (Exception ex)
        {
            status = $"Failed to export manifest to {path}: {ex.Message}";
            return false;
        }
    }

    public IReadOnlyList<string> GetKnownLayerSelectors(uint territoryTypeId)
    {
        if (territoryTypeId == 0)
            return [];

        var mapSheet = dataManager.GetExcelSheet<Map>();
        if (mapSheet is null)
            return [];

        return mapSheet
            .Where(x => x.TerritoryType.RowId == territoryTypeId)
            .Select(BuildMapName)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public string? GetActiveLayerName(DutyContextSnapshot context)
        => TryGetActiveLayerName(context);

    public bool MatchesCurrentDutyScopeForEditor(ObjectPriorityRule rule, DutyContextSnapshot context)
        => MatchesDutyScope(rule, context, includeLayerScope: false);

    public ObjectPriorityRule? MatchObjectRule(
        DutyContextSnapshot context,
        ObjectKind objectKind,
        uint baseId,
        string objectName,
        Vector3? objectPosition = null,
        uint objectMapId = 0)
        => GetMatchingObjectRules(context, objectKind, baseId, objectName, objectPosition, objectMapId).FirstOrDefault();

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

    public bool ShouldSuppressOffLayerBattleNpcTruth(
        DutyContextSnapshot context,
        uint baseId,
        string objectName,
        Vector3 objectPosition)
    {
        var candidates = GetMatchingObjectRules(
                context,
                ObjectKind.BattleNpc,
                baseId,
                objectName,
                objectPosition,
                objectMapId: 0,
                includeLayerScope: false)
            .ToList();
        if (candidates.Count == 0)
            return false;

        if (candidates.Any(x => string.IsNullOrWhiteSpace(GetLayerSelector(x))))
            return false;

        if (candidates.Any(x => MatchesLayerScope(x, context)))
            return false;

        var activeLayer = TryGetActiveLayerName(context) ?? $"Map {context.MapId}";
        var configuredLayers = string.Join(
            ", ",
            candidates
                .Select(GetLayerSelector)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        var logKey = $"{context.ContentFinderConditionId}:{context.TerritoryTypeId}:{baseId}:{objectName}:{activeLayer}:{configuredLayers}";
        if (loggedOffLayerBattleNpcSuppressions.Add(logKey))
        {
            log.Information(
                $"[ADS] Suppressing visible BattleNpc {objectName} because only layer-scoped rules [{configuredLayers}] match it while the active layer is {activeLayer}.");
        }

        return true;
    }

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
        if (rule is null || RulePassesDistanceGates(rule, distance, verticalDelta))
            return false;

        if (TryParseClassification(rule.Classification, out var classification)
            && classification is InteractableClass.Ignored or InteractableClass.Follow)
        {
            return false;
        }

        return true;
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

    private string EnsurePresetSeeded(string presetName)
    {
        if (IsDefaultPreset(presetName))
        {
            EnsureSeeded();
            return configPath;
        }

        var path = GetPresetPath(presetName);
        if (File.Exists(path))
            return path;

        if (File.Exists(configPath))
        {
            File.Copy(configPath, path, overwrite: false);
            return path;
        }

        if (!string.IsNullOrWhiteSpace(bundledPath) && File.Exists(bundledPath))
        {
            File.Copy(bundledPath, path, overwrite: false);
            return path;
        }

        File.WriteAllText(path, GetDefaultJson());
        return path;
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
        uint objectMapId,
        bool includeLayerScope = true)
    {
        if (!MatchesDutyScope(rule, context, includeLayerScope))
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

    private IEnumerable<ObjectPriorityRule> GetMatchingObjectRules(
        DutyContextSnapshot context,
        ObjectKind objectKind,
        uint baseId,
        string objectName,
        Vector3? objectPosition,
        uint objectMapId,
        bool includeLayerScope = true)
    {
        return Current.Rules
            .Where(x => x.Enabled)
            .Where(x => !IsManualDestinationRule(x))
            .Where(x => Matches(x, context, objectKind, baseId, objectName, objectPosition, objectMapId, includeLayerScope))
            .OrderByDescending(GetSpecificityScore)
            .ThenBy(x => x.Priority);
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

    private string? TryGetActiveLayerName(DutyContextSnapshot context)
    {
        if (context.MapId == 0)
            return null;

        var mapSheet = dataManager.GetExcelSheet<Map>();
        if (mapSheet is null || !mapSheet.TryGetRow(context.MapId, out var map))
            return null;

        return BuildMapName(map);
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
            WaitAfterInteractSeconds = rule.WaitAfterInteractSeconds,
            Notes = rule.Notes,
        };

    private bool TryLoadManifestFromPath(string path, out ObjectPriorityRuleManifest manifest, out string status, bool persistMigrations)
    {
        manifest = new ObjectPriorityRuleManifest();
        status = $"Failed to load manifest from {path}.";

        try
        {
            var json = File.ReadAllText(path);
            if (!TryDeserializeManifest(json, path, out manifest, out status))
                return false;

            var migrated = ApplyBuiltInRuleMigrations(manifest);
            if (migrated && persistMigrations)
                WriteManifestToPath(path, manifest);

            status = migrated
                ? $"Loaded {manifest.Rules.Count(x => x.Enabled)} active rule(s) from {path}; applied built-in rule migration(s)."
                : $"Loaded {manifest.Rules.Count(x => x.Enabled)} active rule(s) from {path}.";
            return true;
        }
        catch (Exception ex)
        {
            status = $"Failed to load manifest from {path}: {ex.Message}";
            return false;
        }
    }

    private static bool TryDeserializeManifest(string json, string sourcePath, out ObjectPriorityRuleManifest manifest, out string status)
    {
        manifest = new ObjectPriorityRuleManifest();
        status = $"Failed to parse manifest from {sourcePath}.";

        try
        {
            manifest = JsonSerializer.Deserialize<ObjectPriorityRuleManifest>(json, JsonOptions) ?? new ObjectPriorityRuleManifest();
            manifest.Rules ??= [];
            return true;
        }
        catch (Exception ex)
        {
            status = $"Failed to parse manifest from {sourcePath}: {ex.Message}";
            return false;
        }
    }

    private static void WriteManifestToPath(string path, ObjectPriorityRuleManifest manifest)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(manifest, JsonOptions);
        File.WriteAllText(path, json);
    }

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

            if (rule.WaitAtDestinationSeconds < 0f)
            {
                rule.WaitAtDestinationSeconds = 0f;
                changed = true;
            }

            if (rule.WaitAfterInteractSeconds < 0f)
            {
                rule.WaitAfterInteractSeconds = 0f;
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
                && string.Equals(rule.Classification, InteractableClass.Required.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                rule.Classification = InteractableClass.CombatFriendly.ToString();
                var migrationNote = "Goblin Pathfinder talk targets should use CombatFriendly so ADS routes them through the direct-interact pipeline.";
                rule.Notes = string.IsNullOrWhiteSpace(rule.Notes)
                    ? migrationNote
                    : rule.Notes.Contains(migrationNote, StringComparison.OrdinalIgnoreCase)
                        ? rule.Notes
                        : $"{rule.Notes} {migrationNote}";
                changed = true;
            }

            if (string.Equals(rule.DutyEnglishName, "Copperbell Mines", StringComparison.OrdinalIgnoreCase)
                && string.Equals(rule.ObjectName, "Copper", StringComparison.OrdinalIgnoreCase)
                && string.Equals(rule.NameMatchMode, "Contains", StringComparison.OrdinalIgnoreCase)
                && string.Equals(rule.Classification, InteractableClass.Ignored.ToString(), StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(rule.ObjectKind))
            {
                rule.ObjectKind = ObjectKind.EventObj.ToString();
                var migrationNote = "Copper ignore row was narrowed to EventObj so it no longer matches Copperbell Coblyn BattleNpc rows on First Drop.";
                rule.Notes = string.IsNullOrWhiteSpace(rule.Notes)
                    ? migrationNote
                    : rule.Notes.Contains(migrationNote, StringComparison.OrdinalIgnoreCase)
                        ? rule.Notes
                        : $"{rule.Notes} {migrationNote}";
                changed = true;
            }

            if (string.Equals(rule.DutyEnglishName, "Copperbell Mines", StringComparison.OrdinalIgnoreCase)
                && string.Equals(rule.ObjectName, "Lift Lever", StringComparison.OrdinalIgnoreCase)
                && string.Equals(rule.NameMatchMode, "Exact", StringComparison.OrdinalIgnoreCase)
                && string.Equals(rule.Classification, InteractableClass.Required.ToString(), StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(rule.Layer)
                && rule.WaitAtDestinationSeconds > 0f
                && (!rule.WaitAfterInteractSeconds.Equals(0f) ? rule.WaitAfterInteractSeconds == rule.WaitAtDestinationSeconds : true))
            {
                rule.WaitAfterInteractSeconds = MathF.Max(rule.WaitAfterInteractSeconds, rule.WaitAtDestinationSeconds);
                rule.WaitAtDestinationSeconds = 0f;
                var migrationNote = "Copperbell generic Lift Lever stale wait row was migrated so the old dead wait field becomes post-interact hold instead of a new pre-interact stall.";
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
  "description": "Human-edited ADS duty object rules. Lower priority wins. Zero numeric ids mean global. Use dutyEnglishName while scouting, then tighten to contentFinderConditionId or territoryTypeId later if needed. classification supports Ignored for sticky non-progression objects, Required for BattleNpc kill priority, BattleNpc-only Follow for live movement anchors such as Cid, BattleNpc-only BossFight for live boss targets that should beat nearby trash/objectives once the rule gates pass, CombatFriendly on BattleNpc or EventNpc for direct-interact talk targets such as Goblin Pathfinder, MapXzDestination with mapCoordinates like 11.3,10.4 for manual sub-area waypoints, and XYZ with worldCoordinates like 154.1,101.9,-34.2 for precise world-space manual staging. objectMapCoordinates or objectWorldCoordinates can pin an ordinary same-name row to one physical object instance, and objectMatchRadius defaults to 6y when left blank on a positional row. layer now means the optional live-map/sub-area selector for any rule: leave it blank for any active layer, or set it to a live map name / map row id to restrict that row to one layer. Legacy destinationType layer rows auto-migrate on load; MapXzDestination rows no longer need destinationType set to MapXZ, and XYZ rows no longer need destinationType set to XYZ. Manual destination rows can also intentionally beat worse live progression interactables when their authored priority is better and no live monsters/follow anchors remain. Non-BattleNpc Follow and BossFight rules are ignored. waitAtDestinationSeconds now means the pre-interact arrival hold, and waitAfterInteractSeconds is the post-interact follow-through hold.",
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
      "waitAfterInteractSeconds": 0.0,
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
      "waitAfterInteractSeconds": 0.0,
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
      "waitAfterInteractSeconds": 0.0,
      "notes": "Sticky optional note remains targetable after use and should not hold planner truth."
    }
  ]
}
""";
}
