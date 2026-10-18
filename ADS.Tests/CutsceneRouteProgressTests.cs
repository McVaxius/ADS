using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using ADS.Models;
using ADS.Services;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;

namespace ADS.Tests;

public sealed class CutsceneRouteProgressTests
{
    [Theory]
    [InlineData("cfc", 1)]
    [InlineData("duty", 1)]
    [InlineData("territory", 1)]
    [InlineData("all", 1)]
    [InlineData("cfc", 2)]
    [InlineData("duty", 2)]
    [InlineData("territory", 2)]
    [InlineData("all", 2)]
    [InlineData("cfc", 4)]
    [InlineData("duty", 4)]
    [InlineData("territory", 4)]
    [InlineData("all", 4)]
    [InlineData("cfc", 7)]
    [InlineData("duty", 7)]
    [InlineData("territory", 7)]
    [InlineData("all", 7)]
    public void CompletedTeleportDoesNotBecomeEligibleOrReplayDuringCutscene(string missing, int cutsceneFlags)
    {
        var preset = new ObjectPriorityRuleManifest
        {
            Rules = [Rule("TP 01", "XYZForceMarch", "0,0,0", 30), Rule("TP 02", "XYZ", "20,0,0", 120)],
        };
        using var fixture = new HyperFixatedTargetExecutionTests.Fixture(Vector3.Zero, includeObservationMemory: true,
            initialRules: new ObjectPriorityRuleManifest());
        var normal = TestDutyContextFactory.Create();
        var rules = Get<ObjectPriorityRuleService>(fixture.Execution, "objectPriorityRuleService");
        Assert.True(rules.SaveManifest("Cutscene regression", preset), rules.LastLoadStatus);
        var firstRule = rules.Current.Rules.Single(rule => rule.ObjectName == "TP 01");
        Assert.True(rules.TryGetRuleActivationIdentity(firstRule, out _, out var customOverride));
        Assert.True(customOverride);
        var first = Point(normal, firstRule);
        var second = Point(normal, rules.Current.Rules.Single(rule => rule.ObjectName == "TP 02"));
        Set(fixture.Frontier, "activeDutyKey", normal.ContentFinderConditionId);
        Target(fixture.Frontier, first);
        Update(fixture, normal, first);
        var visited = Get<HashSet<string>>(fixture.Frontier, "visitedFrontierKeys");
        Assert.Contains(first.Key, visited);
        Assert.Single(fixture.Commands, command => command == "/echo TP 01");

        Target(fixture.Frontier, second);
        Update(fixture, normal, second);
        var commandsBeforeHold = fixture.Commands.Where(command => command != "/vnav stop").ToArray();
        var interrupted = TestDutyContextFactory.Create(
            cutscene: (cutsceneFlags & 1) != 0, watchingCutscene: (cutsceneFlags & 2) != 0,
            watchingCutscene78: (cutsceneFlags & 4) != 0,
            inDuty: missing is not ("duty" or "all"),
            territoryId: missing is "territory" or "all" ? 0u : normal.TerritoryTypeId,
            cfcId: missing is "cfc" or "all" ? 0u : normal.ContentFinderConditionId);
        fixture.Frontier.Update(interrupted, ObservationSnapshot.Empty);
        Update(fixture, interrupted, second);

        Assert.Contains(first.Key, visited);
        Assert.Equal(OwnershipMode.OwnedStartInside, fixture.Execution.CurrentMode);
        Assert.Equal(commandsBeforeHold, fixture.Commands.Where(command => command != "/vnav stop"));
        Assert.Same(second, fixture.Frontier.CurrentTarget);
        Assert.Null(SelectForceMarch(fixture.Frontier, [Point(normal, firstRule), second]));
        Update(fixture, normal, second);
        Assert.Single(fixture.Commands, command => command == "/echo TP 01");
        Assert.Single(fixture.Commands, command => command == "/echo TP 02");
    }

