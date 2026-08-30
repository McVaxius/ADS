using ADS.Services;

namespace ADS.Models;

public enum DutyRuleCoverageFilter
{
    All = 0,
    NoExplicitRules = 1,
    HasRules = 2,
    DenseRules = 3,
}

public enum DutyWaypointCoverageFilter
{
    All = 0,
    NoWaypoints = 1,
    HasWaypoints = 2,
}

public sealed class DutyMaturityFilterState
{
    public string Search { get; set; } = string.Empty;
    public HashSet<DutyCategory> Families { get; } = Enum.GetValues<DutyCategory>().ToHashSet();
    public HashSet<DutyClearanceStatus> ClearanceStatuses { get; } = Enum.GetValues<DutyClearanceStatus>().ToHashSet();
    // Retained for source/wire compatibility. Duty Manager no longer gives this legacy field UI meaning.
    public HashSet<DutySupportLevel> SupportLevels { get; } = Enum.GetValues<DutySupportLevel>().ToHashSet();
    public uint? ExpansionId { get; set; }
    public bool MainScenarioOnly { get; set; }
    public bool PlannedOnly { get; set; }
    public bool OverridesOnly { get; set; }
    public bool ChangedOnly { get; set; }
    public bool CurrentDutyOnly { get; set; }
    public bool HasNoteOnly { get; set; }
    public bool DawntrailOnly { get; set; }
    public bool SelectedOnly { get; set; }
    public DutyRuleCoverageFilter RuleCoverage { get; set; }
    public DutyWaypointCoverageFilter WaypointCoverage { get; set; }

    public void SetAllFamilies(bool enabled)
    {
        Families.Clear();
        if (!enabled)
            return;

        foreach (var value in Enum.GetValues<DutyCategory>())
            Families.Add(value);
    }

    public void SetAllClearanceStatuses(bool enabled)
    {
        ClearanceStatuses.Clear();
        if (!enabled)
            return;

        foreach (var value in Enum.GetValues<DutyClearanceStatus>())
            ClearanceStatuses.Add(value);
    }

    public void SetAllSupportLevels(bool enabled)
    {
        SupportLevels.Clear();
        if (!enabled)
            return;
        foreach (var value in Enum.GetValues<DutySupportLevel>())
            SupportLevels.Add(value);
    }

}

public static class DutyMaturityFilterHelper
{
    public static bool Matches(
        IDutyMaturityCatalogRow row,
        DutyMaturityFilterState filters,
        DutyContextSnapshot currentContext,
        int explicitRuleCount)
        => Matches(
            row,
            filters,
            currentContext,
            new DutyRuleCoverage(explicitRuleCount, explicitRuleCount, 0, 0),
            isSelected: false);

    internal static bool Matches(
        IDutyMaturityCatalogRow row,
        DutyMaturityFilterState filters,
        DutyContextSnapshot currentContext,
        DutyRuleCoverage coverage,
        bool isSelected)
    {
        if (!filters.Families.Contains(row.Category))
            return false;

        if (!filters.ClearanceStatuses.Contains(row.ClearanceStatus))
            return false;

        if (filters.ExpansionId.HasValue && filters.ExpansionId.Value != row.ExVersion)
            return false;

        if (filters.DawntrailOnly && row.ExVersion != 5)
            return false;

        if (filters.SelectedOnly && !isSelected)
            return false;

        if (filters.MainScenarioOnly && !row.IsMainScenario)
            return false;

        if (filters.PlannedOnly && !row.IsPlannedTest)
            return false;

        if (filters.OverridesOnly && DutyMaturityCatalog.IsDefaultMaturityEntry(row))
            return false;

        if (filters.ChangedOnly && !row.IsChanged)
            return false;

        if (filters.CurrentDutyOnly && !DutyMaturityCatalog.DutyMatchesCurrentContext(row, currentContext))
            return false;

        if (filters.HasNoteOnly && !DutyMaturityCatalog.HasCustomSupportNote(row.SupportNote))
            return false;

        if (!RuleCoverageMatches(filters.RuleCoverage, coverage.AssociatedRuleCount))
            return false;

        if (!WaypointCoverageMatches(filters.WaypointCoverage, coverage.EnabledValidWaypointCount))
            return false;

        var search = filters.Search.Trim();
        return search.Length == 0 || SearchMatches(row, search);
    }

    private static bool RuleCoverageMatches(DutyRuleCoverageFilter filter, int explicitRuleCount)
        => filter switch
        {
            DutyRuleCoverageFilter.NoExplicitRules => explicitRuleCount == 0,
            DutyRuleCoverageFilter.HasRules => explicitRuleCount > 0,
            DutyRuleCoverageFilter.DenseRules => explicitRuleCount > DutyMaturityCatalog.DenseRuleThreshold,
            _ => true,
        };

    private static bool WaypointCoverageMatches(DutyWaypointCoverageFilter filter, int waypointCount)
        => filter switch
        {
            DutyWaypointCoverageFilter.NoWaypoints => waypointCount == 0,
            DutyWaypointCoverageFilter.HasWaypoints => waypointCount > 0,
            _ => true,
        };

    private static bool SearchMatches(IDutyMaturityCatalogRow row, string search)
    {
        var family = DutyCategoryDisplayCatalog.Get(row.Category).FilterLabel;
        return row.EnglishName.Contains(search, StringComparison.OrdinalIgnoreCase)
               || row.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
               || family.Contains(search, StringComparison.OrdinalIgnoreCase)
               || row.ExpansionName.Contains(search, StringComparison.OrdinalIgnoreCase)
               || row.ContentTypeName.Contains(search, StringComparison.OrdinalIgnoreCase)
               || row.SupportNote.Contains(search, StringComparison.OrdinalIgnoreCase)
               || DutyMaturityDisplayCatalog.GetClearanceLabel(row.ClearanceStatus).Contains(search, StringComparison.OrdinalIgnoreCase)
               || row.ClearanceStatus.ToString().Contains(search, StringComparison.OrdinalIgnoreCase)
               || (row.IsMainScenario && "MSQ".Contains(search, StringComparison.OrdinalIgnoreCase))
               || row.TerritoryTypeId.ToString().Contains(search, StringComparison.OrdinalIgnoreCase)
               || row.ContentFinderConditionId.ToString().Contains(search, StringComparison.OrdinalIgnoreCase);
    }
}
