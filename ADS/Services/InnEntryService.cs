using System.Globalization;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Command;
using Dalamud.Plugin.Services;

namespace ADS.Services;

public sealed class InnEntryService
{
    private enum InnEntryState
    {
        Idle,
        MovingToNpc,
        WaitingForMenu,
        WaitingForZone,
    }

    private const float SearchRadiusYalms = 30.0f;
    private const float InteractRadiusYalms = 3.0f;
    private static readonly TimeSpan MoveRetryCooldown = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan InteractRetryCooldown = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MenuRetryCooldown = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan ZoneWaitTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan OverallTimeout = TimeSpan.FromSeconds(90);

    private static readonly HashSet<string> KnownInnNpcNames = new(StringComparer.Ordinal)
    {
        "Antoinaut",
        "Otopa Pottopa",
        "Mytesyn",
        "Bamponcet",
        "Ushitora",
        "Manager of Suites",
        "Ojika Tsunjika",
        "Peshekwa",
    };

    private readonly IDataManager dataManager;
    private readonly IObjectTable objectTable;
    private readonly ITargetManager targetManager;
    private readonly ICommandManager commandManager;
    private readonly IClientState clientState;
    private readonly ICondition condition;
    private readonly IPluginLog log;

    private InnEntryState state = InnEntryState.Idle;
    private string targetNpcName = string.Empty;
    private DateTime startedAtUtc = DateTime.MinValue;
    private DateTime stateStartedAtUtc = DateTime.MinValue;
    private DateTime lastMoveCommandUtc = DateTime.MinValue;
    private DateTime lastInteractUtc = DateTime.MinValue;
    private DateTime lastMenuClickUtc = DateTime.MinValue;

    public InnEntryService(
        IDataManager dataManager,
        IObjectTable objectTable,
        ITargetManager targetManager,
        ICommandManager commandManager,
        IClientState clientState,
        ICondition condition,
        IPluginLog log)
    {
        this.dataManager = dataManager;
        this.objectTable = objectTable;
        this.targetManager = targetManager;
        this.commandManager = commandManager;
        this.clientState = clientState;
        this.condition = condition;
        this.log = log;
    }

    public bool IsRunning => state != InnEntryState.Idle;
    public string StatusMessage { get; private set; } = "Idle";

    public bool StartManualEntry()
    {
        if (!clientState.IsLoggedIn || objectTable.LocalPlayer == null)
        {
            StatusMessage = "Enter inn requires a logged-in character.";
            return false;
        }

        if (IsRunning)
            Cancel("manual restart");

        if (GameInteractionHelper.IsInnTerritory(dataManager, (ushort)clientState.TerritoryType))
        {
            var territoryName = GameInteractionHelper.GetTerritoryName(dataManager, clientState.TerritoryType);
            StatusMessage = $"Already inside inn territory: {territoryName}.";
            log.Information($"[ADS][Inn] /ads enterinn skipped because the player is already inside {territoryName}.");
            return true;
        }

        var npc = FindNearbyInnNpc();
        if (npc == null)
        {
            StatusMessage = $"No innkeeper found within {SearchRadiusYalms:F0}y.";
            log.Information($"[ADS][Inn] /ads enterinn found no innkeeper within {SearchRadiusYalms:F0}y.");
            return false;
        }

        targetNpcName = npc.Name.TextValue;
        startedAtUtc = DateTime.UtcNow;
        stateStartedAtUtc = startedAtUtc;
        lastMoveCommandUtc = DateTime.MinValue;
        lastInteractUtc = DateTime.MinValue;
        lastMenuClickUtc = DateTime.MinValue;

        var distance = DistanceToLocalPlayer(npc);
        if (distance <= InteractRadiusYalms)
        {
            state = InnEntryState.WaitingForMenu;
            StatusMessage = $"Interacting with innkeeper {targetNpcName}";
            TryInteract(npc);
            log.Information($"[ADS][Inn] /ads enterinn found {targetNpcName} at {distance:F1}y; interacting immediately.");
            return true;
        }

        state = InnEntryState.MovingToNpc;
        StatusMessage = $"Moving to innkeeper {targetNpcName}";
        SendMoveCommand(npc, initial: true);
        log.Information($"[ADS][Inn] /ads enterinn found {targetNpcName} at {distance:F1}y; moving into interaction range.");
        return true;
    }

