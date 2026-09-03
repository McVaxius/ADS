using System.Text.Json;
using System.Text.RegularExpressions;
using ADS.Models;

namespace ADS.Services;

internal sealed partial class ObjectRuleShardStore
{
    public const int SchemaVersion = 1;
    public const string DirectoryName = "territories";
    public const string IndexFileName = "index.json";
    public const string GlobalFileName = "GLOBAL_rule_objects.json";
    public const string LegacyFileName = "duty-object-rules.json";
    public const string LegacyPresetDirectoryName = "rule-presets";
    public const string LegacyMaturePresetName = "MATURE-PROPOSALS";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    private readonly string configDirectory;
    private readonly IReadOnlyList<DutyCatalogEntry> dutyCatalog;

    public ObjectRuleShardStore(string configDirectory, IReadOnlyList<DutyCatalogEntry>? dutyCatalog)
    {
        this.configDirectory = Path.GetFullPath(configDirectory);
        this.dutyCatalog = dutyCatalog ?? [];
        RootPath = Path.Combine(this.configDirectory, DirectoryName);
        IndexPath = Path.Combine(RootPath, IndexFileName);
        LegacyPath = Path.Combine(this.configDirectory, LegacyFileName);
        LegacyPresetDirectoryPath = Path.Combine(this.configDirectory, LegacyPresetDirectoryName);
    }

    public string RootPath { get; }
    public string IndexPath { get; }
    public string LegacyPath { get; }
    public string LegacyPresetDirectoryPath { get; }

    public static string GetTerritoryFileName(uint territoryTypeId)
        => territoryTypeId == 0
            ? throw new ArgumentOutOfRangeException(nameof(territoryTypeId), "Territory shard ids must be non-zero.")
            : $"{territoryTypeId}_rule_objects.json";

    public string GetPresetDirectoryPath(string presetName)
        => Path.Combine(RootPath, SanitizePresetName(presetName));

