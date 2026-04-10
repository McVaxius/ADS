using ADS.Models;
using Dalamud.Memory;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace ADS.Services;

public sealed class DialogAutomationService
{
    private static readonly TimeSpan DialogCheckCooldown = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan DialogHandleCooldown = TimeSpan.FromSeconds(2);

    private readonly IGameGui gameGui;
    private readonly DialogYesNoRuleService ruleService;
    private readonly IPluginLog log;
    private DateTime lastDialogCheckUtc = DateTime.MinValue;
    private string lastHandledDialog = string.Empty;
    private DateTime lastHandledDialogAtUtc = DateTime.MinValue;

    public DialogAutomationService(IGameGui gameGui, DialogYesNoRuleService ruleService, IPluginLog log)
    {
        this.gameGui = gameGui;
        this.ruleService = ruleService;
        this.log = log;
    }

    public void Update(DutyContextSnapshot context, OwnershipMode ownershipMode, bool pluginEnabled)
    {
        if (!pluginEnabled || !context.InDuty || !context.IsSupportedDuty)
            return;

        if (ownershipMode is not OwnershipMode.OwnedStartOutside
            and not OwnershipMode.OwnedStartInside
            and not OwnershipMode.OwnedResumeInside
            and not OwnershipMode.Leaving)
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (now - lastDialogCheckUtc < DialogCheckCooldown)
            return;

        lastDialogCheckUtc = now;
        TryHandleSelectYesno(now);
    }

    private unsafe void TryHandleSelectYesno(DateTime now)
    {
        nint addonPtr = gameGui.GetAddonByName("SelectYesno", 1);
        if (addonPtr == 0)
            return;

        var addon = (AddonSelectYesno*)addonPtr;
        if (addon == null || !addon->AtkUnitBase.IsVisible)
            return;

        var promptNode = addon->PromptText;
        if (promptNode == null || promptNode->NodeText.StringPtr == null)
            return;

        var promptSeString = MemoryHelper.ReadSeStringNullTerminated(new IntPtr(promptNode->NodeText.StringPtr));
        var dialogText = promptSeString.TextValue?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(dialogText))
            return;

        if (string.Equals(dialogText, lastHandledDialog, StringComparison.OrdinalIgnoreCase)
            && now - lastHandledDialogAtUtc < DialogHandleCooldown)
        {
            return;
        }

        var matchedRule = ruleService.MatchRule(dialogText);
        if (matchedRule is null)
            return;

        if (!TryClickResponse(addon, matchedRule))
        {
            log.Warning($"[ADS] Dialog rule matched but click failed: {dialogText}");
            return;
        }

        lastHandledDialog = dialogText;
        lastHandledDialogAtUtc = now;
        log.Information($"[ADS] Auto-confirmed SelectYesno via dialog rule '{matchedRule.PromptPattern}' -> {matchedRule.Response}: {dialogText}");
    }

    private static unsafe bool TryClickResponse(AddonSelectYesno* addon, DialogYesNoRule rule)
    {
        var callbackIndex = string.Equals(rule.Response, "No", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        var atkValues = stackalloc AtkValue[2];
        atkValues[0].Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int;
        atkValues[0].Int = callbackIndex;
        atkValues[1].Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int;
        atkValues[1].Int = 0;
        addon->AtkUnitBase.FireCallback(2, atkValues);
        return true;
    }
}
