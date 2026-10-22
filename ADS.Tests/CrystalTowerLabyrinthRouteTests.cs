using System.Numerics;
using System.Reflection;
using ADS.Models;
using ADS.Services;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using static ADS.Services.ExecutionService;

namespace ADS.Tests;

public sealed class CrystalTowerLabyrinthRouteTests
{
    [Theory]
    [InlineData("SYNC")]
    [InlineData("UNSYNC")]
    public void BoneCorpseRequiresItsDirectorReceiptBeforeTheNativeShortcutCanBeAttempted(string mode)
    {
        using var f = new Fixture(new(-451.2f, 25.53f, 23.93f), mode: mode);
        f.Actors = [Npc(1, 0x92B, f.Local.Position, true)];
        Assert.True(f.Tick());
        Assert.Equal("Bone", f.Stage);
        f.Directors = [new(11, f.Now, 1, 0x80000001, 0xB5, 0, 0, 0)];
        Assert.True(f.Tick());
        Assert.Equal("Bone", f.Stage);
        f.Directors = [new(12, f.Now, 1, 0x80000001, 0xB4, 0, 0, 0)];
        Assert.True(f.Tick());
        Assert.Equal("BoneShortcut", f.Stage);
        f.Directors = [];
        Assert.True(f.Tick());
        Assert.Contains("/vnav moveto -450.51 24.74 12.31", f.Native.Commands);
        Assert.Empty(f.Interactions);
        f.Local = f.Local with { Position = new(-450.51f, 24.74f, 12.31f) };
        var shortcutPosition = new Vector3(-451.17691f, 25.50491f, 18.912251f);
        f.Actors = [.. f.Actors, Event(20, 2002804, shortcutPosition)];
        Assert.True(f.Tick());
        Assert.True(f.Tick());
        Assert.Empty(f.Interactions); // The authored player approach is not the object position.
        f.Local = f.Local with { Position = shortcutPosition };
        Assert.True(f.Tick());
        Assert.Contains("movement settle", f.Native.Execution.LastStatus);
        f.Now += TimeSpan.FromSeconds(1);
        Assert.True(f.Tick());
        Assert.Equal(new ulong[] { 20 }, f.Interactions);
        Assert.Equal("BoneShortcut", f.Stage); // A successful dispatch is not a landing.
        f.Dialog = true;
        Assert.True(f.Tick());
        f.Dialog = false;
        f.Actors = [];
        Assert.True(f.Tick());
        Assert.Equal("BoneShortcut", f.Stage); // Neither dialog close nor disappearance clears it.
        f.Context = Context(betweenAreas: true);
        Assert.True(f.Tick());
        Assert.Equal("BoneShortcut", f.Stage);
        f.Context = Context();
        f.Local = f.Local with { Position = new(-110.22f, 48.51f, 318.52f) };
        Assert.True(f.Tick());
        Assert.Equal("BoneShortcut", f.Stage);
        f.Local = f.Local with { Position = new(-110.22f, 45.51f, 318.52f) };
        Assert.True(f.Tick());
        Assert.Equal("Atomos", f.Stage);
        Assert.Single(f.Interactions);
    }