    public string GetShardPath(string presetName, string fileName)
        => string.Equals(presetName, ObjectPriorityRuleService.DefaultPresetName, StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(RootPath, fileName)
            : Path.Combine(GetPresetDirectoryPath(presetName), fileName);

    public static string SanitizePresetName(string presetName)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var cleaned = new string((presetName ?? string.Empty).Where(ch => !invalidCharacters.Contains(ch)).ToArray());
        cleaned = string.Join(' ', cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Trim('.', ' ');
        if (string.IsNullOrWhiteSpace(cleaned))
            cleaned = "Preset";
        if (string.Equals(cleaned, ObjectPriorityRuleService.DefaultPresetName, StringComparison.OrdinalIgnoreCase))
            cleaned = $"{ObjectPriorityRuleService.DefaultPresetName}-copy";
        if (string.Equals(cleaned, LegacyMaturePresetName, StringComparison.OrdinalIgnoreCase))
            cleaned = $"{LegacyMaturePresetName}-copy";
        return cleaned;
    }

    public bool TryLoadEffectivePreset(
        string presetName,
        out ObjectPriorityRuleManifest manifest,
        out IReadOnlyDictionary<string, ObjectPriorityRuleManifest> effectiveShards,
        out string status)
    {
        manifest = new ObjectPriorityRuleManifest();
        effectiveShards = new Dictionary<string, ObjectPriorityRuleManifest>(StringComparer.OrdinalIgnoreCase);
        if (!TryLoadDefaultShards(out var defaultShards, out status))
            return false;

        var combined = new Dictionary<string, ObjectPriorityRuleManifest>(defaultShards, StringComparer.OrdinalIgnoreCase);
        if (!string.Equals(presetName, ObjectPriorityRuleService.DefaultPresetName, StringComparison.OrdinalIgnoreCase))
        {
            var presetPath = GetPresetDirectoryPath(presetName);
            if (!Directory.Exists(presetPath))
            {
                status = $"Preset {presetName} does not exist at {presetPath}.";
                return false;
            }

            foreach (var path in Directory.EnumerateFiles(presetPath, "*_rule_objects.json", SearchOption.TopDirectoryOnly))
            {
                var fileName = Path.GetFileName(path);
                if (!TryParseCanonicalFileName(fileName, out _))
                {
                    status = $"Preset {presetName} contains an unknown shard filename: {fileName}.";
                    return false;
                }

                if (!TryLoadAndValidateShard(path, fileName, out var shard, out status))
                    return false;
                combined[fileName] = shard;
            }
        }

        var orderedFiles = SortFileNames(combined.Keys);
        manifest = new ObjectPriorityRuleManifest
        {
            SchemaVersion = SchemaVersion,
            Description = string.Equals(presetName, ObjectPriorityRuleService.DefaultPresetName, StringComparison.OrdinalIgnoreCase)
                ? "Effective ADS DEFAULT territory shards."
                : $"Effective ADS object rules for preset {presetName}; missing contexts inherit DEFAULT.",
            Rules = orderedFiles.SelectMany(file => combined[file].Rules.Select(CloneRule)).ToList(),
        };
        effectiveShards = combined;
        status = $"Loaded {manifest.Rules.Count(rule => rule.Enabled)} active rule(s) from {combined.Count} effective shard(s) for {presetName}.";
        return true;
    }

    public bool TryLoadDefaultShards(out Dictionary<string, ObjectPriorityRuleManifest> shards, out string status)
    {
        shards = new Dictionary<string, ObjectPriorityRuleManifest>(StringComparer.OrdinalIgnoreCase);
        if (!TryLoadIndex(IndexPath, out var index, out status))
            return false;

        foreach (var fileName in index.Files)
        {
            var path = Path.Combine(RootPath, fileName);
            if (!File.Exists(path))
            {
                status = $"Indexed DEFAULT shard is missing: {path}.";
                return false;
            }

            if (!TryLoadAndValidateShard(path, fileName, out var shard, out status))
                return false;
            shards.Add(fileName, shard);
        }

        status = $"Loaded {shards.Count} DEFAULT territory shard(s) from {IndexPath}.";
        return true;
    }

    public bool TryWriteChangedContexts(
        string presetName,
        ObjectPriorityRuleManifest effectiveManifest,
        IReadOnlyCollection<string> changedFiles,
        out string status)
    {
        status = "No object-rule contexts were saved.";
        if (changedFiles.Count == 0)
            return true;
        if (!TrySplitManifest(effectiveManifest, out var split, out status))
            return false;

        foreach (var fileName in changedFiles)
        {
            if (!TryParseCanonicalFileName(fileName, out _))
            {
                status = $"Cannot save unknown shard filename {fileName}.";
                return false;
            }
        }

        try
        {
            var isDefault = string.Equals(presetName, ObjectPriorityRuleService.DefaultPresetName, StringComparison.OrdinalIgnoreCase);
            ObjectPriorityRuleShardIndex? defaultIndex = null;
            if (isDefault)
            {
                if (!TryLoadIndex(IndexPath, out defaultIndex, out status))
                    return false;
            }

            var directory = isDefault ? RootPath : GetPresetDirectoryPath(presetName);
            Directory.CreateDirectory(directory);
            foreach (var fileName in SortFileNames(changedFiles))
            {
                var shard = split.GetValueOrDefault(fileName) ?? new ObjectPriorityRuleManifest
                {
                    SchemaVersion = SchemaVersion,
                    Description = effectiveManifest.Description,
                    Rules = [],
                };
                WriteManifestAtomic(Path.Combine(directory, fileName), shard);
            }

            if (isDefault)
            {
                defaultIndex!.Files = SortFileNames(defaultIndex.Files.Concat(changedFiles)).ToList();
                WriteIndexAtomic(IndexPath, defaultIndex);
            }

            status = $"Saved {changedFiles.Count} complete context shard(s) for {presetName}.";
            return true;
        }
        catch (Exception ex)
        {
            status = $"Failed to save object-rule shards for {presetName}: {ex.Message}";
            return false;
        }
    }

    public bool TryWriteFullPreset(string presetName, ObjectPriorityRuleManifest manifest, out string status)
    {
        if (!TrySplitManifest(manifest, out var split, out status))
            return false;

        try
        {
            var isDefault = string.Equals(presetName, ObjectPriorityRuleService.DefaultPresetName, StringComparison.OrdinalIgnoreCase);
            if (isDefault)
            {
                Directory.CreateDirectory(RootPath);
                foreach (var pair in split)
                    WriteManifestAtomic(Path.Combine(RootPath, pair.Key), pair.Value);

                var keep = split.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var existing in Directory.EnumerateFiles(RootPath, "*_rule_objects.json", SearchOption.TopDirectoryOnly))
                {
                    if (TryParseCanonicalFileName(Path.GetFileName(existing), out _) && !keep.Contains(Path.GetFileName(existing)))
                        File.Delete(existing);
                }

                WriteIndexAtomic(IndexPath, new ObjectPriorityRuleShardIndex { Files = SortFileNames(split.Keys).ToList() });
            }
            else
            {
                var defaultShards = TryLoadDefaultShards(out var loadedDefaults, out var defaultStatus)
                    ? loadedDefaults
                    : throw new InvalidDataException(defaultStatus);
                var presetPath = GetPresetDirectoryPath(presetName);
                Directory.CreateDirectory(presetPath);
                var overrides = split
                    .Where(pair => !defaultShards.TryGetValue(pair.Key, out var defaultShard)
                                   || !RuleListsEqual(pair.Value.Rules, defaultShard.Rules))
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
                foreach (var pair in defaultShards.Where(pair => !split.ContainsKey(pair.Key)))
                {
                    overrides[pair.Key] = new ObjectPriorityRuleManifest
                    {
                        SchemaVersion = SchemaVersion,
                        Description = manifest.Description,
                        Rules = [],
                    };
                }
                foreach (var pair in overrides)
                    WriteManifestAtomic(Path.Combine(presetPath, pair.Key), pair.Value);
                foreach (var existing in Directory.EnumerateFiles(presetPath, "*_rule_objects.json", SearchOption.TopDirectoryOnly))
                {
                    if (TryParseCanonicalFileName(Path.GetFileName(existing), out _) && !overrides.ContainsKey(Path.GetFileName(existing)))
                        File.Delete(existing);
                }
            }

            status = $"Saved complete preset {presetName} as territory shards.";
            return true;
        }
        catch (Exception ex)
        {
            status = $"Failed to save complete preset {presetName}: {ex.Message}";
            return false;
        }
    }

