using ADS.Models;
using ADS.Services;

namespace ADS.Tests;

public sealed class DutyMaturityDraftAndFilterTests
{
    [Fact]
    public void EditingDraftDoesNotMutateSourceEntryBeforeSave()
    {
        var source = CreateDuty();
        var draft = DutyMaturityDraftRow.FromEntry(source);

        draft.ClearanceStatus = DutyClearanceStatus.FourPlayerSyncCleared;
        draft.SupportLevel = DutySupportLevel.ActiveSupported;
        draft.IsPlannedTest = true;
        draft.IsMainScenario = true;
        draft.SupportNote = "draft-only note";

        Assert.Equal(DutyClearanceStatus.NotCleared, source.ClearanceStatus);
        Assert.Equal(DutySupportLevel.PassiveOnly, source.SupportLevel);
        Assert.False(source.IsPlannedTest);
        Assert.False(source.IsMainScenario);
        Assert.Equal(DutyCatalogService.DefaultSupportNote, source.SupportNote);
        Assert.True(draft.IsChanged);
    }

    [Fact]
    public void ResetDraftRowReturnsAllMaturityFieldsToDefaults()
    {
        var source = CreateDuty();
        source.ClearanceStatus = DutyClearanceStatus.FourPlayerSyncCleared;
        source.SupportLevel = DutySupportLevel.ActiveSupported;
        source.IsPlannedTest = true;
        source.IsMainScenario = true;
        source.SupportNote = "manual note";
        var draft = DutyMaturityDraftRow.FromEntry(source);

        draft.ResetToDefaults();

        Assert.Equal(DutyClearanceStatus.NotCleared, draft.ClearanceStatus);
        Assert.Equal(DutySupportLevel.PassiveOnly, draft.SupportLevel);
        Assert.False(draft.IsPlannedTest);
        Assert.False(draft.IsMainScenario);
        Assert.Equal(DutyCatalogService.DefaultSupportNote, draft.SupportNote);
        Assert.True(draft.IsChanged);
    }

    [Fact]
    public void FilterHelperMatchesBulkTriageFields()
    {
        var source = CreateDuty();
        source.ClearanceStatus = DutyClearanceStatus.OnePlayerUnsyncCleared;
        source.SupportLevel = DutySupportLevel.ActiveSupported;
        source.IsPlannedTest = true;
        source.IsMainScenario = true;
        source.SupportNote = "manual support note";
        var row = DutyMaturityDraftRow.FromEntry(source);
        var current = CreateContext(row);

        Assert.True(Matches(row, new DutyMaturityFilterState(), current, explicitRules: 0));

        var family = new DutyMaturityFilterState();
        family.SetAllFamilies(enabled: false);
        family.Families.Add(DutyCategory.EightMan);
        Assert.False(Matches(row, family, current, explicitRules: 0));
        family.Families.Clear();
        family.Families.Add(DutyCategory.FourMan);
        Assert.True(Matches(row, family, current, explicitRules: 0));

        var clearance = new DutyMaturityFilterState();
        clearance.SetAllClearanceStatuses(enabled: false);
        clearance.ClearanceStatuses.Add(DutyClearanceStatus.NotCleared);
        Assert.False(Matches(row, clearance, current, explicitRules: 0));
        clearance.ClearanceStatuses.Clear();
        clearance.ClearanceStatuses.Add(DutyClearanceStatus.OnePlayerUnsyncCleared);
        Assert.True(Matches(row, clearance, current, explicitRules: 0));

        var legacySupport = new DutyMaturityFilterState();
        legacySupport.SetAllSupportLevels(enabled: false);
        Assert.True(Matches(row, legacySupport, current, explicitRules: 0));

        Assert.True(Matches(row, new DutyMaturityFilterState { PlannedOnly = true }, current, explicitRules: 0));
        Assert.True(Matches(row, new DutyMaturityFilterState { MainScenarioOnly = true }, current, explicitRules: 0));
        Assert.True(Matches(row, new DutyMaturityFilterState { OverridesOnly = true }, current, explicitRules: 0));
        Assert.True(Matches(row, new DutyMaturityFilterState { HasNoteOnly = true }, current, explicitRules: 0));
        Assert.True(Matches(row, new DutyMaturityFilterState { CurrentDutyOnly = true }, current, explicitRules: 0));
        Assert.False(Matches(row, new DutyMaturityFilterState { CurrentDutyOnly = true }, CreateContext(row, territoryTypeId: 999), explicitRules: 0));

        Assert.True(Matches(row, new DutyMaturityFilterState { RuleCoverage = DutyRuleCoverageFilter.NoExplicitRules }, current, explicitRules: 0));
        Assert.False(Matches(row, new DutyMaturityFilterState { RuleCoverage = DutyRuleCoverageFilter.NoExplicitRules }, current, explicitRules: 1));
        Assert.True(Matches(row, new DutyMaturityFilterState { RuleCoverage = DutyRuleCoverageFilter.HasRules }, current, explicitRules: 1));
        Assert.True(Matches(row, new DutyMaturityFilterState { RuleCoverage = DutyRuleCoverageFilter.DenseRules }, current, explicitRules: DutyMaturityCatalog.DenseRuleThreshold + 1));

        Assert.True(Matches(row, new DutyMaturityFilterState { ExpansionId = row.ExVersion }, current, explicitRules: 0));
        Assert.False(Matches(row, new DutyMaturityFilterState { ExpansionId = row.ExVersion + 1 }, current, explicitRules: 0));

        Assert.True(Matches(row, new DutyMaturityFilterState { Search = "support note" }, current, explicitRules: 0));
        Assert.False(Matches(row, new DutyMaturityFilterState { Search = "does-not-exist" }, current, explicitRules: 0));

        row.SupportNote = "changed draft note";
        Assert.True(Matches(row, new DutyMaturityFilterState { ChangedOnly = true }, current, explicitRules: 0));
    }

