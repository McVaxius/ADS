using System.Globalization;
using System.Numerics;
using ADS.Models;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using Lumina.Excel.Sheets;

namespace ADS.Services;

public sealed class DungeonFrontierService
{
    private const uint PraetoriumTerritoryTypeId = 1044;
    private const float FrontierVisitRadius = 8f;
    private const float FrontierVisitVerticalCap = 12f;
    private const float FrontierBlockingVerticalSanityCap = 100f;
    private const float ManualMapXzDestinationVisitRadius = 1f;
    private const float HeadingSampleMinDistance = 6f;
    private const float HeadingScoutProjectionDistance = 18f;
    private const float HeadingScoutAdvanceRadius = 4f;
    private const float HeadingScoutMinForwardDot = 0.25f;
    private const float LabelFrontierMinForwardDot = 0.25f;
    private const float LabelFrontierRetargetBacktrackDot = -0.10f;

    private readonly IDataManager dataManager;
    private readonly IObjectTable objectTable;
    private readonly IPluginLog log;
    private readonly ObjectPriorityRuleService objectPriorityRuleService;
    private readonly Dictionary<ulong, IReadOnlyList<DungeonFrontierPoint>> frontierCache = [];
    private readonly Dictionary<ulong, IReadOnlyList<MapLabelMarker>> labelMarkerCache = [];
    private readonly Dictionary<ulong, string> labelMarkerStatusCache = [];
    private readonly HashSet<string> visitedFrontierKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> loggedActiveMapSelections = new(StringComparer.Ordinal);
    private readonly HashSet<string> loggedInvalidMapXzDestinationRules = new(StringComparer.Ordinal);
    private readonly HashSet<string> loggedResolvedMapXzDestinationRules = new(StringComparer.Ordinal);
    private readonly HashSet<string> loggedInvalidXyzDestinationRules = new(StringComparer.Ordinal);
    private readonly HashSet<string> loggedResolvedXyzDestinationRules = new(StringComparer.Ordinal);
    private readonly HashSet<string> loggedCombatBypassManualSelections = new(StringComparer.Ordinal);
    private uint activeDutyKey;
    private Vector3? lastProgressSamplePosition;
    private Vector3? currentHeading;
    private DungeonFrontierPoint? headingScoutTarget;
    private DungeonFrontierPoint? lastValidManualDestination;
    private int headingScoutSequence;

    public DungeonFrontierService(
        IDataManager dataManager,
        IObjectTable objectTable,
        IPluginLog log,
        ObjectPriorityRuleService objectPriorityRuleService)
    {
        this.dataManager = dataManager;
        this.objectTable = objectTable;
        this.log = log;
        this.objectPriorityRuleService = objectPriorityRuleService;
    }

    public DungeonFrontierPoint? CurrentTarget { get; private set; }

    public FrontierMode CurrentMode { get; private set; }

    public Vector3? CurrentHeading
        => currentHeading;

    public IReadOnlyList<MapLabelMarker> CurrentLabelMarkers { get; private set; } = [];

    public string CurrentLabelStatus { get; private set; } = "No current map labels loaded.";

    public uint ActiveMapId { get; private set; }

    public string ActiveMapName { get; private set; } = "No current map resolved.";

    public int TotalPoints { get; private set; }

    public int VisitedPoints { get; private set; }

    public int ManualMapXzDestinationCount { get; private set; }

    public int VisitedManualMapXzDestinations { get; private set; }

    public int ManualXyzDestinationCount { get; private set; }

    public int VisitedManualXyzDestinations { get; private set; }

    public bool HasRemainingManualDestinations
        => ManualMapXzDestinationCount > VisitedManualMapXzDestinations
           || ManualXyzDestinationCount > VisitedManualXyzDestinations;

    public DungeonFrontierPoint? LastGhostedManualDestination { get; private set; }

    public DateTime? LastGhostedManualDestinationUtc { get; private set; }

    public string LastGhostedManualDestinationReason { get; private set; } = string.Empty;

    public void Update(DutyContextSnapshot context, ObservationSnapshot observation)
    {
        if (!context.PluginEnabled || !context.IsLoggedIn || !context.InInstancedDuty || context.TerritoryTypeId == 0)
        {
            if (activeDutyKey != 0)
            {
                Reset();
                activeDutyKey = 0;
            }

            return;
        }

        var dutyKey = context.ContentFinderConditionId != 0 ? context.ContentFinderConditionId : context.TerritoryTypeId;
        if (dutyKey != activeDutyKey)
        {
            Reset();
            activeDutyKey = dutyKey;
        }

        var previousTarget = CurrentTarget;
        RememberManualDestination(previousTarget);
        CurrentTarget = null;
        CurrentMode = FrontierMode.None;
        CurrentLabelMarkers = [];
        CurrentLabelStatus = "No current map labels loaded.";
        ActiveMapId = 0;
        ActiveMapName = "No current map resolved.";
        TotalPoints = 0;
        VisitedPoints = 0;
        ManualMapXzDestinationCount = 0;
        VisitedManualMapXzDestinations = 0;
        ManualXyzDestinationCount = 0;
        VisitedManualXyzDestinations = 0;

        var playerPosition = objectTable.LocalPlayer?.Position;
        if (context.IsUnsafeTransition)
        {
            if (context.BetweenAreas)
                GhostCurrentOrLastManualDestination(previousTarget);

            return;
        }

        var hasActiveMap = TryResolveActiveMap(context, out var activeMap, out var activeMapStatus);
        CurrentLabelStatus = activeMapStatus;
        if (hasActiveMap)
        {
            ActiveMapId = activeMap.RowId;
            ActiveMapName = BuildMapName(activeMap);
        }

        UpdateHeadingMemory(playerPosition, observation);
        if (hasActiveMap)
        {
            CurrentLabelMarkers = GetLabelMarkers(context.TerritoryTypeId, activeMap.RowId);
            var labelCacheKey = BuildCacheKey(context.TerritoryTypeId, activeMap.RowId);
            if (labelMarkerStatusCache.TryGetValue(labelCacheKey, out var labelStatus))
                CurrentLabelStatus = labelStatus;
        }

        var noFrontierBlockingLiveObjects = HasNoFrontierBlockingLiveObjects(context, observation, playerPosition);

        var manualMapXzDestinations = hasActiveMap && playerPosition.HasValue
            ? BuildMapXzDestinationPoints(context, activeMap, playerPosition.Value)
            : [];
        var manualXyzDestinations = hasActiveMap && playerPosition.HasValue
            ? BuildXyzDestinationPoints(context, activeMap, playerPosition.Value)
            : [];
        ManualMapXzDestinationCount = manualMapXzDestinations.Count;
        VisitedManualMapXzDestinations = manualMapXzDestinations.Count(x => visitedFrontierKeys.Contains(x.Key));
        ManualXyzDestinationCount = manualXyzDestinations.Count;
        VisitedManualXyzDestinations = manualXyzDestinations.Count(x => visitedFrontierKeys.Contains(x.Key));
        var manualDestinations = manualMapXzDestinations
            .Concat(manualXyzDestinations)
            .ToList();
        var manualDestinationCanBeatLiveProgression = ShouldPrioritizeManualDestinationBeforeLiveProgression(
            context,
            observation,
            playerPosition,
            manualDestinations);
        var selectedCombatBypassManualDestination = SelectPraetoriumOnFootCombatBypassManualDestination(
                context,
                observation,
                playerPosition,
                manualDestinations,
                previousTarget)
            ?? SelectCombatBypassManualDestinationAgainstLiveBlockers(
                context,
                observation,
                playerPosition,
                manualDestinations)
            ?? SelectPraetoriumMountedCombatBypassManualDestination(
                context,
                observation,
                playerPosition,
                manualDestinations);
        if (noFrontierBlockingLiveObjects
            || manualDestinationCanBeatLiveProgression
            || selectedCombatBypassManualDestination is not null)
        {
            CurrentTarget = selectedCombatBypassManualDestination
                ?? SelectCurrentManualDestination(manualDestinations, playerPosition);
            if (CurrentTarget is not null)
            {
                CurrentMode = CurrentTarget.IsManualXyzDestination
                    ? FrontierMode.XyzDestination
                    : FrontierMode.MapXzDestination;
                RememberManualDestination(CurrentTarget);
                return;
            }
        }

        if (hasActiveMap && noFrontierBlockingLiveObjects)
        {
            var treasureRoutePoints = BuildTreasureRoutePoints(context, activeMap, manualDestinations);
            if (treasureRoutePoints.Count > 0)
            {
                if (playerPosition.HasValue)
                    MarkVisitedPoints(treasureRoutePoints, playerPosition.Value, FrontierVisitRadius, FrontierVisitVerticalCap);

                CurrentTarget = SelectCurrentTarget(treasureRoutePoints, playerPosition, previousTarget);
                if (CurrentTarget is not null)
                {
                    CurrentMode = FrontierMode.TreasureDungeon;
                    return;
                }
            }
        }

        if (hasActiveMap)
        {
            var points = GetFrontierPoints(context.TerritoryTypeId, activeMap.RowId);
            TotalPoints = points.Count;
            if (points.Count > 0)
            {
                headingScoutTarget = null;

                if (playerPosition.HasValue)
                    MarkVisitedPoints(points, playerPosition.Value, FrontierVisitRadius, FrontierVisitVerticalCap);

                VisitedPoints = points.Count(x => visitedFrontierKeys.Contains(x.Key));
                if (!noFrontierBlockingLiveObjects)
                    return;

                CurrentTarget = SelectCurrentTarget(points, playerPosition, previousTarget);
                if (CurrentTarget is not null)
                    CurrentMode = FrontierMode.Label;

                return;
            }
        }

        if (!playerPosition.HasValue || !noFrontierBlockingLiveObjects)
            return;

        CurrentTarget = SelectHeadingScoutTarget(context, playerPosition.Value);
        if (CurrentTarget is not null)
            CurrentMode = FrontierMode.HeadingScout;
    }