    [Theory]
    [InlineData("A", "SYNC", 244f, 0x1E8F80u)]
    [InlineData("B", "SYNC", 280f, 0x1E8F81u)]
    [InlineData("C", "SYNC", 315f, 0x1E8F82u)]
    [InlineData("A", "UNSYNC", 244f, 0x1E8F80u)]
    [InlineData("B", "UNSYNC", 280f, 0x1E8F81u)]
    [InlineData("C", "UNSYNC", 315f, 0x1E8F82u)]
    public void SharedBridgeApproachUsesTheObservedOwnLanePadInFullXyzBeforeMechanics(string alliance,
        string mode, float z, uint padId)
    {
        using var f = BoneToAtomos(alliance, mode);
        Assert.True(f.Tick());
        Assert.Contains("/vnav moveto 10.37 48.54 278.98", f.Native.Commands);
        f.Local = f.Local with { Position = new(10.37f, 48.54f, 278.98f) };
        Assert.True(f.Tick());
        Assert.True(f.Tick());
        Assert.Contains("/vnav moveto 126.25 62.40 269.16", f.Native.Commands);
        f.Local = f.Local with { Position = new(126.25f, 62.4f, 269.16f) };
        Assert.True(f.Tick());
        var pad = Event(20, padId, new(197, 50.56f, z), targetable: false);
        f.Actors = [pad, .. Atomos(false)];
        // Enter the old XZ-only handler's rectangle while still on the bridge's Y.
        f.Local = f.Local with { Position = new(197, 62.4f, 280) };
        f.Native.Commands.Clear();
        Assert.True(f.Tick());
        Assert.Equal(2, f.Waypoint);
        Assert.Contains($"/vnav moveto 197.00 50.56 {z.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}", f.Native.Commands);
        Assert.DoesNotContain(f.Native.Commands, command => command.Contains("195.66"));
        f.Local = f.Local with { Position = pad.Position };
        Assert.True(f.Tick());
        Assert.Equal(3, f.Waypoint);
        Assert.True(f.Tick()); // Roster remains incomplete: mechanics hold, no invented clear.
        Assert.Equal("Atomos", f.Stage);
        f.Actors = [pad, .. Atomos(true)];
        Assert.True(f.Tick());
        Assert.Equal("Thanatos", f.Stage);
        Assert.True(f.Tick());
        Assert.Contains("/vnav moveto 353.65 57.95 279.33", f.Native.Commands);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("native-identity")]
    [InlineData("native-position")]
    [InlineData("wrong-map")]
    [InlineData("duplicate")]
    [InlineData("dialog")]
    [InlineData("approach-point")]
    public void AShortcutNeedsAnExactNativeAndObservedJoinAndNeverConfirmsAnUnknownPrompt(string invalid)
    {
        using var f = ClearedBoss(0x92E, new(440.4f, 66.21f, 280));
        f.Local = f.Local with { Position = new(451.2f, 66.16f, 288.54f) };
        var shortcut = Event(20, 2002805, new(455.122894f, 66.249268f, 279.987701f));
        f.Actors = [shortcut];
        Assert.True(f.Tick()); // Exact authored source arrival, not an interaction.
        f.Local = f.Local with { Position = shortcut.Position };
        if (invalid == "missing") f.IncludeInteractables = false;
        if (invalid == "native-identity") f.NativeOverride = actor => actor with { EntityId = actor.EntityId + 1 };
        if (invalid == "native-position") f.NativeOverride = actor => actor with { Position = actor.Position + Vector3.UnitX };
        if (invalid == "wrong-map") f.ObservedMap = 2;
        if (invalid == "duplicate") f.Actors = [shortcut, shortcut with { GameObjectId = 21 }];
        if (invalid == "dialog") f.Dialog = true;
        if (invalid == "approach-point") f.Actors = [shortcut with { Position = new(451.2f, 66.16f, 288.54f) }];
        Assert.True(f.Tick());
        Assert.Empty(f.Interactions);
        Assert.Equal("ThanatosShortcut", f.Stage);
        Assert.Contains("verified shortcut activation and arrival", f.Native.Execution.LastStatus);
    }

    [Fact]
    public void ThanatosShortcutUsesThePlacedObjectAfterTheDistinctPlayerApproach()
    {
        using var f = ClearedBoss(0x92E, new(440.4f, 66.21f, 280));
        f.Local = f.Local with { Position = new(451.2f, 66.16f, 288.54f) };
        var position = new Vector3(455.122894f, 66.249268f, 279.987701f);
        f.Actors = [Event(20, 2002805, position)];
        Assert.True(f.Tick());
        Assert.True(f.Tick());
        Assert.Empty(f.Interactions);
        f.Local = f.Local with { Position = position };
        Assert.True(f.Tick());
        Assert.Contains("movement settle", f.Native.Execution.LastStatus);
        f.Now += TimeSpan.FromSeconds(1);
        Assert.True(f.Tick());
        Assert.Equal(new ulong[] { 20 }, f.Interactions);
        Assert.Equal("ThanatosShortcut", f.Stage);
    }

