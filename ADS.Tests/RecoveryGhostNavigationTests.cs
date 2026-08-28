using System.Numerics;
using System.Reflection;
using ADS.Models;
using ADS.Services;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;

namespace ADS.Tests;

public sealed class RecoveryGhostNavigationTests
{
    [Fact]
    public void StationaryTargetTimesOutWhileMovementOrTargetChangeRestartsWindow()
    {
        using var tempDirectory = new TempDirectory();
        var log = DispatchProxy.Create<IPluginLog, ForceMarchLockTests.NoOpProxy>();
        var objectTable = DispatchProxy.Create<IObjectTable, ForceMarchLockTests.ObjectTableProxy>();
        var objectTableProxy = (ForceMarchLockTests.ObjectTableProxy)(object)objectTable;
        var player = DispatchProxy.Create<IPlayerCharacter, ForceMarchLockTests.GameObjectProxy>();
        var playerProxy = (ForceMarchLockTests.GameObjectProxy)(object)player;
        playerProxy.Position = Vector3.Zero;
        objectTableProxy.LocalPlayer = player;

        var partyList = DispatchProxy.Create<IPartyList, TreasureCofferObservationPolicyTests.PartyListProxy>();
        var ruleService = new ObjectPriorityRuleService(log, null!, tempDirectory.Path);
        var observationMemory = new ObservationMemoryService(objectTable, partyList, log, ruleService);
        var frontier = new DungeonFrontierService(null!, objectTable, log, ruleService, null!);
        var commands = new List<string>();
        var commandManager = DispatchProxy.Create<ICommandManager, ForceMarchLockTests.CommandManagerProxy>();
        ((ForceMarchLockTests.CommandManagerProxy)(object)commandManager).Commands = commands;
        var keyState = DispatchProxy.Create<IKeyState, ForceMarchLockTests.NoOpProxy>();
        var execution = new ExecutionService(
            null!,
            objectTable,
            null!,
            commandManager,
            observationMemory,
            frontier,
            null!,
            ruleService,
            new TreasureDoorStrafeInputService(keyState, log),
            new CardinalHoldInputService(keyState, log),
            new Configuration(),
            log);

        var ghostA = MonsterGhost("ghost-a", "Ghost A", new Vector3(40f, 0f, 0f));
        var ghostB = MonsterGhost("ghost-b", "Ghost B", new Vector3(80f, 0f, 0f));
        var knownMonsters = GetPrivateField<Dictionary<string, ObservedMonster>>(observationMemory, "knownMonsters");
        knownMonsters[ghostA.Key] = ghostA;
        knownMonsters[ghostB.Key] = ghostB;
        var observation = Snapshot(ghostA, ghostB);

        AdvanceRecovery(execution, Planner(ghostA), observation);
        AgeProgressWindow(execution);

        playerProxy.Position = new Vector3(0.5f, 0f, 0f);
        AdvanceRecovery(execution, Planner(ghostA), observation);

        Assert.Contains(ghostA.Key, knownMonsters.Keys);
        Assert.True(GetPrivateField<DateTime>(execution, "recoveryNavigationLastProgressUtc") > DateTime.UtcNow.AddSeconds(-2));

        AgeProgressWindow(execution);
        AdvanceRecovery(execution, Planner(ghostB), observation);

        Assert.Equal(ghostB.Key, GetPrivateField<string?>(execution, "recoveryNavigationTargetKey"));
        Assert.Contains(ghostA.Key, knownMonsters.Keys);
        Assert.Contains(ghostB.Key, knownMonsters.Keys);

        AgeProgressWindow(execution);
        AdvanceRecovery(execution, Planner(ghostB), observation);

        Assert.Equal("/vnav stop", commands[^1]);
        Assert.Contains(ghostA.Key, knownMonsters.Keys);
        Assert.DoesNotContain(ghostB.Key, knownMonsters.Keys);
        Assert.Equal(ExecutionPhase.RecoveryHint, execution.CurrentPhase);
        Assert.Contains("retired the selected ghost", execution.LastStatus, StringComparison.Ordinal);
    }

    private static ObservedMonster MonsterGhost(string key, string name, Vector3 position)
        => new()
        {
            Key = key,
            GameObjectId = 0,
            DataId = 100,
            MapId = 1,
            Name = name,
            Position = position,
            LastSeenUtc = DateTime.UtcNow,
        };

    private static PlannerSnapshot Planner(ObservedMonster ghost)
        => new()
        {
            Mode = PlannerMode.Recovery,
            ObjectiveKind = PlannerObjectiveKind.MonsterGhost,
            Objective = $"Advance toward monster ghost: {ghost.Name}",
            Explanation = "test recovery",
            TargetName = ghost.Name,
            TargetDistance = ghost.Position.X,
            CapturedAtUtc = DateTime.UtcNow,
        };

    private static ObservationSnapshot Snapshot(params ObservedMonster[] ghosts)
        => new()
        {
            LiveMonsters = [],
            LiveFollowTargets = [],
            MonsterGhosts = ghosts,
            LiveInteractables = [],
            InteractableGhosts = [],
        };

    private static void AdvanceRecovery(ExecutionService execution, PlannerSnapshot planner, ObservationSnapshot observation)
        => typeof(ExecutionService)
            .GetMethod("TryAdvanceRecoveryObjective", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(execution, [planner, observation, "test"]);

    private static void AgeProgressWindow(ExecutionService execution)
        => SetPrivateField(execution, "recoveryNavigationLastProgressUtc", DateTime.UtcNow.AddSeconds(-13));

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
}
