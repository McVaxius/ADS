using ADS.Models;
using ADS.Services;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using System.Reflection;

namespace ADS.Tests;

public sealed class TreasureDungeonRoleTests
{
    [Theory]
    [InlineData(false, false, 712u, true, false, false)]
    [InlineData(true, false, 777u, true, false, false)]
    [InlineData(true, true, 777u, true, false, false)]
    [InlineData(true, false, 712u, false, false, false)]
    [InlineData(true, false, 712u, true, true, false)]
    [InlineData(true, false, 712u, true, false, true)]
    public void HigherLowerDiagnosticsOnlyProbeWhenEnabledInsideTreasureDuty(
        bool enabled, bool datamining, uint territory, bool inDuty, bool betweenAreas, bool shouldProbe)
    {
        using var directory = new TempDirectory();
        var gameGui = DispatchProxy.Create<IGameGui, DiagnosticProxy>();
        var clientState = DispatchProxy.Create<IClientState, DiagnosticProxy>();
        ((DiagnosticProxy)(object)clientState).Territory = territory;
        var configuration = new Configuration
        {
            HigherLowerDiagnosticsEnabled = enabled,
            HigherLowerVfxDataminingEnabled = datamining,
        };
        using var diagnostics = new TreasureHighLowDiagnosticService(gameGui,
            DispatchProxy.Create<IObjectTable, DiagnosticProxy>(), clientState,
            DispatchProxy.Create<IDataManager, DiagnosticProxy>(),
            DispatchProxy.Create<IPluginLog, DiagnosticProxy>(), configuration, directory.Path);
        var context = TestDutyContextFactory.Create(territoryId: territory, inDuty: inDuty, betweenAreas: betweenAreas);
        if (!enabled && territory == 712)
            Assert.True(diagnostics.TagKnownBoard("1", "2", "test"));

        diagnostics.ForceDump();
        diagnostics.Update(context, ObservationSnapshot.Empty, new PlannerSnapshot
        {
            Mode = default, ObjectiveKind = default, Objective = string.Empty,
            Explanation = string.Empty, CapturedAtUtc = DateTime.UtcNow,
        }, string.Empty);

        Assert.Equal(shouldProbe, ((DiagnosticProxy)(object)gameGui).Calls > 0);
        if (!enabled && territory == 712)
            Assert.NotNull(diagnostics.PeekKnownBoardForSolver());
        if (!enabled || territory == 777)
        {
            var calls = ((DiagnosticProxy)(object)gameGui).Calls;
            Assert.False(diagnostics.CaptureLiveProbe().Runtime.Active);
            Assert.Equal(calls, ((DiagnosticProxy)(object)gameGui).Calls);
            Assert.False(diagnostics.StartTrace(1).Success);
            Assert.False(Directory.Exists(diagnostics.DiagnosticDirectory));
        }
        Assert.False(new Configuration().HigherLowerDiagnosticsEnabled);
    }

    public class DiagnosticProxy : DispatchProxy
    {
        public uint Territory { get; set; }
        public int Calls { get; private set; }

        protected override object? Invoke(MethodInfo? method, object?[]? arguments)
        {
            Calls++;
            if (method?.Name == "get_TerritoryType")
                return Territory;
            if (method?.Name == "GetEnumerator")
                return Array.Empty<IGameObject>().AsEnumerable().GetEnumerator();
            var returnType = method?.ReturnType;
            return returnType is null || returnType == typeof(void) ? null
                : returnType == typeof(string) ? string.Empty
                : returnType.IsValueType ? Activator.CreateInstance(returnType) : null;
        }
    }

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
            IsMainScenario = false,
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