    [Theory]
    [InlineData(0x92Eu, 440.4f, 66.21f, 280f, "Thanatos", "ThanatosShortcut")]
    [InlineData(0x967u, -110f, 44.17f, -165.6f, "Bomb", "Behemoth")]
    [InlineData(0x932u, -110f, 68.05f, -368.35f, "Behemoth", "BehemothShortcut")]
    public void OnlyTheExactCurrentNativePrimaryDeathClearsANormalEncounter(uint id, float x, float y, float z,
        string stage, string next)
    {
        using var f = new Fixture(new(x, y, z));
        var boss = Npc(1, id, f.Local.Position, null);
        f.Actors = [boss];
        Assert.True(f.Tick());
        Assert.Equal(stage, f.Stage);
        f.Actors = [];
        Assert.True(f.Tick());
        Assert.Equal(stage, f.Stage);
        f.Actors = [boss with { Dead = true }];
        f.NativeOverride = actor => actor with { EntityId = 99 };
        Assert.True(f.Tick());
        Assert.Equal(stage, f.Stage);
        f.NativeOverride = null;
        Assert.True(f.Tick());
        Assert.Equal(next, f.Stage);
        f.Actors = [boss with { GameObjectId = 2, EntityId = 2, Dead = false }];
        f.Tick();
        Assert.Equal(stage, f.Stage);
    }

    [Fact]
    public void EarlyUnnamedTrashAndAStaleDifferentFloorDoNotAdmitABossRoute()
    {
        using var f = new Fixture(new(-353.5f, 41.6f, 279.17f));
        f.Actors = [Npc(1, 0x92B, new(-451.2f, 25.53f, 23.93f), true)];
        Assert.False(f.Tick());
        Assert.Null(f.Stage);
        f.Local = f.Local with { Position = new(-451.2f, 100, 23.93f) };
        f.Tick();
        Assert.Null(f.Stage);
        Assert.DoesNotContain(f.Native.Commands, IsMove);
    }

    [Fact]
    public void FinalTransferCannotWalkAcrossFloorsOrCompleteFromACorpse()
    {
        using var f = ClearedBoss(0x932, new(-110, 68.05f, -368.35f));
        f.Local = f.Local with { Position = new(-113.87f, 68.16f, -376.17f) };
        var shortcutPosition = new Vector3(-109.976601f, 68.215019f, -378.115906f);
        f.Actors = [Event(20, 2002806, shortcutPosition)];
        Assert.True(f.Tick());
        Assert.True(f.Tick());
        f.Local = f.Local with { Position = shortcutPosition };
        Assert.True(f.Tick());
        Assert.Contains("movement settle", f.Native.Execution.LastStatus);
        f.Now += TimeSpan.FromSeconds(1);
        Assert.True(f.Tick());
        Assert.Single(f.Interactions);
        f.Local = f.Local with { Position = new(-109.55f, 45.51f, 315.78f) };
        f.Actors = [];
        Assert.True(f.Tick());
        Assert.Equal("FinalTransfer", f.Stage);
        Assert.True(f.Tick());
        Assert.Contains("/vnav moveto -110.57 48.22 300.28", f.Native.Commands);
        f.Local = f.Local with { Position = new(-110.57f, 48.22f, 300.28f) };
        Assert.True(f.Tick());
        f.Native.Commands.Clear();
        f.Now += TimeSpan.FromMinutes(5);
        Assert.True(f.Tick());
        Assert.Equal("FinalTransfer", f.Stage);
        Assert.DoesNotContain(f.Native.Commands, IsMove);
        f.Local = f.Local with { Position = new(-112.49f, 650.13f, 225.52f) };
        Assert.True(f.Tick());
        Assert.Equal("FinalTransfer", f.Stage); // Position alone cannot invent the destination encounter.
        f.Actors = [Npc(1, 0x938, new(-110, 650.13f, 181.6f), true)];
        Assert.True(f.Tick());
        Assert.Equal("Phlegethon", f.Stage);
        Assert.True(f.Tick());
        f.Actors = [];
        Assert.True(f.Tick());
        f.Native.Execution.MarkDutyCompleted(f.Context, 174, 92);
        Assert.False(f.Tick());
        Assert.DoesNotContain(f.Native.Commands, IsMove);
    }

