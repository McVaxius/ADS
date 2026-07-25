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
    [Fact]
    public void OffRejectsReportedCofferBeforeAuthoredRequiredRule()
    {
        using var fixture = new ObservationFixture(
            Context(),
            [GameObject("Treasure Coffer", ObjectKind.EventObj)],
            Rule("Treasure Coffer", InteractableClass.Required));

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
        InteractableClass classification)
        => new()
        {
            Key = key,
            GameObjectId = 1,
            DataId = 100,
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
