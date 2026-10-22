using System.Numerics;
using System.Reflection;
using System.Globalization;
using ADS.Models;
using ADS.Services;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using static ADS.Services.ExecutionService;
using ServerEventKind = ADS.Services.HigherLowerServerEventTraceService.ServerEventKind;
using ServerEventRow = ADS.Services.HigherLowerServerEventTraceService.ServerEventRow;

namespace ADS.Tests;

public sealed class CrystalTowerWorldOfDarknessTests
{
    public static IEnumerable<object[]> EarlierRoomsWithLoadedFutureActors()
    {
        // Reviewed awgil d409d336 arena centers, not fabricated route coordinates.
        foreach (var room in new[] { (0xD9Cu, -77f, 383f), (0xE00u, -147f, 297f),
                     (0xDA0u, 130f, 265f), (0xDFCu, 200f, 179f) })
        foreach (var later in new[] { "Atomos", "Cerberus", "Cloud" })
            yield return [room.Item1, room.Item2, room.Item3, later];
    }

    [Theory]
    [MemberData(nameof(EarlierRoomsWithLoadedFutureActors))]
    public void RootDispatchDoesNotLetLoadedLaterRoomsHijackAnEarlierEncounter(uint dataId, float x, float z, string later)
    {
        foreach (var mode in new[] { "SYNC", "UNSYNC" })
        {
            using var f = new Fixture(mode: mode);
            f.Local = f.Local with { Position = new(x, 100, z) };
            var earlier = Npc(80, dataId, new(x + 1, 100, z));
            f.Actors = [earlier, .. LoadedFutureRoom(later)];
            if (later == "Cloud")
            {
                var hex = f.Actors.Single(actor => actor.DataId == 0x1E9752);
                f.Animations = [new(10, f.Now, hex.GameObjectId, hex.EntityId, hex.DataId, 0x00040008)];
            }

            f.Tick(); // Invoke the actual shared Crystal Tower dispatch, not a room helper.

            Assert.True(f.Native.Target is null || f.Native.Target.GameObjectId == earlier.GameObjectId);
            Assert.DoesNotContain(f.Native.Commands, command => command.StartsWith("/vnav moveto"));
            Assert.DoesNotContain("assigned Atomos", f.Native.Execution.LastStatus);
            Assert.DoesNotContain("hypercharged-cloud", f.Native.Execution.LastStatus);
            Assert.DoesNotContain("automatic chain", f.Native.Execution.LastStatus);
        }
    }

    [Fact]
    public void DoomOwnershipCannotAdmitLoadedFutureRoomsAfterTheDebuffClears()
    {
        using var f = new Fixture();
        f.Local = f.Local with { Position = new(-147, 100, 297), StatusIds = [210] };
        var pad = Event(70, 0x1E9711, new(-140, 100, 297));
        var hex = LoadedFutureRoom("Cloud").Single(actor => actor.DataId == 0x1E9752);
        f.Actors = [pad, .. LoadedFutureRoom("Atomos"), .. LoadedFutureRoom("Cerberus"), hex];
        f.Rows = [f.State(pad, 4, 10)];
        f.Animations = [new(10, f.Now, hex.GameObjectId, hex.EntityId, hex.DataId, 0x00040008)];

        Assert.True(f.Tick());
        Assert.Contains("/vnav moveto -140.00 100.00 297.00", f.Native.Commands);
        var before = f.Native.Commands.Count;
        f.Local = f.Local with { StatusIds = [] };
        f.Tick();

        Assert.Null(f.Native.Target);
        Assert.DoesNotContain(f.Native.Commands.Skip(before), command => command.StartsWith("/vnav moveto"));
        Assert.Contains("/vnav stop", f.Native.Commands.Skip(before));
    }

    [Fact]
    public void StomachFloorPrecedesLoadedOtherRoomsWhenTheOutsideBossIsMissing()
    {
        using var f = new Fixture("B");
        f.Local = f.Local with { Position = new(0, -200, -198) };
        var stomach = Npc(80, 0xDFA, new(1, -200, -198));
        f.Actors = [stomach, .. LoadedFutureRoom("Atomos"), .. LoadedFutureRoom("Cloud")];

        Assert.True(f.Tick());
        Assert.Equal(stomach.GameObjectId, f.Native.Target?.GameObjectId);
        Assert.DoesNotContain(f.Native.Commands, command => command.StartsWith("/vnav moveto"));
    }

    [Theory]
    [InlineData(0xDFDu)]
    [InlineData(0xE13u)]
    [InlineData(0xE14u)]
    [InlineData(0xE15u)]
    [InlineData(0xE16u)]
    public void FiveHeadedNativeAddsTakePriorityOverTheOrdinaryBossInBothModes(uint addId)
    {
        foreach (var mode in new[] { "SYNC", "UNSYNC" })
        {
            using var f = new Fixture(mode: mode);
            f.Local = f.Local with { Position = new(200, 100, 179) };
            var add = Npc(81, addId, new(201, 100, 179));
            f.Actors = [Npc(80, 0xDFC, new(200, 100, 180)), add, .. LoadedFutureRoom("Atomos"),
                .. LoadedFutureRoom("Cerberus"), .. LoadedFutureRoom("Cloud")];

            Assert.True(f.Tick());
            Assert.Equal(add.GameObjectId, f.Native.Target?.GameObjectId);
            Assert.DoesNotContain(f.Native.Commands, command => command.StartsWith("/vnav moveto"));
        }
    }

    [Fact]
    public void ProminenceWinsBeforeCloserHeadsAndDeadProminenceDoesNotShadowALivingHead()
    {
        foreach (var mode in new[] { "SYNC", "UNSYNC" })
        {
            using var f = new Fixture(mode: mode);
            f.Local = f.Local with { Position = new(200, 100, 179) };
            var head = Npc(81, 0xE13, new(201, 100, 179));
            var prominence = Npc(82, 0xDFD, new(202, 100, 179));
            f.Actors = [Npc(80, 0xDFC, new(200, 100, 180)), head, prominence];
            Assert.True(f.Tick());
            Assert.Equal(prominence.GameObjectId, f.Native.Target?.GameObjectId);

            f.Actors = [f.Actors[0], head, prominence with { Dead = true }];
            Assert.True(f.Tick());
            Assert.Equal(head.GameObjectId, f.Native.Target?.GameObjectId);
        }
    }

