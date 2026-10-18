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
        var gameMain = GameMain.Instance();
        Update(pluginEnabled,
            gameMain is not null ? gameMain->CurrentTerritoryTypeId : 0u,
            gameMain is not null ? gameMain->CurrentMapId : 0u,
            gameMain is not null ? gameMain->CurrentContentFinderConditionId : 0u);
    }

    internal void Update(bool pluginEnabled, uint gameMainTerritoryId, uint gameMainMapId, uint contentFinderConditionId)
    {
        var territoryTypeId = gameMainTerritoryId != 0 ? gameMainTerritoryId : clientState.TerritoryType;
        var clientStateMapId = clientState.MapId;
        uint mapId = clientStateMapId;
        var isLoggedIn = clientState.IsLoggedIn;
        var betweenAreas = condition[ConditionFlag.BetweenAreas];
        var betweenAreas51 = condition[ConditionFlag.BetweenAreas51];
        var boundByDuty = condition[ConditionFlag.BoundByDuty];
        var boundByDuty56 = condition[ConditionFlag.BoundByDuty56];
        var occupiedInCutSceneEvent = condition[ConditionFlag.OccupiedInCutSceneEvent];
        var watchingCutscene = condition[ConditionFlag.WatchingCutscene];
        var watchingCutscene78 = condition[ConditionFlag.WatchingCutscene78];
        var transitHold = betweenAreas || betweenAreas51 || occupiedInCutSceneEvent || watchingCutscene || watchingCutscene78;

        // Missing native fields during a held run are not a new duty. Retaining
        // the identity here also keeps authored destination keys consistent.
        if (pluginEnabled && isLoggedIn && Current.PluginEnabled && Current.IsLoggedIn
            && (boundByDuty || boundByDuty56 || transitHold)
            && (Current.InInstancedDuty || Current.IsCutscene || Current.IsUnsafeTransition)
            && Current.TerritoryTypeId != 0 && Current.ContentFinderConditionId != 0
            && (territoryTypeId == 0 || territoryTypeId == Current.TerritoryTypeId)
            && (contentFinderConditionId == 0 || contentFinderConditionId == Current.ContentFinderConditionId))
        {
            if (territoryTypeId == 0 && transitHold)
                territoryTypeId = Current.TerritoryTypeId;
            if (territoryTypeId == Current.TerritoryTypeId && contentFinderConditionId == 0)
                contentFinderConditionId = Current.ContentFinderConditionId;
        }

        // ClientState.MapId has proven to settle faster across Praetorium layer swaps.
        // Prefer it when available so layer-scoped rules do not keep running on the previous sub-area.
        if (clientStateMapId != 0)
            mapId = clientStateMapId;
        else if (gameMainMapId != 0)
            mapId = gameMainMapId;

        var currentDuty = dutyCatalogService.ResolveCurrentDuty(contentFinderConditionId, territoryTypeId);
        var automationExcludedTerritory = AutomationTerritoryPolicy.IsAutomationExcludedTerritory(territoryTypeId);
        Current = new DutyContextSnapshot
        {
            PluginEnabled = pluginEnabled,
            IsLoggedIn = isLoggedIn,
            BoundByDuty = boundByDuty,
            BoundByDuty56 = boundByDuty56,
            BetweenAreas = betweenAreas,
            BetweenAreas51 = betweenAreas51,
            Jumping = condition[ConditionFlag.Jumping],
            Jumping61 = condition[ConditionFlag.Jumping61],
            Occupied33 = condition[ConditionFlag.Occupied33],
            OccupiedInQuestEvent = condition[ConditionFlag.OccupiedInQuestEvent],
            OccupiedInEvent = condition[ConditionFlag.OccupiedInEvent],
            OccupiedInCutSceneEvent = occupiedInCutSceneEvent,
            WatchingCutscene = watchingCutscene,
            WatchingCutscene78 = watchingCutscene78,
            InCombat = condition[ConditionFlag.InCombat],
            Mounted = condition[ConditionFlag.Mounted],
            Casting = condition[ConditionFlag.Casting] || condition[ConditionFlag.Casting87],
            Occupied39 = condition[ConditionFlag.Occupied39],
            TerritoryTypeId = territoryTypeId,
            MapId = mapId,
            ContentFinderConditionId = contentFinderConditionId,
            CurrentMapId = isLoggedIn && !betweenAreas && !betweenAreas51 && territoryTypeId != 0 && mapId != 0
                ? mapId : null,
            Alliance = automationExcludedTerritory ? null : TryResolveAlliance(),
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
