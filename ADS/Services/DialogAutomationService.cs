using ADS.Models;
using Dalamud.Plugin.Services;

namespace ADS.Services;

public sealed class DialogAutomationService
{
    private static readonly TimeSpan DialogCheckCooldown = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan DialogHandleCooldown = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SelectYesNoResponseWindow = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan SelectYesNoResponseRetryCooldown = TimeSpan.FromMilliseconds(500);
    private const int MaxSelectYesNoResponseAttempts = 3;
    private const string IdleStatus = "Dialog automation idle; no actionable dialog visible.";
    private const string TreasurePassagePrompt = "Move immediately to sealed area?";
    private const string TreasurePassageActionKeyPrefix = "builtin|treasure-passage|";

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
    private bool selectYesNoResponsePending;
    private bool pendingSelectYesNoResponseYes;
    private DateTime pendingSelectYesNoStartedAtUtc = DateTime.MinValue;
    private DateTime pendingSelectYesNoLastAttemptAtUtc = DateTime.MinValue;
    private int pendingSelectYesNoAttempts;
    private string pendingSelectYesNoActionKey = string.Empty;
    private string pendingSelectYesNoRule = string.Empty;
    private string pendingSelectYesNoPrompt = string.Empty;
    private string pendingSelectYesNoSource = string.Empty;
    private string suppressedSelectYesNoActionKey = string.Empty;
    private string suppressedSelectYesNoPrompt = string.Empty;
    private string loggedTreasurePassageAcceptedKey = string.Empty;

    public DialogAutomationService(IGameGui gameGui, DialogYesNoRuleService ruleService, IPluginLog log)
    {
        this.gameGui = gameGui;
        this.ruleService = ruleService;
        this.log = log;
    }

    public bool DialogVisible { get; private set; }

    public string DialogPrompt { get; private set; } = string.Empty;

    public string DialogRule { get; private set; } = string.Empty;

    public string DialogStatus { get; private set; } = IdleStatus;

    public string DialogLastAction { get; private set; } = string.Empty;

    public string DialogLastFailure { get; private set; } = string.Empty;

    public DateTime DialogLastActionAtUtc { get; private set; } = DateTime.MinValue;

    public void Update(
        DutyContextSnapshot context,
        OwnershipMode ownershipMode,
        bool pluginEnabled,
        bool processDialogRulesOutsideOwnedDuty,
        bool suppressGenericYesNo)
    {
        RefreshVisibleDialogSnapshot();

        if (!pluginEnabled)
        {
            SetBlockedStatus("ADS disabled");
            return;
        }

        if (!context.IsLoggedIn)
        {
            SetBlockedStatus("character not logged in");
            return;
        }

        if (context.IsUnsafeTransition)
        {
            SetBlockedStatus("unsafe transition active");
            return;
        }

        if (suppressGenericYesNo)
        {
            SetBlockedStatus("utility repair owns SelectYesno confirmations");
            return;
        }

        if (!processDialogRulesOutsideOwnedDuty
            && (!context.InInstancedDuty || !IsOwnedOrLeaving(ownershipMode))
            && !HasBuiltInTreasurePassagePrompt(context))
        {
            SetBlockedStatus("dialog scope requires owned/leaving duty");
            return;
        }

        var now = DateTime.UtcNow;
        if (now - lastDialogCheckUtc < DialogCheckCooldown)
            return;

        lastDialogCheckUtc = now;
        TryHandleDialogRules(context, now);
    }

    private static bool IsOwnedOrLeaving(OwnershipMode ownershipMode)
        => ownershipMode is OwnershipMode.OwnedStartOutside
            or OwnershipMode.OwnedStartInside
            or OwnershipMode.OwnedResumeInside
            or OwnershipMode.Leaving;