    public void Reset()
    {
        visitedFrontierKeys.Clear();
        CurrentTarget = null;
        CurrentMode = FrontierMode.None;
        TotalPoints = 0;
        VisitedPoints = 0;
        ActiveMapId = 0;
        ActiveMapName = "No current map resolved.";
        lastProgressSamplePosition = null;
        currentHeading = null;
        headingScoutTarget = null;
        lastValidManualDestination = null;
        headingScoutSequence = 0;
        CurrentLabelMarkers = [];
        CurrentLabelStatus = "No current map labels loaded.";
        ManualMapXzDestinationCount = 0;
        VisitedManualMapXzDestinations = 0;
        ManualXyzDestinationCount = 0;
        VisitedManualXyzDestinations = 0;
        LastGhostedManualDestination = null;
        LastGhostedManualDestinationUtc = null;
        LastGhostedManualDestinationReason = string.Empty;
        loggedCombatBypassManualSelections.Clear();
    }

    public void MarkVisited(DungeonFrontierPoint point, Vector3 playerPosition)
    {
        if (!visitedFrontierKeys.Add(point.Key))
            return;

        ClearRememberedManualDestination(point);
        if (point.IsManualDestination)
        {
            var ghostReason = point.IsManualXyzDestination ? "ReachedXyzArrival" : "ReachedXzArrival";
            RememberGhostedManualDestination(point, ghostReason);
            var reachText = point.IsManualXyzDestination
                ? $"after reaching 3D {Vector3.Distance(playerPosition, point.Position):0.0}y"
                : $"after reaching XZ {GetHorizontalDistance(playerPosition, point.Position):0.0}y";
            log.Information($"[ADS] Ghosted {GetManualDestinationLabel(point)} {point.Name} at {FormatVector(point.Position)} {reachText}.");
        }
    }

    public void RetireManualDestination(DungeonFrontierPoint point, string reason, string detail)
    {
        if (!visitedFrontierKeys.Add(point.Key))
            return;

        ClearRememberedManualDestination(point);
        if (!point.IsManualDestination)
            return;

        RememberGhostedManualDestination(point, reason);
        log.Information($"[ADS] Ghosted {GetManualDestinationLabel(point)} {point.Name} at {FormatVector(point.Position)} {detail}.");
    }

    public DungeonFrontierPoint? GetCurrentOrRememberedManualDestination(Vector3? playerPosition)
    {
        var point = CurrentTarget is { IsManualDestination: true }
            ? CurrentTarget
            : lastValidManualDestination;
        if (point is null)
            return null;

        return BuildNavigationPoint(point, playerPosition);
    }

    public DungeonFrontierPoint? GetCurrentOrRememberedManualMapXzDestination(Vector3? playerPosition)
        => GetCurrentOrRememberedManualDestination(playerPosition);

    public DungeonFrontierPoint? FindNearbyUnvisitedManualDestination(
        DutyContextSnapshot context,
        Vector3 playerPosition,
        Vector3 anchorPosition,
        float radius,
        bool requirePlayerNear = false)
    {
        if (!context.PluginEnabled || !context.IsLoggedIn || !context.InInstancedDuty || context.TerritoryTypeId == 0)
            return null;

        if (!TryResolveActiveMap(context, out var activeMap, out _))
            return null;

        var manualMapXzDestinations = BuildMapXzDestinationPoints(context, activeMap, playerPosition);
        var manualXyzDestinations = BuildXyzDestinationPoints(context, activeMap, playerPosition);
        return manualMapXzDestinations
            .Concat(manualXyzDestinations)
            .Where(point => !visitedFrontierKeys.Contains(point.Key))
            .Select(point => new
            {
                Point = point,
                AnchorDistance = GetManualDestinationDistance(anchorPosition, point),
                PlayerDistance = GetManualDestinationDistance(playerPosition, point),
            })
            .Where(x => x.AnchorDistance <= radius && (!requirePlayerNear || x.PlayerDistance <= radius))
            .OrderBy(x => x.Point.Priority)
            .ThenBy(x => x.AnchorDistance)
            .ThenBy(x => x.PlayerDistance)
            .ThenBy(x => x.Point.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Point)
            .FirstOrDefault();
    }

    public DungeonFrontierPoint? FindNextForceMarchManualDestinationAfter(
        DutyContextSnapshot context,
        Vector3 playerPosition,
        DungeonFrontierPoint completedPoint)
    {
        if (!completedPoint.IsManualDestination)
            return null;

        if (!context.PluginEnabled || !context.IsLoggedIn || !context.InInstancedDuty || context.TerritoryTypeId == 0)
            return null;

        if (!TryResolveActiveMap(context, out var activeMap, out _))
            return null;

        var manualDestinations = BuildMapXzDestinationPoints(context, activeMap, playerPosition)
            .Concat(BuildXyzDestinationPoints(context, activeMap, playerPosition))
            .ToList();
        if (manualDestinations.Count == 0)
            return null;

        var anchorIndex = manualDestinations.FindIndex(point => string.Equals(point.Key, completedPoint.Key, StringComparison.Ordinal));
        if (anchorIndex < 0)
            return null;

        var nextUnvisitedManualDestination = manualDestinations
            .Skip(anchorIndex + 1)
            .FirstOrDefault(point => !visitedFrontierKeys.Contains(point.Key));
        return nextUnvisitedManualDestination is { AllowCombatBypass: true } nextForceMarchDestination
            ? BuildNavigationPoint(nextForceMarchDestination, playerPosition)
            : null;
    }

