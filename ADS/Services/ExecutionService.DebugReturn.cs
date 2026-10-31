using ADS.Models;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace ADS.Services;

internal readonly record struct DebugReturnFrame(
    ulong CharacterId, bool PlayerKnown, bool Dead, bool ReviveStateKnown,
    bool Reviving, bool ReviveFinished, bool PromptKnown, bool PromptVisible, bool ReturnPrompt);

public sealed partial class ExecutionService
{
    internal Func<DebugReturnFrame>? ReadDebugReturnFrame { get; set; }
    private (ulong Character, uint Territory, uint Cfc)? debugReturnDeath;
    private bool debugReturnPromptSeen;
    private bool debugReturnRevivingSeen;
    private bool debugReturnTransitionSeen;
    private int debugReturnLivingFrames;

    internal bool DebugReturnPending => debugReturnDeath.HasValue;

    internal void CancelDebugReturn()
    {
        debugReturnDeath = null;
        debugReturnPromptSeen = debugReturnRevivingSeen = debugReturnTransitionSeen = false;
        debugReturnLivingFrames = 0;
    }

    internal void ObserveDebugReturnTerritory(uint territory)
    {
        if (territory != 0 && debugReturnDeath is { } death && territory != death.Territory)
            CancelDebugReturn();
    }

    // Called once at the beginning of the existing framework coordinator, before
    // holds or observations can hide the Return transition or rebuild old progress.
    // True holds progression only while confirmed Return is settling into living state.
    internal bool RefreshDebugReturn(DutyContextSnapshot context, bool debugEnabled)
    {
        if (ReadRaidState(context).Enabled)
        {
            CancelDebugReturn();
            return false;
        }
        if (!debugEnabled || !context.PluginEnabled || !context.IsLoggedIn || !IsActiveOwnedDutyMode()
            || completedDuty is not null || !context.InInstancedDuty && !context.IsUnsafeTransition && !context.IsCutscene)
        {
            CancelDebugReturn();
            return false;
        }

        var frame = ReadDebugReturnFrame?.Invoke() ?? ReadNativeDebugReturnFrame();
        if (debugReturnDeath is { } previous
            && (frame.CharacterId != 0 && frame.CharacterId != previous.Character
                || context.TerritoryTypeId != 0 && context.TerritoryTypeId != previous.Territory
                || context.ContentFinderConditionId != 0 && context.ContentFinderConditionId != previous.Cfc))
        {
            CancelDebugReturn();
            return false;
        }
        if (context.TerritoryTypeId == 0 || context.ContentFinderConditionId == 0)
        {
            debugReturnLivingFrames = 0;
            return false;
        }

        if (debugReturnDeath is null && frame.PlayerKnown && frame.Dead && frame.CharacterId != 0 && context.InInstancedDuty)
            debugReturnDeath = (frame.CharacterId, context.TerritoryTypeId, context.ContentFinderConditionId);
        if (debugReturnDeath is null)
            return false;

        // Bind the prompt to a confirmed death. Another readable dialog (including
        // Raise) invalidates that candidate. Unreadable frames supply no new proof.
        if (frame.PlayerKnown && frame.Dead && frame.PromptKnown && frame.PromptVisible)
        {
            if (frame.ReturnPrompt) debugReturnPromptSeen = true;
            else
                debugReturnPromptSeen = debugReturnRevivingSeen = debugReturnTransitionSeen = false;
        }
        if (debugReturnPromptSeen && frame.ReviveStateKnown && frame.Reviving)
            debugReturnRevivingSeen = true;
        if (debugReturnPromptSeen && debugReturnRevivingSeen && context.IsUnsafeTransition)
            debugReturnTransitionSeen = true;

        if (!frame.PlayerKnown || frame.CharacterId == 0 || frame.Dead || context.IsUnsafeTransition || context.IsCutscene)
        {
            debugReturnLivingFrames = 0;
            return false;
        }
        if (!debugReturnPromptSeen || !debugReturnRevivingSeen || !debugReturnTransitionSeen)
        {
            // Raise/Phoenix, a closed prompt or a position change alone do not Return.
            CancelDebugReturn();
            return false;
        }
        if (!frame.ReviveStateKnown || !frame.ReviveFinished || !frame.PromptKnown || frame.PromptVisible
            || context.IsInteractionOccupied || context.InCombat)
        {
            debugReturnLivingFrames = 0;
            return false;
        }
        if (++debugReturnLivingFrames < 2)
        {
            SetPhase(ExecutionPhase.TransitionHold, "Debug Return: waiting for stable living duty state.");
            return true;
        }

        var policy = configuration.DebugReturnPolicy;
        CancelDebugReturn(); // Consume before any reset; this never acquires ownership.
        ResumeCutsceneTimers();
        CancelInteractionPause();
        ReleaseHeldMovementKeys("debug Return");
        StopMovementAssists();
        ClearInteractableCommitment();
        ClearCommittedForceMarchManualDestination();
        ResetRecoveryHold();
        ResetAquapolisRouteWiggleTracking();
        ClearTreasureFollowerPostTransitSettle("debug Return");
        unsafeTransitionNavigationStopLatched = treasureDungeonCombatStopLatched = false;
        treasureFollowerRouteTransitHoldLatched = treasureFollowerRouteTransitCutsceneSeen = false;
        if (policy == DebugReturnPolicy.RestartFromEntrance)
        {
            ResetCardinalHolds("debug Return restart");
            activeDebugCommandRuleIdentity = null;
            observationMemoryService.Reset();
            dungeonFrontierService.Reset();
        }
        var status = policy == DebugReturnPolicy.RestartFromEntrance
            ? "Debug Return: ADS progression restarted from the returned position."
            : "Debug Return: existing progression retained; replanning from the returned position.";
        SetPhase(ExecutionPhase.WaitingForTruth, status);
        log?.Information($"[ADS] {status}");
        return false;
    }

    private unsafe DebugReturnFrame ReadNativeDebugReturnFrame()
    {
        var character = Plugin.PlayerState?.ContentId ?? 0;
        var player = objectTable.LocalPlayer;
        if (player is null || player.Address == 0 || character == 0 || Plugin.Condition is null)
            return new(character, false, false, false, false, false, false, false, false);
        var state = UIState.Instance();
        var reviveKnown = state != null;
        var revive = reviveKnown ? state->Revive.State : default;
        nint addonAddress = Plugin.GameGui.GetAddonByName("SelectYesno", 1);
        var addon = (AddonSelectYesno*)addonAddress;
        var visible = addon != null && addon->AtkUnitBase.IsVisible;
        var readable = !visible || GameInteractionHelper.TryGetSelectYesNoPromptText(Plugin.GameGui, out _);
        return new(character, true, player.IsDead || Plugin.Condition[ConditionFlag.Unconscious],
            reviveKnown, reviveKnown && revive == ReviveState.Reviving, reviveKnown && revive == ReviveState.Alive,
            readable, visible, visible && readable && GameInteractionHelper.IsReturnToEntrancePromptVisible());
    }
}
