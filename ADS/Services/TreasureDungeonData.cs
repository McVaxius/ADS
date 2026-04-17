using System.Numerics;
using ADS.Models;

namespace ADS.Services;

public static class TreasureDungeonData
{
    private readonly record struct RoutePoint(Vector3 Position, string Label);

    private readonly record struct TreasureRoute(RoutePoint Start, RoutePoint[] Doors);

    private static readonly Dictionary<uint, TreasureRoute> Routes = new()
    {
        {
            712,
            new TreasureRoute(
                new RoutePoint(new Vector3(0.018579919f, 149.79604f, 388.26758f), "Dungeon Start"),
                [
                    new RoutePoint(new Vector3(-22.346756f, 99.705315f, 277.46317f), "Room 1 Left Door"),
                    new RoutePoint(new Vector3(23.240828f, 99.0855f, 276.92383f), "Room 1 Right Door"),
                    new RoutePoint(new Vector3(-22.666056f, 49.53088f, 157.3789f), "Room 2 Left Door"),
                    new RoutePoint(new Vector3(22.353483f, 49.650784f, 157.34358f), "Room 2 Right Door"),
                    new RoutePoint(new Vector3(-22.334154f, -0.17543781f, 37.72586f), "Room 3 Left Door"),
                    new RoutePoint(new Vector3(22.305756f, -0.16592562f, 37.71951f), "Room 3 Right Door"),
                    new RoutePoint(new Vector3(-22.36422f, -50.172253f, -82.23673f), "Room 4 Left Door"),
                    new RoutePoint(new Vector3(22.503517f, -50.322968f, -82.44588f), "Room 4 Right Door"),
                    new RoutePoint(new Vector3(-22.151629f, -100.21078f, -202.53842f), "Room 5 Left Door"),
                    new RoutePoint(new Vector3(22.411543f, -100.280045f, -202.4386f), "Room 5 Right Door"),
                    new RoutePoint(new Vector3(-23.203735f, -150.77336f, -322.78708f), "Room 6 Left Door"),
                    new RoutePoint(new Vector3(22.608999f, -150.35568f, -322.41605f), "Room 6 Right Door"),
                ])
        },
        {
            725,
            new TreasureRoute(
                new RoutePoint(new Vector3(0.018579919f, 149.79604f, 388.26758f), "Dungeon Start"),
                [
                    new RoutePoint(new Vector3(-22.346756f, 99.705315f, 277.46317f), "Room 1 Left Door"),
                    new RoutePoint(new Vector3(0.15397932f, 100.005486f, 267.77084f), "Room 1 Centre Door"),
                    new RoutePoint(new Vector3(23.240828f, 99.0855f, 276.92383f), "Room 1 Right Door"),
                    new RoutePoint(new Vector3(-22.666056f, 49.53088f, 157.3789f), "Room 2 Left Door"),
                    new RoutePoint(new Vector3(0.23556912f, 49.569042f, 147.67918f), "Room 2 Centre Door"),
                    new RoutePoint(new Vector3(22.353483f, 49.650784f, 157.34358f), "Room 2 Right Door"),
                    new RoutePoint(new Vector3(-22.334154f, -0.17543781f, 37.72586f), "Room 3 Left Door"),
                    new RoutePoint(new Vector3(0.18939842f, -0.6225517f, 27.164404f), "Room 3 Centre Door"),
                    new RoutePoint(new Vector3(22.305756f, -0.16592562f, 37.71951f), "Room 3 Right Door"),
                    new RoutePoint(new Vector3(-22.36422f, -50.172253f, -82.23673f), "Room 4 Left Door"),
                    new RoutePoint(new Vector3(-0.35707533f, -50.35107f, -92.114006f), "Room 4 Centre Door"),
                    new RoutePoint(new Vector3(22.503517f, -50.322968f, -82.44588f), "Room 4 Right Door"),
                    new RoutePoint(new Vector3(-22.151629f, -100.21078f, -202.53842f), "Room 5 Left Door"),
                    new RoutePoint(new Vector3(-0.24459839f, -100.71657f, -213.06108f), "Room 5 Centre Door"),
                    new RoutePoint(new Vector3(22.411543f, -100.280045f, -202.4386f), "Room 5 Right Door"),
                    new RoutePoint(new Vector3(-23.203735f, -150.77336f, -322.78708f), "Room 6 Left Door"),
                    new RoutePoint(new Vector3(0.18377349f, -150.18068f, -331.6725f), "Room 6 Centre Door"),
                    new RoutePoint(new Vector3(22.608999f, -150.35568f, -322.41605f), "Room 6 Right Door"),
                ])
        },
        {
            558,
            new TreasureRoute(
                new RoutePoint(new Vector3(1.0083783f, 0.19999814f, 340.36688f), "Room 1 Start"),
                [
                    new RoutePoint(new Vector3(-0.016964452f, -7.800004f, 217.08427f), "Room 2"),
                    new RoutePoint(new Vector3(0.0065348446f, -15.800005f, 92.169876f), "Room 3"),
                    new RoutePoint(new Vector3(-0.12571298f, -23.800001f, -30.496042f), "Room 4"),
                    new RoutePoint(new Vector3(0.25867504f, -31.72483f, -157.66818f), "Room 5"),
                    new RoutePoint(new Vector3(-0.0969127f, -39.77959f, -282.12805f), "Room 6"),
                    new RoutePoint(new Vector3(-0.095316514f, -47.701828f, -403.92584f), "Room 7"),
                ])
        },
        {
            879,
            new TreasureRoute(
                new RoutePoint(new Vector3(0.3018191f, -39.97151f, 142.62704f), "Room 1 Start"),
                [
                    new RoutePoint(new Vector3(28.071524f, -39.235474f, 101.0369f), "Room 2 Left Door"),
                    new RoutePoint(new Vector3(28.071524f, -39.235474f, 101.0369f), "Room 2 Right Door"),
                    new RoutePoint(new Vector3(-29.093864f, 1.1753497f, -29.101763f), "Room 3 Left Door"),
                    new RoutePoint(new Vector3(29.33053f, 1.2513843f, -29.013733f), "Room 3 Right Door"),
                    new RoutePoint(new Vector3(-29.061462f, 41.129097f, -158.93839f), "Room 4 Left Door"),
                    new RoutePoint(new Vector3(29.22315f, 41.200306f, -158.97258f), "Room 4 Right Door"),
                    new RoutePoint(new Vector3(-28.825865f, 81.00473f, -288.78784f), "Room 5 Left Door"),
                    new RoutePoint(new Vector3(29.285349f, 81.2058f, -288.87875f), "Room 5 Right Door"),
                ])
        },
        {
            1000,
            new TreasureRoute(
                new RoutePoint(new Vector3(0.03230051f, 20.000008f, 254.26851f), "Room 1 Start"),
                [
                    new RoutePoint(new Vector3(80.953316f, -10.038639f, 101.36717f), "Room 2 Left Door"),
                    new RoutePoint(new Vector3(138.77351f, -10.038636f, 101.35768f), "Room 2 Right Door"),
                    new RoutePoint(new Vector3(81.37923f, -10.03865f, -48.60507f), "Room 3 Left Door"),
                    new RoutePoint(new Vector3(138.87137f, -10.03865f, -48.890167f), "Room 3 Right Door"),
                    new RoutePoint(new Vector3(-138.5103f, 19.96135f, -168.72546f), "Room 4 Left Door"),
                    new RoutePoint(new Vector3(138.79027f, -10.03865f, -48.79255f), "Room 4 Right Door"),
                    new RoutePoint(new Vector3(-138.91505f, 19.961369f, -319.07587f), "Room 5 Left Door"),
                    new RoutePoint(new Vector3(-81.35945f, 19.961378f, -318.83533f), "Room 5 Right Door"),
                ])
        },
        {
            1209,
            new TreasureRoute(
                new RoutePoint(new Vector3(0.1223174f, -400.0f, 377.30017f), "Room 1 Start"),
                [
                    new RoutePoint(new Vector3(-35.06371f, -400.00003f, 341.5195f), "Room 2 Left Door"),
                    new RoutePoint(new Vector3(35.617687f, -400.0f, 341.68155f), "Room 2 Right Door"),
                    new RoutePoint(new Vector3(-35.291565f, -400.0f, 156.95146f), "Room 3 Left Door"),
                    new RoutePoint(new Vector3(35.601677f, -400.0f, 156.61826f), "Room 3 Right Door"),
                    new RoutePoint(new Vector3(124.29012f, -290.00003f, -16.546095f), "Room 4 Left Door"),
                    new RoutePoint(new Vector3(195.96709f, -290.00015f, -16.546623f), "Room 4 Right Door"),
                    new RoutePoint(new Vector3(-232.17561f, -169.0f, -180.32594f), "Room 5 Left Door"),
                    new RoutePoint(new Vector3(-162.12967f, -169.00015f, -179.9811f), "Room 5 Right Door"),
                ])
        },
    };

