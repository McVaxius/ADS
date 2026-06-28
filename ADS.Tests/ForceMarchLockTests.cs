using System.Collections;
using System.Numerics;
using System.Reflection;
using ADS.Models;
using ADS.Services;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;

namespace ADS.Tests;

public sealed class ForceMarchLockTests
{
    [Fact]
    public void ForceMarchRetirementGateRejectsNonCompletionReasons()
    {
        using var fixture = new ServiceFixture(Vector3.Zero);
        var point = ForceMarchXyz("force:reject", new Vector3(10f, 0f, 0f));

        fixture.Frontier.RetireManualDestination(point, "SatisfiedByLiveProgression", "test rejection");

        Assert.Empty(fixture.VisitedKeys);
        Assert.Null(fixture.Frontier.LastGhostedManualDestination);
        Assert.Equal(string.Empty, fixture.Frontier.LastGhostedManualDestinationReason);
    }

    [Theory]
    [InlineData(DungeonFrontierService.ForceMarchMapXzArrivedReason)]
    [InlineData(DungeonFrontierService.ForceMarchXyzArrivedReason)]
    [InlineData(DungeonFrontierService.ForceMarchXyzXzFallbackArrivedReason)]
    [InlineData(DungeonFrontierService.ForceMarchTransitionReason)]
    public void ForceMarchRetirementGateAcceptsCompletionReasons(string reason)
    {
        using var fixture = new ServiceFixture(Vector3.Zero);
        var point = ForceMarchXyz($"force:{reason}", new Vector3(10f, 0f, 0f));

        fixture.Frontier.RetireManualDestination(point, reason, "test completion");

        Assert.Contains(point.Key, fixture.VisitedKeys);
        Assert.Equal(point.Key, fixture.Frontier.LastGhostedManualDestination?.Key);
        Assert.Equal(reason, fixture.Frontier.LastGhostedManualDestinationReason);
    }

    [Fact]
    public void GenericMarkVisitedDoesNotCompleteForceMarch()
    {
        using var fixture = new ServiceFixture(Vector3.Zero);
        var point = ForceMarchXyz("force:generic", new Vector3(0.5f, 0f, 0f));

        fixture.Frontier.MarkVisited(point, Vector3.Zero);

        Assert.Empty(fixture.VisitedKeys);
        Assert.Null(fixture.Frontier.LastGhostedManualDestination);
    }

    [Fact]
    public void PlannerForceMarchSnapshotPreemptsLiveObjectivesAndRecovery()
    {
        using var fixture = new ServiceFixture(Vector3.Zero);
        var forceMarch = ForceMarchXyz("force:planner", new Vector3(40f, 0f, 0f));
        fixture.SetFrontierTarget(forceMarch, FrontierMode.XyzDestination);

        fixture.Planner.Update(
            Context(),
            Observation(
                monsters: [Monster("Incidental Trash", new Vector3(8f, 0f, 0f))],
                followTargets: [Monster("Follow Anchor", new Vector3(6f, 0f, 0f))],
                interactables:
                [
                    Interactable("Required Lever", InteractableClass.Required, new Vector3(5f, 0f, 0f)),
                    Interactable("Combat Device", InteractableClass.CombatFriendly, new Vector3(4f, 0f, 0f)),
                    Interactable("Shortcut", InteractableClass.Expendable, new Vector3(3f, 0f, 0f)),
                    Interactable("Treasure Coffer", InteractableClass.TreasureCoffer, new Vector3(2f, 0f, 0f)),
                ],
                monsterGhosts: [Monster("Ghost Trash", new Vector3(1f, 0f, 0f))],
                interactableGhosts: [Interactable("Ghost Lever", InteractableClass.Required, new Vector3(1f, 0f, 1f))]),
            OwnershipMode.OwnedStartInside,
            considerTreasureCoffers: true);

        Assert.Equal(PlannerObjectiveKind.XyzForceMarchDestination, fixture.Planner.Current.ObjectiveKind);
        Assert.Equal(forceMarch.Name, fixture.Planner.Current.TargetName);
    }

