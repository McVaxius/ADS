namespace ADS.Models;

public static class DutyMaturityCatalog
{
    public const int DenseRuleThreshold = 10;
    public const string DefaultSupportNote = "Untested/default maturity row. Runtime ownership can still run from live duty truth, but this duty needs scouting before promotion.";

    public static string BuildDutyCatalogKey(IDutyMaturityCatalogRow row)
        => row.ContentFinderConditionId != 0
            ? $"cfc:{row.ContentFinderConditionId}"
            : $"terr:{row.TerritoryTypeId}";

    public static bool DutyMatchesCurrentContext(IDutyMaturityCatalogRow row, DutyContextSnapshot context)
    {
        if (row.ContentFinderConditionId != 0 && row.ContentFinderConditionId == context.ContentFinderConditionId)
            return true;

        return row.ContentFinderConditionId == 0
               && row.TerritoryTypeId != 0
               && row.TerritoryTypeId == context.TerritoryTypeId;
    }

    public static bool IsDefaultMaturityEntry(IDutyMaturityCatalogRow row)
        => row.ClearanceStatus == DutyClearanceStatus.NotCleared
           && row.SupportLevel == DutySupportLevel.PassiveOnly
           && !row.IsPlannedTest
           && !row.IsMainScenario
           && IsDefaultSupportNote(row.SupportNote);

    public static bool HasCustomSupportNote(string supportNote)
        => !IsDefaultSupportNote(supportNote);

    public static bool IsDefaultSupportNote(string supportNote)
        => string.IsNullOrWhiteSpace(supportNote)
           || string.Equals(NormalizeText(supportNote), DefaultSupportNote, StringComparison.Ordinal);

    public static string NormalizeText(string value)
        => string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
