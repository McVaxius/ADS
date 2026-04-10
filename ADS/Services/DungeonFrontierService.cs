using System.Globalization;
using System.Numerics;
using ADS.Models;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using Lumina.Excel.Sheets;

namespace ADS.Services;

public sealed class DungeonFrontierService
{
    private const float FrontierVisitRadius = 8f;
    private const float FrontierVisitVerticalCap = 12f;
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
    private readonly Dictionary<uint, IReadOnlyList<DungeonFrontierPoint>> frontierCache = [];
    private readonly Dictionary<uint, IReadOnlyList<MapLabelMarker>> labelMarkerCache = [];
    private readonly Dictionary<uint, string> labelMarkerStatusCache = [];
    private readonly HashSet<string> visitedFrontierKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> loggedInvalidMapXzDestinationRules = new(StringComparer.Ordinal);
    private uint activeDutyKey;
    private Vector3? lastProgressSamplePosition;
    private Vector3? currentHeading;
    private DungeonFrontierPoint? headingScoutTarget;
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

    public int TotalPoints { get; private set; }

    public int VisitedPoints { get; private set; }

    public int ManualMapXzDestinationCount { get; private set; }

    public int VisitedManualMapXzDestinations { get; private set; }

    public void Update(DutyContextSnapshot context, ObservationSnapshot observation)
    {
        var dutyKey = context.ContentFinderConditionId != 0 ? context.ContentFinderConditionId : context.TerritoryTypeId;
        if (dutyKey != activeDutyKey)
        {
            Reset();
            activeDutyKey = dutyKey;
        }

        var previousTarget = CurrentTarget;
        CurrentTarget = null;
        CurrentMode = FrontierMode.None;
        CurrentLabelMarkers = [];
        CurrentLabelStatus = "No current map labels loaded.";
        TotalPoints = 0;
        VisitedPoints = 0;
        ManualMapXzDestinationCount = 0;
        VisitedManualMapXzDestinations = 0;

        if (!context.PluginEnabled || !context.IsLoggedIn || !context.InDuty || !context.IsSupportedDuty || context.TerritoryTypeId == 0)
            return;

        var playerPosition = objectTable.LocalPlayer?.Position;
        UpdateHeadingMemory(playerPosition, observation);
        CurrentLabelMarkers = GetLabelMarkers(context.TerritoryTypeId);
        CurrentLabelStatus = labelMarkerStatusCache.TryGetValue(context.TerritoryTypeId, out var labelStatus)
            ? labelStatus
            : "No cached map-label status.";
        var noLiveObjects = observation.LiveMonsters.Count == 0
            && observation.LiveInteractables.Count == 0
            && observation.LiveFollowTargets.Count == 0;

        var manualMapXzDestinations = playerPosition.HasValue
            ? BuildMapXzDestinationPoints(context, playerPosition.Value)
            : [];
        ManualMapXzDestinationCount = manualMapXzDestinations.Count;
        if (playerPosition.HasValue && manualMapXzDestinations.Count > 0)
            MarkVisitedPoints(manualMapXzDestinations, playerPosition.Value, ManualMapXzDestinationVisitRadius, float.MaxValue);

        VisitedManualMapXzDestinations = manualMapXzDestinations.Count(x => visitedFrontierKeys.Contains(x.Key));
        if (noLiveObjects)
        {
            CurrentTarget = SelectCurrentMapXzDestination(manualMapXzDestinations, playerPosition);
            if (CurrentTarget is not null)
            {
                CurrentMode = FrontierMode.MapXzDestination;
                return;
            }
        }

        var points = GetFrontierPoints(context.TerritoryTypeId);
        TotalPoints = points.Count;
        if (points.Count > 0)
        {
            headingScoutTarget = null;

            if (playerPosition.HasValue)
                MarkVisitedPoints(points, playerPosition.Value, FrontierVisitRadius, FrontierVisitVerticalCap);

            VisitedPoints = points.Count(x => visitedFrontierKeys.Contains(x.Key));
            if (!noLiveObjects)
                return;

            CurrentTarget = SelectCurrentTarget(points, playerPosition, previousTarget);
            if (CurrentTarget is not null)
                CurrentMode = FrontierMode.Label;

            return;
        }

        if (!playerPosition.HasValue)
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
        lastProgressSamplePosition = null;
        currentHeading = null;
        headingScoutTarget = null;
        headingScoutSequence = 0;
        CurrentLabelMarkers = [];
        CurrentLabelStatus = "No current map labels loaded.";
        ManualMapXzDestinationCount = 0;
        VisitedManualMapXzDestinations = 0;
    }

