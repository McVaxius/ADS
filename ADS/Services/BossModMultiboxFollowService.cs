using System.Collections;
using System.Globalization;
using System.Reflection;
using ADS;
using ADS.Models;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace ADS.Services;

public sealed class BossModMultiboxFollowService
{
    private const string MultiboxModuleFullName = "BossMod.Autorotation.MiscAI.Multibox";
    private const string MultiboxLeaderTrack = "Leader";
    private const string AdsTreasureFollowPresetName = "ADS Treasure Follow";
    private const string FollowProviderLabel = "BMRAI/VBM";
    private const string AdsTreasureFollowPresetJson = """
        {
          "Name": "ADS Treasure Follow",
          "Modules": {
            "BossMod.Autorotation.MiscAI.Multibox": [
              { "Track": "Leader", "Value": 0 }
            ]
          }
        }
        """;

    private static readonly string[] MultiboxModuleTypeNames =
    [
        "BossMod.Autorotation.MiscAI.Multibox, BossMod",
        "BossMod.Autorotation.MiscAI.Multibox, BossModReborn",
        "BossMod.Autorotation.MiscAI.Multibox",
    ];

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commandManager;
    private readonly Configuration configuration;
    private readonly IPluginLog log;

    private string lastLoggedSuccessKey = string.Empty;
    private string lastReapplyAttemptKey = string.Empty;
    private uint bmraiFollowCommandAcceptedDutyKey;
    private bool bmraiFollowActivated;

    public BossModMultiboxFollowService(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        Configuration configuration,
        IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.commandManager = commandManager;
        this.configuration = configuration;
        this.log = log;
    }

    public bool FollowApplied { get; private set; }

    public string FollowStatus { get; private set; } = "No treasure portal opener captured.";

    public bool FollowerMovementOwnedByBmrai { get; private set; }

    public string FollowerMovementStatus { get; private set; } = "No treasure portal opener captured.";

    public ulong? FollowLeaderContentId { get; private set; }

    public string FollowMethod { get; private set; } = string.Empty;

    public string BmraiFollowCommandMethod { get; private set; } = "Name";

    public string BmraiFollowCommandText { get; private set; } = string.Empty;

    public bool? BmraiFollowCommandAccepted { get; private set; }

    public DateTime? BmraiFollowCommandAtUtc { get; private set; }

    public string BmraiFollowCommandStatus { get; private set; } = "BMRAI/VBM command not sent: no direct portal chat or interaction witness opener captured.";

    public string BmraiFollowCommandTargetName { get; private set; } = string.Empty;

    public int? BmraiFollowCommandTargetSlot { get; private set; }

    public ulong? BmraiFollowCommandTargetContentId { get; private set; }

    public string BmraiFollowCommandTargetSource { get; private set; } = string.Empty;

    public bool CleanupPending
        => configuration.BmraiTreasureFollowCleanupPending || bmraiFollowActivated;

    public void Clear(string reason, bool forceProviderCleanup = false)
    {
        var cleanupStatus = string.Empty;
        var shouldDisableBmrai = bmraiFollowActivated || configuration.BmraiTreasureFollowCleanupPending;
        if (forceProviderCleanup || CleanupPending)
        {
            cleanupStatus = DisableBmraiTreasureFollow(reason, shouldDisableBmrai);
            if (configuration.BmraiTreasureFollowCleanupPending)
            {
                configuration.BmraiTreasureFollowCleanupPending = false;
                configuration.Save();
            }
        }

        FollowApplied = false;
        FollowLeaderContentId = null;
        FollowMethod = string.Empty;
        FollowStatus = $"Treasure portal follow cleared after {reason}.{cleanupStatus}";
        SetBmraiCommandSkipped(null, FollowStatus);
        SetFollowerMovementAuthority(false, FollowStatus);
        lastLoggedSuccessKey = string.Empty;
        lastReapplyAttemptKey = string.Empty;
        bmraiFollowCommandAcceptedDutyKey = 0;
        bmraiFollowActivated = false;
    }

    public bool ApplyDirectTreasurePortalOpener(
        TreasurePortalOpenerSnapshot opener,
        DutyContextSnapshot? context = null,
        string reason = "direct treasure opener")
    {
        FollowLeaderContentId = opener.ContentId;

        if (!IsDirectBmraiSource(opener.Source))
        {
            FollowApplied = false;
            FollowMethod = string.Empty;
            FollowStatus = $"Treasure opener '{opener.OpenerName}' from {opener.Source} ignored for {FollowProviderLabel} follow; source is not direct portal chat or interaction witness.";
            SetBmraiCommandSkipped(opener, $"{FollowProviderLabel} command not sent: source {opener.Source} is not direct portal chat or interaction witness.");
            SetFollowerMovementAuthority(false, FollowStatus);
            return false;
        }

        var followKey = BuildFollowKey(opener);
        if (!TryApplyViaFollowSlash(opener, out var commandResult))
        {
            FollowApplied = false;
            FollowMethod = commandResult.Method;
            FollowStatus = $"{FollowProviderLabel} name follow not applied for direct treasure opener '{opener.OpenerName}' from {opener.Source}. {commandResult.Status}";
            RecordBmraiFollowCommand(opener, commandResult);
            SetFollowerMovementAuthority(false, FollowStatus);
            log.Information($"[ADS] {FollowStatus}");
            return false;
        }

        MarkBmraiApplied(opener, followKey, commandResult, GetAcceptedFollowDutyKey(context), reason);
        return true;
    }

    public bool ReapplyDirectTreasurePortalOpenerIfNeeded(
        TreasurePortalOpenerSnapshot opener,
        DutyContextSnapshot context,
        string reason)
    {
        if (!TryGetDirectTreasureFollowReapplyReason(opener, context, out var reapplyReason))
            return false;

        var dutyKey = GetAcceptedFollowDutyKey(context);
        var reapplyKey = $"{dutyKey}:{BuildFollowKey(opener)}:{reapplyReason}";
        if (string.Equals(reapplyKey, lastReapplyAttemptKey, StringComparison.Ordinal))
            return false;

        lastReapplyAttemptKey = reapplyKey;
        log.Information(
            $"[ADS] Reapplying {FollowProviderLabel} treasure follow after {reason}: opener='{opener.OpenerName}', source={opener.Source}, dutyKey={dutyKey}, reason={reapplyReason}.");
        return ApplyDirectTreasurePortalOpener(opener, context, $"reapply after {reason}");
    }

