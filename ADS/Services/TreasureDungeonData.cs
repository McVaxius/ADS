using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using ADS.Models;
using Dalamud.Plugin.Services;

namespace ADS.Services;

public static class TreasureDungeonData
{
    public const string FileName = "treasure-dungeon-data.json";

    private static readonly TimeSpan ReloadPollInterval = TimeSpan.FromSeconds(1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        Converters =
        {
            new JsonStringEnumConverter<TreasureRouteKind>(),
        },
    };

    private static readonly object Gate = new();

    private readonly record struct RoutePoint(Vector3 Position, string Label, int Room, TreasureRoutePointSlot Slot);

    private readonly record struct TreasureRoute(
        bool Enabled,
        uint TerritoryTypeId,
        string DutyName,
        TreasureRouteKind RouteKind,
        RoutePoint Start,
        RoutePoint[] Doors);

    private readonly record struct BuiltInRouteRoom(
        int Room,
        RoutePoint? Left,
        RoutePoint? Middle,
        RoutePoint? Right);

    private enum TreasureRoutePointSlot
    {
        Unknown = 0,
        Start,
        Left,
        Centre,
        Right,
        Single,
    }

    private static readonly Dictionary<uint, TreasureRoute> BuiltInRoutes = new()
    {
        {
            712,
            new TreasureRoute(
                true,
                712,
                "the Lost Canals of Uznair",
                TreasureRouteKind.Treasure,
                Point(0.018579919f, 149.79604f, 388.26758f, "Dungeon Start", 0, TreasureRoutePointSlot.Start),
                [
                    Point(-22.346756f, 99.705315f, 277.46317f, "Room 1 Left Door", 1, TreasureRoutePointSlot.Left),
                    Point(22.346756f, 99.0855f, 277.46317f, "Room 1 Right Door", 1, TreasureRoutePointSlot.Right),
                    Point(-22.346756f, 49.53088f, 157.46317f, "Room 2 Left Door", 2, TreasureRoutePointSlot.Left),
                    Point(22.346756f, 49.650784f, 157.46317f, "Room 2 Right Door", 2, TreasureRoutePointSlot.Right),
                    Point(-22.346756f, -0.17543781f, 37.72586f, "Room 3 Left Door", 3, TreasureRoutePointSlot.Left),
                    Point(22.346756f, -0.16592562f, 37.72586f, "Room 3 Right Door", 3, TreasureRoutePointSlot.Right),
                    Point(-22.346756f, -50.172253f, -82.23673f, "Room 4 Left Door", 4, TreasureRoutePointSlot.Left),
                    Point(22.346756f, -50.322968f, -82.23673f, "Room 4 Right Door", 4, TreasureRoutePointSlot.Right),
                    Point(-22.346756f, -100.21078f, -82.23673f, "Room 5 Left Door", 5, TreasureRoutePointSlot.Left),
                    Point(22.346756f, -100.280045f, -82.23673f, "Room 5 Right Door", 5, TreasureRoutePointSlot.Right),
                    Point(-22.346756f, -150.77336f, -322.78708f, "Room 6 Left Door", 6, TreasureRoutePointSlot.Left),
                    Point(22.346756f, -150.35568f, -322.78708f, "Room 6 Right Door", 6, TreasureRoutePointSlot.Right),
                ])
        },
        {
            725,
            new TreasureRoute(
                true,
                725,
                "the Hidden Canals of Uznair",
                TreasureRouteKind.Thief,
                Point(0.018579919f, 149.79604f, 388.26758f, "Dungeon Start", 0, TreasureRoutePointSlot.Start),
                [
                    Point(-22.346756f, 99.705315f, 277.46317f, "Room 1 Left Door", 1, TreasureRoutePointSlot.Left),
                    Point(0.15397932f, 100.005486f, 267.77084f, "Room 1 Centre Door", 1, TreasureRoutePointSlot.Centre),
                    Point(23.240828f, 99.0855f, 276.92383f, "Room 1 Right Door", 1, TreasureRoutePointSlot.Right),
                    Point(-22.666056f, 49.53088f, 157.3789f, "Room 2 Left Door", 2, TreasureRoutePointSlot.Left),
                    Point(0.23556912f, 49.569042f, 147.67918f, "Room 2 Centre Door", 2, TreasureRoutePointSlot.Centre),
                    Point(22.353483f, 49.650784f, 157.34358f, "Room 2 Right Door", 2, TreasureRoutePointSlot.Right),
                    Point(-22.334154f, -0.17543781f, 37.72586f, "Room 3 Left Door", 3, TreasureRoutePointSlot.Left),
                    Point(0.18939842f, -0.6225517f, 27.164404f, "Room 3 Centre Door", 3, TreasureRoutePointSlot.Centre),
                    Point(22.305756f, -0.16592562f, 37.71951f, "Room 3 Right Door", 3, TreasureRoutePointSlot.Right),
                    Point(-22.36422f, -50.172253f, -82.23673f, "Room 4 Left Door", 4, TreasureRoutePointSlot.Left),
                    Point(-0.35707533f, -50.35107f, -92.114006f, "Room 4 Centre Door", 4, TreasureRoutePointSlot.Centre),
                    Point(22.503517f, -50.322968f, -82.44588f, "Room 4 Right Door", 4, TreasureRoutePointSlot.Right),
                    Point(-22.151629f, -100.21078f, -202.53842f, "Room 5 Left Door", 5, TreasureRoutePointSlot.Left),
                    Point(-0.24459839f, -100.71657f, -213.06108f, "Room 5 Centre Door", 5, TreasureRoutePointSlot.Centre),
                    Point(22.411543f, -100.280045f, -202.4386f, "Room 5 Right Door", 5, TreasureRoutePointSlot.Right),
                    Point(-23.203735f, -150.77336f, -322.78708f, "Room 6 Left Door", 6, TreasureRoutePointSlot.Left),
                    Point(0.18377349f, -150.18068f, -331.6725f, "Room 6 Centre Door", 6, TreasureRoutePointSlot.Centre),
                    Point(22.608999f, -150.35568f, -322.41605f, "Room 6 Right Door", 6, TreasureRoutePointSlot.Right),
                ])
        },
        {
            558,
            new TreasureRoute(
                true,
                558,
                "the Aquapolis",
                TreasureRouteKind.Unknown,
                Point(1.0083783f, 0.19999814f, 340.36688f, "Room 1 Start", 1, TreasureRoutePointSlot.Start),
                [
                    Point(-0.016964452f, -7.800004f, 217.08427f, "Room 2", 2, TreasureRoutePointSlot.Single),
                    Point(0.0065348446f, -15.800005f, 92.169876f, "Room 3", 3, TreasureRoutePointSlot.Single),
                    Point(-0.12571298f, -23.800001f, -30.496042f, "Room 4", 4, TreasureRoutePointSlot.Single),
                    Point(0.25867504f, -31.72483f, -157.66818f, "Room 5", 5, TreasureRoutePointSlot.Single),
                    Point(-0.0969127f, -39.77959f, -282.12805f, "Room 6", 6, TreasureRoutePointSlot.Single),
                    Point(-0.095316514f, -47.701828f, -403.92584f, "Room 7", 7, TreasureRoutePointSlot.Single),
                ])
        },
        {
            879,
            new TreasureRoute(
                true,
                879,
                "The Dungeons of Lyhe Ghiah",
                TreasureRouteKind.Treasure,
                Point(0.3018191f, -39.97151f, 142.62704f, "Room 1 Start", 1, TreasureRoutePointSlot.Start),
                [
                    Point(-28.071524f, -39.235474f, 101.0369f, "Room 2 Left Door", 2, TreasureRoutePointSlot.Left),
                    Point(28.071524f, -39.235474f, 101.0369f, "Room 2 Right Door", 2, TreasureRoutePointSlot.Right),
                    Point(-29.093864f, 1.1753497f, -29.101763f, "Room 3 Left Door", 3, TreasureRoutePointSlot.Left),
                    Point(29.33053f, 1.2513843f, -29.013733f, "Room 3 Right Door", 3, TreasureRoutePointSlot.Right),
                    Point(-29.061462f, 41.129097f, -158.93839f, "Room 4 Left Door", 4, TreasureRoutePointSlot.Left),
                    Point(29.22315f, 41.200306f, -158.97258f, "Room 4 Right Door", 4, TreasureRoutePointSlot.Right),
                    Point(-28.825865f, 81.00473f, -288.78784f, "Room 5 Left Door", 5, TreasureRoutePointSlot.Left),
                    Point(29.285349f, 81.2058f, -288.87875f, "Room 5 Right Door", 5, TreasureRoutePointSlot.Right),
                ])
        },
        {
            1000,
            new TreasureRoute(
                true,
                1000,
                "the Excitatron 6000",
                TreasureRouteKind.Treasure,
                Point(0.03230051f, 20.000008f, 254.26851f, "Room 1 Start", 1, TreasureRoutePointSlot.Start),
                [
                    Point(80.953316f, -10.038639f, 101.36717f, "Room 2 Left Door", 2, TreasureRoutePointSlot.Left),
                    Point(138.77351f, -10.038636f, 101.35768f, "Room 2 Right Door", 2, TreasureRoutePointSlot.Right),
                    Point(81.37923f, -10.03865f, -48.60507f, "Room 3 Left Door", 3, TreasureRoutePointSlot.Left),
                    Point(138.87137f, -10.03865f, -48.890167f, "Room 3 Right Door", 3, TreasureRoutePointSlot.Right),
                    Point(-138.5103f, 19.96135f, -168.72546f, "Room 4 Left Door", 4, TreasureRoutePointSlot.Left),
                    Point(-81.4899f, 19.96135f, -168.72546f, "Room 4 Right Door", 4, TreasureRoutePointSlot.Right),
                    Point(-138.91505f, 19.961369f, -319.07587f, "Room 5 Left Door", 5, TreasureRoutePointSlot.Left),
                    Point(-81.35945f, 19.961378f, -318.83533f, "Room 5 Right Door", 5, TreasureRoutePointSlot.Right),
                ])
        },
        {
            1209,
            new TreasureRoute(
                true,
                1209,
                "Cenote Ja Ja Gural",
                TreasureRouteKind.Treasure,
                Point(0.1223174f, -400.0f, 377.30017f, "Room 1 Start", 1, TreasureRoutePointSlot.Start),
                [
                    Point(-35.06371f, -400.00003f, 341.5195f, "Room 2 Left Door", 2, TreasureRoutePointSlot.Left),
                    Point(35.617687f, -400.0f, 341.68155f, "Room 2 Right Door", 2, TreasureRoutePointSlot.Right),
                    Point(-35.291565f, -400.0f, 156.95146f, "Room 3 Left Door", 3, TreasureRoutePointSlot.Left),
                    Point(35.601677f, -400.0f, 156.61826f, "Room 3 Right Door", 3, TreasureRoutePointSlot.Right),
                    Point(124.29012f, -290.00003f, -16.546095f, "Room 4 Left Door", 4, TreasureRoutePointSlot.Left),
                    Point(195.96709f, -290.00015f, -16.546623f, "Room 4 Right Door", 4, TreasureRoutePointSlot.Right),
                    Point(-232.17561f, -169.0f, -180.32594f, "Room 5 Left Door", 5, TreasureRoutePointSlot.Left),
                    Point(-162.12967f, -169.00015f, -179.9811f, "Room 5 Right Door", 5, TreasureRoutePointSlot.Right),
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

    private static IPluginLog? log;
    private static string configPath = FileName;
    private static Dictionary<uint, TreasureRoute> routes = CloneRoutes(BuiltInRoutes);
    private static HashSet<uint> supportedDutyTerritories = BuildSupportedDutyTerritories(routes);
    private static DateTime lastObservedWriteUtc;
    private static DateTime nextReloadPollUtc;
    private static IReadOnlyList<string> fallbackWarnings = [];

    public static string ConfigPath
    {
        get
        {
            lock (Gate)
                return configPath;
        }
    }

    public static string LastLoadStatus { get; private set; } = "Treasure route data is using built-in fallback; plugin config path not initialized.";

    public static IReadOnlyList<string> FallbackWarnings
    {
        get
        {
            lock (Gate)
                return fallbackWarnings.ToList();
        }
    }

    public static int ActiveRouteCount
    {
        get
        {
            lock (Gate)
                return routes.Count;
        }
    }

    public static void Configure(string configDirectory, IPluginLog pluginLog)
    {
        lock (Gate)
        {
            log = pluginLog;
            Directory.CreateDirectory(configDirectory);
            configPath = Path.Combine(configDirectory, FileName);
        }

        Reload();
    }

    public static bool Reload()
    {
        lock (Gate)
        {
            if (string.IsNullOrWhiteSpace(configPath))
                return UseBuiltInFallbackLocked("Treasure route data cannot load because the config path is blank.");

            if (!File.Exists(configPath))
                return UseBuiltInFallbackLocked($"Treasure route JSON missing at {configPath}; using built-in routes until remote cache refresh or editor save.");

            try
            {
                var json = File.ReadAllText(configPath);
                if (!TryDeserializeManifest(json, configPath, out var manifest, out var status))
                    return UseBuiltInFallbackLocked(status);

                var nextRoutes = CloneRoutes(BuiltInRoutes);
                var warnings = new List<string>();
                var manifestTerritories = new HashSet<uint>();
                foreach (var route in manifest.Routes.OrderBy(x => x.TerritoryTypeId))
                {
                    manifestTerritories.Add(route.TerritoryTypeId);
                    if (!route.Enabled)
                    {
                        nextRoutes.Remove(route.TerritoryTypeId);
                        warnings.Add($"Territory {route.TerritoryTypeId} route disabled by {FileName}.");
                        continue;
                    }

                    nextRoutes[route.TerritoryTypeId] = ConvertRoute(route, configPath);
                }

                foreach (var territoryId in GetEditableBuiltInRouteTerritoryIds().Where(x => !manifestTerritories.Contains(x)))
                    warnings.Add($"Territory {territoryId} omitted by {FileName}; using built-in route fallback.");

                routes = nextRoutes;
                supportedDutyTerritories = BuildSupportedDutyTerritories(routes);
                fallbackWarnings = warnings;
                lastObservedWriteUtc = File.GetLastWriteTimeUtc(configPath);
                LastLoadStatus = warnings.Count == 0
                    ? $"Loaded {routes.Count} active treasure route(s) from {configPath}."
                    : $"Loaded {routes.Count} active treasure route(s) from {configPath}; {warnings.Count} fallback warning(s).";
                log?.Information($"[ADS] {LastLoadStatus}");
                foreach (var warning in warnings)
                    log?.Warning($"[ADS] {warning}");
                return true;
            }
            catch (Exception ex)
            {
                return UseBuiltInFallbackLocked($"Failed to load treasure route JSON from {configPath}: {ex.Message}");
            }
        }
    }

    public static bool ReloadIfChanged()
    {
        var now = DateTime.UtcNow;
        lock (Gate)
        {
            if (now < nextReloadPollUtc)
                return false;

            nextReloadPollUtc = now + ReloadPollInterval;
            if (!File.Exists(configPath))
            {
                if (lastObservedWriteUtc != default)
                    return Reload();

                return false;
            }

            var currentWriteUtc = File.GetLastWriteTimeUtc(configPath);
            if (currentWriteUtc == lastObservedWriteUtc)
                return false;
        }

        return Reload();
    }

    public static TreasureRouteManifest CreateEditableCopy()
    {
        lock (Gate)
            return CreateManifest(routes);
    }

    public static TreasureRouteManifest CreateBuiltInEditableCopy()
        => CreateManifest(BuiltInRoutes);

    public static bool TryCreateBuiltInRouteCopy(uint territoryId, out TreasureRouteDefinition route)
    {
        route = new TreasureRouteDefinition();
        if (!BuiltInRoutes.TryGetValue(territoryId, out var builtInRoute))
            return false;

        if (!IsEditableRouteShape(builtInRoute))
            return false;

        route = ConvertRoute(builtInRoute);
        return true;
    }

    public static IReadOnlyList<uint> GetRouteTerritoryIds()
    {
        lock (Gate)
            return routes.Keys.OrderBy(x => x).ToList();
    }

    public static IReadOnlyList<uint> GetBuiltInRouteTerritoryIds()
        => GetEditableBuiltInRouteTerritoryIds().ToList();

    public static bool SaveManifest(TreasureRouteManifest manifest)
    {
        try
        {
            ValidateManifest(manifest, configPath);
            var directory = Path.GetDirectoryName(configPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var json = JsonSerializer.Serialize(manifest, JsonOptions);
            File.WriteAllText(configPath, json);
            return Reload();
        }
        catch (Exception ex)
        {
            LastLoadStatus = $"Failed to save {FileName}: {ex.Message}";
            log?.Warning(ex, $"[ADS] {LastLoadStatus}");
            return false;
        }
    }

    public static void ValidateJson(string json, string source)
    {
        if (!TryDeserializeManifest(json, source, out _, out var status))
            throw new InvalidDataException(status);
    }

    public static bool TryGetInteractableClassification(uint territoryId, string objectName, out InteractableClass classification)
    {
        if (DoorNames.TryGetValue(territoryId, out var doorName)
            && string.Equals(objectName, doorName, StringComparison.OrdinalIgnoreCase))
        {
            classification = InteractableClass.TreasureDoor;
            return true;
        }

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
    {
        lock (Gate)
            return routes.ContainsKey(territoryId);
    }

    public static bool IsSupportedDutyTerritory(uint territoryId)
    {
        lock (Gate)
            return supportedDutyTerritories.Contains(territoryId);
    }

    public static IReadOnlyCollection<uint> GetSupportedDutyTerritories()
    {
        lock (Gate)
            return supportedDutyTerritories.ToList();
    }

    public static IReadOnlyList<DungeonFrontierPoint> BuildRoutePoints(uint territoryId, uint mapId)
    {
        TreasureRoute route;
        lock (Gate)
        {
            if (!routes.TryGetValue(territoryId, out route))
                return [];
        }

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
        TreasureRoute route;
        lock (Gate)
        {
            if (!routes.TryGetValue(territoryId, out route))
                return null;
        }

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

    private static bool UseBuiltInFallbackLocked(string status)
    {
        routes = CloneRoutes(BuiltInRoutes);
        supportedDutyTerritories = BuildSupportedDutyTerritories(routes);
        fallbackWarnings = [status];
        lastObservedWriteUtc = File.Exists(configPath) ? File.GetLastWriteTimeUtc(configPath) : default;
        LastLoadStatus = $"{status} Active treasure routes: {routes.Count} built-in fallback route(s).";
        log?.Warning($"[ADS] {LastLoadStatus}");
        return false;
    }

    private static TreasureRoute ConvertRoute(TreasureRouteDefinition route, string source)
    {
        ValidateRoute(route, source);
        var builtInRoute = BuiltInRoutes[route.TerritoryTypeId];
        var roomByNumber = route.Rooms.ToDictionary(x => x.Room);
        var doors = builtInRoute.Doors
            .Select(door =>
            {
                var room = roomByNumber[door.Room];
                var coordinate = GetRoomCoordinate(room, door.Slot);
                return door with { Position = ConvertCoordinate(coordinate) };
            })
            .ToArray();

        return builtInRoute with
        {
            Enabled = route.Enabled,
            Start = builtInRoute.Start with { Position = ConvertCoordinate(route.EntryPoint!) },
            Doors = doors,
        };
    }

    private static TreasureRouteDefinition ConvertRoute(TreasureRoute route)
        => new()
        {
            Enabled = route.Enabled,
            TerritoryTypeId = route.TerritoryTypeId,
            DutyName = route.DutyName,
            RouteKind = route.RouteKind,
            EntryPoint = ConvertCoordinate(route.Start.Position),
            Rooms = BuildRouteRooms(route)
                .Select(room => new TreasureRouteRoomDefinition
                {
                    Room = room.Room,
                    Left = ConvertCoordinate(room.Left!.Value.Position),
                    Middle = route.RouteKind == TreasureRouteKind.Thief
                        ? ConvertCoordinate(room.Middle!.Value.Position)
                        : null,
                    Right = ConvertCoordinate(room.Right!.Value.Position),
                })
                .ToList(),
        };

    private static Vector3 ConvertCoordinate(TreasureRouteCoordinate coordinate)
        => new(coordinate.X!.Value, coordinate.Y!.Value, coordinate.Z!.Value);

    private static TreasureRouteCoordinate ConvertCoordinate(Vector3 position)
        => new()
        {
            X = position.X,
            Y = position.Y,
            Z = position.Z,
        };

    private static TreasureRouteManifest CreateManifest(IReadOnlyDictionary<uint, TreasureRoute> sourceRoutes)
        => new()
        {
            SchemaVersion = 1,
            Description = "ADS treasure dungeon route geometry. Edit only entryPoint and room door XYZ values; object names, labels, room metadata, and route behavior stay compiled in ADS.",
            Routes = sourceRoutes
                .OrderBy(x => x.Key)
                .Where(x => IsEditableRouteShape(x.Value))
                .Select(x => ConvertRoute(x.Value))
                .ToList(),
        };

    private static bool TryDeserializeManifest(string json, string source, out TreasureRouteManifest manifest, out string status)
    {
        manifest = new TreasureRouteManifest();
        status = $"Failed to parse treasure route manifest from {source}.";

        try
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                status = $"{source} was empty.";
                return false;
            }

            manifest = JsonSerializer.Deserialize<TreasureRouteManifest>(json, JsonOptions)
                ?? new TreasureRouteManifest();
            ValidateManifest(manifest, source);
            status = $"Validated {manifest.Routes.Count(x => x.Enabled)} enabled treasure route(s) from {source}.";
            return true;
        }
        catch (Exception ex)
        {
            status = $"Failed to parse treasure route manifest from {source}: {ex.Message}";
            return false;
        }
    }

    private static void ValidateManifest(TreasureRouteManifest manifest, string source)
    {
        if (manifest.SchemaVersion != 1)
            throw new InvalidDataException($"{source} has unsupported schemaVersion {manifest.SchemaVersion}; expected 1.");

        manifest.Routes ??= [];
        var seenTerritories = new HashSet<uint>();
        foreach (var route in manifest.Routes)
        {
            if (route.TerritoryTypeId == 0)
                throw new InvalidDataException($"{source} has a treasure route with territoryTypeId 0.");

            if (!seenTerritories.Add(route.TerritoryTypeId))
                throw new InvalidDataException($"{source} has duplicate treasure route territoryTypeId {route.TerritoryTypeId}.");

            ValidateRoute(route, source);
        }
    }

    private static void ValidateRoute(TreasureRouteDefinition route, string source)
    {
        if (!BuiltInRoutes.TryGetValue(route.TerritoryTypeId, out var builtInRoute))
            throw new InvalidDataException($"{source} route {route.TerritoryTypeId} is not a compiled ADS treasure route.");

        if (!IsEditableRouteShape(builtInRoute))
            throw new InvalidDataException($"{source} route {route.TerritoryTypeId} is compiled-only and cannot be edited through JSON.");

        if (route.RouteKind == TreasureRouteKind.Unknown)
            throw new InvalidDataException($"{source} route {route.TerritoryTypeId} is missing routeKind.");

        if (route.RouteKind != builtInRoute.RouteKind)
            throw new InvalidDataException($"{source} route {route.TerritoryTypeId} routeKind {route.RouteKind} does not match compiled routeKind {builtInRoute.RouteKind}.");

        if (route.EntryPoint is null)
            throw new InvalidDataException($"{source} route {route.TerritoryTypeId} is missing entryPoint.");

        route.Rooms ??= [];
        ValidateCoordinate(route.EntryPoint, source, route.TerritoryTypeId, "entryPoint");

        var expectedRooms = BuildRouteRooms(builtInRoute);
        if (route.Rooms.Count != expectedRooms.Count)
            throw new InvalidDataException($"{source} route {route.TerritoryTypeId} has {route.Rooms.Count} room row(s); expected {expectedRooms.Count}.");

        var seenRooms = new HashSet<int>();
        var roomByNumber = new Dictionary<int, TreasureRouteRoomDefinition>();
        foreach (var room in route.Rooms)
        {
            if (room.Room <= 0)
                throw new InvalidDataException($"{source} route {route.TerritoryTypeId} has invalid room {room.Room}.");

            if (!seenRooms.Add(room.Room))
                throw new InvalidDataException($"{source} route {route.TerritoryTypeId} has duplicate room {room.Room}.");

            roomByNumber[room.Room] = room;
        }

        foreach (var expectedRoom in expectedRooms)
        {
            if (!roomByNumber.TryGetValue(expectedRoom.Room, out var room))
                throw new InvalidDataException($"{source} route {route.TerritoryTypeId} is missing room {expectedRoom.Room}.");

            ValidateCoordinate(room.Left, source, route.TerritoryTypeId, $"rooms[{expectedRoom.Room}].left");
            if (route.RouteKind == TreasureRouteKind.Thief)
            {
                ValidateCoordinate(room.Middle, source, route.TerritoryTypeId, $"rooms[{expectedRoom.Room}].middle");
            }
            else if (room.Middle is not null)
            {
                throw new InvalidDataException($"{source} route {route.TerritoryTypeId} room {expectedRoom.Room} includes middle coordinates but routeKind is {route.RouteKind}.");
            }

            ValidateCoordinate(room.Right, source, route.TerritoryTypeId, $"rooms[{expectedRoom.Room}].right");
        }
    }

    private static void ValidateCoordinate(
        TreasureRouteCoordinate? point,
        string source,
        uint territoryTypeId,
        string pointPath)
    {
        if (point is null)
            throw new InvalidDataException($"{source} route {territoryTypeId} {pointPath} is missing coordinates.");

        if (!point.X.HasValue || !point.Y.HasValue || !point.Z.HasValue
            || !float.IsFinite(point.X.Value)
            || !float.IsFinite(point.Y.Value)
            || !float.IsFinite(point.Z.Value))
        {
            throw new InvalidDataException($"{source} route {territoryTypeId} {pointPath} has malformed coordinates.");
        }
    }

    private static TreasureRouteCoordinate GetRoomCoordinate(TreasureRouteRoomDefinition room, TreasureRoutePointSlot slot)
        => slot switch
        {
            TreasureRoutePointSlot.Left => room.Left ?? throw new InvalidDataException($"Room {room.Room} is missing left coordinates."),
            TreasureRoutePointSlot.Centre => room.Middle ?? throw new InvalidDataException($"Room {room.Room} is missing middle coordinates."),
            TreasureRoutePointSlot.Right => room.Right ?? throw new InvalidDataException($"Room {room.Room} is missing right coordinates."),
            _ => throw new InvalidDataException($"Room {room.Room} cannot map slot {slot} from editable treasure route JSON."),
        };

    private static IReadOnlyList<uint> GetEditableBuiltInRouteTerritoryIds()
        => BuiltInRoutes
            .Where(x => IsEditableRouteShape(x.Value))
            .Select(x => x.Key)
            .OrderBy(x => x)
            .ToList();

    private static bool IsEditableRouteShape(TreasureRoute route)
    {
        var rooms = BuildRouteRooms(route);
        return route.RouteKind switch
        {
            TreasureRouteKind.Treasure => route.Doors.Length == rooms.Count * 2
                && rooms.Count > 0
                && rooms.All(x => x.Left.HasValue && !x.Middle.HasValue && x.Right.HasValue),
            TreasureRouteKind.Thief => route.Doors.Length == rooms.Count * 3
                && rooms.Count > 0
                && rooms.All(x => x.Left.HasValue && x.Middle.HasValue && x.Right.HasValue),
            _ => false,
        };
    }

    private static IReadOnlyList<BuiltInRouteRoom> BuildRouteRooms(TreasureRoute route)
    {
        var rooms = new List<BuiltInRouteRoom>();
        foreach (var door in route.Doors)
        {
            var index = rooms.FindIndex(x => x.Room == door.Room);
            if (index < 0)
            {
                rooms.Add(new BuiltInRouteRoom(door.Room, null, null, null));
                index = rooms.Count - 1;
            }

            var room = rooms[index];
            rooms[index] = door.Slot switch
            {
                TreasureRoutePointSlot.Left => room with { Left = door },
                TreasureRoutePointSlot.Centre => room with { Middle = door },
                TreasureRoutePointSlot.Right => room with { Right = door },
                _ => room,
            };
        }

        return rooms;
    }

    private static RoutePoint Point(float x, float y, float z, string label, int room, TreasureRoutePointSlot slot)
        => new(new Vector3(x, y, z), label, room, slot);

    private static Dictionary<uint, TreasureRoute> CloneRoutes(IReadOnlyDictionary<uint, TreasureRoute> source)
        => source.ToDictionary(
            x => x.Key,
            x => x.Value with { Doors = x.Value.Doors.ToArray() });

    private static HashSet<uint> BuildSupportedDutyTerritories(IReadOnlyDictionary<uint, TreasureRoute> routeSource)
        => routeSource.Keys
            .Concat(DoorNames.Keys)
            .Concat(SphereNames.Keys)
            .ToHashSet();

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
            TreasureRouteIndex = priority,
            TreasureRoomIndex = InferRoomIndex(routePoint, priority),
            TreasurePassageGroup = InferPassageGroup(routePoint, priority),
        };

    private static int InferRoomIndex(RoutePoint routePoint, int priority)
    {
        if (routePoint.Room > 0)
            return routePoint.Room;

        var label = routePoint.Label;
        var roomIndex = label.IndexOf("Room ", StringComparison.OrdinalIgnoreCase);
        if (roomIndex < 0)
            return 0;

        var start = roomIndex + "Room ".Length;
        var end = start;
        while (end < label.Length && char.IsDigit(label[end]))
            end++;

        if (end == start)
            return 0;

        return int.TryParse(label[start..end], out var parsed)
            ? parsed
            : Math.Max(0, priority);
    }

    private static string InferPassageGroup(RoutePoint routePoint, int priority)
    {
        return routePoint.Slot switch
        {
            TreasureRoutePointSlot.Start => "Start",
            TreasureRoutePointSlot.Left => "Left",
            TreasureRoutePointSlot.Right => "Right",
            TreasureRoutePointSlot.Centre => "Centre",
            TreasureRoutePointSlot.Single => priority == 0 ? "Start" : $"Passage {priority}",
            _ => InferPassageGroup(routePoint.Label, priority),
        };
    }

    private static string InferPassageGroup(string label, int priority)
    {
        if (label.Contains("Left", StringComparison.OrdinalIgnoreCase))
            return "Left";

        if (label.Contains("Right", StringComparison.OrdinalIgnoreCase))
            return "Right";

        if (label.Contains("Centre", StringComparison.OrdinalIgnoreCase)
            || label.Contains("Center", StringComparison.OrdinalIgnoreCase))
        {
            return "Centre";
        }

        return priority == 0 ? "Start" : $"Passage {priority}";
    }
}