    public void MarkVisited(DungeonFrontierPoint point, Vector3 playerPosition)
    {
        if (!visitedFrontierKeys.Add(point.Key))
            return;

        if (point.IsManualMapXzDestination)
        {
            log.Information($"[ADS] Ghosted map XZ destination {point.Name} at {FormatVector(point.Position)} after reaching XZ {GetHorizontalDistance(playerPosition, point.Position):0.0}y.");
        }
    }

    private IReadOnlyList<DungeonFrontierPoint> GetFrontierPoints(uint territoryTypeId)
    {
        if (frontierCache.TryGetValue(territoryTypeId, out var cached))
            return cached;

        var built = BuildFrontierPoints(territoryTypeId);
        frontierCache[territoryTypeId] = built;
        return built;
    }

    private IReadOnlyList<MapLabelMarker> GetLabelMarkers(uint territoryTypeId)
    {
        if (labelMarkerCache.TryGetValue(territoryTypeId, out var cached))
            return cached;

        var built = BuildLabelMarkers(territoryTypeId);
        labelMarkerCache[territoryTypeId] = built;
        return built;
    }

    private IReadOnlyList<DungeonFrontierPoint> BuildMapXzDestinationPoints(DutyContextSnapshot context, Vector3 playerPosition)
    {
        var destinationRules = objectPriorityRuleService.GetMapXzDestinationRules(context);
        if (destinationRules.Count == 0)
            return [];

        if (!TryGetCurrentMap(context.TerritoryTypeId, out var map))
        {
            LogMapXzDestinationWarning(
                $"map-missing:{context.TerritoryTypeId}",
                $"[ADS] Map XZ destination rules are configured for territory {context.TerritoryTypeId}, but ADS could not resolve the active map row.");
            return [];
        }

        var points = new List<DungeonFrontierPoint>();
        foreach (var rule in destinationRules)
        {
            var ruleKey = BuildMapXzDestinationRuleKey(context, rule);
            if (!TryParseMapCoordinates(rule.MapCoordinates, out var mapCoordinates))
            {
                LogMapXzDestinationWarning(
                    $"bad-coordinates:{ruleKey}",
                    $"[ADS] Ignoring Map XZ destination rule {rule.ObjectName}: could not parse mapCoordinates '{rule.MapCoordinates}'. Use a value like 11.3,10.4.");
                continue;
            }

            var worldPosition = ConvertMapCoordinatesToWorld(mapCoordinates, map, playerPosition.Y);
            var name = string.IsNullOrWhiteSpace(rule.ObjectName)
                ? $"Map XZ {mapCoordinates.X:0.0}, {mapCoordinates.Y:0.0}"
                : rule.ObjectName;

            points.Add(new DungeonFrontierPoint
            {
                Key = $"{ruleKey}:{map.RowId}:{mapCoordinates.X:0.###},{mapCoordinates.Y:0.###}",
                Name = name,
                Position = worldPosition,
                LevelRowId = 0,
                Priority = rule.Priority,
                MapCoordinates = mapCoordinates,
                UsePlayerYForNavigation = true,
                IsManualMapXzDestination = true,
                ArrivalRadiusXz = ManualMapXzDestinationVisitRadius,
            });
        }

        return points;
    }

