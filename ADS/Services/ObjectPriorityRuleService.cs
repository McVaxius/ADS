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
    internal const float BattleNpcPlanningVerticalSanityCap = 100f;
    public const string DefaultPresetName = "DEFAULT";
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
    private readonly ObjectRuleShardStore shardStore;
    private readonly IReadOnlyList<DutyCatalogEntry> dutyCatalog;
    private readonly Configuration? configuration;
    private readonly System.Action? saveConfiguration;
    private readonly System.Action<string>? showToast;
    private readonly HashSet<string> loggedInvalidObjectSpatialRules = new(StringComparer.Ordinal);
    private readonly HashSet<string> loggedOffLayerBattleNpcSuppressions = new(StringComparer.Ordinal);
    private ObjectRulePresetFileState? lastObservedPresetState;
    private DateTime nextReloadPollUtc;
    private string activePresetName = DefaultPresetName;
    private IReadOnlyList<ObjectRuleContextDescriptor> contextDescriptors = [];

    public ObjectPriorityRuleService(
        IPluginLog log,
        IDataManager dataManager,
        string configDirectory)
        : this(log, dataManager, configDirectory, [], null, null, null)
    {
    }

    public ObjectPriorityRuleService(
        IPluginLog log,
        IDataManager dataManager,
        string configDirectory,
        IReadOnlyList<DutyCatalogEntry> dutyCatalog,
        Configuration? configuration,
        System.Action? saveConfiguration,
        System.Action<string>? showToast)
    {
        this.log = log;
        this.dataManager = dataManager;
        this.dutyCatalog = dutyCatalog;
        this.configuration = configuration;
        this.saveConfiguration = saveConfiguration;
        this.showToast = showToast;
        Directory.CreateDirectory(configDirectory);
        shardStore = new ObjectRuleShardStore(configDirectory, dutyCatalog);
        LastSyncStatus = "DEFAULT object rules load from indexed territory shards; the remote updater refreshes the complete validated set from botologyupdates.";

        TryDeserializeManifest(GetDefaultJson(), "<built-in fallback>", out var fallback, out _);
        var pendingLegacyPresets = configuration?.ObjectRuleShardMigrationComplete == true
            ? configuration.PendingLegacyObjectRulePresets
            : null;
        if (shardStore.TryEnsureInitialLayout(fallback, pendingLegacyPresets, out var failedPresets, out var migrationStatus))
        {
            LastSyncStatus = migrationStatus;
            if (configuration is not null
                && (!configuration.ObjectRuleShardMigrationComplete
                    || !configuration.PendingLegacyObjectRulePresets.SequenceEqual(failedPresets, StringComparer.OrdinalIgnoreCase)))
            {
                configuration.ObjectRuleShardMigrationComplete = true;
                configuration.PendingLegacyObjectRulePresets = failedPresets.ToList();
                saveConfiguration?.Invoke();
            }
        }
        else
        {
            LastSyncStatus = $"Object-rule shard migration failed; legacy DEFAULT remains active for this run. {migrationStatus}";
            log.Warning($"[ADS] {LastSyncStatus}");
        }

        activePresetName = string.IsNullOrWhiteSpace(configuration?.ActiveObjectRulePreset)
            ? DefaultPresetName
            : configuration.ActiveObjectRulePreset;
        if (!Reload(notifyActivePreset: true)
            && shardStore.TryLoadLegacyDefault(out var legacy, out var legacyStatus))
        {
            Current = legacy;
            activePresetName = DefaultPresetName;
            PersistActivePreset();
            LastLoadStatus = $"Loaded legacy DEFAULT for this run because the shard store is unavailable. {legacyStatus}";
            log.Warning($"[ADS] {LastLoadStatus}");
            showToast?.Invoke("Object rules active preset: DEFAULT (legacy fallback for this run)");
        }
    }

    public string ConfigPath
        => shardStore.IndexPath;

    public string PresetDirectoryPath
        => shardStore.RootPath;

    public string TerritoriesPath
        => shardStore.RootPath;

    public string ActivePresetName
        => activePresetName;

    public string LastLoadStatus { get; private set; } = "Rules not loaded yet.";

    public string LastSyncStatus { get; private set; }

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
        => shardStore.GetPresetNames();

    public string GetPresetPath(string presetName)
        => IsDefaultPreset(presetName)
            ? shardStore.RootPath
            : shardStore.GetPresetDirectoryPath(presetName);

    public string SanitizePresetName(string presetName)
    {
        return ObjectRuleShardStore.SanitizePresetName(presetName);
    }

    public bool Reload()
        => Reload(notifyActivePreset: false);

    private bool Reload(bool notifyActivePreset)
    {
        try
        {
            var fallbackToastShown = false;
            if (!shardStore.TryLoadEffectivePreset(activePresetName, out var manifest, out var effectiveShards, out var status))
            {
                if (IsDefaultPreset(activePresetName)
                    || !shardStore.TryLoadEffectivePreset(DefaultPresetName, out manifest, out effectiveShards, out var fallbackStatus))
                {
                    LastLoadStatus = status;
                    lastObservedPresetState = shardStore.CapturePresetState(activePresetName);
                    log.Warning($"[ADS] {LastLoadStatus}");
                    return false;
                }

                var missingPreset = activePresetName;
                activePresetName = DefaultPresetName;
                PersistActivePreset();
                status = $"Active preset {missingPreset} was missing or invalid and fell back to DEFAULT. {fallbackStatus}";
                showToast?.Invoke($"Object rules active preset: DEFAULT ({missingPreset} was unavailable)");
                fallbackToastShown = true;
            }

            Current = manifest;
            contextDescriptors = shardStore.CreateContextDescriptors(activePresetName, effectiveShards);
            lastObservedPresetState = shardStore.CapturePresetState(activePresetName);
            LastLoadStatus = status;
            log.Information($"[ADS] {LastLoadStatus}");
            if (notifyActivePreset && !fallbackToastShown)
                showToast?.Invoke($"Object rules active preset: {activePresetName}");
            return true;
        }
        catch (Exception ex)
        {
            lastObservedPresetState = shardStore.CapturePresetState(activePresetName);
            LastLoadStatus = $"Failed to load object-rule territory shards: {ex.Message}";
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
            var currentState = shardStore.CapturePresetState(activePresetName);
            if (lastObservedPresetState is not null && currentState.SameAs(lastObservedPresetState))
                return false;
            lastObservedPresetState = currentState;
            return Reload(notifyActivePreset: false);
        }
        catch (Exception ex)
        {
            LastLoadStatus = $"Failed to check object-rule territory shards: {ex.Message}";
            log.Warning(ex, $"[ADS] {LastLoadStatus}");
            return false;
        }
    }

    internal void RetainCurrentAfterFailedRemoteUpdate()
    {
        try
        {
            lastObservedPresetState = shardStore.CapturePresetState(activePresetName);
            nextReloadPollUtc = DateTime.UtcNow + ReloadPollInterval;
            log.Warning("[ADS] Retained the current in-memory object rules after a failed remote shard apply.");
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[ADS] Could not snapshot the failed remote shard disk state; current object rules remain unchanged for this frame.");
        }
    }

    public bool SaveManifest(ObjectPriorityRuleManifest manifest)
        => SaveManifest(DefaultPresetName, manifest);

    public bool SaveManifest(string presetName, ObjectPriorityRuleManifest manifest)
    {
        if (!shardStore.TryWriteFullPreset(presetName, manifest, out var status))
        {
            LastLoadStatus = status;
            log.Warning($"[ADS] {LastLoadStatus}");
            return false;
        }
        LastLoadStatus = status;
        return ActivatePreset(presetName, notify: !IsDefaultPreset(presetName));
    }

    public bool SaveChangedContexts(
        string presetName,
        ObjectPriorityRuleManifest baseline,
        ObjectPriorityRuleManifest manifest,
        bool allowDefaultWrite,
        out IReadOnlyList<string> changedFiles,
        out string status)
    {
        changedFiles = [];
        if (IsDefaultPreset(presetName) && !allowDefaultWrite)
        {
            status = "DEFAULT is protected. Create a custom preset, or enable /ads debug on for this session to save DEFAULT shards directly.";
            LastLoadStatus = status;
            return false;
        }
        if (!shardStore.TryGetChangedContextFiles(baseline, manifest, out changedFiles, out status))
        {
            LastLoadStatus = status;
            return false;
        }
        if (!shardStore.TryWriteChangedContexts(presetName, manifest, changedFiles, out status))
        {
            LastLoadStatus = status;
            return false;
        }
        if (string.Equals(presetName, activePresetName, StringComparison.OrdinalIgnoreCase) && !Reload())
        {
            status = LastLoadStatus;
            return false;
        }
        LastLoadStatus = status;
        return true;
    }

    public bool TryLoadManifest(string presetName, out ObjectPriorityRuleManifest manifest, out string status)
    {
        if (!shardStore.TryLoadEffectivePreset(presetName, out manifest, out var effectiveShards, out status))
            return false;
        if (string.Equals(presetName, activePresetName, StringComparison.OrdinalIgnoreCase))
            contextDescriptors = shardStore.CreateContextDescriptors(activePresetName, effectiveShards);
        return true;
    }

    public bool TryLoadDefaultCacheManifest(out ObjectPriorityRuleManifest manifest, out string status)
        => shardStore.TryLoadEffectivePreset(DefaultPresetName, out manifest, out _, out status);

    public bool ActivatePreset(string presetName, bool notify = true)
    {
        if (!shardStore.TryLoadEffectivePreset(presetName, out var manifest, out var effectiveShards, out var status))
        {
            LastLoadStatus = status;
            return false;
        }
        activePresetName = IsDefaultPreset(presetName) ? DefaultPresetName : SanitizePresetName(presetName);
        Current = manifest;
        contextDescriptors = shardStore.CreateContextDescriptors(activePresetName, effectiveShards);
        lastObservedPresetState = shardStore.CapturePresetState(activePresetName);
        PersistActivePreset();
        LastLoadStatus = status;
        log.Information($"[ADS] Active object-rule preset is {activePresetName}. {status}");
        if (notify)
            showToast?.Invoke($"Object rules active preset: {activePresetName}");
        return true;
    }

    public bool TryDeletePreset(string presetName, out string status)
    {
        status = "Preset was not deleted.";
        if (IsDefaultPreset(presetName))
        {
            status = "DEFAULT cannot be deleted.";
            return false;
        }

        if (!shardStore.TryDeletePreset(presetName, out status))
            return false;
        if (string.Equals(activePresetName, presetName, StringComparison.OrdinalIgnoreCase))
        {
            activePresetName = DefaultPresetName;
            PersistActivePreset();
            if (!Reload())
            {
                status = $"{status} DEFAULT fallback failed: {LastLoadStatus}";
                return false;
            }
            showToast?.Invoke($"Object rules active preset: DEFAULT ({presetName} was deleted)");
        }
        return true;
    }

    public bool TryRevertContextToDefault(string presetName, string fileName, out string status)
    {
        if (!TryRevertContextsToDefault(presetName, [fileName], out var deletedFiles, out _, out status))
            return false;
        return deletedFiles.Count == 1;
    }

    internal bool TryRevertContextsToDefault(
        string presetName,
        IEnumerable<string> fileNames,
        out IReadOnlyList<string> deletedFiles,
        out IReadOnlyList<string> skippedFiles,
        out string status)
    {
        if (!shardStore.TryDeleteOverrides(presetName, fileNames, out deletedFiles, out skippedFiles, out status))
            return false;
        if (deletedFiles.Count > 0
            && string.Equals(activePresetName, presetName, StringComparison.OrdinalIgnoreCase)
            && !Reload())
        {
            status = $"{status} Reload failed: {LastLoadStatus}";
            return false;
        }
        return true;
    }

    public bool HasContextOverride(string presetName, string fileName)
        => shardStore.HasOverride(presetName, fileName);

    public string GetContextShardPath(string presetName, string fileName)
        => shardStore.GetShardPath(presetName, fileName);

    internal ObjectRulePresetFileState CapturePresetFileState(string presetName)
        => shardStore.CapturePresetState(presetName);

    internal ObjectRulePresetFileState CaptureContextFileState(string presetName, IEnumerable<string> fileNames)
        => shardStore.CaptureContextState(presetName, fileNames);

    internal bool TryGetChangedContextFiles(ObjectPriorityRuleManifest baseline, ObjectPriorityRuleManifest draft, out IReadOnlyList<string> changedFiles, out string status)
        => shardStore.TryGetChangedContextFiles(baseline, draft, out changedFiles, out status);

    internal IReadOnlyList<string> GetContextFileNames(ObjectPriorityRuleManifest manifest)
        => shardStore.GetContextFileNames(manifest);

    public IReadOnlyList<string> GetAvailableContextFileNames(string presetName)
        => string.Equals(presetName, activePresetName, StringComparison.OrdinalIgnoreCase)
            ? contextDescriptors.Select(descriptor => descriptor.FileName).ToList()
            : shardStore.TryLoadEffectivePreset(presetName, out _, out var shards, out _)
                ? ObjectRuleShardStore.SortFileNames(shards.Keys)
                : [];

    internal IReadOnlyList<ObjectRuleContextDescriptor> GetContextDescriptors(
        ObjectPriorityRuleManifest draft,
        ObjectPriorityRuleManifest baseline,
        uint currentTerritoryTypeId)
    {
        var byFile = contextDescriptors.ToDictionary(descriptor => descriptor.FileName, StringComparer.OrdinalIgnoreCase);
        if (!shardStore.TrySplitManifest(baseline, out var before, out _)
            || !shardStore.TrySplitManifest(draft, out var after, out _))
            return contextDescriptors;

        var changed = before.Keys.Concat(after.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(fileName => !ObjectRuleShardStore.RuleListsEqual(
                before.GetValueOrDefault(fileName)?.Rules ?? [],
                after.GetValueOrDefault(fileName)?.Rules ?? []))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fileNames = new HashSet<string>(byFile.Keys, StringComparer.OrdinalIgnoreCase);
        fileNames.Add(ObjectRuleShardStore.GlobalFileName);
        foreach (var territoryTypeId in dutyCatalog.Select(entry => entry.TerritoryTypeId).Where(id => id != 0))
            fileNames.Add(ObjectRuleShardStore.GetTerritoryFileName(territoryTypeId));
        fileNames.UnionWith(before.Keys);
        fileNames.UnionWith(after.Keys);
        if (currentTerritoryTypeId != 0)
            fileNames.Add(ObjectRuleShardStore.GetTerritoryFileName(currentTerritoryTypeId));

        return ObjectRuleShardStore.SortFileNames(fileNames).Select(fileName =>
        {
            if (!byFile.TryGetValue(fileName, out var descriptor))
            {
                ObjectRuleShardStore.TryParseCanonicalFileName(fileName, out var territoryTypeId);
                descriptor = new ObjectRuleContextDescriptor(
                    fileName,
                    territoryTypeId,
                    ResolveTerritoryName(territoryTypeId),
                    IsDefaultPreset(activePresetName),
                    false,
                    false,
                    0,
                    false,
                    false,
                    false);
            }

            var rowCount = after.TryGetValue(fileName, out var draftShard)
                ? draftShard.Rules.Count
                : changed.Contains(fileName)
                    ? 0
                    : descriptor.EffectiveRowCount;
            return descriptor with
            {
                EffectiveRowCount = rowCount,
                HasUnsavedChanges = changed.Contains(fileName),
            };
        }).ToList();
    }

    private string ResolveTerritoryName(uint? territoryTypeId)
    {
        if (!territoryTypeId.HasValue)
            return "Global";
        var names = dutyCatalog
            .Where(entry => entry.TerritoryTypeId == territoryTypeId.Value)
            .Select(entry => entry.EnglishName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToList();
        return names.Count switch
        {
            0 => $"Territory {territoryTypeId.Value}",
            1 => names[0],
            _ => string.Join(" / ", names),
        };
    }

    private void PersistActivePreset()
    {
        if (configuration is null)
            return;
        configuration.ActiveObjectRulePreset = activePresetName;
        saveConfiguration?.Invoke();
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

            return TryLoadManifestFromPath(path, out manifest, out status);
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

    internal bool MatchesEditorDistancePreviewObject(
        ObjectPriorityRule rule,
        DutyContextSnapshot context,
        RuleDistancePreviewObject liveObject)
        => !IsManualDestinationRule(rule)
           && !IsCardinalHoldRule(rule)
           && Matches(
               rule,
               context,
               liveObject.ObjectKind,
               liveObject.BaseId,
               liveObject.Name,
               liveObject.Position,
               liveObject.MapId);

    internal Vector3? ResolveEditorDistancePreviewMapCoordinates(
        DutyContextSnapshot context,
        Vector2 mapCoordinates,
        float playerY)
    {
        if (context.MapId == 0
            || !TryResolveMapRow(context.MapId, context.TerritoryTypeId, out var map))
        {
            return null;
        }

        return ConvertMapCoordinatesToWorld(mapCoordinates, map, playerY);
    }

    public ObjectPriorityRule? MatchObjectRule(
        DutyContextSnapshot context,
        ObjectKind objectKind,
        uint baseId,
        string objectName,
        Vector3? objectPosition = null,
        uint objectMapId = 0)
        => GetMatchingObjectRules(context, objectKind, baseId, objectName, objectPosition, objectMapId).FirstOrDefault();

    public IReadOnlyList<ObjectPriorityRule> GetExplorerMatches(
        DutyContextSnapshot context,
        ObjectKind objectKind,
        uint baseId,
        string objectName,
        Vector3? objectPosition = null,
        uint objectMapId = 0)
        => GetMatchingObjectRules(context, objectKind, baseId, objectName, objectPosition, objectMapId, includeLayerScope: false).ToList();

    public bool MatchesCurrentLayerForExplorer(
        DutyContextSnapshot context,
        ObjectKind objectKind,
        uint baseId,
        string objectName,
        Vector3? objectPosition = null,
        uint objectMapId = 0)
    {
        var candidates = GetMatchingObjectRules(context, objectKind, baseId, objectName, objectPosition, objectMapId, includeLayerScope: false)
            .ToList();
        if (candidates.Count == 0)
            return true;

        var scopedCandidates = candidates
            .Where(x => !string.IsNullOrWhiteSpace(GetLayerSelector(x)))
            .ToList();
        if (scopedCandidates.Count == 0)
            return true;

        return scopedCandidates.Any(x => MatchesLayerScope(x, context));
    }

    public string DescribeRuleScope(ObjectPriorityRule rule)
    {
        var parts = new List<string>();
        if (rule.ContentFinderConditionId != 0)
            parts.Add($"CFC {rule.ContentFinderConditionId}");
        if (rule.TerritoryTypeId != 0)
            parts.Add($"Terr {rule.TerritoryTypeId}");
        if (!string.IsNullOrWhiteSpace(rule.DutyEnglishName))
            parts.Add(rule.DutyEnglishName);
        if (!string.IsNullOrWhiteSpace(rule.Alliance))
            parts.Add($"Alliance {rule.Alliance.Trim()}");

        var selector = GetLayerSelector(rule);
        if (!string.IsNullOrWhiteSpace(selector))
            parts.Add($"Layer {selector}");

        return parts.Count == 0
            ? "Global"
            : string.Join(" | ", parts);
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

    public CardinalHoldRule? GetActiveCardinalHoldRule(
        DutyContextSnapshot context,
        Vector3 playerPosition,
        Func<string, bool> isGhosted)
        => CardinalHoldPolicy.SelectActive(
            Current.Rules,
            playerPosition,
            rule => MatchesDutyScope(rule, context, includeLayerScope: true),
            isGhosted);

    public bool DestinationRulePassesDistanceGates(ObjectPriorityRule rule, Vector3 playerPosition, Vector3 destinationPosition)
    {
        var distance = Vector3.Distance(destinationPosition, playerPosition);
        var verticalDelta = MathF.Abs(destinationPosition.Y - playerPosition.Y);
        return RulePassesDistanceGates(rule, distance, verticalDelta);
    }

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

    public bool TryGetEffectiveBattleNpcClassificationOverride(
        DutyContextSnapshot context,
        uint baseId,
        string objectName,
        Vector3 objectPosition,
        uint objectMapId,
        float? distance,
        float? verticalDelta,
        out InteractableClass classification)
    {
        classification = default;
        var rule = ResolveObjectRule(
                context,
                ObjectKind.BattleNpc,
                baseId,
                objectName,
                objectPosition,
                objectMapId,
                distance,
                verticalDelta)
            .EffectiveRule;
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
        => ResolveObjectRule(
                context,
                interactable.ObjectKind,
                interactable.DataId,
                interactable.Name,
                interactable.Position,
                interactable.MapId,
                distance,
                verticalDelta)
            .EffectiveRule;

    internal ObjectPriorityRule? GetEligibleAuthoredLiveObjectRule(
        DutyContextSnapshot context,
        ObservedInteractable interactable,
        float? distance,
        float? verticalDelta)
    {
        var rule = GetEffectiveRule(context, interactable, distance, verticalDelta);
        return rule is not null && IsAuthoredLiveObjectTruthRule(rule)
            ? rule
            : null;
    }

    public ObjectPriorityRule? GetEffectiveBattleNpcRule(
        DutyContextSnapshot context,
        ObservedMonster monster,
        float? distance,
        float? verticalDelta)
        => ResolveObjectRule(
                context,
                ObjectKind.BattleNpc,
                monster.DataId,
                monster.Name,
                monster.Position,
                monster.MapId,
                distance,
                verticalDelta)
            .EffectiveRule;

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
        var resolution = ResolveObjectRule(
            context,
            ObjectKind.BattleNpc,
            monster.DataId,
            monster.Name,
            monster.Position,
            monster.MapId,
            distance,
            verticalDelta);
        return IsBattleNpcSuppressedByRuleGates(resolution);
    }

    internal BattleNpcPlanningEligibility EvaluateBattleNpcPlanningEligibility(
        DutyContextSnapshot context,
        ObservedMonster monster,
        Vector3? playerPosition)
    {
        var distance = playerPosition.HasValue
            ? Vector3.Distance(playerPosition.Value, monster.Position)
            : (float?)null;
        var verticalDelta = playerPosition.HasValue
            ? MathF.Abs(playerPosition.Value.Y - monster.Position.Y)
            : (float?)null;
        var resolution = ResolveObjectRule(
            context,
            ObjectKind.BattleNpc,
            monster.DataId,
            monster.Name,
            monster.Position,
            monster.MapId,
            distance,
            verticalDelta);
        var effectiveClassification = resolution.EffectiveRule is not null
                                      && TryParseClassification(resolution.EffectiveRule.Classification, out var classification)
            ? classification
            : (InteractableClass?)null;

        return new BattleNpcPlanningEligibility(
            monster,
            distance,
            verticalDelta,
            resolution.EffectiveRule,
            effectiveClassification,
            !verticalDelta.HasValue || verticalDelta.Value <= BattleNpcPlanningVerticalSanityCap,
            IsBattleNpcSuppressedByRuleGates(resolution));
    }

    internal IReadOnlyList<BattleNpcPlanningEligibility> EvaluateBattleNpcPlanningEligibility(
        DutyContextSnapshot context,
        IEnumerable<ObservedMonster> monsters,
        Vector3? playerPosition)
        => monsters
            .Select(monster => EvaluateBattleNpcPlanningEligibility(context, monster, playerPosition))
            .ToList();

    public bool IsSuppressedByRuleGates(
        DutyContextSnapshot context,
        ObservedInteractable interactable,
        float? distance,
        float? verticalDelta)
    {
        var resolution = ResolveObjectRule(
            context,
            interactable.ObjectKind,
            interactable.DataId,
            interactable.Name,
            interactable.Position,
            interactable.MapId,
            distance,
            verticalDelta);
        return resolution.MatchedRule is not null && resolution.EffectiveRule is null;
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
        var rule = ResolveObjectRule(context, objectKind, baseId, objectName, null, 0, distance, verticalDelta).EffectiveRule;
        return rule is not null
            && IsIgnoredRule(rule);
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
        var rule = ResolveObjectRule(context, objectKind, baseId, objectName, objectPosition, objectMapId, distance, verticalDelta).EffectiveRule;
        return rule is not null
            && IsIgnoredRule(rule);
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

        var rule = ResolveObjectRule(context, objectKind, baseId, objectName, null, 0, distance, verticalDelta).EffectiveRule;
        return rule is not null
            && IsFollowRule(rule);
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

        var rule = ResolveObjectRule(context, objectKind, baseId, objectName, objectPosition, objectMapId, distance, verticalDelta).EffectiveRule;
        return rule is not null
            && IsFollowRule(rule);
    }

    private static bool RulePassesDistanceGates(ObjectPriorityRule rule, float? distance, float? verticalDelta)
    {
        if (rule.PriorityVerticalRadius > 0f && verticalDelta.HasValue && verticalDelta.Value > rule.PriorityVerticalRadius)
            return false;

        if (rule.MaxDistance.HasValue && distance.HasValue && distance.Value > rule.MaxDistance.Value)
            return false;

        return true;
    }

    private ObjectRuleResolution ResolveObjectRule(
        DutyContextSnapshot context,
        ObjectKind objectKind,
        uint baseId,
        string objectName,
        Vector3? objectPosition,
        uint objectMapId,
        float? distance,
        float? verticalDelta)
    {
        var matchingRules = GetMatchingObjectRules(
                context,
                objectKind,
                baseId,
                objectName,
                objectPosition,
                objectMapId)
            .ToList();
        return new ObjectRuleResolution(
            matchingRules.FirstOrDefault(),
            matchingRules.FirstOrDefault(rule => RulePassesDistanceGates(rule, distance, verticalDelta)));
    }

    private static bool IsBattleNpcSuppressedByRuleGates(ObjectRuleResolution resolution)
    {
        if (resolution.MatchedRule is null || resolution.EffectiveRule is not null)
            return false;

        return !TryParseClassification(resolution.MatchedRule.Classification, out var classification)
               || classification is not (InteractableClass.Ignored or InteractableClass.Follow);
    }

    private readonly record struct ObjectRuleResolution(
        ObjectPriorityRule? MatchedRule,
        ObjectPriorityRule? EffectiveRule);

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

        if (!MatchesConfiguredObjectKind(rule, objectKind, objectName))
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

    private static bool MatchesConfiguredObjectKind(ObjectPriorityRule rule, ObjectKind objectKind, string objectName)
    {
        if (string.IsNullOrWhiteSpace(rule.ObjectKind))
            return true;

        if (string.Equals(rule.ObjectKind, objectKind.ToString(), StringComparison.OrdinalIgnoreCase))
            return true;

        return IsTreasureCofferKindAlias(rule, objectKind, objectName);
    }

    private static bool IsTreasureCofferKindAlias(ObjectPriorityRule rule, ObjectKind objectKind, string objectName)
    {
        if (!LooksLikeTreasureCofferRule(rule) || !LooksLikeTreasureCofferName(objectName))
            return false;

        var configuredKindIsTreasure = string.Equals(rule.ObjectKind, ObjectKind.Treasure.ToString(), StringComparison.OrdinalIgnoreCase);
        var configuredKindIsEvent = string.Equals(rule.ObjectKind, ObjectKind.EventObj.ToString(), StringComparison.OrdinalIgnoreCase)
                                    || string.Equals(rule.ObjectKind, ObjectKind.EventNpc.ToString(), StringComparison.OrdinalIgnoreCase);
        var observedKindIsTreasure = objectKind == ObjectKind.Treasure;
        var observedKindIsEvent = objectKind is ObjectKind.EventObj or ObjectKind.EventNpc;
        return (configuredKindIsTreasure && observedKindIsEvent)
               || (configuredKindIsEvent && observedKindIsTreasure);
    }

    private static bool LooksLikeTreasureCofferRule(ObjectPriorityRule rule)
        => (TryParseClassification(rule.Classification, out var classification)
            && classification == InteractableClass.TreasureCoffer)
           || string.Equals(NormalizeName(rule.ObjectName), "Treasure Coffer", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeTreasureCofferName(string objectName)
    {
        var normalized = NormalizeName(objectName);
        return normalized.Contains("coffer", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("sack", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("treasure chest", StringComparison.OrdinalIgnoreCase)
               || normalized.Equals("chest", StringComparison.OrdinalIgnoreCase)
               || normalized.EndsWith(" chest", StringComparison.OrdinalIgnoreCase);
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
            .Where(x => !IsManualDestinationRule(x) && !IsCardinalHoldRule(x))
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

        var configuredAlliance = rule.Alliance?.Trim();
        if (!string.IsNullOrWhiteSpace(configuredAlliance)
            && (!AllianceScopeParser.IsValidScope(configuredAlliance)
                || !AllianceScopeParser.IsValidScope(context.Alliance)
                || !string.Equals(configuredAlliance, context.Alliance, StringComparison.OrdinalIgnoreCase)))
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

    internal static bool IsAuthoredLiveObjectTruthRule(ObjectPriorityRule rule)
        => !IsManualDestinationRule(rule)
           && !IsCardinalHoldRule(rule)
           && TryParseClassification(rule.Classification, out var classification)
           && classification != InteractableClass.Ignored;

    private static bool IsFollowRule(ObjectPriorityRule rule)
        => TryParseClassification(rule.Classification, out var classification)
           && classification == InteractableClass.Follow;

    private static bool IsMapXzDestinationRule(ObjectPriorityRule rule)
        => string.Equals(rule.DestinationType, MapXzDestinationType, StringComparison.OrdinalIgnoreCase)
           || (TryParseClassification(rule.Classification, out var classification)
               && classification is InteractableClass.MapXzDestination or InteractableClass.MapXzForceMarch);

    private static bool IsXyzDestinationRule(ObjectPriorityRule rule)
        => string.Equals(rule.DestinationType, XyzDestinationType, StringComparison.OrdinalIgnoreCase)
           || (TryParseClassification(rule.Classification, out var classification)
               && classification is InteractableClass.XYZ or InteractableClass.XYZForceMarch);

    private static bool IsManualDestinationRule(ObjectPriorityRule rule)
        => IsMapXzDestinationRule(rule) || IsXyzDestinationRule(rule);

    private static bool IsCardinalHoldRule(ObjectPriorityRule rule)
        => CardinalHoldPolicy.TryParseDirection(rule.Classification, out _);

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
            Alliance = rule.Alliance,
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

    private bool TryLoadManifestFromPath(string path, out ObjectPriorityRuleManifest manifest, out string status)
    {
        manifest = new ObjectPriorityRuleManifest();
        status = $"Failed to load manifest from {path}.";

        try
        {
            var json = File.ReadAllText(path);
            if (!TryDeserializeManifest(json, path, out manifest, out status))
                return false;

            status = $"Loaded {manifest.Rules.Count(x => x.Enabled)} active rule(s) from {path}.";
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

            if (string.Equals(rule.DutyEnglishName, "Copperbell Mines", StringComparison.OrdinalIgnoreCase)
                && string.Equals(rule.ObjectName, "Lift Lever", StringComparison.OrdinalIgnoreCase)
                && string.Equals(rule.ObjectKind, ObjectKind.BattleNpc.ToString(), StringComparison.OrdinalIgnoreCase)
                && string.Equals(rule.Classification, InteractableClass.Required.ToString(), StringComparison.OrdinalIgnoreCase)
                && rule.Enabled)
            {
                rule.Enabled = false;
                var migrationNote = "Disabled stale Copperbell BattleNpc Lift Lever row; generic non-kind Lift Lever plus positional ignored rows handle lever interaction.";
                rule.Notes = string.IsNullOrWhiteSpace(rule.Notes)
                    ? migrationNote
                    : rule.Notes.Contains(migrationNote, StringComparison.OrdinalIgnoreCase)
                        ? rule.Notes
                        : $"{rule.Notes} {migrationNote}";
                changed = true;
            }

            if (string.Equals(rule.DutyEnglishName, "Copperbell Mines", StringComparison.OrdinalIgnoreCase)
                && TryParseClassification(rule.Classification, out ruleClassification)
                && ruleClassification == InteractableClass.XYZ
                && string.IsNullOrWhiteSpace(rule.WorldCoordinates)
                && string.IsNullOrWhiteSpace(rule.MapCoordinates)
                && !string.IsNullOrWhiteSpace(rule.ObjectWorldCoordinates)
                && rule.Enabled)
            {
                rule.Enabled = false;
                var migrationNote = "Disabled stale malformed Copperbell XYZ row that stored the destination in ObjectWorldCoordinates; use WorldCoordinates on the fixed post-door XYZ row.";
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

            if (string.Equals(rule.DutyEnglishName, "Castrum Meridianum", StringComparison.OrdinalIgnoreCase)
                && string.Equals(rule.ObjectKind, ObjectKind.EventNpc.ToString(), StringComparison.OrdinalIgnoreCase)
                && string.Equals(rule.Classification, InteractableClass.Ignored.ToString(), StringComparison.OrdinalIgnoreCase)
                && (string.Equals(rule.ObjectName, "Cid", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(rule.ObjectName, "Livia sas Junius", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(rule.ObjectName, "The Black Eft", StringComparison.OrdinalIgnoreCase)))
            {
                rule.ObjectKind = string.Empty;
                var migrationNote = "Castrum Meridianum companion ignore rows were widened from stale EventNpc-only matching so they still suppress live BattleNpc truth.";
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

    private static float ConvertMapCoordinateToWorld(float mapCoordinate, uint sizeFactor, int offset)
    {
        if (sizeFactor == 0)
            return 0f;

        return (50f * (mapCoordinate - 1f - (2048f / sizeFactor))) - offset;
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
      "territoryTypeId": 1037,
      "contentFinderConditionId": 2,
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
      "territoryTypeId": 1037,
      "contentFinderConditionId": 2,
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
