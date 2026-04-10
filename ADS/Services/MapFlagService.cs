using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;

namespace ADS.Services;

public sealed class MapFlagService
{
    private readonly IDataManager dataManager;
    private readonly IClientState clientState;
    private readonly ICondition condition;
    private readonly IPluginLog log;

    public MapFlagService(IDataManager dataManager, IClientState clientState, ICondition condition, IPluginLog log)
    {
        this.dataManager = dataManager;
        this.clientState = clientState;
        this.condition = condition;
        this.log = log;
    }

    public unsafe bool TryPlaceFlag(uint territoryId, Vector3 worldPosition, string objectName, out string status)
    {
        if (condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51])
        {
            status = $"Skipped map flag for {objectName}: zone transition is active.";
            log.Warning($"[ADS] {status}");
            return false;
        }

        if (territoryId == 0)
        {
            status = $"Skipped map flag for {objectName}: no current territory id.";
            log.Warning($"[ADS] {status}");
            return false;
        }

        try
        {
            var agentMap = AgentMap.Instance();
            if (agentMap == null)
            {
                status = $"Skipped map flag for {objectName}: AgentMap is unavailable.";
                log.Warning($"[ADS] {status}");
                return false;
            }

            var territorySheet = dataManager.GetExcelSheet<TerritoryType>();
            if (!territorySheet.TryGetRow(territoryId, out var territory))
            {
                status = $"Skipped map flag for {objectName}: territory {territoryId} was not found.";
                log.Warning($"[ADS] {status}");
                return false;
            }

            var mapId = clientState.MapId != 0 ? clientState.MapId : territory.Map.RowId;
            if (mapId == 0)
            {
                status = $"Skipped map flag for {objectName}: no active map id was available.";
                log.Warning($"[ADS] {status}");
                return false;
            }

            var flagPosition = new Vector3(worldPosition.X, 0f, worldPosition.Z);
            agentMap->SetFlagMapMarker(territoryId, mapId, flagPosition);
            status = $"Placed map flag for {objectName} on map {mapId} at {worldPosition.X:0.0}, {worldPosition.Z:0.0}.";
            log.Information($"[ADS] {status}");
            return true;
        }
        catch (Exception ex)
        {
            status = $"Failed to place map flag for {objectName}: {ex.Message}";
            log.Warning(ex, $"[ADS] {status}");
            return false;
        }
    }
}
