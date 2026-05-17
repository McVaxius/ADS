using System.Globalization;
using System.Text;
using Dalamud.Memory;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Command;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using GameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace ADS.Services;

public static class GameInteractionHelper
{
    public enum SelectYesNoClickMethod
    {
        ButtonEvent,
        FireCallbackInt,
        LegacyCallback,
    }

    private static readonly HashSet<string> KnownInnTerritoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "The Mizzenmast",
        "Mizzenmast Inn",
        "The Roost",
        "The Hourglass",
        "The Forgotten Knight",
        "Cloud Nine",
        "Bokairo Inn",
        "The Pendants",
        "The Andron",
        "The Baldesion Annex",
        "The For'ard Cabins",
    };

    public static unsafe bool IsAddonVisible(string addonName)
    {
        try
        {
            var addon = RaptureAtkUnitManager.Instance()->GetAddonByName(addonName);
            return addon != null && addon->IsVisible;
        }
        catch
        {
            return false;
        }
    }

    public static void FireAddonCallback(string addonName, bool updateState, params object[] args)
        => TryFireAddonCallback(addonName, updateState, args);

    public static unsafe bool TryFireAddonCallback(string addonName, bool updateState, params object[] args)
    {
        try
        {
            var addon = RaptureAtkUnitManager.Instance()->GetAddonByName(addonName);
            if (addon == null || !addon->IsVisible)
                return false;

            var atkValues = new AtkValue[args.Length];
            for (var i = 0; i < args.Length; i++)
            {
                atkValues[i] = args[i] switch
                {
                    int intVal => new AtkValue { Type = AtkValueType.Int, Int = intVal },
                    uint uintVal => new AtkValue { Type = AtkValueType.UInt, UInt = uintVal },
                    bool boolVal => new AtkValue { Type = AtkValueType.Bool, Byte = (byte)(boolVal ? 1 : 0) },
                    _ => new AtkValue { Type = AtkValueType.Int, Int = Convert.ToInt32(args[i], CultureInfo.InvariantCulture) },
                };
            }

            fixed (AtkValue* ptr = atkValues)
            {
                addon->FireCallback((uint)atkValues.Length, ptr, updateState);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    public static unsafe bool ClickYesIfVisible(IPluginLog? log = null)
        => TrySelectYesNo(true, Plugin.GameGui, log);

    public static unsafe bool TryGetSelectYesNoPromptText(IGameGui gameGui, out string promptText)
    {
        promptText = string.Empty;
        try
        {
            nint addonPtr = gameGui.GetAddonByName("SelectYesno", 1);
            if (addonPtr == nint.Zero)
                return false;

            var addon = (AddonSelectYesno*)addonPtr;
            if (addon == null || !addon->AtkUnitBase.IsVisible)
                return false;

            var promptNode = addon->PromptText;
            if (promptNode == null || !promptNode->NodeText.StringPtr.HasValue)
                return false;

            var promptSeString = MemoryHelper.ReadSeStringNullTerminated(new IntPtr(promptNode->NodeText.StringPtr));
            promptText = promptSeString.TextValue?.Trim() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(promptText);
        }
        catch
        {
            promptText = string.Empty;
            return false;
        }
    }

    public static unsafe bool TrySelectYesNo(bool yes, IGameGui gameGui, IPluginLog? log = null)
        => TrySelectYesNo(yes, gameGui, SelectYesNoClickMethod.ButtonEvent, log);

    public static unsafe bool TrySelectYesNo(bool yes, IGameGui gameGui, SelectYesNoClickMethod method, IPluginLog? log = null)
    {
        try
        {
            nint addonPtr = gameGui.GetAddonByName("SelectYesno", 1);
            if (addonPtr == nint.Zero)
                return false;

            var addon = (AddonSelectYesno*)addonPtr;
            if (addon == null || !addon->AtkUnitBase.IsVisible)
                return false;

            var clicked = method switch
            {
                SelectYesNoClickMethod.ButtonEvent => TryClickSelectYesNoButton(addon, yes, log),
                SelectYesNoClickMethod.FireCallbackInt => TrySelectYesNoCallbackInt(addon, yes, log),
                SelectYesNoClickMethod.LegacyCallback => TrySelectYesNoLegacyCallback(&addon->AtkUnitBase, yes, log),
                _ => false,
            };

            if (clicked)
                log?.Information($"[ADS] Sent {(yes ? "Yes" : "No")} to SelectYesno via {DescribeSelectYesNoClickMethod(method)}.");

            return clicked;
        }
        catch (Exception ex)
        {
            log?.Warning(ex, $"[ADS] SelectYesno {DescribeSelectYesNoClickMethod(method)} failed for {(yes ? "Yes" : "No")}.");
            return false;
        }
    }

    public static string DescribeSelectYesNoClickMethod(SelectYesNoClickMethod method)
        => method switch
        {
            SelectYesNoClickMethod.ButtonEvent => "button event",
            SelectYesNoClickMethod.FireCallbackInt => "FireCallbackInt",
            SelectYesNoClickMethod.LegacyCallback => "legacy callback",
            _ => method.ToString(),
        };

    public static unsafe bool TryClickAddonNodeButton(
        string addonName,
        uint nodeId,
        IGameGui gameGui,
        IPluginLog? log = null)
    {
        try
        {
            nint addonPtr = gameGui.GetAddonByName(addonName, 1);
            if (addonPtr == nint.Zero)
                return false;

            var addon = (AtkUnitBase*)addonPtr;
            if (addon == null || !addon->IsVisible)
                return false;

            var node = addon->GetNodeById(nodeId);
            if (node == null || !node->IsVisible())
                return false;

            var atkEvent = node->AtkEventManager.Event;
            while (atkEvent != null && atkEvent->State.EventType != AtkEventType.ButtonClick)
                atkEvent = atkEvent->NextEvent;

            if (atkEvent == null)
            {
                atkEvent = node->AtkEventManager.Event;
                while (atkEvent != null
                       && atkEvent->State.EventType is not (AtkEventType.MouseClick or AtkEventType.MouseDown or AtkEventType.MouseUp))
                {
                    atkEvent = atkEvent->NextEvent;
                }
            }

            if (atkEvent == null)
                return false;

            addon->ReceiveEvent(atkEvent->State.EventType, (int)atkEvent->Param, atkEvent);
            return true;
        }
        catch (Exception ex)
        {
            log?.Warning(ex, $"[ADS] Failed to click {addonName} node {nodeId}.");
            return false;
        }
    }

    private static unsafe bool TryClickSelectYesNoButton(AddonSelectYesno* addon, bool yes, IPluginLog? log)
    {
        var button = yes ? addon->YesButton : addon->NoButton;
        if (button == null)
            return false;

        var ownerNode = button->AtkComponentBase.OwnerNode;
        if (ownerNode == null)
            return false;

        if (!button->IsEnabled)
        {
            log?.Warning($"[ADS] SelectYesno {(yes ? "Yes" : "No")} button is not enabled.");
            return false;
        }

        var eventNode = ownerNode->AtkResNode;
        var atkEvent = eventNode.AtkEventManager.Event;
        while (atkEvent != null && atkEvent->State.EventType != AtkEventType.ButtonClick)
            atkEvent = atkEvent->NextEvent;

        if (atkEvent == null)
            atkEvent = eventNode.AtkEventManager.Event;
        if (atkEvent == null)
            return false;

        addon->AtkUnitBase.ReceiveEvent(atkEvent->State.EventType, (int)atkEvent->Param, atkEvent);
        return true;
    }

    private static unsafe bool TrySelectYesNoCallbackInt(AddonSelectYesno* addon, bool yes, IPluginLog? log)
    {
        if (addon->AtkUnitBase.FireCallbackInt(yes ? 0 : 1))
            return true;

        log?.Warning($"[ADS] SelectYesno FireCallbackInt returned false for {(yes ? "Yes" : "No")}.");
        return false;
    }

    private static unsafe bool TrySelectYesNoLegacyCallback(AtkUnitBase* addon, bool yes, IPluginLog? log)
    {
        var callbackIndex = yes ? 0 : 1;
        var atkValues = stackalloc AtkValue[2];
        atkValues[0] = default;
        atkValues[1] = default;
        atkValues[0].Type = AtkValueType.Int;
        atkValues[0].Int = callbackIndex;
        atkValues[1].Type = AtkValueType.Int;
        atkValues[1].Int = 0;
        addon->FireCallback(2, atkValues);
        return true;
    }

    public static unsafe bool TryInteractWithObject(ITargetManager targetManager, IGameObject gameObject, IPluginLog? log = null)
    {
        try
        {
            targetManager.Target = gameObject;

            var targetSystem = TargetSystem.Instance();
            if (targetSystem == null)
                return false;

            var nativeObject = (GameObject*)gameObject.Address;
            if (nativeObject == null)
                return false;

            targetSystem->InteractWithObject(nativeObject, true);
            return true;
        }
        catch (Exception ex)
        {
            log?.Warning(ex, $"[ADS] Direct interact failed for {gameObject.Name.TextValue}.");
            return false;
        }
    }

    public static unsafe bool TryUseGeneralAction(uint actionId, IPluginLog? log = null)
    {
        try
        {
            var actionManager = ActionManager.Instance();
            if (actionManager == null)
                return false;

            actionManager->UseAction(ActionType.GeneralAction, actionId);
            return true;
        }
        catch (Exception ex)
        {
            log?.Warning(ex, $"[ADS] Failed to use general action {actionId}.");
            return false;
        }
    }

    public static unsafe bool TryCloseAddon(string addonName, IPluginLog? log = null)
    {
        try
        {
            var addon = RaptureAtkUnitManager.Instance()->GetAddonByName(addonName);
            if (addon == null || !addon->IsVisible)
                return false;

            addon->Close(true);
            return true;
        }
        catch (Exception ex)
        {
            log?.Warning(ex, $"[ADS] Failed to close addon {addonName}.");
            return false;
        }
    }

    public static unsafe bool TrySendChatCommand(ICommandManager commandManager, string command, IPluginLog? log = null)
    {
        try
        {
            if (commandManager.ProcessCommand(command))
                return true;

            var uiModule = UIModule.Instance();
            if (uiModule == null)
                return false;

            var bytes = Encoding.UTF8.GetBytes(command);
            var utf8String = Utf8String.FromSequence(bytes);
            uiModule->ProcessChatBoxEntry(utf8String, nint.Zero);
            return true;
        }
        catch (Exception ex)
        {
            log?.Warning(ex, $"[ADS] Failed to send chat command: {command}");
            return false;
        }
    }

    public static string GetTerritoryName(IDataManager dataManager, uint territoryId)
    {
        try
        {
            var sheet = dataManager.GetExcelSheet<TerritoryType>();
            if (sheet != null && sheet.TryGetRow(territoryId, out var territory))
            {
                var placeName = territory.PlaceName.Value.Name.ToString().Trim();
                if (!string.IsNullOrWhiteSpace(placeName))
                    return placeName;
            }
        }
        catch
        {
            // Fall through to the generic territory label.
        }

        return $"Territory {territoryId}";
    }

    public static bool IsInnTerritory(IDataManager dataManager, ushort territoryId)
    {
        var territoryName = GetTerritoryName(dataManager, territoryId);
        if (territoryName.StartsWith("Territory ", StringComparison.OrdinalIgnoreCase))
            return false;

        return territoryName.Contains("Inn", StringComparison.OrdinalIgnoreCase)
            || KnownInnTerritoryNames.Contains(territoryName);
    }
}