    public void Update(TreasureDungeonRole role, string roleDisplayName, TreasurePortalOpenerSnapshot? opener, bool followAllowed)
    {
        if (opener is null)
        {
            FollowLeaderContentId = null;
            FollowApplied = false;
            FollowMethod = string.Empty;
            FollowStatus = "No treasure portal opener captured.";
            if (BmraiFollowCommandAccepted is null)
                SetBmraiCommandSkipped(null, $"{FollowProviderLabel} command not sent: no direct portal chat or interaction witness opener captured.");

            SetFollowerMovementAuthority(false, FollowStatus);
            return;
        }

        FollowLeaderContentId = opener.ContentId;

        if (!IsDirectBmraiSource(opener.Source))
        {
            FollowApplied = false;
            FollowMethod = string.Empty;
            FollowStatus = $"Treasure opener '{opener.OpenerName}' from {opener.Source} ignored for {FollowProviderLabel} follow; only direct portal chat or interaction witness can send {FollowProviderLabel}.";
            SetBmraiCommandSkipped(opener, $"{FollowProviderLabel} command not sent: source {opener.Source} is not direct portal chat or interaction witness.");
            SetFollowerMovementAuthority(false, FollowStatus);
            return;
        }

        var commandMatchesOpener = CommandMatchesOpener(opener);
        if (!commandMatchesOpener)
        {
            FollowApplied = false;
            FollowMethod = BmraiFollowCommandMethod;
            FollowStatus = $"Direct treasure opener '{opener.OpenerName}' from {opener.Source} has no accepted {FollowProviderLabel} follow command. {BmraiFollowCommandStatus}";
            SetFollowerMovementAuthority(false, FollowStatus);
            return;
        }

        FollowApplied = true;
        FollowMethod = BmraiFollowCommandMethod;
        FollowStatus = $"{FollowProviderLabel} name follow accepted for direct treasure opener '{opener.OpenerName}' from {opener.Source}. {BmraiFollowCommandStatus}";
        SetFollowerMovementAuthority(
            followAllowed,
            followAllowed
                ? FollowStatus
                : $"{FollowStatus} ADS movement authority inactive for role {roleDisplayName} ({role}).");
    }

    private void MarkBmraiApplied(
        TreasurePortalOpenerSnapshot opener,
        string followKey,
        BmraiFollowCommandResult commandResult,
        uint acceptedDutyKey,
        string reason)
    {
        FollowApplied = true;
        FollowMethod = commandResult.Method;
        FollowLeaderContentId = opener.ContentId;
        bmraiFollowActivated = true;
        RecordBmraiFollowCommand(opener, commandResult);
        RecordAcceptedFollowDutyKey(commandResult, acceptedDutyKey);
        TryProcessFollowCommand("/bmrai followoutofcombat on", "BMRAI", out _);

        var contentId = opener.ContentId?.ToString(CultureInfo.InvariantCulture) ?? "unresolved";
        FollowStatus = $"{FollowProviderLabel} name follow set to direct treasure opener '{opener.OpenerName}' from {opener.Source}, content id {contentId}. {commandResult.Status}";
        SetFollowerMovementAuthority(true, FollowStatus);

        var successKey = $"{FollowProviderLabel}:{followKey}:{commandResult.Method}:{commandResult.CommandText}:{commandResult.Accepted}:{acceptedDutyKey}:{reason}";
        if (string.Equals(successKey, lastLoggedSuccessKey, StringComparison.Ordinal))
            return;

        lastLoggedSuccessKey = successKey;
        log.Information(
            $"[ADS] {FollowProviderLabel} follow target commands: source={opener.Source}, opener='{opener.OpenerName}', contentId={contentId}, method={commandResult.Method}, commands='{commandResult.CommandText}', accepted={commandResult.Accepted?.ToString() ?? "not sent"}, dutyKey={acceptedDutyKey}, reason='{reason}', at={commandResult.AtUtc:O}.");
        log.Information($"[ADS] {FollowStatus}");
    }

    private void SetFollowerMovementAuthority(bool ownedByBmrai, string status)
    {
        FollowerMovementOwnedByBmrai = ownedByBmrai;
        FollowerMovementStatus = status;
    }

    private void SetBmraiCommandSkipped(TreasurePortalOpenerSnapshot? target, string status)
    {
        BmraiFollowCommandMethod = "Name";
        BmraiFollowCommandText = string.Empty;
        BmraiFollowCommandAccepted = null;
        BmraiFollowCommandAtUtc = null;
        BmraiFollowCommandStatus = status;
        BmraiFollowCommandTargetName = target?.OpenerName ?? string.Empty;
        BmraiFollowCommandTargetSlot = target?.PartySlot;
        BmraiFollowCommandTargetContentId = target?.ContentId;
        BmraiFollowCommandTargetSource = target?.Source ?? string.Empty;
        bmraiFollowCommandAcceptedDutyKey = 0;
    }

    private void RecordBmraiFollowCommand(TreasurePortalOpenerSnapshot target, BmraiFollowCommandResult result)
    {
        BmraiFollowCommandMethod = result.Method;
        BmraiFollowCommandText = result.CommandText;
        BmraiFollowCommandAccepted = result.Accepted;
        BmraiFollowCommandAtUtc = result.AtUtc;
        BmraiFollowCommandStatus = result.Status;
        BmraiFollowCommandTargetName = target.OpenerName;
        BmraiFollowCommandTargetSlot = target.PartySlot;
        BmraiFollowCommandTargetContentId = target.ContentId;
        BmraiFollowCommandTargetSource = target.Source;
        if (result.Accepted != true)
            bmraiFollowCommandAcceptedDutyKey = 0;
    }