    [Fact]
    public void PlannerForceMarchSnapshotPreemptsMountedCombatStart()
    {
        using var fixture = new ServiceFixture(Vector3.Zero);
        var forceMarch = ForceMarchXyz("force:mounted", new Vector3(40f, 0f, 0f));
        fixture.SetFrontierTarget(forceMarch, FrontierMode.XyzDestination);

        fixture.Planner.Update(
            Context(inCombat: true, mounted: true),
            Observation(monsters: [Monster("Mounted Trash", new Vector3(8f, 0f, 0f))]),
            OwnershipMode.OwnedStartInside,
            considerTreasureCoffers: false);

        Assert.Equal(PlannerObjectiveKind.XyzForceMarchDestination, fixture.Planner.Current.ObjectiveKind);
        Assert.Equal(forceMarch.Name, fixture.Planner.Current.TargetName);
    }

    [Fact]
    public void CommittedForceMarchIgnoresPlannerObjectiveChanges()
    {
        using var fixture = new ServiceFixture(Vector3.Zero);
        var point = ForceMarchXyz("force:committed", new Vector3(40f, 0f, 0f));
        fixture.CommitForceMarch(point);

        var advanced = fixture.InvokeTryAdvanceCommittedForceMarch(
            Context(),
            Planner(PlannerObjectiveKind.RequiredInteractable, "Required Lever"));

        Assert.True(advanced);
        Assert.Equal(point.Key, fixture.CommittedForceMarch?.Key);
        Assert.Empty(fixture.VisitedKeys);
        Assert.Contains(fixture.Commands, command => command.StartsWith("/vnav moveto", StringComparison.Ordinal));
    }

    [Fact]
    public void UpdateKeepsCommittedForceMarchAheadOfFollowThroughAndCombatHold()
    {
        using var fixture = new ServiceFixture(Vector3.Zero);
        var point = ForceMarchXyz("force:update-committed", new Vector3(40f, 0f, 0f));
        fixture.SetExecutionMode(OwnershipMode.OwnedStartInside);
        fixture.CommitForceMarch(point);
        fixture.SetPendingProgressionInteractable(Interactable("Expired Shortcut", InteractableClass.Expendable, new Vector3(1f, 0f, 0f)));

        fixture.Execution.Update(
            Context(inCombat: true),
            Planner(PlannerObjectiveKind.RequiredInteractable, "Required Lever"),
            Observation(interactables: [Interactable("Required Lever", InteractableClass.Required, new Vector3(1f, 0f, 0f))]),
            pluginEnabled: true,
            considerTreasureCoffers: false,
            dialogAutomationStatus: string.Empty);

        Assert.Equal(point.Key, fixture.CommittedForceMarch?.Key);
        Assert.Empty(fixture.VisitedKeys);
        Assert.Contains(fixture.Commands, command => command.StartsWith("/vnav moveto", StringComparison.Ordinal));
        Assert.NotEqual(ExecutionPhase.CombatHold, fixture.Execution.CurrentPhase);
    }

    [Fact]
    public void NoProgressDoesNotRetireOrClearCommittedForceMarch()
    {
        using var fixture = new ServiceFixture(Vector3.Zero);
        var point = ForceMarchXyz("force:no-progress", new Vector3(40f, 0f, 0f));
        fixture.CommitForceMarch(point);

        var retired = fixture.InvokeTryRetireManualDestinationForNoProgress(point, Vector3.Zero);

        Assert.False(retired);
        Assert.Equal(point.Key, fixture.CommittedForceMarch?.Key);
        Assert.Empty(fixture.VisitedKeys);
    }

    [Fact]
    public void CommittedMapXzForceMarchArrivalCompletesThroughGate()
    {
        using var fixture = new ServiceFixture(new Vector3(0.5f, 100f, 0.5f));
        var point = ForceMarchMapXz("force:mapxz", new Vector3(0f, 0f, 0f));
        fixture.CommitForceMarch(point);

        var advanced = fixture.InvokeTryAdvanceCommittedForceMarch(
            Context(),
            Planner(PlannerObjectiveKind.MapXzForceMarchDestination, point.Name));

        Assert.True(advanced);
        Assert.Null(fixture.CommittedForceMarch);
        Assert.Contains(point.Key, fixture.VisitedKeys);
        Assert.Equal(DungeonFrontierService.ForceMarchMapXzArrivedReason, fixture.Frontier.LastGhostedManualDestinationReason);
    }

