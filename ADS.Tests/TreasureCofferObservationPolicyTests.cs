using System.Collections;
using System.Numerics;
using System.Reflection;
using ADS.Models;
using ADS.Services;
using Dalamud.Game.ClientState.Party;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;

namespace ADS.Tests;

public sealed class TreasureCofferObservationPolicyTests
{
    [Theory]
    [InlineData(InteractableClass.Required)]
    [InlineData(InteractableClass.Optional)]
    [InlineData(InteractableClass.Expendable)]
    public void OffRejectsReportedCofferBeforeAuthoredRule(InteractableClass classification)
    {
        using var fixture = new ObservationFixture(
            Context(),
            [GameObject("Treasure Coffer", ObjectKind.EventObj)],
            Rule("Treasure Coffer", classification));

        fixture.Service.Update(fixture.Context, considerTreasureCoffers: false);

        Assert.Empty(fixture.Service.Current.LiveInteractables);
        Assert.Empty(fixture.Service.Current.InteractableGhosts);
    }

    [Theory]
    [InlineData("Treasure Coffer")]
    [InlineData("Ornate Coffer")]
    [InlineData("Treasure Chest")]
    [InlineData("Chest")]
    [InlineData("Wooden Chest")]
    [InlineData("Staging Coffer: Gilded Strongbox")]
    public void OffRejectsRecognizedCofferAndChestNames(string name)
    {
        Assert.True(ObservationMemoryService.ShouldHardIgnoreTreasureLoot(
            Context(),
            considerTreasureCoffers: false,
            ObjectKind.EventObj,
            name));
    }

    [Fact]
    public void OffRejectsSupportedTreasureDungeonSackNames()
    {
        Assert.True(ObservationMemoryService.ShouldHardIgnoreTreasureLoot(
            Context(territoryTypeId: 712),
            considerTreasureCoffers: false,
            ObjectKind.EventNpc,
            "Leather Sack"));
    }

    [Fact]
    public void OffRejectsGenericTreasureKindWithoutLootName()
    {
        Assert.True(ObservationMemoryService.ShouldHardIgnoreTreasureLoot(
            Context(),
            considerTreasureCoffers: false,
            ObjectKind.Treasure,
            "Mysterious Cache"));
    }

    [Theory]
    [InlineData(ObjectKind.EventObj, "Treasure Coffer")]
    [InlineData(ObjectKind.EventNpc, "Treasure Chest")]
    [InlineData(ObjectKind.Treasure, "Mysterious Cache")]
    public void EnabledPolicyDoesNotHardIgnoreTreasureLoot(ObjectKind objectKind, string name)
    {
        Assert.False(ObservationMemoryService.ShouldHardIgnoreTreasureLoot(
            Context(),
            considerTreasureCoffers: true,
            objectKind,
            name));
    }

    [Fact]
    public void EnabledUpdatePreservesExistingTreasureClassification()
    {
        using var fixture = new ObservationFixture(
            Context(),
            [
                GameObject("Treasure Coffer", ObjectKind.EventObj, gameObjectId: 1),
                GameObject("Mysterious Cache", ObjectKind.Treasure, gameObjectId: 2),
            ]);

        fixture.Service.Update(fixture.Context, considerTreasureCoffers: true);

        Assert.Collection(
            fixture.Service.Current.LiveInteractables.OrderBy(x => x.GameObjectId),
            interactable => Assert.Equal(InteractableClass.TreasureCoffer, interactable.Classification),
            interactable => Assert.Equal(InteractableClass.TreasureCoffer, interactable.Classification));
    }

    [Theory]
    [InlineData(ObjectKind.EventObj)]
    [InlineData(ObjectKind.Treasure)]
    public void OffPreservesTreasureDoorProgression(ObjectKind objectKind)
    {
        using var fixture = new ObservationFixture(
            Context(territoryTypeId: 558),
            [GameObject("Vault Door", objectKind)]);

        fixture.Service.Update(fixture.Context, considerTreasureCoffers: false);

        var door = Assert.Single(fixture.Service.Current.LiveInteractables);
        Assert.Equal("Vault Door", door.Name);
        Assert.Equal(InteractableClass.TreasureDoor, door.Classification);
    }