    private void TryHandleDialogRules(DutyContextSnapshot context, DateTime now)
    {
        ClearSuppressedSelectYesNoIfDialogChanged();

        if (TryHandlePendingSelectYesNoResponse(now))
            return;

        var action = FindBuiltInTreasurePassageAction(context) ?? FindNextAction();
        if (action is null)
        {
            ResetPendingAction();
            SetNoActionStatus();
            return;
        }

        DialogRule = DescribeRule(action.Rule);
        if (IsSuppressedSelectYesNoAction(action))
        {
            DialogStatus = $"SelectYesno response exhausted for this prompt; manual action required: {DescribeAction(action)}";
            DialogLastFailure = DialogStatus;
            return;
        }

        if (string.Equals(action.Key, lastHandledActionKey, StringComparison.OrdinalIgnoreCase)
            && now - lastHandledActionAtUtc < DialogHandleCooldown)
        {
            var remaining = DialogHandleCooldown - (now - lastHandledActionAtUtc);
            DialogStatus = $"Dialog rule matched, but last action cooldown is still active for {Math.Max(0, remaining.TotalSeconds):0.0}s: {DescribeAction(action)}";
            return;
        }

        if (!IsActionDelaySatisfied(action, now))
            return;

        if (IsSelectYesNoAction(action))
        {
            StartSelectYesNoResponse(action, now);
            return;
        }

        if (!ExecuteAction(action))
        {
            var failure = $"Dialog rule matched but action failed: {DescribeAction(action)}";
            DialogLastFailure = failure;
            DialogStatus = failure;
            log.Warning($"[ADS] {failure}");
            ResetPendingAction();
            return;
        }

        var actionDescription = DescribeAction(action);
        lastHandledActionKey = action.Key;
        lastHandledActionAtUtc = now;
        DialogLastAction = actionDescription;
        DialogLastActionAtUtc = now;
        DialogLastFailure = string.Empty;
        DialogStatus = $"Dialog rule action completed: {actionDescription}";
        ResetPendingAction();
        log.Information($"[ADS] {DialogStatus}");
    }

    private bool TryHandlePendingSelectYesNoResponse(DateTime now)
    {
        if (!selectYesNoResponsePending)
            return false;

        DialogRule = pendingSelectYesNoRule;

        if (!DialogVisible)
        {
            CompletePendingSelectYesNoResponse(now, "dialog closed");
            return true;
        }

        if (!string.IsNullOrWhiteSpace(DialogPrompt)
            && !string.Equals(DialogPrompt, pendingSelectYesNoPrompt, StringComparison.Ordinal))
        {
            CompletePendingSelectYesNoResponse(now, $"prompt changed to: {DialogPrompt}");
            return true;
        }

        if (pendingSelectYesNoAttempts >= MaxSelectYesNoResponseAttempts)
        {
            ExhaustPendingSelectYesNoResponse();
            return true;
        }

        if (now - pendingSelectYesNoStartedAtUtc > SelectYesNoResponseWindow)
        {
            ExhaustPendingSelectYesNoResponse();
            return true;
        }

        if (pendingSelectYesNoLastAttemptAtUtc != DateTime.MinValue
            && now - pendingSelectYesNoLastAttemptAtUtc < SelectYesNoResponseRetryCooldown)
        {
            var remaining = SelectYesNoResponseRetryCooldown - (now - pendingSelectYesNoLastAttemptAtUtc);
            DialogStatus = $"Sent {FormatYesNo(pendingSelectYesNoResponseYes)} to SelectYesno; waiting {Math.Max(0, remaining.TotalSeconds):0.0}s for close before retry: {pendingSelectYesNoSource}";
            return true;
        }

        SendPendingSelectYesNoResponse(now);
        return true;
    }

    private void StartSelectYesNoResponse(DialogRuleAction action, DateTime now)
    {
        ResetPendingAction();
        selectYesNoResponsePending = true;
        pendingSelectYesNoResponseYes = IsYesResponse(action.Rule);
        pendingSelectYesNoStartedAtUtc = now;
        pendingSelectYesNoLastAttemptAtUtc = DateTime.MinValue;
        pendingSelectYesNoAttempts = 0;
        pendingSelectYesNoActionKey = action.Key;
        pendingSelectYesNoRule = DescribeRule(action.Rule);
        pendingSelectYesNoPrompt = action.DialogText;
        pendingSelectYesNoSource = DescribeAction(action);
        DialogRule = pendingSelectYesNoRule;
        SendPendingSelectYesNoResponse(now);
    }

    private void SendPendingSelectYesNoResponse(DateTime now)
    {
        pendingSelectYesNoAttempts++;
        pendingSelectYesNoLastAttemptAtUtc = now;

        var responseText = FormatYesNo(pendingSelectYesNoResponseYes);
        var method = GetSelectYesNoClickMethodForAttempt(pendingSelectYesNoAttempts);
        var methodDescription = GameInteractionHelper.DescribeSelectYesNoClickMethod(method);
        if (GameInteractionHelper.TrySelectYesNo(pendingSelectYesNoResponseYes, gameGui, method, log))
        {
            var action = pendingSelectYesNoAttempts == 1
                ? $"sent {responseText} via {methodDescription}; waiting for SelectYesno to close: {pendingSelectYesNoSource}"
                : $"retried {responseText} via {methodDescription} (attempt {pendingSelectYesNoAttempts}/{MaxSelectYesNoResponseAttempts}); waiting for SelectYesno to close: {pendingSelectYesNoSource}";
            DialogLastAction = action;
            DialogLastActionAtUtc = now;
            DialogLastFailure = string.Empty;
            DialogStatus = action;
            log.Information($"[ADS] {DialogStatus}");
            return;
        }

        var failure = $"SelectYesno {responseText} send failed via {methodDescription} (attempt {pendingSelectYesNoAttempts}/{MaxSelectYesNoResponseAttempts}): {pendingSelectYesNoSource}";
        DialogLastFailure = failure;
        DialogStatus = pendingSelectYesNoAttempts >= MaxSelectYesNoResponseAttempts
            ? $"{failure}; manual action required."
            : $"{failure}; will retry.";
        log.Warning($"[ADS] {DialogStatus}");
    }

