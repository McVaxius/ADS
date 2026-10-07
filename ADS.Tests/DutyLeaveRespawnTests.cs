using System.Reflection;
using System.Numerics;
using ADS.Models;
using ADS.Services;
using Dalamud.Game;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Text.Evaluator;
using Dalamud.Plugin.Services;
using Lumina.Text;
using Lumina.Text.ReadOnly;

namespace ADS.Tests;

public sealed class DutyLeaveRespawnTests
{
    [Theory]
    [InlineData("ongoing", false)]
    [InlineData("ongoing", true)]
    [InlineData("stop", false)]
    [InlineData("stop", true)]
    [InlineData("completed", false)]
    [InlineData("completed", true)]
    public void InDutyReturnPreservesOnlyTheStillRequestedUnfinishedOwnershipThroughLoading(
        string boundary, bool betweenAreas51)
    {
        using var tempDirectory = new TempDirectory();
        var execution = CreateExecution(tempDirectory.Path, out var objects);
        var player = DispatchProxy.Create<IPlayerCharacter, DutyReturnPlayerProxy>();
        var playerState = (DutyReturnPlayerProxy)(object)player;
        objects.LocalPlayer = player;
        var context = Context();
        var planner = new PlannerSnapshot
        {
            Mode = PlannerMode.Recovery, ObjectiveKind = PlannerObjectiveKind.None,
            Objective = "Waiting for recovery truth", Explanation = "Return recovery regression",
            CapturedAtUtc = DateTime.UtcNow,
        };
        Assert.True(execution.StartDutyFromInside(context));
        var originalMode = execution.CurrentMode;
        playerState.Dead = true;
        Tick(context);
        Assert.Equal(originalMode, execution.CurrentMode);

        // FrenRider owns Return. ADS retains this existing run through loading
        // rather than receiving another Start/Resume or running a second revive flow.
        var loading = Context(inDuty: false, transition: !betweenAreas51,
            transition51: betweenAreas51, territory: 0, content: 0);
        execution.ObserveDutyCompletion(loading);
        Tick(loading);
        Assert.Equal(originalMode, execution.CurrentMode);
        Assert.Equal(ExecutionPhase.TransitionHold, execution.CurrentPhase);
        if (boundary == "stop")
            execution.Stop(context);
        else if (boundary == "completed")
        {
            execution.MarkDutyCompleted(context, 1044, 831);
            execution.CompleteDuty("Completed duty");
        }
        Tick(loading);
        playerState.Dead = false;
        execution.ObserveDutyCompletion(context);
        Tick(context);
        Tick(context);
        Assert.Equal(boundary == "ongoing" ? originalMode : OwnershipMode.Observing, execution.CurrentMode);
        Assert.Equal(boundary == "ongoing", execution.IsOwned);
        Assert.False(execution.IsLeaveRequested);
        Assert.DoesNotContain(execution.LastStatus, "Resumed ownership", StringComparison.Ordinal);

        void Tick(DutyContextSnapshot frame)
            => execution.Update(frame, planner, ObservationSnapshot.Empty,
                pluginEnabled: true, considerTreasureCoffers: false, dialogAutomationStatus: string.Empty);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LootGoblinOrdinaryTreasureCompletionWaitsForDelayedCofferAndLootWithoutExiting(bool completionBeforeFirstStatusRead)
    {
        using var tempDirectory = new TempDirectory();
        var execution = CreateExecution(tempDirectory.Path, out var objects);
        var context = TreasureContext(occupied: true);
        Assert.True(Plugin.ShouldRunDutyCompletionTreasureSweep(context, true));
        Assert.True(execution.StartDutyFromInside(context, sweepWithoutExit: true));
        if (completionBeforeFirstStatusRead)
            BeginCompletion();
        execution.ObserveDutyCompletion(context); // Pending handoff survives ordinary status observation.
        Assert.Equal((1044u, 831u), execution.CompletionTreasureSweepWithoutExitDuty);
        Assert.False(execution.CompletionTreasureSweepCompleted);
        if (!completionBeforeFirstStatusRead)
            BeginCompletion();

        var now = DateTime.UtcNow;
        var exitRequests = 0;
        void Tick(DateTime time, ObservationSnapshot? observation = null)
            => execution.UpdateLeaveDuty(context, observation ?? ObservationSnapshot.Empty, true, () => ++exitRequests, time);
        Tick(now);
        Assert.True(execution.IsOwned);
        Tick(now.AddMinutes(1)); // First clear observation begins settling after the spawn grace.
        var coffer = new ObservedInteractable
        {
            Key = "delayed-final-coffer", GameObjectId = 1, DataId = 1, MapId = 1,
            ObjectKind = ObjectKind.Treasure, Name = "Treasure Coffer", Position = Vector3.Zero,
            LastSeenUtc = now, Classification = InteractableClass.TreasureCoffer, GhostReason = GhostReason.SeenPreviously,
        };
        objects.Coffer = DispatchProxy.Create<IGameObject, CofferProxy>();
        Tick(now.AddMinutes(1).AddSeconds(1), new ObservationSnapshot
        {
            LiveInteractables = [coffer], LiveMonsters = [], LiveFollowTargets = [], MonsterGhosts = [], InteractableGhosts = [],
        });
        Assert.True(execution.IsOwned);
        Assert.False(execution.CompletionTreasureSweepCompleted);
        Assert.Equal(DateTime.MinValue, Field<DateTime>(execution, "leaveTreasureSweepClearSinceUtc"));
        objects.Coffer = null;
        // Exercise the real interaction-result and distribution branches without issuing a native interact.
        SetField(execution, "leaveTreasureInteractionSent", true);
        SetField(execution, "nextInteractAttemptUtc", now.AddMinutes(2));
        Tick(now.AddMinutes(1).AddSeconds(2));
        Assert.Contains("interaction result", execution.LastStatus);
        Assert.False(execution.CompletionTreasureSweepCompleted);
        Tick(now.AddMinutes(2));
        var clearSince = Field<DateTime>(execution, "leaveTreasureSweepClearSinceUtc");
        BeginCompletion(); // A duplicate event cannot restart the active grace/settlement clocks.
        Assert.Equal(clearSince, Field<DateTime>(execution, "leaveTreasureSweepClearSinceUtc"));
        Tick(now.AddMinutes(2).AddSeconds(3));
        Assert.Contains("loot distribution", execution.LastStatus);
        Assert.True(execution.IsOwned);
        Tick(now.AddMinutes(2).AddSeconds(4));
        Assert.False(execution.CompletionTreasureSweepCompleted);
        Tick(now.AddMinutes(3));
        Assert.Equal(OwnershipMode.Observing, execution.CurrentMode);
        Assert.True(execution.CompletionTreasureSweepCompleted);
        Assert.Equal(0, exitRequests);
        execution.ObserveDutyCompletion(context);
        BeginCompletion(); // Duplicate completion after release retains the matching positive result.
        Assert.True(execution.CompletionTreasureSweepCompleted);
        Assert.False(execution.IsOwned);

        Assert.True(execution.LeaveDuty(context, false));
        Assert.Null(execution.CompletionTreasureSweepWithoutExitDuty);
        execution.UpdateLeaveDuty(context, ObservationSnapshot.Empty, false, () => ++exitRequests);
        Assert.Equal(1, exitRequests);

        void BeginCompletion()
        {
            execution.MarkDutyCompleted(context, 1044, 831);
            Assert.True(execution.BeginDutyCompletionTreasureSweep(context, "Ordinary treasure duty"));
        }
    }

    [Fact]
    public void OrdinaryStandaloneTreasureCompletionStillArmsDutyExit()
    {
        using var tempDirectory = new TempDirectory();
        var execution = CreateExecution(tempDirectory.Path, out _);
        var context = TreasureContext();
        Assert.True(Plugin.ShouldRunDutyCompletionTreasureSweep(context, true));
        Assert.True(execution.StartDutyFromInside(context));
        execution.MarkDutyCompleted(context, 1044, 831);
        Assert.True(execution.BeginDutyCompletionTreasureSweep(context, "Ordinary treasure duty"));
        var now = DateTime.UtcNow.AddMinutes(1);
        var exits = 0;
        execution.UpdateLeaveDuty(context, ObservationSnapshot.Empty, true, () => ++exits, now);
        execution.UpdateLeaveDuty(context, ObservationSnapshot.Empty, true, () => ++exits, now.AddSeconds(3));
        Assert.Equal(1, exits);
        Assert.True(execution.IsOwned);
        Assert.Null(execution.CompletionTreasureSweepWithoutExitDuty);
        Assert.False(execution.CompletionTreasureSweepCompleted);
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("inside")]
    [InlineData("outside")]
    [InlineData("resume")]
    [InlineData("new-duty")]
    [InlineData("departure")]
    [InlineData("logout")]
    [InlineData("territory")]
    [InlineData("content")]
    public void LootGoblinSweepResultClearsAtOwnershipAndDutyBoundaries(string reset)
    {
        using var tempDirectory = new TempDirectory();
        var execution = CreateExecution(tempDirectory.Path, out _);
        var context = Context();
        Assert.True(execution.StartDutyFromInside(context, true));
        Assert.True(execution.BeginDutyCompletionTreasureSweep(context, "Treasure duty"));
        var now = DateTime.UtcNow.AddMinutes(1);
        execution.UpdateLeaveDuty(context, ObservationSnapshot.Empty, true, () => Assert.Fail("Unexpected exit"), now);
        execution.UpdateLeaveDuty(context, ObservationSnapshot.Empty, true, () => Assert.Fail("Unexpected exit"), now.AddSeconds(3));
        Assert.True(execution.CompletionTreasureSweepCompleted);
        execution.ObserveDutyCompletion(Context(inDuty: false, transition: true, territory: 0, content: 0));
        Assert.True(execution.CompletionTreasureSweepCompleted); // Temporary loading truth does not erase a result.
        switch (reset)
        {
            case "stop": execution.Stop(context); break;
            case "inside": execution.StartDutyFromInside(context); break;
            case "outside": execution.StartDutyFromOutside(); break;
            case "resume": execution.ResumeDutyFromInside(context); break;
            case "new-duty": execution.ResetDutyCompletion(); break;
            case "departure": execution.ObserveDutyCompletion(Context(inDuty: false)); break;
            case "logout": execution.ObserveDutyCompletion(Context(loggedIn: false)); break;
            case "territory": execution.ObserveDutyCompletion(Context(territory: 999)); break;
            case "content": execution.ObserveDutyCompletion(Context(content: 999)); break;
        }
        Assert.Null(execution.CompletionTreasureSweepWithoutExitDuty);
        Assert.False(execution.CompletionTreasureSweepCompleted);
    }

    [Fact]
    public void CancelledOrDisabledSweepNeverReportsCompletion()
    {
        using var tempDirectory = new TempDirectory();
        var execution = CreateExecution(tempDirectory.Path, out _);
        var context = Context();
        execution.StartDutyFromInside(context, true);
        execution.BeginDutyCompletionTreasureSweep(context, "Treasure duty");
        execution.Stop(context);
        Assert.Null(execution.CompletionTreasureSweepWithoutExitDuty);
        Assert.False(execution.CompletionTreasureSweepCompleted);
        execution.StartDutyFromInside(context, true);
        execution.BeginDutyCompletionTreasureSweep(context, "Treasure duty");
        execution.UpdateLeaveDuty(context, ObservationSnapshot.Empty, false, () => Assert.Fail("Unexpected exit"));
        Assert.False(execution.CompletionTreasureSweepCompleted);
        Assert.False(execution.IsOwned);
    }

    [Fact]
    public void VariantCriterionCompletionRetainsOwnershipThroughLootThenReleasesWithoutLeaving()
    {
        using var tempDirectory = new TempDirectory();
        var log = DispatchProxy.Create<IPluginLog, ForceMarchLockTests.NoOpProxy>();
        var keys = DispatchProxy.Create<IKeyState, ForceMarchLockTests.NoOpProxy>();
        var objects = DispatchProxy.Create<IObjectTable, ForceMarchLockTests.ObjectTableProxy>();
        ((ForceMarchLockTests.ObjectTableProxy)(object)objects).LocalPlayer =
            DispatchProxy.Create<IPlayerCharacter, ForceMarchLockTests.GameObjectProxy>();
        var commands = DispatchProxy.Create<ICommandManager, ForceMarchLockTests.CommandManagerProxy>();
        var rules = new ObjectPriorityRuleService(log, null!, tempDirectory.Path);
        var frontier = new DungeonFrontierService(null!, objects, log, rules, null!);
        var execution = new ExecutionService(null!, objects, null!, commands, null!, frontier, null!, rules,
            new HyperFocusLeaseService(_ => "{}", _ => "{}", _ => "{}", () => "{}"),
            new TreasureDoorStrafeInputService(keys, log), new CardinalHoldInputService(keys, log),
            new Configuration(), log);
        var context = Context(territory: 1069, content: 900, duty: new DutyCatalogEntry
        {
            ContentFinderConditionId = 900, TerritoryTypeId = 1069, Name = "V&C", EnglishName = "V&C",
            ContentTypeName = "V&C Dungeon", ExpansionName = "EW", SupportNote = "Existing maturity",
            LevelRequired = 90, SortKey = 1, ExVersion = 4, ContentTypeRowId = 30, ContentMemberTypeRowId = 3,
            PartySize = 4, Category = DutyCategory.FourMan, SupportLevel = DutySupportLevel.PassiveOnly,
            ClearanceStatus = DutyClearanceStatus.NotCleared, IsPlannedTest = true, IsMainScenario = false,
        });
        Assert.True(Plugin.ShouldRunDutyCompletionTreasureSweep(context, true));
        Assert.False(Plugin.ShouldRunDutyCompletionTreasureSweep(context, false));
        Assert.False(Plugin.ShouldRunDutyCompletionTreasureSweep(Context(), true));
        Assert.True(execution.BeginDutyCompletionTreasureSweep(context, "V&C"));
        Assert.True(execution.IsOwned);
        Assert.False(execution.IsLeaveRequested);
        var now = DateTime.UtcNow;
        var exitRequests = 0;
        void Tick(DateTime time) => execution.UpdateLeaveDuty(context, ObservationSnapshot.Empty, true,
            () => ++exitRequests, time);
        Tick(now); // Spawn grace keeps ownership even before the final coffer appears.
        Assert.True(execution.IsOwned);
        // Exercise the existing post-interaction loot wait without invoking a native game callback.
        typeof(ExecutionService).GetField("leaveTreasureInteractionSent", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(execution, true);
        Tick(now.AddMinutes(1)); // First clear observation starts settling.
        Assert.True(execution.IsOwned);
        Tick(now.AddMinutes(1).AddSeconds(3)); // Settled; start loot-distribution wait.
        Assert.True(execution.IsOwned);
        Tick(now.AddMinutes(1).AddSeconds(4));
        Assert.True(execution.IsOwned);
        Tick(now.AddMinutes(2));
        Assert.False(execution.IsOwned);
        Assert.Equal(OwnershipMode.Observing, execution.CurrentMode);
        Assert.Equal(0, exitRequests);
        Assert.Equal(DutySupportLevel.PassiveOnly, context.CurrentDuty!.SupportLevel);
        Assert.True(context.CurrentDuty.IsPlannedTest);

        Assert.True(execution.LeaveDuty(context, false)); // A later explicit Leave still works.
        execution.UpdateLeaveDuty(context, ObservationSnapshot.Empty, false, () => ++exitRequests);
        Assert.Equal(1, exitRequests);
    }

    [Theory]
    [InlineData(ClientLanguage.English, "Return to the starting point for<br><string(gstr56)>?")]
    [InlineData(ClientLanguage.French, "Retourner au point de départ de la mission <string(gstr56)><nbsp>?")]
    [InlineData(ClientLanguage.German, "Zum Ausgangspunkt von „<string(gstr56)>“ zurückkehren?")]
    [InlineData(ClientLanguage.Japanese, "「<string(gstr56)>」の<br>開始地点に戻ります。よろしいですか？")]
    public void ExplicitCompletedDutyLeaveAcceptsOnlyReturnThenResumesExitAndRejectsStaleCompletion(
        ClientLanguage language, string macro)
    {
        using var tempDirectory = new TempDirectory();
        var log = DispatchProxy.Create<IPluginLog, ForceMarchLockTests.NoOpProxy>();
        var keys = DispatchProxy.Create<IKeyState, ForceMarchLockTests.NoOpProxy>();
        var objects = DispatchProxy.Create<IObjectTable, ForceMarchLockTests.ObjectTableProxy>();
        ((ForceMarchLockTests.ObjectTableProxy)(object)objects).LocalPlayer =
            DispatchProxy.Create<IPlayerCharacter, ForceMarchLockTests.GameObjectProxy>();
        var commands = DispatchProxy.Create<ICommandManager, ForceMarchLockTests.CommandManagerProxy>();
        var rules = new ObjectPriorityRuleService(log, null!, tempDirectory.Path);
        var frontier = new DungeonFrontierService(null!, objects, log, rules, null!);
        var execution = new ExecutionService(null!, objects, null!, commands, null!, frontier, null!, rules,
            new HyperFocusLeaseService(_ => "{}", _ => "{}", _ => "{}", () => "{}"),
            new TreasureDoorStrafeInputService(keys, log), new CardinalHoldInputService(keys, log),
            new Configuration(), log);
        var context = Context();
        var evaluator = new ReturnEvaluator(language, macro);
        var prompt = evaluator.Prompt;
        var clicks = 0;
        var attempts = 0;
        var clickSucceeds = true;
        bool AcceptReturn()
        {
            ++attempts;
            if (!GameInteractionHelper.IsReturnToEntrancePrompt(prompt, language, _ => macro, evaluator))
                return false;
            if (!clickSucceeds) return false;
            ++clicks;
            return true;
        }

        // Loading the plugin in an already completed duty is still unknown completion.
        Assert.True(execution.LeaveDuty(context, considerTreasureCoffers: false));
        Assert.False(execution.TryHandleLeaveRespawn(context, true, AcceptReturn));
        Assert.Equal(0, attempts);
        execution.MarkDutyCompleted(context, 1048, 832); // Different duty event.
        execution.MarkDutyCompleted(context, 1044, 999); // Same territory, wrong content.
        Assert.False(execution.TryHandleLeaveRespawn(context, true, AcceptReturn));

        execution.MarkDutyCompleted(context, 1044, 831);
        execution.CompleteDuty("Test duty");
        Assert.False(execution.TryHandleLeaveRespawn(context, true, AcceptReturn));
        Assert.True(execution.BeginDutyCompletionTreasureSweep(context, "Test duty"));
        Assert.False(execution.TryHandleLeaveRespawn(context, true, AcceptReturn));
        Assert.Equal(0, attempts); // Completion and its automatic sweep cannot authorize respawn.

        Assert.True(execution.LeaveDuty(context, considerTreasureCoffers: true));
        foreach (var unrelated in new[] { "", "Accept Raise?", "Abandon your current duty?", "Purchase this item?", prompt + " extra" })
        {
            prompt = unrelated;
            Assert.True(execution.TryHandleLeaveRespawn(context, true, AcceptReturn));
            Assert.Equal(0, clicks);
        }

        prompt = evaluator.Prompt;
        clickSucceeds = false;
        Assert.True(execution.TryHandleLeaveRespawn(context, true, AcceptReturn));
        Assert.Equal(0, clicks);
        clickSucceeds = true;
        Assert.True(execution.TryHandleLeaveRespawn(context, true, AcceptReturn));
        Assert.Equal(1, clicks); // No delay after the recognized return prompt appears.
        Assert.True(execution.IsLeaveRequested);
        Assert.True(execution.TryHandleLeaveRespawn(context, true, AcceptReturn));
        Assert.True(execution.TryHandleLeaveRespawn(context, null, AcceptReturn));
        Assert.Equal(1, clicks); // No repeated Yes while waiting for respawn.

        var transition = Context(inDuty: false, transition: true, territory: 0, content: 0);
        execution.ObserveDutyCompletion(transition);
        Assert.True(execution.TryHandleLeaveRespawn(transition, false, AcceptReturn));
        Assert.True(execution.IsLeaveRequested);
        Assert.False(execution.TryHandleLeaveRespawn(context, false, AcceptReturn));
        var exitRequests = 0;
        execution.UpdateLeaveDuty(context, new ObservationSnapshot
        {
            LiveMonsters = [], LiveFollowTargets = [], MonsterGhosts = [], LiveInteractables = [], InteractableGhosts = [],
        }, considerTreasureCoffers: false, () => ++exitRequests);
        Assert.Equal(1, exitRequests);
        Assert.Equal(OwnershipMode.Leaving, execution.CurrentMode);
        Assert.True(execution.IsLeaveRequested);

        // Every supported return row follows the same evaluated, client-language recognition.
        foreach (var row in new uint[] { 118, 119, 194, 197 })
            Assert.True(GameInteractionHelper.IsReturnToEntrancePrompt(prompt, language, id => id == row ? macro : null, evaluator));
        Assert.False(GameInteractionHelper.IsReturnToEntrancePrompt(prompt, language, _ => macro + "<string(lstr1)>", evaluator));
        evaluator.Destination = "";
        Assert.False(GameInteractionHelper.IsReturnToEntrancePrompt(prompt, language, _ => macro, evaluator));
        evaluator.Destination = "Test Duty";
        evaluator.Throw = true;
        Assert.False(GameInteractionHelper.IsReturnToEntrancePrompt(prompt, language, _ => macro, evaluator));
        evaluator.Throw = false;

        // Exit, logout, new entry (including the same duty), and identity changes all invalidate completion.
        foreach (var reset in new Action[]
        {
            () => execution.ObserveDutyCompletion(Context(inDuty: false)),
            () => execution.ObserveDutyCompletion(Context(loggedIn: false, transition: true)),
            execution.ResetDutyCompletion,
            () => execution.ObserveDutyCompletion(Context(territory: 1048)),
            () => execution.ObserveDutyCompletion(Context(content: 999)),
        })
        {
            execution.MarkDutyCompleted(context, 1044, 831);
            reset();
            execution.LeaveDuty(context, considerTreasureCoffers: false);
            Assert.False(execution.TryHandleLeaveRespawn(context, true, AcceptReturn));
            Assert.Equal(1, clicks);
        }
    }

    private static DutyContextSnapshot Context(bool inDuty = true, bool loggedIn = true, bool transition = false,
        uint territory = 1044, uint content = 831, DutyCatalogEntry? duty = null, bool occupied = false,
        bool transition51 = false) => new()
    {
        PluginEnabled = true, IsLoggedIn = loggedIn, BoundByDuty = inDuty, BoundByDuty56 = false,
        BetweenAreas = transition, BetweenAreas51 = transition51, Jumping = false, Jumping61 = false,
        Occupied33 = false, OccupiedInQuestEvent = false, OccupiedInEvent = occupied,
        OccupiedInCutSceneEvent = false, WatchingCutscene = false, InCombat = false, Mounted = false,
        TerritoryTypeId = territory, MapId = 1, ContentFinderConditionId = content, CurrentDuty = duty,
    };

    private static ExecutionService CreateExecution(string path, out SweepObjectTableProxy table)
    {
        var log = DispatchProxy.Create<IPluginLog, ForceMarchLockTests.NoOpProxy>();
        var keys = DispatchProxy.Create<IKeyState, ForceMarchLockTests.NoOpProxy>();
        var objects = DispatchProxy.Create<IObjectTable, SweepObjectTableProxy>();
        table = (SweepObjectTableProxy)(object)objects;
        table.LocalPlayer = DispatchProxy.Create<IPlayerCharacter, ForceMarchLockTests.GameObjectProxy>();
        var rules = new ObjectPriorityRuleService(log, null!, path);
        var frontier = new DungeonFrontierService(null!, objects, log, rules, null!);
        return new ExecutionService(null!, objects, null!, DispatchProxy.Create<ICommandManager, ForceMarchLockTests.CommandManagerProxy>(),
            null!, frontier, null!, rules, new HyperFocusLeaseService(_ => "{}", _ => "{}", _ => "{}", () => "{}"),
            new TreasureDoorStrafeInputService(keys, log), new CardinalHoldInputService(keys, log), new Configuration(), log);
    }

    private static DutyContextSnapshot TreasureContext(bool occupied = false)
        => Context(occupied: occupied, duty: new DutyCatalogEntry
        {
            ContentFinderConditionId = 831, TerritoryTypeId = 1044, Name = "Ordinary treasure duty", EnglishName = "Ordinary treasure duty",
            ContentTypeName = "Treasure Hunt", ExpansionName = "EW", SupportNote = "Existing maturity",
            LevelRequired = 90, SortKey = 1, ExVersion = 4, ContentTypeRowId = 9, ContentMemberTypeRowId = 3,
            PartySize = 8, Category = DutyCategory.TreasureDungeon, SupportLevel = DutySupportLevel.PassiveOnly,
            ClearanceStatus = DutyClearanceStatus.NotCleared, IsPlannedTest = true, IsMainScenario = false,
        });

    private static T Field<T>(ExecutionService execution, string name)
        => (T)typeof(ExecutionService).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(execution)!;
    private static void SetField(ExecutionService execution, string name, object value)
        => typeof(ExecutionService).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(execution, value);

    public class SweepObjectTableProxy : DispatchProxy
    {
        public IPlayerCharacter? LocalPlayer { get; set; }
        public IGameObject? Coffer { get; set; }
        protected override object? Invoke(MethodInfo? method, object?[]? args)
            => method?.Name switch
            {
                "get_LocalPlayer" => LocalPlayer,
                "GetEnumerator" => (Coffer is null ? Array.Empty<IGameObject>() : new[] { Coffer! }).AsEnumerable().GetEnumerator(),
                _ => method?.ReturnType.IsValueType == true ? Activator.CreateInstance(method.ReturnType) : null,
            };
    }

    public class CofferProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args)
            => method?.Name switch
            {
                "get_GameObjectId" => 1ul,
                "get_IsTargetable" => true,
                "get_Position" => Vector3.Zero,
                _ => method?.ReturnType.IsValueType == true ? Activator.CreateInstance(method.ReturnType) : null,
            };
    }