    [Fact]
    public void OffPurgesRememberedLootAndSuppressionWithoutRemovingTreasureDoor()
    {
        using var fixture = new ObservationFixture(Context(territoryTypeId: 558), []);
        fixture.Service.Update(fixture.Context, considerTreasureCoffers: true);

        var known = GetPrivateDictionary<string, ObservedInteractable>(fixture.Service, "knownInteractables");
        known["coffer"] = Interactable("coffer", "Treasure Coffer", ObjectKind.EventObj, InteractableClass.Required);
        known["treasure"] = Interactable("treasure", "Mysterious Cache", ObjectKind.Treasure, InteractableClass.TreasureCoffer);
        known["door"] = Interactable("door", "Vault Door", ObjectKind.EventObj, InteractableClass.TreasureDoor);

        var suppressions = GetPrivateDictionary<string, DateTime>(fixture.Service, "treasureSuppressionUntil");
        suppressions["coffer"] = DateTime.UtcNow.AddMinutes(1);
        suppressions["treasure"] = DateTime.UtcNow.AddMinutes(1);
        suppressions["orphaned-treasure-key"] = DateTime.UtcNow.AddMinutes(1);

        fixture.Service.Update(fixture.Context, considerTreasureCoffers: false);

        var remaining = Assert.Single(fixture.Service.Current.InteractableGhosts);
        Assert.Equal("Vault Door", remaining.Name);
        Assert.Equal(InteractableClass.TreasureDoor, remaining.Classification);
        Assert.Equal(["door"], known.Keys);
        Assert.Empty(suppressions);
    }

    [Fact]
    public void OffPreservesUnrelatedAuthoredInteractionsAndReenableRediscoversCoffers()
    {
        using var fixture = new ObservationFixture(
            Context(territoryTypeId: 558),
            [
                GameObject("Treasure Coffer", ObjectKind.EventObj, gameObjectId: 1),
                GameObject("Optional Terminal", ObjectKind.EventObj, gameObjectId: 2),
                GameObject("Required Switch", ObjectKind.EventObj, gameObjectId: 3),
                GameObject("Vault Door", ObjectKind.Treasure, gameObjectId: 4),
            ],
            Rule("Optional Terminal", InteractableClass.Optional),
            Rule("Required Switch", InteractableClass.Required));

        fixture.Service.Update(fixture.Context, considerTreasureCoffers: true);
        Assert.Contains(fixture.Service.Current.LiveInteractables,
            item => item.GameObjectId == 1 && item.Classification == InteractableClass.TreasureCoffer);

        fixture.Service.Update(fixture.Context, considerTreasureCoffers: false);
        Assert.Collection(fixture.Service.Current.LiveInteractables.OrderBy(item => item.GameObjectId),
            item => Assert.Equal(InteractableClass.Optional, item.Classification),
            item => Assert.Equal(InteractableClass.Required, item.Classification),
            item => Assert.Equal(InteractableClass.TreasureDoor, item.Classification));
        Assert.DoesNotContain(fixture.Service.Current.InteractableGhosts, item => item.GameObjectId == 1);

        fixture.Service.Update(fixture.Context, considerTreasureCoffers: true);
        Assert.Contains(fixture.Service.Current.LiveInteractables,
            item => item.GameObjectId == 1 && item.Classification == InteractableClass.TreasureCoffer);
        Assert.Equal(4, fixture.Service.Current.LiveInteractables.Count);
    }

    [Theory]
    [InlineData(42u, 214u, ObjectKind.EventNpc, 1004745u, true)]
    [InlineData(42u, 214u, ObjectKind.EventNpc, 1004746u, true)]
    [InlineData(42u, 215u, ObjectKind.EventNpc, 1004746u, false)]
    [InlineData(43u, 214u, ObjectKind.EventNpc, 1004746u, false)]
    [InlineData(42u, 214u, ObjectKind.EventObj, 1004746u, false)]
    [InlineData(42u, 214u, ObjectKind.EventNpc, 1004747u, false)]
    public void EnemyPartiesDoesNotObserveTiroroAsProgression(
        uint cfc, uint territory, ObjectKind kind, uint baseId, bool ignored)
    {
        const string localizedName = "Localized guide";
        var guide = GameObject(localizedName, kind);
        ((GameObjectProxy)(object)guide).BaseId = baseId;
        using var fixture = new ObservationFixture(
            TestDutyContextFactory.Create(territoryId: territory, cfcId: cfc),
            [guide],
            new ObjectPriorityRule
            {
                ObjectKind = kind.ToString(),
                BaseId = baseId,
                Classification = InteractableClass.Required.ToString(),
                Priority = 0,
            });

        fixture.Service.Update(fixture.Context, considerTreasureCoffers: false);

        if (ignored)
            Assert.Empty(fixture.Service.Current.LiveInteractables);
        else
            Assert.Equal(InteractableClass.Required, Assert.Single(fixture.Service.Current.LiveInteractables).Classification);
        Assert.Empty(fixture.Service.Current.InteractableGhosts);

        var guideProxy = (GameObjectProxy)(object)guide;
        guideProxy.IsTargetable = false;
        var known = GetPrivateDictionary<string, ObservedInteractable>(fixture.Service, "knownInteractables");
        known["remembered-guide"] = Interactable("remembered-guide", localizedName, kind, InteractableClass.Required, baseId);

        fixture.Service.Update(fixture.Context, considerTreasureCoffers: false);

        Assert.Empty(fixture.Service.Current.LiveInteractables);
        Assert.Equal(!ignored, fixture.Service.Current.InteractableGhosts.Any(item => item.Key == "remembered-guide"));
    }