    private void RecordAcceptedFollowDutyKey(BmraiFollowCommandResult result, uint dutyKey)
        => bmraiFollowCommandAcceptedDutyKey = result.Accepted == true ? dutyKey : 0;

    private bool TryApplyViaFollowSlash(TreasurePortalOpenerSnapshot opener, out BmraiFollowCommandResult result)
    {
        var targetName = opener.OpenerName.Trim();
        if (string.IsNullOrWhiteSpace(targetName))
        {
            result = new BmraiFollowCommandResult(
                "Name",
                string.Empty,
                null,
                DateTime.UtcNow,
                $"{FollowProviderLabel} command not sent: direct opener name from {opener.Source} is empty.");
            return false;
        }

        var bmraiResult = TryApplyProviderFollowSlash("BMRAI", "/bmrai", targetName, cleanupRejectedBmrai: true);
        var vbmResult = TryApplyProviderFollowSlash("VBM", "/vbmai", targetName, cleanupRejectedBmrai: false);
        var accepted = bmraiResult.Accepted || vbmResult.Accepted;
        var acceptedStatus = accepted ? true : bmraiResult.CommandSent || vbmResult.CommandSent ? false : (bool?)null;
        var commandText = string.Join(" | ", new[] { bmraiResult.CommandText, vbmResult.CommandText }
            .Where(command => !string.IsNullOrWhiteSpace(command)));
        var atUtc = MaxUtc(bmraiResult.AtUtc, vbmResult.AtUtc);

        if (accepted)
        {
            EnsureCleanupPending();
            result = new BmraiFollowCommandResult(
                "Name",
                commandText,
                acceptedStatus,
                atUtc,
                $"{FollowProviderLabel} name follow accepted. {bmraiResult.Status} {vbmResult.Status}");
            return true;
        }

        result = new BmraiFollowCommandResult(
            "Name",
            commandText,
            acceptedStatus,
            atUtc,
            $"{FollowProviderLabel} name follow rejected. {bmraiResult.Status} {vbmResult.Status}");
        return false;
    }

    private FollowProviderCommandResult TryApplyProviderFollowSlash(
        string providerName,
        string commandPrefix,
        string targetName,
        bool cleanupRejectedBmrai)
    {
        var nameCommand = $"{commandPrefix} follow {targetName}";
        var followOutOfCombatResult = TrySetProviderFollowOutOfCombat(providerName, commandPrefix, enabled: true);
        var commandText = JoinCommandText(followOutOfCombatResult.CommandText, nameCommand);
        if (!followOutOfCombatResult.Accepted)
        {
            return new FollowProviderCommandResult(
                commandText,
                false,
                false,
                followOutOfCombatResult.AtUtc,
                $"{providerName} target command not sent: {followOutOfCombatResult.Status}");
        }

        var nameAtUtc = DateTime.UtcNow;
        if (TryProcessFollowCommand(nameCommand, providerName, out var nameStatus))
        {
            return new FollowProviderCommandResult(
                commandText,
                true,
                true,
                nameAtUtc,
                $"{providerName} name follow accepted. {followOutOfCombatResult.Status} {nameStatus}");
        }

        if (cleanupRejectedBmrai)
        {
            TryProcessFollowCommand("/bmrai followoutofcombat off", "BMRAI", out _);
            TryProcessFollowCommand("/bmrai followcombat off", "BMRAI", out _);
        }

        return new FollowProviderCommandResult(
            commandText,
            true,
            false,
            nameAtUtc,
            $"{providerName} name follow rejected. {followOutOfCombatResult.Status} Name command status: {nameStatus}");
    }

    private FollowProviderCommandResult TrySetProviderFollowOutOfCombat(string providerName, string commandPrefix, bool enabled)
    {
        var command = $"{commandPrefix} followoutofcombat {(enabled ? "on" : "off")}";
        var atUtc = DateTime.UtcNow;
        var accepted = TryProcessFollowCommand(command, providerName, out var status);
        return new FollowProviderCommandResult(
            command,
            accepted,
            accepted,
            atUtc,
            status);
    }

    private void EnsureCleanupPending()
    {
        if (configuration.BmraiTreasureFollowCleanupPending)
            return;

        configuration.BmraiTreasureFollowCleanupPending = true;
        configuration.Save();
    }

    private string DisableBmraiTreasureFollow(string reason, bool disableBmrai)
    {
        var cleanupStatuses = new List<string>();
        TryProcessFollowCommand("/bmrai followoutofcombat off", "BMRAI", out var followOutOfCombatStatus);
        cleanupStatuses.Add(followOutOfCombatStatus);
        TryProcessFollowCommand("/bmrai followcombat off", "BMRAI", out var followCombatStatus);
        cleanupStatuses.Add(followCombatStatus);

        TryProcessFollowCommand("/vbmai followoutofcombat off", "VBM", out var vbmFollowOutOfCombatStatus);
        cleanupStatuses.Add(vbmFollowOutOfCombatStatus);
        TryProcessFollowCommand("/vbmai followcombat off", "VBM", out var vbmFollowCombatStatus);
        cleanupStatuses.Add(vbmFollowCombatStatus);

        if (disableBmrai)
        {
            TryProcessFollowCommand("/bmrai off", "BMRAI", out var bmraiOffStatus);
            cleanupStatuses.Add(bmraiOffStatus);
        }

        TryProcessFollowCommand("/vbmai follow Slot1", "VBM", out var vbmFollowSlotStatus);
        cleanupStatuses.Add(vbmFollowSlotStatus);

        var status = $" Sent cleanup commands: {string.Join(" ", cleanupStatuses)}";
        log.Information($"[ADS] Clearing ADS {FollowProviderLabel} follow after {reason}.{status}");
        return status;
    }

