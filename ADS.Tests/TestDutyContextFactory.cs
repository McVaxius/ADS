using ADS.Models;

namespace ADS.Tests;

internal static class TestDutyContextFactory
{
    public static DutyContextSnapshot Create(
        DutyCategory? category = DutyCategory.FourMan,
        bool pluginEnabled = true,
        bool loggedIn = true,
        bool inDuty = true,
        bool betweenAreas = false,
        bool occupied = false,
        bool cutscene = false,
        uint territoryId = 777,
        uint cfcId = 888)
        => new()
        {
            PluginEnabled = pluginEnabled,
            IsLoggedIn = loggedIn,
            BoundByDuty = inDuty,
            BoundByDuty56 = false,
            BetweenAreas = betweenAreas,
            BetweenAreas51 = false,
            Jumping = false,
            Jumping61 = false,
            Occupied33 = false,
            OccupiedInQuestEvent = occupied,
            OccupiedInEvent = false,
            OccupiedInCutSceneEvent = cutscene,
            WatchingCutscene = false,
            InCombat = false,
            Mounted = false,
            TerritoryTypeId = territoryId,
            MapId = 1,
            ContentFinderConditionId = cfcId,
            CurrentDuty = category is null ? null : CreateDuty(category.Value, territoryId, cfcId),
        };

    private static DutyCatalogEntry CreateDuty(DutyCategory category, uint territoryId, uint cfcId)
        => new()
        {
            ContentFinderConditionId = cfcId,
            TerritoryTypeId = territoryId,
            Name = "Test Duty",
            EnglishName = "Test Duty",
            ContentTypeName = "Test",
            ExpansionName = "Test",
            SupportNote = string.Empty,
            LevelRequired = 1,
            SortKey = 1,
            ExVersion = 1,
            ContentTypeRowId = 1,
            ContentMemberTypeRowId = 1,
            PartySize = category == DutyCategory.Solo ? 1 : 4,
            Category = category,
            SupportLevel = DutySupportLevel.Unsupported,
            ClearanceStatus = DutyClearanceStatus.NotCleared,
            IsPlannedTest = false,
            IsMainScenario = false,
        };
}
