using System.Globalization;
using ADS.Models;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Command;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;

namespace ADS.Services;

public sealed unsafe class TreasureFollowerAutoMoveAssistService
{
    private const float TriggerDistanceXz = 30.0f;
    private const float ResetDistanceXz = 25.0f;
    private const string AutoMoveCommand = "/automove";

    private readonly IObjectTable objectTable;
    private readonly IPartyList partyList;
    private readonly ICommandManager commandManager;
    private readonly IPluginLog log;

    private string currentTargetKey = string.Empty;
    private string latchedWindowKey = string.Empty;

    public TreasureFollowerAutoMoveAssistService(
        IObjectTable objectTable,
        IPartyList partyList,
        ICommandManager commandManager,
        IPluginLog log)
    {
        this.objectTable = objectTable;
        this.partyList = partyList;
        this.commandManager = commandManager;
        this.log = log;
    }

    public string Status { get; private set; } = "Inactive: waiting for treasure follower BMRAI/VBM follow.";

    public string TargetName { get; private set; } = string.Empty;

    public float? DistanceXz { get; private set; }

    public DateTime? CommandSentAtUtc { get; private set; }

    public void Update(
        DutyContextSnapshot context,
        TreasureDungeonRole role,
        bool followerMovementOwnedByBmrai,
        TreasurePortalOpenerSnapshot? opener)
    {
        if (!context.PluginEnabled || !context.IsLoggedIn)
        {
            ResetInactive("Inactive: plugin disabled or character not logged in.");
            return;
        }

        if (role != TreasureDungeonRole.Follower)
        {
            ResetInactive($"Inactive: treasure role is {role}.");
            return;
        }

        if (!IsSupportedTreasureDutyContext(context))
        {
            ResetInactive("Inactive: outside supported treasure dungeon duty.");
            return;
        }

        if (context.InCombat)
        {
            ResetInactive("Inactive: combat active; automove assist reset.");
            return;
        }

        if (context.IsUnsafeTransition)
        {
            ResetInactive("Inactive: unsafe transition active; automove assist reset.");
            return;
        }

        if (opener is null)
        {
            ResetInactive("Inactive: no direct treasure portal opener follow target.");
            return;
        }

        if (!TryResolveTarget(opener, out var target, out var targetSource) || target is null)
        {
            ResetTarget(opener.OpenerName, $"Inactive: opener '{FormatName(opener.OpenerName)}' is not resolved to a live object.");
            return;
        }

        var targetKey = BuildTargetKey(context, opener, target);
        if (!string.Equals(targetKey, currentTargetKey, StringComparison.Ordinal))
        {
            currentTargetKey = targetKey;
            latchedWindowKey = string.Empty;
            CommandSentAtUtc = null;
        }

        TargetName = FormatName(opener.OpenerName);
        DistanceXz = GetDistanceXz(objectTable.LocalPlayer, target);
        if (DistanceXz is not { } distanceXz)
        {
            Status = $"Inactive: player or target position unavailable for opener '{TargetName}'.";
            return;
        }

        if (!followerMovementOwnedByBmrai)
        {
            Status = $"Inactive: BMRAI/VBM does not own treasure follower movement for opener '{TargetName}' ({distanceXz:0.0}y XZ).";
            return;
        }

        var windowKey = $"{targetKey}:far";
        if (distanceXz <= ResetDistanceXz)
        {
            latchedWindowKey = string.Empty;
            CommandSentAtUtc = null;
            Status = $"Ready: opener '{TargetName}' within reset distance ({distanceXz:0.0}y XZ <= {ResetDistanceXz:0.0}y).";
            return;
        }

        if (distanceXz <= TriggerDistanceXz)
        {
            Status = string.Equals(latchedWindowKey, windowKey, StringComparison.Ordinal)
                ? $"Latched: opener '{TargetName}' is {distanceXz:0.0}y XZ; waiting for <= {ResetDistanceXz:0.0}y reset."
                : $"Ready: opener '{TargetName}' is {distanceXz:0.0}y XZ; trigger is > {TriggerDistanceXz:0.0}y.";
            return;
        }

        if (string.Equals(latchedWindowKey, windowKey, StringComparison.Ordinal))
        {
            Status = $"Latched: one automove assist attempt already handled for opener '{TargetName}' at {distanceXz:0.0}y XZ.";
            return;
        }

        latchedWindowKey = windowKey;
        if (!TryIsAutoRunning(out var isAutoRunning))
        {
            Status = $"Latched: autorun state unavailable for opener '{TargetName}' at {distanceXz:0.0}y XZ; ADS did not toggle {AutoMoveCommand}.";
            log.Warning(
                $"[ADS] Treasure follower automove assist latched without command because autorun state was unavailable: opener='{TargetName}', source={opener.Source}, resolve={targetSource}, distanceXz={distanceXz.ToString("0.0", CultureInfo.InvariantCulture)}, duty={BuildDutyKey(context)}.");
            return;
        }

        if (isAutoRunning)
        {
            Status = $"Latched: autorun already active for opener '{TargetName}' at {distanceXz:0.0}y XZ; ADS did not toggle {AutoMoveCommand}.";
            log.Information(
                $"[ADS] Treasure follower automove assist latched without command because autorun is already active: opener='{TargetName}', source={opener.Source}, resolve={targetSource}, distanceXz={distanceXz.ToString("0.0", CultureInfo.InvariantCulture)}, duty={BuildDutyKey(context)}.");
            return;
        }

        var sentAtUtc = DateTime.UtcNow;
        if (GameInteractionHelper.TrySendChatCommand(commandManager, AutoMoveCommand, log))
        {
            CommandSentAtUtc = sentAtUtc;
            Status = $"Sent {AutoMoveCommand}: opener '{TargetName}' was {distanceXz:0.0}y XZ away.";
            log.Information(
                $"[ADS] Treasure follower automove assist sent {AutoMoveCommand}: opener='{TargetName}', source={opener.Source}, resolve={targetSource}, distanceXz={distanceXz.ToString("0.0", CultureInfo.InvariantCulture)}, duty={BuildDutyKey(context)}, at={sentAtUtc:O}.");
            return;
        }

        Status = $"Latched: failed to send {AutoMoveCommand} for opener '{TargetName}' at {distanceXz:0.0}y XZ.";
        log.Warning(
            $"[ADS] Treasure follower automove assist failed to send {AutoMoveCommand}: opener='{TargetName}', source={opener.Source}, resolve={targetSource}, distanceXz={distanceXz.ToString("0.0", CultureInfo.InvariantCulture)}, duty={BuildDutyKey(context)}, at={sentAtUtc:O}.");
    }

