using ADS.Models;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace ADS.Services;

public sealed class DutyContextService
{
    private readonly IClientState clientState;
    private readonly ICondition condition;
    private readonly DutyCatalogService dutyCatalogService;
    private readonly IPartyList partyList;

    public DutyContextService(
        IClientState clientState,
        ICondition condition,
        DutyCatalogService dutyCatalogService,
        IPartyList partyList)
    {
        this.clientState = clientState;
        this.condition = condition;
        this.dutyCatalogService = dutyCatalogService;
        this.partyList = partyList;
        Current = new DutyContextSnapshot
        {
            PluginEnabled = true,
            IsLoggedIn = false,
            BoundByDuty = false,
            BoundByDuty56 = false,
            BetweenAreas = false,
            BetweenAreas51 = false,
            Jumping = false,
            Jumping61 = false,
            Occupied33 = false,
            OccupiedInQuestEvent = false,
            OccupiedInEvent = false,
            OccupiedInCutSceneEvent = false,
            WatchingCutscene = false,
            InCombat = false,
            Mounted = false,
            TerritoryTypeId = 0,
            MapId = 0,
            ContentFinderConditionId = 0,
            Alliance = null,
            CurrentDuty = null,
        };
    }

    public DutyContextSnapshot Current { get; private set; }

    public unsafe void Update(bool pluginEnabled)
    {
        uint territoryTypeId = clientState.TerritoryType;
        var clientStateMapId = clientState.MapId;
        uint mapId = clientStateMapId;
        uint gameMainMapId = 0;
        uint contentFinderConditionId = 0;
        var gameMain = GameMain.Instance();
        if (gameMain is not null)
        {
            territoryTypeId = gameMain->CurrentTerritoryTypeId;
            gameMainMapId = gameMain->CurrentMapId;
            contentFinderConditionId = gameMain->CurrentContentFinderConditionId;
        }

        // ClientState.MapId has proven to settle faster across Praetorium layer swaps.
        // Prefer it when available so layer-scoped rules do not keep running on the previous sub-area.
        if (clientStateMapId != 0)
            mapId = clientStateMapId;
        else if (gameMainMapId != 0)
            mapId = gameMainMapId;

        var currentDuty = dutyCatalogService.ResolveCurrentDuty(contentFinderConditionId, territoryTypeId);
        Current = new DutyContextSnapshot
        {
            PluginEnabled = pluginEnabled,
            IsLoggedIn = clientState.IsLoggedIn,
            BoundByDuty = condition[ConditionFlag.BoundByDuty],
            BoundByDuty56 = condition[ConditionFlag.BoundByDuty56],
            BetweenAreas = condition[ConditionFlag.BetweenAreas],
            BetweenAreas51 = condition[ConditionFlag.BetweenAreas51],
            Jumping = condition[ConditionFlag.Jumping],
            Jumping61 = condition[ConditionFlag.Jumping61],
            Occupied33 = condition[ConditionFlag.Occupied33],
            OccupiedInQuestEvent = condition[ConditionFlag.OccupiedInQuestEvent],
            OccupiedInEvent = condition[ConditionFlag.OccupiedInEvent],
            OccupiedInCutSceneEvent = condition[ConditionFlag.OccupiedInCutSceneEvent],
            WatchingCutscene = condition[ConditionFlag.WatchingCutscene],
            InCombat = condition[ConditionFlag.InCombat],
            Mounted = condition[ConditionFlag.Mounted],
            TerritoryTypeId = territoryTypeId,
            MapId = mapId,
            ContentFinderConditionId = contentFinderConditionId,
            Alliance = TryResolveAlliance(),
            CurrentDuty = currentDuty,
        };
    }

    private unsafe string? TryResolveAlliance()
    {
        try
        {
            if (!partyList.IsAlliance)
                return null;

            var unitManager = RaptureAtkUnitManager.Instance();
            if (unitManager == null)
                return null;

            var addon = (AddonPartyList*)unitManager->GetAddonByName("_PartyList");
            if (addon == null
                || !addon->AtkUnitBase.IsReady
                || !addon->AtkUnitBase.IsVisible
                || addon->PartyTypeTextNode == null)
            {
                return null;
            }

            return AllianceScopeParser.Parse(
                isAlliance: true,
                addon->PartyTypeTextNode->NodeText.ToString());
        }
        catch
        {
            return null;
        }
    }
}