    [Fact]
    public void CoverageAggregationMatchesAllConfiguredDutyScopeFieldsInOnePass()
    {
        var first = CreateDuty();
        first = CloneDuty(first, 101, 202, "The Test Duty");
        var second = CloneDuty(first, 102, 203, "Other Duty");
        var rules = new List<ObjectPriorityRule>
        {
            new() { ContentFinderConditionId = 101 },
            new() { TerritoryTypeId = 202 },
            new() { DutyEnglishName = "Test Duty" },
            new() { ContentFinderConditionId = 101, TerritoryTypeId = 202, DutyEnglishName = "test duty" },
            new() { Enabled = false, ContentFinderConditionId = 101 },
            new() { ContentFinderConditionId = 101, TerritoryTypeId = 999 },
            new() { ContentFinderConditionId = 101, DutyEnglishName = "Other Duty" },
            new() { ContentFinderConditionId = 101, Alliance = "A" },
            new(),
        };

        var snapshot = DutyRuleCoverageHelper.BuildSnapshot([first, second], rules);
        var firstCoverage = snapshot.Get(first);

        Assert.Equal(8, firstCoverage.AssociatedRuleCount);
        Assert.Equal(7, firstCoverage.EnabledRuleCount);
        Assert.Equal(2, firstCoverage.RedundantScopeMismatchCount);
        Assert.Equal(0, snapshot.Get(second).AssociatedRuleCount);
        Assert.Equal(1, snapshot.GlobalRuleCount);
        Assert.Equal(0, snapshot.UnresolvedRuleCount);
    }

    [Theory]
    [InlineData(DutyClearanceStatus.NotCleared, "M0")]
    [InlineData(DutyClearanceStatus.OnePlayerUnsyncCleared, "M1")]
    [InlineData(DutyClearanceStatus.OnePlayerDutySupport, "M2")]
    [InlineData(DutyClearanceStatus.FourPlayerSyncCleared, "M3")]
    public void MaturityDisplayUsesNumericTiers(DutyClearanceStatus status, string expected)
        => Assert.Equal(expected, DutyMaturityDisplayCatalog.GetClearanceLabel(status));

