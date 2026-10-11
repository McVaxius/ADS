using System.Numerics;
using ADS.Models;

namespace ADS.Tests;

public sealed class MapFlagMonitorPolicyTests
{
    private static readonly DateTime Start = new(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ExistingFlagIsDetectedOnFirstObservation()
    {
        var policy = new MapFlagMonitorPolicy();

        Assert.Equal(MapFlagMonitorDecision.QueryDestination, policy.Observe(Present(Flag()), Start));
    }

    [Fact]
    public void UnchangedResolvedFlagDoesNotRepeatQuery()
    {
        var policy = new MapFlagMonitorPolicy();
        policy.Observe(Present(Flag()), Start);
        policy.RecordQueryResult(MapFlagDestinationQueryResult.Resolved, Start);

        Assert.Equal(MapFlagMonitorDecision.None, policy.Observe(Present(Flag()), Start.AddHours(1)));
    }

    [Fact]
    public void ChangedFlagTriggersNewResolution()
    {
        var policy = new MapFlagMonitorPolicy();
        policy.Observe(Present(Flag()), Start);
        policy.RecordQueryResult(MapFlagDestinationQueryResult.Resolved, Start);

        Assert.Equal(
            MapFlagMonitorDecision.QueryDestination,
            policy.Observe(Present(Flag(worldX: 101f)), Start.AddMilliseconds(250)));
    }

    [Fact]
    public void ClearedFlagUpdatesStatusOnce()
    {
        var policy = new MapFlagMonitorPolicy();
        policy.Observe(Present(Flag()), Start);
        policy.RecordQueryResult(MapFlagDestinationQueryResult.Resolved, Start);

        Assert.Equal(MapFlagMonitorDecision.ReportCleared, policy.Observe(MapFlagObservation.Cleared(), Start.AddMilliseconds(250)));
        Assert.Equal(MapFlagMonitorDecision.None, policy.Observe(MapFlagObservation.Cleared(), Start.AddMilliseconds(500)));
    }

    [Fact]
    public void TransitionPreservesPriorState()
        => AssertUnavailablePreservesPriorState(MapFlagUnavailableReason.Transition);

    [Fact]
    public void AgentMapFailurePreservesPriorState()
        => AssertUnavailablePreservesPriorState(MapFlagUnavailableReason.AgentMapUnavailable);

    [Fact]
    public void UnresolvedFloorRetriesAfterOneSecond()
        => AssertRetriesAfterFailure(MapFlagDestinationQueryResult.Unresolved);

    [Fact]
    public void UnavailableIpcRetriesAfterOneSecond()
        => AssertRetriesAfterFailure(MapFlagDestinationQueryResult.Unavailable);

    [Fact]
    public void SuccessfulRetryStopsFurtherQueries()
    {
        var policy = new MapFlagMonitorPolicy();
        policy.Observe(Present(Flag()), Start);
        policy.RecordQueryResult(MapFlagDestinationQueryResult.Unresolved, Start);
        policy.Observe(Present(Flag()), Start.AddSeconds(1));

        policy.RecordQueryResult(MapFlagDestinationQueryResult.Resolved, Start.AddSeconds(1));

        Assert.Equal(MapFlagMonitorDecision.None, policy.Observe(Present(Flag()), Start.AddHours(1)));
    }

    [Fact]
    public void AdsCreatedFlagBaselineSuppressesDuplicateMonitorStatus()
    {
        var policy = new MapFlagMonitorPolicy();
        policy.RecordBaseline(Flag(), MapFlagDestinationQueryResult.Resolved, Start);

        Assert.Equal(MapFlagMonitorDecision.None, policy.Observe(Present(Flag()), Start.AddMilliseconds(250)));
    }

    [Fact]
    public void DetectedStatusIncludesVisibleMapCoordinatesAndResolvedWorldCoordinates()
    {
        var status = MapFlagMonitorPolicy.BuildDetectedStatus(
            Flag(),
            "Resolved /vnav moveflag destination at 100, 12.5, -30.");

        Assert.Equal(
            "Detected map flag on map 20 at map X/Y 12.3, 45.6. Resolved /vnav moveflag destination at 100, 12.5, -30.",
            status);
    }

    [Fact]
    public void DetectedStatusReportsUnavailableMapConversion()
    {
        var snapshot = Flag() with
        {
            MapCoordinates = null,
            MapCoordinateUnavailableReason = "map row 20 is unavailable",
        };

        var status = MapFlagMonitorPolicy.BuildDetectedStatus(
            snapshot,
            "/vnav moveflag destination unavailable: navmesh has no resolved floor/query.");

        Assert.Equal(
            "Detected map flag on map 20; map X/Y unavailable: map row 20 is unavailable. /vnav moveflag destination unavailable: navmesh has no resolved floor/query.",
            status);
    }

    [Fact]
    public void ResolvedFlagDistanceUsesFullVector3Distance()
    {
        var status = MapFlagMonitorPolicy.BuildDetectedStatus(
            Flag(),
            "Resolved /vnav moveflag destination at 1, 2, 2.",
            Vector3.Zero,
            new Vector3(1, 2, 2));

        Assert.EndsWith("Current full-3D distance: 3.00y.", status);
    }

    [Fact]
    public void PlayerMovementUpdatesDistanceWithoutNewDestinationQuery()
    {
        var policy = new MapFlagMonitorPolicy();
        policy.Observe(Present(Flag()), Start);
        policy.RecordQueryResult(
            MapFlagDestinationQueryResult.Resolved,
            new Vector3(10, 0, 0),
            "Resolved /vnav moveflag destination at 10, 0, 0.",
            Start);

        Assert.Contains("Current full-3D distance: 10.00y.", policy.BuildCurrentStatus(Vector3.Zero));
        Assert.Contains("Current full-3D distance: 5.00y.", policy.BuildCurrentStatus(new Vector3(5, 0, 0)));
        Assert.Equal(MapFlagMonitorDecision.None, policy.Observe(Present(Flag()), Start.AddHours(1)));
    }

    [Fact]
    public void UnresolvedNoPlayerAndNonFiniteDestinationOmitDistance()
    {
        var unresolved = MapFlagMonitorPolicy.BuildDetectedStatus(
            Flag(),
            "/vnav moveflag destination unavailable: navmesh has no resolved floor/query.",
            Vector3.Zero,
            null);
        var noPlayer = MapFlagMonitorPolicy.BuildDetectedStatus(
            Flag(),
            "Resolved /vnav moveflag destination at 10, 0, 0.",
            null,
            new Vector3(10, 0, 0));
        var nonFinite = MapFlagMonitorPolicy.BuildDetectedStatus(
            Flag(),
            "/vnav moveflag destination unavailable: navmesh returned non-finite coordinates.",
            Vector3.Zero,
            new Vector3(float.NaN, 0, 0));

        Assert.DoesNotContain("Current full-3D distance", unresolved);
        Assert.DoesNotContain("Current full-3D distance", noPlayer);
        Assert.DoesNotContain("Current full-3D distance", nonFinite);
    }

    [Fact]
    public void FlagClearResetsResolvedDistance()
    {
        var policy = new MapFlagMonitorPolicy();
        policy.Observe(Present(Flag()), Start);
        policy.RecordQueryResult(
            MapFlagDestinationQueryResult.Resolved,
            new Vector3(10, 0, 0),
            "Resolved /vnav moveflag destination at 10, 0, 0.",
            Start);

        policy.Observe(MapFlagObservation.Cleared(), Start.AddMilliseconds(250));

        Assert.Equal("Map flag cleared.", policy.BuildCurrentStatus(Vector3.Zero));
    }

    [Fact]
    public void CurrentCoordinatesKeepHorizontalCopyAvailableWithoutInventingHeight()
    {
        var policy = new MapFlagMonitorPolicy();
        var flag = Flag();
        Assert.True(policy.TryGetCoordinates(Present(flag), 10, out var horizontal, out var destination));
        Assert.Equal(new Vector2(100, -30), horizontal);
        Assert.Null(destination);

        policy.Observe(Present(flag), Start);
        policy.RecordQueryResult(MapFlagDestinationQueryResult.Resolved, new Vector3(100, 12.5f, -30), "resolved", Start);
        Assert.True(policy.TryGetCoordinates(Present(flag), 10, out horizontal, out destination));
        Assert.Equal(new Vector3(100, 12.5f, -30), destination);

        Assert.True(policy.TryGetCoordinates(Present(Flag(101)), 10, out horizontal, out destination));
        Assert.Equal(new Vector2(101, -30), horizontal);
        Assert.Null(destination);
        Assert.True(policy.TryGetCoordinates(Present(flag with { MapId = 21 }), 10, out _, out destination));
        Assert.Null(destination);
    }

    [Fact]
    public void CopyCoordinatesRejectUnavailableClearedForeignAndInvalidFlags()
    {
        var policy = new MapFlagMonitorPolicy();
        policy.RecordBaseline(Flag(), MapFlagDestinationQueryResult.Resolved, new Vector3(100, 12.5f, -30), "resolved", Start);
        foreach (var observation in new[]
        {
            MapFlagObservation.Cleared(),
            MapFlagObservation.Unavailable(MapFlagUnavailableReason.Transition),
            MapFlagObservation.Unavailable(MapFlagUnavailableReason.AgentMapUnavailable),
            Present(Flag() with { TerritoryId = 11 }),
            Present(Flag(float.NaN)),
            Present(Flag() with { WorldZ = float.PositiveInfinity }),
        })
        {
            policy.RecordBaseline(Flag(), MapFlagDestinationQueryResult.Resolved, new Vector3(100, 12.5f, -30), "resolved", Start);
            Assert.False(policy.TryGetCoordinates(observation, 10, out _, out var destination));
            Assert.Null(destination);
            Assert.True(policy.TryGetCoordinates(Present(Flag()), 10, out _, out destination));
            Assert.Null(destination);
        }
        Assert.False(policy.TryGetCoordinates(Present(Flag()), 0, out _, out _));
        policy.RecordQueryResult(MapFlagDestinationQueryResult.Resolved, new Vector3(100, float.NaN, -30), "invalid", Start);
        Assert.True(policy.TryGetCoordinates(Present(Flag()), 10, out _, out var invalidDestination));
        Assert.Null(invalidDestination);
    }

    private static MapFlagObservation Present(MapFlagSnapshot snapshot)
        => MapFlagObservation.Present(snapshot);

    private static void AssertRetriesAfterFailure(MapFlagDestinationQueryResult result)
    {
        var policy = new MapFlagMonitorPolicy();
        policy.Observe(Present(Flag()), Start);
        policy.RecordQueryResult(result, Start);

        Assert.Equal(MapFlagMonitorDecision.None, policy.Observe(Present(Flag()), Start.AddMilliseconds(999)));
        Assert.Equal(MapFlagMonitorDecision.QueryDestination, policy.Observe(Present(Flag()), Start.AddSeconds(1)));
    }

    private static void AssertUnavailablePreservesPriorState(MapFlagUnavailableReason reason)
    {
        var policy = new MapFlagMonitorPolicy();
        policy.Observe(Present(Flag()), Start);
        policy.RecordQueryResult(MapFlagDestinationQueryResult.Resolved, Start);

        Assert.Equal(MapFlagMonitorDecision.None, policy.Observe(MapFlagObservation.Unavailable(reason), Start.AddMilliseconds(250)));
        Assert.Equal(MapFlagMonitorDecision.None, policy.Observe(Present(Flag()), Start.AddMilliseconds(500)));
    }

    private static MapFlagSnapshot Flag(float worldX = 100f)
        => new(
            TerritoryId: 10,
            MapId: 20,
            WorldX: worldX,
            WorldZ: -30f,
            MapCoordinates: new Vector2(12.34f, 45.64f),
            MapCoordinateUnavailableReason: string.Empty);
}
