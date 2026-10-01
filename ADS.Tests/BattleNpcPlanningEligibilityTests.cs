using System.Numerics;
using System.Reflection;
using ADS.Models;
using ADS.Services;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;

namespace ADS.Tests;

public sealed class BattleNpcPlanningEligibilityTests
{
    [Theory]
    [InlineData(BattleNpcSubKind.Pet, null, true)]
    [InlineData(BattleNpcSubKind.Pet, "Required", true)]
    [InlineData(BattleNpcSubKind.Pet, "Follow", true)]
    [InlineData(BattleNpcSubKind.Pet, "CombatFriendly", true)]
    [InlineData(BattleNpcSubKind.Buddy, null, true)]
    [InlineData(BattleNpcSubKind.Buddy, "Required", true)]
    [InlineData(BattleNpcSubKind.Buddy, "Follow", true)]
    [InlineData(BattleNpcSubKind.Buddy, "CombatFriendly", true)]
    [InlineData(BattleNpcSubKind.NpcPartyMember, null, true)]
    [InlineData(BattleNpcSubKind.NpcPartyMember, "Required", true)]
    [InlineData(BattleNpcSubKind.NpcPartyMember, "Follow", true)]
    [InlineData(BattleNpcSubKind.NpcPartyMember, "CombatFriendly", true)]
    [InlineData(BattleNpcSubKind.Combatant, null, false)]
    [InlineData(BattleNpcSubKind.Combatant, "Required", false)]
    [InlineData(BattleNpcSubKind.Combatant, "Follow", false)]
    [InlineData(BattleNpcSubKind.Combatant, "CombatFriendly", false)]
    public void ObservationExcludesCompanionsBeforeRulesAndForgetsRememberedState(
        BattleNpcSubKind subKind,
        string? classification,
        bool excluded)
    {
        using var fixture = new RuleServiceFixture();
        var battleNpc = DispatchProxy.Create<IBattleNpc, BattleNpcProxy>();
        var proxy = (BattleNpcProxy)(object)battleNpc;
        proxy.Name = "Target";
        proxy.ObjectKind = ObjectKind.BattleNpc;
        proxy.GameObjectId = 1;
        proxy.BaseId = 100;
        proxy.IsTargetable = true;
        proxy.BattleNpcKind = BattleNpcSubKind.Combatant;

        var objectTable = DispatchProxy.Create<IObjectTable, TreasureCofferObservationPolicyTests.ObjectTableProxy>();
        var objectTableProxy = (TreasureCofferObservationPolicyTests.ObjectTableProxy)(object)objectTable;
        objectTableProxy.Objects = [battleNpc];
        var partyList = DispatchProxy.Create<IPartyList, TreasureCofferObservationPolicyTests.PartyListProxy>();
        var log = DispatchProxy.Create<IPluginLog, NoOpProxy>();
        var observation = new ObservationMemoryService(objectTable, partyList, log, fixture.Service);
        var context = Context();

        observation.Update(context, considerTreasureCoffers: true);
        var rememberedMonster = Assert.Single(observation.Current.LiveMonsters);
        var knownMonsters = GetPrivateDictionary<string, ObservedMonster>(observation, "knownMonsters");
        var knownInteractables = GetPrivateDictionary<string, ObservedInteractable>(observation, "knownInteractables");
        var suppressions = GetPrivateDictionary<string, DateTime>(observation, "treasureSuppressionUntil");
        knownInteractables[rememberedMonster.Key] = new ObservedInteractable
        {
            Key = rememberedMonster.Key,
            GameObjectId = rememberedMonster.GameObjectId,
            DataId = rememberedMonster.DataId,
            MapId = rememberedMonster.MapId,
            ObjectKind = ObjectKind.BattleNpc,
            Name = rememberedMonster.Name,
            Position = rememberedMonster.Position,
            LastSeenUtc = rememberedMonster.LastSeenUtc,
            Classification = InteractableClass.CombatFriendly,
            GhostReason = GhostReason.SeenPreviously,
        };
        suppressions[rememberedMonster.Key] = DateTime.UtcNow.AddMinutes(1);
        if (classification is not null)
            Assert.True(fixture.Service.SaveManifest(new ObjectPriorityRuleManifest { Rules = [Rule(classification, priority: 10)] }));

        proxy.BattleNpcKind = subKind;
        observation.Update(context, considerTreasureCoffers: true);

        if (excluded)
        {
            Assert.Empty(observation.Current.LiveMonsters);
            Assert.Empty(observation.Current.LiveFollowTargets);
            Assert.Empty(observation.Current.LiveInteractables);
            Assert.Empty(observation.Current.MonsterGhosts);
            Assert.Empty(observation.Current.InteractableGhosts);
            Assert.Empty(knownMonsters);
            Assert.Empty(knownInteractables);
            Assert.Empty(suppressions);

            objectTableProxy.Objects = [];
            observation.Update(context, considerTreasureCoffers: true);
            Assert.Empty(observation.Current.MonsterGhosts);
            Assert.Empty(observation.Current.InteractableGhosts);
        }
        else if (classification == "Follow")
        {
            Assert.Equal(rememberedMonster.Key, Assert.Single(observation.Current.LiveFollowTargets).Key);
        }
        else if (classification == "CombatFriendly")
        {
            Assert.Equal(rememberedMonster.Key, Assert.Single(observation.Current.LiveInteractables).Key);
        }
        else
        {
            Assert.Equal(rememberedMonster.Key, Assert.Single(observation.Current.LiveMonsters).Key);
        }
    }

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

    private static Dictionary<TKey, TValue> GetPrivateDictionary<TKey, TValue>(object target, string fieldName)
        where TKey : notnull
        => (Dictionary<TKey, TValue>)target
            .GetType()
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(target)!;

    public class BattleNpcProxy : TreasureCofferObservationPolicyTests.GameObjectProxy
    {
        public BattleNpcSubKind BattleNpcKind { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod?.Name switch
            {
                "get_BattleNpcKind" => BattleNpcKind,
                "get_CurrentHp" => 100u,
                _ => base.Invoke(targetMethod, args),
            };
    }

    public class NoOpProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod is null || targetMethod.ReturnType == typeof(void) || !targetMethod.ReturnType.IsValueType
                ? null
                : Activator.CreateInstance(targetMethod.ReturnType);
    }
}
