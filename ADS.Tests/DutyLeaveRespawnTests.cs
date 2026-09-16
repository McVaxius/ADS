using System.Reflection;
using ADS.Models;
using ADS.Services;
using Dalamud.Game;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.Text.Evaluator;
using Dalamud.Plugin.Services;
using Lumina.Text;
using Lumina.Text.ReadOnly;

namespace ADS.Tests;

public sealed class DutyLeaveRespawnTests
{
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
        uint territory = 1044, uint content = 831) => new()
    {
        PluginEnabled = true, IsLoggedIn = loggedIn, BoundByDuty = inDuty, BoundByDuty56 = false,
        BetweenAreas = transition, BetweenAreas51 = false, Jumping = false, Jumping61 = false,
        Occupied33 = false, OccupiedInQuestEvent = false, OccupiedInEvent = false,
        OccupiedInCutSceneEvent = false, WatchingCutscene = false, InCombat = false, Mounted = false,
        TerritoryTypeId = territory, MapId = 1, ContentFinderConditionId = content, CurrentDuty = null,
    };

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