    [Fact]
    public void MissingCfcDuringBetweenAreasPreservesCompletedStopsAndCompletesTheCurrentTransition()
    {
        using var fixture = new HyperFixatedTargetExecutionTests.Fixture(Vector3.Zero, includeObservationMemory: true);
        var normal = TestDutyContextFactory.Create();
        var first = Point(normal, Rule("TP 01", "XYZForceMarch", "0,0,0", 30));
        var second = Point(normal, Rule("TP 02", "XYZ", "20,0,0", 120));
        fixture.Frontier.RetireManualDestination(first, DungeonFrontierService.ForceMarchXyzArrivedReason, "synthetic arrival");
        Set(fixture.Frontier, "activeDutyKey", normal.ContentFinderConditionId);
        Target(fixture.Frontier, second);

        fixture.Frontier.HoldUnsafeTransition(TestDutyContextFactory.Create(betweenAreas: true, cfcId: 0));

        var visited = Get<HashSet<string>>(fixture.Frontier, "visitedFrontierKeys");
        Assert.Contains(first.Key, visited);
        Assert.Contains(second.Key, visited);
        Assert.Equal(normal.ContentFinderConditionId, Get<uint>(fixture.Frontier, "activeDutyKey"));
        Assert.Null(SelectForceMarch(fixture.Frontier, [first, second]));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(7)]
    public void CapturedIdentityAndDestinationKeysSurviveMissingNativeFieldsAndRestoration(int cutsceneFlags)
    {
        var capture = new ContextCapture();
        var original = capture.Service.Current;
        using var fixture = new HyperFixatedTargetExecutionTests.Fixture(Vector3.Zero, includeObservationMemory: true);
        var rule = Rule("TP 01", "XYZForceMarch", "0,0,0", 30);
        var first = Point(original, rule);
        fixture.Frontier.RetireManualDestination(first, DungeonFrontierService.ForceMarchXyzArrivedReason, "synthetic arrival");
        Set(fixture.Frontier, "activeDutyKey", original.ContentFinderConditionId);
        capture.Flags.Active.Clear();
        if ((cutsceneFlags & 1) != 0) capture.Flags.Active.Add(ConditionFlag.OccupiedInCutSceneEvent);
        if ((cutsceneFlags & 2) != 0) capture.Flags.Active.Add(ConditionFlag.WatchingCutscene);
        if ((cutsceneFlags & 4) != 0) capture.Flags.Active.Add(ConditionFlag.WatchingCutscene78);
        capture.Client.Territory = 0;
        for (var frame = 0; frame < 3; frame++)
        {
            capture.Service.Update(true, 0, 0, 0);
            var held = capture.Service.Current;
            Assert.Equal(original.TerritoryTypeId, held.TerritoryTypeId);
            Assert.Equal(original.ContentFinderConditionId, held.ContentFinderConditionId);
            Assert.True(held.IsCutscene);
            Assert.True(held.IsInteractionActionActive);
            Assert.True(held.IsTreasureRouteTransitHold);
            fixture.Frontier.Update(held, ObservationSnapshot.Empty);
            Assert.Contains(first.Key, Get<HashSet<string>>(fixture.Frontier, "visitedFrontierKeys"));
        }

        capture.Flags.Active.Clear();
        capture.Flags.Active.Add(ConditionFlag.BetweenAreas);
        capture.Service.Update(true, 0, 0, 0);
        fixture.Frontier.HoldUnsafeTransition(capture.Service.Current);
        Assert.Contains(first.Key, Get<HashSet<string>>(fixture.Frontier, "visitedFrontierKeys"));

        capture.Flags.Active.Clear();
        capture.Flags.Active.Add(ConditionFlag.BoundByDuty);
        capture.Client.Territory = original.TerritoryTypeId;
        // ClientState has settled before GameMain; the CFC is still unavailable.
        capture.Service.Update(true, 0, 0, 0);
        Assert.Equal(first.Key, Point(capture.Service.Current, rule).Key);
        Assert.False(capture.Service.Current.IsCutscene);
        Assert.Null(SelectForceMarch(fixture.Frontier, [Point(capture.Service.Current, rule)]));
        capture.Service.Update(true, original.TerritoryTypeId, 1, original.ContentFinderConditionId);
        Assert.Equal(first.Key, Point(capture.Service.Current, rule).Key);
    }

