using ADS.Models;
using Dalamud.Plugin.Services;

namespace ADS.Services;

public sealed class DialogAutomationService
{
    private static readonly TimeSpan DialogCheckCooldown = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan DialogHandleCooldown = TimeSpan.FromSeconds(2);

    private enum DialogRuleActionKind
    {
        ClickAddon,
        RestoreNotification,
    }

    private sealed record DialogRuleAction(
        DialogRuleActionKind Kind,
        DialogYesNoRule Rule,
        string AddonName,
        string DialogText,
        string NotificationName,
        string CallbackText,
        string Key,
        double DelaySeconds);

    private readonly IGameGui gameGui;
    private readonly DialogYesNoRuleService ruleService;
    private readonly IPluginLog log;
    private DateTime lastDialogCheckUtc = DateTime.MinValue;
    private string pendingActionKey = string.Empty;
    private DateTime pendingActionFirstSeenUtc = DateTime.MinValue;
    private string lastHandledActionKey = string.Empty;
    private DateTime lastHandledActionAtUtc = DateTime.MinValue;

    public DialogAutomationService(IGameGui gameGui, DialogYesNoRuleService ruleService, IPluginLog log)
    {
        this.gameGui = gameGui;
        this.ruleService = ruleService;
        this.log = log;
    }

    public void Update(DutyContextSnapshot context, OwnershipMode ownershipMode, bool pluginEnabled)
    {
        if (!pluginEnabled || !context.InInstancedDuty || context.IsUnsafeTransition)
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
        TryHandleDialogRules(now);
    }

    private void TryHandleDialogRules(DateTime now)
    {
        var action = FindNextAction();
        if (action is null)
        {
            ResetPendingAction();
            return;
        }

        if (string.Equals(action.Key, lastHandledActionKey, StringComparison.OrdinalIgnoreCase)
            && now - lastHandledActionAtUtc < DialogHandleCooldown)
        {
            return;
        }

        if (!IsActionDelaySatisfied(action, now))
            return;

        if (!ExecuteAction(action))
        {
            log.Warning($"[ADS] Dialog rule matched but action failed: {DescribeAction(action)}");
            ResetPendingAction();
            return;
        }

        lastHandledActionKey = action.Key;
        lastHandledActionAtUtc = now;
        ResetPendingAction();
        log.Information($"[ADS] Dialog rule action completed: {DescribeAction(action)}");
    }

    private DialogRuleAction? FindNextAction()
    {
        foreach (var rule in ruleService.GetEnabledRules())
        {
            var addonName = NormalizeAddon(rule.Addon);
            var notificationName = NormalizeOptional(rule.Notification);
            var callbackText = NormalizeOptional(rule.NotificationCB);

            if (string.IsNullOrWhiteSpace(notificationName)
                || string.IsNullOrWhiteSpace(callbackText)
                || GameInteractionHelper.IsAddonVisible(addonName)
                || !GameInteractionHelper.IsAddonVisible(notificationName))
            {
                continue;
            }

            return new DialogRuleAction(
                DialogRuleActionKind.RestoreNotification,
                rule,
                addonName,
                string.Empty,
                notificationName,
                callbackText,
                BuildActionKey("notification", rule, addonName, notificationName, callbackText),
                Math.Max(0, rule.Delay));
        }

        foreach (var rule in ruleService.GetEnabledRules())
        {
            var addonName = NormalizeAddon(rule.Addon);
            if (!GameInteractionHelper.IsAddonVisible(addonName))
                continue;

            if (IsSelectYesnoAddon(addonName))
            {
                if (!GameInteractionHelper.TryGetSelectYesNoPromptText(gameGui, out var dialogText))
                    continue;

                if (!ruleService.MatchesPrompt(rule, dialogText))
                    continue;

                return new DialogRuleAction(
                    DialogRuleActionKind.ClickAddon,
                    rule,
                    addonName,
                    dialogText,
                    NormalizeOptional(rule.Notification),
                    NormalizeOptional(rule.NotificationCB),
                    BuildActionKey("click", rule, addonName, dialogText, rule.Response),
                    Math.Max(0, rule.Delay));
            }

            if (!string.IsNullOrWhiteSpace(rule.PromptPattern))
                continue;

            var callbackText = NormalizeOptional(rule.NotificationCB);
            if (string.IsNullOrWhiteSpace(callbackText))
                continue;

            return new DialogRuleAction(
                DialogRuleActionKind.ClickAddon,
                rule,
                addonName,
                string.Empty,
                NormalizeOptional(rule.Notification),
                callbackText,
                BuildActionKey("callback", rule, addonName, callbackText, string.Empty),
                Math.Max(0, rule.Delay));
        }

        return null;
    }