    private void ResetInactive(string status)
    {
        currentTargetKey = string.Empty;
        latchedWindowKey = string.Empty;
        TargetName = string.Empty;
        DistanceXz = null;
        CommandSentAtUtc = null;
        Status = status;
    }

    private void ResetTarget(string targetName, string status)
    {
        currentTargetKey = string.Empty;
        latchedWindowKey = string.Empty;
        TargetName = FormatName(targetName);
        DistanceXz = null;
        CommandSentAtUtc = null;
        Status = status;
    }

    private bool TryResolveTarget(TreasurePortalOpenerSnapshot opener, out IGameObject? target, out string source)
    {
        if (TryFindObjectByGameObjectId(opener.GameObjectId, out target))
        {
            source = "gameObjectId";
            return true;
        }

        if (TryFindObjectByEntityId(opener.EntityId, out target))
        {
            source = "entityId";
            return true;
        }

        if (opener.PartySlot is { } partySlot && TryGetPartySlotObject(partySlot, out target))
        {
            source = "partySlot";
            return true;
        }

        if (TryFindPlayerByName(opener.OpenerName, out target))
        {
            source = "playerName";
            return true;
        }

        target = null;
        source = "unresolved";
        return false;
    }

    private bool TryFindObjectByGameObjectId(ulong? gameObjectId, out IGameObject? target)
    {
        target = null;
        if (gameObjectId is not { } resolvedGameObjectId || !IsValidObjectId(resolvedGameObjectId))
            return false;

        foreach (var obj in objectTable)
        {
            if (obj is not null && obj.GameObjectId == resolvedGameObjectId)
            {
                target = obj;
                return true;
            }
        }

        return false;
    }