    [Theory]
    [InlineData("dead")]
    [InlineData("unknown-life")]
    [InlineData("untargetable")]
    [InlineData("unknown-targetable")]
    [InlineData("foreign-room")]
    [InlineData("nonfinite-position")]
    [InlineData("invalid-entity")]
    [InlineData("invalid-object")]
    public void FiveHeadedAddsRequireALivingTargetableIdentityInTheCurrentRoom(string invalid)
    {
        using var f = new Fixture();
        f.Local = f.Local with { Position = new(200, 100, 179) };
        var add = Npc(81, 0xDFD, new(201, 100, 179));
        add = invalid switch
        {
            "dead" => add with { Dead = true },
            "unknown-life" => add with { Dead = null },
            "untargetable" => add with { Targetable = false },
            "unknown-targetable" => add with { Targetable = null },
            "foreign-room" => add with { Position = new(0, 100, -198) },
            "nonfinite-position" => add with { Position = new(float.NaN, 100, 179) },
            "invalid-entity" => add with { EntityId = 0xE0000000 },
            _ => add with { GameObjectId = 0 },
        };
        var boss = Npc(80, 0xDFC, new(200, 100, 180));
        f.Actors = [boss, add];
        f.Tick();

        Assert.True(f.Native.Target is null || f.Native.Target.GameObjectId == boss.GameObjectId);
        Assert.DoesNotContain(f.Native.Commands, command => command.StartsWith("/vnav moveto"));
    }

    [Theory]
    [InlineData(-77f, 383f)]
    [InlineData(-147f, 297f)]
    [InlineData(130f, 265f)]
    public void LoadedFiveHeadedAddsCannotAdmitPlayersInEarlierRooms(float x, float z)
    {
        using var f = new Fixture();
        f.Local = f.Local with { Position = new(x, 100, z) };
        var earlier = Npc(80, x == -77 ? 0xD9Cu : x == -147 ? 0xE00u : 0xDA0u, new(x + 1, 100, z));
        f.Actors = [earlier, Npc(81, 0xDFD, new(200, 100, 179)), Npc(82, 0xE13, new(201, 100, 179))];
        f.Tick();

        Assert.True(f.Native.Target is null || f.Native.Target.GameObjectId == earlier.GameObjectId);
        Assert.DoesNotContain(f.Native.Commands, command => command.StartsWith("/vnav moveto"));
    }

    [Theory]
    [InlineData(210u, "Doom-cleansing")]
    [InlineData(639u, "movement provider")]
    public void DoomAndPyreticRetainPriorityOverFiveHeadedAdds(uint status, string reason)
    {
        using var f = new Fixture();
        f.Local = f.Local with { Position = new(200, 100, 179), StatusIds = [status] };
        f.Actors = [Npc(80, 0xDFC, new(200, 100, 180)), Npc(81, 0xDFD, new(201, 100, 179))];

        Assert.True(f.Tick());
        Assert.Null(f.Native.Target);
        Assert.Contains(reason, f.Native.Execution.LastStatus);
        Assert.DoesNotContain(f.Native.Commands, command => command.StartsWith("/vnav moveto"));
    }

    [Theory]
    [InlineData("A", -57f, 49f, -41f, 49f)]
    [InlineData("B", 0f, -31f, 0f, -15f)]
    [InlineData("C", 57f, 49f, 41f, 49f)]
    public void AtomosEnclosingRoomRetainsLowerRingReturnsAtTheOuterPlatforms(
        string alliance, float x, float z, float padX, float padZ)
    {
        using var f = new Fixture(alliance);
        f.Local = f.Local with { Position = new(x, 88, z) };
        f.Actors = [.. Atomos(), Event(50, 0x1E9738, new(padX, 100, padZ))];

        Assert.True(f.Tick());
        var destination = LastNavigation(f);
        Assert.Equal(88f, destination.Y);
        var own = Atomos().Single(actor => actor.GameObjectId == (alliance == "A" ? 10ul : alliance == "B" ? 11ul : 12ul));
        Assert.InRange(Vector2.Distance(new(destination.X, destination.Z), new(own.Position.X, own.Position.Z)), 25.99f, 26.01f);
        Assert.DoesNotContain(f.Native.Commands, command => command.Contains(" 100.00 "));
    }

    [Fact]
    public void DoomUsesJoinedNativeStateAndReleasesItsOwnedPathWhenTheDebuffClears()
    {
        using var f = new Fixture();
        f.Local = f.Local with { StatusIds = [210] };
        var pad = Event(10, 0x1E9711, new(12, 100, 20));
        f.Actors = [pad];
        f.Rows = [f.State(pad, 4, 10)];
        Assert.True(f.Tick());
        Assert.Contains("/vnav moveto 12.00 100.00 20.00", f.Native.Commands);
        f.Local = f.Local with { StatusIds = [] };
        Assert.False(f.Tick());
        Assert.Contains("/vnav stop", f.Native.Commands);
    }

    [Theory]
    [InlineData("no-packet")]
    [InlineData("wrong-identity")]
    [InlineData("old-owner")]
    [InlineData("replay")]
    public void AByteStateOrUnownedPacketDoesNotEstablishTheDoomPad(string invalid)
    {
        using var f = new Fixture();
        f.Local = f.Local with { StatusIds = [210] };
        var pad = Event(10, 0x1E9711, new(12, 100, 20)) with { EventState = 4 };
        f.Actors = [pad];
        var row = f.State(pad, 4, 10);
        f.Rows = invalid switch
        {
            "no-packet" => [],
            "wrong-identity" => [row with { EntityId = 1234 }],
            "old-owner" => [row with { TimestampUtc = f.Now.AddSeconds(-1) }],
            _ => [row with { Replaying = 1 }],
        };
        Assert.True(f.Tick());
        Assert.DoesNotContain(f.Native.Commands, command => command.StartsWith("/vnav moveto"));
        Assert.Contains("Doom-cleansing", f.Native.Execution.LastStatus);
    }