    [Fact]
    public void ARealLaterShortcutLandingDoesNotNavigateBackToAnUnfinishedSourceApproach()
    {
        using var f = ClearedBoss(0x92E, new(440.4f, 66.21f, 280));
        Assert.Equal(0, f.Waypoint);
        f.Local = f.Local with { Position = new(-110.36f, 45.51f, 316.1f) };
        f.Actors = [];
        Assert.True(f.Tick());
        Assert.Equal("Bomb", f.Stage);
        Assert.Empty(f.Interactions);
        Assert.DoesNotContain(f.Native.Commands, IsMove);
    }

    [Fact]
    public void NoProgressAndTransitionsDoNotConsumeAWaypointAndCancellationStopsThePath()
    {
        using var f = ClearedBoss(0x92E, new(440.4f, 66.21f, 280));
        Assert.True(f.Tick());
        Assert.Contains(f.Native.Commands, IsMove);
        f.Now += TimeSpan.FromMinutes(2);
        Assert.True(f.Tick());
        Assert.Equal(0, f.Waypoint);
        f.Context = Context(betweenAreas: true);
        Assert.True(f.Tick());
        Assert.Equal(0, f.Waypoint);
        Assert.Equal("ThanatosShortcut", f.Stage);
        f.Context = Context();
        f.Native.Execution.CancelCrystalTower(f.Context);
        Assert.Null(f.Stage);
        Assert.Equal(0, f.Waypoint);
        Assert.Contains("/vnav stop", f.Native.Commands);
    }

    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    private static T Get<T>(ExecutionService service, string name) => (T)typeof(ExecutionService).GetField(name, Flags)!.GetValue(service)!;
    private static bool IsMove(string command) => command.StartsWith("/vnav moveto", StringComparison.Ordinal);
    private static DutyContextSnapshot Context(string alliance = "A", bool betweenAreas = false)
        => TestDutyContextFactory.Create(DutyCategory.AllianceRaid, territoryId: 174, cfcId: 92,
            alliance: alliance, betweenAreas: betweenAreas);
    private static CrystalTowerActorObservation Npc(ulong id, uint dataId, Vector3 position, bool? dead = false)
        => new(id, (uint)id, dataId, ObjectKind.BattleNpc, position, dead, true, false, 0, [], null, null, 1);
    private static CrystalTowerActorObservation Event(ulong id, uint dataId, Vector3 position, bool targetable = true)
        => new(id, 0xE0000000, dataId, ObjectKind.EventObj, position, null, targetable, false, 0, [], 0, null, 0);
    private static CrystalTowerActorObservation[] Atomos(bool dead)
        => new[] { 244f, 280f, 315f }.Select((z, i) => Npc((ulong)i + 1, 0x963, new(253, 50.56f, z), dead)).ToArray();
    private static Fixture ClearedBoss(uint id, Vector3 position)
    {
        var f = new Fixture(position);
        f.Actors = [Npc(1, id, position, true)];
        Assert.True(f.Tick());
        return f;
    }
    private static Fixture BoneToAtomos(string alliance, string mode)
    {
        var f = new Fixture(new(-451.2f, 25.53f, 23.93f), alliance, mode);
        f.Actors = [Npc(1, 0x92B, f.Local.Position, true)];
        f.Directors = [new(12, f.Now, 1, 0x80000001, 0xB4, 0, 0, 0)];
        Assert.True(f.Tick());
        f.Directors = [];
        f.Local = f.Local with { Position = new(-450.51f, 24.74f, 12.31f) };
        Assert.True(f.Tick());
        f.Local = f.Local with { Position = new(-110.22f, 45.51f, 318.52f) };
        f.Actors = [];
        Assert.True(f.Tick());
        Assert.Equal("Atomos", f.Stage);
        return f;
    }

