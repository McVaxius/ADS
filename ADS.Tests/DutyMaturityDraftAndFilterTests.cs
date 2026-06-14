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

        var support = new DutyMaturityFilterState();
        support.SetAllSupportLevels(enabled: false);
        support.SupportLevels.Add(DutySupportLevel.PassiveOnly);
        Assert.False(Matches(row, support, current, explicitRules: 0));
        support.SupportLevels.Clear();
        support.SupportLevels.Add(DutySupportLevel.ActiveSupported);
        Assert.True(Matches(row, support, current, explicitRules: 0));

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

        Assert.True(Matches(row, new DutyMaturityFilterState { Search = "support note" }, current, explicitRules: 0));
        Assert.False(Matches(row, new DutyMaturityFilterState { Search = "does-not-exist" }, current, explicitRules: 0));

        row.SupportNote = "changed draft note";
        Assert.True(Matches(row, new DutyMaturityFilterState { ChangedOnly = true }, current, explicitRules: 0));
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
            ExVersion = 1,
            ContentTypeRowId = 1,
            ContentMemberTypeRowId = 4,
            PartySize = 4,
            Category = DutyCategory.FourMan,
            SupportLevel = DutySupportLevel.PassiveOnly,
            ClearanceStatus = DutyClearanceStatus.NotCleared,
            IsPlannedTest = false,
            IsMainScenario = false,
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