    [Fact]
    public void AtomosShockwaveUsesTheAssignedNativePlatformInBothModes()
    {
        foreach (var mode in new[] { "SYNC", "UNSYNC" })
        {
            using var f = new Fixture(mode: mode);
            f.Local = f.Local with { Position = new(-20, 100, 49) };
            f.Actors = Atomos().Select(actor => actor.GameObjectId == 10
                ? actor with { IsCasting = true, CastActionId = 3416 } : actor).ToArray();
            Assert.True(f.Tick());
            Assert.Contains("/vnav moveto -31.00 100.00 49.00", f.Native.Commands);
            Assert.DoesNotContain(f.Native.Commands, command => command.Contains("0.00 100.00 -5.00"));
        }
    }

    [Fact]
    public void FallenAtomosPlayerUsesObservedPadGeometryAndTheLowerObservedHeight()
    {
        using var f = new Fixture();
        f.Local = f.Local with { Position = new(-5, 88, 49) };
        f.Actors = Atomos().Append(Event(50, 0x1E9738, new(-21, 100, 49))).ToArray();
        Assert.True(f.Tick());
        Assert.Contains("/vnav moveto -12.62 88.00 30.62", f.Native.Commands);
        Assert.DoesNotContain(f.Native.Commands, command => command.Contains(" 100.00 "));
    }

    [Theory]
    [InlineData(88f, true)]
    [InlineData(92.99f, true)]
    [InlineData(93f, false)]
    [InlineData(100f, false)]
    public void UpperAtomosHolderElectionUsesActualActorHeight(float otherHeight, bool localHolds)
    {
        using var f = new Fixture();
        f.Local = f.Local with { Position = new(-31, 100, 49) };
        var pad = Event(50, 0x1E9738, new(-21, 100, 49));
        f.Members = [new(120, CrystalTowerRole.Healer, true, new(-31, 49)),
            new(123, CrystalTowerRole.Ranged, true, new(-31, 49))];
        f.Actors = [.. Atomos(), pad, Player(120, new(-31, otherHeight, 49))];
        f.Animations = [new(10, f.Now, pad.GameObjectId, pad.EntityId, pad.DataId, 0x00800100)];

        Assert.True(f.Tick());
        if (localHolds)
            Assert.Contains("/vnav moveto -21.00 100.00 49.00", f.Native.Commands);
        else
        {
            Assert.DoesNotContain(f.Native.Commands, command => command.StartsWith("/vnav moveto"));
            Assert.Equal(10ul, f.Native.Target?.GameObjectId);
        }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("nonfinite")]
    [InlineData("dead")]
    public void UnknownUpperAtomosCandidateHeightCannotBeInferredFromTheXzRoster(string invalid)
    {
        using var f = new Fixture();
        f.Local = f.Local with { Position = new(-31, 100, 49) };
        var pad = Event(50, 0x1E9738, new(-21, 100, 49));
        var other = Player(120, new(-31, 100, 49));
        f.Members = [new(120, CrystalTowerRole.Healer, true, new(-31, 49)),
            new(123, CrystalTowerRole.Ranged, true, new(-31, 49))];
        CrystalTowerActorObservation[] candidates = invalid switch
        {
            "missing" => Array.Empty<CrystalTowerActorObservation>(),
            "duplicate" => [other, other with { GameObjectId = 1121 }],
            "nonfinite" => [other with { Position = new(-31, float.NaN, 49) }],
            _ => [other with { Dead = true }],
        };
        f.Actors = [.. Atomos(), pad, .. candidates];
        f.Animations = [new(10, f.Now, pad.GameObjectId, pad.EntityId, pad.DataId, 0x00800100)];

        Assert.True(f.Tick());
        Assert.Contains("verified tower assignments", f.Native.Execution.LastStatus);
        Assert.Null(f.Native.Target);
        Assert.DoesNotContain(f.Native.Commands, command => command.StartsWith("/vnav moveto"));
    }

    [Theory]
    [InlineData(0f, true)]
    [InlineData(60f, true)]
    [InlineData(62f, true)]
    [InlineData(65f, false)]
    [InlineData(180f, false)]
    public void WhiteHoleUsesCastFacingAndIncludesThePlayersHitboxAtTheConeBoundary(float playerAngle, bool mustAvoid)
    {
        using var f = new Fixture();
        f.Local = f.Local with { Position = AtomosPoint(10, playerAngle) };
        f.Actors = Atomos().Select(actor => actor.GameObjectId == 10
            ? actor with { IsCasting = true, CastActionId = 3415, CastRotation = 0, Rotation = MathF.PI } : actor).ToArray();

        Assert.True(f.Tick());
        if (mustAvoid) AssertAtomosConeEscape(f, 60);
        else
        {
            Assert.DoesNotContain(f.Native.Commands, command => command.StartsWith("/vnav moveto"));
            Assert.Equal(10ul, f.Native.Target?.GameObjectId);
        }
    }

    [Fact]
    public void ConeEscapeNearThePlatformEdgeKeepsTheWholeNativeHitboxOnTheAssignedPlatform()
    {
        using var f = new Fixture();
        f.Local = f.Local with { Position = AtomosPoint(17.8f, 0) };
        f.Actors = Atomos().Select(actor => actor.GameObjectId == 10
            ? actor with { IsCasting = true, CastActionId = 3415, CastRotation = 0 } : actor).ToArray();

        Assert.True(f.Tick());
        AssertAtomosConeEscape(f, 60);
    }

    [Theory]
    [InlineData("missing-cast-facing")]
    [InlineData("nonfinite-cast-facing")]
    [InlineData("unknown-cast")]
    [InlineData("unknown-action")]
    [InlineData("missing-local-radius")]
    [InlineData("missing-boss-radius")]
    [InlineData("nonfinite-radius")]
    [InlineData("no-body-clearance")]
    public void UnknownWhiteHoleGeometryCannotUseCurrentFacingOrAnInventedEscape(string invalid)
    {
        using var f = new Fixture();
        f.Local = f.Local with { Position = AtomosPoint(10, 0) };
        var boss = Atomos()[0] with { IsCasting = true, CastActionId = 3415, CastRotation = 0, Rotation = 0 };
        switch (invalid)
        {
            case "missing-cast-facing": boss = boss with { CastRotation = null }; break;
            case "nonfinite-cast-facing": boss = boss with { CastRotation = float.NaN }; break;
            case "unknown-cast": boss = boss with { IsCasting = null }; break;
            case "unknown-action": boss = boss with { CastActionId = null }; break;
            case "missing-local-radius": f.Local = f.Local with { HitboxRadius = null }; break;
            case "missing-boss-radius": boss = boss with { HitboxRadius = null }; break;
            case "nonfinite-radius": boss = boss with { HitboxRadius = float.PositiveInfinity }; break;
            case "no-body-clearance": boss = boss with { HitboxRadius = 17 }; break;
        }
        f.Actors = [boss, .. Atomos().Skip(1)];

        Assert.True(f.Tick());
        Assert.Contains("verified alliance mechanic state", f.Native.Execution.LastStatus);
        Assert.DoesNotContain(f.Native.Commands, command => command.StartsWith("/vnav moveto"));
        Assert.Null(f.Native.Target);
    }