    public class DutyReturnPlayerProxy : DispatchProxy
    {
        internal bool Dead;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
            => method?.Name switch
            {
                "get_IsDead" => Dead,
                "get_CurrentHp" => Dead ? 0u : 100u,
                "get_MaxHp" => 100u,
                _ => method?.ReturnType.IsValueType == true ? Activator.CreateInstance(method.ReturnType) : null,
            };
    }

    private sealed class ReturnEvaluator(ClientLanguage client, string macro) : ISeStringEvaluator
    {
        internal string Destination = "Test Duty";
        internal bool Throw;
        internal string Prompt => Build(macro.Replace("<string(gstr56)>", Destination)).ToString();
        private static ReadOnlySeString Build(string value) => new SeStringBuilder().AppendMacroString(value).ToReadOnlySeString();
        private ReadOnlySeString Result(string value, ClientLanguage? language)
        {
            Assert.Equal(client, language);
            if (Throw) throw new InvalidOperationException("Unavailable evaluator");
            return Build(value);
        }
        public ReadOnlySeString EvaluateFromAddon(uint row, Span<SeStringParameter> localParameters = default, ClientLanguage? language = null)
            => Result(macro.Replace("<string(gstr56)>", Destination), language);
        public ReadOnlySeString EvaluateMacroString(string macroString, Span<SeStringParameter> localParameters = default, ClientLanguage? language = null)
            => Result(Destination, language);
        public ReadOnlySeString Evaluate(ReadOnlySeString str, Span<SeStringParameter> localParameters = default, ClientLanguage? language = null) => throw new NotSupportedException();
        public ReadOnlySeString Evaluate(ReadOnlySeStringSpan str, Span<SeStringParameter> localParameters = default, ClientLanguage? language = null) => throw new NotSupportedException();
        public ReadOnlySeString EvaluateMacroString(ReadOnlySpan<byte> macroString, Span<SeStringParameter> localParameters = default, ClientLanguage? language = null) => throw new NotSupportedException();
        public ReadOnlySeString EvaluateFromLogMessage(uint row, Span<SeStringParameter> localParameters = default, ClientLanguage? language = null) => throw new NotSupportedException();
        public ReadOnlySeString EvaluateFromLobby(uint row, Span<SeStringParameter> localParameters = default, ClientLanguage? language = null) => throw new NotSupportedException();
        public string EvaluateActStr(ActionKind kind, uint id, ClientLanguage? language = null) => throw new NotSupportedException();
        public string EvaluateObjStr(ObjectKind kind, uint id, ClientLanguage? language = null) => throw new NotSupportedException();
    }
}