    [Fact]
    public void CommittedXyzForceMarchArrivalCompletesThroughGate()
    {
        using var fixture = new ServiceFixture(new Vector3(0.5f, 0.5f, 0.5f));
        var point = ForceMarchXyz("force:xyz", Vector3.Zero);
        fixture.CommitForceMarch(point);

        var advanced = fixture.InvokeTryAdvanceCommittedForceMarch(
            Context(),
            Planner(PlannerObjectiveKind.XyzForceMarchDestination, point.Name));

        Assert.True(advanced);
        Assert.Null(fixture.CommittedForceMarch);
        Assert.Contains(point.Key, fixture.VisitedKeys);
        Assert.Equal(DungeonFrontierService.ForceMarchXyzArrivedReason, fixture.Frontier.LastGhostedManualDestinationReason);
    }

    [Fact]
    public void CommittedXyzForceMarchUsesXzFallbackWhenYIsUnreliable()
    {
        using var fixture = new ServiceFixture(new Vector3(0.5f, 100f, 0.5f));
        var point = ForceMarchXyz("force:xyz-fallback", Vector3.Zero, usePlayerYForNavigation: true);
        fixture.CommitForceMarch(point);

        var advanced = fixture.InvokeTryAdvanceCommittedForceMarch(
            Context(),
            Planner(PlannerObjectiveKind.XyzForceMarchDestination, point.Name));

        Assert.True(advanced);
        Assert.Null(fixture.CommittedForceMarch);
        Assert.Contains(point.Key, fixture.VisitedKeys);
        Assert.Equal(DungeonFrontierService.ForceMarchXyzXzFallbackArrivedReason, fixture.Frontier.LastGhostedManualDestinationReason);
    }

    [Fact]
    public void UnsafeTransitionCompletesCommittedForceMarch()
    {
        using var fixture = new ServiceFixture(Vector3.Zero);
        var point = ForceMarchXyz("force:transition", new Vector3(40f, 0f, 0f));
        fixture.CommitForceMarch(point);

        fixture.Execution.Update(
            Context(betweenAreas: true),
            Planner(PlannerObjectiveKind.None, "Hold"),
            ObservationSnapshot.Empty,
            pluginEnabled: true,
            considerTreasureCoffers: false,
            dialogAutomationStatus: string.Empty);

        Assert.Null(fixture.CommittedForceMarch);
        Assert.Contains(point.Key, fixture.VisitedKeys);
        Assert.Equal(DungeonFrontierService.ForceMarchTransitionReason, fixture.Frontier.LastGhostedManualDestinationReason);
    }

    [Fact]
    public void OwnershipResetClearsCommittedForceMarchWithoutMarkingVisited()
    {
        using var fixture = new ServiceFixture(Vector3.Zero);
        var point = ForceMarchXyz("force:ownership-reset", new Vector3(40f, 0f, 0f));
        fixture.CommitForceMarch(point);

        fixture.Execution.Update(
            Context(pluginEnabled: false),
            Planner(PlannerObjectiveKind.None, "Disabled"),
            ObservationSnapshot.Empty,
            pluginEnabled: false,
            considerTreasureCoffers: false,
            dialogAutomationStatus: string.Empty);

        Assert.Null(fixture.CommittedForceMarch);
        Assert.Empty(fixture.VisitedKeys);
        Assert.Null(fixture.Frontier.LastGhostedManualDestination);
    }

    private static DungeonFrontierPoint ForceMarchXyz(string key, Vector3 position, bool usePlayerYForNavigation = false)
        => new()
        {
            Key = key,
            Name = key,
            Position = position,
            LevelRowId = 0,
            MapId = 1,
            ManualDestinationKind = ManualDestinationKind.Xyz,
            AllowCombatBypass = true,
            UsePlayerYForNavigation = usePlayerYForNavigation,
            ArrivalRadius3d = 1f,
        };