    private bool IsActionDelaySatisfied(DialogRuleAction action, DateTime now)
    {
        if (action.DelaySeconds <= 0)
            return true;

        if (!string.Equals(action.Key, pendingActionKey, StringComparison.OrdinalIgnoreCase))
        {
            pendingActionKey = action.Key;
            pendingActionFirstSeenUtc = now;
            return false;
        }

        return (now - pendingActionFirstSeenUtc).TotalSeconds >= action.DelaySeconds;
    }

    private void ResetPendingAction()
    {
        pendingActionKey = string.Empty;
        pendingActionFirstSeenUtc = DateTime.MinValue;
    }

    private bool ExecuteAction(DialogRuleAction action)
    {
        return action.Kind switch
        {
            DialogRuleActionKind.RestoreNotification => TryFireConfiguredCallback(action.CallbackText),
            DialogRuleActionKind.ClickAddon when IsSelectYesnoAddon(action.AddonName) => TryClickResponse(action.Rule),
            DialogRuleActionKind.ClickAddon when !string.IsNullOrWhiteSpace(action.CallbackText) => TryFireConfiguredCallback(action.CallbackText),
            _ => false,
        };
    }

    private bool TryFireConfiguredCallback(string callbackText)
    {
        if (!TryParseAddonCallback(callbackText, out var addonName, out var updateState, out var args))
        {
            log.Warning($"[ADS] Invalid dialog rule callback '{callbackText}'. Expected: Addon true|false [args...]");
            return false;
        }

        if (GameInteractionHelper.TryFireAddonCallback(addonName, updateState, args))
            return true;

        log.Warning($"[ADS] Dialog rule callback failed because addon was not visible or callback threw: {callbackText}");
        return false;
    }

    private bool TryClickResponse(DialogYesNoRule rule)
    {
        var yes = !string.Equals(rule.Response, "No", StringComparison.OrdinalIgnoreCase);
        return GameInteractionHelper.TrySelectYesNo(yes, gameGui, log);
    }

    private static bool TryParseAddonCallback(string callbackText, out string addonName, out bool updateState, out object[] args)
    {
        addonName = string.Empty;
        updateState = false;
        args = [];

        var tokens = callbackText
            .Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (tokens.Length > 0 && string.Equals(tokens[0], "/callback", StringComparison.OrdinalIgnoreCase))
            tokens = tokens[1..];

        if (tokens.Length < 2 || !bool.TryParse(tokens[1], out updateState))
            return false;

        addonName = tokens[0];
        var parsedArgs = new object[tokens.Length - 2];
        for (var i = 2; i < tokens.Length; i++)
        {
            var token = tokens[i];
            if (bool.TryParse(token, out var boolValue))
                parsedArgs[i - 2] = boolValue;
            else if (int.TryParse(token, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var intValue))
                parsedArgs[i - 2] = intValue;
            else
                return false;
        }

        args = parsedArgs;
        return !string.IsNullOrWhiteSpace(addonName);
    }

    private static string DescribeAction(DialogRuleAction action)
        => action.Kind switch
        {
            DialogRuleActionKind.RestoreNotification => $"restore {action.NotificationName} via {action.CallbackText}",
            DialogRuleActionKind.ClickAddon when IsSelectYesnoAddon(action.AddonName) => $"click {action.Rule.Response} on {action.AddonName} via rule '{action.Rule.PromptPattern}': {action.DialogText}",
            DialogRuleActionKind.ClickAddon => $"fire {action.CallbackText} for {action.AddonName}",
            _ => action.Key,
        };

    private static string BuildActionKey(string kind, DialogYesNoRule rule, string first, string second, string third)
        => string.Join('|', kind, NormalizeAddon(rule.Addon), rule.PromptPattern, rule.MatchMode, rule.Response, first, second, third);

    private static string NormalizeAddon(string? addon)
        => string.IsNullOrWhiteSpace(addon) ? "SelectYesno" : addon.Trim();

    private static string NormalizeOptional(string? value)
        => value?.Trim() ?? string.Empty;

    private static bool IsSelectYesnoAddon(string addonName)
        => string.Equals(addonName, "SelectYesno", StringComparison.OrdinalIgnoreCase);
}
