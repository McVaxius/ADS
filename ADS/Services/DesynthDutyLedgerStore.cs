using System.Text;
using System.Text.Json;
using ADS.Models;
using Dalamud.Plugin.Services;

namespace ADS.Services;

public sealed class DesynthDutyLedgerStore
{
    public const string FileName = "desynth-duty-ledger.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly IPluginLog? log;
    private DesynthDutyLedgerManifest manifest = new();
    private bool initialized;
    private bool wasInDuty;

    public DesynthDutyLedgerStore(string configDirectory)
        : this(configDirectory, null)
    {
    }

    public DesynthDutyLedgerStore(string configDirectory, IPluginLog? log)
    {
        this.log = log;
        ConfigPath = Path.Combine(configDirectory, FileName);
        Reload();
    }

    public string ConfigPath { get; }
    public DesynthDutyTracking? Active => manifest.Active;
    public DesynthDutyLedger? Ledger => manifest.Ledger;
    public string LastStatus { get; private set; } = "No duty ledger.";

    public void Update(bool inDuty, uint territoryTypeId, bool shouldTrack, Func<Dictionary<uint, int>> snapshot)
    {
        if (!initialized)
        {
            initialized = true;
            wasInDuty = inDuty;
            if (!inDuty && manifest.Active != null)
                Finalize(snapshot());
            return;
        }

        if (!wasInDuty && inDuty && shouldTrack)
            Begin(territoryTypeId, snapshot());
        else if (wasInDuty && !inDuty && manifest.Active != null)
            Finalize(snapshot());

        wasInDuty = inDuty;
    }

    public void MarkDutyCompleted()
    {
        if (manifest.Active == null)
            return;

        manifest.Active.CompletedAtUtc = DateTime.UtcNow;
        Save();
        LastStatus = "Tracked duty completed; waiting for duty exit to finalize gains.";
    }

    public IReadOnlyDictionary<uint, int> GetRemainingCounts()
        => manifest.Ledger is { DutyCompleted: true, Abandoned: false }
            ? manifest.Ledger.RemainingCounts
            : new Dictionary<uint, int>();

    public bool Consume(uint itemId)
    {
        var normalized = DesynthPolicyService.NormalizeBaseItemId(itemId);
        if (manifest.Ledger == null
            || !manifest.Ledger.RemainingCounts.TryGetValue(normalized, out var remaining)
            || remaining <= 0)
        {
            return false;
        }

        if (remaining == 1)
            manifest.Ledger.RemainingCounts.Remove(normalized);
        else
            manifest.Ledger.RemainingCounts[normalized] = remaining - 1;
        Save();
        LastStatus = $"Consumed one last-duty item {normalized}; {manifest.Ledger.RemainingCounts.Values.Sum()} remaining.";
        return true;
    }

    public void Clear()
    {
        manifest = new DesynthDutyLedgerManifest();
        Save();
        LastStatus = "Duty ledger cleared.";
    }

    public void Reload()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            if (File.Exists(ConfigPath))
            {
                manifest = JsonSerializer.Deserialize<DesynthDutyLedgerManifest>(File.ReadAllText(ConfigPath), JsonOptions)
                    ?? new DesynthDutyLedgerManifest();
            }
            if (manifest.Version != 1)
                throw new InvalidDataException($"Unsupported duty-ledger schema version {manifest.Version}.");
            Normalize(manifest.Active?.StartingCounts);
            Normalize(manifest.Ledger?.RemainingCounts);
            LastStatus = BuildStatus();
        }
        catch (Exception ex)
        {
            manifest = new DesynthDutyLedgerManifest();
            LastStatus = $"Duty ledger load failed: {ex.Message}";
            log?.Warning(ex, $"[ADS][Desynth] {LastStatus}");
        }
    }

    private void Begin(uint territoryTypeId, Dictionary<uint, int> counts)
    {
        Normalize(counts);
        manifest.Active = new DesynthDutyTracking
        {
            TerritoryTypeId = territoryTypeId,
            StartedAtUtc = DateTime.UtcNow,
            StartingCounts = counts,
        };
        manifest.Ledger = null;
        Save();
        LastStatus = $"Tracking duty {territoryTypeId} gains.";
    }

    private void Finalize(Dictionary<uint, int> endingCounts)
    {
        var active = manifest.Active!;
        Normalize(endingCounts);
        var completed = active.CompletedAtUtc.HasValue;
        var gains = completed
            ? endingCounts
                .Where(x => x.Value - active.StartingCounts.GetValueOrDefault(x.Key) > 0)
                .ToDictionary(x => x.Key, x => x.Value - active.StartingCounts.GetValueOrDefault(x.Key))
            : [];
        manifest.Ledger = new DesynthDutyLedger
        {
            TerritoryTypeId = active.TerritoryTypeId,
            StartedAtUtc = active.StartedAtUtc,
            FinalizedAtUtc = DateTime.UtcNow,
            DutyCompleted = completed,
            Abandoned = !completed,
            RemainingCounts = gains,
        };
        manifest.Active = null;
        Save();
        LastStatus = BuildStatus();
    }

    private void Save()
        => File.WriteAllText(ConfigPath, JsonSerializer.Serialize(manifest, JsonOptions), new UTF8Encoding(false));

    private string BuildStatus()
    {
        if (manifest.Active != null)
            return manifest.Active.CompletedAtUtc.HasValue
                ? "Tracked duty completed; waiting for duty exit."
                : $"Tracking duty {manifest.Active.TerritoryTypeId} gains.";
        if (manifest.Ledger == null)
            return "No duty ledger.";
        if (manifest.Ledger.Abandoned)
            return $"Last tracked duty {manifest.Ledger.TerritoryTypeId} was abandoned.";
        return $"Last-duty ledger has {manifest.Ledger.RemainingCounts.Values.Sum()} item(s) remaining.";
    }

    private static void Normalize(Dictionary<uint, int>? counts)
    {
        if (counts == null)
            return;

        var normalized = counts
            .Where(x => x.Key > 0 && x.Value > 0)
            .GroupBy(x => DesynthPolicyService.NormalizeBaseItemId(x.Key))
            .ToDictionary(x => x.Key, x => x.Sum(y => y.Value));
        counts.Clear();
        foreach (var entry in normalized)
            counts[entry.Key] = entry.Value;
    }
}