    private static readonly Dictionary<uint, string> DoorNames = new()
    {
        { 558, "Vault Door" },
        { 712, "Sluice Gate" },
        { 725, "Sluice Gate" },
        { 879, "Elaborate Gate" },
        { 1000, "Stage Door" },
        { 1209, "Vault Door" },
    };

    private static readonly Dictionary<uint, string> SphereNames = new()
    {
        { 794, "Arcane Sphere" },
        { 924, "Arcane Sphere" },
        { 1123, "Arcane Sphere" },
        { 1279, "Hypnoslot Machine" },
    };

    private static readonly HashSet<uint> SupportedDutyTerritories = Routes.Keys
        .Concat(DoorNames.Keys)
        .Concat(SphereNames.Keys)
        .ToHashSet();

    public static bool TryGetInteractableClassification(uint territoryId, string objectName, out InteractableClass classification)
    {
        if (IsRepeatableProgressionInteractable(territoryId, objectName))
        {
            classification = InteractableClass.Required;
            return true;
        }

        classification = default;
        return false;
    }

    public static bool IsRepeatableProgressionInteractable(uint territoryId, string objectName)
        => SphereNames.TryGetValue(territoryId, out var sphereName)
            && string.Equals(objectName, sphereName, StringComparison.OrdinalIgnoreCase);

