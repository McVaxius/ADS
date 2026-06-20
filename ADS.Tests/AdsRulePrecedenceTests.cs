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

public sealed class AdsRulePrecedenceTests
{
    [Fact]
    public void LiveRequiredObjectRuleBeatsGeneratedLabelFrontier()
    {
        using var fixture = new PlannerFixture(
            Vector3.Zero,
            Rule("Transporter", InteractableClass.Required, priority: 10));
        fixture.SetFrontier(Label("Generated Lumina Label"), FrontierMode.Label);

        fixture.Planner.Update(
            Context(),
            Observation(interactables: [Interactable("Transporter", InteractableClass.Required, new Vector3(20f, 0f, 0f))]),
            OwnershipMode.OwnedStartInside,
            considerTreasureCoffers: false);

        Assert.Equal(PlannerObjectiveKind.RequiredInteractable, fixture.Planner.Current.ObjectiveKind);
        Assert.Equal("Transporter", fixture.Planner.Current.TargetName);
    }

    [Fact]
    public void LiveRequiredObjectRuleBeatsXyzForceMarchOutsideCombat()
    {
        using var fixture = new PlannerFixture(
            Vector3.Zero,
            Rule("Transporter", InteractableClass.Required, priority: 10));
        fixture.SetFrontier(ManualXyz("Force March", priority: 0, allowCombatBypass: true), FrontierMode.XyzDestination);

        fixture.Planner.Update(
            Context(),
            Observation(interactables: [Interactable("Transporter", InteractableClass.Required, new Vector3(20f, 0f, 0f))]),
            OwnershipMode.OwnedStartInside,
            considerTreasureCoffers: false);

        Assert.Equal(PlannerObjectiveKind.RequiredInteractable, fixture.Planner.Current.ObjectiveKind);
        Assert.Equal("Transporter", fixture.Planner.Current.TargetName);
    }

    [Fact]
    public void XyzForceMarchCombatBypassStillWorksWithoutLiveProgressionObject()
    {
        using var fixture = new PlannerFixture(Vector3.Zero);
        fixture.SetFrontier(ManualXyz("Force March", priority: 0, allowCombatBypass: true), FrontierMode.XyzDestination);

        fixture.Planner.Update(
            Context(inCombat: true),
            Observation(monsters: [Monster("Incidental Trash", new Vector3(10f, 0f, 0f))]),
            OwnershipMode.OwnedStartInside,
            considerTreasureCoffers: false);

        Assert.Equal(PlannerObjectiveKind.XyzForceMarchDestination, fixture.Planner.Current.ObjectiveKind);
        Assert.Equal("Force March", fixture.Planner.Current.TargetName);
    }

    [Theory]
    [InlineData(InteractableClass.Optional, PlannerObjectiveKind.OptionalInteractable)]
    [InlineData(InteractableClass.Expendable, PlannerObjectiveKind.ExpendableInteractable)]
    public void AuthoredLowerValueObjectRuleBeatsGeneratedLuminaLabel(
        InteractableClass classification,
        PlannerObjectiveKind expectedKind)
    {
        using var fixture = new PlannerFixture(
            Vector3.Zero,
            Rule("Side Console", classification, priority: 10));
        fixture.SetFrontier(Label("Generated Lumina Label"), FrontierMode.Label);

        fixture.Planner.Update(
            Context(),
            Observation(interactables: [Interactable("Side Console", classification, new Vector3(20f, 0f, 0f))]),
            OwnershipMode.OwnedStartInside,
            considerTreasureCoffers: false);

        Assert.Equal(expectedKind, fixture.Planner.Current.ObjectiveKind);
        Assert.Equal("Side Console", fixture.Planner.Current.TargetName);
    }