    public void Update()
    {
        if (!IsRunning)
            return;

        try
        {
            if (GameInteractionHelper.IsInnTerritory(dataManager, (ushort)clientState.TerritoryType))
            {
                Complete("Entered inn territory successfully.");
                return;
            }

            if (DateTime.UtcNow - startedAtUtc > OverallTimeout)
            {
                Fail("Timed out while trying to enter the inn.");
                return;
            }

            switch (state)
            {
                case InnEntryState.MovingToNpc:
                    UpdateMovingToNpc();
                    break;
                case InnEntryState.WaitingForMenu:
                    UpdateWaitingForMenu();
                    break;
                case InnEntryState.WaitingForZone:
                    UpdateWaitingForZone();
                    break;
            }
        }
        catch (Exception ex)
        {
            Fail($"Inn entry failed: {ex.Message}");
        }
    }

    public void Cancel(string reason)
    {
        if (!IsRunning)
            return;

        StopMovement();
        log.Warning($"[ADS][Inn] /ads enterinn cancelled: {reason}");
        state = InnEntryState.Idle;
        StatusMessage = "Idle";
        targetNpcName = string.Empty;
    }

    private void UpdateMovingToNpc()
    {
        if (TryAdvanceInnDialogs())
        {
            TransitionTo(InnEntryState.WaitingForZone, "Waiting for inn zone transition");
            return;
        }

        var npc = FindTargetNpc();
        if (npc == null)
        {
            Fail($"Innkeeper {targetNpcName} is no longer nearby.");
            return;
        }

        var distance = DistanceToLocalPlayer(npc);
        if (distance <= InteractRadiusYalms)
        {
            StopMovement();
            TransitionTo(InnEntryState.WaitingForMenu, $"Interacting with {targetNpcName}");
            TryInteract(npc);
            return;
        }

        if (DateTime.UtcNow - lastMoveCommandUtc >= MoveRetryCooldown)
            SendMoveCommand(npc, initial: false);
    }

    private void UpdateWaitingForMenu()
    {
        if (TryAdvanceInnDialogs())
        {
            TransitionTo(InnEntryState.WaitingForZone, "Waiting for inn zone transition");
            return;
        }

        var npc = FindTargetNpc();
        if (npc == null)
        {
            Fail($"Innkeeper {targetNpcName} is no longer nearby.");
            return;
        }

        var distance = DistanceToLocalPlayer(npc);
        if (distance > SearchRadiusYalms + 5.0f)
        {
            Fail($"Drifted too far away from {targetNpcName} while waiting to interact.");
            return;
        }

        if (distance > InteractRadiusYalms)
        {
            TransitionTo(InnEntryState.MovingToNpc, $"Repositioning near {targetNpcName}");
            SendMoveCommand(npc, initial: true);
            return;
        }

        if (DateTime.UtcNow - lastInteractUtc >= InteractRetryCooldown)
            TryInteract(npc);
    }

    private void UpdateWaitingForZone()
    {
        if (TryAdvanceInnDialogs())
            return;

        if (condition[ConditionFlag.BetweenAreas])
            return;

        if (DateTime.UtcNow - stateStartedAtUtc < ZoneWaitTimeout)
            return;

        log.Warning($"[ADS][Inn] Zone transition did not start after selecting the inn option for {targetNpcName}; retrying interaction.");
        TransitionTo(InnEntryState.WaitingForMenu, $"Retrying {targetNpcName}");
    }

    private void TransitionTo(InnEntryState nextState, string statusMessage)
    {
        state = nextState;
        stateStartedAtUtc = DateTime.UtcNow;
        StatusMessage = statusMessage;
    }

