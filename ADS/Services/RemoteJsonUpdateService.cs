using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ADS.Models;
using Dalamud.Plugin.Services;

namespace ADS.Services;

public sealed class RemoteJsonUpdateService : IDisposable
{
    public const string ObjectRulesFileName = "duty-object-rules.json";
    public const string DialogRulesFileName = "dialog-yesno-rules.json";
    public const string DutyMaturityFileName = "duty-maturity.json";
    public const string TreasureRoutesFileName = TreasureDungeonData.FileName;
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(24);

    private const string RemoteBaseUrl = "https://raw.githubusercontent.com/McVaxius/botologyupdates/main/ads/";

    private static readonly RemoteJsonFile[] Files =
    [
        new(ObjectRulesFileName, RemoteJsonKind.ObjectRules),
        new(DialogRulesFileName, RemoteJsonKind.DialogRules),
        new(DutyMaturityFileName, RemoteJsonKind.DutyMaturity),
        new(TreasureRoutesFileName, RemoteJsonKind.TreasureRoutes),
    ];

    internal static IReadOnlyList<string> RemoteCacheFileNames
        => Files.Select(file => file.FileName).ToList();

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
    private readonly HttpClient httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
    };
    private readonly object updateGate = new();
    private Task? activeUpdateTask;
    private int completedUpdateSerial;
    private int consumedUpdateSerial;

    public RemoteJsonUpdateService(IPluginLog log, string configDirectory)
    {
        this.log = log;
        this.configDirectory = configDirectory;
        Directory.CreateDirectory(configDirectory);
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

    public string GetConfigPath(string fileName)
        => Path.Combine(configDirectory, fileName);

    public IReadOnlyList<string> GetCacheStatusLines()
        => Files
            .Select(file =>
            {
                var path = GetConfigPath(file.FileName);
                if (!File.Exists(path))
                    return $"{file.FileName}: missing";

                var writeUtc = File.GetLastWriteTimeUtc(path);
                var age = DateTime.UtcNow - writeUtc;
                var staleText = age > RefreshInterval ? "stale" : "fresh";
                return $"{file.FileName}: {FormatAge(age)} old, {staleText}, {writeUtc.ToLocalTime():yyyy-MM-dd HH:mm}";
            })
            .ToList();

    public bool TryStartMissingUpdate(string reason)
    {
        var missing = Files
            .Where(file => !File.Exists(GetConfigPath(file.FileName)))
            .Select(file => file.FileName)
            .ToList();
        if (missing.Count == 0)
        {
            LastUpdateStatus = "Remote config update skipped at startup: all cache files exist. Stale cache refresh waits for duty ownership or Update.";
            return false;
        }

        return TryStartUpdate(force: true, $"{reason}; missing {string.Join(", ", missing)}");
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

    public bool TryConsumeCompletedUpdate()
    {
        if (completedUpdateSerial == consumedUpdateSerial)
            return false;

        consumedUpdateSerial = completedUpdateSerial;
        return true;
    }

    public void Dispose()
        => httpClient.Dispose();

    private bool NeedsStaleRefresh(out string status)
    {
        var decision = GetRefreshDecision(DateTime.UtcNow);
        status = decision.Status;
        return decision.ShouldRefresh;
    }

    private RemoteJsonRefreshDecision GetRefreshDecision(DateTime utcNow)
        => DecideRefresh(
            Files.Select(file =>
            {
                var path = GetConfigPath(file.FileName);
                return File.Exists(path)
                    ? new RemoteJsonCacheFileState(file.FileName, true, File.GetLastWriteTimeUtc(path))
                    : new RemoteJsonCacheFileState(file.FileName, false, DateTime.MinValue);
            }),
            utcNow,
            RefreshInterval);

    internal static RemoteJsonRefreshDecision DecideRefresh(
        IEnumerable<RemoteJsonCacheFileState> fileStates,
        DateTime utcNow,
        TimeSpan refreshInterval)
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

            var age = utcNow - state.LastWriteUtc;
            if (age > refreshInterval)
            {
                stale.Add(state.FileName);
                staleDetails.Add($"{state.FileName} {FormatAge(age)} old");
            }
        }

        if (missing.Count == 0 && stale.Count == 0)
        {
            return new RemoteJsonRefreshDecision(
                false,
                "cache files are younger than 24h",
                missing,
                stale);
        }

        var statusParts = new List<string>();
        if (missing.Count > 0)
            statusParts.Add($"missing {string.Join(", ", missing)}");
        if (staleDetails.Count > 0)
            statusParts.Add($"stale cache {string.Join(", ", staleDetails)}");

        return new RemoteJsonRefreshDecision(
            true,
            string.Join("; ", statusParts),
            missing,
            stale);
    }

    private async Task RunUpdateAsync(string reason)
    {
        var tempPaths = new List<string>();
        var refreshedFiles = new List<RemoteJsonRefreshResult>();
        var cacheBustToken = Guid.NewGuid().ToString("N");
        try
        {
            LastUpdateStatus = $"Remote config update running: {reason}.";
            foreach (var file in Files)
            {
                var url = BuildRemoteUrl(file.FileName, cacheBustToken);
                log.Debug($"[ADS] Remote config fetching {file.FileName} from {url}.");

                var bytes = await httpClient.GetByteArrayAsync(url).ConfigureAwait(false);
                var json = DecodeUtf8Json(bytes);
                ValidateJson(file.Kind, json, url);

                var tempPath = Path.Combine(configDirectory, $"{file.FileName}.{Guid.NewGuid():N}.tmp");
                await File.WriteAllBytesAsync(tempPath, bytes).ConfigureAwait(false);
                tempPaths.Add(tempPath);
                refreshedFiles.Add(new RemoteJsonRefreshResult(file.FileName, bytes.Length));
            }

            for (var index = 0; index < Files.Length; index++)
            {
                var targetPath = GetConfigPath(Files[index].FileName);
                AtomicReplace(tempPaths[index], targetPath);
            }

            tempPaths.Clear();
            LastUpdateStatus = $"Remote config update complete: refreshed {FormatRefreshSummary(refreshedFiles)} from botologyupdates.";
            completedUpdateSerial++;
            log.Information($"[ADS] {LastUpdateStatus}");
        }
        catch (Exception ex)
        {
            LastUpdateStatus = $"Remote config update failed: {ex.Message}";
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
                    // Best effort cleanup; next update writes unique temp names.
                }
            }
        }
    }

    private static string BuildRemoteUrl(string fileName, string cacheBustToken)
        => $"{RemoteBaseUrl}{fileName}?adsCacheBust={Uri.EscapeDataString(cacheBustToken)}";

    private static string DecodeUtf8Json(byte[] bytes)
    {
        if (bytes is [0xEF, 0xBB, 0xBF, ..])
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);

        return Encoding.UTF8.GetString(bytes);
    }

    private static string FormatRefreshSummary(IReadOnlyList<RemoteJsonRefreshResult> refreshedFiles)
        => string.Join(", ", refreshedFiles.Select(file => $"{file.FileName} ({file.ByteCount} bytes)"));

    private static void ValidateJson(RemoteJsonKind kind, string json, string source)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidDataException($"{source} was empty.");

        switch (kind)
        {
            case RemoteJsonKind.ObjectRules:
                var objectRules = JsonSerializer.Deserialize<ObjectPriorityRuleManifest>(json, JsonOptions)
                    ?? throw new InvalidDataException($"{source} did not contain an object-rule manifest.");
                objectRules.Rules ??= [];
                break;
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
        var directory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        if (File.Exists(targetPath))
        {
            File.Replace(tempPath, targetPath, null);
            return;
        }

        File.Move(tempPath, targetPath);
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

    private sealed record RemoteJsonRefreshResult(string FileName, int ByteCount);

    private enum RemoteJsonKind
    {
        ObjectRules,
        DialogRules,
        DutyMaturity,
        TreasureRoutes,
    }
}

internal sealed record RemoteJsonCacheFileState(string FileName, bool Exists, DateTime LastWriteUtc);

internal sealed record RemoteJsonRefreshDecision(
    bool ShouldRefresh,
    string Status,
    IReadOnlyList<string> MissingFiles,
    IReadOnlyList<string> StaleFiles);
