using System.Numerics;
using System.Text.Json;
using ADS.Services;

namespace ADS.Tests;

public sealed class ExplorerSnapshotExportTests
{
    [Fact]
    public void JsonlDiskLoggingDefaultsOff()
    {
        var configuration = new Configuration();

        Assert.False(configuration.HigherLowerVfxDataminingEnabled);
    }

    [Fact]
    public void SnapshotDocumentIncludesObjectTableAndUnfilteredServerEvents()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "snapshot.json");
        var timestamp = new DateTime(2026, 7, 2, 12, 0, 0, DateTimeKind.Utc);
        var serverEvent = new HigherLowerServerEventTraceService.ServerEventRow(
            Sequence: 42,
            TimestampUtc: timestamp,
            Kind: HigherLowerServerEventTraceService.ServerEventKind.EObjState,
            BossModKind: "EObjState",
            TerritoryId: 100,
            MapId: 200,
            ActorId: 300,
            TargetId: 400,
            Category: 500,
            P1: 1,
            P2: 2,
            P3: 3,
            P4: 4,
            P5: 5,
            P6: 6,
            P7: 7,
            P8: 8,
            Replaying: 0,
            ObjectName: "Test Object",
            ObjectKind: "EventObj",
            GameObjectId: 600,
            EntityId: 700,
            BaseId: 800,
            LayoutId: 900,
            GimmickId: 1000,
            EventState: 2,
            EventId: 1100,
            Targetable: true,
            Position: new Vector3(1.25f, 2.5f, 3.75f),
            Distance: 4.5f,
            StateData: "state",
            SourceParams: "params",
            DataHex: "AABB",
            HigherLowerRelevant: false);
        var objectRow = new ExplorerObjectSnapshot(
            TableIndex: 12,
            Name: "Loaded Object",
            GameObjectId: 1,
            GameObjectIdHex: "0x1",
            EntityId: 2,
            EntityIdHex: "0x00000002",
            DataId: 3,
            BaseId: 3,
            OwnerId: 4,
            OwnerIdHex: "0x00000004",
            ObjectKind: "EventObj",
            SubKind: 5,
            YalmDistanceX: 6,
            YalmDistanceZ: 7,
            IsDead: false,
            IsTargetable: true,
            IsLocalPlayer: false,
            Position: new ExplorerPositionSnapshot(8, 9, 10),
            Rotation: 11,
            HitboxRadius: 12,
            TargetObjectId: 13,
            TargetObjectIdHex: "0xD",
            AddressHex: "0xE");
        var document = new ExplorerSnapshotDocument(
            FormatVersion: 1,
            ExportedAtUtc: timestamp,
            PluginVersion: "1.2.3.4",
            Context: new ExplorerContextSnapshot(100, 200, 300, true, true, false),
            ObjectTableLength: 596,
            LoadedObjectCount: 1,
            ServerEventCount: 1,
            Objects: [objectRow],
            ServerEvents: [serverEvent]);

        ExplorerSnapshotExportService.WriteDocument(path, document);

        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var root = json.RootElement;
        Assert.Equal(596, root.GetProperty("ObjectTableLength").GetInt32());
        Assert.Equal("Loaded Object", root.GetProperty("Objects")[0].GetProperty("Name").GetString());
        Assert.Equal("EObjState", root.GetProperty("ServerEvents")[0].GetProperty("Kind").GetString());
        Assert.Equal(1.25f, root.GetProperty("ServerEvents")[0].GetProperty("Position").GetProperty("X").GetSingle());
    }
}