    private bool TryAdvanceInnDialogs()
    {
        var now = DateTime.UtcNow;
        if (now - lastMenuClickUtc < MenuRetryCooldown)
            return false;

        if (GameInteractionHelper.IsAddonVisible("SelectString"))
        {
            GameInteractionHelper.FireAddonCallback("SelectString", true, 0);
            lastMenuClickUtc = now;
            log.Information($"[ADS][Inn] Selecting the first SelectString option for {targetNpcName}.");
            return true;
        }

        if (GameInteractionHelper.IsAddonVisible("SelectIconString"))
        {
            GameInteractionHelper.FireAddonCallback("SelectIconString", true, 0);
            lastMenuClickUtc = now;
            log.Information($"[ADS][Inn] Selecting the first SelectIconString option for {targetNpcName}.");
            return true;
        }

        if (GameInteractionHelper.ClickYesIfVisible(log))
        {
            lastMenuClickUtc = now;
            log.Information($"[ADS][Inn] Confirmed SelectYesno while entering the inn through {targetNpcName}.");
            return true;
        }

        return false;
    }

    private void TryInteract(IGameObject npc)
    {
        lastInteractUtc = DateTime.UtcNow;
        if (GameInteractionHelper.TryInteractWithObject(targetManager, npc, log))
            log.Information($"[ADS][Inn] Interacting with innkeeper {targetNpcName}.");
    }

    private void SendMoveCommand(IGameObject npc, bool initial)
    {
        lastMoveCommandUtc = DateTime.UtcNow;
        var command = string.Format(
            CultureInfo.InvariantCulture,
            "/vnav moveto {0:F2} {1:F2} {2:F2}",
            npc.Position.X,
            npc.Position.Y,
            npc.Position.Z);
        GameInteractionHelper.TrySendChatCommand(commandManager, command, log);
        log.Information($"[ADS][Inn] {(initial ? "Starting" : "Refreshing")} movement toward {targetNpcName} at {DistanceToLocalPlayer(npc):F1}y.");
    }

    private void StopMovement()
        => GameInteractionHelper.TrySendChatCommand(commandManager, "/vnav stop", log);

    private void Complete(string message)
    {
        StopMovement();
        log.Information($"[ADS][Inn] {message}");
        state = InnEntryState.Idle;
        StatusMessage = "Idle";
        targetNpcName = string.Empty;
    }

    private void Fail(string message)
    {
        StopMovement();
        log.Warning($"[ADS][Inn] {message}");
        state = InnEntryState.Idle;
        StatusMessage = "Idle";
        targetNpcName = string.Empty;
    }

    private IGameObject? FindTargetNpc()
    {
        if (string.IsNullOrWhiteSpace(targetNpcName))
            return null;

        IGameObject? nearest = null;
        var nearestDistance = float.MaxValue;
        foreach (var obj in objectTable)
        {
            if (obj == null || obj.ObjectKind != ObjectKind.EventNpc)
                continue;

            if (!string.Equals(obj.Name.TextValue, targetNpcName, StringComparison.Ordinal))
                continue;

            var distance = DistanceToLocalPlayer(obj);
            if (distance >= nearestDistance)
                continue;

            nearest = obj;
            nearestDistance = distance;
        }

        return nearest;
    }

    private IGameObject? FindNearbyInnNpc()
    {
        IGameObject? nearest = null;
        var nearestDistance = float.MaxValue;
        foreach (var obj in objectTable)
        {
            if (obj == null || obj.ObjectKind != ObjectKind.EventNpc)
                continue;

            var name = obj.Name.TextValue;
            if (!KnownInnNpcNames.Contains(name))
                continue;

            var distance = DistanceToLocalPlayer(obj);
            if (distance > SearchRadiusYalms || distance >= nearestDistance)
                continue;

            nearest = obj;
            nearestDistance = distance;
        }

        return nearest;
    }

    private float DistanceToLocalPlayer(IGameObject obj)
    {
        var player = objectTable.LocalPlayer;
        return player == null ? float.MaxValue : Vector3.Distance(player.Position, obj.Position);
    }
}