    private IReadOnlyList<DungeonFrontierPoint> GetFrontierPoints(uint territoryTypeId, uint mapId)
    {
        var cacheKey = BuildCacheKey(territoryTypeId, mapId);
        if (frontierCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var built = BuildFrontierPoints(territoryTypeId, mapId);
        frontierCache[cacheKey] = built;
        return built;
    }

    private IReadOnlyList<MapLabelMarker> GetLabelMarkers(uint territoryTypeId, uint mapId)
    {
        var cacheKey = BuildCacheKey(territoryTypeId, mapId);
        if (labelMarkerCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var built = BuildLabelMarkers(territoryTypeId, mapId);
        labelMarkerCache[cacheKey] = built;
        return built;
    }

    private IReadOnlyList<DungeonFrontierPoint> BuildMapXzDestinationPoints(DutyContextSnapshot context, Map map, Vector3 playerPosition)
    {
        var destinationRules = objectPriorityRuleService.GetMapXzDestinationRules(context);
        if (destinationRules.Count == 0)
            return [];

        var points = new List<DungeonFrontierPoint>();
        foreach (var rule in destinationRules)
        {
            var ruleKey = BuildMapXzDestinationRuleKey(context, rule);
            if (!DoesManualDestinationRuleMatchActiveMap(rule, map))
                continue;

            if (!TryParseMapCoordinates(rule.MapCoordinates, out var mapCoordinates))
            {
                LogMapXzDestinationWarning(
                    $"bad-coordinates:{ruleKey}",
                    $"[ADS] Ignoring Map XZ destination rule {rule.ObjectName}: could not parse mapCoordinates '{rule.MapCoordinates}'. Use a value like 11.3,10.4.");
                continue;
            }

            var name = string.IsNullOrWhiteSpace(rule.ObjectName)
                ? $"Map XZ {mapCoordinates.X:0.0}, {mapCoordinates.Y:0.0}"
                : rule.ObjectName;
            var worldPosition = ConvertMapCoordinatesToWorld(mapCoordinates, map, playerPosition.Y);
            if (!objectPriorityRuleService.DestinationRulePassesDistanceGates(rule, playerPosition, worldPosition))
                continue;

            if (loggedResolvedMapXzDestinationRules.Add(ruleKey))
            {
                log.Information(
                    $"[ADS] Resolved Map XZ destination {name} ({mapCoordinates.X:0.0}, {mapCoordinates.Y:0.0}) on map row {map.RowId} to world {FormatVector(worldPosition)} using sizeFactor {map.SizeFactor} and offsets {map.OffsetX},{map.OffsetY}.");
            }

            points.Add(new DungeonFrontierPoint
            {
                Key = $"{ruleKey}:{map.RowId}:{mapCoordinates.X:0.###},{mapCoordinates.Y:0.###}",
                Name = name,
                Position = worldPosition,
                LevelRowId = 0,
                MapId = map.RowId,
                Priority = rule.Priority,
                MapCoordinates = mapCoordinates,
                UsePlayerYForNavigation = true,
                ManualDestinationKind = ManualDestinationKind.MapXz,
                AllowCombatBypass = AllowsCombatBypass(rule),
                ArrivalRadiusXz = ManualMapXzDestinationVisitRadius,
            });
        }

        return points;
    }

    private IReadOnlyList<DungeonFrontierPoint> BuildXyzDestinationPoints(DutyContextSnapshot context, Map map, Vector3 playerPosition)
    {
        var destinationRules = objectPriorityRuleService.GetXyzDestinationRules(context);
        if (destinationRules.Count == 0)
            return [];

        var points = new List<DungeonFrontierPoint>();
        foreach (var rule in destinationRules)
        {
            var ruleKey = BuildXyzDestinationRuleKey(context, rule);
            if (!DoesManualDestinationRuleMatchActiveMap(rule, map))
                continue;

            if (!TryParseWorldCoordinates(rule.WorldCoordinates, out var worldCoordinates))
            {
                LogXyzDestinationWarning(
                    $"bad-coordinates:{ruleKey}",
                    $"[ADS] Ignoring XYZ destination rule {rule.ObjectName}: could not parse worldCoordinates '{rule.WorldCoordinates}'. Use a value like 154.1,101.9,-34.2.");
                continue;
            }

            var name = string.IsNullOrWhiteSpace(rule.ObjectName)
                ? $"XYZ {worldCoordinates.X:0.0}, {worldCoordinates.Y:0.0}, {worldCoordinates.Z:0.0}"
                : rule.ObjectName;
            if (!objectPriorityRuleService.DestinationRulePassesDistanceGates(rule, playerPosition, worldCoordinates))
                continue;

            if (loggedResolvedXyzDestinationRules.Add(ruleKey))
            {
                log.Information(
                    $"[ADS] Resolved XYZ destination {name} on map row {map.RowId} to world {FormatVector(worldCoordinates)}.");
            }

            points.Add(new DungeonFrontierPoint
            {
                Key = $"{ruleKey}:{map.RowId}:{worldCoordinates.X:0.###},{worldCoordinates.Y:0.###},{worldCoordinates.Z:0.###}",
                Name = name,
                Position = worldCoordinates,
                LevelRowId = 0,
                MapId = map.RowId,
                Priority = rule.Priority,
                ManualDestinationKind = ManualDestinationKind.Xyz,
                AllowCombatBypass = AllowsCombatBypass(rule),
                ArrivalRadius3d = ManualMapXzDestinationVisitRadius,
            });
        }

        return points;
    }

    private IReadOnlyList<DungeonFrontierPoint> BuildTreasureRoutePoints(
        DutyContextSnapshot context,
        Map map,
        IReadOnlyList<DungeonFrontierPoint> manualDestinations)
    {
        if (!TreasureDungeonData.HasRoute(context.TerritoryTypeId))
            return [];

        if (manualDestinations.Any(point => !visitedFrontierKeys.Contains(point.Key)))
            return [];

        return TreasureDungeonData.BuildRoutePoints(context.TerritoryTypeId, map.RowId);
    }

    private IReadOnlyList<DungeonFrontierPoint> BuildFrontierPoints(uint territoryTypeId, uint mapId)
    {
        try
        {
            var territorySheet = dataManager.GetExcelSheet<TerritoryType>();
            var levelSheet = dataManager.GetExcelSheet<Level>();
            var mapMarkerSheet = dataManager.GetSubrowExcelSheet<MapMarker>();
            if (territorySheet is null || levelSheet is null || mapMarkerSheet is null)
            {
                return BuildMapMarkerRangeFrontierPoints(territoryTypeId, mapId, "level-backed sheet lookup failed");
            }

            if (!territorySheet.TryGetRow(territoryTypeId, out var territory))
            {
                LogUnavailableFrontierLabels(territoryTypeId, mapId, "territory row was missing");
                return [];
            }

            var levelsByRowId = levelSheet
                .Where(level => level.Territory.RowId == territoryTypeId && level.Map.RowId == mapId)
                .ToDictionary(level => level.RowId, level => level);
            if (levelsByRowId.Count == 0)
            {
                return BuildMapMarkerRangeFrontierPoints(territoryTypeId, mapId, $"no matching Level rows for map {mapId}");
            }

            var points = new List<DungeonFrontierPoint>();
            var seenKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var subrowCollection in mapMarkerSheet)
            {
                foreach (var marker in subrowCollection)
                {
                    if (marker.PlaceNameSubtext.ValueNullable is null)
                        continue;

                    var name = NormalizeName(marker.PlaceNameSubtext.Value.Name.ToString());
                    if (string.IsNullOrWhiteSpace(name))
                        continue;

                    if (marker.DataKey.RowId == 0 || !levelsByRowId.TryGetValue(marker.DataKey.RowId, out var level))
                        continue;

                    var position = new Vector3(level.X, level.Y, level.Z);
                    var dedupeKey = $"{territoryTypeId}:{mapId}:{name}:{Quantize(position)}";
                    if (!seenKeys.Add(dedupeKey))
                        continue;

                    points.Add(new DungeonFrontierPoint
                    {
                        Key = dedupeKey,
                        Name = name,
                        Position = position,
                        LevelRowId = level.RowId,
                        MapId = mapId,
                    });
                }
            }

            points.Sort(static (left, right) =>
            {
                var byLevel = left.LevelRowId.CompareTo(right.LevelRowId);
                if (byLevel != 0)
                    return byLevel;

                return string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
            });

            if (points.Count > 0)
                log.Information($"[ADS] Built {points.Count} frontier label point(s) for territory {territoryTypeId} on map {mapId}.");

            return points.Count > 0
                ? points
                : BuildMapMarkerRangeFrontierPoints(territoryTypeId, mapId, $"matched {levelsByRowId.Count} Level row(s) but found no DataKey-backed MapMarker labels");
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"[ADS] Failed to build frontier label points for territory {territoryTypeId} on map {mapId}.");
            return BuildMapMarkerRangeFrontierPoints(territoryTypeId, mapId, $"level-backed lookup threw {ex.GetType().Name}");
        }
    }

    private IReadOnlyList<DungeonFrontierPoint> BuildMapMarkerRangeFrontierPoints(uint territoryTypeId, uint mapId, string levelBackedFailureReason)
    {
        var labels = GetLabelMarkers(territoryTypeId, mapId);
        if (labels.Count == 0)
        {
            LogUnavailableFrontierLabels(territoryTypeId, mapId, $"{levelBackedFailureReason}; MapMarkerRange fallback produced 0 labels");
            return [];
        }

        var points = labels
            .OrderBy(static label => label.MapId)
            .ThenBy(static label => label.MarkerRangeId)
            .ThenBy(static label => label.SubrowId)
            .ThenBy(static label => label.Name, StringComparer.OrdinalIgnoreCase)
            .Select(static label => new DungeonFrontierPoint
            {
                Key = $"map-marker-range:{label.Key}",
                Name = label.Name,
                Position = label.WorldPosition,
                LevelRowId = BuildLabelSortOrder(label),
                MapId = label.MapId,
                UsePlayerYForNavigation = true,
            })
            .ToList();

        log.Information($"[ADS] Built {points.Count} frontier MapMarkerRange point(s) for territory {territoryTypeId} on map {mapId} after level-backed frontier was unavailable ({levelBackedFailureReason}).");
        return points;
    }

