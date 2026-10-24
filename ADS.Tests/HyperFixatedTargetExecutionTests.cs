using System.Collections;
using System.Numerics;
using System.Reflection;
using ADS.Models;
using ADS.Services;
using Dalamud.Game.Command;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;

namespace ADS.Tests;

public sealed class HyperFixatedTargetExecutionTests
{
    [Fact]
    public void DragonsNeckReturnsToCenterImmediatelyThenEveryEightSecondsWithoutCatchUp()
    {
        using var fixture = new Fixture(Vector3.Zero);
        fixture.Execution.ReadUtcNow = () => fixture.Now;
        var context = TestDutyContextFactory.Create(DutyCategory.EightMan,
            territoryId: 142, cfcId: 81, inCombat: true);
        const string move = "/vnav moveto -258.08 19.07 18.11";

        fixture.Update(IdlePlanner(), context);
        Assert.Equal(new[] { "/bmrai off", "/vbmai off", move }, fixture.Commands);
        Assert.Equal(ExecutionPhase.CombatHold, fixture.Execution.CurrentPhase);

        fixture.Now = fixture.Now.AddMilliseconds(7999);
        fixture.Update(IdlePlanner(), context);
        Assert.Single(fixture.Commands, command => command == move);
        Assert.DoesNotContain("/vnav stop", fixture.Commands);

        fixture.Now = fixture.Now.AddMilliseconds(1);
        fixture.Update(IdlePlanner(), context);
        Assert.Equal(2, fixture.Commands.Count(command => command == move));
        fixture.Now = fixture.Now.AddSeconds(25);
        fixture.Update(IdlePlanner(), context);
        Assert.Equal(3, fixture.Commands.Count(command => command == move));
    }

    [Theory]
    [InlineData("combat ended")]
    [InlineData("stop")]
    [InlineData("disable")]
    [InlineData("cutscene")]
    [InlineData("departure")]
    [InlineData("completion")]
    [InlineData("dispose")]
    public void DragonsNeckReleasesOnlyItsOwnedPathAtLifecycleBoundaries(string boundary)
    {
        using var fixture = new Fixture(Vector3.Zero);
        fixture.Execution.ReadUtcNow = () => fixture.Now;
        var context = TestDutyContextFactory.Create(DutyCategory.EightMan,
            territoryId: 142, cfcId: 81, inCombat: true);
        fixture.Update(IdlePlanner(), context);
        fixture.Now = fixture.Now.AddSeconds(1);

        switch (boundary)
        {
            case "stop": fixture.Execution.Stop(context); break;
            case "completion": fixture.Execution.CompleteDuty("Test Duty"); break;
            case "dispose": fixture.Execution.ReleaseHeldMovementKeys("plugin dispose"); break;
            case "cutscene":
                Assert.True(fixture.Execution.RefreshCutsceneHold(TestDutyContextFactory.Create(DutyCategory.EightMan,
                    territoryId: 142, cfcId: 81, inCombat: true, cutscene: true), pluginEnabled: true));
                break;
            default:
                fixture.Update(IdlePlanner(boundary == "departure" ? PlannerMode.UnsafeTransition : PlannerMode.Progression),
                    TestDutyContextFactory.Create(DutyCategory.EightMan,
                    territoryId: boundary == "departure" ? 777u : 142u, cfcId: 81,
                    inCombat: boundary != "combat ended", cutscene: boundary == "cutscene"),
                    pluginEnabled: boundary != "disable");
                break;
        }

        Assert.Single(fixture.Commands, command => command == "/vnav stop");
        Assert.Single(fixture.Commands, command => command.StartsWith("/vnav moveto", StringComparison.Ordinal));
        fixture.Execution.ReleaseHeldMovementKeys("repeat cleanup");
        Assert.Single(fixture.Commands, command => command == "/vnav stop");
        if (boundary is "combat ended" or "cutscene" or "dispose")
        {
            fixture.Update(IdlePlanner(), context);
            Assert.Equal(2, fixture.Commands.Count(command => command.StartsWith("/vnav moveto", StringComparison.Ordinal)));
        }
    }