    private bool TryFindObjectByEntityId(ulong? entityId, out IGameObject? target)
    {
        target = null;
        if (entityId is not { } resolvedEntityId || !IsValidObjectId(resolvedEntityId))
            return false;

        foreach (var obj in objectTable)
        {
            if (obj is not null && obj.EntityId == resolvedEntityId)
            {
                target = obj;
                return true;
            }
        }

        return false;
    }

    private bool TryGetPartySlotObject(int partySlot, out IGameObject? target)
    {
        target = null;
        if (partySlot <= 0 || partySlot > partyList.Length)
            return false;

        try
        {
            var member = partyList[partySlot - 1];
            target = member?.GameObject;
            return target is not null;
        }
        catch (Exception ex)
        {
            log.Debug($"[ADS] Treasure follower automove assist failed to resolve party slot {partySlot}: {ex.Message}");
            return false;
        }
    }

    private bool TryFindPlayerByName(string openerName, out IGameObject? target)
    {
        target = null;
        var normalizedOpenerName = NormalizeName(openerName);
        if (string.IsNullOrWhiteSpace(normalizedOpenerName))
            return false;

        foreach (var obj in objectTable)
        {
            if (obj is null || obj.ObjectKind != ObjectKind.Pc)
                continue;

            if (!NamesMatch(normalizedOpenerName, obj.Name.TextValue))
                continue;

            target = obj;
            return true;
        }

        return false;
    }

    private static bool IsSupportedTreasureDutyContext(DutyContextSnapshot context)
        => context.InInstancedDuty
           && (context.CurrentDuty?.Category == DutyCategory.TreasureDungeon
               || TreasureDungeonData.IsSupportedDutyTerritory(context.TerritoryTypeId));

    private static float? GetDistanceXz(IGameObject? player, IGameObject target)
    {
        if (player is null)
            return null;

        var dx = player.Position.X - target.Position.X;
        var dz = player.Position.Z - target.Position.Z;
        return MathF.Sqrt((dx * dx) + (dz * dz));
    }

    private static bool TryIsAutoRunning(out bool isAutoRunning)
    {
        try
        {
            isAutoRunning = InputManager.IsAutoRunning();
            return true;
        }
        catch
        {
            isAutoRunning = false;
            return false;
        }
    }

    private static string BuildTargetKey(DutyContextSnapshot context, TreasurePortalOpenerSnapshot opener, IGameObject target)
        => string.Join(
            ":",
            BuildDutyKey(context),
            opener.ContentId?.ToString(CultureInfo.InvariantCulture) ?? "no-content",
            opener.GameObjectId?.ToString(CultureInfo.InvariantCulture) ?? target.GameObjectId.ToString(CultureInfo.InvariantCulture),
            opener.EntityId?.ToString(CultureInfo.InvariantCulture) ?? target.EntityId.ToString(CultureInfo.InvariantCulture),
            opener.PartySlot?.ToString(CultureInfo.InvariantCulture) ?? "no-slot",
            NormalizeName(opener.OpenerName));

    private static string BuildDutyKey(DutyContextSnapshot context)
        => $"{context.TerritoryTypeId.ToString(CultureInfo.InvariantCulture)}:{context.ContentFinderConditionId.ToString(CultureInfo.InvariantCulture)}";

    private static bool IsValidObjectId(ulong value)
        => value != 0 && value != 0xE0000000UL;

    private static bool NamesMatch(string left, string right)
        => string.Equals(NormalizeName(left), NormalizeName(right), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeName(string value)
        => string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string FormatName(string value)
        => string.IsNullOrWhiteSpace(value) ? "Unknown" : NormalizeName(value);
}