    [Theory]
    [InlineData("new-duty")]
    [InlineData("exit")]
    [InlineData("logout")]
    [InlineData("disabled")]
    public void AuthoritativeDutyChangesAndCancellationStillClearOldProgress(string change)
    {
        var capture = new ContextCapture();
        using var fixture = new HyperFixatedTargetExecutionTests.Fixture(Vector3.Zero, includeObservationMemory: true);
        var first = Point(capture.Service.Current, Rule("TP 01", "XYZForceMarch", "0,0,0", 30));
        fixture.Frontier.RetireManualDestination(first, DungeonFrontierService.ForceMarchXyzArrivedReason, "synthetic arrival");
        Set(fixture.Frontier, "activeDutyKey", 888u);
        capture.Flags.Active.Clear();
        if (change == "new-duty")
        {
            capture.Flags.Active.Add(ConditionFlag.BoundByDuty);
            capture.Flags.Active.Add(ConditionFlag.WatchingCutscene78);
            capture.Client.Territory = 778;
        }
        else if (change == "exit") capture.Client.Territory = 129;
        else if (change == "logout") capture.Client.LoggedIn = false;

        capture.Service.Update(change != "disabled", capture.Client.Territory, 1, change == "new-duty" ? 889u : 0u);
        var context = capture.Service.Current;
        Assert.Equal(change == "new-duty" ? 889u : 0u, context.ContentFinderConditionId);
        fixture.Frontier.Update(context, ObservationSnapshot.Empty);
        Assert.Empty(Get<HashSet<string>>(fixture.Frontier, "visitedFrontierKeys"));
        if (change == "new-duty")
        {
            var nextRule = Rule("TP 01", "XYZForceMarch", "0,0,0", 30);
            nextRule.TerritoryTypeId = context.TerritoryTypeId;
            var nextFirst = Point(context, nextRule);
            Assert.Same(nextFirst, SelectForceMarch(fixture.Frontier, [nextFirst]));
        }
        else
        {
            Assert.False(fixture.Execution.RefreshCutsceneHold(context, context.PluginEnabled));
            Update(fixture, context, first);
            Assert.False(fixture.Execution.IsOwned);
        }
    }

    [Fact]
    public void Watching78HoldsUnfinishedForceMarchBudgetAndStopCancelsIt()
    {
        using var fixture = new HyperFixatedTargetExecutionTests.Fixture(Vector3.Zero, includeObservationMemory: true);
        var now = DateTime.UtcNow;
        fixture.Execution.ReadUtcNow = () => now;
        var point = new DungeonFrontierPoint
        {
            Key = "unfinished", Name = "Unfinished TP", Position = new Vector3(40, 0, 0),
            LevelRowId = 0, ManualDestinationKind = ManualDestinationKind.Xyz, AllowCombatBypass = true,
        };
        Set(fixture.Execution, "committedForceMarchManualDestination", point);
        Set(fixture.Execution, "nextNavigationCommandUtc", now.AddSeconds(3));
        var cutscene = TestDutyContextFactory.Create(watchingCutscene78: true, inDuty: false);
        Update(fixture, cutscene, point);
        now = now.AddMinutes(2);
        Update(fixture, cutscene, point);
        Assert.Same(point, Get<DungeonFrontierPoint>(fixture.Execution, "committedForceMarchManualDestination"));
        Assert.Empty(fixture.Commands);
        Assert.False(fixture.Execution.RefreshCutsceneHold(TestDutyContextFactory.Create(), true));
        Assert.Equal(TimeSpan.FromSeconds(3), Get<DateTime>(fixture.Execution, "nextNavigationCommandUtc") - now);
        Assert.True(fixture.Execution.RefreshCutsceneHold(cutscene, true));
        fixture.Execution.Stop(cutscene);
        Update(fixture, TestDutyContextFactory.Create(), point);
        Assert.Null(Get<DungeonFrontierPoint?>(fixture.Execution, "committedForceMarchManualDestination"));
        Assert.False(fixture.Execution.IsOwned);
    }

    private static ObjectPriorityRule Rule(string name, string classification, string coordinates, int priority)
        => new()
        {
            TerritoryTypeId = 777, ObjectName = name,
            Classification = classification, WorldCoordinates = coordinates, Priority = priority,
            DebugCommand = $"/echo {name}",
        };

