using System.Reflection;
using System.Text.Json;
using ADS;
using ADS.Models;
using ADS.Services;
using Dalamud.Game.Command;
using Dalamud.Plugin.Services;

namespace ADS.Tests;

public sealed class BossModMultiboxFollowServiceTests
{
    [Fact]
    public void MissingRegularDutyFollowSettingDefaultsToEnabled()
    {
        var configuration = JsonSerializer.Deserialize<Configuration>("{}")!;

        Assert.True(configuration.EnableBmraiVbmInRegularDuties);
    }

    [Fact]
    public void RegularDutyCleanupSendsSlot1OnceAndBlocksFollowChanges()
    {
        var commandManager = DispatchProxy.Create<ICommandManager, CommandManagerProxy>();
        var commands = ((CommandManagerProxy)(object)commandManager).Commands;
        var log = DispatchProxy.Create<IPluginLog, NoOpProxy>();
        var service = new BossModMultiboxFollowService(null!, commandManager, new Configuration(), log);

        Assert.True(service.EnterRegularDuty("first stable normal-duty tick"));

        Assert.Equal(["/bmrai follow Slot1", "/vbmai follow Slot1"], commands);
        Assert.Equal(1, commands.Count(command => command == "/bmrai follow Slot1"));
        Assert.Equal(1, commands.Count(command => command == "/vbmai follow Slot1"));

        var firstTickCommandCount = commands.Count;
        Assert.False(service.EnterRegularDuty("later normal-duty tick"));
        Assert.False(service.ApplyDirectTreasurePortalOpener(CreatePortalOpener()));
        Assert.False(service.ReapplyDirectTreasurePortalOpenerIfNeeded(
            CreatePortalOpener(),
            CreateRegularDutyContext(),
            "later normal-duty tick"));

        Assert.Equal(firstTickCommandCount, commands.Count);
        Assert.Equal(1, commands.Count(command => command == "/bmrai follow Slot1"));
        Assert.Equal(1, commands.Count(command => command == "/vbmai follow Slot1"));
    }

    [Fact]
    public void DisabledRegularDutyFollowSendsNoSlot1Commands()
    {
        var commandManager = DispatchProxy.Create<ICommandManager, CommandManagerProxy>();
        var commands = ((CommandManagerProxy)(object)commandManager).Commands;
        var log = DispatchProxy.Create<IPluginLog, NoOpProxy>();
        var configuration = new Configuration
        {
            EnableBmraiVbmInRegularDuties = false,
        };
        var service = new BossModMultiboxFollowService(null!, commandManager, configuration, log);

        Assert.True(service.EnterRegularDuty("first stable normal-duty tick"));

        Assert.DoesNotContain("/bmrai follow Slot1", commands);
        Assert.DoesNotContain("/vbmai follow Slot1", commands);
        Assert.Empty(commands);
    }

    [Fact]
    public void DisabledRegularDutyFollowStillShutsDownPendingTreasureFollow()
    {
        var commandManager = DispatchProxy.Create<ICommandManager, CommandManagerProxy>();
        var commands = ((CommandManagerProxy)(object)commandManager).Commands;
        var log = DispatchProxy.Create<IPluginLog, NoOpProxy>();
        var configuration = new Configuration
        {
            EnableBmraiVbmInRegularDuties = false,
        };
        var service = new BossModMultiboxFollowService(null!, commandManager, configuration, log);
        SetTreasureFollowActivated(service);

        Assert.True(service.EnterRegularDuty("first stable normal-duty tick"));

        Assert.Contains("/bmrai followoutofcombat off", commands);
        Assert.Contains("/bmrai followcombat off", commands);
        Assert.Contains("/vbmai followoutofcombat off", commands);
        Assert.Contains("/vbmai followcombat off", commands);
        Assert.Contains("/bmrai off", commands);
        Assert.DoesNotContain("/bmrai follow Slot1", commands);
        Assert.DoesNotContain("/vbmai follow Slot1", commands);
    }

    [Fact]
    public void RegularDutyCleanupLatchResetsAfterLeavingDuty()
    {
        var commandManager = DispatchProxy.Create<ICommandManager, CommandManagerProxy>();
        var commands = ((CommandManagerProxy)(object)commandManager).Commands;
        var log = DispatchProxy.Create<IPluginLog, NoOpProxy>();
        var service = new BossModMultiboxFollowService(null!, commandManager, new Configuration(), log);

        Assert.True(service.EnterRegularDuty("first duty"));
        service.LeaveRegularDuty("outside duty");
        Assert.True(service.EnterRegularDuty("second duty"));

        Assert.Equal(2, commands.Count(command => command == "/bmrai follow Slot1"));
        Assert.Equal(2, commands.Count(command => command == "/vbmai follow Slot1"));
    }