    [Theory]
    [InlineData(0f, true)]
    [InlineData(45f, true)]
    [InlineData(47f, true)]
    [InlineData(50f, false)]
    public void BlackHoleFacesTheObservedNativeTargetInsteadOfTheBossRotation(float playerAngle, bool mustAvoid)
    {
        using var f = new Fixture();
        f.Local = f.Local with { Position = AtomosPoint(10, playerAngle) };
        var target = Player(120, AtomosPoint(12, 0));
        f.Actors = [.. Atomos().Select(actor => actor.GameObjectId == 10
            ? actor with { TargetObjectId = target.GameObjectId, Rotation = MathF.PI / 2 } : actor), target];

        Assert.True(f.Tick());
        if (mustAvoid) AssertAtomosConeEscape(f, 45);
        else
        {
            Assert.DoesNotContain(f.Native.Commands, command => command.StartsWith("/vnav moveto"));
            Assert.Equal(10ul, f.Native.Target?.GameObjectId);
        }
    }

    [Theory]
    [InlineData("missing-id")]
    [InlineData("zero-id")]
    [InlineData("entity-id-only")]
    [InlineData("missing-actor")]
    [InlineData("duplicate-actor")]
    [InlineData("dead-actor")]
    [InlineData("coincident-actor")]
    public void UnknownBlackHoleTargetCannotInferFacingOrTheMarkedPlayer(string invalid)
    {
        using var f = new Fixture();
        f.Local = f.Local with { Position = AtomosPoint(10, 0) };
        var target = Player(120, AtomosPoint(12, 0));
        var boss = Atomos()[0] with { TargetObjectId = target.GameObjectId, Rotation = 0 };
        CrystalTowerActorObservation[] targets = [target];
        switch (invalid)
        {
            case "missing-id": boss = boss with { TargetObjectId = null }; break;
            case "zero-id": boss = boss with { TargetObjectId = 0 }; break;
            case "entity-id-only": boss = boss with { TargetObjectId = f.Local.EntityId }; break;
            case "missing-actor": targets = []; break;
            case "duplicate-actor": targets = [target, target with { EntityId = 121 }]; break;
            case "dead-actor": targets = [target with { Dead = true }]; break;
            case "coincident-actor": targets = [target with { Position = boss.Position }]; break;
        }
        f.Actors = [boss, .. Atomos().Skip(1), .. targets];

        Assert.True(f.Tick());
        Assert.Contains("verified alliance mechanic state", f.Native.Execution.LastStatus);
        Assert.DoesNotContain(f.Native.Commands, command => command.StartsWith("/vnav moveto"));
        Assert.Null(f.Native.Target);
    }

    [Fact]
    public void TheActualBlackHoleTargetIsExemptWithoutInventingItsFacingOrHitboxes()
    {
        using var f = new Fixture();
        f.Local = f.Local with { Position = AtomosPoint(1, 0), HitboxRadius = null };
        f.Actors = Atomos().Select(actor => actor.GameObjectId == 10
            ? actor with { TargetObjectId = f.Local.GameObjectId, Rotation = null, HitboxRadius = null } : actor).ToArray();

        Assert.True(f.Tick());
        Assert.Equal(10ul, f.Native.Target?.GameObjectId);
        Assert.DoesNotContain(f.Native.Commands, command => command.StartsWith("/vnav moveto"));
    }

    [Fact]
    public void BlackHoleIsInactiveDuringAnotherKnownCastAndForeignPlatformConesDoNotOwnThisLane()
    {
        using var f = new Fixture();
        f.Local = f.Local with { Position = AtomosPoint(1, 0) };
        f.Actors = Atomos().Select(actor => actor.GameObjectId == 10
            ? actor with { IsCasting = true, CastActionId = 3419, TargetObjectId = null }
            : actor with { IsCasting = true, CastActionId = 3415, CastRotation = null }).ToArray();

        Assert.True(f.Tick());
        Assert.Equal(10ul, f.Native.Target?.GameObjectId);
        Assert.DoesNotContain(f.Native.Commands, command => command.StartsWith("/vnav moveto"));
    }

    [Theory]
    [InlineData("B", 11ul, 0f, -5f)]
    [InlineData("C", 12ul, 31f, 49f)]
    public void ConeAvoidanceStaysInsideTheActualAssignedPlatform(string alliance, ulong bossId, float x, float z)
    {
        using var f = new Fixture(alliance);
        f.Local = f.Local with { Position = new(x, 100, z + 10) };
        f.Actors = Atomos().Select(actor => actor with { IsCasting = true, CastActionId = 3415,
            CastRotation = actor.GameObjectId == bossId ? 0 : null }).ToArray();

        Assert.True(f.Tick());
        var destination = LastNavigation(f);
        var offset = new Vector2(destination.X - x, destination.Z - z);
        Assert.InRange(offset.Length(), 4.5f, 17.5f);
        Assert.True(MathF.Abs(offset.X) * 0.5f - offset.Y * MathF.Sin(MathF.PI / 3) > 0.5f);
        Assert.Equal(100f, destination.Y);
    }

    [Fact]
    public void LowerRingReturnAndShockwaveTakePrecedenceOverUnknownConeEvidence()
    {
        using (var f = new Fixture())
        {
            f.Local = f.Local with { Position = new(-5, 88, 49) };
            f.Actors = [.. Atomos().Select(actor => actor with { IsCasting = true, CastActionId = 3415, CastRotation = null }),
                Event(50, 0x1E9738, new(-21, 100, 49))];
            Assert.True(f.Tick());
            Assert.Contains("/vnav moveto -12.62 88.00 30.62", f.Native.Commands);
        }
        using (var f = new Fixture())
        {
            f.Local = f.Local with { Position = AtomosPoint(10, 0), HitboxRadius = null };
            f.Actors = Atomos().Select(actor => actor.GameObjectId == 10
                ? actor with { IsCasting = true, CastActionId = 3416, CastRotation = null, TargetObjectId = null } : actor).ToArray();
            Assert.True(f.Tick());
            Assert.Contains("/vnav moveto -31.00 100.00 49.00", f.Native.Commands);
        }
    }