    [Theory]
    [InlineData(142u, 80u)]
    [InlineData(777u, 81u)]
    public void DragonsNeckDoesNotControlAnotherDuty(uint territory, uint cfc)
    {
        using var fixture = new Fixture(Vector3.Zero);
        fixture.Update(IdlePlanner(PlannerMode.UnsafeTransition), TestDutyContextFactory.Create(DutyCategory.EightMan,
            territoryId: territory, cfcId: cfc, inCombat: true));
        Assert.Empty(fixture.Commands);
    }

    [Fact]
    public void DragonsNeckDoesNotControlAnUnownedBattle()
    {
        using var fixture = new Fixture(Vector3.Zero);
        var context = TestDutyContextFactory.Create(DutyCategory.EightMan,
            territoryId: 142, cfcId: 81, inCombat: true);
        fixture.Execution.Stop(context);
        fixture.Update(IdlePlanner(), context);
        Assert.Empty(fixture.Commands);
    }

    [Fact]
    public void DragonsNeckFailedMovementWaitsForTheNextEightSecondAttempt()
    {
        using var fixture = new Fixture(Vector3.Zero, commandsAccepted: false);
        fixture.Execution.ReadUtcNow = () => fixture.Now;
        var context = TestDutyContextFactory.Create(DutyCategory.EightMan,
            territoryId: 142, cfcId: 81, inCombat: true);
        fixture.Update(IdlePlanner(), context);
        fixture.Now = fixture.Now.AddSeconds(1);
        fixture.Update(IdlePlanner(), context);
        Assert.Single(fixture.Commands, command => command.StartsWith("/vnav moveto", StringComparison.Ordinal));
    }

    [Fact]
    public void LeasePrecedesExactTargetAndMovementThenChainsWithoutRelease()
    {
        using var fixture = new Fixture(Vector3.Zero);
        var first = fixture.AddMonster(21, "Same Name", new Vector3(8f, 0f, 0f));
        var second = fixture.AddMonster(22, "Same Name", new Vector3(6f, 0f, 0f));

        fixture.Update(HyperPlanner(first));

        Assert.Equal(ExecutionPhase.NavigatingToMonsterObjective, fixture.Execution.CurrentPhase);
        Assert.Same(first.GameObject, fixture.Target);
        Assert.True(fixture.Events.IndexOf("acquire") < fixture.Events.IndexOf("target:21"));
        Assert.True(fixture.Events.IndexOf("target:21") < fixture.Events.FindIndex(x => x.StartsWith("command:/vnav moveto", StringComparison.Ordinal)));

        fixture.Update(HyperPlanner(second));

        Assert.Same(second.GameObject, fixture.Target);
        Assert.Equal(1, fixture.AcquireCalls);
        Assert.Equal(0, fixture.ReleaseCalls);
    }

    [Fact]
    public void ExactTargetIsRetainedAtTwoYardsThenReleasedWhenNoHyperTargetRemains()
    {
        using var fixture = new Fixture(Vector3.Zero);
        var target = fixture.AddMonster(31, "Focus", new Vector3(2f, 0f, 0f));

        fixture.Update(HyperPlanner(target));

        Assert.Equal(ExecutionPhase.ReadyForMonsterObjective, fixture.Execution.CurrentPhase);
        Assert.Same(target.GameObject, fixture.Target);
        Assert.DoesNotContain(fixture.Commands, x => x.StartsWith("/vnav moveto", StringComparison.Ordinal));

        fixture.Update(IdlePlanner());

        Assert.Null(fixture.Target);
        Assert.Equal(1, fixture.ReleaseCalls);
    }

