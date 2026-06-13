using ADS.Models;

namespace ADS.Tests;

public sealed class TreasureDungeonRoleTests
{
    [Fact]
    public void FrenRiderFollowerBecomesRegularInsideNormalDuty()
    {
        var inferred = CreateFollowerInference();
        var context = CreateContext(inDuty: true, CreateDuty(DutyCategory.FourMan));

        var segmented = TreasureDungeonRoleInference.SegmentForDuty(
            inferred,
            context,
            supportedTreasureTerritory: false);

        Assert.Equal(TreasureDungeonRole.Regular, segmented.Role);
        Assert.Equal("Regular", segmented.DisplayName);
        Assert.False(segmented.AllowsOutsideBmraiFollow);
        Assert.True(segmented.FrenRiderEnabled);
    }

    [Fact]
    public void TreasureDutyKeepsFollowerInference()
    {
        var inferred = CreateFollowerInference();
        var context = CreateContext(inDuty: true, CreateDuty(DutyCategory.TreasureDungeon));

        var segmented = TreasureDungeonRoleInference.SegmentForDuty(
            inferred,
            context,
            supportedTreasureTerritory: false);

        Assert.Equal(inferred, segmented);
    }

    [Fact]
    public void SupportedTreasureTerritoryFallbackKeepsFollowerInference()
    {
        var inferred = CreateFollowerInference();
        var context = CreateContext(inDuty: true, CreateDuty(DutyCategory.Other));

        var segmented = TreasureDungeonRoleInference.SegmentForDuty(
            inferred,
            context,
            supportedTreasureTerritory: true);

        Assert.Equal(inferred, segmented);
    }

    [Fact]
    public void OutsideDutyKeepsFollowerInference()
    {
        var inferred = CreateFollowerInference();
        var context = CreateContext(inDuty: false, currentDuty: null);

        var segmented = TreasureDungeonRoleInference.SegmentForDuty(
            inferred,
            context,
            supportedTreasureTerritory: false);

        Assert.Equal(inferred, segmented);
    }

    [Fact]
    public void UnknownOrLoadingDutyWaitsBeforeRegularClassification()
    {
        var inferred = CreateFollowerInference();
        var unknown = CreateContext(inDuty: true, currentDuty: null);
        var loading = CreateContext(inDuty: true, CreateDuty(DutyCategory.FourMan), betweenAreas: true);

        Assert.Equal(
            inferred,
            TreasureDungeonRoleInference.SegmentForDuty(inferred, unknown, supportedTreasureTerritory: false));
        Assert.Equal(
            inferred,
            TreasureDungeonRoleInference.SegmentForDuty(inferred, loading, supportedTreasureTerritory: false));
    }

    private static TreasureDungeonRoleInference CreateFollowerInference()
        => new(
            TreasureDungeonRole.Follower,
            "FrenRider",
            "FrenRider enabled.",
            "Test Character@Test World",
            FrenRiderLoaded: true,
            FrenRiderEnabled: true,
            LootGoblinLoaded: false,
            LootGoblinEnabled: false,
            LootGoblinAdsSolverEnabled: false);

    private static DutyCatalogEntry CreateDuty(DutyCategory category)
        => new()
        {
            ContentFinderConditionId = 100,
            TerritoryTypeId = 200,
            Name = "Test Duty",
            EnglishName = "Test Duty",
            ContentTypeName = category.ToString(),
            ExpansionName = "Test",
            SupportNote = string.Empty,
            LevelRequired = 1,
            SortKey = 1,
            ExVersion = 1,
            ContentTypeRowId = 1,
            ContentMemberTypeRowId = 1,
            PartySize = 4,
            Category = category,
            SupportLevel = DutySupportLevel.PassiveOnly,
            ClearanceStatus = DutyClearanceStatus.NotCleared,
            IsPlannedTest = false,
        };

    private static DutyContextSnapshot CreateContext(
        bool inDuty,
        DutyCatalogEntry? currentDuty,
        bool betweenAreas = false)
        => new()
        {
            PluginEnabled = true,
            IsLoggedIn = true,
            BoundByDuty = inDuty,
            BoundByDuty56 = false,
            BetweenAreas = betweenAreas,
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
            TerritoryTypeId = currentDuty?.TerritoryTypeId ?? 0,
            MapId = 0,
            ContentFinderConditionId = currentDuty?.ContentFinderConditionId ?? 0,
            CurrentDuty = currentDuty,
        };
}
