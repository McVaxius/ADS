namespace ADS.Models;

public sealed class DutyCatalogEntry
{
    public required uint ContentFinderConditionId { get; init; }
    public required uint TerritoryTypeId { get; init; }
    public required string Name { get; init; }
    public required string EnglishName { get; init; }
    public required string ContentTypeName { get; init; }
    public required string ExpansionName { get; init; }
    public required string SupportNote { get; init; }
    public required byte LevelRequired { get; init; }
    public required ushort SortKey { get; init; }
    public required uint ExVersion { get; init; }
    public required uint ContentTypeRowId { get; init; }
    public required uint ContentMemberTypeRowId { get; init; }
    public required int PartySize { get; init; }
    public required DutySupportLevel SupportLevel { get; init; }
    public required DutyClearanceStatus ClearanceStatus { get; init; }
    public required bool IsPlannedTest { get; init; }

    public bool SupportsPassiveObservation
        => SupportLevel is DutySupportLevel.PassiveOnly or DutySupportLevel.ActiveSupported;

    public bool SupportsActiveExecution
        => SupportLevel == DutySupportLevel.ActiveSupported;
}
