using System.Text.Json.Serialization;

namespace ADS.Models;

public sealed class ObjectPriorityRuleManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string Description { get; set; } = string.Empty;
    public List<ObjectPriorityRule> Rules { get; set; } = [];
}

public sealed class ObjectPriorityRuleShardIndex
{
    public int SchemaVersion { get; set; } = 1;
    public List<string> Files { get; set; } = [];
}

internal enum ObjectRuleContextBackingState
{
    DefaultFile,
    InheritedDefault,
    OverrideFile,
    EmptyOverride,
    CustomOnlyFile,
    NoFileYet,
}

internal sealed record ObjectRuleContextDescriptor(
    string FileName,
    uint? TerritoryTypeId,
    string Name,
    bool IsDefaultPreset,
    bool HasDefaultFile,
    bool HasCustomOverride,
    int EffectiveRowCount,
    bool IsEmptyOverride,
    bool IsCustomOnly,
    bool HasUnsavedChanges)
{
    public ObjectRuleContextBackingState BackingState
        => IsDefaultPreset
            ? HasDefaultFile
                ? ObjectRuleContextBackingState.DefaultFile
                : ObjectRuleContextBackingState.NoFileYet
            : HasCustomOverride
                ? IsEmptyOverride
                    ? ObjectRuleContextBackingState.EmptyOverride
                    : IsCustomOnly
                        ? ObjectRuleContextBackingState.CustomOnlyFile
                        : ObjectRuleContextBackingState.OverrideFile
                : HasDefaultFile
                    ? ObjectRuleContextBackingState.InheritedDefault
                    : ObjectRuleContextBackingState.NoFileYet;

    public string BackingStateLabel
        => BackingState switch
        {
            ObjectRuleContextBackingState.DefaultFile => "DEFAULT file",
            ObjectRuleContextBackingState.InheritedDefault => "Inherited DEFAULT",
            ObjectRuleContextBackingState.OverrideFile => "Override file",
            ObjectRuleContextBackingState.EmptyOverride => "Empty override",
            ObjectRuleContextBackingState.CustomOnlyFile => "Custom-only file",
            _ => "No file yet",
        };
}

public sealed class ObjectPriorityRule
{
    private string? debugCommand;

    public bool Enabled { get; set; } = true;
    public uint TerritoryTypeId { get; set; }
    public uint ContentFinderConditionId { get; set; }
    public string DutyEnglishName { get; set; } = string.Empty;
    public string? Alliance { get; set; }
    public string ObjectKind { get; set; } = string.Empty;
    public uint BaseId { get; set; }
    public string ObjectName { get; set; } = string.Empty;
    public string NameMatchMode { get; set; } = "Exact";
    public string Classification { get; set; } = string.Empty;
    public string DestinationType { get; set; } = string.Empty;
    public string Layer { get; set; } = string.Empty;
    public string MapCoordinates { get; set; } = string.Empty;
    public string WorldCoordinates { get; set; } = string.Empty;
    public string ObjectMapCoordinates { get; set; } = string.Empty;
    public string ObjectWorldCoordinates { get; set; } = string.Empty;
    public float? ObjectMatchRadius { get; set; }
    public int Priority { get; set; } = 1000;
    public float PriorityVerticalRadius { get; set; }
    public float? MaxDistance { get; set; }
    public float WaitAtDestinationSeconds { get; set; }
    public float WaitAfterInteractSeconds { get; set; }
    public string Notes { get; set; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DebugCommand
    {
        get => debugCommand;
        set => debugCommand = string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