    private void CompletePendingSelectYesNoResponse(DateTime now, string reason)
    {
        var action = $"SelectYesno response accepted; {reason}: {pendingSelectYesNoSource}";
        lastHandledActionKey = pendingSelectYesNoActionKey;
        lastHandledActionAtUtc = now;
        DialogLastAction = action;
        DialogLastActionAtUtc = now;
        DialogLastFailure = string.Empty;
        DialogStatus = action;
        LogTreasurePassageAcceptedIfNeeded();
        ClearPendingSelectYesNoResponse();
        log.Information($"[ADS] {DialogStatus}");
    }

    private void ExhaustPendingSelectYesNoResponse()
    {
        suppressedSelectYesNoActionKey = pendingSelectYesNoActionKey;
        suppressedSelectYesNoPrompt = pendingSelectYesNoPrompt;
        var failure = $"SelectYesno response exhausted after {pendingSelectYesNoAttempts} attempt(s); still visible; manual action required: {pendingSelectYesNoSource}";
        DialogLastFailure = failure;
        DialogStatus = failure;
        ClearPendingSelectYesNoResponse();
        log.Warning($"[ADS] {failure}");
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

    private DialogRuleAction? FindBuiltInTreasurePassageAction(DutyContextSnapshot context)
    {
        if (!HasBuiltInTreasurePassagePrompt(context))
        {
            return null;
        }

        var rule = new DialogYesNoRule
        {
            Enabled = true,
            Addon = "SelectYesno",
            PromptPattern = TreasurePassagePrompt,
            MatchMode = "Equals",
            Response = "Yes",
            Delay = 0,
            Notes = "Built-in treasure passage prompt."
        };

        return new DialogRuleAction(
            DialogRuleActionKind.ClickAddon,
            rule,
            "SelectYesno",
            DialogPrompt,
            string.Empty,
            string.Empty,
            BuildTreasurePassageActionKey(DialogPrompt),
            0);
    }

    private bool HasBuiltInTreasurePassagePrompt(DutyContextSnapshot context)
        => context.InInstancedDuty
           && TreasureDungeonData.IsSupportedDutyTerritory(context.TerritoryTypeId)
           && DialogVisible
           && IsTreasurePassagePrompt(DialogPrompt);

    private bool IsActionDelaySatisfied(DialogRuleAction action, DateTime now)
    {
        if (action.DelaySeconds <= 0)
            return true;

        if (!string.Equals(action.Key, pendingActionKey, StringComparison.OrdinalIgnoreCase))
        {
            pendingActionKey = action.Key;
            pendingActionFirstSeenUtc = now;
            DialogStatus = $"Dialog rule matched; waiting configured delay {action.DelaySeconds:0.0}s before action: {DescribeAction(action)}";
            return false;
        }

        var elapsed = (now - pendingActionFirstSeenUtc).TotalSeconds;
        if (elapsed >= action.DelaySeconds)
            return true;

        DialogStatus = $"Dialog rule matched; waiting configured delay {Math.Max(0, action.DelaySeconds - elapsed):0.0}s before action: {DescribeAction(action)}";
        return false;
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
        var yes = IsYesResponse(rule);
        return GameInteractionHelper.TrySelectYesNo(yes, gameGui, log);
    }

    private static bool IsSelectYesNoAction(DialogRuleAction action)
        => action.Kind == DialogRuleActionKind.ClickAddon && IsSelectYesnoAddon(action.AddonName);

    private bool IsSuppressedSelectYesNoAction(DialogRuleAction action)
        => IsSelectYesNoAction(action)
            && !string.IsNullOrWhiteSpace(suppressedSelectYesNoActionKey)
            && string.Equals(action.Key, suppressedSelectYesNoActionKey, StringComparison.OrdinalIgnoreCase);

    private void ClearPendingSelectYesNoResponse()
    {
        selectYesNoResponsePending = false;
        pendingSelectYesNoResponseYes = false;
        pendingSelectYesNoStartedAtUtc = DateTime.MinValue;
        pendingSelectYesNoLastAttemptAtUtc = DateTime.MinValue;
        pendingSelectYesNoAttempts = 0;
        pendingSelectYesNoActionKey = string.Empty;
        pendingSelectYesNoRule = string.Empty;
        pendingSelectYesNoPrompt = string.Empty;
        pendingSelectYesNoSource = string.Empty;
    }

    private void LogTreasurePassageAcceptedIfNeeded()
    {
        if (!IsTreasurePassageActionKey(pendingSelectYesNoActionKey))
            return;

        var key = $"{pendingSelectYesNoActionKey}|{pendingSelectYesNoStartedAtUtc.Ticks}";
        if (string.Equals(key, loggedTreasurePassageAcceptedKey, StringComparison.Ordinal))
            return;

        loggedTreasurePassageAcceptedKey = key;
        log.Information($"[ADS] treasure passage prompt accepted prompt='{EscapeLogText(pendingSelectYesNoPrompt)}'");
    }

    private void ClearSuppressedSelectYesNoIfDialogChanged()
    {
        if (string.IsNullOrWhiteSpace(suppressedSelectYesNoActionKey))
            return;

        if (!DialogVisible
            || (!string.IsNullOrWhiteSpace(DialogPrompt)
                && !string.Equals(DialogPrompt, suppressedSelectYesNoPrompt, StringComparison.Ordinal)))
        {
            suppressedSelectYesNoActionKey = string.Empty;
            suppressedSelectYesNoPrompt = string.Empty;
        }
    }

    private static GameInteractionHelper.SelectYesNoClickMethod GetSelectYesNoClickMethodForAttempt(int attempt)
        => attempt switch
        {
            1 => GameInteractionHelper.SelectYesNoClickMethod.ButtonEvent,
            2 => GameInteractionHelper.SelectYesNoClickMethod.FireCallbackInt,
            _ => GameInteractionHelper.SelectYesNoClickMethod.LegacyCallback,
        };

    private static bool IsYesResponse(DialogYesNoRule rule)
        => !string.Equals(rule.Response, "No", StringComparison.OrdinalIgnoreCase);

    private static string FormatYesNo(bool yes)
        => yes ? "Yes" : "No";

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

    private void RefreshVisibleDialogSnapshot()
    {
        DialogVisible = GameInteractionHelper.IsAddonVisible("SelectYesno");
        if (!DialogVisible)
        {
            DialogPrompt = string.Empty;
            return;
        }

        DialogPrompt = GameInteractionHelper.TryGetSelectYesNoPromptText(gameGui, out var dialogText)
            ? dialogText
            : string.Empty;
    }

    private void SetBlockedStatus(string reason)
    {
        ResetPendingAction();
        ClearPendingSelectYesNoResponse();
        DialogRule = string.Empty;
        DialogStatus = DialogVisible
            ? string.IsNullOrWhiteSpace(DialogPrompt)
                ? $"Dialog automation blocked ({reason}) while SelectYesno is visible, but prompt text could not be read."
                : $"Dialog automation blocked ({reason}) while SelectYesno is visible: {DialogPrompt}"
            : $"Dialog automation blocked ({reason}).";
    }

    private void SetNoActionStatus()
    {
        DialogRule = string.Empty;
        if (!DialogVisible)
        {
            DialogStatus = IdleStatus;
            return;
        }

        DialogStatus = string.IsNullOrWhiteSpace(DialogPrompt)
            ? "SelectYesno is visible, but ADS could not read the prompt text; no dialog rule action was taken."
            : $"SelectYesno is visible, but no dialog rule matched prompt: {DialogPrompt}";
    }

    private static string DescribeRule(DialogYesNoRule rule)
        => $"{NormalizeAddon(rule.Addon)} {rule.MatchMode} '{rule.PromptPattern}' => {rule.Response}";

    private static string BuildActionKey(string kind, DialogYesNoRule rule, string first, string second, string third)
        => string.Join('|', kind, NormalizeAddon(rule.Addon), rule.PromptPattern, rule.MatchMode, rule.Response, first, second, third);

    private static string NormalizeAddon(string? addon)
        => string.IsNullOrWhiteSpace(addon) ? "SelectYesno" : addon.Trim();

    private static string NormalizeOptional(string? value)
        => value?.Trim() ?? string.Empty;

    private static bool IsSelectYesnoAddon(string addonName)
        => string.Equals(addonName, "SelectYesno", StringComparison.OrdinalIgnoreCase);

    private static bool IsTreasurePassagePrompt(string prompt)
        => string.Equals(prompt?.Trim(), TreasurePassagePrompt, StringComparison.OrdinalIgnoreCase);

    private static string BuildTreasurePassageActionKey(string prompt)
        => $"{TreasurePassageActionKeyPrefix}{prompt.Trim()}";

    private static bool IsTreasurePassageActionKey(string actionKey)
        => actionKey.StartsWith(TreasurePassageActionKeyPrefix, StringComparison.OrdinalIgnoreCase);

    private static string EscapeLogText(string value)
        => (value ?? string.Empty)
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
}
