using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ADS.Models;
using Dalamud.Plugin.Services;

namespace ADS.Services;

public sealed class RemoteJsonUpdateService : IDisposable
{
    public const string TerritoriesIndexFileName = "territories/index.json";
    public const string DialogRulesFileName = "dialog-yesno-rules.json";
    public const string DutyMaturityFileName = "duty-maturity.json";
    public const string TreasureRoutesFileName = TreasureDungeonData.FileName;
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(24);

    private const string RemoteBaseUrl = "https://raw.githubusercontent.com/McVaxius/botologyupdates/main/ads/";

    private static readonly RemoteJsonFile[] FlatFiles =
    [
        new(DialogRulesFileName, RemoteJsonKind.DialogRules),
        new(DutyMaturityFileName, RemoteJsonKind.DutyMaturity),
        new(TreasureRoutesFileName, RemoteJsonKind.TreasureRoutes),
    ];

    internal static IReadOnlyList<string> RemoteCacheFileNames
        => [TerritoriesIndexFileName, .. FlatFiles.Select(file => file.FileName)];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters =
        {
            new JsonStringEnumConverter<DutyClearanceStatus>(),
            new JsonStringEnumConverter<DutySupportLevel>(),
        },
    };

    private readonly IPluginLog log;
    private readonly string configDirectory;
    private readonly IReadOnlyList<DutyCatalogEntry> dutyCatalog;
    private readonly HttpClient httpClient = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly object updateGate = new();
    private Task? activeUpdateTask;
    private int completedUpdateSerial;
    private int consumedUpdateSerial;
    private RemoteJsonUpdateCompletion completedUpdate = new(true, false, []);

    public RemoteJsonUpdateService(IPluginLog log, string configDirectory, IReadOnlyList<DutyCatalogEntry>? dutyCatalog = null)
    {
        this.log = log;
        this.configDirectory = Path.GetFullPath(configDirectory);
        this.dutyCatalog = dutyCatalog ?? [];
        Directory.CreateDirectory(this.configDirectory);
        LastUpdateStatus = "Remote config cache not checked yet.";
    }

    public string LastUpdateStatus { get; private set; }

    public bool IsUpdateRunning
    {
        get
        {
            lock (updateGate)
                return activeUpdateTask is { IsCompleted: false };
        }
    }

    public bool ShouldDeferLocalReloadPolling
    {
        get
        {
            lock (updateGate)
                return activeUpdateTask is { IsCompleted: false }
                       || completedUpdateSerial != consumedUpdateSerial;
        }
    }

    public string GetConfigPath(string relativePath)
        => Path.Combine(configDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));

    public IReadOnlyList<string> GetCacheStatusLines()
    {
        var lines = new List<string>();
        var indexPath = GetConfigPath(TerritoriesIndexFileName);
        if (!File.Exists(indexPath))
        {
            lines.Add($"{TerritoriesIndexFileName}: missing");
        }
        else
        {
            var shardCount = ObjectRuleShardStore.TryParseIndexJson(File.ReadAllText(indexPath), out var index, out _)
                ? index.Files.Count
                : 0;
            lines.Add(BuildCacheStatusLine(TerritoriesIndexFileName, indexPath, $", {shardCount} shard(s)"));
        }

        lines.AddRange(FlatFiles.Select(file =>
        {
            var path = GetConfigPath(file.FileName);
            return File.Exists(path) ? BuildCacheStatusLine(file.FileName, path) : $"{file.FileName}: missing";
        }));
        return lines;
    }

    public bool TryStartStartupRefresh(string reason)
    {
        if (!NeedsStaleRefresh(out var status))
        {
            LastUpdateStatus = $"Remote config update skipped at startup: {status}.";
            return false;
        }
        return TryStartUpdate(force: true, $"{reason}; {status}");
    }

    public bool TryStartStaleUpdate(string reason)
    {
        if (!NeedsStaleRefresh(out var status))
        {
            LastUpdateStatus = $"Remote config update skipped: {status}";
            return false;
        }
        return TryStartUpdate(force: true, $"{reason}; {status}");
    }

    public bool TryStartUpdate(bool force, string reason)
    {
        if (!force && !NeedsStaleRefresh(out var status))
        {
            LastUpdateStatus = $"Remote config update skipped: {status}";
            return false;
        }
        lock (updateGate)
        {
            if (activeUpdateTask is { IsCompleted: false })
            {
                LastUpdateStatus = $"Remote config update already running; ignored {reason}.";
                return false;
            }
            LastUpdateStatus = $"Remote config update queued: {reason}.";
            activeUpdateTask = Task.Run(() => RunUpdateAsync(reason));
            return true;
        }
    }

    public bool TryConsumeCompletedUpdate(out RemoteJsonUpdateCompletion completion)
    {
        lock (updateGate)
        {
            if (completedUpdateSerial == consumedUpdateSerial)
            {
                completion = new RemoteJsonUpdateCompletion(true, false, []);
                return false;
            }
            consumedUpdateSerial = completedUpdateSerial;
            completion = completedUpdate;
            return true;
        }
    }

    public void Dispose()
        => httpClient.Dispose();

    private bool NeedsStaleRefresh(out string status)
    {
        var objectRuleCacheState = InspectLocalObjectRuleCache(configDirectory, dutyCatalog);
        var decision = DecideRefresh(
            RemoteCacheFileNames.Select(fileName =>
            {
                var path = GetConfigPath(fileName);
                return File.Exists(path)
                    ? new RemoteJsonCacheFileState(fileName, true, File.GetLastWriteTimeUtc(path))
                    : new RemoteJsonCacheFileState(fileName, false, DateTime.MinValue);
            }),
            DateTime.UtcNow,
            RefreshInterval,
            objectRuleCacheState);
        status = decision.Status;
        return decision.ShouldRefresh;
    }

    internal static RemoteJsonRefreshDecision DecideRefresh(
        IEnumerable<RemoteJsonCacheFileState> fileStates,
        DateTime utcNow,
        TimeSpan refreshInterval,
        RemoteJsonObjectRuleCacheState? objectRuleCacheState = null)
    {
        var missing = new List<string>();
        var stale = new List<string>();
        var staleDetails = new List<string>();
        foreach (var state in fileStates)
        {
            if (!state.Exists)
            {
                missing.Add(state.FileName);
                continue;
            }
            if (!string.Equals(state.FileName, TerritoriesIndexFileName, StringComparison.Ordinal))
                continue;
            var age = utcNow - state.LastWriteUtc;
            if (age <= refreshInterval)
                continue;
            stale.Add(state.FileName);
            staleDetails.Add($"{state.FileName} {FormatAge(age)} old");
        }
        if (missing.Count == 0 && stale.Count == 0 && objectRuleCacheState is not { IsValid: false })
            return new RemoteJsonRefreshDecision(false, "territory index is younger than 24h and all shared files are present", missing, stale);
        var parts = new List<string>();
        if (missing.Count > 0)
            parts.Add($"missing {string.Join(", ", missing)}");
        if (staleDetails.Count > 0)
            parts.Add($"stale cache {string.Join(", ", staleDetails)}");
        if (objectRuleCacheState is { IsValid: false })
            parts.Add($"invalid object-rule cache: {objectRuleCacheState.Status}");
        return new RemoteJsonRefreshDecision(true, string.Join("; ", parts), missing, stale);
    }

    internal static RemoteJsonObjectRuleCacheState InspectLocalObjectRuleCache(
        string configDirectory,
        IReadOnlyList<DutyCatalogEntry> dutyCatalog)
    {
        var indexPath = Path.Combine(configDirectory, TerritoriesIndexFileName.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            if (!File.Exists(indexPath))
                return new RemoteJsonObjectRuleCacheState(false, $"{TerritoriesIndexFileName} is missing", [TerritoriesIndexFileName]);
            if (!ObjectRuleShardStore.TryParseIndexJson(File.ReadAllText(indexPath), out var index, out var indexStatus))
                return new RemoteJsonObjectRuleCacheState(false, indexStatus, [TerritoriesIndexFileName]);

            var problems = new List<string>();
            var territoriesPath = Path.GetDirectoryName(indexPath)!;
            foreach (var fileName in index.Files)
            {
                var relativePath = $"{ObjectRuleShardStore.DirectoryName}/{fileName}";
                var path = Path.Combine(territoriesPath, fileName);
                if (!File.Exists(path))
                {
                    problems.Add(relativePath);
                    continue;
                }
                if (!ObjectRuleShardStore.TryValidateShardJson(File.ReadAllText(path), fileName, dutyCatalog, out _, out _))
                    problems.Add(relativePath);
            }

            return problems.Count == 0
                ? new RemoteJsonObjectRuleCacheState(true, $"validated {index.Files.Count} indexed shard(s)", [])
                : new RemoteJsonObjectRuleCacheState(false, $"missing or invalid {string.Join(", ", problems)}", problems);
        }
        catch (Exception ex)
        {
            return new RemoteJsonObjectRuleCacheState(false, ex.Message, [TerritoriesIndexFileName]);
        }
    }

    internal static bool TryValidateObjectRulePackage(
        string indexJson,
        IReadOnlyDictionary<string, string> shardJson,
        IReadOnlyList<DutyCatalogEntry> dutyCatalog,
        out ObjectPriorityRuleShardIndex index,
        out string status)
    {
        index = new ObjectPriorityRuleShardIndex();
        if (!ObjectRuleShardStore.TryParseIndexJson(indexJson, out index, out status))
            return false;
        foreach (var fileName in index.Files)
        {
            if (!shardJson.TryGetValue(fileName, out var json))
            {
                status = $"Indexed remote object-rule shard {fileName} was not downloaded.";
                return false;
            }
            if (!ObjectRuleShardStore.TryValidateShardJson(json, fileName, dutyCatalog, out _, out status))
                return false;
        }
        var indexedFiles = index.Files;
        if (shardJson.Keys.Any(file => !indexedFiles.Contains(file, StringComparer.Ordinal)))
        {
            status = "Remote object-rule package included an unindexed shard.";
            return false;
        }
        status = $"Validated complete remote object-rule package with {index.Files.Count} shard(s).";
        return true;
    }

    private async Task RunUpdateAsync(string reason)
    {
        var tempPaths = new List<string>();
        var objectRuleApplyStarted = false;
        try
        {
            LastUpdateStatus = $"Remote config update running: {reason}.";
            var cacheBustToken = Guid.NewGuid().ToString("N");
            var indexBytes = await FetchAsync(TerritoriesIndexFileName, cacheBustToken).ConfigureAwait(false);
            var indexJson = DecodeUtf8Json(indexBytes);
            if (!ObjectRuleShardStore.TryParseIndexJson(indexJson, out var index, out var indexStatus))
                throw new InvalidDataException(indexStatus);

            var shardBytes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            var shardJson = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var fileName in index.Files)
            {
                var relativePath = $"{ObjectRuleShardStore.DirectoryName}/{fileName}";
                var bytes = await FetchAsync(relativePath, cacheBustToken).ConfigureAwait(false);
                shardBytes.Add(fileName, bytes);
                shardJson.Add(fileName, DecodeUtf8Json(bytes));
            }
            if (!TryValidateObjectRulePackage(indexJson, shardJson, dutyCatalog, out _, out var packageStatus))
                throw new InvalidDataException(packageStatus);

            var flatBytes = new Dictionary<RemoteJsonFile, byte[]>();
            foreach (var file in FlatFiles)
            {
                var bytes = await FetchAsync(file.FileName, cacheBustToken).ConfigureAwait(false);
                ValidateJson(file.Kind, DecodeUtf8Json(bytes), file.FileName);
                flatBytes.Add(file, bytes);
            }

            var pendingWrites = new List<(string RelativePath, string TargetPath, string TempPath)>();
            foreach (var pair in shardBytes)
                StageIfChanged($"{ObjectRuleShardStore.DirectoryName}/{pair.Key}", pair.Value, pendingWrites, tempPaths);
            foreach (var pair in flatBytes)
                StageIfChanged(pair.Key.FileName, pair.Value, pendingWrites, tempPaths);

            var indexTargetPath = GetConfigPath(TerritoriesIndexFileName);
            var indexChanged = !File.Exists(indexTargetPath)
                               || !(await File.ReadAllBytesAsync(indexTargetPath).ConfigureAwait(false)).AsSpan().SequenceEqual(indexBytes);
            var indexTempPath = $"{indexTargetPath}.{Guid.NewGuid():N}.tmp";
            Directory.CreateDirectory(Path.GetDirectoryName(indexTargetPath)!);
            await File.WriteAllBytesAsync(indexTempPath, indexBytes).ConfigureAwait(false);
            tempPaths.Add(indexTempPath);

            foreach (var write in pendingWrites)
            {
                if (write.RelativePath.StartsWith($"{ObjectRuleShardStore.DirectoryName}/", StringComparison.Ordinal))
                    objectRuleApplyStarted = true;
                AtomicReplace(write.TempPath, write.TargetPath);
            }

            var keep = index.Files.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var territoriesPath = Path.Combine(configDirectory, ObjectRuleShardStore.DirectoryName);
            var removedFiles = new List<string>();
            if (Directory.Exists(territoriesPath))
            {
                foreach (var existing in Directory.EnumerateFiles(territoriesPath, "*_rule_objects.json", SearchOption.TopDirectoryOnly))
                {
                    var fileName = Path.GetFileName(existing);
                    if (ObjectRuleShardStore.TryParseCanonicalFileName(fileName, out _) && !keep.Contains(fileName))
                    {
                        objectRuleApplyStarted = true;
                        File.Delete(existing);
                        removedFiles.Add($"{ObjectRuleShardStore.DirectoryName}/{fileName}");
                    }
                }
            }
            AtomicReplace(indexTempPath, indexTargetPath);
            tempPaths.Clear();

            var changedFiles = pendingWrites.Select(write => write.RelativePath).ToList();
            changedFiles.AddRange(removedFiles);
            if (indexChanged)
                changedFiles.Add(TerritoriesIndexFileName);
            var changedShardCount = changedFiles.Count(file => file.StartsWith($"{ObjectRuleShardStore.DirectoryName}/", StringComparison.Ordinal));
            var changedFlatCount = changedFiles.Count - changedShardCount;
            LastUpdateStatus = changedFiles.Count == 0
                ? $"Remote config update complete: validated {index.Files.Count} object-rule shard(s); downloaded data matched the local cache."
                : $"Remote config update complete: validated {index.Files.Count} object-rule shard(s), changed {changedShardCount} shard/index file(s) and {changedFlatCount} other config file(s).";
            PublishCompletion(new RemoteJsonUpdateCompletion(true, false, changedFiles));
            log.Information($"[ADS] {LastUpdateStatus}");
        }
        catch (Exception ex)
        {
            LastUpdateStatus = $"Remote config update failed; the last valid in-memory rules remain active: {ex.Message}";
            PublishCompletion(new RemoteJsonUpdateCompletion(false, objectRuleApplyStarted, []));
            log.Warning(ex, $"[ADS] {LastUpdateStatus}");
        }
        finally
        {
            foreach (var tempPath in tempPaths)
            {
                try
                {
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                }
                catch
                {
                    // Best-effort cleanup; each update uses unique temp names.
                }
            }
        }
    }

    private void PublishCompletion(RemoteJsonUpdateCompletion completion)
    {
        lock (updateGate)
        {
            completedUpdate = completion;
            completedUpdateSerial++;
        }
    }

    private async Task<byte[]> FetchAsync(string relativePath, string cacheBustToken)
    {
        var url = $"{RemoteBaseUrl}{relativePath}?adsCacheBust={Uri.EscapeDataString(cacheBustToken)}";
        log.Debug($"[ADS] Remote config fetching {relativePath} from {url}.");
        return await httpClient.GetByteArrayAsync(url).ConfigureAwait(false);
    }

    private void StageIfChanged(
        string relativePath,
        byte[] bytes,
        List<(string RelativePath, string TargetPath, string TempPath)> pendingWrites,
        List<string> tempPaths)
    {
        var targetPath = GetConfigPath(relativePath);
        if (File.Exists(targetPath) && File.ReadAllBytes(targetPath).AsSpan().SequenceEqual(bytes))
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        var tempPath = $"{targetPath}.{Guid.NewGuid():N}.tmp";
        File.WriteAllBytes(tempPath, bytes);
        tempPaths.Add(tempPath);
        pendingWrites.Add((relativePath, targetPath, tempPath));
    }

    private static string DecodeUtf8Json(byte[] bytes)
        => bytes is [0xEF, 0xBB, 0xBF, ..]
            ? Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3)
            : Encoding.UTF8.GetString(bytes);

    private static void ValidateJson(RemoteJsonKind kind, string json, string source)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidDataException($"{source} was empty.");
        switch (kind)
        {
            case RemoteJsonKind.DialogRules:
                var dialogRules = JsonSerializer.Deserialize<DialogYesNoRuleManifest>(json, JsonOptions)
                                  ?? throw new InvalidDataException($"{source} did not contain a dialog-rule manifest.");
                dialogRules.Rules ??= [];
                break;
            case RemoteJsonKind.DutyMaturity:
                var maturity = JsonSerializer.Deserialize<DutyMaturityManifest>(json, JsonOptions)
                               ?? throw new InvalidDataException($"{source} did not contain a duty-maturity manifest.");
                maturity.Duties ??= [];
                break;
            case RemoteJsonKind.TreasureRoutes:
                TreasureDungeonData.ValidateJson(json, source);
                break;
            default:
                throw new InvalidDataException($"Unsupported remote JSON kind {kind}.");
        }
    }

    private static void AtomicReplace(string tempPath, string targetPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        if (File.Exists(targetPath))
            File.Replace(tempPath, targetPath, null);
        else
            File.Move(tempPath, targetPath);
    }

    private static string BuildCacheStatusLine(string label, string path, string suffix = "")
    {
        var writeUtc = File.GetLastWriteTimeUtc(path);
        var age = DateTime.UtcNow - writeUtc;
        return $"{label}: {FormatAge(age)} old, {(age > RefreshInterval ? "stale" : "fresh")}, {writeUtc.ToLocalTime():yyyy-MM-dd HH:mm}{suffix}";
    }

    private static string FormatAge(TimeSpan age)
    {
        if (age.TotalDays >= 1)
            return $"{age.TotalDays:0.0}d";
        if (age.TotalHours >= 1)
            return $"{age.TotalHours:0.0}h";
        if (age.TotalMinutes >= 1)
            return $"{age.TotalMinutes:0.0}m";
        return $"{Math.Max(0, age.TotalSeconds):0}s";
    }

    private sealed record RemoteJsonFile(string FileName, RemoteJsonKind Kind);

    private enum RemoteJsonKind
    {
        DialogRules,
        DutyMaturity,
        TreasureRoutes,
    }
}

internal sealed record RemoteJsonCacheFileState(string FileName, bool Exists, DateTime LastWriteUtc);

internal sealed record RemoteJsonObjectRuleCacheState(
    bool IsValid,
    string Status,
    IReadOnlyList<string> ProblemFiles);

internal sealed record RemoteJsonRefreshDecision(
    bool ShouldRefresh,
    string Status,
    IReadOnlyList<string> MissingFiles,
    IReadOnlyList<string> StaleFiles);

public sealed record RemoteJsonUpdateCompletion(
    bool Success,
    bool ObjectRuleDiskStateMayBePartial,
    IReadOnlyList<string> ChangedFiles);
