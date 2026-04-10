namespace ADS.Models;

public sealed class ObjectPriorityRuleManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string Description { get; set; } = string.Empty;
    public List<ObjectPriorityRule> Rules { get; set; } = [];
}

public sealed class ObjectPriorityRule
{
    public bool Enabled { get; set; } = true;
    public uint TerritoryTypeId { get; set; }
    public uint ContentFinderConditionId { get; set; }
    public string DutyEnglishName { get; set; } = string.Empty;
    public string ObjectKind { get; set; } = string.Empty;
    public uint BaseId { get; set; }
    public string ObjectName { get; set; } = string.Empty;
    public string NameMatchMode { get; set; } = "Exact";
    public string Classification { get; set; } = string.Empty;
    public int Priority { get; set; } = 1000;
    public float PriorityVerticalRadius { get; set; }
    public float? MaxDistance { get; set; }
    public float WaitAtDestinationSeconds { get; set; }
    public string Notes { get; set; } = string.Empty;
}