    [Theory]
    [InlineData(5f, 0f)]
    [InlineData(null, 2f)]
    public void FailedDistanceOrVerticalGateDoesNotBlockGeneratedLabel(float? maxDistance, float verticalRadius)
    {
        using var fixture = new PlannerFixture(
            Vector3.Zero,
            Rule(
                "Distant Console",
                InteractableClass.Required,
                priority: 10,
                maxDistance: maxDistance,
                verticalRadius: verticalRadius));
        fixture.SetFrontier(Label("Generated Lumina Label"), FrontierMode.Label);
        var position = maxDistance.HasValue
            ? new Vector3(20f, 0f, 0f)
            : new Vector3(0f, 10f, 0f);

        fixture.Planner.Update(
            Context(),
            Observation(interactables: [Interactable("Distant Console", InteractableClass.Required, position)]),
            OwnershipMode.OwnedStartInside,
            considerTreasureCoffers: false);

        Assert.Equal(PlannerObjectiveKind.Frontier, fixture.Planner.Current.ObjectiveKind);
        Assert.Equal("Generated Lumina Label", fixture.Planner.Current.TargetName);
    }

    private static ObjectPriorityRule Rule(
        string name,
        InteractableClass classification,
        int priority,
        float? maxDistance = null,
        float verticalRadius = 0f)
        => new()
        {
            ObjectKind = ObjectKind.EventObj.ToString(),
            ObjectName = name,
            NameMatchMode = "Exact",
            Classification = classification.ToString(),
            Priority = priority,
            MaxDistance = maxDistance,
            PriorityVerticalRadius = verticalRadius,
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

    private static ObservationSnapshot Observation(
        IReadOnlyList<ObservedInteractable>? interactables = null,
        IReadOnlyList<ObservedMonster>? monsters = null)
        => new()
        {
            LiveMonsters = monsters ?? [],
            LiveFollowTargets = [],
            MonsterGhosts = [],
            LiveInteractables = interactables ?? [],
            InteractableGhosts = [],
        };

    private static DungeonFrontierPoint Label(string name)
        => new()
        {
            Key = $"label:{name}",
            Name = name,
            Position = new Vector3(40f, 0f, 0f),
            LevelRowId = 1,
            MapId = 1,
        };

    private static DungeonFrontierPoint ManualXyz(string name, int priority, bool allowCombatBypass)
        => new()
        {
            Key = $"xyz:{name}",
            Name = name,
            Position = new Vector3(40f, 0f, 0f),
            LevelRowId = 0,
            MapId = 1,
            Priority = priority,
            ManualDestinationKind = ManualDestinationKind.Xyz,
            AllowCombatBypass = allowCombatBypass,
            ArrivalRadius3d = 1f,
        };

    private static DutyContextSnapshot Context(bool inCombat = false)
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
            InCombat = inCombat,
            Mounted = false,
            TerritoryTypeId = 100,
            MapId = 1,
            ContentFinderConditionId = 200,
            CurrentDuty = null,
        };

    private sealed class PlannerFixture : IDisposable
    {
        private readonly TempDirectory tempDirectory = new();

        public PlannerFixture(Vector3 playerPosition, params ObjectPriorityRule[] rules)
        {
            var log = DispatchProxy.Create<IPluginLog, NoOpProxy>();
            var objectTable = CreateObjectTable(playerPosition);
            RuleService = new ObjectPriorityRuleService(log, null!, tempDirectory.Path);
            if (!RuleService.SaveManifest(new ObjectPriorityRuleManifest { Rules = [.. rules] }))
                throw new InvalidOperationException(RuleService.LastLoadStatus);

            FrontierService = new DungeonFrontierService(null!, objectTable, log, RuleService, null!);
            Planner = new ObjectivePlannerService(objectTable, RuleService, FrontierService, null!);
        }

        public ObjectPriorityRuleService RuleService { get; }

        public DungeonFrontierService FrontierService { get; }

        public ObjectivePlannerService Planner { get; }

        public void SetFrontier(DungeonFrontierPoint target, FrontierMode mode)
        {
            SetPrivateSetProperty(FrontierService, nameof(DungeonFrontierService.CurrentTarget), target);
            SetPrivateSetProperty(FrontierService, nameof(DungeonFrontierService.CurrentMode), mode);
        }

        public void Dispose()
            => tempDirectory.Dispose();

        private static IObjectTable CreateObjectTable(Vector3 playerPosition)
        {
            var player = DispatchProxy.Create<IPlayerCharacter, GameObjectProxy>();
            ((GameObjectProxy)(object)player).Position = playerPosition;

            var objectTable = DispatchProxy.Create<IObjectTable, ObjectTableProxy>();
            ((ObjectTableProxy)(object)objectTable).LocalPlayer = player;
            return objectTable;
        }

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