    private bool TryProcessFollowCommand(string command, string providerName, out string status)
    {
        try
        {
            if (commandManager.ProcessCommand(command))
            {
                status = $"{command} accepted.";
                return true;
            }

            status = $"{command} was not handled; {providerName} may be missing or unloaded.";
            return false;
        }
        catch (Exception ex)
        {
            status = $"{command} failed: {ex.Message}";
            return false;
        }
    }

    private bool TryApplyMultiboxLeaderHint(ulong? leaderContentId, out string method, out string status)
    {
        method = string.Empty;
        if (leaderContentId is not { } resolvedLeaderContentId)
        {
            status = "Multibox target hint skipped because content id is not resolved.";
            return false;
        }

        if (TryApplyViaIpc(resolvedLeaderContentId, out var ipcStatus))
        {
            method = "Multibox IPC target hint";
            status = $"{method}: {ipcStatus}";
            return true;
        }

        if (TryApplyViaReflection(resolvedLeaderContentId, out var reflectionStatus))
        {
            method = "Multibox Reflection target hint";
            status = $"{method}: {reflectionStatus}";
            return true;
        }

        status = $"Multibox target hint unavailable: {ipcStatus} Reflection fallback failed: {reflectionStatus}";
        return false;
    }

    private static string BuildFollowKey(TreasurePortalOpenerSnapshot opener)
        => $"{opener.Source}:{opener.PartySlot?.ToString(CultureInfo.InvariantCulture) ?? "none"}:{opener.ContentId?.ToString(CultureInfo.InvariantCulture) ?? "none"}:{opener.OpenerName.Trim()}";

    private bool CommandMatchesOpener(TreasurePortalOpenerSnapshot opener)
        => BmraiFollowCommandAccepted == true
           && NamesMatch(BmraiFollowCommandTargetName, opener.OpenerName)
           && string.Equals(BmraiFollowCommandTargetSource, opener.Source, StringComparison.OrdinalIgnoreCase);

    private bool TryGetDirectTreasureFollowReapplyReason(
        TreasurePortalOpenerSnapshot opener,
        DutyContextSnapshot context,
        out string reason)
    {
        reason = string.Empty;
        if (!context.PluginEnabled
            || !context.IsLoggedIn
            || !context.InInstancedDuty
            || context.IsUnsafeTransition
            || !IsDirectBmraiSource(opener.Source))
        {
            return false;
        }

        var dutyKey = GetAcceptedFollowDutyKey(context);
        if (dutyKey == 0)
            return false;

        if (!CommandMatchesOpener(opener))
        {
            reason = "accepted command target missing or stale";
            return true;
        }

        if (bmraiFollowCommandAcceptedDutyKey != dutyKey)
        {
            reason = $"accepted command duty key {bmraiFollowCommandAcceptedDutyKey.ToString(CultureInfo.InvariantCulture)} does not match current duty key {dutyKey.ToString(CultureInfo.InvariantCulture)}";
            return true;
        }

        return false;
    }

    private static uint GetAcceptedFollowDutyKey(DutyContextSnapshot? context)
    {
        if (context is null || !context.InInstancedDuty)
            return 0;

        return context.TerritoryTypeId != 0
            ? context.TerritoryTypeId
            : context.ContentFinderConditionId;
    }

    private static bool IsDirectBmraiSource(string source)
        => string.Equals(source, "PortalChat", StringComparison.OrdinalIgnoreCase)
           || source.StartsWith("InteractionWitness:", StringComparison.OrdinalIgnoreCase);

    private bool TryApplyViaIpc(ulong leaderContentId, out string status)
    {
        try
        {
            if (!TryGetActivePresetNamesViaIpc(out var activePresets, out var activeListStatus))
            {
                status = activeListStatus;
                return false;
            }

            var appliedPresets = new List<string>();
            var skippedPresets = new List<string>();
            foreach (var presetName in activePresets)
            {
                if (TryApplyTransientToPresetViaIpc(presetName, leaderContentId))
                    appliedPresets.Add(presetName);
                else
                    skippedPresets.Add(presetName);
            }

            if (appliedPresets.Count > 0)
            {
                status = skippedPresets.Count == 0
                    ? $"Applied to active preset(s): {string.Join(", ", appliedPresets)}."
                    : $"Applied to active preset(s): {string.Join(", ", appliedPresets)}. Skipped preset(s) without Multibox: {string.Join(", ", skippedPresets)}.";
                return true;
            }

            var activeStatus = activePresets.Count == 0
                ? "BossMod IPC returned no active autorotation presets."
                : $"BossMod IPC found active preset(s) [{string.Join(", ", activePresets)}], but none contained {MultiboxModuleFullName}.{MultiboxLeaderTrack}.";

            if (!TryCreateActivateAndApplyAdsPresetViaIpc(leaderContentId, activePresets.Count == 0, out var fallbackStatus))
            {
                status = $"{activeStatus} ADS Treasure Follow IPC fallback failed: {fallbackStatus}";
                return false;
            }

            status = $"{activeStatus} {fallbackStatus}";
            return true;
        }
        catch (Exception ex)
        {
            status = $"BossMod IPC unavailable or failed: {ex.Message}";
            return false;
        }
    }

    private bool TryGetActivePresetNamesViaIpc(out List<string> activePresets, out string status)
    {
        activePresets = [];
        var failures = new List<string>();

        try
        {
            activePresets = pluginInterface.GetIpcSubscriber<List<string>>("BossMod.Presets.GetActiveList").InvokeFunc() ?? [];
            status = "BossMod IPC active preset list resolved.";
            return true;
        }
        catch (Exception ex)
        {
            failures.Add($"GetActiveList failed: {ex.Message}");
        }

        try
        {
            var activePreset = pluginInterface.GetIpcSubscriber<string>("BossMod.Presets.GetActive").InvokeFunc();
            if (!string.IsNullOrWhiteSpace(activePreset))
                activePresets.Add(activePreset);
            status = "BossMod IPC single active preset resolved.";
            return true;
        }
        catch (Exception ex)
        {
            failures.Add($"GetActive failed: {ex.Message}");
        }

        status = $"BossMod IPC active preset query failed. {string.Join(" ", failures)}";
        return false;
    }

