namespace ADS.Models;

public sealed class DutyMaturityManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string Description { get; set; } = string.Empty;
    public List<DutyMaturityRow> Duties { get; set; } = [];
}

public sealed class DutyMaturityRow
{
    public uint ContentFinderConditionId { get; set; }
    public uint TerritoryTypeId { get; set; }
    public string DutyEnglishName { get; set; } = string.Empty;
    public DutyClearanceStatus ClearanceStatus { get; set; } = DutyClearanceStatus.NotCleared;
    public DutySupportLevel SupportLevel { get; set; } = DutySupportLevel.PassiveOnly;
    public bool IsPlannedTest { get; set; }
    public string SupportNote { get; set; } = string.Empty;
}
