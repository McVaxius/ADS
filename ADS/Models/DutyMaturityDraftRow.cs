namespace ADS.Models;

public sealed class DutyMaturityDraftRow : IDutyMaturityCatalogRow
{
    public required uint ContentFinderConditionId { get; init; }
    public required uint TerritoryTypeId { get; init; }
    public required string Name { get; init; }
    public required string EnglishName { get; init; }
    public required string ContentTypeName { get; init; }
    public required string ExpansionName { get; init; }
    public required byte LevelRequired { get; init; }
    public required ushort SortKey { get; init; }
    public required uint ExVersion { get; init; }
    public required uint ContentTypeRowId { get; init; }
    public required uint ContentMemberTypeRowId { get; init; }
    public required int PartySize { get; init; }
    public required DutyCategory Category { get; init; }
    public DutySupportLevel OriginalSupportLevel { get; private set; }
    public DutyClearanceStatus OriginalClearanceStatus { get; private set; }
    public bool OriginalIsPlannedTest { get; private set; }
    public bool OriginalIsMainScenario { get; private set; }
    public string OriginalSupportNote { get; private set; } = string.Empty;

    public required DutySupportLevel SupportLevel { get; set; }
    public required DutyClearanceStatus ClearanceStatus { get; set; }
    public required bool IsPlannedTest { get; set; }
    public required bool IsMainScenario { get; set; }
    public required string SupportNote { get; set; }

    public bool IsChanged
        => SupportLevel != OriginalSupportLevel
           || ClearanceStatus != OriginalClearanceStatus
           || IsPlannedTest != OriginalIsPlannedTest
           || IsMainScenario != OriginalIsMainScenario
           || !string.Equals(SupportNote, OriginalSupportNote, StringComparison.Ordinal);

    public static DutyMaturityDraftRow FromEntry(DutyCatalogEntry entry)
        => new()
        {
            ContentFinderConditionId = entry.ContentFinderConditionId,
            TerritoryTypeId = entry.TerritoryTypeId,
            Name = entry.Name,
            EnglishName = entry.EnglishName,
            ContentTypeName = entry.ContentTypeName,
            ExpansionName = entry.ExpansionName,
            LevelRequired = entry.LevelRequired,
            SortKey = entry.SortKey,
            ExVersion = entry.ExVersion,
            ContentTypeRowId = entry.ContentTypeRowId,
            ContentMemberTypeRowId = entry.ContentMemberTypeRowId,
            PartySize = entry.PartySize,
            Category = entry.Category,
            SupportLevel = entry.SupportLevel,
            ClearanceStatus = entry.ClearanceStatus,
            IsPlannedTest = entry.IsPlannedTest,
            IsMainScenario = entry.IsMainScenario,
            SupportNote = entry.SupportNote,
            OriginalSupportLevel = entry.SupportLevel,
            OriginalClearanceStatus = entry.ClearanceStatus,
            OriginalIsPlannedTest = entry.IsPlannedTest,
            OriginalIsMainScenario = entry.IsMainScenario,
            OriginalSupportNote = entry.SupportNote,
        };

    public void ApplyTo(DutyCatalogEntry entry)
    {
        entry.ClearanceStatus = ClearanceStatus;
        entry.SupportLevel = SupportLevel;
        entry.IsPlannedTest = IsPlannedTest;
        entry.IsMainScenario = IsMainScenario;
        entry.SupportNote = string.IsNullOrWhiteSpace(SupportNote)
            ? DutyMaturityCatalog.DefaultSupportNote
            : DutyMaturityCatalog.NormalizeText(SupportNote);
    }

    public void ResetToDefaults()
    {
        ClearanceStatus = DutyClearanceStatus.NotCleared;
        SupportLevel = DutySupportLevel.PassiveOnly;
        IsPlannedTest = false;
        IsMainScenario = false;
        SupportNote = DutyMaturityCatalog.DefaultSupportNote;
    }

    public void AcceptChanges()
    {
        OriginalSupportLevel = SupportLevel;
        OriginalClearanceStatus = ClearanceStatus;
        OriginalIsPlannedTest = IsPlannedTest;
        OriginalIsMainScenario = IsMainScenario;
        OriginalSupportNote = SupportNote;
    }
}
