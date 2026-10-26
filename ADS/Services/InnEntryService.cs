using System.Globalization;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Command;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;

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

    private const float SearchRadiusYalms = 120.0f;
    private const float InteractRadiusYalms = 3.0f;
    private static readonly TimeSpan MoveRetryCooldown = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan InteractRetryCooldown = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MenuRetryCooldown = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan ZoneWaitTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan OverallTimeout = TimeSpan.FromSeconds(90);

    private static readonly HashSet<uint> KnownInnNpcIds =
        [1000102, 1000974, 1001976, 1011193, 1018981, 1027231, 1037293, 1048375];

    private readonly IDataManager dataManager;
    private readonly IObjectTable objectTable;
    private readonly ITargetManager targetManager;
    private readonly ICommandManager commandManager;
    private readonly IClientState clientState;
    private readonly ICondition condition;
    private readonly IPluginLog log;

    private InnEntryState state = InnEntryState.Idle;
    private string targetNpcName = string.Empty;
    private ulong targetNpcObjectId;
    private DateTime startedAtUtc = DateTime.MinValue;
    private DateTime stateStartedAtUtc = DateTime.MinValue;
    private DateTime lastMoveCommandUtc = DateTime.MinValue;
    private DateTime lastMovementProgressUtc = DateTime.MinValue;
    private Vector3 lastMovementProgressPosition;
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

    internal Func<string, (bool Visible, bool Ready)> QueryInnDialog { get; set; } = ReadInnDialog;
    internal Func<string, bool> ClickInnDialog { get; set; } = TryClickInnDialog;

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
        targetNpcObjectId = npc.GameObjectId;
        startedAtUtc = DateTime.UtcNow;
        stateStartedAtUtc = startedAtUtc;
        lastMoveCommandUtc = DateTime.MinValue;
        lastInteractUtc = DateTime.MinValue;
        lastMenuClickUtc = DateTime.MinValue;

        state = InnEntryState.WaitingForMenu;
        StatusMessage = $"Waiting for innkeeper {targetNpcName}";
        if (TryAdvanceInnDialogs())
        {
            StopMovement();
            log.Information($"[ADS][Inn] /ads enterinn continuing the current innkeeper dialog for {targetNpcName}.");
            return IsRunning;
        }

        var distance = DistanceToLocalPlayer(npc);
        if (HasReachedInteractionPoint(npc))
        {
            StopMovement();
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
        return IsRunning;
    }

    public void Update()
    {
        if (!IsRunning)
            return;

        try
        {
            if (condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51] || objectTable.LocalPlayer == null)
            {
                if (lastMoveCommandUtc != DateTime.MinValue)
                    StopMovement();
                return;
            }

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
        targetNpcObjectId = 0;
    }

    private void UpdateMovingToNpc()
    {
        if (TryAdvanceInnDialogs())
            return;

        var npc = FindTargetNpc();
        if (npc == null)
        {
            Fail($"Innkeeper {targetNpcName} is no longer nearby.");
            return;
        }

        if (HasReachedInteractionPoint(npc))
        {
            StopMovement();
            TransitionTo(InnEntryState.WaitingForMenu, $"Interacting with {targetNpcName}");
            TryInteract(npc);
            return;
        }

        SendMoveCommand(npc, initial: false);
    }

    private void UpdateWaitingForMenu()
    {
        if (TryAdvanceInnDialogs())
            return;

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

        if (!HasReachedInteractionPoint(npc))
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
        var hasDialog = TryAdvanceInnDialogs();

        if (condition[ConditionFlag.BetweenAreas])
            return;

        if (DateTime.UtcNow - stateStartedAtUtc < ZoneWaitTimeout)
            return;

        log.Warning($"[ADS][Inn] Zone transition did not start after selecting the inn option for {targetNpcName}; resuming the current dialog.");
        TransitionTo(InnEntryState.WaitingForMenu, $"Waiting for innkeeper {targetNpcName}");
        if (!hasDialog)
            UpdateWaitingForMenu();
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
        foreach (var addonName in new[] { "Talk", "SelectString", "SelectIconString", "SelectYesno" })
        {
            var dialog = QueryInnDialog(addonName);
            if (!dialog.Visible)
                continue;

            if (state == InnEntryState.MovingToNpc)
            {
                StopMovement();
                TransitionTo(InnEntryState.WaitingForMenu, $"Waiting for innkeeper {targetNpcName}");
            }

            // Visible UI still owns the wait while it becomes ready or the existing click cooldown settles.
            var target = targetManager?.Target;
            if (!IsRunning || targetNpcObjectId == 0 || target == null || target.GameObjectId != targetNpcObjectId
                || !KnownInnNpcIds.Contains(target.BaseId) || !condition[ConditionFlag.OccupiedInQuestEvent]
                || !dialog.Ready || now - lastMenuClickUtc < MenuRetryCooldown)
                return true;

            if (!ClickInnDialog(addonName))
                return true;

            lastMenuClickUtc = now;
            if (addonName == "Talk")
                log.Information($"[ADS][Inn] Advancing innkeeper Talk for {targetNpcName}.");
            else
            {
                log.Information($"[ADS][Inn] Selected the inn entry option in {addonName} for {targetNpcName}.");
                TransitionTo(InnEntryState.WaitingForZone, "Waiting for inn zone transition");
            }
            return true;
        }

        return now - lastMenuClickUtc < MenuRetryCooldown;
    }

    private static unsafe (bool Visible, bool Ready) ReadInnDialog(string addonName)
    {
        var manager = RaptureAtkUnitManager.Instance();
        var addon = manager == null ? null : manager->GetAddonByName(addonName);
        return (addon != null && addon->IsVisible, addon != null && addon->IsReady);
    }

    private static unsafe bool TryClickInnDialog(string addonName)
    {
        var manager = RaptureAtkUnitManager.Instance();
        var addon = manager == null ? null : manager->GetAddonByName(addonName);
        if (addon == null || !addon->IsVisible || !addon->IsReady)
            return false;

        if (addonName == "Talk")
        {
            new ECommons.UIHelpers.AddonMasterImplementations.AddonMaster.Talk(addon).Click();
            return true;
        }

        return addonName == "SelectYesno"
            ? GameInteractionHelper.ClickYesIfVisible()
            : GameInteractionHelper.TryFireAddonCallback(addonName, true, 0);
    }

    private void TryInteract(IGameObject npc)
    {
        lastInteractUtc = DateTime.UtcNow;
        if (GameInteractionHelper.TryInteractWithObject(targetManager, npc, log))
            log.Information($"[ADS][Inn] Interacting with innkeeper {targetNpcName}.");
    }

    private void SendMoveCommand(IGameObject npc, bool initial)
    {
        if (objectTable.LocalPlayer is not { } player)
            return;
        var now = DateTime.UtcNow;
        try
        {
            if (!Plugin.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady").InvokeFunc())
            {
                lastMovementProgressUtc = DateTime.MinValue;
                StatusMessage = $"Waiting for the navigation mesh before moving toward {targetNpcName}.";
                return;
            }
        }
        catch { /* Preserve command-based movement when readiness IPC is unavailable. */ }

        if (!initial)
        {
            if (IsMovementStalled(player.Position, now, ref lastMovementProgressPosition, ref lastMovementProgressUtc))
            {
                StopMovement();
                log.Warning($"[ADS][Inn] No movement for 3 seconds toward {targetNpcName}; cancelled navigation and will retry from the current position.");
                return;
            }
            if (now - lastMoveCommandUtc < MoveRetryCooldown)
                return;
            lastMoveCommandUtc = now;
            try
            {
                if (!ShouldSubmitMovement(
                        () => Plugin.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.SimpleMove.PathfindInProgress").InvokeFunc(),
                        () => Plugin.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning").InvokeFunc()))
                    return;
            }
            catch
            {
                // Match the repair helper's retry behavior when navigation IPC is unavailable.
            }
        }

        lastMoveCommandUtc = now;
        lastMovementProgressUtc = now;
        lastMovementProgressPosition = player.Position;
        var destination = InteractionPoint(clientState.TerritoryType, npc.BaseId, npc.Position);
        var command = string.Format(
            CultureInfo.InvariantCulture,
            "/vnav moveto {0:F2} {1:F2} {2:F2}",
            destination.X,
            destination.Y,
            destination.Z);
        if (!GameInteractionHelper.TrySendChatCommand(commandManager, command, log))
        {
            Fail($"Movement toward innkeeper {targetNpcName} was rejected.");
            return;
        }
        log.Information($"[ADS][Inn] {(initial ? "Starting" : "Refreshing")} movement toward {targetNpcName} at {DistanceToLocalPlayer(npc):F1}y.");
    }

    private void StopMovement()
    {
        StopNavigation(commandManager, log);
        lastMoveCommandUtc = DateTime.MinValue;
        lastMovementProgressUtc = DateTime.MinValue;
    }

    internal static bool IsMovementStalled(Vector3 position, DateTime now, ref Vector3 lastPosition, ref DateTime lastProgressUtc)
    {
        if (lastProgressUtc == DateTime.MinValue || Vector3.DistanceSquared(position, lastPosition) >= 0.01f)
        {
            lastPosition = position;
            lastProgressUtc = now;
        }
        return now - lastProgressUtc >= TimeSpan.FromSeconds(3);
    }

    internal static void StopNavigation(ICommandManager commandManager, IPluginLog log)
    {
        try
        {
            // A pending SimpleMove query can install a path after Path.Stop.
            if (Plugin.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.SimpleMove.PathfindInProgress").InvokeFunc())
                Plugin.PluginInterface.GetIpcSubscriber<object>("vnavmesh.Nav.PathfindCancelAll").InvokeAction();
        }
        catch (Exception ex) { log.Debug(ex, "[ADS][Inn] Pending navigation cancellation unavailable."); }
        GameInteractionHelper.TrySendChatCommand(commandManager, "/vnav stop", log);
    }

    internal static Vector3 InteractionPoint(uint territory, uint npcId, Vector3 npcPosition)
        => territory == 130 && npcId == 1001976 ? new Vector3(31.5f, 7.0f, -82.0f) : npcPosition;

    internal static bool HasReachedInteractionPoint(uint territory, uint npcId, Vector3 npcPosition, Vector3 playerPosition)
        => Vector3.Distance(playerPosition, InteractionPoint(territory, npcId, npcPosition))
            <= (territory == 130 && npcId == 1001976 ? 1.0f : InteractRadiusYalms);

    private bool HasReachedInteractionPoint(IGameObject npc)
        => objectTable.LocalPlayer is { } player
            && HasReachedInteractionPoint(clientState.TerritoryType, npc.BaseId, npc.Position, player.Position);

    internal static bool ShouldSubmitMovement(Func<bool> pathfinding, Func<bool> followingPath)
        => !pathfinding() && !followingPath();

    private void Complete(string message)
    {
        StopMovement();
        log.Information($"[ADS][Inn] {message}");
        state = InnEntryState.Idle;
        StatusMessage = "Idle";
        targetNpcName = string.Empty;
        targetNpcObjectId = 0;
    }

    private void Fail(string message)
    {
        StopMovement();
        log.Warning($"[ADS][Inn] {message}");
        state = InnEntryState.Idle;
        StatusMessage = message;
        targetNpcName = string.Empty;
        targetNpcObjectId = 0;
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

            if (!KnownInnNpcIds.Contains(obj.BaseId))
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