    private IReadOnlyList<DungeonFrontierPoint> BuildFrontierPoints(uint territoryTypeId)
    {
        try
        {
            var territorySheet = dataManager.GetExcelSheet<TerritoryType>();
            var levelSheet = dataManager.GetExcelSheet<Level>();
            var mapMarkerSheet = dataManager.GetSubrowExcelSheet<MapMarker>();
            if (territorySheet is null || levelSheet is null || mapMarkerSheet is null)
            {
                return BuildMapMarkerRangeFrontierPoints(territoryTypeId, "level-backed sheet lookup failed");
            }

            if (!territorySheet.TryGetRow(territoryTypeId, out var territory))
            {
                LogUnavailableFrontierLabels(territoryTypeId, "territory row was missing");
                return [];
            }

            var mapId = territory.Map.RowId;
            var levelsByRowId = levelSheet
                .Where(level => level.Territory.RowId == territoryTypeId && level.Map.RowId == mapId)
                .ToDictionary(level => level.RowId, level => level);
            if (levelsByRowId.Count == 0)
            {
                return BuildMapMarkerRangeFrontierPoints(territoryTypeId, $"no matching Level rows for map {mapId}");
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
                    var dedupeKey = $"{territoryTypeId}:{name}:{Quantize(position)}";
                    if (!seenKeys.Add(dedupeKey))
                        continue;

                    points.Add(new DungeonFrontierPoint
                    {
                        Key = dedupeKey,
                        Name = name,
                        Position = position,
                        LevelRowId = level.RowId,
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
                log.Information($"[ADS] Built {points.Count} frontier label point(s) for territory {territoryTypeId}.");

            return points.Count > 0
                ? points
                : BuildMapMarkerRangeFrontierPoints(territoryTypeId, $"matched {levelsByRowId.Count} Level row(s) but found no DataKey-backed MapMarker labels");
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"[ADS] Failed to build frontier label points for territory {territoryTypeId}.");
            return BuildMapMarkerRangeFrontierPoints(territoryTypeId, $"level-backed lookup threw {ex.GetType().Name}");
        }
    }

    private IReadOnlyList<DungeonFrontierPoint> BuildMapMarkerRangeFrontierPoints(uint territoryTypeId, string levelBackedFailureReason)
    {
        var labels = GetLabelMarkers(territoryTypeId);
        if (labels.Count == 0)
        {
            LogUnavailableFrontierLabels(territoryTypeId, $"{levelBackedFailureReason}; MapMarkerRange fallback produced 0 labels");
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
                UsePlayerYForNavigation = true,
            })
            .ToList();

        log.Information($"[ADS] Built {points.Count} frontier MapMarkerRange point(s) for territory {territoryTypeId} after level-backed frontier was unavailable ({levelBackedFailureReason}).");
        return points;
    }

    private IReadOnlyList<MapLabelMarker> BuildLabelMarkers(uint territoryTypeId)
    {
        try
        {
            var territorySheet = dataManager.GetExcelSheet<TerritoryType>();
            var mapSheet = dataManager.GetExcelSheet<Map>();
            var mapMarkerSheet = dataManager.GetSubrowExcelSheet<MapMarker>();
            if (territorySheet is null || mapSheet is null || mapMarkerSheet is null)
            {
                labelMarkerStatusCache[territoryTypeId] = "Map-label lookup failed: one or more sheets were unavailable.";
                return [];
            }

            if (!territorySheet.TryGetRow(territoryTypeId, out var territory))
            {
                labelMarkerStatusCache[territoryTypeId] = $"Map-label lookup failed: territory {territoryTypeId} was not found.";
                return [];
            }

            var maps = mapSheet
                .Where(x => x.TerritoryType.RowId == territoryTypeId)
                .OrderBy(x => x.RowId)
                .ToList();
            if (maps.Count == 0 && territory.Map.RowId != 0 && mapSheet.TryGetRow(territory.Map.RowId, out var fallbackMap))
                maps.Add(fallbackMap);

            if (maps.Count == 0)
            {
                labelMarkerStatusCache[territoryTypeId] = $"Map-label lookup failed: territory {territoryTypeId} had no associated map rows.";
                return [];
            }

            var points = new List<MapLabelMarker>();
            var seenKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var map in maps)
            {
                var mapName = BuildMapName(map);
                var markerRangeId = (uint)map.MapMarkerRange;
                if (markerRangeId == 0)
                    continue;

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

            labelMarkerStatusCache[territoryTypeId] = points.Count > 0
                ? $"Loaded {points.Count} map label marker(s) for territory {territoryTypeId}."
                : $"Loaded 0 map label marker(s) for territory {territoryTypeId}.";

            log.Information($"[ADS] {labelMarkerStatusCache[territoryTypeId]}");
            return points;
        }
        catch (Exception ex)
        {
            labelMarkerStatusCache[territoryTypeId] = $"Map-label lookup failed for territory {territoryTypeId}: {ex.Message}";
            log.Warning(ex, $"[ADS] Failed to build map label markers for territory {territoryTypeId}.");
            return [];
        }
    }

    private void LogUnavailableFrontierLabels(uint territoryTypeId, string reason)
        => log.Information($"[ADS] Built 0 frontier label point(s) for territory {territoryTypeId}; label frontier unavailable ({reason}).");

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

    private DungeonFrontierPoint? SelectCurrentMapXzDestination(IReadOnlyList<DungeonFrontierPoint> points, Vector3? playerPosition)
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
                    Distance = GetHorizontalDistance(playerPosition.Value, point.Position),
                })
                .OrderBy(x => x.Point.Priority)
                .ThenBy(x => x.Distance)
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
            Priority = point.Priority,
            MapCoordinates = point.MapCoordinates,
            UsePlayerYForNavigation = true,
            IsManualMapXzDestination = point.IsManualMapXzDestination,
            ArrivalRadiusXz = point.ArrivalRadiusXz,
        };
    }

    private bool TryGetCurrentMap(uint territoryTypeId, out Map map)
    {
        map = default;
        var territorySheet = dataManager.GetExcelSheet<TerritoryType>();
        var mapSheet = dataManager.GetExcelSheet<Map>();
        if (territorySheet is null || mapSheet is null)
            return false;

        if (territorySheet.TryGetRow(territoryTypeId, out var territory)
            && territory.Map.RowId != 0
            && mapSheet.TryGetRow(territory.Map.RowId, out map))
        {
            return true;
        }

        foreach (var candidate in mapSheet.Where(x => x.TerritoryType.RowId == territoryTypeId).OrderBy(x => x.RowId))
        {
            map = candidate;
            return true;
        }

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

    private static Vector3 ConvertMapCoordinatesToWorld(Vector2 mapCoordinates, Map map, float currentPlayerY)
        => new(
            ConvertMapCoordinateToWorld(mapCoordinates.X, map.SizeFactor, map.OffsetX),
            currentPlayerY,
            ConvertMapCoordinateToWorld(mapCoordinates.Y, map.SizeFactor, map.OffsetY));

    private static float ConvertMapCoordinateToWorld(float mapCoordinate, uint scale, int offset)
    {
        var mapScale = scale / 100f;
        if (mapScale <= float.Epsilon)
            return -offset;

        return (((mapCoordinate - 1f) * mapScale) / 41f * 2048f) - offset;
    }

    private static string BuildMapXzDestinationRuleKey(DutyContextSnapshot context, ObjectPriorityRule rule)
        => $"map-xz:{context.ContentFinderConditionId}:{context.TerritoryTypeId}:{rule.Priority}:{rule.ObjectName}:{rule.MapCoordinates}";

    private void LogMapXzDestinationWarning(string key, string message)
    {
        if (loggedInvalidMapXzDestinationRules.Add(key))
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

    private static float GetHorizontalDistance(Vector3 a, Vector3 b)
    {
        var x = a.X - b.X;
        var z = a.Z - b.Z;
        return MathF.Sqrt((x * x) + (z * z));
    }
}