    [Fact]
    public void RegularDutyCleanupDisablesAdsEnabledFollowModes()
    {
        var commandManager = DispatchProxy.Create<ICommandManager, CommandManagerProxy>();
        var commands = ((CommandManagerProxy)(object)commandManager).Commands;
        var log = DispatchProxy.Create<IPluginLog, NoOpProxy>();
        var service = new BossModMultiboxFollowService(null!, commandManager, new Configuration(), log);
        SetTreasureFollowActivated(service);

        Assert.True(service.EnterRegularDuty("first stable normal-duty tick"));

        Assert.Contains("/bmrai followoutofcombat off", commands);
        Assert.Contains("/bmrai followcombat off", commands);
        Assert.Contains("/vbmai followoutofcombat off", commands);
        Assert.Contains("/vbmai followcombat off", commands);
        Assert.Contains("/bmrai follow Slot1", commands);
        Assert.Contains("/vbmai follow Slot1", commands);
    }

    [Fact]
    public void NonRegularCleanupRetainsExistingVbmSlot1Reset()
    {
        var commandManager = DispatchProxy.Create<ICommandManager, CommandManagerProxy>();
        var commands = ((CommandManagerProxy)(object)commandManager).Commands;
        var log = DispatchProxy.Create<IPluginLog, NoOpProxy>();
        var service = new BossModMultiboxFollowService(null!, commandManager, new Configuration(), log);
        SetTreasureFollowActivated(service);

        service.Clear("treasure cleanup");

        Assert.Contains("/bmrai followoutofcombat off", commands);
        Assert.Contains("/bmrai followcombat off", commands);
        Assert.Contains("/vbmai followoutofcombat off", commands);
        Assert.Contains("/vbmai followcombat off", commands);
        Assert.DoesNotContain("/bmrai follow Slot1", commands);
        Assert.Contains("/vbmai follow Slot1", commands);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DirectPortalOpenerFollowRemainsActiveOutsideRegularDuty(bool inDuty)
    {
        var commandManager = DispatchProxy.Create<ICommandManager, CommandManagerProxy>();
        var commands = ((CommandManagerProxy)(object)commandManager).Commands;
        var log = DispatchProxy.Create<IPluginLog, NoOpProxy>();
        var configuration = new Configuration
        {
            BmraiTreasureFollowCleanupPending = true,
        };
        var service = new BossModMultiboxFollowService(null!, commandManager, configuration, log);

        Assert.True(service.ApplyDirectTreasurePortalOpener(
            CreatePortalOpener(),
            inDuty ? CreateRegularDutyContext() : null));

        Assert.Contains("/bmrai follow Portal Opener", commands);
        Assert.Contains("/vbmai follow Portal Opener", commands);
    }

    [Fact]
    public void TreasureFollowerDutyExitCleanupRemainsUnchanged()
    {
        var commandManager = DispatchProxy.Create<ICommandManager, CommandManagerProxy>();
        var commands = ((CommandManagerProxy)(object)commandManager).Commands;
        var log = DispatchProxy.Create<IPluginLog, NoOpProxy>();
        var service = new TreasureFollowerDutyExitMonitorService(commandManager, log);
        service.Arm(CreateRegularDutyContext(), "test");
        typeof(TreasureFollowerDutyExitMonitorService)
            .GetField("outsideStableSinceUtc", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, DateTime.UtcNow.AddSeconds(-2));

        service.Update(
            CreateOutsideContext(),
            supportedTreasureDuty: false,
            TreasureDungeonRole.Regular,
            "Regular");

        Assert.Equal(
            [
                "/bmrai followoutofcombat off",
                "/cbt disable AutoFollow",
                "/bmrai followcombat off",
                "/vbmai follow Slot1",
            ],
            commands);
    }

    private static void SetTreasureFollowActivated(BossModMultiboxFollowService service)
        => typeof(BossModMultiboxFollowService)
            .GetField("bmraiFollowActivated", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, true);

    private static TreasurePortalOpenerSnapshot CreatePortalOpener()
        => new(
            "Portal Opener",
            "Local Player",
            2,
            10,
            20,
            30,
            IsLocalOpener: false,
            Source: "PortalChat",
            ChatText: "Portal Opener places a hand on the portal.",
            CapturedUtc: DateTime.UtcNow);

    private static DutyContextSnapshot CreateRegularDutyContext()
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
            TerritoryTypeId = 200,
            MapId = 0,
            ContentFinderConditionId = 100,
            CurrentDuty = null,
        };

    private static DutyContextSnapshot CreateOutsideContext()
        => new()
        {
            PluginEnabled = true,
            IsLoggedIn = true,
            BoundByDuty = false,
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
            TerritoryTypeId = 0,
            MapId = 0,
            ContentFinderConditionId = 0,
            CurrentDuty = null,
        };

    public class CommandManagerProxy : DispatchProxy
    {
        public List<string> Commands { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(ICommandManager.ProcessCommand)
                && args is [string command, ..])
            {
                Commands.Add(command);
                return true;
            }

            return DefaultValue(targetMethod?.ReturnType);
        }
    }

    public class NoOpProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => DefaultValue(targetMethod?.ReturnType);
    }

    private static object? DefaultValue(Type? type)
        => type is null || type == typeof(void) || !type.IsValueType
            ? null
            : Activator.CreateInstance(type);
}