    private IReadOnlyList<MapLabelMarker> BuildLabelMarkers(uint territoryTypeId, uint mapId)
    {
        var cacheKey = BuildCacheKey(territoryTypeId, mapId);
        try
        {
            var territorySheet = dataManager.GetExcelSheet<TerritoryType>();
            var mapSheet = dataManager.GetExcelSheet<Map>();
            var mapMarkerSheet = dataManager.GetSubrowExcelSheet<MapMarker>();
            if (territorySheet is null || mapSheet is null || mapMarkerSheet is null)
            {
                labelMarkerStatusCache[cacheKey] = $"Map-label lookup failed for territory {territoryTypeId} map {mapId}: one or more sheets were unavailable.";
                return [];
            }

            if (!territorySheet.TryGetRow(territoryTypeId, out var territory))
            {
                labelMarkerStatusCache[cacheKey] = $"Map-label lookup failed: territory {territoryTypeId} was not found.";
                return [];
            }

            if (!mapSheet.TryGetRow(mapId, out var map) || map.TerritoryType.RowId != territoryTypeId)
            {
                labelMarkerStatusCache[cacheKey] = $"Map-label lookup failed: territory {territoryTypeId} active map {mapId} was not found.";
                return [];
            }

            var points = new List<MapLabelMarker>();
            var seenKeys = new HashSet<string>(StringComparer.Ordinal);
            var mapName = BuildMapName(map);
            var markerRangeId = (uint)map.MapMarkerRange;
            if (markerRangeId == 0)
            {
                labelMarkerStatusCache[cacheKey] = $"Loaded 0 map label marker(s) for territory {territoryTypeId} on active map {mapName} ({mapId}): no MapMarkerRange row was assigned.";
                return [];
            }

            foreach (var subrowCollection in mapMarkerSheet)
            {
                foreach (var marker in subrowCollection)
                {
                    if (marker.RowId != markerRangeId)
                        continue;

                    if (marker.PlaceNameSubtext.ValueNullable is null)
                        continue;

                    var name = NormalizeName(marker.PlaceNameSubtext.Value.Name.ToString());
                    if (string.IsNullOrWhiteSpace(name))
                        continue;

                    var worldPosition = ConvertTextureToWorld(marker.X, marker.Y, map);
                    var mapCoordinates = MapUtil.WorldToMap(new Vector2(worldPosition.X, worldPosition.Z), map);
                    var key = $"{territoryTypeId}:{map.RowId}:{markerRangeId}:{marker.SubrowId}:{name}:{marker.X}:{marker.Y}";
                    if (!seenKeys.Add(key))
                        continue;

                    points.Add(new MapLabelMarker
                    {
                        Key = key,
                        Name = name,
                        MapId = map.RowId,
                        MarkerRangeId = markerRangeId,
                        MapName = mapName,
                        SubrowId = marker.SubrowId,
                        DataType = marker.DataType,
                        Icon = marker.Icon,
                        TextureX = marker.X,
                        TextureY = marker.Y,
                        WorldPosition = worldPosition,
                        MapCoordinates = mapCoordinates,
                    });
                }
            }

            points.Sort(static (left, right) =>
            {
                var byMap = left.MapId.CompareTo(right.MapId);
                if (byMap != 0)
                    return byMap;

                var byMarkerRange = left.MarkerRangeId.CompareTo(right.MarkerRangeId);
                if (byMarkerRange != 0)
                    return byMarkerRange;

                var bySubrow = left.SubrowId.CompareTo(right.SubrowId);
                if (bySubrow != 0)
                    return bySubrow;

                return string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
            });

            labelMarkerStatusCache[cacheKey] = points.Count > 0
                ? $"Loaded {points.Count} map label marker(s) for territory {territoryTypeId} on active map {mapName} ({mapId})."
                : $"Loaded 0 map label marker(s) for territory {territoryTypeId} on active map {mapName} ({mapId}).";

            log.Information($"[ADS] {labelMarkerStatusCache[cacheKey]}");
            return points;
        }
        catch (Exception ex)
        {
            labelMarkerStatusCache[cacheKey] = $"Map-label lookup failed for territory {territoryTypeId} map {mapId}: {ex.Message}";
            log.Warning(ex, $"[ADS] Failed to build map label markers for territory {territoryTypeId} map {mapId}.");
            return [];
        }
    }

    private void LogUnavailableFrontierLabels(uint territoryTypeId, uint mapId, string reason)
        => log.Information($"[ADS] Built 0 frontier label point(s) for territory {territoryTypeId} on map {mapId}; label frontier unavailable ({reason}).");

    private void UpdateHeadingMemory(Vector3? playerPosition, ObservationSnapshot observation)
    {
        if (!playerPosition.HasValue)
            return;

        if (observation.LiveMonsters.Count == 0 && observation.LiveInteractables.Count == 0)
            return;

        headingScoutTarget = null;
        if (!lastProgressSamplePosition.HasValue)
        {
            lastProgressSamplePosition = playerPosition.Value;
            return;
        }

        var flatDelta = new Vector3(
            playerPosition.Value.X - lastProgressSamplePosition.Value.X,
            0f,
            playerPosition.Value.Z - lastProgressSamplePosition.Value.Z);
        var flatDistance = flatDelta.Length();
        if (flatDistance < HeadingSampleMinDistance || flatDistance <= float.Epsilon)
            return;

        currentHeading = Vector3.Normalize(flatDelta);
        lastProgressSamplePosition = playerPosition.Value;
    }

    private DungeonFrontierPoint? SelectHeadingScoutTarget(DutyContextSnapshot context, Vector3 playerPosition)
    {
        if (!currentHeading.HasValue || currentHeading.Value.LengthSquared() <= float.Epsilon)
            return null;

        if (headingScoutTarget is not null && ShouldKeepHeadingScoutTarget(headingScoutTarget, playerPosition, currentHeading.Value))
            return headingScoutTarget;

        headingScoutSequence++;
        var scoutPosition = new Vector3(
            playerPosition.X + (currentHeading.Value.X * HeadingScoutProjectionDistance),
            playerPosition.Y,
            playerPosition.Z + (currentHeading.Value.Z * HeadingScoutProjectionDistance));
        headingScoutTarget = new DungeonFrontierPoint
        {
            Key = $"scout:{context.TerritoryTypeId}:{headingScoutSequence}:{Quantize(scoutPosition)}",
            Name = $"Heading Scout {headingScoutSequence}",
            Position = scoutPosition,
            LevelRowId = 0,
            MapId = context.MapId,
        };
        log.Information(
            $"[ADS] Activated heading frontier scout for territory {context.TerritoryTypeId}: target {FormatVector(scoutPosition)} from heading {FormatVector(currentHeading.Value)}.");
        return headingScoutTarget;
    }

    private void MarkVisitedPoints(IReadOnlyList<DungeonFrontierPoint> points, Vector3 playerPosition, float defaultVisitRadius, float defaultVerticalCap)
    {
        foreach (var point in points)
        {
            var horizontalDistance = GetHorizontalDistance(playerPosition, point.Position);
            var verticalDelta = point.UsePlayerYForNavigation
                ? 0f
                : MathF.Abs(point.Position.Y - playerPosition.Y);
            var visitRadius = point.ArrivalRadiusXz > 0f ? point.ArrivalRadiusXz : defaultVisitRadius;
            if (horizontalDistance <= visitRadius && verticalDelta <= defaultVerticalCap)
                MarkVisited(point, playerPosition);
        }
    }

    private DungeonFrontierPoint? SelectCurrentManualDestination(IReadOnlyList<DungeonFrontierPoint> points, Vector3? playerPosition)
    {
        if (points.Count == 0)
            return null;

        var unvisitedPoints = points
            .Where(point => !visitedFrontierKeys.Contains(point.Key))
            .ToList();
        if (unvisitedPoints.Count == 0)
            return null;

        if (!playerPosition.HasValue)
            return unvisitedPoints
                .OrderBy(point => point.Priority)
                .ThenBy(point => point.Name, StringComparer.OrdinalIgnoreCase)
                .First();

        return BuildNavigationPoint(
            unvisitedPoints
                .Select(point => new
                {
                    Point = point,
                    Distance = GetManualDestinationDistance(playerPosition.Value, point),
                    VerticalDelta = MathF.Abs(point.Position.Y - playerPosition.Value.Y),
                })
                .OrderBy(x => x.Point.Priority)
                .ThenBy(x => x.Distance)
                .ThenBy(x => x.VerticalDelta)
                .ThenBy(x => x.Point.Name, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.Point)
                .First(),
            playerPosition);
    }

    private DungeonFrontierPoint? SelectCurrentTarget(IReadOnlyList<DungeonFrontierPoint> points, Vector3? playerPosition, DungeonFrontierPoint? previousTarget)
    {
        if (points.Count == 0)
            return null;

        var unvisitedPoints = points
            .Where(point => !visitedFrontierKeys.Contains(point.Key))
            .ToList();
        if (unvisitedPoints.Count == 0)
            return null;

        var usesMapMarkerRangeFallback = points.Any(point => point.UsePlayerYForNavigation);
        if (usesMapMarkerRangeFallback && playerPosition.HasValue)
        {
            if (previousTarget is not null
                && unvisitedPoints.Any(point => point.Key == previousTarget.Key)
                && ShouldKeepLabelTarget(previousTarget, playerPosition.Value))
            {
                return BuildNavigationPoint(previousTarget, playerPosition);
            }

            var forwardPoint = SelectForwardLabelPoint(unvisitedPoints, playerPosition.Value);
            if (forwardPoint is not null)
                return BuildNavigationPoint(forwardPoint, playerPosition);
        }

        if (usesMapMarkerRangeFallback)
            return BuildNavigationPoint(SelectNearestPoint(unvisitedPoints, playerPosition), playerPosition);

        var furthestVisitedIndex = -1;
        for (var index = 0; index < points.Count; index++)
        {
            if (visitedFrontierKeys.Contains(points[index].Key))
                furthestVisitedIndex = index;
        }

        if (furthestVisitedIndex >= 0)
        {
            for (var index = furthestVisitedIndex + 1; index < points.Count; index++)
            {
                if (!visitedFrontierKeys.Contains(points[index].Key))
                    return BuildNavigationPoint(points[index], playerPosition);
            }

            return null;
        }

        if (!playerPosition.HasValue)
            return points[0];

        return BuildNavigationPoint(SelectNearestPoint(unvisitedPoints, playerPosition), playerPosition);
    }