    public bool TryDeletePreset(string presetName, out string status)
    {
        status = "Preset was not deleted.";
        var path = GetPresetDirectoryPath(presetName);
        try
        {
            var root = Path.GetFullPath(RootPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var target = Path.GetFullPath(path);
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase) || string.Equals(target, RootPath, StringComparison.OrdinalIgnoreCase))
            {
                status = $"Refused to delete preset path outside {RootPath}.";
                return false;
            }
            if (!Directory.Exists(target))
            {
                status = $"Preset {presetName} did not exist on disk.";
                return false;
            }
            Directory.Delete(target, recursive: true);
            status = $"Deleted preset {presetName}.";
            return true;
        }
        catch (Exception ex)
        {
            status = $"Failed to delete preset {presetName}: {ex.Message}";
            return false;
        }
    }

    public bool TryDeleteOverride(string presetName, string fileName, out string status)
    {
        status = "Context override was not reverted.";
        if (!TryParseCanonicalFileName(fileName, out _))
        {
            status = $"Unknown context shard {fileName}.";
            return false;
        }
        if (string.Equals(presetName, ObjectPriorityRuleService.DefaultPresetName, StringComparison.OrdinalIgnoreCase))
        {
            status = "DEFAULT contexts cannot be reverted to DEFAULT.";
            return false;
        }
        try
        {
            var path = GetShardPath(presetName, fileName);
            if (!File.Exists(path))
            {
                status = $"Preset {presetName} already inherits {fileName} from DEFAULT.";
                return false;
            }
            File.Delete(path);
            status = $"Reverted {fileName} in {presetName} to inherited DEFAULT content.";
            return true;
        }
        catch (Exception ex)
        {
            status = $"Failed to revert {fileName}: {ex.Message}";
            return false;
        }
    }

    public bool HasOverride(string presetName, string fileName)
        => !string.Equals(presetName, ObjectPriorityRuleService.DefaultPresetName, StringComparison.OrdinalIgnoreCase)
           && TryParseCanonicalFileName(fileName, out _)
           && File.Exists(GetShardPath(presetName, fileName));

    public IReadOnlyList<string> GetPresetNames()
    {
        var names = new List<string> { ObjectPriorityRuleService.DefaultPresetName };
        if (!Directory.Exists(RootPath))
            return names;
        names.AddRange(Directory.EnumerateDirectories(RootPath, "*", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Where(name => !string.Equals(name, LegacyMaturePresetName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)!);
        return names;
    }

    public ObjectRulePresetFileState CapturePresetState(string presetName)
    {
        var paths = new List<string> { IndexPath };
        if (TryLoadIndex(IndexPath, out var index, out _))
            paths.AddRange(index.Files.Select(file => Path.Combine(RootPath, file)));
        if (!string.Equals(presetName, ObjectPriorityRuleService.DefaultPresetName, StringComparison.OrdinalIgnoreCase))
        {
            var presetPath = GetPresetDirectoryPath(presetName);
            if (Directory.Exists(presetPath))
                paths.AddRange(Directory.EnumerateFiles(presetPath, "*_rule_objects.json", SearchOption.TopDirectoryOnly));
            else
                paths.Add(presetPath);
        }
        return CapturePaths(paths);
    }

    public ObjectRulePresetFileState CaptureContextState(string presetName, IEnumerable<string> fileNames)
    {
        var paths = new List<string>();
        foreach (var fileName in fileNames)
        {
            paths.Add(Path.Combine(RootPath, fileName));
            if (!string.Equals(presetName, ObjectPriorityRuleService.DefaultPresetName, StringComparison.OrdinalIgnoreCase))
                paths.Add(GetShardPath(presetName, fileName));
        }
        return CapturePaths(paths);
    }

    public IReadOnlyList<string> GetContextFileNames(ObjectPriorityRuleManifest manifest)
        => TrySplitManifest(manifest, out var split, out _) ? SortFileNames(split.Keys) : [];

    public bool TryGetChangedContextFiles(
        ObjectPriorityRuleManifest baseline,
        ObjectPriorityRuleManifest draft,
        out IReadOnlyList<string> changedFiles,
        out string status)
    {
        changedFiles = [];
        if (!TrySplitManifest(baseline, out var before, out status)
            || !TrySplitManifest(draft, out var after, out status))
            return false;
        changedFiles = SortFileNames(before.Keys.Concat(after.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(file => !RuleListsEqual(
                before.GetValueOrDefault(file)?.Rules ?? [],
                after.GetValueOrDefault(file)?.Rules ?? [])))
            .ToList();
        status = $"Detected {changedFiles.Count} changed object-rule context(s).";
        return true;
    }

    public bool TryEnsureInitialLayout(
        ObjectPriorityRuleManifest fallbackManifest,
        IEnumerable<string>? legacyPresetNamesToRetry,
        out IReadOnlyList<string> failedLegacyPresets,
        out string status)
    {
        failedLegacyPresets = [];
        if (File.Exists(IndexPath))
        {
            if (!TryLoadDefaultShards(out _, out status))
                return false;
            return TryMigratePendingLegacyPresets(legacyPresetNamesToRetry ?? [], out failedLegacyPresets, out status);
        }

        ObjectPriorityRuleManifest legacyDefault;
        if (File.Exists(LegacyPath))
        {
            if (!TryLoadManifest(LegacyPath, out legacyDefault, out status)
                || !TrySplitManifest(legacyDefault, out _, out status))
                return false;
        }
        else
        {
            legacyDefault = CloneManifest(fallbackManifest);
            if (!TrySplitManifest(legacyDefault, out _, out status))
                return false;
        }

        var customPresets = new Dictionary<string, Dictionary<string, ObjectPriorityRuleManifest>>(StringComparer.OrdinalIgnoreCase);
        var failures = new List<string>();
        if (Directory.Exists(LegacyPresetDirectoryPath))
        {
            foreach (var path in Directory.EnumerateFiles(LegacyPresetDirectoryPath, "*.json", SearchOption.TopDirectoryOnly))
            {
                var legacyName = Path.GetFileNameWithoutExtension(path);
                if (string.Equals(legacyName, LegacyMaturePresetName, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!TryBuildLegacyPresetOverrides(path, legacyDefault, out var name, out var overrides, out _))
                {
                    failures.Add(legacyName);
                    continue;
                }
                customPresets[name] = overrides;
            }
        }

        var stagingPath = Path.Combine(configDirectory, $"{DirectoryName}.migrate.{Guid.NewGuid():N}");
        try
        {
            if (!TrySplitManifest(legacyDefault, out var defaultShards, out status))
                return false;
            Directory.CreateDirectory(stagingPath);
            foreach (var pair in defaultShards)
                WriteManifestAtomic(Path.Combine(stagingPath, pair.Key), pair.Value);
            foreach (var preset in customPresets)
            {
                var presetPath = Path.Combine(stagingPath, preset.Key);
                Directory.CreateDirectory(presetPath);
                foreach (var pair in preset.Value)
                    WriteManifestAtomic(Path.Combine(presetPath, pair.Key), pair.Value);
            }
            WriteIndexAtomic(Path.Combine(stagingPath, IndexFileName), new ObjectPriorityRuleShardIndex
            {
                Files = SortFileNames(defaultShards.Keys).ToList(),
            });

            if (Directory.Exists(RootPath))
                throw new IOException($"Cannot publish the migrated shard tree because {RootPath} already exists without a valid index.");
            Directory.Move(stagingPath, RootPath);
            if (!File.Exists(LegacyPath))
                File.SetLastWriteTimeUtc(IndexPath, DateTime.UtcNow - TimeSpan.FromDays(2));
            failedLegacyPresets = failures;
            status = failures.Count == 0
                ? $"Migrated DEFAULT and {customPresets.Count} legacy preset(s) to {RootPath}."
                : $"Migrated DEFAULT and {customPresets.Count} legacy preset(s); {failures.Count} invalid preset(s) remain unavailable: {string.Join(", ", failures)}.";
            return true;
        }
        catch (Exception ex)
        {
            status = $"Failed to publish the object-rule shard migration: {ex.Message}";
            return false;
        }
        finally
        {
            try
            {
                if (Directory.Exists(stagingPath))
                    Directory.Delete(stagingPath, recursive: true);
            }
            catch
            {
                // Best-effort cleanup of the private staging directory.
            }
        }
    }

    public bool TryLoadLegacyDefault(out ObjectPriorityRuleManifest manifest, out string status)
        => TryLoadManifest(LegacyPath, out manifest, out status);

    public bool TrySplitManifest(
        ObjectPriorityRuleManifest manifest,
        out Dictionary<string, ObjectPriorityRuleManifest> shards,
        out string status)
        => TrySplitManifest(manifest, dutyCatalog, out shards, out status);

    internal static bool TrySplitManifest(
        ObjectPriorityRuleManifest manifest,
        IReadOnlyList<DutyCatalogEntry> dutyCatalog,
        out Dictionary<string, ObjectPriorityRuleManifest> shards,
        out string status)
    {
        shards = new Dictionary<string, ObjectPriorityRuleManifest>(StringComparer.OrdinalIgnoreCase);
        if (manifest.SchemaVersion != SchemaVersion)
        {
            status = $"Unsupported object-rule manifest schema {manifest.SchemaVersion}; expected {SchemaVersion}.";
            return false;
        }
        manifest.Rules ??= [];
        foreach (var rule in manifest.Rules)
        {
            if (!TryResolveContextFileName(rule, dutyCatalog, out var fileName, out status))
                return false;
            if (!shards.TryGetValue(fileName, out var shard))
            {
                shard = new ObjectPriorityRuleManifest
                {
                    SchemaVersion = SchemaVersion,
                    Description = manifest.Description,
                    Rules = [],
                };
                shards.Add(fileName, shard);
            }
            shard.Rules.Add(CloneRule(rule));
        }
        status = $"Validated {manifest.Rules.Count} rule(s) across {shards.Count} context shard(s).";
        return true;
    }

    internal static bool TryResolveContextFileName(
        ObjectPriorityRule rule,
        IReadOnlyList<DutyCatalogEntry> dutyCatalog,
        out string fileName,
        out string status)
    {
        fileName = string.Empty;
        var hasDutyScope = rule.TerritoryTypeId != 0
                           || rule.ContentFinderConditionId != 0
                           || !string.IsNullOrWhiteSpace(rule.DutyEnglishName);
        if (!hasDutyScope)
        {
            fileName = GlobalFileName;
            status = string.Empty;
            return true;
        }

        var territories = new List<uint>();
        if (rule.TerritoryTypeId != 0)
            territories.Add(rule.TerritoryTypeId);
        if (rule.ContentFinderConditionId != 0)
        {
            var matches = dutyCatalog.Where(entry => entry.ContentFinderConditionId == rule.ContentFinderConditionId)
                .Select(entry => entry.TerritoryTypeId).Distinct().ToList();
            if (matches.Count != 1)
            {
                status = $"Rule '{rule.ObjectName}' has unresolved CFC {rule.ContentFinderConditionId}.";
                return false;
            }
            territories.Add(matches[0]);
        }
        if (!string.IsNullOrWhiteSpace(rule.DutyEnglishName))
        {
            var normalizedName = DutyRuleCoverageHelper.NormalizeDutyLookupName(rule.DutyEnglishName);
            var matches = dutyCatalog
                .Where(entry => string.Equals(DutyRuleCoverageHelper.NormalizeDutyLookupName(entry.EnglishName), normalizedName, StringComparison.OrdinalIgnoreCase))
                .Select(entry => entry.TerritoryTypeId).Distinct().ToList();
            if (matches.Count != 1)
            {
                status = $"Rule '{rule.ObjectName}' has unresolved or ambiguous duty name '{rule.DutyEnglishName}'.";
                return false;
            }
            territories.Add(matches[0]);
        }

        if (territories.Count == 0 || territories.Any(territory => territory == 0) || territories.Distinct().Count() != 1)
        {
            status = $"Rule '{rule.ObjectName}' has contradictory territory, CFC, or duty-name scope.";
            return false;
        }
        fileName = GetTerritoryFileName(territories[0]);
        status = string.Empty;
        return true;
    }

    internal static bool TryParseIndexJson(string json, out ObjectPriorityRuleShardIndex index, out string status)
    {
        index = new ObjectPriorityRuleShardIndex();
        try
        {
            index = JsonSerializer.Deserialize<ObjectPriorityRuleShardIndex>(json, JsonOptions)
                    ?? throw new InvalidDataException("Index JSON was empty.");
            index.Files ??= [];
            if (index.SchemaVersion != SchemaVersion)
                throw new InvalidDataException($"Unsupported territory index schema {index.SchemaVersion}; expected {SchemaVersion}.");
            if (index.Files.Count != index.Files.Distinct(StringComparer.OrdinalIgnoreCase).Count())
                throw new InvalidDataException("Territory index contains duplicate filenames.");
            foreach (var fileName in index.Files)
            {
                if (Path.IsPathRooted(fileName) || fileName.Contains('/') || fileName.Contains('\\') || fileName.Contains("..", StringComparison.Ordinal)
                    || !TryParseCanonicalFileName(fileName, out _))
                    throw new InvalidDataException($"Territory index contains unsafe or unknown filename '{fileName}'.");
            }
            var sorted = SortFileNames(index.Files);
            if (!index.Files.SequenceEqual(sorted, StringComparer.Ordinal))
                throw new InvalidDataException("Territory index filenames are not in canonical Global-first numeric order.");
            status = $"Validated territory index with {index.Files.Count} shard(s).";
            return true;
        }
        catch (Exception ex)
        {
            status = $"Invalid territory index: {ex.Message}";
            return false;
        }
    }

    internal static bool TryValidateShardJson(
        string json,
        string fileName,
        IReadOnlyList<DutyCatalogEntry> dutyCatalog,
        out ObjectPriorityRuleManifest manifest,
        out string status)
    {
        manifest = new ObjectPriorityRuleManifest();
        try
        {
            if (!TryParseCanonicalFileName(fileName, out _))
                throw new InvalidDataException($"Unknown shard filename {fileName}.");
            manifest = JsonSerializer.Deserialize<ObjectPriorityRuleManifest>(json, JsonOptions)
                       ?? throw new InvalidDataException("Manifest JSON was empty.");
            manifest.Rules ??= [];
            if (manifest.SchemaVersion != SchemaVersion)
                throw new InvalidDataException($"Unsupported manifest schema {manifest.SchemaVersion}; expected {SchemaVersion}.");
            foreach (var rule in manifest.Rules)
            {
                if (!TryResolveContextFileName(rule, dutyCatalog, out var resolvedFile, out var resolutionStatus))
                    throw new InvalidDataException(resolutionStatus);
                if (!string.Equals(resolvedFile, fileName, StringComparison.Ordinal))
                    throw new InvalidDataException($"Rule '{rule.ObjectName}' belongs to {resolvedFile}, not {fileName}.");
            }
            status = $"Validated {manifest.Rules.Count} rule(s) in {fileName}.";
            return true;
        }
        catch (Exception ex)
        {
            status = $"Invalid object-rule shard {fileName}: {ex.Message}";
            return false;
        }
    }

    internal static bool TryParseCanonicalFileName(string fileName, out uint? territoryTypeId)
    {
        territoryTypeId = null;
        if (string.Equals(fileName, GlobalFileName, StringComparison.Ordinal))
            return true;
        var match = TerritoryFileRegex().Match(fileName);
        if (!match.Success || !uint.TryParse(match.Groups[1].Value, out var territory) || territory == 0)
            return false;
        if (!string.Equals(fileName, GetTerritoryFileName(territory), StringComparison.Ordinal))
            return false;
        territoryTypeId = territory;
        return true;
    }

    internal static IReadOnlyList<string> SortFileNames(IEnumerable<string> fileNames)
        => fileNames
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(file => string.Equals(file, GlobalFileName, StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(file => TryParseCanonicalFileName(file, out var territory) ? territory ?? 0 : uint.MaxValue)
            .ToList();

    internal static bool RuleListsEqual(IReadOnlyList<ObjectPriorityRule> left, IReadOnlyList<ObjectPriorityRule> right)
        => JsonSerializer.Serialize(left, JsonOptions) == JsonSerializer.Serialize(right, JsonOptions);

    internal static ObjectPriorityRule CloneRule(ObjectPriorityRule rule)
        => JsonSerializer.Deserialize<ObjectPriorityRule>(JsonSerializer.Serialize(rule, JsonOptions), JsonOptions)!;

    private bool TryMigratePendingLegacyPresets(
        IEnumerable<string> legacyPresetNames,
        out IReadOnlyList<string> failures,
        out string status)
    {
        var failed = new List<string>();
        failures = failed;
        if (!TryLoadManifest(LegacyPath, out var legacyDefault, out status))
        {
            if (!legacyPresetNames.Any())
            {
                status = "The territory layout is valid and no legacy preset retries are pending.";
                return true;
            }
            return false;
        }

        foreach (var legacyName in legacyPresetNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.Equals(legacyName, LegacyMaturePresetName, StringComparison.OrdinalIgnoreCase))
                continue;
            var path = Path.Combine(LegacyPresetDirectoryPath, $"{legacyName}.json");
            if (!TryBuildLegacyPresetOverrides(path, legacyDefault, out var name, out var overrides, out _))
            {
                failed.Add(legacyName);
                continue;
            }
            string? stagingPath = null;
            try
            {
                var presetPath = GetPresetDirectoryPath(name);
                if (Directory.Exists(presetPath))
                    throw new IOException($"Preset destination already exists: {presetPath}.");
                stagingPath = Path.Combine(configDirectory, $"{DirectoryName}.preset-migrate.{Guid.NewGuid():N}");
                Directory.CreateDirectory(stagingPath);
                foreach (var pair in overrides)
                    WriteManifestAtomic(Path.Combine(stagingPath, pair.Key), pair.Value);
                Directory.Move(stagingPath, presetPath);
                stagingPath = null;
            }
            catch
            {
                failed.Add(legacyName);
            }
            finally
            {
                try
                {
                    if (stagingPath is not null && Directory.Exists(stagingPath))
                        Directory.Delete(stagingPath, recursive: true);
                }
                catch
                {
                    // Best-effort cleanup of the unpublished preset staging directory.
                }
            }
        }
        status = failed.Count == 0
            ? "The territory layout is valid and all pending legacy presets migrated."
            : $"The territory layout is valid; invalid legacy presets remain unavailable: {string.Join(", ", failed)}.";
        return true;
    }

    private bool TryBuildLegacyPresetOverrides(
        string path,
        ObjectPriorityRuleManifest legacyDefault,
        out string presetName,
        out Dictionary<string, ObjectPriorityRuleManifest> overrides,
        out string status)
    {
        presetName = SanitizePresetName(Path.GetFileNameWithoutExtension(path));
        overrides = new Dictionary<string, ObjectPriorityRuleManifest>(StringComparer.OrdinalIgnoreCase);
        status = $"Legacy preset does not exist at {path}.";
        if (!File.Exists(path) || !TryLoadManifest(path, out var manifest, out status)
            || !TrySplitManifest(legacyDefault, out var defaults, out status)
            || !TrySplitManifest(manifest, out var custom, out status))
            return false;
        foreach (var pair in custom)
        {
            if (!defaults.TryGetValue(pair.Key, out var defaultShard) || !RuleListsEqual(pair.Value.Rules, defaultShard.Rules))
                overrides.Add(pair.Key, pair.Value);
        }
        status = $"Prepared legacy preset {presetName} with {overrides.Count} override shard(s).";
        return true;
    }

    private bool TryLoadAndValidateShard(
        string path,
        string fileName,
        out ObjectPriorityRuleManifest manifest,
        out string status)
    {
        manifest = new ObjectPriorityRuleManifest();
        try
        {
            return TryValidateShardJson(File.ReadAllText(path), fileName, dutyCatalog, out manifest, out status);
        }
        catch (Exception ex)
        {
            status = $"Failed to load object-rule shard {path}: {ex.Message}";
            return false;
        }
    }

    private static bool TryLoadManifest(string path, out ObjectPriorityRuleManifest manifest, out string status)
    {
        manifest = new ObjectPriorityRuleManifest();
        try
        {
            var json = File.ReadAllText(path);
            manifest = JsonSerializer.Deserialize<ObjectPriorityRuleManifest>(json, JsonOptions)
                       ?? throw new InvalidDataException("Manifest JSON was empty.");
            manifest.Rules ??= [];
            if (manifest.SchemaVersion != SchemaVersion)
                throw new InvalidDataException($"Unsupported manifest schema {manifest.SchemaVersion}; expected {SchemaVersion}.");
            status = $"Loaded {manifest.Rules.Count} legacy rule(s) from {path}.";
            return true;
        }
        catch (Exception ex)
        {
            status = $"Failed to load manifest from {path}: {ex.Message}";
            return false;
        }
    }

    private static bool TryLoadIndex(string path, out ObjectPriorityRuleShardIndex index, out string status)
    {
        index = new ObjectPriorityRuleShardIndex();
        try
        {
            if (!File.Exists(path))
            {
                status = $"Territory index does not exist at {path}.";
                return false;
            }
            return TryParseIndexJson(File.ReadAllText(path), out index, out status);
        }
        catch (Exception ex)
        {
            status = $"Failed to load territory index {path}: {ex.Message}";
            return false;
        }
    }

    private static void WriteManifestAtomic(string path, ObjectPriorityRuleManifest manifest)
        => WriteJsonAtomic(path, JsonSerializer.Serialize(manifest, JsonOptions) + Environment.NewLine);

    internal static void WriteIndexAtomic(string path, ObjectPriorityRuleShardIndex index)
    {
        index.SchemaVersion = SchemaVersion;
        index.Files = SortFileNames(index.Files).ToList();
        WriteJsonAtomic(path, JsonSerializer.Serialize(index, JsonOptions) + Environment.NewLine);
    }

    internal static void WriteJsonAtomic(string path, string json)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tempPath, json);
            if (File.Exists(path))
                File.Replace(tempPath, path, null);
            else
                File.Move(tempPath, path);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    private static ObjectPriorityRuleManifest CloneManifest(ObjectPriorityRuleManifest manifest)
        => new()
        {
            SchemaVersion = manifest.SchemaVersion,
            Description = manifest.Description,
            Rules = manifest.Rules.Select(CloneRule).ToList(),
        };

    private static ObjectRulePresetFileState CapturePaths(IEnumerable<string> paths)
    {
        var files = new Dictionary<string, ObjectRuleShardFileState>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var exists = File.Exists(path) || Directory.Exists(path);
            files[Path.GetFullPath(path)] = new ObjectRuleShardFileState(
                exists,
                exists ? File.GetLastWriteTimeUtc(path) : null,
                File.Exists(path) ? new FileInfo(path).Length : null);
        }
        return new ObjectRulePresetFileState(files);
    }

    [GeneratedRegex("^([1-9][0-9]*)_rule_objects\\.json$", RegexOptions.CultureInvariant)]
    private static partial Regex TerritoryFileRegex();
}

internal sealed class ObjectRulePresetFileState(IReadOnlyDictionary<string, ObjectRuleShardFileState> files)
{
    public IReadOnlyDictionary<string, ObjectRuleShardFileState> Files { get; } = files;

    public bool SameAs(ObjectRulePresetFileState other)
        => Files.Count == other.Files.Count
           && Files.All(pair => other.Files.TryGetValue(pair.Key, out var state) && state == pair.Value);
}

internal readonly record struct ObjectRuleShardFileState(bool Exists, DateTime? LastWriteUtc, long? Length);
