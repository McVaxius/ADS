using System.Numerics;
using System.Collections.Frozen;
using System.Reflection;
using System.Runtime.CompilerServices;
using Dalamud.Game.ClientState.Aetherytes;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using ADS.Services;
using Xunit;

namespace ADS.Tests;

public sealed class StartingCityInnRepairRegressionTests
{
    [Fact]
    public void InnRouteUsesUnlockedRootFromTheSameAethernetGroup()
    {
        // Limsa's main crystal and inn shard occupy different territories.
        // Another main crystal in the inn territory must not be selected instead.
        var aetherytes = RepairAetherytes(
            (8, true, 129, 1), (41, false, 128, 1), (2, true, 132, 2), (94, false, 132, 2),
            (9, true, 130, 3), (33, false, 130, 3), (999, true, 128, 4));
        Assert.Equal(7, aetherytes.Count());
        Assert.True(aetherytes.GetRow(8).IsAetheryte);
        Assert.Equal(129u, aetherytes.GetRow(8).Territory.RowId);
        Assert.Equal(aetherytes.GetRow(8).AethernetGroup, aetherytes.GetRow(41).AethernetGroup);
        var unlocked = new HashSet<uint> { 2, 8, 9, 999 };
        var ids = new uint[] { 2, 8, 9, 999 };
        var list = Fake<IAetheryteList>((method, args) => method.Name switch
        {
            "get_Length" => ids.Length,
            "get_Item" => Fake<IAetheryteEntry>((entryMethod, _) => entryMethod.Name switch
            {
                "get_AetheryteId" => unlocked.Contains(ids[(int)args![0]!]) ? ids[(int)args[0]!] : 0u,
                "get_GilCost" => ids[(int)args![0]!] * 10u,
                _ => Default(entryMethod.ReturnType),
            }),
            _ => Default(method.ReturnType),
        });
        var property = typeof(Plugin).GetProperty("AetheryteList", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previous = property.GetValue(null);
        property.SetValue(null, list);
        try
        {
            var data = new TestData(InnTerritories([177, 178, 179]), aetherytes);
            var service = new UtilityAutomationService(data, null!, null!, null!, null!, null!,
                new Configuration(), null!, null!, null!, () => false, () => false, Fake<IPluginLog>((call, args) =>
                {
                    if (call.Name == "Warning" && args?[0] is Exception exception)
                        throw new InvalidOperationException("Unexpected route lookup failure", exception);
                    return Default(call.ReturnType);
                }));
            var method = typeof(UtilityAutomationService).GetMethod("TryGetUnlockedInnRouteGilCost", BindingFlags.Instance | BindingFlags.NonPublic)!;
            foreach (var (shard, root) in new[] { (41u, 8u), (94u, 2u), (33u, 9u) })
            {
                object[] args = [aetherytes.GetRow(shard), 0];
                Assert.True((bool)method.Invoke(service, args)!);
                Assert.Equal((int)root * 10, args[1]);
                unlocked.Remove(root);
                Assert.False((bool)method.Invoke(service, args)!);
                unlocked.Add(root);
            }
        }
        finally
        {
            property.SetValue(null, previous);
        }
    }

    [Fact]
    public void InnTravelSettlesAfterFinalHopAndStalledMovementRetries()
    {
        var tick = new DateTime(2026, 9, 24, 14, 0, 0, DateTimeKind.Utc);
        var progressPosition = Vector3.Zero;
        var progressTime = tick;
        Assert.False(InnEntryService.IsMovementStalled(Vector3.Zero, tick.AddMilliseconds(2999), ref progressPosition, ref progressTime));
        Assert.True(InnEntryService.IsMovementStalled(Vector3.Zero, tick.AddSeconds(3), ref progressPosition, ref progressTime));
        Assert.False(InnEntryService.IsMovementStalled(Vector3.UnitX, tick.AddSeconds(3), ref progressPosition, ref progressTime));
        Assert.False(InnEntryService.IsMovementStalled(Vector3.UnitX, tick.AddMilliseconds(5999), ref progressPosition, ref progressTime));
        Assert.True(InnEntryService.IsMovementStalled(Vector3.UnitX, tick.AddSeconds(6), ref progressPosition, ref progressTime));

        var events = new List<string>();
        var ready = true;
        var pathfinding = false;
        var following = false;
        var busy = true;
        var zoning = false;
        uint territory = 178;
        var position = new Vector3(-137.8995f, -3.154889f, -168.48999f);
        var player = Fake<IPlayerCharacter>((method, _) => method.Name == "get_Position" ? position : Default(method.ReturnType));
        var objects = Fake<IObjectTable>((method, _) => method.Name == "get_LocalPlayer" ? player : Default(method.ReturnType));
        var client = Fake<IClientState>((method, _) => method.Name switch
        {
            "get_IsLoggedIn" => true, "get_TerritoryType" => territory, _ => Default(method.ReturnType),
        });
        var condition = Fake<ICondition>((method, _) => method.Name == "get_Item" ? zoning : Default(method.ReturnType));
        var command = Fake<ICommandManager>((method, args) =>
        {
            if (method.Name != "ProcessCommand") return Default(method.ReturnType);
            var text = (string)args![0]!;
            events.Add(text);
            if (text == "/vnav stop") following = false;
            return true;
        });
        var pluginInterface = Fake<IDalamudPluginInterface>((method, args) =>
        {
            if (method.Name != "GetIpcSubscriber") return Default(method.ReturnType);
            var name = (string)args![0]!;
            if (method.GetGenericArguments().Single() == typeof(bool))
                return Fake<ICallGateSubscriber<bool>>((call, _) => call.Name != "InvokeFunc" ? Default(call.ReturnType) : name switch
                {
                    "Lifestream.IsBusy" => busy,
                    "vnavmesh.Nav.IsReady" => ready,
                    "vnavmesh.SimpleMove.PathfindInProgress" => pathfinding,
                    "vnavmesh.Path.IsRunning" => following,
                    _ => throw new InvalidOperationException(name),
                });
            return Fake<ICallGateSubscriber<object>>((call, _) =>
            {
                if (call.Name == "InvokeAction")
                {
                    events.Add(name);
                    if (name == "vnavmesh.Nav.PathfindCancelAll") pathfinding = false;
                }
                return Default(call.ReturnType);
            });
        });
        var property = typeof(Plugin).GetProperty("PluginInterface", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previous = property.GetValue(null);
        property.SetValue(null, pluginInterface);
        try
        {
            var data = new TestData(InnTerritories([178, 179, 177]));
            var log = Fake<IPluginLog>();
            var inn = new InnEntryService(data, objects, null!, command, client, condition, log);
            var service = new UtilityAutomationService(data, objects, null!, command, client, condition,
                new Configuration(), null!, null!, null!, () => false, () => false, log, inn);
            Assert.True(service.StartNpcRepairYesInn("uldah"));
            territory = 130;
            var routeType = typeof(UtilityAutomationService).GetNestedType("ResolvedInnRepairRoute", BindingFlags.NonPublic)!;
            var route = Activator.CreateInstance(routeType, 130u, "Ul'dah", 33u, "Adventurers' Guild",
                new[] { new Vector3(53.7f, 4, -126) }, 0)!;
            var stageType = Get(service, "npcRepairTravelStage")!.GetType();
            Set(service, "npcRepairTravelStage", Enum.Parse(stageType, "TeleportingToInnAethernet"));
            Set(service, "npcRepairOwnsInnTravel", true);
            Set(service, "npcRepairTravelCommandUtc", tick.AddSeconds(-20));

            Invoke(service, "UpdateNpcRepairInnTeleport", tick, route);
            Assert.Equal(DateTime.MinValue, Get(service, "npcRepairInnArrivalReadyUtc"));
            busy = false;
            Invoke(service, "UpdateNpcRepairInnTeleport", tick, route);
            Invoke(service, "UpdateNpcRepairInnTeleport", tick.AddMilliseconds(400), route);
            Assert.Equal("TeleportingToInnAethernet", Get(service, "npcRepairTravelStage")!.ToString());

            // Replay the reported gap: IsBusy clears, then the final aethernet hop starts.
            zoning = true;
            service.Update();
            Assert.Equal(DateTime.MinValue, Get(service, "npcRepairInnArrivalReadyUtc"));
            zoning = false;
            Invoke(service, "UpdateNpcRepairInnTeleport", tick.AddSeconds(2), route);
            Invoke(service, "UpdateNpcRepairInnTeleport", tick.AddMilliseconds(3399), route);
            Assert.Equal("TeleportingToInnAethernet", Get(service, "npcRepairTravelStage")!.ToString());
            Invoke(service, "UpdateNpcRepairInnTeleport", tick.AddMilliseconds(3400), route);
            Assert.Equal("WalkingInnPath", Get(service, "npcRepairTravelStage")!.ToString());
            Assert.Empty(events);

            ready = false;
            Invoke(service, "UpdateNpcRepairInnPath", DateTime.UtcNow, route);
            Assert.Empty(events);
            ready = true;
            Invoke(service, "UpdateNpcRepairInnPath", DateTime.UtcNow, route);
            Assert.Equal("/vnav moveto 53.70 4.00 -126.00", Assert.Single(events));
            events.Clear();
            following = true;
            Set(service, "lastMovementProgressUtc", DateTime.UtcNow.AddSeconds(-2.5));
            Set(service, "lastMoveCommandUtc", DateTime.UtcNow.AddSeconds(-2.1));
            Invoke(service, "UpdateNpcRepairInnPath", DateTime.UtcNow, route);
            Assert.Empty(events);
            pathfinding = true;
            Set(service, "lastMovementProgressUtc", DateTime.UtcNow.AddSeconds(-3.1));
            Invoke(service, "UpdateNpcRepairInnPath", DateTime.UtcNow, route);
            Assert.Equal(new[] { "vnavmesh.Nav.PathfindCancelAll", "/vnav stop" }, events);
            Invoke(service, "UpdateNpcRepairInnPath", DateTime.UtcNow, route);
            Assert.Equal("/vnav moveto 53.70 4.00 -126.00", events.Last());
            events.Clear();

            following = true;
            position += Vector3.UnitX;
            Set(service, "lastMovementProgressUtc", DateTime.UtcNow.AddSeconds(-4));
            Invoke(service, "UpdateNpcRepairInnPath", DateTime.UtcNow, route);
            Assert.Empty(events); // Moving paths are not restarted.
            pathfinding = true;
            zoning = true;
            service.Update();
            Assert.Equal(new[] { "vnavmesh.Nav.PathfindCancelAll", "/vnav stop" }, events);
            events.Clear();
            service.Update();
            Assert.Empty(events); // Loading cancellation is issued once.
            zoning = false;
            service.Cancel("test Stop");
            events.Clear();
            service.Update();
            Assert.Empty(events);
            Assert.False(service.IsRunning);

            // The innkeeper approach shares the same recovery, retaining the Ul'dah counter fix.
            var npc = Fake<IGameObject>((method, _) => method.Name switch
            {
                "get_BaseId" => 1001976u, "get_Position" => new Vector3(28.86f, 7, -80.13f), _ => Default(method.ReturnType),
            });
            Invoke(inn, "SendMoveCommand", npc, true);
            Assert.Equal("/vnav moveto 31.50 7.00 -82.00", Assert.Single(events));
            events.Clear();
            following = true;
            Set(inn, "lastMovementProgressUtc", DateTime.UtcNow.AddSeconds(-3.1));
            Invoke(inn, "SendMoveCommand", npc, false);
            Assert.Equal("/vnav stop", Assert.Single(events));
            Invoke(inn, "SendMoveCommand", npc, false);
            Assert.Equal("/vnav moveto 31.50 7.00 -82.00", events.Last());
        }
        finally
        {
            property.SetValue(null, previous);
        }
    }

    [Fact]
    public void ManualInnRepairPreservesDestinationAndCancellationBoundaries()
    {
        // Otopa is more than three yalms away from the accessible side of the counter.
        var npc = new Vector3(28.86f, 7, -80.13f);
        var approach = new Vector3(31.5f, 7, -82);
        Assert.True(Vector3.Distance(npc, approach) > 3);
        Assert.Equal(approach, InnEntryService.InteractionPoint(130, 1001976, npc));
        Assert.True(InnEntryService.HasReachedInteractionPoint(130, 1001976, npc, approach));
        Assert.True(InnEntryService.HasReachedInteractionPoint(130, 1001976, npc, approach + Vector3.UnitX));
        Assert.False(InnEntryService.HasReachedInteractionPoint(130, 1001976, npc, approach + Vector3.UnitX * 1.01f));
        foreach (var (cityTerritory, id) in new[] { (128u, 1000974u), (132u, 1000102u), (131u, 1001976u) })
        {
            Assert.Equal(npc, InnEntryService.InteractionPoint(cityTerritory, id, npc));
            Assert.True(InnEntryService.HasReachedInteractionPoint(cityTerritory, id, npc, npc + Vector3.UnitX * 3));
            Assert.False(InnEntryService.HasReachedInteractionPoint(cityTerritory, id, npc, npc + Vector3.UnitX * 3.01f));
        }
        Assert.True(InnEntryService.ShouldSubmitMovement(() => false, () => false));
        Assert.False(InnEntryService.ShouldSubmitMovement(() => true, () => false));
        Assert.False(InnEntryService.ShouldSubmitMovement(() => false, () => true));
        Assert.False(InnEntryService.ShouldSubmitMovement(() => true, () => throw new InvalidOperationException("must short-circuit")));

        var cities = new[] { ("uldah", 33u, 178u), ("gridania", 94u, 179u), ("limsa", 41u, 177u),
            ("ishgard", 80u, 429u), ("crystarium", 152u, 843u), ("sharlayan", 185u, 990u), ("tuliyollal", 220u, 1205u) };
        foreach (var (name, route, room) in cities)
        {
            Assert.True(UtilityAutomationService.TryGetInnDestination(name.ToUpperInvariant(), out var actualRoute, out var actualRoom));
            Assert.Equal(route, actualRoute);
            Assert.Equal(room, actualRoom);
            Assert.Equal("npc-yes-inn-" + name, Plugin.NormalizeRepairMode(" NPC-YES-INN-" + name.ToUpperInvariant() + " "));
        }
        foreach (var invalid in new[] { "", "kugane", "unknown", "uldah extra" })
        {
            Assert.False(UtilityAutomationService.TryGetInnDestination(invalid, out _, out _));
            Assert.Empty(Plugin.NormalizeRepairMode("npc-yes-inn-" + invalid));
        }
        Assert.Equal("npc-yes-inn", Plugin.NormalizeRepairMode("yesinn"));
        Assert.Equal("npc", Plugin.NormalizeRepairMode("npcrepair"));
        Assert.Equal("self", Plugin.NormalizeRepairMode("selfrepair"));
        Assert.Equal("npc-no-inn", Plugin.NormalizeRepairMode("noinn"));
        Assert.Equal("npc-no-teleport-no-inn", Plugin.NormalizeRepairMode("npc-no-tp-no-inn"));

        var log = Fake<IPluginLog>();
        uint territory = 178;
        var rejectMovement = false;
        var commands = new List<string>();
        var command = Fake<ICommandManager>((method, args) =>
        {
            if (method.Name != "ProcessCommand") return Default(method.ReturnType);
            commands.Add((string)args![0]!);
            return !rejectMovement || !((string)args[0]!).StartsWith("/vnav moveto");
        });
        var player = Fake<IPlayerCharacter>();
        var objects = Fake<IObjectTable>((method, _) => method.Name switch
        {
            "get_LocalPlayer" => player,
            "GetEnumerator" => ((IEnumerable<Dalamud.Game.ClientState.Objects.Types.IGameObject>)Array.Empty<Dalamud.Game.ClientState.Objects.Types.IGameObject>()).GetEnumerator(),
            _ => Default(method.ReturnType),
        });
        var client = Fake<IClientState>((method, _) => method.Name switch
        {
            "get_IsLoggedIn" => true, "get_TerritoryType" => territory, _ => Default(method.ReturnType),
        });
        var territories = InnTerritories(cities.Select(city => city.Item3).Append(629u).ToArray());
        var data = new TestData(territories);
        var condition = Fake<ICondition>();
        var innEntry = new InnEntryService(data, objects, null!, command, client, condition, log);
        UtilityAutomationService Create() => new(data, objects, null!, command, client, condition,
            new Configuration(), null!, null!, null!, () => false, () => false, log, innEntry);

        // Actual start state, terminal entry checks, and cleanup; no game process/native callbacks.
        foreach (var (name, route, room) in cities.Take(3))
        {
            var startingRoom = room;
            territory = startingRoom;
            var service = Create();
            Assert.True(service.StartNpcRepairYesInn(name));
            Assert.True(service.IsRunning);
            Assert.Equal("ExitingInn", Get(service, "npcRepairTravelStage")!.ToString());
            Assert.Equal(startingRoom, Get(service, "npcRepairStartingInnTerritory"));
            Assert.Equal(room, Get(service, "npcRepairRequiredInnTerritory"));
            Assert.Equal(route, Get(service, "npcRepairDestinationAethernet"));

            // Invalid input cannot restart or redirect an accepted attempt.
            Assert.False(service.StartNpcRepairYesInn("kugane"));
            Assert.Equal(room, Get(service, "npcRepairRequiredInnTerritory"));
            Assert.True(service.IsRunning);
            // Even fully repaired gear cannot complete before reaching the mender.
            Assert.False((bool)Invoke(service, "TryCompleteRepairIfFinished", "fully repaired")!);
            Assert.True(service.IsRunning);

            // Simulate the repair result after the visit. Completion must remain pending for entry.
            Set(service, "npcRepairMenderReached", true);
            Invoke(service, "Complete", "Equipped gear is fully repaired.");
            Assert.True(service.IsRunning);
            Assert.Empty(service.LastSuccessMessage);
            Assert.Equal("ReturningToInn", Get(service, "npcRepairTravelStage")!.ToString());
            territory = room;
            Invoke(service, "UpdateNpcRepairInnReturn");
            Assert.False(service.IsRunning);
            Assert.Contains($"room {room}", service.LastSuccessMessage);
            Assert.Empty(service.LastFailureMessage);
            Assert.Equal(0u, Get(service, "npcRepairRequiredInnTerritory"));

            // Another inn is never accepted as the destination.
            territory = startingRoom;
            service = Create();
            Assert.True(service.StartNpcRepairYesInn(name));
            Invoke(service, "Complete", "repaired");
            territory = room == 178 ? 179u : 178u;
            Invoke(service, "UpdateNpcRepairInnReturn");
            Assert.False(service.IsRunning);
            Assert.Empty(service.LastSuccessMessage);
            Assert.Contains("expected", service.LastFailureMessage);

            foreach (var reason in new[] { "Stop", "logout", "plugin dispose" })
            {
                territory = startingRoom;
                service = Create();
                Assert.True(service.StartNpcRepairYesInn(name));
                service.Cancel(reason);
                Assert.False(service.IsRunning);
                Assert.Contains(reason, service.LastFailureMessage);
                Assert.Equal(0u, Get(service, "npcRepairStartingInnTerritory"));
                Assert.Equal(0u, Get(service, "npcRepairDestinationAethernet"));
            }

            service = Create();
            Assert.True(service.StartNpcRepairYesInn(name));
            Set(service, "startedAtUtc", DateTime.UtcNow.AddMinutes(-3));
            service.Update();
            Assert.False(service.IsRunning);
            Assert.Contains("Timed out", service.LastFailureMessage);
        }

        // Other-inn starts go directly to route resolution, never to the exit door.
        // With missing route data they fail immediately, preserving the requested destination.
        foreach (var (name, _, room) in cities.Take(3))
        foreach (var otherRoom in cities.Take(3).Select(city => city.Item3).Where(candidate => candidate != room))
        {
            territory = otherRoom;
            var crossCity = Create();
            var commandCount = commands.Count;
            Assert.False(crossCity.StartNpcRepairYesInn(name));
            Assert.False(crossCity.IsRunning);
            Assert.Contains("requested inn repair route", crossCity.LastFailureMessage);
            Assert.DoesNotContain(commands.Skip(commandCount), text => text.StartsWith("/vnav moveto"));
        }

        territory = 179;
        var automatic = Create();
        Assert.True(automatic.StartNpcRepairYesInn());
        Assert.Equal(179u, Get(automatic, "npcRepairRequiredInnTerritory"));
        Assert.Equal(0u, Get(automatic, "npcRepairDestinationAethernet"));
        automatic.Cancel("test complete");

        // Missing door, mender, and innkeeper are terminal; none skips ahead to success.
        territory = 178;
        var missingDoor = Create();
        Assert.True(missingDoor.StartNpcRepairYesInn("uldah"));
        Invoke(missingDoor, "UpdateNpcRepairInnExit");
        Assert.False(missingDoor.IsRunning);
        Assert.Contains("exit door", missingDoor.LastFailureMessage);

        territory = 179;
        var missingMender = Create();
        Assert.True(missingMender.StartNpcRepairYesInn("gridania"));
        var routeType = typeof(UtilityAutomationService).GetNestedType("ResolvedInnRepairRoute", BindingFlags.NonPublic)!;
        var resolvedRoute = Activator.CreateInstance(routeType, 132u, "Gridania", 94u, "test route", Array.Empty<Vector3>(), 0)!;
        territory = 132;
        Set(missingMender, "npcRepairTravelStageStartedUtc", DateTime.UtcNow.AddSeconds(-3));
        Invoke(missingMender, "UpdateNpcRepairInnNpcSearch", DateTime.UtcNow, resolvedRoute);
        Assert.False(missingMender.IsRunning);
        Assert.Contains("no repair NPC", missingMender.LastFailureMessage);

        territory = 179;
        var failedEntry = Create();
        Assert.True(failedEntry.StartNpcRepairYesInn("gridania"));
        Invoke(failedEntry, "Complete", "repaired");
        territory = 132;
        Set(failedEntry, "npcRepairTravelStageStartedUtc", DateTime.UtcNow.AddSeconds(-3));
        Invoke(failedEntry, "UpdateNpcRepairInnReturn");
        Assert.False(failedEntry.IsRunning);
        Assert.Contains("No innkeeper", failedEntry.LastFailureMessage);

        territory = 178;
        var rejectedMove = Create();
        Assert.True(rejectedMove.StartNpcRepairYesInn("uldah"));
        rejectMovement = true;
        Invoke(rejectedMove, "SendMoveCommand", Vector3.One, "test mender", true);
        Assert.False(rejectedMove.IsRunning);
        Assert.Contains("rejected", rejectedMove.LastFailureMessage);
        rejectMovement = false;

        // An outside start with missing route data fails instead of substituting another destination.
        territory = 130;
        foreach (var (name, _, _) in cities.Take(3))
        {
            var service = Create();
            Assert.False(service.StartNpcRepairYesInn(name));
            Assert.False(service.IsRunning);
            Assert.NotEmpty(service.LastFailureMessage);
        }
        Assert.DoesNotContain(commands, text => text.StartsWith("/li "));
        Assert.Contains("/vnav stop", commands);
    }

    // In-memory TerritoryType rows exercise the real intended-use check without installed game data.
    private static ExcelSheet<TerritoryType> InnTerritories(uint[] ids)
    {
        var raw = (RawExcelSheet)RuntimeHelpers.GetUninitializedObject(typeof(RawExcelSheet));
        var bytes = new byte[512];
        var page = (ExcelPage)Activator.CreateInstance(typeof(ExcelPage), BindingFlags.Instance | BindingFlags.NonPublic,
            null, [raw, bytes, (ushort)0], null)!;
        var probe = CreateRow<TerritoryType>(page, ids[0]);
        var found = false;
        for (var offset = 0; offset < bytes.Length; offset++)
        {
            bytes[offset] = 2;
            if (probe.TerritoryIntendedUse.RowId == 2) { found = true; break; }
            bytes[offset] = 0;
        }
        Assert.True(found, "TerritoryIntendedUse field must be represented in the test row.");
        var lookupType = typeof(RawExcelSheet).GetNestedType("RowOffsetLookup", BindingFlags.NonPublic)!;
        var lookups = Array.CreateInstance(lookupType, ids.Length);
        var indices = Enumerable.Repeat(-1, (int)ids.Max() + 1).ToArray();
        for (var i = 0; i < ids.Length; i++)
        {
            lookups.SetValue(Activator.CreateInstance(lookupType, ids[i], 0u, (ushort)0, (ushort)1), i);
            indices[ids[i]] = i;
        }
        Set(raw, "_pages", new[] { page });
        Set(raw, "_rowOffsetLookupTable", lookups);
        Set(raw, "_rowIndexLookupArray", indices);
        Set(raw, "_rowIndexLookupDict", FrozenDictionary<int, int>.Empty);
        return new ExcelSheet<TerritoryType>(raw);
    }

    private static T CreateRow<T>(ExcelPage page, uint id) where T : struct, IExcelRow<T> => T.Create(page, 0, id);

    private sealed class TestData(ExcelSheet<TerritoryType> territories, ExcelSheet<Aetheryte>? aetherytes = null) : IDataManager
    {
        public Dalamud.Game.ClientLanguage Language => Dalamud.Game.ClientLanguage.English;
        public Lumina.GameData GameData => null!;
        public ExcelModule Excel => null!;
        public bool HasModifiedGameDataFiles => false;
        public ExcelSheet<T> GetExcelSheet<T>(Dalamud.Game.ClientLanguage? language = null, string? name = null)
            where T : struct, IExcelRow<T> => typeof(T) == typeof(TerritoryType) ? (ExcelSheet<T>)(object)territories
                : typeof(T) == typeof(Aetheryte) ? (ExcelSheet<T>)(object)aetherytes! : null!;
        public SubrowExcelSheet<T> GetSubrowExcelSheet<T>(Dalamud.Game.ClientLanguage? language = null, string? name = null)
            where T : struct, IExcelSubrow<T> => null!;
        public Lumina.Data.FileResource? GetFile(string path) => null;
        public T? GetFile<T>(string path) where T : Lumina.Data.FileResource => null;
        public Task<T> GetFileAsync<T>(string path, CancellationToken cancellationToken) where T : Lumina.Data.FileResource => Task.FromResult<T>(null!);
        public bool FileExists(string path) => false;
    }

    private static ExcelSheet<Aetheryte> RepairAetherytes(params (uint Id, bool Main, byte Territory, byte Group)[] entries)
    {
        entries = entries.OrderBy(entry => entry.Id).ToArray();
        var raw = (RawExcelSheet)RuntimeHelpers.GetUninitializedObject(typeof(RawExcelSheet));
        var pages = new ExcelPage[entries.Length];
        var lookupType = typeof(RawExcelSheet).GetNestedType("RowOffsetLookup", BindingFlags.NonPublic)!;
        var lookups = Array.CreateInstance(lookupType, entries.Length);
        var indices = Enumerable.Repeat(-1, (int)entries.Max(entry => entry.Id) + 1).ToArray();
        for (var i = 0; i < entries.Length; i++)
        {
            var entry = entries[i];
            var bytes = new byte[512];
            var page = (ExcelPage)Activator.CreateInstance(typeof(ExcelPage), BindingFlags.Instance | BindingFlags.NonPublic,
                null, [raw, bytes, (ushort)0], null)!;
            var row = CreateRow<Aetheryte>(page, entry.Id);
            WriteField(() => row.Territory.RowId, entry.Territory);
            WriteField(() => row.AethernetGroup, entry.Group);
            if (entry.Main)
            {
                var found = false;
                for (var offset = 0; offset < bytes.Length && !found; offset++)
                for (var bit = 1; bit <= 128 && !found; bit <<= 1)
                {
                    var saved = bytes[offset];
                    bytes[offset] |= (byte)bit;
                    if (row.IsAetheryte) found = true;
                    else bytes[offset] = saved;
                }
                Assert.True(found);
            }
            pages[i] = page;
            lookups.SetValue(Activator.CreateInstance(lookupType, entry.Id, 0u, (ushort)i, (ushort)1), i);
            indices[entry.Id] = i;

            void WriteField(Func<uint> read, byte value)
            {
                for (var offset = 0; offset < bytes.Length; offset++)
                {
                    var saved = bytes[offset];
                    bytes[offset] = 123;
                    if (read() == 123) { bytes[offset] = value; return; }
                    bytes[offset] = saved;
                }
                Assert.Fail("Aetheryte field must be represented in the test row.");
            }
        }
        Set(raw, "_pages", pages);
        Set(raw, "<Count>k__BackingField", entries.Length);
        Set(raw, "_rowOffsetLookupTable", lookups);
        Set(raw, "_rowIndexLookupArray", indices);
        Set(raw, "_rowIndexLookupDict", FrozenDictionary<int, int>.Empty);
        return new ExcelSheet<Aetheryte>(raw);
    }

    private static object? Get(object target, string name) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);
    private static void Set(object target, string name, object value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static object? Invoke(object target, string name, params object[] args) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static object? Default(Type type) => type == typeof(void) ? null : type.IsValueType ? Activator.CreateInstance(type) : null;
    private static T Fake<T>(Func<MethodInfo, object?[]?, object?>? handler = null) where T : class
    {
        var value = DispatchProxy.Create<T, Stub>();
        ((Stub)(object)value).Handler = handler;
        return value;
    }
    public class Stub : DispatchProxy
    {
        internal Func<MethodInfo, object?[]?, object?>? Handler;
        protected override object? Invoke(MethodInfo? method, object?[]? args) =>
            Handler == null ? Default(method!.ReturnType) : Handler(method!, args);
    }
}