    private static DungeonFrontierPoint Point(DutyContextSnapshot context, ObjectPriorityRule rule)
    {
        var ruleKey = typeof(DungeonFrontierService).GetMethod("BuildXyzDestinationRuleKey", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [context, rule]);
        var position = rule.Priority == 30 ? Vector3.Zero : new Vector3(20, 0, 0);
        return new DungeonFrontierPoint
        {
            Key = $"{ruleKey}:1:{position.X:0},0,0", Name = rule.ObjectName, Position = position,
            LevelRowId = 0, MapId = 1, Priority = rule.Priority, SourceRule = rule,
            ManualDestinationKind = ManualDestinationKind.Xyz,
            AllowCombatBypass = rule.Classification == "XYZForceMarch", ArrivalRadius3d = 1,
        };
    }

    private static void Target(DungeonFrontierService frontier, DungeonFrontierPoint point)
    {
        typeof(DungeonFrontierService).GetProperty(nameof(frontier.CurrentTarget))!.SetValue(frontier, point);
        typeof(DungeonFrontierService).GetProperty(nameof(frontier.CurrentMode))!.SetValue(frontier, FrontierMode.XyzDestination);
    }

    private static void Update(HyperFixatedTargetExecutionTests.Fixture fixture, DutyContextSnapshot context, DungeonFrontierPoint point)
        => fixture.Execution.Update(context, new PlannerSnapshot
        {
            Mode = PlannerMode.Progression,
            ObjectiveKind = point.AllowCombatBypass ? PlannerObjectiveKind.XyzForceMarchDestination : PlannerObjectiveKind.XyzDestination,
            Objective = point.Name, Explanation = "Synthetic teleport route", TargetName = point.Name,
            ActiveRule = point.SourceRule, CapturedAtUtc = DateTime.UtcNow,
        }, ObservationSnapshot.Empty, context.PluginEnabled, false, string.Empty, sessionDebugEnabled: true);

    private sealed class ContextCapture
    {
        public ContextCapture()
        {
            var client = DispatchProxy.Create<IClientState, ClientProxy>();
            Client = (ClientProxy)(object)client;
            var conditions = DispatchProxy.Create<ICondition, ConditionsProxy>();
            Flags = (ConditionsProxy)(object)conditions;
            Flags.Active.Add(ConditionFlag.BoundByDuty);
            var first = TestDutyContextFactory.Create().CurrentDuty!;
            var second = TestDutyContextFactory.Create(territoryId: 778, cfcId: 889).CurrentDuty!;
            var catalog = (DutyCatalogService)RuntimeHelpers.GetUninitializedObject(typeof(DutyCatalogService));
            Set(catalog, "entriesByCfc", new Dictionary<uint, DutyCatalogEntry> { [888] = first, [889] = second });
            Set(catalog, "entriesByTerritory", new Dictionary<uint, DutyCatalogEntry> { [777] = first, [778] = second });
            Service = new DutyContextService(client, conditions, catalog,
                DispatchProxy.Create<IPartyList, HyperFixatedTargetExecutionTests.NoOpProxy>());
            Service.Update(true, 777, 1, 888);
        }

        public DutyContextService Service { get; }
        public ClientProxy Client { get; }
        public ConditionsProxy Flags { get; }
    }

    public class ClientProxy : DispatchProxy
    {
        public uint Territory = 777;
        public bool LoggedIn = true;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
        {
            "get_IsLoggedIn" => LoggedIn,
            "get_TerritoryType" => Convert.ChangeType(Territory, method.ReturnType),
            "get_MapId" => Convert.ChangeType(1u, method.ReturnType),
            _ => method?.ReturnType.IsValueType == true ? Activator.CreateInstance(method.ReturnType) : null,
        };
    }

    public class ConditionsProxy : DispatchProxy
    {
        public HashSet<ConditionFlag> Active { get; } = [];
        protected override object? Invoke(MethodInfo? method, object?[]? args)
            => method?.Name == "get_Item" && args is [ConditionFlag flag] ? Active.Contains(flag) : null;
    }

    private static DungeonFrontierPoint? SelectForceMarch(DungeonFrontierService frontier, IReadOnlyList<DungeonFrontierPoint> points)
        => (DungeonFrontierPoint?)typeof(DungeonFrontierService)
            .GetMethod("SelectCurrentForceMarchManualDestination", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(frontier, [points, null, null]);

    private static T Get<T>(object target, string name)
        => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    private static void Set<T>(object target, string name, T value)
        => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
}
