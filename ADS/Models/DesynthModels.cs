using System.Text.Json.Serialization;

namespace ADS.Models;

[JsonConverter(typeof(JsonStringEnumConverter<DesynthSource>))]
public enum DesynthSource
{
    ActiveWhitelist,
    AllInventory,
    LastDutyGains,
}

[JsonConverter(typeof(JsonStringEnumConverter<DesynthRunMode>))]
public enum DesynthRunMode
{
    Configured,
    All,
    Whitelist,
    LastDuty,
    Skillups,
    InventoryOnly,
    EverywhereSkipGearsets,
    Everywhere,
}

[JsonConverter(typeof(JsonStringEnumConverter<DesynthInventoryScope>))]
public enum DesynthInventoryScope
{
    InventoryOnly,
    InventoryAndArmourySkipGearsets,
    InventoryAndArmoury,
}

public sealed record DesynthCandidate(
    uint BaseItemId,
    string Category,
    uint ItemLevel,
    float DesynthLevel,
    float MaximumDesynthLevel,
    bool InGearset);

public sealed record DesynthPolicy(
    DesynthRunMode Mode,
    DesynthSource Source,
    DesynthInventoryScope Scope,
    string PresetName,
    bool SkillUpFilterEnabled,
    int SkillUpThreshold,
    bool ProtectGearsets,
    IReadOnlySet<string> Categories,
    IReadOnlySet<uint> Whitelist,
    IReadOnlyDictionary<uint, int> RemainingDutyItems)
{
    public bool IsEligible(DesynthCandidate candidate)
    {
        if (candidate.BaseItemId == 0 || candidate.BaseItemId == 10146)
            return false;

        if (!Categories.Contains(candidate.Category))
            return false;

        if (ProtectGearsets && candidate.InGearset)
            return false;

        if (SkillUpFilterEnabled
            && !(candidate.DesynthLevel > 0
                 && candidate.DesynthLevel < candidate.ItemLevel + SkillUpThreshold
                 && candidate.DesynthLevel < candidate.MaximumDesynthLevel))
        {
            return false;
        }

        return Source switch
        {
            DesynthSource.ActiveWhitelist => Whitelist.Contains(candidate.BaseItemId),
            DesynthSource.LastDutyGains => RemainingDutyItems.TryGetValue(candidate.BaseItemId, out var remaining) && remaining > 0,
            _ => true,
        };
    }
}

public sealed class DesynthPresetManifest
{
    public int Version { get; set; } = 1;
    public List<DesynthPreset> Presets { get; set; } = [];
}

public sealed class DesynthPreset
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<uint> ItemIds { get; set; } = [];
}

public sealed class DesynthDutyLedgerManifest
{
    public int Version { get; set; } = 1;
    public DesynthDutyTracking? Active { get; set; }
    public DesynthDutyLedger? Ledger { get; set; }
}

public sealed class DesynthDutyTracking
{
    public uint TerritoryTypeId { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public Dictionary<uint, int> StartingCounts { get; set; } = [];
}

public sealed class DesynthDutyLedger
{
    public uint TerritoryTypeId { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime FinalizedAtUtc { get; set; }
    public bool DutyCompleted { get; set; }
    public bool Abandoned { get; set; }
    public Dictionary<uint, int> RemainingCounts { get; set; } = [];
}