    private bool TryCreateActivateAndApplyAdsPresetViaIpc(ulong leaderContentId, bool allowExclusiveActivation, out string status)
    {
        if (!TryCreateAdsTreasureFollowPresetViaIpc(out var createStatus))
        {
            status = createStatus;
            return false;
        }

        if (!TryActivateAdsTreasureFollowPresetViaIpc(allowExclusiveActivation, out var activateStatus))
        {
            status = $"{createStatus} {activateStatus}";
            return false;
        }

        if (!TryApplyTransientToPresetViaIpc(AdsTreasureFollowPresetName, leaderContentId))
        {
            status = $"{createStatus} {activateStatus} Created preset did not accept transient {MultiboxLeaderTrack}.";
            return false;
        }

        status = $"{createStatus} {activateStatus} Applied transient {MultiboxLeaderTrack} to {AdsTreasureFollowPresetName}.";
        return true;
    }

    private bool TryCreateAdsTreasureFollowPresetViaIpc(out string status)
    {
        try
        {
            var createPreset = pluginInterface.GetIpcSubscriber<string, bool, bool>("BossMod.Presets.Create");
            if (!createPreset.InvokeFunc(AdsTreasureFollowPresetJson, true))
            {
                status = $"{AdsTreasureFollowPresetName} preset create IPC returned false.";
                return false;
            }

            status = $"{AdsTreasureFollowPresetName} preset created/updated.";
            return true;
        }
        catch (Exception ex)
        {
            status = $"Preset create IPC unavailable or failed: {ex.Message}";
            return false;
        }
    }

    private bool TryActivateAdsTreasureFollowPresetViaIpc(bool allowExclusiveActivation, out string status)
    {
        var failures = new List<string>();
        try
        {
            var activatePreset = pluginInterface.GetIpcSubscriber<string, bool, bool>("BossMod.Presets.Activate");
            if (activatePreset.InvokeFunc(AdsTreasureFollowPresetName, false))
            {
                status = $"{AdsTreasureFollowPresetName} activated non-exclusively.";
                return true;
            }

            failures.Add("Activate(name, false) returned false");
        }
        catch (Exception ex)
        {
            failures.Add($"Activate(name, false) failed: {ex.Message}");
        }

        try
        {
            var activatePreset = pluginInterface.GetIpcSubscriber<string, bool>("BossMod.Presets.Activate");
            if (activatePreset.InvokeFunc(AdsTreasureFollowPresetName))
            {
                status = $"{AdsTreasureFollowPresetName} activated.";
                return true;
            }

            failures.Add("Activate(name) returned false");
        }
        catch (Exception ex)
        {
            failures.Add($"Activate(name) failed: {ex.Message}");
        }

        if (allowExclusiveActivation)
        {
            try
            {
                var setActivePreset = pluginInterface.GetIpcSubscriber<string, bool>("BossMod.Presets.SetActive");
                if (setActivePreset.InvokeFunc(AdsTreasureFollowPresetName))
                {
                    status = $"{AdsTreasureFollowPresetName} set as sole active preset because no active preset was present.";
                    return true;
                }

                failures.Add("SetActive(name) returned false");
            }
            catch (Exception ex)
            {
                failures.Add($"SetActive(name) failed: {ex.Message}");
            }
        }

        status = $"No non-exclusive activation IPC succeeded. {string.Join(" ", failures)}";
        return false;
    }

    private bool TryApplyTransientToPresetViaIpc(string presetName, ulong leaderContentId)
    {
        var addTransientStrategy = pluginInterface.GetIpcSubscriber<string, string, string, string, bool>("BossMod.Presets.AddTransientStrategy");
        var value = leaderContentId.ToString(CultureInfo.InvariantCulture);
        foreach (var moduleTypeName in MultiboxModuleTypeNames)
        {
            if (addTransientStrategy.InvokeFunc(presetName, moduleTypeName, MultiboxLeaderTrack, value))
                return true;
        }

        return false;
    }

    private bool TryApplyViaReflection(ulong leaderContentId, out string status)
    {
        status = string.Empty;
        try
        {
            if (!TryResolveReflectionContext(out var context, out status))
                return false;

            var activePresets = GetActivePresetsByReflection(context.Rotation);
            var activePresetNames = activePresets.Select(GetPresetName).ToList();
            var appliedPresetNames = new List<string>();
            foreach (var preset in activePresets)
            {
                if (preset is null)
                    continue;

                if (TryApplyToPresetByReflection(preset, leaderContentId))
                    appliedPresetNames.Add(GetPresetName(preset));
            }

            if (appliedPresetNames.Count > 0)
            {
                DirtyActiveModules(context.Rotation);
                status = $"Applied to active preset(s): {string.Join(", ", appliedPresetNames)}.";
                return true;
            }

            var activeStatus = activePresetNames.Count == 0
                ? "BossMod/VBM has no active autorotation presets."
                : $"BossMod/VBM active preset(s) [{string.Join(", ", activePresetNames)}] did not contain {MultiboxModuleFullName}.";

            if (!TryEnsureActivateAndApplyAdsPresetByReflection(context, leaderContentId, activePresetNames.Count == 0, out var fallbackStatus))
            {
                status = $"{activeStatus} ADS Treasure Follow reflection fallback failed: {fallbackStatus}";
                return false;
            }

            status = $"{activeStatus} {fallbackStatus}";
            return true;
        }
        catch (Exception ex)
        {
            status = ex.Message;
            return false;
        }
    }