    private DungeonFrontierPoint? SelectForwardLabelPoint(IReadOnlyList<DungeonFrontierPoint> points, Vector3 playerPosition)
    {
        if (!currentHeading.HasValue || currentHeading.Value.LengthSquared() <= float.Epsilon)
            return null;

        return points
            .Select(point => new
            {
                Point = point,
                Distance = GetHorizontalDistance(playerPosition, point.Position),
                ForwardDot = GetForwardDot(playerPosition, point.Position, currentHeading.Value),
            })
            .Where(x => x.Distance > FrontierVisitRadius && x.ForwardDot >= LabelFrontierMinForwardDot)
            .OrderBy(x => x.Distance * (2f - x.ForwardDot))
            .ThenBy(x => x.Point.LevelRowId)
            .Select(x => x.Point)
            .FirstOrDefault();
    }

    private bool ShouldKeepLabelTarget(DungeonFrontierPoint point, Vector3 playerPosition)
    {
        if (GetHorizontalDistance(playerPosition, point.Position) <= FrontierVisitRadius)
            return false;

        if (!currentHeading.HasValue || currentHeading.Value.LengthSquared() <= float.Epsilon)
            return true;

        return GetForwardDot(playerPosition, point.Position, currentHeading.Value) >= LabelFrontierRetargetBacktrackDot;
    }

    private static DungeonFrontierPoint SelectNearestPoint(IReadOnlyList<DungeonFrontierPoint> points, Vector3? playerPosition)
    {
        if (!playerPosition.HasValue)
            return points[0];

        return points
            .Select(point => new
            {
                Point = point,
                Distance = GetHorizontalDistance(playerPosition.Value, point.Position),
            })
            .OrderBy(x => x.Distance)
            .ThenBy(x => x.Point.LevelRowId)
            .Select(x => x.Point)
            .First();
    }

    private static bool ShouldKeepHeadingScoutTarget(DungeonFrontierPoint scoutTarget, Vector3 playerPosition, Vector3 heading)
    {
        var flatDelta = new Vector3(
            scoutTarget.Position.X - playerPosition.X,
            0f,
            scoutTarget.Position.Z - playerPosition.Z);
        var flatDistance = flatDelta.Length();
        if (flatDistance <= HeadingScoutAdvanceRadius || flatDistance <= float.Epsilon)
            return false;

        var forwardDot = Vector3.Dot(Vector3.Normalize(flatDelta), heading);
        return forwardDot >= HeadingScoutMinForwardDot;
    }

    private static float GetForwardDot(Vector3 playerPosition, Vector3 targetPosition, Vector3 heading)
    {
        var flatDelta = new Vector3(
            targetPosition.X - playerPosition.X,
            0f,
            targetPosition.Z - playerPosition.Z);
        if (flatDelta.LengthSquared() <= float.Epsilon || heading.LengthSquared() <= float.Epsilon)
            return 0f;

        return Vector3.Dot(Vector3.Normalize(flatDelta), Vector3.Normalize(heading));
    }

    private static string NormalizeName(string value)
        => string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private void GhostCurrentOrLastManualDestination(DungeonFrontierPoint? previousTarget)
    {
        var pointToGhost = previousTarget is { IsManualDestination: true }
            ? previousTarget
            : lastValidManualDestination;
        if (pointToGhost is null)
            return;

        if (!visitedFrontierKeys.Add(pointToGhost.Key))
        {
            ClearRememberedManualDestination(pointToGhost);
            return;
        }

        ClearRememberedManualDestination(pointToGhost);
        RememberGhostedManualDestination(pointToGhost, "BetweenAreas");
        log.Information($"[ADS] Ghosted {GetManualDestinationLabel(pointToGhost)} {pointToGhost.Name} at {FormatVector(pointToGhost.Position)} after BetweenAreas transition.");
    }

    private void RememberGhostedManualDestination(DungeonFrontierPoint point, string reason)
    {
        LastGhostedManualDestination = point;
        LastGhostedManualDestinationUtc = DateTime.UtcNow;
        LastGhostedManualDestinationReason = reason;
    }

    private void RememberManualDestination(DungeonFrontierPoint? point)
    {
        if (point is { IsManualDestination: true })
            lastValidManualDestination = point;
    }

    private void ClearRememberedManualDestination(DungeonFrontierPoint point)
    {
        if (!point.IsManualDestination || lastValidManualDestination is null)
            return;

        if (string.Equals(lastValidManualDestination.Key, point.Key, StringComparison.Ordinal))
            lastValidManualDestination = null;
    }

    private bool HasNoFrontierBlockingLiveObjects(DutyContextSnapshot context, ObservationSnapshot observation, Vector3? playerPosition)
        => !observation.LiveMonsters.Any(x => IsSaneVerticalBlocker(playerPosition, x.Position))
           && !observation.LiveFollowTargets.Any(x => IsSaneVerticalBlocker(playerPosition, x.Position))
           && !observation.LiveInteractables.Any(x => IsEligibleFrontierBlockingInteractable(context, x, playerPosition));

