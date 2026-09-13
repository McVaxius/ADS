using System.Numerics;
using System.Text.Json;
using ADS.Models;
using ADS.Services;
using static ADS.Windows.ObjectExplorerWindow;

namespace ADS.Tests;

public sealed class ExplorerSnapshotExportTests
{
    [Fact]
    public void ObjectExplorerSortingKeepsPrioritiesMissingValuesAndLiveDistancesConsistent()
    {
        var sample = new ObjectExplorerRow(
            Name: "Alpha", ObjectKind: "BattleNpc", Level: 2, ForayLevel: 2, ForayElement: 1,
            Distance: 2f, VerticalDelta: 2f, BaseId: 0, GameObjectId: 1,
            IsTargetable: true, MatchesCurrentLayer: true, Position: Vector3.Zero,
            MatchingRules: [new ObjectPriorityRule(), new ObjectPriorityRule()]);
        var other = sample with { GameObjectId = 2 };
        (SortColumn Column, ObjectExplorerRow Lower, ObjectExplorerRow Higher)[] pairs =
        [
            (SortColumn.Name, sample with { Name = "alpha" }, other with { Name = "Bravo" }),
            (SortColumn.Kind, sample with { ObjectKind = "battleNpc" }, other with { ObjectKind = "EventObj" }),
            (SortColumn.Level, sample, other with { Level = 10 }),
            (SortColumn.ForayLevel, sample, other with { ForayLevel = 10 }),
            (SortColumn.Element, sample with { ForayElement = 4 }, other), // Earth before Fire, despite IDs 4 and 1.
            (SortColumn.Element, sample with { ForayElement = 10 }, other with { ForayElement = 7 }), // Displayed numeric labels are text.
            (SortColumn.Distance, sample, other with { Distance = 10f }),
            (SortColumn.VerticalDelta, sample, other with { VerticalDelta = 10f }),
            (SortColumn.Rules, sample, other with { MatchingRules = Enumerable.Range(0, 10).Select(_ => new ObjectPriorityRule()).ToArray() }),
        ];
        foreach (var (column, lower, higher) in pairs)
        {
            AssertOrder([higher, lower], [new(column)], 1, 2);
            AssertOrder([lower, higher], [new(column, Descending: true)], 2, 1);
        }

        // Missing values remain last even if their distance/name would otherwise put them first.
        var missing = sample with { GameObjectId = 3, Name = "A", Distance = 0, Level = null, ForayLevel = null, ForayElement = null };
        foreach (var column in new[] { SortColumn.Level, SortColumn.ForayLevel, SortColumn.Element })
        {
            foreach (var descending in new[] { false, true })
            {
                SortCriterion[] criteria = [new(column, descending)];
                AssertOrder([missing, sample], criteria, 1, 3);
                Assert.True(CompareRows(sample, missing, criteria) < 0);
                Assert.True(CompareRows(missing, sample, criteria) > 0);
                AssertOrder([missing, missing with { GameObjectId = 4, Distance = 1 }], criteria, 3, 4);
            }
        }

        List<ObjectExplorerRow> mixed =
        [
            sample,
            other with { ObjectKind = "battleNPC", Level = 10, Distance = 1 },
            sample with { GameObjectId = 3, ObjectKind = "EventObj", Level = 100, Distance = 0 },
            sample with { GameObjectId = 4, Level = 10, Distance = 3 },
        ];
        AssertOrder(mixed, [new(SortColumn.Kind), new(SortColumn.Level, Descending: true)], 2, 4, 1, 3);
        AssertOrder(mixed, [new(SortColumn.Level, Descending: true), new(SortColumn.Kind)], 3, 2, 4, 1);

        // Distance, case-insensitive name, then the full object ID resolve remaining ties.
        List<ObjectExplorerRow> ties =
        [
            sample with { Name = "Beta" },
            sample with { GameObjectId = 0x1_0000_0002 },
            other with { Name = "alpha" },
            sample with { GameObjectId = 7, Name = "Zulu", Distance = 1 },
        ];
        AssertOrder(ties, [], 7, 2, 0x1_0000_0002, 1);
        ties.Reverse();
        AssertOrder(ties, [new(SortColumn.Level)], 7, 2, 0x1_0000_0002, 1);
        Assert.Equal(0, CompareRows(sample, sample, [new(SortColumn.Name)]));

        SortCriterion[] distancePriority = [new(SortColumn.Distance)];
        AssertOrder([sample, other with { Distance = 10 }], distancePriority, 1, 2);
        AssertOrder([sample with { Distance = 20 }, other with { Distance = 1 }], distancePriority, 2, 1);

        static void AssertOrder(List<ObjectExplorerRow> rows, IReadOnlyList<SortCriterion> criteria, params ulong[] expected)
        {
            SortRows(rows, criteria);
            Assert.Equal(expected, rows.Select(row => row.GameObjectId));
        }
    }

    [Fact]
    public void JsonlDiskLoggingDefaultsOff()
    {
        var configuration = new Configuration();

        Assert.False(configuration.HigherLowerVfxDataminingEnabled);
    }

    [Fact]
    public void FrameworkHitchProfilerDefaultsOffForNewAndPreExistingConfigurations()
    {
        var newConfiguration = new Configuration();
        var existingConfiguration = JsonSerializer.Deserialize<Configuration>("""{"Version":21,"PluginEnabled":true}""")!;

        Assert.False(newConfiguration.FrameworkHitchProfilerEnabled);
        Assert.False(existingConfiguration.FrameworkHitchProfilerEnabled);
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
