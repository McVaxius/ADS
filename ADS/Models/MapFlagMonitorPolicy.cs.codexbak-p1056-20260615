using System.Globalization;
using System.Numerics;

namespace ADS.Models;

internal readonly record struct MapFlagSnapshot(
    uint TerritoryId,
    uint MapId,
    float WorldX,
    float WorldZ,
    Vector2? MapCoordinates,
    string MapCoordinateUnavailableReason)
{
    public bool IsSameFlag(MapFlagSnapshot other)
        => TerritoryId == other.TerritoryId
           && MapId == other.MapId
           && WorldX.Equals(other.WorldX)
           && WorldZ.Equals(other.WorldZ);
}

internal enum MapFlagObservationKind
{
    Unavailable,
    Cleared,
    Present,
}

internal enum MapFlagUnavailableReason
{
    None,
    Transition,
    AgentMapUnavailable,
    ReadFailure,
}

internal readonly record struct MapFlagObservation(
    MapFlagObservationKind Kind,
    MapFlagSnapshot? Snapshot,
    MapFlagUnavailableReason UnavailableReason)
{
    public static MapFlagObservation Unavailable(MapFlagUnavailableReason reason)
        => new(MapFlagObservationKind.Unavailable, null, reason);

    public static MapFlagObservation Cleared()
        => new(MapFlagObservationKind.Cleared, null, MapFlagUnavailableReason.None);

    public static MapFlagObservation Present(MapFlagSnapshot snapshot)
        => new(MapFlagObservationKind.Present, snapshot, MapFlagUnavailableReason.None);
}

internal enum MapFlagMonitorDecision
{
    None,
    QueryDestination,
    ReportCleared,
}

internal enum MapFlagDestinationQueryResult
{
    Resolved,
    Unresolved,
    Unavailable,
}

internal sealed class MapFlagMonitorPolicy
{
    internal static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(1);

    private bool hasObservation;
    private MapFlagSnapshot? observedFlag;
    private bool destinationResolved;
    private DateTime nextRetryUtc = DateTime.MinValue;

    public MapFlagMonitorDecision Observe(MapFlagObservation observation, DateTime utcNow)
    {
        if (observation.Kind == MapFlagObservationKind.Unavailable)
            return MapFlagMonitorDecision.None;

        if (observation.Kind == MapFlagObservationKind.Cleared)
        {
            var reportCleared = hasObservation && observedFlag.HasValue;
            hasObservation = true;
            observedFlag = null;
            destinationResolved = false;
            nextRetryUtc = DateTime.MinValue;
            return reportCleared
                ? MapFlagMonitorDecision.ReportCleared
                : MapFlagMonitorDecision.None;
        }

        var snapshot = observation.Snapshot!.Value;
        if (!hasObservation
            || !observedFlag.HasValue
            || !observedFlag.Value.IsSameFlag(snapshot))
        {
            hasObservation = true;
            observedFlag = snapshot;
            destinationResolved = false;
            nextRetryUtc = DateTime.MinValue;
            return MapFlagMonitorDecision.QueryDestination;
        }

        observedFlag = snapshot;
        return !destinationResolved && utcNow >= nextRetryUtc
            ? MapFlagMonitorDecision.QueryDestination
            : MapFlagMonitorDecision.None;
    }

    public void RecordQueryResult(MapFlagDestinationQueryResult result, DateTime utcNow)
    {
        destinationResolved = result == MapFlagDestinationQueryResult.Resolved;
        nextRetryUtc = destinationResolved ? DateTime.MaxValue : utcNow + RetryInterval;
    }

    public void RecordBaseline(MapFlagSnapshot snapshot, MapFlagDestinationQueryResult result, DateTime utcNow)
    {
        hasObservation = true;
        observedFlag = snapshot;
        RecordQueryResult(result, utcNow);
    }

    public static string BuildDetectedStatus(MapFlagSnapshot snapshot, string destinationStatus)
    {
        if (snapshot.MapCoordinates is { } mapCoordinates)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"Detected map flag on map {snapshot.MapId} at map X/Y {mapCoordinates.X:0.0}, {mapCoordinates.Y:0.0}. {destinationStatus}");
        }

        return $"Detected map flag on map {snapshot.MapId}; map X/Y unavailable: {snapshot.MapCoordinateUnavailableReason}. {destinationStatus}";
    }
}