    [Theory]
    [InlineData("unsafe-destination")]
    [InlineData("unsafe-approach")]
    public void AGlowingPadCannotPullASafePlayerIntoAnObservedCone(string unsafeRoute)
    {
        using var f = new Fixture();
        f.Local = f.Local with { Position = AtomosPoint(10, 90) };
        var pad = Event(50, 0x1E9738, AtomosPoint(10, unsafeRoute == "unsafe-destination" ? 0 : -90));
        f.Actors = [.. Atomos().Select(actor => actor.GameObjectId == 10
            ? actor with { IsCasting = true, CastActionId = 3415, CastRotation = 0 } : actor), pad];
        f.Animations = [new(10, f.Now, pad.GameObjectId, pad.EntityId, pad.DataId, 0x00800100)];

        Assert.True(f.Tick());
        Assert.Equal(10ul, f.Native.Target?.GameObjectId);
        Assert.DoesNotContain(f.Native.Commands, command => command.StartsWith("/vnav moveto"));
    }

    [Fact]
    public void AVerifiedSafePadApproachCanContinueBesideAnObservedCone()
    {
        using var f = new Fixture();
        f.Local = f.Local with { Position = AtomosPoint(10, 90) };
        var pad = Event(50, 0x1E9738, new(-21, 100, 44));
        f.Actors = [.. Atomos().Select(actor => actor.GameObjectId == 10
            ? actor with { IsCasting = true, CastActionId = 3415, CastRotation = 0 } : actor), pad];
        f.Animations = [new(10, f.Now, pad.GameObjectId, pad.EntityId, pad.DataId, 0x00800100)];

        Assert.True(f.Tick());
        Assert.Contains("/vnav moveto -21.00 100.00 44.00", f.Native.Commands);
    }

    [Fact]
    public void WhiteHoleAvoidancePrecedesAPadHoldAndDoesNotSurviveVerifiedCastClear()
    {
        using var f = new Fixture();
        f.Local = f.Local with { Position = AtomosPoint(10, 0) };
        var pad = Event(50, 0x1E9738, f.Local.Position);
        f.Actors = [.. Atomos().Select(actor => actor.GameObjectId == 10
            ? actor with { IsCasting = true, CastActionId = 3415, CastRotation = 0 } : actor), pad];
        f.Animations = [new(10, f.Now, pad.GameObjectId, pad.EntityId, pad.DataId, 0x00800100)];
        Assert.True(f.Tick());
        AssertAtomosConeEscape(f, 60);

        f.Local = f.Local with { Position = LastNavigation(f) };
        f.Actors = [.. Atomos(), pad];
        Assert.True(f.Tick());
        Assert.Equal("/vnav moveto -31.00 100.00 59.00", f.Native.Commands.Last(command => command.StartsWith("/vnav moveto")));
    }

    [Fact]
    public void MissingAtomosDeathEvidenceDoesNotBecomeEncounterCompletion()
    {
        using var f = new Fixture();
        f.Local = f.Local with { Position = new(-20, 100, 49) };
        f.Actors = Atomos().Select(actor => actor with { IsCasting = true, CastActionId = 3416 }).ToArray();
        Assert.True(f.Tick());
        f.Actors = [];
        Assert.True(f.Tick());
        Assert.Contains("/vnav stop", f.Native.Commands);
        f.Actors = Atomos().Select(actor => actor with { Dead = true }).ToArray();
        Assert.False(f.Tick());
    }

    [Fact]
    public void PyreticStopsOwnedMovementAndTargetUntilTheObservedStatusIsGone()
    {
        using var f = new Fixture();
        f.Local = f.Local with { Position = new(0, 100, -198) };
        var boss = Npc(10, 0xDF6, new(0, 100, -198));
        var add = Npc(11, 0xDF8, new(1, 100, -198));
        f.Actors = [boss, add, Npc(12, 0xDF9, new(10, 100, -198))];
        Assert.True(f.Tick());
        Assert.Equal(add.GameObjectId, f.Native.Target?.GameObjectId);
        f.Local = f.Local with { StatusIds = [639] };
        Assert.True(f.Tick());
        Assert.Null(f.Native.Target);
        Assert.Contains("waiting for the movement provider", f.Native.Execution.LastStatus);
        f.Actors = [];
        f.Local = f.Local with { StatusIds = [] };
        Assert.False(f.Tick());
    }

    [Fact]
    public void BellyEntryRequiresMiniReadbackBeforeAnyPuddleMovement()
    {
        using var f = new Fixture("B");
        f.Local = f.Local with { Position = new(-20, 100, -198) };
        var boss = Npc(10, 0xDF6, new(0, 100, -198));
        var juice = Npc(11, 0xDF9, new(-10, 100, -198));
        var puddle = Event(12, 0x1E968C, new(5, 100, -198)) with { EventState = 0 };
        f.Actors = [boss, juice, puddle];
        Assert.True(f.Tick());
        Assert.DoesNotContain(f.Native.Commands, command => command.StartsWith("/vnav moveto"));
        f.Actors = [boss, juice with { IsCasting = true, CastActionId = 3249 }, puddle];
        Assert.True(f.Tick());
        Assert.Contains("/vnav moveto -10.00 100.00 -198.00", f.Native.Commands);
        Assert.DoesNotContain(f.Native.Commands, command => command == "/vnav moveto 5.00 100.00 -198.00");
        f.Local = f.Local with { StatusIds = [438] };
        Assert.True(f.Tick());
        Assert.Contains("/vnav moveto 5.00 100.00 -198.00", f.Native.Commands);
    }

    [Theory]
    [InlineData("A", 11ul)]
    [InlineData("C", 10ul)]
    public void OutsideAssignmentsCannotSelectTheGastricJuiceOrStomachTargets(string alliance, ulong target)
    {
        using var f = new Fixture(alliance);
        f.Local = f.Local with { Position = new(0, 100, -198) };
        f.Actors = [Npc(10, 0xDF6, new(0, 100, -198)), Npc(11, 0xDF8, new(1, 100, -198)),
            Npc(12, 0xDF9, new(20, 100, -198)) with { IsCasting = true, CastActionId = 3249 },
            Npc(13, 0xDFA, new(1, -200, -198))];
        Assert.True(f.Tick());
        Assert.Equal(target, f.Native.Target?.GameObjectId);
        Assert.DoesNotContain(f.Native.Commands, command => command.StartsWith("/vnav moveto"));
    }