    [Fact]
    public void LostHeartbeatStopsMovementClearsTargetAndReportsFailure()
    {
        using var fixture = new Fixture(Vector3.Zero) { HeartbeatOk = false };
        var target = fixture.AddMonster(41, "Focus", new Vector3(8f, 0f, 0f));
        fixture.Update(HyperPlanner(target));
        fixture.Now = fixture.Now.AddSeconds(3);

        fixture.Update(HyperPlanner(target));

        Assert.Equal(ExecutionPhase.Failure, fixture.Execution.CurrentPhase);
        Assert.Contains("heartbeat lost", fixture.Execution.LastStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Null(fixture.Target);
        Assert.Contains("/vnav stop", fixture.Commands);
        Assert.Equal(1, fixture.HeartbeatCalls);
    }

    internal static PlannerSnapshot HyperPlanner(TargetFixture target)
        => new()
        {
            Mode = PlannerMode.Progression,
            ObjectiveKind = PlannerObjectiveKind.HyperFixatedAttackTarget,
            Objective = $"Hyper-focus {target.Observed.Name}",
            Explanation = "test",
            TargetName = target.Observed.Name,
            TargetGameObjectId = target.Observed.GameObjectId,
            TargetDistance = target.Observed.Position.Length(),
            CapturedAtUtc = DateTime.UtcNow,
        };

    private static PlannerSnapshot IdlePlanner(PlannerMode mode = PlannerMode.Progression)
        => new()
        {
            Mode = mode,
            ObjectiveKind = PlannerObjectiveKind.None,
            Objective = "No target",
            Explanation = "test",
            CapturedAtUtc = DateTime.UtcNow,
        };

    internal sealed class Fixture : IDisposable
    {
        private readonly TempDirectory tempDirectory = new();
        private readonly ObjectTableProxy objectTableProxy;
        private readonly TargetManagerProxy targetManagerProxy;
        private readonly List<TargetFixture> targets = [];

        public Fixture(Vector3 playerPosition, bool includeObservationMemory = false, ObjectPriorityRuleManifest? initialRules = null,
            bool commandsAccepted = true)
        {
            var log = DispatchProxy.Create<IPluginLog, NoOpProxy>();
            var objectTable = DispatchProxy.Create<IObjectTable, ObjectTableProxy>();
            objectTableProxy = (ObjectTableProxy)(object)objectTable;
            objectTableProxy.LocalPlayer = CreatePlayer(playerPosition);
            objectTableProxy.Objects = targets;

            var targetManager = DispatchProxy.Create<ITargetManager, TargetManagerProxy>();
            targetManagerProxy = (TargetManagerProxy)(object)targetManager;
            targetManagerProxy.Events = Events;

            var commandManager = DispatchProxy.Create<ICommandManager, CommandManagerProxy>();
            ((CommandManagerProxy)(object)commandManager).Commands = Commands;
            ((CommandManagerProxy)(object)commandManager).Events = Events;
            ((CommandManagerProxy)(object)commandManager).CommandsAccepted = commandsAccepted;
            var keyState = DispatchProxy.Create<IKeyState, NoOpProxy>();
            if (initialRules is not null)
                File.WriteAllText(Path.Combine(tempDirectory.Path, ObjectRuleShardStore.LegacyFileName),
                    System.Text.Json.JsonSerializer.Serialize(initialRules));
            var ruleService = new ObjectPriorityRuleService(log, null!, tempDirectory.Path);
            Memory = includeObservationMemory
                ? new ObservationMemoryService(objectTable,
                    DispatchProxy.Create<IPartyList, TreasureCofferObservationPolicyTests.PartyListProxy>(), log, ruleService)
                : null;
            Frontier = new DungeonFrontierService(null!, objectTable, log, ruleService, Memory!);
            var lease = new HyperFocusLeaseService(
                _ =>
                {
                    AcquireCalls++;
                    Events.Add("acquire");
                    return Response(true, "acquired");
                },
                _ =>
                {
                    HeartbeatCalls++;
                    Events.Add("heartbeat");
                    return Response(HeartbeatOk, HeartbeatOk ? "heartbeat" : "heartbeat lost");
                },
                _ =>
                {
                    ReleaseCalls++;
                    Events.Add("release");
                    return Response(true, "released");
                },
                () => "{\"contractVersion\":1}",
                () => Now);

            Execution = new ExecutionService(
                null!,
                objectTable,
                targetManager,
                commandManager,
                Memory!,
                Frontier,
                null!,
                ruleService,
                lease,
                new TreasureDoorStrafeInputService(keyState, log),
                new CardinalHoldInputService(keyState, log),
                Configuration,
                log);
            SetPrivateSetProperty(Execution, nameof(ExecutionService.CurrentMode), OwnershipMode.OwnedStartInside);
        }

        public ExecutionService Execution { get; }
        public Configuration Configuration { get; } = new();
        public DungeonFrontierService Frontier { get; }
        public ObservationMemoryService? Memory { get; }
        public DateTime Now { get; set; } = new(2026, 8, 28, 12, 0, 0, DateTimeKind.Utc);
        public bool HeartbeatOk { get; set; } = true;
        public int AcquireCalls { get; private set; }
        public int HeartbeatCalls { get; private set; }
        public int ReleaseCalls { get; private set; }
        public List<string> Events { get; } = [];
        public List<string> Commands { get; } = [];
        public IGameObject? Target => targetManagerProxy.Target;

        public TargetFixture AddMonster(ulong gameObjectId, string name, Vector3 position)
        {
            var gameObject = DispatchProxy.Create<IGameObject, GameObjectProxy>();
            var proxy = (GameObjectProxy)(object)gameObject;
            proxy.GameObjectId = gameObjectId;
            proxy.Position = position;
            proxy.IsTargetable = true;
            var target = new TargetFixture(
                gameObject,
                new ObservedMonster
                {
                    Key = $"{name}:{gameObjectId}",
                    GameObjectId = gameObjectId,
                    DataId = 100,
                    MapId = 1,
                    Name = name,
                    Position = position,
                    LastSeenUtc = DateTime.UtcNow,
                });
            targets.Add(target);
            return target;
        }

        public void Update(PlannerSnapshot planner, DutyContextSnapshot? context = null, bool pluginEnabled = true)
        {
            Execution.Update(
                context ?? TestDutyContextFactory.Create(DutyCategory.Solo),
                planner,
                new ObservationSnapshot
                {
                    LiveMonsters = targets.Select(x => x.Observed).ToList(),
                    LiveFollowTargets = [],
                    MonsterGhosts = [],
                    LiveInteractables = [],
                    InteractableGhosts = [],
                },
                pluginEnabled: pluginEnabled,
                considerTreasureCoffers: false,
                dialogAutomationStatus: string.Empty);
        }

        public void Dispose() => tempDirectory.Dispose();

        private static IPlayerCharacter CreatePlayer(Vector3 position)
        {
            var player = DispatchProxy.Create<IPlayerCharacter, GameObjectProxy>();
            ((GameObjectProxy)(object)player).Position = position;
            return player;
        }

        private static void SetPrivateSetProperty<T>(object target, string propertyName, T value)
            => target.GetType()
                .GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
                .SetValue(target, value);

        private static string Response(bool ok, string reason)
            => $"{{\"ok\":{ok.ToString().ToLowerInvariant()},\"reason\":\"{reason}\",\"contractVersion\":1}}";
    }

    public sealed record TargetFixture(IGameObject GameObject, ObservedMonster Observed);

    public class ObjectTableProxy : DispatchProxy
    {
        public IPlayerCharacter? LocalPlayer { get; set; }
        public List<TargetFixture> Objects { get; set; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod?.Name switch
            {
                "get_LocalPlayer" => LocalPlayer,
                "GetEnumerator" => Objects.Select(x => x.GameObject).GetEnumerator(),
                _ => DefaultValue(targetMethod),
            };
    }

    public class GameObjectProxy : DispatchProxy
    {
        public ulong GameObjectId { get; set; }
        public Vector3 Position { get; set; }
        public bool IsTargetable { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod?.Name switch
            {
                "get_GameObjectId" => GameObjectId,
                "get_Position" => Position,
                "get_IsTargetable" => IsTargetable,
                _ => DefaultValue(targetMethod),
            };
    }

    public class TargetManagerProxy : DispatchProxy
    {
        public IGameObject? Target { get; private set; }
        public List<string> Events { get; set; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "get_Target")
                return Target;
            if (targetMethod?.Name == "set_Target" && args is { Length: 1 })
            {
                var target = args[0] as IGameObject;
                Target = target;
                Events.Add(target is null ? "target:clear" : $"target:{target.GameObjectId}");
                return null;
            }

            return DefaultValue(targetMethod);
        }
    }

    public class CommandManagerProxy : DispatchProxy
    {
        public bool CommandsAccepted { get; set; } = true;
        public List<string> Commands { get; set; } = [];
        public List<string> Events { get; set; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "get_Commands")
                return new System.Collections.ObjectModel.ReadOnlyDictionary<string, IReadOnlyCommandInfo>(
                    new Dictionary<string, IReadOnlyCommandInfo>
                {
                    ["/bmrai"] = new CommandInfo((_, _) => { }),
                    ["/vbmai"] = new CommandInfo((_, _) => { }),
                });
            if (targetMethod?.Name == "ProcessCommand" && args is [string command])
            {
                Commands.Add(command);
                Events.Add($"command:{command}");
                return CommandsAccepted;
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
