using System.Numerics;
using System.Reflection;
using ADS.Models;
using ADS.Services;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Plugin.Services;

namespace ADS.Tests;

public sealed class BattleNpcPlanningEligibilityTests
{
    [Fact]
    public void RequiredOutsideYGateIsNotEligibleFrontierBlocker()
    {
        using var fixture = new RuleServiceFixture(Rule("Required", priority: 10, verticalRadius: 5f));

        var eligibility = fixture.Service.EvaluateBattleNpcPlanningEligibility(
            Context(),
            Monster("Target", new Vector3(0f, 10f, 0f)),
            Vector3.Zero);

        Assert.Null(eligibility.EffectiveRule);
        Assert.True(eligibility.SuppressedByRuleGates);
        Assert.False(eligibility.IsEligibleBlocker);
    }

    [Fact]
    public void RequiredInsideYGateIsEligible()
    {
        using var fixture = new RuleServiceFixture(Rule("Required", priority: 10, verticalRadius: 5f));

        var eligibility = fixture.Service.EvaluateBattleNpcPlanningEligibility(
            Context(),
            Monster("Target", new Vector3(0f, 4f, 0f)),
            Vector3.Zero);

        Assert.Equal(10, eligibility.EffectiveRule?.Priority);
        Assert.False(eligibility.SuppressedByRuleGates);
        Assert.True(eligibility.IsEligibleBlocker);
    }

    [Fact]
    public void FailedHigherPriorityRuleFallsThroughToLowerEligibleRule()
    {
        using var fixture = new RuleServiceFixture(
            Rule("Required", priority: 10, maxDistance: 5f),
            Rule("Required", priority: 20, maxDistance: 50f));
        var monster = Monster("Target", new Vector3(20f, 0f, 0f));

        var effectiveRule = fixture.Service.GetEffectiveBattleNpcRule(Context(), monster, 20f, 0f);

        Assert.Equal(20, effectiveRule?.Priority);
    }

    [Fact]
    public void FailedHigherPriorityRuleAllowsLowerEligibleClassificationToExecute()
    {
        using var fixture = new RuleServiceFixture(
            Rule("Required", priority: 10, maxDistance: 5f),
            Rule("Ignored", priority: 20, maxDistance: 50f));

        var shouldIgnore = fixture.Service.ShouldIgnoreObject(
            Context(),
            ObjectKind.BattleNpc,
            1,
            "Target",
            new Vector3(20f, 0f, 0f),
            objectMapId: 0,
            distance: 20f,
            verticalDelta: 0f);

        Assert.True(shouldIgnore);
    }

    [Theory]
    [InlineData("Ignored")]
    [InlineData("Follow")]
    public void FailedIgnoredOrFollowRulePreservesGenericMonsterFallback(string classification)
    {
        using var fixture = new RuleServiceFixture(Rule(classification, priority: 10, maxDistance: 5f));

        var eligibility = fixture.Service.EvaluateBattleNpcPlanningEligibility(
            Context(),
            Monster("Target", new Vector3(20f, 0f, 0f)),
            Vector3.Zero);

        Assert.Null(eligibility.EffectiveRule);
        Assert.False(eligibility.SuppressedByRuleGates);
        Assert.True(eligibility.IsEligibleBlocker);
    }

    [Fact]
    public void UnruledMonsterRemainsBlocker()
    {
        using var fixture = new RuleServiceFixture();

        var eligibility = fixture.Service.EvaluateBattleNpcPlanningEligibility(
            Context(),
            Monster("Unruled", new Vector3(20f, 0f, 0f)),
            Vector3.Zero);

        Assert.Null(eligibility.EffectiveRule);
        Assert.False(eligibility.SuppressedByRuleGates);
        Assert.True(eligibility.IsEligibleBlocker);
    }

    [Fact]
    public void FailedGateMonsterDoesNotHideEligibleMonster()
    {
        using var fixture = new RuleServiceFixture(Rule("Required", priority: 10, maxDistance: 5f));
        var candidates = fixture.Service.EvaluateBattleNpcPlanningEligibility(
            Context(),
            [
                Monster("Target", new Vector3(20f, 0f, 0f)),
                Monster("Unruled", new Vector3(10f, 0f, 0f)),
            ],
            Vector3.Zero);

        var eligible = candidates.Where(x => x.IsEligibleBlocker).ToList();

        Assert.Single(eligible);
        Assert.Equal("Unruled", eligible[0].Monster.Name);
    }

    private static ObjectPriorityRule Rule(
        string classification,
        int priority,
        float verticalRadius = 0f,
        float? maxDistance = null)
        => new()
        {
            ObjectKind = ObjectKind.BattleNpc.ToString(),
            ObjectName = "Target",
            NameMatchMode = "Exact",
            Classification = classification,
            Priority = priority,
            PriorityVerticalRadius = verticalRadius,
            MaxDistance = maxDistance,
        };

    private static ObservedMonster Monster(string name, Vector3 position)
        => new()
        {
            Key = name,
            GameObjectId = 1,
            DataId = 1,
            MapId = 0,
            Name = name,
            Position = position,
            LastSeenUtc = DateTime.UtcNow,
        };

    private static DutyContextSnapshot Context()
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
            TerritoryTypeId = 100,
            MapId = 0,
            ContentFinderConditionId = 200,
            CurrentDuty = null,
        };

    private sealed class RuleServiceFixture : IDisposable
    {
        private readonly TempDirectory tempDirectory = new();

        public RuleServiceFixture(params ObjectPriorityRule[] rules)
        {
            var log = DispatchProxy.Create<IPluginLog, NoOpProxy>();
            Service = new ObjectPriorityRuleService(log, null!, tempDirectory.Path);
            if (!Service.SaveManifest(new ObjectPriorityRuleManifest { Rules = [.. rules] }))
                throw new InvalidOperationException(Service.LastLoadStatus);
        }

        public ObjectPriorityRuleService Service { get; }

        public void Dispose()
            => tempDirectory.Dispose();
    }

    public class NoOpProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod is null || targetMethod.ReturnType == typeof(void) || !targetMethod.ReturnType.IsValueType
                ? null
                : Activator.CreateInstance(targetMethod.ReturnType);
    }
}