    [Fact]
    public void CoverageCountsOnlyEnabledFiniteWaypoints()
    {
        var duty = CreateDuty();
        var snapshot = DutyRuleCoverageHelper.BuildSnapshot(
            [duty],
            [
                new ObjectPriorityRule { ContentFinderConditionId = duty.ContentFinderConditionId, Enabled = true, Classification = "XYZ", WorldCoordinates = "1,2,3" },
                new ObjectPriorityRule { ContentFinderConditionId = duty.ContentFinderConditionId, Enabled = false, Classification = "XYZ", WorldCoordinates = "1,2,3" },
                new ObjectPriorityRule { ContentFinderConditionId = duty.ContentFinderConditionId, Enabled = true, Classification = "MapXzDestination", MapCoordinates = "NaN,2" },
            ]);

        Assert.Equal(1, snapshot.Get(duty).EnabledValidWaypointCount);
    }

    [Fact]
    public void DutyManagerSpecificFiltersUseWaypointDawntrailAndSelectionTruth()
    {
        var duty = CreateDuty();
        var row = DutyMaturityDraftRow.FromEntry(duty);
        var context = CreateContext(row);
        var coverage = new DutyRuleCoverage(2, 1, 1, 0);

        Assert.True(DutyMaturityFilterHelper.Matches(
            row,
            new DutyMaturityFilterState
            {
                DawntrailOnly = true,
                SelectedOnly = true,
                WaypointCoverage = DutyWaypointCoverageFilter.HasWaypoints,
            },
            context,
            coverage,
            isSelected: true));
        Assert.False(DutyMaturityFilterHelper.Matches(
            row,
            new DutyMaturityFilterState { SelectedOnly = true },
            context,
            coverage,
            isSelected: false));
    }

    private static bool Matches(
        DutyMaturityDraftRow row,
        DutyMaturityFilterState filter,
        DutyContextSnapshot current,
        int explicitRules)
        => DutyMaturityFilterHelper.Matches(row, filter, current, explicitRules);

    private static DutyCatalogEntry CreateDuty()
        => new()
        {
            ContentFinderConditionId = 101,
            TerritoryTypeId = 202,
            Name = "Test Duty",
            EnglishName = "Test Duty",
            ContentTypeName = "Dungeon",
            ExpansionName = "Test",
            SupportNote = DutyCatalogService.DefaultSupportNote,
            LevelRequired = 1,
            SortKey = 1,
            ExVersion = 5,
            ContentTypeRowId = 1,
            ContentMemberTypeRowId = 4,
            PartySize = 4,
            Category = DutyCategory.FourMan,
            SupportLevel = DutySupportLevel.PassiveOnly,
            ClearanceStatus = DutyClearanceStatus.NotCleared,
            IsPlannedTest = false,
            IsMainScenario = false,
        };

    private static DutyCatalogEntry CloneDuty(
        DutyCatalogEntry source,
        uint contentFinderConditionId,
        uint territoryTypeId,
        string englishName)
        => new()
        {
            ContentFinderConditionId = contentFinderConditionId,
            TerritoryTypeId = territoryTypeId,
            Name = englishName,
            EnglishName = englishName,
            ContentTypeName = source.ContentTypeName,
            ExpansionName = source.ExpansionName,
            SupportNote = source.SupportNote,
            LevelRequired = source.LevelRequired,
            SortKey = source.SortKey,
            ExVersion = source.ExVersion,
            ContentTypeRowId = source.ContentTypeRowId,
            ContentMemberTypeRowId = source.ContentMemberTypeRowId,
            PartySize = source.PartySize,
            Category = source.Category,
            SupportLevel = source.SupportLevel,
            ClearanceStatus = source.ClearanceStatus,
            IsPlannedTest = source.IsPlannedTest,
            IsMainScenario = source.IsMainScenario,
        };

    private static DutyContextSnapshot CreateContext(IDutyMaturityCatalogRow row, uint? territoryTypeId = null)
        => new()
        {
            PluginEnabled = true,
            IsLoggedIn = true,
            BoundByDuty = true,
            BoundByDuty56 = false,
            BetweenAreas = false,
            BetweenAreas51 = false,
            Jumping = false,
            Jumping61 = false,
            Occupied33 = false,
            OccupiedInQuestEvent = false,
            OccupiedInEvent = false,
            OccupiedInCutSceneEvent = false,
            WatchingCutscene = false,
            InCombat = false,
            Mounted = false,
            TerritoryTypeId = territoryTypeId ?? row.TerritoryTypeId,
            MapId = 0,
            ContentFinderConditionId = territoryTypeId.HasValue ? 0 : row.ContentFinderConditionId,
            CurrentDuty = null,
        };
}
