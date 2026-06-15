using System.Globalization;
using System.Numerics;
using ADS.Models;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using LuminaMap = Lumina.Excel.Sheets.Map;

namespace ADS.Services;

public sealed class MapFlagService
{
    private const string FlagToPointIpc = "vnavmesh.Query.Mesh.FlagToPoint";

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IDataManager dataManager;
    private readonly IClientState clientState;
    private readonly ICondition condition;
    private readonly IPluginLog log;
    private DateTime nextReadFailureLogUtc = DateTime.MinValue;
    private DateTime nextIpcFailureLogUtc = DateTime.MinValue;

    public MapFlagService(
        IDalamudPluginInterface pluginInterface,
        IDataManager dataManager,
        IClientState clientState,
        ICondition condition,
        IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.dataManager = dataManager;
        this.clientState = clientState;
        this.condition = condition;
        this.log = log;
    }

    public bool TryQueryFlagDestination(out Vector3? destination, out string status)
    {
        try
        {
            destination = pluginInterface.GetIpcSubscriber<Vector3?>(FlagToPointIpc).InvokeFunc();
            status = destination.HasValue
                ? string.Create(
                    CultureInfo.InvariantCulture,
                    $"Resolved /vnav moveflag destination at {destination.Value.X:R}, {destination.Value.Y:R}, {destination.Value.Z:R}.")
                : "/vnav moveflag destination unavailable: navmesh has no resolved floor/query.";
            return true;
        }
        catch (Exception ex)
        {
            destination = null;
            status = $"/vnav moveflag destination unavailable: vnavmesh IPC unavailable ({ex.Message}).";
            var now = DateTime.UtcNow;
            if (now >= nextIpcFailureLogUtc)
            {
                nextIpcFailureLogUtc = now + TimeSpan.FromSeconds(5);
                log.Debug(ex, $"[ADS] {status}");
            }

            return false;
        }
    }

    internal unsafe MapFlagObservation ReadCurrentFlag()
    {
        if (condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51])
            return MapFlagObservation.Unavailable(MapFlagUnavailableReason.Transition);

        try
        {
            var agentMap = AgentMap.Instance();
            if (agentMap == null)
                return MapFlagObservation.Unavailable(MapFlagUnavailableReason.AgentMapUnavailable);

            if (agentMap->FlagMarkerCount == 0)
                return MapFlagObservation.Cleared();

            var marker = agentMap->FlagMapMarkers[0];
            if (marker.TerritoryId == 0)
                return MapFlagObservation.Unavailable(MapFlagUnavailableReason.ReadFailure);

            var mapCoordinates = TryGetMapCoordinates(
                marker.TerritoryId,
                marker.MapId,
                marker.XFloat,
                marker.YFloat,
                out var resolvedMapCoordinates,
                out var unavailableReason)
                ? resolvedMapCoordinates
                : (Vector2?)null;

            return MapFlagObservation.Present(new MapFlagSnapshot(
                marker.TerritoryId,
                marker.MapId,
                marker.XFloat,
                marker.YFloat,
                mapCoordinates,
                unavailableReason));
        }
        catch (Exception ex)
        {
            var now = DateTime.UtcNow;
            if (now >= nextReadFailureLogUtc)
            {
                nextReadFailureLogUtc = now + TimeSpan.FromSeconds(5);
                log.Debug(ex, "[ADS] Current map flag read unavailable.");
            }

            return MapFlagObservation.Unavailable(MapFlagUnavailableReason.ReadFailure);
        }
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

    private bool TryGetMapCoordinates(
        uint territoryId,
        uint mapId,
        float worldX,
        float worldZ,
        out Vector2 mapCoordinates,
        out string unavailableReason)
    {
        mapCoordinates = default;
        if (mapId == 0)
        {
            unavailableReason = "marker map id is unavailable";
            return false;
        }

        try
        {
            var mapSheet = dataManager.GetExcelSheet<LuminaMap>();
            if (mapSheet is null || !mapSheet.TryGetRow(mapId, out var map))
            {
                unavailableReason = $"map row {mapId} is unavailable";
                return false;
            }

            if (map.TerritoryType.RowId != territoryId)
            {
                unavailableReason = $"map row {mapId} does not match marker territory {territoryId}";
                return false;
            }

            mapCoordinates = MapUtil.WorldToMap(new Vector2(worldX, worldZ), map);
            unavailableReason = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            unavailableReason = $"map row {mapId} conversion failed ({ex.Message})";
            return false;
        }
    }
}
