using System.Collections;
using System.Reflection;
using ADS.Models;
using ADS.Services;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace ADS.Tests;

public sealed class DutyBmrPolicyTests
{
    [Fact]
    public void SharedRefreshPreservesExplicitLocalChoicesAndContributionKeepsBoth()
    {
        using var directory = new TempDirectory();
        var duty = Duty();
        var config = new Configuration();
        var saves = 0;
        var service = new DutyBmrPolicyService(Log(), directory.Path, [duty], config, () => saves++);
        Assert.False(service.DisablesBmr(duty));
        File.WriteAllText(service.ConfigPath, Policy(true));
        Assert.True(service.Reload());
        Assert.True(service.DisablesBmr(duty));
        service.ApplyOverrides(new Dictionary<string, bool?> { ["11:22"] = false });
        Assert.Equal(1, saves);
        Assert.False(service.DisablesBmr(duty));
        Assert.True(service.Reload());
        Assert.False(service.DisablesBmr(duty));
        var contribution = DutyBmrPolicyService.ValidateJson(service.BuildContributionJson(), [duty]);
        Assert.False(Assert.Single(contribution.Duties).DisableBmr);
        service.ApplyOverrides(new Dictionary<string, bool?> { ["11:22"] = null });
        Assert.True(service.DisablesBmr(duty));
        Assert.Empty(config.DutyBmrPolicyOverrides);
        Assert.False(service.DisablesBmr(Duty(DutyCategory.FourMan)));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"SchemaVersion\":2,\"Duties\":[]}")]
    [InlineData("{\"SchemaVersion\":1,\"Duties\":null}")]
    [InlineData("{\"SchemaVersion\":1,\"Duties\":[{\"ContentFinderConditionId\":11,\"TerritoryTypeId\":22}]}")]
    [InlineData("{\"SchemaVersion\":1,\"Duties\":[{\"ContentFinderConditionId\":11,\"TerritoryTypeId\":23,\"DisableBmr\":true}]}")]
    [InlineData("{\"SchemaVersion\":1,\"Duties\":[{\"ContentFinderConditionId\":11,\"TerritoryTypeId\":22,\"DisableBmr\":true},{\"ContentFinderConditionId\":11,\"TerritoryTypeId\":22,\"DisableBmr\":true}]}")]
    public void InvalidSharedPolicyRetainsLastValidRuntimeChoice(string invalid)
    {
        using var directory = new TempDirectory();
        var duty = Duty();
        var service = new DutyBmrPolicyService(Log(), directory.Path, [duty], new Configuration());
        File.WriteAllText(service.ConfigPath, Policy(true));
        Assert.True(service.Reload());
        File.WriteAllText(service.ConfigPath, invalid);
        Assert.False(service.Reload());
        Assert.True(service.DisablesBmr(duty));
    }

    [Fact]
    public void NativeMetadataMatchingUsesExactCfcOrLinkedQuestAndOwnedSoloContext()
    {
        Assert.True(BmrReflectionService.MatchesDutyModuleInfo(new Info("CFC", 11), 11, 33));
        Assert.True(BmrReflectionService.MatchesDutyModuleInfo(new Info("Quest", 33), 11, 33));
        Assert.False(BmrReflectionService.MatchesDutyModuleInfo(new Info("Quest", 11), 11, 33));
        Assert.False(BmrReflectionService.MatchesDutyModuleInfo(new Info("Hunt", 11), 11, 33));
        Assert.False(BmrReflectionService.MatchesDutyModuleInfo(new Info("CFC", 11), 0, 0));
        Assert.Equal((11u, 33u), BmrReflectionService.ResolveDutyPolicyIdentity(Context(), OwnershipMode.OwnedStartInside, true));
        Assert.Equal((0u, 0u), BmrReflectionService.ResolveDutyPolicyIdentity(Context(), OwnershipMode.Observing, true));
        Assert.Equal((0u, 0u), BmrReflectionService.ResolveDutyPolicyIdentity(Context(), OwnershipMode.OwnedStartInside, false));
        Assert.Equal((0u, 0u), BmrReflectionService.ResolveDutyPolicyIdentity(Context(transition: true), OwnershipMode.OwnedStartInside, true));
        Assert.Equal((0u, 0u), BmrReflectionService.ResolveDutyPolicyIdentity(Context(category: DutyCategory.FourMan), OwnershipMode.OwnedStartInside, true));
        Assert.Equal((0u, 0u), BmrReflectionService.ResolveDutyPolicyIdentity(Context(territory: 23), OwnershipMode.OwnedStartInside, true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RegistryRemovalStopsOnlySelectedModulesAndRestoresActorsAfterStopOrExcludedEntry(bool excluded)
    {
        var config = new Configuration { ReflectionQueenLunatenderDisabled = true, ReflectionHuntsDisabled = true };
        var service = new BmrReflectionService(DispatchProxy.Create<IDalamudPluginInterface, EmptyPluginsProxy>(), config, Log());
        var registry = new Hashtable
        {
            [501u] = new Info("CFC", 11), [502u] = new Info("CFC", 12),
            [BmrReflectionService.QueenLunatenderOid] = new Info("CFC", 44), [503u] = new Info("Hunt", 1),
        };
        var selected = new Module(501);
        var retained = new Module(502);
        var pending = new ArrayList { selected, retained };
        var loaded = new ArrayList();
        var manager = new Manager();
        manager.WorldState.Actors.Add(new Actor(501));
        var type = typeof(BmrReflectionService);
        var contextType = type.GetNestedType("BmrReflectionContext", BindingFlags.NonPublic)!;
        var context = Activator.CreateInstance(contextType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, [null, new object(), manager, registry, new object(), pending, loaded, null], null)!;
        var dutyOids = (HashSet<uint>)type.GetField("dutySuppressedOids", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
        type.GetField("trackedRegistry", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, registry);
        dutyOids.Add(501);
        var apply = type.GetMethod("ApplyRegistryState", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var disabled = new HashSet<uint> { 501, 503, BmrReflectionService.QueenLunatenderOid };
        apply.Invoke(service, [context, disabled]);
        Assert.False(registry.ContainsKey(501u));
        Assert.True(registry.ContainsKey(502u));
        Assert.True(selected.Disposed);
        Assert.False(retained.Disposed);
        Assert.Same(retained, Assert.Single(pending.Cast<Module>()));
        service.SetDutyPolicy(Context(), OwnershipMode.OwnedStartInside, true);
        if (excluded) service.SetDutyPolicy(Context(territory: 900, dutyTerritory: 900), OwnershipMode.OwnedStartInside, true);
        else service.ReleaseDutyPolicy();
        Assert.Equal(0u, type.GetField("dutyPolicyCfc", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service));
        var cleanup = type.GetMethod("RestoreOwnedDutyRegistry", BindingFlags.Instance | BindingFlags.NonPublic)!;
        cleanup.Invoke(service, [context]);
        cleanup.Invoke(service, [context]);
        Assert.True(registry.ContainsKey(501u));
        Assert.False(registry.ContainsKey(503u));
        Assert.False(registry.ContainsKey(BmrReflectionService.QueenLunatenderOid));
        Assert.Equal([501u], manager.Recreated);

        // Dispose must not restore an old provider's Info into a replacement registry.
        type.GetField("trackedRegistry", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, registry);
        dutyOids.Add(501);
        apply.Invoke(service, [context, disabled]);
        var replacementInfo = new Info("CFC", 99);
        var replacement = new Hashtable { [502u] = replacementInfo };
        var replacementContext = Activator.CreateInstance(contextType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, [null, new object(), manager, replacement, new object(), new ArrayList(), new ArrayList(), null], null)!;
        type.GetMethod("RestoreOwnedDutyRegistry", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, [replacementContext]);
        Assert.Same(replacementInfo, replacement[502u]);
        Assert.False(replacement.ContainsKey(501u));
        Assert.Empty(dutyOids);
        Assert.Equal([501u], manager.Recreated);
    }

    private sealed class Info(string groupType, uint groupId)
    {
        public string GroupType = groupType;
        public uint GroupID = groupId;
    }
    public class EmptyPluginsProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args)
            => method?.Name == "get_InstalledPlugins" ? Array.Empty<IExposedPlugin>() : null;
    }
    private sealed class Actor(uint oid) { public uint OID = oid; }
    private sealed class Module(uint oid) : IDisposable
    {
        public Actor PrimaryActor = new(oid);
        public bool Disposed;
        public void Dispose() => Disposed = true;
    }
    private sealed class World { public List<Actor> Actors = []; }
    private sealed class Manager
    {
        public World WorldState = new();
        public List<uint> Recreated = [];
        private void ActorAdded(Actor actor) => Recreated.Add(actor.OID);
    }
    private static IPluginLog Log() => DispatchProxy.Create<IPluginLog, AdsRulePrecedenceTests.NoOpProxy>();
    private static string Policy(bool disabled) => DutyBmrPolicyService.Serialize(new()
    {
        Duties = [new() { ContentFinderConditionId = 11, TerritoryTypeId = 22, DutyEnglishName = "Fixture solo", DisableBmr = disabled }],
    });
    private static DutyCatalogEntry Duty(DutyCategory category = DutyCategory.Solo, uint territory = 22) => new()
    {
        ContentFinderConditionId = 11, TerritoryTypeId = territory, LinkedQuestId = 33,
        Name = "Fixture solo", EnglishName = "Fixture solo", ContentTypeName = "Quest Battle", ExpansionName = "ARR",
        SupportNote = string.Empty, LevelRequired = 1, SortKey = 1, ExVersion = 0, ContentTypeRowId = 16,
        ContentMemberTypeRowId = 1, PartySize = 1, Category = category,
        SupportLevel = DutySupportLevel.PassiveOnly, ClearanceStatus = DutyClearanceStatus.NotCleared,
        IsPlannedTest = false, IsMainScenario = false,
    };
    private static DutyContextSnapshot Context(bool transition = false, DutyCategory category = DutyCategory.Solo, uint territory = 22, uint dutyTerritory = 22) => new()
    {
        PluginEnabled = true, IsLoggedIn = true, BoundByDuty = true, BoundByDuty56 = false,
        BetweenAreas = transition, BetweenAreas51 = false, Jumping = false, Jumping61 = false,
        Occupied33 = false, OccupiedInQuestEvent = false, OccupiedInEvent = false,
        OccupiedInCutSceneEvent = false, WatchingCutscene = false, InCombat = false, Mounted = false,
        TerritoryTypeId = territory, MapId = 1, ContentFinderConditionId = 11, CurrentDuty = Duty(category, dutyTerritory),
    };
}
