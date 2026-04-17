using System.Globalization;
using System.Text;
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

    public static unsafe void FireAddonCallback(string addonName, bool updateState, params object[] args)
    {
        try
        {
            var addon = RaptureAtkUnitManager.Instance()->GetAddonByName(addonName);
            if (addon == null || !addon->IsVisible)
                return;

            var atkValues = new AtkValue[args.Length];
            for (var i = 0; i < args.Length; i++)
            {
                atkValues[i] = args[i] switch
                {
                    int intVal => new AtkValue { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int, Int = intVal },
                    uint uintVal => new AtkValue { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.UInt, UInt = uintVal },
                    bool boolVal => new AtkValue { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Bool, Byte = (byte)(boolVal ? 1 : 0) },
                    _ => new AtkValue { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int, Int = Convert.ToInt32(args[i], CultureInfo.InvariantCulture) },
                };
            }

            fixed (AtkValue* ptr = atkValues)
            {
                addon->FireCallback((uint)atkValues.Length, ptr, updateState);
            }
        }
        catch
        {
            // Intentionally quiet here; callers already retry and status on failure.
        }
    }

    public static unsafe bool ClickYesIfVisible(IPluginLog? log = null)
    {
        try
        {
            nint addonPtr = Plugin.GameGui.GetAddonByName("SelectYesno", 1);
            if (addonPtr == nint.Zero)
                return false;

            var addon = (AtkUnitBase*)addonPtr;
            if (!addon->IsVisible)
                return false;

            var atkValues = stackalloc AtkValue[2];
            atkValues[0].Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int;
            atkValues[0].Int = 0;
            atkValues[1].Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int;
            atkValues[1].Int = 0;

            addon->FireCallback(2, atkValues);
            log?.Information("[ADS] Clicked Yes on SelectYesno.");
            return true;
        }
        catch (Exception ex)
        {
            log?.Warning(ex, "[ADS] ClickYesIfVisible failed.");
            return false;
        }
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