    private static DungeonFrontierPoint ForceMarchMapXz(string key, Vector3 position)
        => new()
        {
            Key = key,
            Name = key,
            Position = position,
            LevelRowId = 0,
            MapId = 1,
            ManualDestinationKind = ManualDestinationKind.MapXz,
            AllowCombatBypass = true,
            UsePlayerYForNavigation = true,
            ArrivalRadiusXz = 1f,
        };

    private static ObservedMonster Monster(string name, Vector3 position)
        => new()
        {
            Key = name,
            GameObjectId = 2,
            DataId = 200,
            MapId = 1,
            Name = name,
            Position = position,
            LastSeenUtc = DateTime.UtcNow,
        };

    private static ObservedInteractable Interactable(
        string name,
        InteractableClass classification,
        Vector3 position)
        => new()
        {
            Key = name,
            GameObjectId = 1,
            DataId = 100,
            MapId = 1,
            ObjectKind = ObjectKind.EventObj,
            Name = name,
            Position = position,
            LastSeenUtc = DateTime.UtcNow,
            Classification = classification,
            GhostReason = GhostReason.SeenPreviously,
        };

    private static ObservationSnapshot Observation(
        IReadOnlyList<ObservedInteractable>? interactables = null,
        IReadOnlyList<ObservedMonster>? monsters = null,
        IReadOnlyList<ObservedMonster>? followTargets = null,
        IReadOnlyList<ObservedMonster>? monsterGhosts = null,
        IReadOnlyList<ObservedInteractable>? interactableGhosts = null)
        => new()
        {
            LiveMonsters = monsters ?? [],
            LiveFollowTargets = followTargets ?? [],
            MonsterGhosts = monsterGhosts ?? [],
            LiveInteractables = interactables ?? [],
            InteractableGhosts = interactableGhosts ?? [],
        };

    private static DutyContextSnapshot Context(
        bool pluginEnabled = true,
        bool inCombat = false,
        bool mounted = false,
        bool betweenAreas = false)
        => new()
        {
            PluginEnabled = pluginEnabled,
            IsLoggedIn = true,
            BoundByDuty = true,
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
            InCombat = inCombat,
            Mounted = mounted,
            TerritoryTypeId = 100,
            MapId = 1,
            ContentFinderConditionId = 200,
            CurrentDuty = null,
        };

    private static PlannerSnapshot Planner(PlannerObjectiveKind kind, string target)
        => new()
        {
            Mode = kind == PlannerObjectiveKind.None ? PlannerMode.IdleObserve : PlannerMode.Progression,
            ObjectiveKind = kind,
            Objective = target,
            Explanation = target,
            TargetName = target,
            CapturedAtUtc = DateTime.UtcNow,
        };

    private sealed class ServiceFixture : IDisposable
    {
        private readonly TempDirectory tempDirectory = new();
        private readonly ObjectTableProxy objectTableProxy;

        public ServiceFixture(Vector3 playerPosition)
        {
            var log = DispatchProxy.Create<IPluginLog, NoOpProxy>();
            var objectTable = DispatchProxy.Create<IObjectTable, ObjectTableProxy>();
            objectTableProxy = (ObjectTableProxy)(object)objectTable;
            objectTableProxy.LocalPlayer = CreatePlayer(playerPosition);
            ObjectTable = objectTable;
            Commands = [];

            RuleService = new ObjectPriorityRuleService(log, null!, tempDirectory.Path);
            Frontier = new DungeonFrontierService(null!, ObjectTable, log, RuleService, null!);
            Planner = new ObjectivePlannerService(ObjectTable, RuleService, Frontier, null!);

            var commandManager = DispatchProxy.Create<ICommandManager, CommandManagerProxy>();
            ((CommandManagerProxy)(object)commandManager).Commands = Commands;
            var keyState = DispatchProxy.Create<IKeyState, NoOpProxy>();
            Execution = new ExecutionService(
                null!,
                ObjectTable,
                null!,
                commandManager,
                null!,
                Frontier,
                null!,
                RuleService,
                new TreasureDoorStrafeInputService(keyState, log),
                new CardinalHoldInputService(keyState, log),
                new Configuration(),
                log);
        }

