using ADS.Models;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace ADS.Services;

public sealed class DutyContextService
{
    private readonly IClientState clientState;
    private readonly ICondition condition;
    private readonly DutyCatalogService dutyCatalogService;

    public DutyContextService(IClientState clientState, ICondition condition, DutyCatalogService dutyCatalogService)
    {
        this.clientState = clientState;
        this.condition = condition;
        this.dutyCatalogService = dutyCatalogService;
        Current = new DutyContextSnapshot
        {
            PluginEnabled = true,
            IsLoggedIn = false,
            BoundByDuty = false,
            BoundByDuty56 = false,
            BetweenAreas = false,
            BetweenAreas51 = false,
            InCombat = false,
            TerritoryTypeId = 0,
            ContentFinderConditionId = 0,
            CurrentDuty = null,
        };
    }

    public DutyContextSnapshot Current { get; private set; }

    public unsafe void Update(bool pluginEnabled)
    {
        uint territoryTypeId = clientState.TerritoryType;
        uint contentFinderConditionId = 0;
        var gameMain = GameMain.Instance();
        if (gameMain is not null)
        {
            territoryTypeId = gameMain->CurrentTerritoryTypeId;
            contentFinderConditionId = gameMain->CurrentContentFinderConditionId;
        }

        var currentDuty = dutyCatalogService.ResolveCurrentDuty(contentFinderConditionId, territoryTypeId);
        Current = new DutyContextSnapshot
        {
            PluginEnabled = pluginEnabled,
            IsLoggedIn = clientState.IsLoggedIn,
            BoundByDuty = condition[ConditionFlag.BoundByDuty],
            BoundByDuty56 = condition[ConditionFlag.BoundByDuty56],
            BetweenAreas = condition[ConditionFlag.BetweenAreas],
            BetweenAreas51 = condition[ConditionFlag.BetweenAreas51],
            InCombat = condition[ConditionFlag.InCombat],
            TerritoryTypeId = territoryTypeId,
            ContentFinderConditionId = contentFinderConditionId,
            CurrentDuty = currentDuty,
        };
    }
}