    private bool TryResolveReflectionContext(out BossModReflectionContext context, out string status)
    {
        context = default!;
        var exposedPlugin = FindBossModPlugin();
        if (exposedPlugin is null)
        {
            status = "BossMod/VBM plugin not found.";
            return false;
        }

        if (!exposedPlugin.IsLoaded)
        {
            status = "BossMod/VBM plugin is installed but not loaded.";
            return false;
        }

        var localPlugin = GetLocalPlugin(exposedPlugin);
        if (localPlugin is null)
        {
            status = "Could not reflect Dalamud LocalPlugin from BossMod/VBM.";
            return false;
        }

        var instance = GetFieldValue(localPlugin, "instance");
        if (instance is null)
        {
            status = "BossMod/VBM plugin instance not available yet.";
            return false;
        }

        var rotation = GetFieldValue(instance, "_rotation");
        if (rotation is null)
        {
            status = "BossMod/VBM _rotation manager not initialized yet.";
            return false;
        }

        var assembly = instance.GetType().Assembly;
        var presetDatabase = GetPresetDatabase(rotation);
        context = new BossModReflectionContext(exposedPlugin, instance, rotation, assembly, presetDatabase);
        status = string.Empty;
        return true;
    }

    private static List<object> GetActivePresetsByReflection(object rotation)
    {
        var presets = new List<object>();
        AddActivePresetsFromMember(rotation, "Presets", presets);
        AddActivePresetsFromMember(rotation, "ActivePresets", presets);
        AddActivePresetsFromMember(rotation, "ActivePresetList", presets);

        var singlePreset = GetMemberValue(rotation, "Preset");
        if (singlePreset is not null && !ContainsPresetReferenceOrName(presets, singlePreset))
            presets.Add(singlePreset);

        return presets;
    }

    private static void AddActivePresetsFromMember(object rotation, string name, List<object> presets)
    {
        var member = GetMemberValue(rotation, name);
        if (member is string || member is not IEnumerable enumerable)
            return;

        foreach (var preset in enumerable)
        {
            if (preset is not null && !ContainsPresetReferenceOrName(presets, preset))
                presets.Add(preset);
        }
    }

    private static bool TryEnsureActivateAndApplyAdsPresetByReflection(
        BossModReflectionContext context,
        ulong leaderContentId,
        bool allowExclusiveActivation,
        out string status)
    {
        if (!TryEnsureAdsPresetByReflection(context, out var preset, out var ensureStatus))
        {
            status = ensureStatus;
            return false;
        }

        if (!TryActivateAdsPresetByReflection(context, preset, allowExclusiveActivation, out var activateStatus))
        {
            status = $"{ensureStatus} {activateStatus}";
            return false;
        }

        if (!TryApplyToPresetByReflection(preset, leaderContentId))
        {
            status = $"{ensureStatus} {activateStatus} Could not set transient {MultiboxLeaderTrack} on {AdsTreasureFollowPresetName}.";
            return false;
        }

        DirtyActiveModules(context.Rotation);
        status = $"{ensureStatus} {activateStatus} Applied transient {MultiboxLeaderTrack} to {AdsTreasureFollowPresetName}.";
        return true;
    }

    private static bool TryEnsureAdsPresetByReflection(BossModReflectionContext context, out object preset, out string status)
    {
        preset = null!;
        var existing = FindPresetByName(context.PresetDatabase, AdsTreasureFollowPresetName);
        if (existing is not null && PresetContainsModule(existing, MultiboxModuleFullName))
        {
            preset = existing;
            status = $"{AdsTreasureFollowPresetName} preset already contains Multibox.";
            return true;
        }

        if (!TryCreateAdsPresetByReflection(context.Assembly, out var replacement, out var createStatus))
        {
            status = createStatus;
            return false;
        }

        if (context.PresetDatabase is null)
        {
            preset = replacement;
            status = $"{createStatus} Preset database unavailable; using in-memory preset.";
            return true;
        }

        var userPresets = GetMemberValue(context.PresetDatabase, "UserPresets") as IList;
        var userPresetIndex = FindPresetIndex(userPresets, AdsTreasureFollowPresetName);
        if (TryModifyPresetDatabase(context.PresetDatabase, userPresetIndex, replacement))
        {
            preset = FindPresetByName(context.PresetDatabase, AdsTreasureFollowPresetName) ?? replacement;
            status = $"{createStatus} Preset database {(userPresetIndex >= 0 ? "updated" : "created")}.";
            return true;
        }

        if (userPresets is not null)
        {
            if (userPresetIndex >= 0)
                userPresets[userPresetIndex] = replacement;
            else
                userPresets.Add(replacement);

            preset = replacement;
            status = $"{createStatus} Preset injected into user preset list.";
            return true;
        }

        preset = replacement;
        status = $"{createStatus} User preset list unavailable; using in-memory preset.";
        return true;
    }

    private static bool TryCreateAdsPresetByReflection(Assembly assembly, out object preset, out string status)
    {
        preset = null!;
        var presetType = assembly.GetType("BossMod.Autorotation.Preset");
        if (presetType is null)
        {
            status = "BossMod.Autorotation.Preset type not found.";
            return false;
        }

        if (!TryResolveMultiboxRegistryEntry(assembly, out var moduleType, out var definition, out var builder, out status))
            return false;

        var createdPreset = Activator.CreateInstance(presetType, AdsTreasureFollowPresetName);
        if (createdPreset is null)
        {
            status = $"Could not create {AdsTreasureFollowPresetName} preset instance.";
            return false;
        }

        var addModule = FindMethod(presetType, "AddModule", 3);
        if (addModule is null)
        {
            status = "Preset.AddModule method not found.";
            return false;
        }

        addModule.Invoke(createdPreset, [moduleType, definition, builder]);
        var module = FindPresetModule(createdPreset, MultiboxModuleFullName);
        if (module is null)
        {
            status = "Created preset did not receive Multibox module.";
            return false;
        }

        var serializedSettings = GetMemberValue(module, "SerializedSettings") as IList;
        var zeroLeaderSetting = CreateMultiboxLeaderSetting(assembly, 0);
        if (serializedSettings is null || zeroLeaderSetting is null)
        {
            status = "Could not add serialized Leader=0 setting to ADS Treasure Follow.";
            return false;
        }

        serializedSettings.Add(zeroLeaderSetting);
        preset = createdPreset;
        status = $"{AdsTreasureFollowPresetName} preset built in memory.";
        return true;
    }