    [Fact]
    public void NormalUnsyncedBurstDoesNotInventABellyPhaseAndChainsRemainExplicitlyPending()
    {
        using var f = new Fixture("C", "UNSYNC");
        f.Local = f.Local with { Position = new(0, 100, -198) };
        var boss = Npc(10, 0xDF6, new(0, 100, -198));
        f.Actors = [boss];
        Assert.False(f.Tick());
        Assert.Empty(f.Native.Commands);
        f.Actors = [boss with { IsCasting = true, CastActionId = 3507 }, Event(11, 0x1E9735, new(2, 100, -198))];
        Assert.True(f.Tick());
        Assert.Equal("Crystal Tower: automatic chain handling is unavailable.", f.Native.Execution.LastStatus);
        Assert.DoesNotContain(f.Native.Commands, command => command.Contains("interact", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OutsideAlliancesLeaveTheObservedMiniCircleWithoutInventingAnArenaWaypoint()
    {
        using var f = new Fixture("A");
        f.Local = f.Local with { Position = new(5, 100, -198), HitboxRadius = 1 };
        f.Actors = [Npc(10, 0xDF6, new(0, 100, -208)), Npc(11, 0xDF9, new(0, 100, -198))
            with { IsCasting = true, CastActionId = 3249 }];
        Assert.True(f.Tick());
        Assert.Contains("/vnav moveto 10.00 100.00 -198.00", f.Native.Commands);
        Assert.Null(f.Native.Target);
    }

    [Fact]
    public void UnshrunkBellyPlayerCannotNavigateThroughAnObservedLethalPuddle()
    {
        using var f = new Fixture("B");
        f.Local = f.Local with { Position = new(-20, 100, -198) };
        f.Actors = [Npc(10, 0xDF6, new(0, 100, -208)), Npc(11, 0xDF9, new(20, 100, -198))
            with { IsCasting = true, CastActionId = 3249 }, Event(12, 0x1E968C, new(0, 100, -198))];
        Assert.True(f.Tick());
        Assert.DoesNotContain(f.Native.Commands, command => command.StartsWith("/vnav moveto"));
        Assert.Contains("safe approach", f.Native.Execution.LastStatus);
    }

    [Fact]
    public void InsideStomachTargetsOnlyVerifiedLivingStomachActors()
    {
        using var f = new Fixture("B");
        f.Local = f.Local with { Position = new(0, -200, -198) };
        f.Actors = [Npc(10, 0xDF6, new(0, 129, -198)), Npc(11, 0xDF8, new(1, 129, -198)), Npc(12, 0xDFA, new(1, -200, -198))];
        Assert.True(f.Tick());
        Assert.Equal(12ul, f.Native.Target?.GameObjectId);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("dead")]
    [InlineData("duplicate")]
    public void StomachCombatDoesNotRequireAnEligibleOutsideCerberusObservation(string outside)
    {
        using var f = new Fixture("B");
        f.Local = f.Local with { Position = new(0, -200, -198) };
        var boss = Npc(10, 0xDF6, new(0, 129, -198));
        CrystalTowerActorObservation[] bosses = outside switch
        {
            "missing" => [],
            "dead" => [boss with { Dead = true }],
            _ => [boss, boss with { GameObjectId = 20, EntityId = 20 }],
        };
        f.Actors = [.. bosses, Npc(11, 0xDF8, new(1, 129, -198)),
            Npc(12, 0xDFA, new(2, -200, -198)), Npc(13, 0xDF7, new(1, -200, -198)),
            Npc(14, 0xDFA, new(0, -200, -198)) with { Dead = true }];

        Assert.True(f.Tick());
        Assert.Equal(12ul, f.Native.Target?.GameObjectId);
    }

    [Fact]
    public void MissingOutsideBossAndStomachTargetsRetainsTheExitTransitionHold()
    {
        using var f = new Fixture("B");
        f.Local = f.Local with { Position = new(0, -200, -198) };
        f.Actors = [Npc(11, 0xDF8, new(1, 129, -198)), Npc(12, 0xDFA, new(1, 129, -198)),
            Npc(13, 0xDF7, new(1, -200, -198)) with { Targetable = false }];

        Assert.True(f.Tick());
        Assert.Contains("stomach exit transition", f.Native.Execution.LastStatus);
        Assert.Null(f.Native.Target);
        Assert.DoesNotContain(f.Native.Commands, command => command.StartsWith("/vnav moveto"));
    }

    [Theory]
    [InlineData("A", 0x1E9752u, -310f, -400f)]
    [InlineData("B", 0x1E9750u, -300f, -410f)]
    [InlineData("C", 0x1E9751u, -290f, -400f)]
    public void CloudRegionsUseNativeAllianceObjectsAndRetainTheBoundaryUntilClear(string alliance, uint dataId, float x, float z)
    {
        using var f = new Fixture(alliance);
        var hex = Event(10, dataId, new(x, 280, z));
        var cloud = Npc(11, 0xE03, new(x + 1, 280, z));
        f.Local = f.Local with { Position = new(-300, 280, -380) };
        f.Actors = [hex, cloud];
        f.Animations = [new(10, f.Now, hex.GameObjectId, hex.EntityId, hex.DataId, 0x00040008)];
        Assert.True(f.Tick());
        Assert.Contains(FormattableString.Invariant($"/vnav moveto {x:F2} 280.00 {z:F2}"), f.Native.Commands);
        f.Local = f.Local with { Position = hex.Position };
        Assert.True(f.Tick());
        Assert.Equal(cloud.GameObjectId, f.Native.Target?.GameObjectId);
        f.Actors = [hex, cloud with { Dead = true }];
        Assert.True(f.Tick());
        f.Animations = [new(11, f.Now, hex.GameObjectId, hex.EntityId, hex.DataId, 0x00010040)];
        Assert.False(f.Tick());
    }

    [Fact]
    public void CloudTowerAssignmentsUseTheSameFullAllianceRosterAcrossAllThreeParties()
    {
        foreach (var reverse in new[] { false, true })
        foreach (var member in new[] { (Alliance: "A", Id: 120u, X: -20f, TowerX: -10f),
                     (Alliance: "B", Id: 123u, X: 0f, TowerX: 10f), (Alliance: "C", Id: 124u, X: 20f, TowerX: 30f) })
        {
            using var f = new Fixture(member.Alliance, localEntityId: member.Id);
            f.Local = f.Local with { Position = new(member.X - 300, 280, -400) };
            var roster = CloudRoster();
            var partyStart = member.Alliance == "A" ? 125ul : member.Alliance == "B" ? 132ul : 139ul;
            f.Members = roster.Where(candidate => candidate.Id == member.Id
                || candidate.Id >= partyStart && candidate.Id < partyStart + 7).ToArray();
            f.AllianceRosterComplete = true;
            f.AllianceMembers = reverse ? roster.Reverse().ToArray() : roster;
            f.Actors = [Event(10, 0x1E974E, new(-310, 280, -400)), Event(11, 0x1E974E, new(-290, 280, -400)),
                Event(12, 0x1E974E, new(-270, 280, -400))];
            if (reverse) f.Actors = f.Actors.Reverse().ToArray();
            Assert.Equal(8, f.Members.Length);
            Assert.Equal(24, f.AllianceMembers!.Length);
            Assert.True(f.Tick());
            Assert.Contains(FormattableString.Invariant($"/vnav moveto {member.TowerX - 300:F2} 280.00 -400.00"), f.Native.Commands);
        }
    }

    [Theory]
    [InlineData("incomplete")]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("missing-local")]
    [InlineData("duplicate")]
    [InlineData("unknown-alive")]
    [InlineData("missing-position")]
    public void CloudTowersNeverTreatTheOwnPartyAsACompleteAlliance(string invalid)
    {
        using var f = new Fixture();
        f.Local = f.Local with { Position = new(-300, 280, -400) };
        f.Members = CloudRoster().Where(member => member.Id == 123 || member.Id is >= 132 and <= 138).ToArray();
        f.AllianceRosterComplete = invalid != "incomplete";
        var roster = CloudRoster();
        f.AllianceMembers = invalid switch
        {
            "missing" => null,
            "empty" => [],
            "missing-local" => roster.Where(member => member.Id != 123).ToArray(),
            "duplicate" => roster.Select(member => member.Id == 124 ? member with { Id = 123 } : member).ToArray(),
            "unknown-alive" => roster.Select(member => member.Id == 120 ? member with { Alive = null } : member).ToArray(),
            "missing-position" => roster.Select(member => member.Id == 120 ? member with { Position = null } : member).ToArray(),
            _ => roster,
        };
        f.Actors = [Event(10, 0x1E974E, new(-310, 280, -400)), Event(11, 0x1E974E, new(-290, 280, -400))];

        Assert.True(f.Tick());
        Assert.Contains("verified tower assignments", f.Native.Execution.LastStatus);
        Assert.DoesNotContain(f.Native.Commands, command => command.StartsWith("/vnav moveto"));
    }

    [Fact]
    public void CloudTowerAssignmentRechecksTheGlobalRosterWhenAnotherAssignedMemberDies()
    {
        using var f = new Fixture();
        f.Local = f.Local with { Position = new(-300, 280, -400) };
        f.AllianceRosterComplete = true;
        f.AllianceMembers = CloudRoster();
        f.Actors = [Event(10, 0x1E974E, new(-310, 280, -400)), Event(11, 0x1E974E, new(-290, 280, -400))];
        Assert.True(f.Tick());
        Assert.Contains("/vnav moveto -290.00 280.00 -400.00", f.Native.Commands);

        f.AllianceMembers = f.AllianceMembers!.Select(member => member.Id == 120 ? member with { Alive = false } : member).ToArray();
        Assert.True(f.Tick());
        Assert.Equal("/vnav moveto -310.00 280.00 -400.00", f.Native.Commands.Last(command => command.StartsWith("/vnav moveto")));
    }

    [Fact]
    public void CancellationCannotReplayAPreviousOwnersCleansingPlatform()
    {
        using var f = new Fixture();
        f.Local = f.Local with { StatusIds = [210] };
        var pad = Event(10, 0x1E9711, new(12, 100, 20));
        f.Actors = [pad];
        f.Rows = [f.State(pad, 4, 10)];
        Assert.True(f.Tick());
        f.Native.Execution.CancelCrystalTower(f.Context);
        var moves = f.Native.Commands.Count(command => command.StartsWith("/vnav moveto"));
        f.Now = f.Now.AddSeconds(1);
        Assert.True(f.Tick());
        Assert.Equal(moves, f.Native.Commands.Count(command => command.StartsWith("/vnav moveto")));
    }

    private static CrystalTowerActorObservation Event(ulong id, uint dataId, Vector3 position)
        => new(id, 0xE0000000, dataId, ObjectKind.EventObj, position, null, null, null, null, null, 0, null);
    private static CrystalTowerActorObservation Npc(ulong id, uint dataId, Vector3 position)
        => new(id, (uint)id, dataId, ObjectKind.BattleNpc, position, false, true, false, 0, [], 0, null);
    private static CrystalTowerActorObservation Player(uint id, Vector3 position)
        => new(id + 1000ul, id, 0, ObjectKind.Pc, position, false, true, false, 0, [], null, 24);
    private static Vector3 AtomosPoint(float radius, float degrees)
        => new(-31 + radius * MathF.Sin(degrees * MathF.PI / 180), 100, 49 + radius * MathF.Cos(degrees * MathF.PI / 180));
    private static Vector3 LastNavigation(Fixture fixture)
    {
        var parts = fixture.Native.Commands.Last(command => command.StartsWith("/vnav moveto")).Split(' ');
        return new(float.Parse(parts[2], CultureInfo.InvariantCulture), float.Parse(parts[3], CultureInfo.InvariantCulture),
            float.Parse(parts[4], CultureInfo.InvariantCulture));
    }
    private static void AssertAtomosConeEscape(Fixture fixture, float halfAngleDegrees)
    {
        var destination = LastNavigation(fixture);
        var offset = new Vector2(destination.X + 31, destination.Z - 49);
        var halfAngle = halfAngleDegrees * MathF.PI / 180;
        Assert.Equal(100f, destination.Y);
        Assert.InRange(offset.Length(), 4.5f, 17.5f);
        Assert.True(MathF.Abs(offset.X) * MathF.Cos(halfAngle) - offset.Y * MathF.Sin(halfAngle) > 0.5f);
        Assert.Null(fixture.Native.Target);
    }
    private static CrystalTowerActorObservation[] Atomos()
        => new[] { Npc(10, 0xDA4, new(-31, 100, 49)), Npc(11, 0xDA4, new(0, 100, -5)), Npc(12, 0xDA4, new(31, 100, 49)) }
            .Select(actor => actor with { HitboxRadius = 4, TargetObjectId = 1123 }).ToArray();
    private static CrystalTowerActorObservation[] LoadedFutureRoom(string room)
        => room switch
        {
            "Atomos" => Atomos(),
            "Cerberus" => [Npc(20, 0xDF6, new(0, 129, -198)), Npc(21, 0xDF8, new(1, 129, -198)),
                Npc(22, 0xDF9, new(10, 129, -198)) with { IsCasting = true, CastActionId = 3249 }],
            _ => [Event(30, 0x1E9752, new(-310, 280, -400)), Npc(31, 0xE03, new(-309, 280, -400))],
        };
    private static CrystalTowerMember[] CloudRoster()
        => [new(120, CrystalTowerRole.Healer, true, new(-320, -400)), new(123, CrystalTowerRole.Ranged, true, new(-300, -400)),
            new(124, CrystalTowerRole.Ranged, true, new(-280, -400)),
            .. Enumerable.Range(125, 21).Select(id => new CrystalTowerMember((ulong)id, CrystalTowerRole.Melee, true, new(1000, 0)))];

    private sealed class Fixture : IDisposable
    {
        internal Fixture(string alliance = "A", string mode = "SYNC", uint localEntityId = 123)
        {
            Local = Local with { EntityId = localEntityId, GameObjectId = localEntityId + 1000ul };
            Context = TestDutyContextFactory.Create(DutyCategory.AllianceRaid, territoryId: 151, cfcId: 111, alliance: alliance);
            Native = new(new(0, 100, 0), initialRules: new ObjectPriorityRuleManifest
            {
                Rules = [new() { TerritoryTypeId = 151, ContentFinderConditionId = 111, ObjectKind = "BattleNpc",
                    BaseId = 0xDF6, Classification = "Required", CrystalTowerStrategy = mode }],
            });
            Native.Execution.ReadUtcNow = () => Now;
            Native.Execution.CrystalTowerMovementCharacterIdOverride = () => 1;
            Native.Execution.CrystalTowerMovementLocalEntityIdOverride = () => Local.EntityId;
            Native.Execution.ReadCrystalTowerMovementOverride = () => new(false, null, "fixture: no provider");
            Native.Execution.ReadInteractionVbmOverride = () => new(false, null, "fixture: no provider");
            Native.Execution.ReadCrystalTowerEvents = () => Rows;
            Native.Execution.CrystalTowerObservationOverride = _ => new(true, "fixture", Local.EntityId, Local, true,
                Members.Length == 0 ? [new(Local.EntityId, CrystalTowerRole.Ranged, true, new(Local.Position.X, Local.Position.Z))] : Members,
                Actors, Animations, Math.Max(Rows.Select(row => row.Sequence).DefaultIfEmpty(10ul).Max(),
                    Animations.Select(animation => animation.Sequence).DefaultIfEmpty(10ul).Max()),
                AllianceRosterComplete: AllianceRosterComplete, AllianceMembers: AllianceMembers);
        }
        internal HyperFixatedTargetExecutionTests.Fixture Native { get; }
        internal DutyContextSnapshot Context { get; }
        internal DateTime Now = new(2026, 10, 22, 1, 0, 0, DateTimeKind.Utc);
        internal CrystalTowerActorObservation Local = new(999, 123, 0, ObjectKind.Pc, new(0, 100, 0), false, true, false, 0, [], null, 25,
            HitboxRadius: 0.5f);
        internal CrystalTowerActorObservation[] Actors = [];
        internal CrystalTowerMember[] Members = [];
        internal bool AllianceRosterComplete;
        internal CrystalTowerMember[]? AllianceMembers;
        internal CrystalTowerEAnimObservation[] Animations = [];
        internal ServerEventRow[] Rows = [];
        internal ServerEventRow State(CrystalTowerActorObservation actor, uint state, ulong sequence)
            => new(sequence, Now, ServerEventKind.EObjState, "ESTA", 151, Context.MapId,
                (uint)actor.GameObjectId, 0, 409, state, 0, 0, 0, 0, 0, 0, 0, 0, "fixture", "EventObj",
                actor.GameObjectId, actor.EntityId, actor.DataId, 0, 0, 0, 0, null, actor.Position, null, "", "", "", false);
        internal bool Tick()
        {
            var objectTable = (HyperFixatedTargetExecutionTests.ObjectTableProxy)typeof(ExecutionService)
                .GetField("objectTable", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Native.Execution)!;
            ((HyperFixatedTargetExecutionTests.GameObjectProxy)(object)objectTable.LocalPlayer!).Position = Local.Position;
            objectTable.Objects.Clear();
            var monsters = new List<ObservedMonster>();
            foreach (var actor in Actors.Where(actor => actor.ObjectKind == ObjectKind.BattleNpc))
            {
                var gameObject = DispatchProxy.Create<IGameObject, CrystalTowerExecutionTests.ActorProxy>();
                ((CrystalTowerExecutionTests.ActorProxy)(object)gameObject).Actor = actor;
                var monster = new ObservedMonster { Key = $"fixture:{actor.GameObjectId}", GameObjectId = actor.GameObjectId,
                    DataId = actor.DataId, MapId = Context.MapId, Name = "fixture", Position = actor.Position, LastSeenUtc = Now };
                objectTable.Objects.Add(new(gameObject, monster));
                monsters.Add(monster);
            }
            return (bool)typeof(ExecutionService).GetMethod("TryHandleCrystalTower", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(Native.Execution, [Context, new ObservationSnapshot { LiveMonsters = monsters,
                    LiveFollowTargets = [], MonsterGhosts = [], LiveInteractables = [], InteractableGhosts = [] }])!;
        }
        public void Dispose() => Native.Dispose();
    }
}