    private static ObjectPriorityRule Rule(string name, InteractableClass classification)
        => new()
        {
            ObjectKind = ObjectKind.EventObj.ToString(),
            ObjectName = name,
            NameMatchMode = "Exact",
            Classification = classification.ToString(),
            Priority = 0,
        };

    private static DutyContextSnapshot Context(uint territoryTypeId = 100)
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
            TerritoryTypeId = territoryTypeId,
            MapId = 1,
            ContentFinderConditionId = 200,
            CurrentDuty = null,
        };

    private static IGameObject GameObject(
        string name,
        ObjectKind objectKind,
        ulong gameObjectId = 1)
    {
        var gameObject = DispatchProxy.Create<IGameObject, GameObjectProxy>();
        var proxy = (GameObjectProxy)(object)gameObject;
        proxy.Name = name;
        proxy.ObjectKind = objectKind;
        proxy.GameObjectId = gameObjectId;
        proxy.BaseId = 100;
        proxy.IsTargetable = true;
        proxy.Position = new Vector3(gameObjectId, 0f, 0f);
        return gameObject;
    }

    private static ObservedInteractable Interactable(
        string key,
        string name,
        ObjectKind objectKind,
        InteractableClass classification,
        uint baseId = 100)
        => new()
        {
            Key = key,
            GameObjectId = 1,
            DataId = baseId,
            MapId = 1,
            ObjectKind = objectKind,
            Name = name,
            Position = Vector3.Zero,
            LastSeenUtc = DateTime.UtcNow,
            Classification = classification,
            GhostReason = GhostReason.SeenPreviously,
        };

    private static Dictionary<TKey, TValue> GetPrivateDictionary<TKey, TValue>(object target, string fieldName)
        where TKey : notnull
        => (Dictionary<TKey, TValue>)target
            .GetType()
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(target)!;

    private sealed class ObservationFixture : IDisposable
    {
        private readonly TempDirectory tempDirectory = new();

        public ObservationFixture(
            DutyContextSnapshot context,
            IReadOnlyList<IGameObject> objects,
            params ObjectPriorityRule[] rules)
        {
            Context = context;
            var log = DispatchProxy.Create<IPluginLog, NoOpProxy>();
            var objectTable = DispatchProxy.Create<IObjectTable, ObjectTableProxy>();
            ((ObjectTableProxy)(object)objectTable).Objects = objects;
            var partyList = DispatchProxy.Create<IPartyList, PartyListProxy>();
            var ruleService = new ObjectPriorityRuleService(log, null!, tempDirectory.Path);
            if (!ruleService.SaveManifest(new ObjectPriorityRuleManifest { Rules = [.. rules] }))
                throw new InvalidOperationException(ruleService.LastLoadStatus);

            Service = new ObservationMemoryService(objectTable, partyList, log, ruleService);
        }

        public DutyContextSnapshot Context { get; }

        public ObservationMemoryService Service { get; }

        public void Dispose()
            => tempDirectory.Dispose();
    }

    public class ObjectTableProxy : DispatchProxy
    {
        public IReadOnlyList<IGameObject> Objects { get; set; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod?.Name switch
            {
                "get_LocalPlayer" => null,
                "GetEnumerator" => Objects.GetEnumerator(),
                _ => DefaultValue(targetMethod),
            };
    }

    public class PartyListProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod?.Name == "GetEnumerator"
                ? Array.Empty<IPartyMember>().AsEnumerable().GetEnumerator()
                : DefaultValue(targetMethod);
    }

    public class GameObjectProxy : DispatchProxy
    {
        public string Name { get; set; } = string.Empty;

        public ObjectKind ObjectKind { get; set; }

        public ulong GameObjectId { get; set; }

        public uint BaseId { get; set; }

        public bool IsTargetable { get; set; }

        public Vector3 Position { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod?.Name switch
            {
                "get_Name" => new SeString(new TextPayload(Name)),
                "get_ObjectKind" => ObjectKind,
                "get_GameObjectId" => GameObjectId,
                "get_BaseId" => BaseId,
                "get_IsTargetable" => IsTargetable,
                "get_Position" => Position,
                "IsValid" => true,
                _ => DefaultValue(targetMethod),
            };
    }

    public class NoOpProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => DefaultValue(targetMethod);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"ads-treasure-policy-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
            => Directory.Delete(Path, recursive: true);
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