    private static bool TryResolveMultiboxRegistryEntry(
        Assembly assembly,
        out Type moduleType,
        out object definition,
        out object builder,
        out string status)
    {
        moduleType = null!;
        definition = null!;
        builder = null!;

        var registryType = assembly.GetType("BossMod.Autorotation.RotationModuleRegistry");
        var modules = registryType is null ? null : GetStaticFieldValue(registryType, "Modules") as IDictionary;
        if (modules is null)
        {
            status = "RotationModuleRegistry.Modules not available.";
            return false;
        }

        foreach (DictionaryEntry entry in modules)
        {
            if (entry.Key is not Type type
                || !string.Equals(type.FullName, MultiboxModuleFullName, StringComparison.Ordinal)
                || entry.Value is null)
            {
                continue;
            }

            var resolvedDefinition = GetMemberValue(entry.Value, "Definition");
            var resolvedBuilder = GetMemberValue(entry.Value, "Builder");
            if (resolvedDefinition is null || resolvedBuilder is null)
            {
                status = "Multibox registry entry is missing Definition or Builder.";
                return false;
            }

            moduleType = type;
            definition = resolvedDefinition;
            builder = resolvedBuilder;
            status = string.Empty;
            return true;
        }

        status = $"{MultiboxModuleFullName} registry entry not found.";
        return false;
    }

    private static bool TryActivateAdsPresetByReflection(
        BossModReflectionContext context,
        object preset,
        bool allowExclusiveActivation,
        out string status)
    {
        if (TryGetActivePresetList(context.Rotation, out var activePresetList, out var listName))
        {
            if (!ContainsPresetReferenceOrName(activePresetList.Cast<object?>().Where(x => x is not null).Cast<object>(), preset))
                activePresetList.Add(preset);
            status = $"{AdsTreasureFollowPresetName} activated through {listName}.";
            return true;
        }

        var currentPreset = GetMemberValue(context.Rotation, "Preset");
        if (currentPreset is null)
        {
            if (!TrySetMember(context.Rotation, "Preset", preset))
            {
                status = "Single active Preset member is not writable.";
                return false;
            }

            status = $"{AdsTreasureFollowPresetName} set as sole active preset because no active preset was present.";
            return true;
        }

        if (NamesMatch(GetPresetName(currentPreset), AdsTreasureFollowPresetName))
        {
            status = $"{AdsTreasureFollowPresetName} is already the active preset.";
            return true;
        }

        if (!allowExclusiveActivation)
        {
            status = $"No non-exclusive active preset collection was found; preserving active preset '{GetPresetName(currentPreset)}'.";
            return false;
        }

        if (!TrySetMember(context.Rotation, "Preset", preset))
        {
            status = "Single active Preset member is not writable.";
            return false;
        }

        status = $"{AdsTreasureFollowPresetName} set as sole active preset because no active preset was present.";
        return true;
    }

    private static bool TryGetActivePresetList(object rotation, out IList activePresetList, out string memberName)
    {
        foreach (var name in new[] { "Presets", "ActivePresets", "ActivePresetList" })
        {
            if (GetMemberValue(rotation, name) is IList list)
            {
                activePresetList = list;
                memberName = name;
                return true;
            }
        }

        activePresetList = null!;
        memberName = string.Empty;
        return false;
    }

    private static object? GetPresetDatabase(object rotation)
    {
        var database = GetMemberValue(rotation, "Database");
        return database is null ? null : GetMemberValue(database, "Presets");
    }

    private static object? FindPresetByName(object? presetDatabase, string presetName)
    {
        if (presetDatabase is null)
            return null;

        foreach (var memberName in new[] { "AllPresets", "UserPresets", "DefaultPresets" })
        {
            if (GetMemberValue(presetDatabase, memberName) is not IEnumerable presets)
                continue;

            foreach (var preset in presets)
            {
                if (preset is not null && NamesMatch(GetPresetName(preset), presetName))
                    return preset;
            }
        }

        return null;
    }

    private static int FindPresetIndex(IList? presets, string presetName)
    {
        if (presets is null)
            return -1;

        for (var i = 0; i < presets.Count; i++)
        {
            var preset = presets[i];
            if (preset is not null && NamesMatch(GetPresetName(preset), presetName))
                return i;
        }

        return -1;
    }