        public IObjectTable ObjectTable { get; }

        public ObjectPriorityRuleService RuleService { get; }

        public DungeonFrontierService Frontier { get; }

        public ObjectivePlannerService Planner { get; }

        public ExecutionService Execution { get; }

        public List<string> Commands { get; }

        public HashSet<string> VisitedKeys
            => GetPrivateField<HashSet<string>>(Frontier, "visitedFrontierKeys");

        public DungeonFrontierPoint? CommittedForceMarch
            => GetPrivateField<DungeonFrontierPoint?>(Execution, "committedForceMarchManualDestination");

        public void SetFrontierTarget(DungeonFrontierPoint target, FrontierMode mode)
        {
            SetPrivateSetProperty(Frontier, nameof(DungeonFrontierService.CurrentTarget), target);
            SetPrivateSetProperty(Frontier, nameof(DungeonFrontierService.CurrentMode), mode);
        }

        public void CommitForceMarch(DungeonFrontierPoint point)
            => SetPrivateField(Execution, "committedForceMarchManualDestination", point);

        public void SetExecutionMode(OwnershipMode mode)
            => SetPrivateSetProperty(Execution, nameof(ExecutionService.CurrentMode), mode);

        public void SetPendingProgressionInteractable(ObservedInteractable interactable)
            => SetPrivateField(Execution, "pendingProgressionInteractable", interactable);

        public bool InvokeTryAdvanceCommittedForceMarch(DutyContextSnapshot context, PlannerSnapshot planner)
            => (bool)typeof(ExecutionService)
                .GetMethod("TryAdvanceCommittedForceMarchManualDestination", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(Execution, [context, planner, "test"])!;

        public bool InvokeTryRetireManualDestinationForNoProgress(DungeonFrontierPoint point, Vector3 playerPosition)
            => (bool)typeof(ExecutionService)
                .GetMethod("TryRetireManualDestinationForNoProgress", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(Execution,
                [
                    point,
                    playerPosition,
                    point.IsManualXyzDestination,
                    40f,
                    40f,
                    0f,
                    "test",
                ])!;

        public void Dispose()
            => tempDirectory.Dispose();

        private static IPlayerCharacter CreatePlayer(Vector3 position)
        {
            var player = DispatchProxy.Create<IPlayerCharacter, GameObjectProxy>();
            ((GameObjectProxy)(object)player).Position = position;
            return player;
        }

        private static T GetPrivateField<T>(object target, string fieldName)
            => (T)target
                .GetType()
                .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(target)!;

        private static void SetPrivateField<T>(object target, string fieldName, T value)
            => target
                .GetType()
                .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(target, value);

        private static void SetPrivateSetProperty<T>(object target, string propertyName, T value)
            => target
                .GetType()
                .GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
                .SetValue(target, value);
    }

    public class ObjectTableProxy : DispatchProxy
    {
        public IPlayerCharacter? LocalPlayer { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod?.Name switch
            {
                "get_LocalPlayer" => LocalPlayer,
                "GetEnumerator" => Array.Empty<IGameObject>().AsEnumerable().GetEnumerator(),
                _ => DefaultValue(targetMethod),
            };
    }

    public class GameObjectProxy : DispatchProxy
    {
        public Vector3 Position { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod?.Name == "get_Position"
                ? Position
                : DefaultValue(targetMethod);
    }

    public class CommandManagerProxy : DispatchProxy
    {
        public List<string> Commands { get; set; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "ProcessCommand" && args is [string command])
            {
                Commands.Add(command);
                return true;
            }

            return DefaultValue(targetMethod);
        }
    }

    public class NoOpProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => DefaultValue(targetMethod);
    }

    private static object? DefaultValue(MethodInfo? targetMethod)
    {
        if (targetMethod is null)
            return null;

        if (targetMethod.ReturnType == typeof(void) || !targetMethod.ReturnType.IsValueType)
            return null;

        return Activator.CreateInstance(targetMethod.ReturnType);
    }
}