    public static bool HasRoute(uint territoryId)
        => Routes.ContainsKey(territoryId);

    public static bool IsSupportedDutyTerritory(uint territoryId)
        => SupportedDutyTerritories.Contains(territoryId);

    public static IReadOnlyCollection<uint> GetSupportedDutyTerritories()
        => SupportedDutyTerritories;

    public static IReadOnlyList<DungeonFrontierPoint> BuildRoutePoints(uint territoryId, uint mapId)
    {
        if (!Routes.TryGetValue(territoryId, out var route))
            return [];

        var points = new List<DungeonFrontierPoint>
        {
            CreateRoutePoint(territoryId, mapId, "start", route.Start, 0),
        };

        for (var i = 0; i < route.Doors.Length; i++)
            points.Add(CreateRoutePoint(territoryId, mapId, $"door:{i}", route.Doors[i], i + 1));

        return points;
    }

    public static DungeonFrontierPoint? FindNearestDoorTransition(uint territoryId, uint mapId, Vector3 anchorPosition, float maxRange = 35f)
    {
        if (!Routes.TryGetValue(territoryId, out var route))
            return null;

        return route.Doors
            .Select((point, index) => new
            {
                Point = CreateRoutePoint(territoryId, mapId, $"door:{index}", point, index + 1),
                Distance = Vector3.Distance(anchorPosition, point.Position),
            })
            .Where(x => x.Distance <= maxRange)
            .OrderBy(x => x.Distance)
            .Select(x => x.Point)
            .FirstOrDefault();
    }

    private static DungeonFrontierPoint CreateRoutePoint(uint territoryId, uint mapId, string keySuffix, RoutePoint routePoint, int priority)
        => new()
        {
            Key = $"treasure:{territoryId}:{keySuffix}",
            Name = routePoint.Label,
            Position = routePoint.Position,
            LevelRowId = 0,
            MapId = mapId,
            Priority = priority,
            ManualDestinationKind = ManualDestinationKind.None,
            ArrivalRadiusXz = 6.0f,
        };
}