    private bool ShouldPrioritizeManualDestinationBeforeLiveProgression(
        DutyContextSnapshot context,
        ObservationSnapshot observation,
        Vector3? playerPosition,
        IReadOnlyList<DungeonFrontierPoint> manualDestinations)
    {
        if (manualDestinations.Count == 0)
            return false;

        if (observation.LiveMonsters.Any(x => IsSaneVerticalBlocker(playerPosition, x.Position))
            || observation.LiveFollowTargets.Any(x => IsSaneVerticalBlocker(playerPosition, x.Position)))
        {
            return false;
        }

        var bestUnvisitedManualDestination = manualDestinations
            .Where(point => !visitedFrontierKeys.Contains(point.Key))
            .Select(point => new
            {
                Point = point,
                Distance = playerPosition.HasValue ? GetManualDestinationDistance(playerPosition.Value, point) : float.MaxValue,
                VerticalDelta = playerPosition.HasValue ? MathF.Abs(point.Position.Y - playerPosition.Value.Y) : float.MaxValue,
            })
            .OrderBy(x => x.Point.Priority)
            .ThenBy(x => x.Distance)
            .ThenBy(x => x.VerticalDelta)
            .ThenBy(x => x.Point.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (bestUnvisitedManualDestination is null)
            return false;

        var bestLiveProgressionInteractable = observation.LiveInteractables
            .Where(x => IsEligibleFrontierBlockingInteractable(context, x, playerPosition))
            .Select(x => new
            {
                Interactable = x,
                Distance = playerPosition.HasValue ? Vector3.Distance(playerPosition.Value, x.Position) : (float?)null,
                VerticalDelta = playerPosition.HasValue ? MathF.Abs(x.Position.Y - playerPosition.Value.Y) : (float?)null,
            })
            .Select(x => new
            {
                x.Interactable,
                x.Distance,
                x.VerticalDelta,
                Priority = objectPriorityRuleService.GetEffectivePriority(context, x.Interactable, x.Distance, x.VerticalDelta),
            })
            .OrderBy(x => x.Priority)
            .ThenBy(x => x.Distance ?? float.MaxValue)
            .ThenBy(x => x.VerticalDelta ?? float.MaxValue)
            .FirstOrDefault();
        return bestLiveProgressionInteractable is not null
               && ShouldManualDestinationBeatProgressionInteractable(
                   bestUnvisitedManualDestination.Point,
                   bestLiveProgressionInteractable.Interactable,
                   bestLiveProgressionInteractable.Priority);
    }

    private DungeonFrontierPoint? SelectCombatBypassManualDestinationAgainstLiveBlockers(
        DutyContextSnapshot context,
        ObservationSnapshot observation,
        Vector3? playerPosition,
        IReadOnlyList<DungeonFrontierPoint> manualDestinations)
    {
        var bestManualDestination = manualDestinations
            .Where(point => point.AllowCombatBypass && !visitedFrontierKeys.Contains(point.Key))
            .Select(point => new
            {
                Point = point,
                Distance = playerPosition.HasValue ? GetManualDestinationDistance(playerPosition.Value, point) : float.MaxValue,
                VerticalDelta = playerPosition.HasValue ? MathF.Abs(point.Position.Y - playerPosition.Value.Y) : float.MaxValue,
            })
            .OrderBy(x => x.Point.Priority)
            .ThenBy(x => x.Distance)
            .ThenBy(x => x.VerticalDelta)
            .ThenBy(x => x.Point.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (bestManualDestination is null)
            return null;

        if (observation.LiveFollowTargets.Any(x => IsSaneVerticalBlocker(playerPosition, x.Position)))
            return null;

        var bestMonster = observation.LiveMonsters
            .Where(x => IsSaneVerticalBlocker(playerPosition, x.Position))
            .Select(x => new
            {
                Monster = x,
                Distance = playerPosition.HasValue ? Vector3.Distance(playerPosition.Value, x.Position) : (float?)null,
                VerticalDelta = playerPosition.HasValue ? MathF.Abs(x.Position.Y - playerPosition.Value.Y) : (float?)null,
            })
            .Select(x => new
            {
                x.Monster,
                x.Distance,
                x.VerticalDelta,
                Priority = objectPriorityRuleService.GetEffectiveBattleNpcPriority(context, x.Monster, x.Distance, x.VerticalDelta),
            })
            .OrderBy(x => x.Priority)
            .ThenBy(x => x.Distance ?? float.MaxValue)
            .ThenBy(x => x.VerticalDelta ?? float.MaxValue)
            .FirstOrDefault();
        if (bestMonster is null)
            return null;

        if (bestManualDestination.Point.Priority > bestMonster.Priority)
            return null;

        var bestLiveProgressionInteractable = observation.LiveInteractables
            .Where(x => IsEligibleFrontierBlockingInteractable(context, x, playerPosition))
            .Select(x => new
            {
                Interactable = x,
                Distance = playerPosition.HasValue ? Vector3.Distance(playerPosition.Value, x.Position) : (float?)null,
                VerticalDelta = playerPosition.HasValue ? MathF.Abs(x.Position.Y - playerPosition.Value.Y) : (float?)null,
            })
            .Select(x => new
            {
                x.Interactable,
                x.Distance,
                x.VerticalDelta,
                Priority = objectPriorityRuleService.GetEffectivePriority(context, x.Interactable, x.Distance, x.VerticalDelta),
            })
            .OrderBy(x => x.Priority)
            .ThenBy(x => x.Distance ?? float.MaxValue)
            .ThenBy(x => x.VerticalDelta ?? float.MaxValue)
            .FirstOrDefault();
        if (bestLiveProgressionInteractable is not null
            && !ShouldManualDestinationBeatProgressionInteractable(
                bestManualDestination.Point,
                bestLiveProgressionInteractable.Interactable,
                bestLiveProgressionInteractable.Priority))
        {
            return null;
        }

        var selectionLogKey = string.Create(
            CultureInfo.InvariantCulture,
            $"{bestManualDestination.Point.Key}:{bestMonster.Monster.Name}:{bestMonster.Priority}:{bestLiveProgressionInteractable?.Interactable.Name}:{bestLiveProgressionInteractable?.Priority}:{bestLiveProgressionInteractable?.Interactable.Classification}");
        if (loggedCombatBypassManualSelections.Add(selectionLogKey))
        {
            var manualLabel = bestManualDestination.Point.IsManualXyzDestination ? "XYZ" : "map XZ";
            var progressSummary = bestLiveProgressionInteractable is null
                ? "no stronger live progression blocker was visible"
                : $"live progression interactable {bestLiveProgressionInteractable.Interactable.Name} ({bestLiveProgressionInteractable.Interactable.Classification}) at {FormatVector(bestLiveProgressionInteractable.Interactable.Position)} on map {bestLiveProgressionInteractable.Interactable.MapId} resolved at priority {bestLiveProgressionInteractable.Priority} and was intentionally ignored because {DescribeManualDestinationProgressionOverride(bestManualDestination.Point, bestLiveProgressionInteractable.Interactable, bestLiveProgressionInteractable.Priority)}";
            log.Information(
                $"[ADS] Selected force-march {manualLabel} destination {bestManualDestination.Point.Name} at {FormatVector(bestManualDestination.Point.Position)} while live monster pressure remains because manual priority {bestManualDestination.Point.Priority} beat/tied live monster {bestMonster.Monster.Name} ({bestMonster.Priority}) and {progressSummary}.");
        }

        return BuildNavigationPoint(bestManualDestination.Point, playerPosition);
    }

    private DungeonFrontierPoint? SelectPraetoriumOnFootCombatBypassManualDestination(
        DutyContextSnapshot context,
        ObservationSnapshot observation,
        Vector3? playerPosition,
        IReadOnlyList<DungeonFrontierPoint> manualDestinations,
        DungeonFrontierPoint? previousTarget)
    {
        //if (context.Mounted || context.TerritoryTypeId != PraetoriumTerritoryTypeId)
        if (context.Mounted) //because the AI was too stupid to understand
            return null;

        if (!context.InCombat
            && !observation.LiveMonsters.Any(x => IsSaneVerticalBlocker(playerPosition, x.Position)))
        {
            return null;
        }

        if (observation.LiveFollowTargets.Any(x => IsSaneVerticalBlocker(playerPosition, x.Position)))
            return null;

        var currentForceMarchAnchor = GetCurrentAuthoredForceMarchAnchor(manualDestinations, previousTarget);
        if (currentForceMarchAnchor is not null)
            return BuildNavigationPoint(currentForceMarchAnchor, playerPosition);

        var inferredAnchorFromPlayerPosition = false;
        if (!TryGetAuthoredManualAnchorIndex(manualDestinations, previousTarget, out var anchorIndex, out var anchorPoint))
        {
            if (!TryInferPraetoriumOnFootSpatialAnchor(manualDestinations, playerPosition, out anchorIndex, out anchorPoint))
                return null;

            inferredAnchorFromPlayerPosition = true;
            if (anchorPoint.AllowCombatBypass && !visitedFrontierKeys.Contains(anchorPoint.Key))
            {
                var spatialSelectionLogKey = $"praetorium-onfoot-spatial:{anchorPoint.Key}";
                if (loggedCombatBypassManualSelections.Add(spatialSelectionLogKey))
                {
                    var manualLabel = anchorPoint.IsManualXyzDestination ? "XYZ" : "map XZ";
                    var liveMonsterSummary = observation.LiveMonsters.Count == 0
                        ? "local combat state was active without visible live monsters"
                        : $"{observation.LiveMonsters.Count} live monster(s) were visible";
                    log.Information(
                        $"[ADS] Praetorium on-foot authored force-march fallback selected force-march {manualLabel} destination {anchorPoint.Name} at {FormatVector(anchorPoint.Position)} by inferring the nearest authored anchor from the current player position because no prior Praetorium manual anchor was remembered; {liveMonsterSummary}. ADS is preserving the immediate authored handoff instead of yielding to the generic combat-bypass selector.");
                }

                return BuildNavigationPoint(anchorPoint, playerPosition);
            }
        }

        var nextUnvisitedManualDestination = manualDestinations
            .Skip(anchorIndex + 1)
            .FirstOrDefault(point => !visitedFrontierKeys.Contains(point.Key));
        if (nextUnvisitedManualDestination is not { AllowCombatBypass: true } nextForceMarchDestination)
            return null;

        var selectionLogKey = inferredAnchorFromPlayerPosition
            ? $"praetorium-onfoot-spatial:{anchorPoint.Key}:{nextForceMarchDestination.Key}"
            : $"praetorium-onfoot:{anchorPoint.Key}:{nextForceMarchDestination.Key}";
        if (loggedCombatBypassManualSelections.Add(selectionLogKey))
        {
            var manualLabel = nextForceMarchDestination.IsManualXyzDestination ? "XYZ" : "map XZ";
            var liveMonsterSummary = observation.LiveMonsters.Count == 0
                ? "local combat state was active without visible live monsters"
                : $"{observation.LiveMonsters.Count} live monster(s) were visible";
            var anchorSummary = inferredAnchorFromPlayerPosition
                ? $"after inferring {anchorPoint.Name} as the nearest authored Praetorium anchor from the current player position because no prior manual anchor was remembered"
                : $"immediately after {anchorPoint.Name} completed";
            log.Information(
                $"[ADS] Praetorium on-foot authored force-march handoff promoted force-march {manualLabel} destination {nextForceMarchDestination.Name} at {FormatVector(nextForceMarchDestination.Position)} {anchorSummary}; {liveMonsterSummary}. ADS is replicating the mounted force-march behavior for the next authored segment instead of yielding to incidental combat.");
        }

        return BuildNavigationPoint(nextForceMarchDestination, playerPosition);
    }

    private DungeonFrontierPoint? SelectPraetoriumMountedCombatBypassManualDestination(
        DutyContextSnapshot context,
        ObservationSnapshot observation,
        Vector3? playerPosition,
        IReadOnlyList<DungeonFrontierPoint> manualDestinations)
    {
        //if (!context.Mounted || context.TerritoryTypeId != PraetoriumTerritoryTypeId)
        if (!context.Mounted) //because the AI was too stupid to understand
            return null;

        if (!context.InCombat
            && !observation.LiveMonsters.Any(x => IsSaneVerticalBlocker(playerPosition, x.Position)))
        {
            return null;
        }

        if (observation.LiveFollowTargets.Any(x => IsSaneVerticalBlocker(playerPosition, x.Position)))
            return null;

        var bestManualDestination = manualDestinations
            .Where(point => point.AllowCombatBypass && !visitedFrontierKeys.Contains(point.Key))
            .Select(point => new
            {
                Point = point,
                Distance = playerPosition.HasValue ? GetManualDestinationDistance(playerPosition.Value, point) : float.MaxValue,
                VerticalDelta = playerPosition.HasValue ? MathF.Abs(point.Position.Y - playerPosition.Value.Y) : float.MaxValue,
            })
            .OrderBy(x => x.Point.Priority)
            .ThenBy(x => x.Distance)
            .ThenBy(x => x.VerticalDelta)
            .ThenBy(x => x.Point.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (bestManualDestination is null)
            return null;

        var selectionLogKey = $"mounted:{bestManualDestination.Point.Key}";
        if (loggedCombatBypassManualSelections.Add(selectionLogKey))
        {
            var manualLabel = bestManualDestination.Point.IsManualXyzDestination ? "XYZ" : "map XZ";
            var liveMonsterSummary = observation.LiveMonsters.Count == 0
                ? "no live monsters were present, but local mounted combat was active"
                : $"{observation.LiveMonsters.Count} live monster(s) were visible";
            var liveInteractableSummary = observation.LiveInteractables.Count == 0
                ? "no live interactables"
                : $"{observation.LiveInteractables.Count} live interactable(s)";
            log.Information(
                $"[ADS] Praetorium mounted combat fallback selected force-march {manualLabel} destination {bestManualDestination.Point.Name} at {FormatVector(bestManualDestination.Point.Position)} with manual priority {bestManualDestination.Point.Priority}; {liveMonsterSummary} and {liveInteractableSummary}. ADS is preserving the broader authored force-march handoff while mounted combat is active.");
        }

        return BuildNavigationPoint(bestManualDestination.Point, playerPosition);
    }

    private DungeonFrontierPoint? GetCurrentAuthoredForceMarchAnchor(
        IReadOnlyList<DungeonFrontierPoint> manualDestinations,
        DungeonFrontierPoint? previousTarget)
    {
        foreach (var candidate in EnumerateManualAnchorCandidates(previousTarget, includeGhosted: false))
        {
            if (candidate is not { AllowCombatBypass: true })
                continue;

            if (visitedFrontierKeys.Contains(candidate.Key))
                continue;

            var currentPoint = manualDestinations.FirstOrDefault(point => string.Equals(point.Key, candidate.Key, StringComparison.Ordinal));
            if (currentPoint is not null)
                return currentPoint;
        }

        return null;
    }

    private bool TryGetAuthoredManualAnchorIndex(
        IReadOnlyList<DungeonFrontierPoint> manualDestinations,
        DungeonFrontierPoint? previousTarget,
        out int anchorIndex,
        out DungeonFrontierPoint anchorPoint)
    {
        foreach (var candidate in EnumerateManualAnchorCandidates(previousTarget, includeGhosted: true))
        {
            anchorIndex = manualDestinations
                .Select((point, index) => new { Point = point, Index = index })
                .Where(x => string.Equals(x.Point.Key, candidate.Key, StringComparison.Ordinal))
                .Select(x => x.Index)
                .FirstOrDefault(-1);
            if (anchorIndex >= 0)
            {
                anchorPoint = manualDestinations[anchorIndex];
                return true;
            }
        }

        anchorIndex = -1;
        anchorPoint = default!;
        return false;
    }

    private bool TryInferPraetoriumOnFootSpatialAnchor(
        IReadOnlyList<DungeonFrontierPoint> manualDestinations,
        Vector3? playerPosition,
        out int anchorIndex,
        out DungeonFrontierPoint anchorPoint)
    {
        anchorIndex = -1;
        anchorPoint = default!;
        if (!playerPosition.HasValue || manualDestinations.Count == 0)
            return false;

        var nearestCandidate = manualDestinations
            .Select((point, index) => new
            {
                Point = point,
                Index = index,
                IsVisited = visitedFrontierKeys.Contains(point.Key),
                Distance = GetManualDestinationDistance(playerPosition.Value, point),
                VerticalDelta = MathF.Abs(point.Position.Y - playerPosition.Value.Y),
            })
            .OrderBy(x => x.IsVisited)
            .ThenBy(x => x.Distance)
            .ThenBy(x => x.VerticalDelta)
            .ThenBy(x => x.Index)
            .FirstOrDefault();
        if (nearestCandidate is null)
            return false;

        anchorIndex = nearestCandidate.Index;
        anchorPoint = nearestCandidate.Point;
        return true;
    }

    private IEnumerable<DungeonFrontierPoint> EnumerateManualAnchorCandidates(DungeonFrontierPoint? previousTarget, bool includeGhosted)
    {
        if (previousTarget is { IsManualDestination: true })
            yield return previousTarget;

        if (lastValidManualDestination is { IsManualDestination: true } rememberedManualDestination
            && !string.Equals(previousTarget?.Key, rememberedManualDestination.Key, StringComparison.Ordinal))
        {
            yield return rememberedManualDestination;
        }

        if (includeGhosted
            && LastGhostedManualDestination is { IsManualDestination: true } ghostedManualDestination
            && !string.Equals(previousTarget?.Key, ghostedManualDestination.Key, StringComparison.Ordinal)
            && !string.Equals(lastValidManualDestination?.Key, ghostedManualDestination.Key, StringComparison.Ordinal))
        {
            yield return ghostedManualDestination;
        }
    }

    private static bool ShouldManualDestinationBeatProgressionInteractable(
        DungeonFrontierPoint manualDestination,
        ObservedInteractable interactable,
        int interactablePriority)
    {
        if (manualDestination.Priority < interactablePriority)
            return true;

        if (manualDestination.Priority > interactablePriority)
            return false;

        return IsLowerValueProgressionInteractable(interactable);
    }

    private static string DescribeManualDestinationProgressionOverride(
        DungeonFrontierPoint manualDestination,
        ObservedInteractable interactable,
        int interactablePriority)
    {
        if (manualDestination.Priority < interactablePriority)
        {
            return $"manual priority {manualDestination.Priority} beat the live interactable priority {interactablePriority}";
        }

        if (interactable.Classification is InteractableClass.Expendable or InteractableClass.Optional)
        {
            return $"the tie at priority {interactablePriority} was spent on the authored manual waypoint instead of the lower-value {interactable.Classification} interactable";
        }

        return $"manual priority {manualDestination.Priority} beat the live {interactable.Classification} interactable priority {interactablePriority}";
    }

    private static bool IsLowerValueProgressionInteractable(ObservedInteractable interactable)
        => interactable.Classification is InteractableClass.Expendable or InteractableClass.Optional;

    private static bool IsSaneVerticalBlocker(Vector3? playerPosition, Vector3 targetPosition)
        => !playerPosition.HasValue
           || MathF.Abs(targetPosition.Y - playerPosition.Value.Y) <= FrontierBlockingVerticalSanityCap;

    private static string Quantize(Vector3 value)
        => $"{MathF.Round(value.X, 0):0},{MathF.Round(value.Y, 0):0},{MathF.Round(value.Z, 0):0}";

    private static uint BuildLabelSortOrder(MapLabelMarker label)
        => ((label.MapId & 0xFFFF) << 16) | label.SubrowId;

    private static DungeonFrontierPoint BuildNavigationPoint(DungeonFrontierPoint point, Vector3? playerPosition)
    {
        if (!point.UsePlayerYForNavigation || !playerPosition.HasValue)
            return point;

        return new DungeonFrontierPoint
        {
            Key = point.Key,
            Name = point.Name,
            Position = new Vector3(point.Position.X, playerPosition.Value.Y, point.Position.Z),
            LevelRowId = point.LevelRowId,
            MapId = point.MapId,
            Priority = point.Priority,
            MapCoordinates = point.MapCoordinates,
            UsePlayerYForNavigation = true,
            ManualDestinationKind = point.ManualDestinationKind,
            AllowCombatBypass = point.AllowCombatBypass,
            ArrivalRadiusXz = point.ArrivalRadiusXz,
            ArrivalRadius3d = point.ArrivalRadius3d,
        };
    }

    private bool TryResolveActiveMap(DutyContextSnapshot context, out Map map, out string status)
    {
        map = default;
        var mapSheet = dataManager.GetExcelSheet<Map>();
        var territorySheet = dataManager.GetExcelSheet<TerritoryType>();
        if (mapSheet is null || territorySheet is null)
        {
            status = $"Map-row lookup failed for territory {context.TerritoryTypeId}: one or more sheets were unavailable.";
            return false;
        }

        if (context.MapId != 0
            && mapSheet.TryGetRow(context.MapId, out map)
            && map.TerritoryType.RowId == context.TerritoryTypeId)
        {
            var mapSelectionKey = $"{context.TerritoryTypeId}:{map.RowId}";
            if (loggedActiveMapSelections.Add(mapSelectionKey))
            {
                log.Information($"[ADS] Using live map row {map.RowId} ({BuildMapName(map)}) for territory {context.TerritoryTypeId}; frontier labels, map flags, and manual destination resolution are restricted to this sub-area.");
            }

            status = $"Using live map row {map.RowId} ({BuildMapName(map)}) for frontier labels.";
            return true;
        }

        if (territorySheet.TryGetRow(context.TerritoryTypeId, out var territory)
            && territory.Map.RowId != 0
            && mapSheet.TryGetRow(territory.Map.RowId, out map))
        {
            status = $"Live map id {context.MapId} was unavailable; falling back to territory default map row {map.RowId} ({BuildMapName(map)}).";
            return true;
        }

        foreach (var candidate in mapSheet.Where(x => x.TerritoryType.RowId == context.TerritoryTypeId).OrderBy(x => x.RowId))
        {
            map = candidate;
            status = $"Live map id {context.MapId} was unavailable; falling back to first territory map row {map.RowId} ({BuildMapName(map)}).";
            return true;
        }

        status = $"Map-row lookup failed: territory {context.TerritoryTypeId} had no associated map rows.";
        return false;
    }

    private static bool TryParseMapCoordinates(string value, out Vector2 coordinates)
    {
        coordinates = default;
        var parts = value.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
            return false;

        if (!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x))
            return false;

        if (!float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
            return false;

        coordinates = new Vector2(x, z);
        return true;
    }

    private static bool TryParseWorldCoordinates(string value, out Vector3 coordinates)
    {
        coordinates = default;
        var parts = value.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 3)
            return false;

        if (!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x))
            return false;

        if (!float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
            return false;

        if (!float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
            return false;

        coordinates = new Vector3(x, y, z);
        return true;
    }

    private static Vector3 ConvertMapCoordinatesToWorld(Vector2 mapCoordinates, Map map, float currentPlayerY)
        => new(
            ConvertMapCoordinateToWorld(mapCoordinates.X, map.SizeFactor, map.OffsetX),
            currentPlayerY,
            ConvertMapCoordinateToWorld(mapCoordinates.Y, map.SizeFactor, map.OffsetY));

    private static bool DoesManualDestinationRuleMatchActiveMap(ObjectPriorityRule rule, Map map)
    {
        var selector = GetMapLayerSelector(rule);
        if (string.IsNullOrWhiteSpace(selector))
            return true;

        if (uint.TryParse(selector, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mapRowId))
            return mapRowId == map.RowId;

        var activeMapName = BuildMapName(map);
        if (string.Equals(selector, activeMapName, StringComparison.OrdinalIgnoreCase))
            return true;

        var placeName = NormalizeName(map.PlaceName.ValueNullable?.Name.ToString() ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(placeName)
            && string.Equals(selector, placeName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var placeNameSub = NormalizeName(map.PlaceNameSub.ValueNullable?.Name.ToString() ?? string.Empty);
        return !string.IsNullOrWhiteSpace(placeNameSub)
               && string.Equals(selector, placeNameSub, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeMapLayerSelector(string? value)
    {
        var selector = NormalizeName(value ?? string.Empty);
        return string.Equals(selector, "MapXZ", StringComparison.OrdinalIgnoreCase)
               || string.Equals(selector, "XYZ", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : selector;
    }

    private static string GetMapLayerSelector(ObjectPriorityRule rule)
    {
        var explicitLayer = NormalizeMapLayerSelector(rule.Layer);
        if (!string.IsNullOrWhiteSpace(explicitLayer))
            return explicitLayer;

        return NormalizeMapLayerSelector(rule.DestinationType);
    }

    private static float ConvertMapCoordinateToWorld(float mapCoordinate, uint scale, int offset)
    {
        var mapScale = scale / 100f;
        if (mapScale <= float.Epsilon)
            return 0f;

        var textureCoordinate = (((mapCoordinate - 1f) * mapScale) / 41f) * 2048f;
        return (textureCoordinate - (offset + 1024f)) / mapScale;
    }

    private static string BuildMapXzDestinationRuleKey(DutyContextSnapshot context, ObjectPriorityRule rule)
        => $"map-xz:{context.ContentFinderConditionId}:{context.TerritoryTypeId}:{context.MapId}:{rule.Priority}:{rule.ObjectName}:{GetMapLayerSelector(rule)}:{rule.MapCoordinates}";

    private static string BuildXyzDestinationRuleKey(DutyContextSnapshot context, ObjectPriorityRule rule)
        => $"xyz:{context.ContentFinderConditionId}:{context.TerritoryTypeId}:{context.MapId}:{rule.Priority}:{rule.ObjectName}:{GetMapLayerSelector(rule)}:{rule.WorldCoordinates}";

    private static ulong BuildCacheKey(uint territoryTypeId, uint mapId)
        => ((ulong)territoryTypeId << 32) | mapId;

    private void LogMapXzDestinationWarning(string key, string message)
    {
        if (loggedInvalidMapXzDestinationRules.Add(key))
            log.Warning(message);
    }

    private void LogXyzDestinationWarning(string key, string message)
    {
        if (loggedInvalidXyzDestinationRules.Add(key))
            log.Warning(message);
    }

    private static Vector3 ConvertTextureToWorld(short textureX, short textureY, Map map)
    {
        var scale = map.SizeFactor / 100f;
        if (scale <= float.Epsilon)
            return Vector3.Zero;

        var offsetX = map.OffsetX + 1024f;
        var offsetY = map.OffsetY + 1024f;
        return new Vector3(
            (textureX - offsetX) / scale,
            0f,
            (textureY - offsetY) / scale);
    }

    private static string BuildMapName(Map map)
    {
        var placeName = NormalizeName(map.PlaceName.ValueNullable?.Name.ToString() ?? string.Empty);
        var placeNameSub = NormalizeName(map.PlaceNameSub.ValueNullable?.Name.ToString() ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(placeNameSub))
            return placeNameSub;

        if (!string.IsNullOrWhiteSpace(placeName))
            return placeName;

        return $"Map {map.RowId}";
    }

    private static string FormatVector(Vector3 value)
        => string.Create(CultureInfo.InvariantCulture, $"{value.X:0.00},{value.Y:0.00},{value.Z:0.00}");

    private bool IsEligibleFrontierBlockingInteractable(DutyContextSnapshot context, ObservedInteractable interactable, Vector3? playerPosition)
    {
        if (interactable.Classification is not (InteractableClass.Required or InteractableClass.CombatFriendly or InteractableClass.Expendable or InteractableClass.TreasureDoor))
            return false;

        var distance = playerPosition.HasValue
            ? Vector3.Distance(playerPosition.Value, interactable.Position)
            : (float?)null;
        var verticalDelta = playerPosition.HasValue
            ? MathF.Abs(interactable.Position.Y - playerPosition.Value.Y)
            : (float?)null;
        return !objectPriorityRuleService.IsSuppressedByRuleGates(context, interactable, distance, verticalDelta);
    }

    private static float GetManualDestinationDistance(Vector3 playerPosition, DungeonFrontierPoint point)
        => point.IsManualXyzDestination
            ? Vector3.Distance(playerPosition, point.Position)
            : GetHorizontalDistance(playerPosition, point.Position);

    private static bool AllowsCombatBypass(ObjectPriorityRule rule)
        => Enum.TryParse<InteractableClass>(rule.Classification, ignoreCase: true, out var classification)
           && classification is InteractableClass.MapXzForceMarch or InteractableClass.XYZForceMarch;

    private static string GetManualDestinationLabel(DungeonFrontierPoint point)
        => point.IsManualXyzDestination
            ? point.AllowCombatBypass ? "force-march XYZ destination" : "XYZ destination"
            : point.AllowCombatBypass ? "force-march map XZ destination" : "map XZ destination";

    private static float GetHorizontalDistance(Vector3 a, Vector3 b)
    {
        var x = a.X - b.X;
        var z = a.Z - b.Z;
        return MathF.Sqrt((x * x) + (z * z));
    }
}
