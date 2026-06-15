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
