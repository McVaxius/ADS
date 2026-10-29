namespace ADS.Models;

public sealed class DutyBmrPolicyManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string Description { get; set; } = string.Empty;
    public List<DutyBmrPolicyRow> Duties { get; set; } = [];
}

public sealed class DutyBmrPolicyRow
{
    public uint ContentFinderConditionId { get; set; }
    public uint TerritoryTypeId { get; set; }
    public string DutyEnglishName { get; set; } = string.Empty;
    public bool DisableBmr { get; set; }
}
