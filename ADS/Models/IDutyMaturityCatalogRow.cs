namespace ADS.Models;

public interface IDutyMaturityCatalogRow
{
    uint ContentFinderConditionId { get; }
    uint TerritoryTypeId { get; }
    string Name { get; }
    string EnglishName { get; }
    string ContentTypeName { get; }
    string ExpansionName { get; }
    string SupportNote { get; }
    byte LevelRequired { get; }
    ushort SortKey { get; }
    uint ExVersion { get; }
    uint ContentTypeRowId { get; }
    uint ContentMemberTypeRowId { get; }
    int PartySize { get; }
    DutyCategory Category { get; }
    DutySupportLevel SupportLevel { get; }
    DutyClearanceStatus ClearanceStatus { get; }
    bool IsPlannedTest { get; }
    bool IsMainScenario { get; }
    bool IsChanged { get; }
}