    private sealed class Fixture : IDisposable
    {
        internal Fixture(Vector3 position, string alliance = "A", string mode = "SYNC")
        {
            Context = CrystalTowerLabyrinthRouteTests.Context(alliance);
            Local = new(1123, 123, 0, ObjectKind.Pc, position, false, true, false, 0, [], null, 25);
            Native = new(position, initialRules: new ObjectPriorityRuleManifest { Rules =
                [new() { TerritoryTypeId = 174, ContentFinderConditionId = 92, ObjectKind = "BattleNpc",
                    BaseId = 0x92B, Classification = "Required", CrystalTowerStrategy = mode }] });
            Native.Execution.ReadUtcNow = () => Now;
            Native.Execution.CrystalTowerMovementCharacterIdOverride = () => 1;
            Native.Execution.CrystalTowerMovementLocalEntityIdOverride = () => Local.EntityId;
            Native.Execution.ReadCrystalTowerMovementOverride = () => new(false, null, "fixture: no provider");
            Native.Execution.ReadInteractionVbmOverride = () => new(false, null, "fixture: no provider");
            Native.Execution.QueryInteractionDialog = () => Dialog;
            Native.Execution.DispatchInteractionOverride = actor => { Interactions.Add(actor.GameObjectId); return true; };
            Native.Execution.CrystalTowerObservationOverride = _ => new(true, "fixture", Local.EntityId, Local,
                false, [], Actors, [], 20, Directors);
        }
        internal HyperFixatedTargetExecutionTests.Fixture Native { get; }
        internal CrystalTowerActorObservation Local;
        internal DutyContextSnapshot Context;
        internal CrystalTowerActorObservation[] Actors = [];
        internal CrystalTowerDirectorObservation[] Directors = [];
        internal Func<CrystalTowerActorObservation, CrystalTowerActorObservation>? NativeOverride;
        internal bool IncludeInteractables = true;
        internal uint ObservedMap = 1;
        internal bool Dialog;
        internal DateTime Now = new(2026, 10, 22, 1, 0, 0, DateTimeKind.Utc);
        internal List<ulong> Interactions = [];
        internal string? Stage => Get<object?>(Native.Execution, "labyrinthRouteStage")?.ToString();
        internal int Waypoint => Get<int>(Native.Execution, "labyrinthRouteWaypoint");
        internal bool Tick()
        {
            var table = Get<HyperFixatedTargetExecutionTests.ObjectTableProxy>(Native.Execution, "objectTable");
            ((HyperFixatedTargetExecutionTests.GameObjectProxy)(object)table.LocalPlayer!).Position = Local.Position;
            table.Objects.Clear();
            var monsters = new List<ObservedMonster>();
            var interactables = new List<ObservedInteractable>();
            foreach (var actor in Actors)
            {
                var native = DispatchProxy.Create<IGameObject, CrystalTowerWaveTests.ActorProxy>();
                ((CrystalTowerWaveTests.ActorProxy)(object)native).Actor = NativeOverride?.Invoke(actor) ?? actor;
                var observed = new ObservedMonster { Key = $"labyrinth:{actor.GameObjectId}", GameObjectId = actor.GameObjectId,
                    DataId = actor.DataId, MapId = Context.MapId, Name = "fixture", Position = actor.Position, LastSeenUtc = Now };
                table.Objects.Add(new(native, observed));
                if (actor.ObjectKind == ObjectKind.BattleNpc) monsters.Add(observed);
                if (actor.ObjectKind == ObjectKind.EventObj && IncludeInteractables)
                    interactables.Add(new() { Key = observed.Key, GameObjectId = actor.GameObjectId, DataId = actor.DataId,
                        MapId = ObservedMap, ObjectKind = actor.ObjectKind, Name = "fixture shortcut", Position = actor.Position,
                        LastSeenUtc = Now, Classification = InteractableClass.Optional, GhostReason = GhostReason.SeenPreviously });
            }
            return (bool)typeof(ExecutionService).GetMethod("TryHandleCrystalTower", Flags)!.Invoke(Native.Execution,
                [Context, new ObservationSnapshot { LiveMonsters = monsters, LiveFollowTargets = [], MonsterGhosts = [],
                    LiveInteractables = interactables, InteractableGhosts = [] }])!;
        }
        public void Dispose() => Native.Dispose();
    }
}