    private static bool TryModifyPresetDatabase(object presetDatabase, int index, object preset)
    {
        var modify = FindMethod(presetDatabase.GetType(), "Modify", 2);
        if (modify is null)
            return false;

        try
        {
            modify.Invoke(presetDatabase, [index, preset]);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool PresetContainsModule(object preset, string moduleFullName)
        => FindPresetModule(preset, moduleFullName) is not null;

    private static object? FindPresetModule(object preset, string moduleFullName)
    {
        if (GetMemberValue(preset, "Modules") is not IEnumerable modules)
            return null;

        foreach (var moduleSettings in modules)
        {
            if (moduleSettings is null)
                continue;

            var moduleType = GetMemberValue(moduleSettings, "Type") as Type;
            if (moduleType is not null && string.Equals(moduleType.FullName, moduleFullName, StringComparison.Ordinal))
                return moduleSettings;
        }

        return null;
    }

    private static string GetPresetName(object preset)
        => GetMemberValue(preset, "Name")?.ToString() ?? "<unnamed>";

    private static bool ContainsPresetReferenceOrName(IEnumerable<object> presets, object preset)
        => presets.Any(existing => ReferenceEquals(existing, preset) || NamesMatch(GetPresetName(existing), GetPresetName(preset)));

    private static void DirtyActiveModules(object rotation)
    {
        try
        {
            FindMethod(rotation.GetType(), "DirtyActiveModules", 1)?.Invoke(rotation, [true]);
        }
        catch
        {
            // Best-effort only; current BMR builds read transient preset values without a rebuild.
        }
    }

    private static bool TryApplyToPresetByReflection(object preset, ulong leaderContentId)
    {
        var modules = GetMemberValue(preset, "Modules") as IEnumerable;
        if (modules is null)
            return false;

        foreach (var moduleSettings in modules)
        {
            if (moduleSettings is null)
                continue;

            var moduleType = GetMemberValue(moduleSettings, "Type") as Type;
            if (moduleType is null
                || !string.Equals(moduleType.FullName, MultiboxModuleFullName, StringComparison.Ordinal))
            {
                continue;
            }

            var transientSettings = GetMemberValue(moduleSettings, "TransientSettings") as IList;
            if (transientSettings is null)
                return false;

            var setting = CreateMultiboxLeaderSetting(moduleType.Assembly, leaderContentId);
            if (setting is null)
                return false;

            var replaced = false;
            for (var i = 0; i < transientSettings.Count; i++)
            {
                var existing = transientSettings[i];
                if (existing is null
                    || Convert.ToInt32(GetMemberValue(existing, "Track"), CultureInfo.InvariantCulture) != 0)
                {
                    continue;
                }

                transientSettings[i] = setting;
                replaced = true;
                break;
            }

            if (!replaced)
                transientSettings.Add(setting);

            return true;
        }

        return false;
    }

    private static object? CreateMultiboxLeaderSetting(Assembly assembly, ulong leaderContentId)
    {
        var strategyValueType = assembly.GetType("BossMod.Autorotation.StrategyValueInt");
        var moduleSettingType = assembly.GetType("BossMod.Autorotation.Preset+ModuleSetting");
        var modifierType = assembly.GetType("BossMod.Autorotation.Preset+Modifier");
        if (strategyValueType is null || moduleSettingType is null || modifierType is null)
            return null;

        var strategyValue = Activator.CreateInstance(strategyValueType);
        if (strategyValue is null || !TrySetMember(strategyValue, "Value", unchecked((long)leaderContentId)))
            return null;

        var noModifier = Enum.ToObject(modifierType, 0);
        return Activator.CreateInstance(moduleSettingType, noModifier, 0, strategyValue);
    }

    private IExposedPlugin? FindBossModPlugin()
    {
        foreach (var plugin in pluginInterface.InstalledPlugins)
        {
            if (LooksLikeBossMod(plugin))
                return plugin;
        }

        return null;
    }

    private static bool LooksLikeBossMod(IExposedPlugin plugin)
    {
        var name = plugin.Name ?? string.Empty;
        var internalName = plugin.InternalName ?? string.Empty;

        return string.Equals(name, "BossMod", StringComparison.OrdinalIgnoreCase)
               || string.Equals(name, "Boss Mod", StringComparison.OrdinalIgnoreCase)
               || string.Equals(name, "BossMod Reborn", StringComparison.OrdinalIgnoreCase)
               || string.Equals(name, "BossModReborn", StringComparison.OrdinalIgnoreCase)
               || string.Equals(internalName, "BossMod", StringComparison.OrdinalIgnoreCase)
               || string.Equals(internalName, "BossModReborn", StringComparison.OrdinalIgnoreCase)
               || string.Equals(internalName, "BossmodReborn", StringComparison.OrdinalIgnoreCase)
               || (name.Contains("BossMod", StringComparison.OrdinalIgnoreCase)
                   && name.Contains("Reborn", StringComparison.OrdinalIgnoreCase));
    }

    private static object? GetLocalPlugin(IExposedPlugin exposedPlugin)
    {
        var type = exposedPlugin.GetType();
        var field = FindField(type, "<plugin>P")
                    ?? FindField(type, "plugin")
                    ?? FindFields(type).FirstOrDefault(fieldInfo => fieldInfo.FieldType.FullName?.Contains("LocalPlugin", StringComparison.Ordinal) == true);
        return field?.GetValue(exposedPlugin);
    }

    private static object? GetFieldValue(object instance, string name)
        => FindField(instance.GetType(), name)?.GetValue(instance);

    private static object? GetStaticFieldValue(Type type, string name)
        => FindField(type, name)?.GetValue(null);

    private static object? GetMemberValue(object instance, string name)
    {
        var type = instance.GetType();
        var field = FindField(type, name);
        if (field is not null)
            return field.GetValue(instance);

        var property = FindProperty(type, name);
        return property?.GetValue(instance);
    }

    private static bool TrySetMember(object instance, string name, object value)
    {
        var type = instance.GetType();
        var field = FindField(type, name);
        if (field is not null)
        {
            field.SetValue(instance, value);
            return true;
        }

        var property = FindProperty(type, name);
        if (property?.CanWrite == true)
        {
            property.SetValue(instance, value);
            return true;
        }

        return false;
    }

    private static bool NamesMatch(string left, string right)
        => string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string JoinCommandText(params string[] commands)
        => string.Join(" | ", commands.Where(command => !string.IsNullOrWhiteSpace(command)));

    private static DateTime MaxUtc(DateTime left, DateTime right)
        => left >= right ? left : right;

    private static FieldInfo? FindField(Type type, string name)
        => FindFields(type).FirstOrDefault(field => string.Equals(field.Name, name, StringComparison.Ordinal));

    private static IEnumerable<FieldInfo> FindFields(Type type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            foreach (var field in current.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                yield return field;
        }
    }

    private static PropertyInfo? FindProperty(Type type, string name)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            var property = current.GetProperty(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (property is not null)
                return property;
        }

        return null;
    }

    private static MethodInfo? FindMethod(Type type, string name, int parameterCount)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            foreach (var method in current.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (string.Equals(method.Name, name, StringComparison.Ordinal)
                    && method.GetParameters().Length == parameterCount)
                {
                    return method;
                }
            }
        }

        return null;
    }

    private readonly record struct BmraiFollowCommandResult(
        string Method,
        string CommandText,
        bool? Accepted,
        DateTime AtUtc,
        string Status);

    private readonly record struct FollowProviderCommandResult(
        string CommandText,
        bool CommandSent,
        bool Accepted,
        DateTime AtUtc,
        string Status);

    private sealed record BossModReflectionContext(
        IExposedPlugin ExposedPlugin,
        object Instance,
        object Rotation,
        Assembly Assembly,
        object? PresetDatabase);
}
