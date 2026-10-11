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
    private Vector3? resolvedDestination;
    private string destinationStatus = "Flag destination resolution pending.";

    public MapFlagMonitorDecision Observe(MapFlagObservation observation, DateTime utcNow)
    {
        if (observation.Kind == MapFlagObservationKind.Unavailable)
            return MapFlagMonitorDecision.None;

        if (observation.Kind == MapFlagObservationKind.Cleared)
        {
            var reportCleared = hasObservation && observedFlag.HasValue;
            hasObservation = true;
            observedFlag = null;
            ResetDestination();
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
            ResetDestination();
            return MapFlagMonitorDecision.QueryDestination;
        }

        observedFlag = snapshot;
        return !destinationResolved && utcNow >= nextRetryUtc
            ? MapFlagMonitorDecision.QueryDestination
            : MapFlagMonitorDecision.None;
    }

    public void RecordQueryResult(MapFlagDestinationQueryResult result, DateTime utcNow)
        => RecordQueryResult(result, null, string.Empty, utcNow);

    public void RecordQueryResult(
        MapFlagDestinationQueryResult result,
        Vector3? destination,
        string status,
        DateTime utcNow)
    {
        destinationResolved = result == MapFlagDestinationQueryResult.Resolved;
        resolvedDestination = destinationResolved && IsFinite(destination)
            ? destination
            : null;
        destinationStatus = string.IsNullOrWhiteSpace(status)
            ? destinationStatus
            : status;
        nextRetryUtc = destinationResolved ? DateTime.MaxValue : utcNow + RetryInterval;
    }

    public void RecordBaseline(MapFlagSnapshot snapshot, MapFlagDestinationQueryResult result, DateTime utcNow)
        => RecordBaseline(snapshot, result, null, string.Empty, utcNow);

    public void RecordBaseline(
        MapFlagSnapshot snapshot,
        MapFlagDestinationQueryResult result,
        Vector3? destination,
        string status,
        DateTime utcNow)
    {
        hasObservation = true;
        observedFlag = snapshot;
        RecordQueryResult(result, destination, status, utcNow);
    }

    public static string BuildDetectedStatus(MapFlagSnapshot snapshot, string destinationStatus)
        => BuildDetectedStatus(snapshot, destinationStatus, null, null);

    public string BuildCurrentStatus(Vector3? playerPosition)
    {
        if (!hasObservation)
            return "Map flag not inspected.";

        if (!observedFlag.HasValue)
            return "Map flag cleared.";

        return BuildDetectedStatus(observedFlag.Value, destinationStatus, playerPosition, resolvedDestination);
    }

    public bool TryGetCoordinates(MapFlagObservation current, uint territoryId, out Vector2 worldXZ, out Vector3? worldXYZ)
    {
        worldXZ = default;
        worldXYZ = null;
        if (current.Kind != MapFlagObservationKind.Present || current.Snapshot is not { } snapshot
            || territoryId == 0 || snapshot.TerritoryId != territoryId
            || !float.IsFinite(snapshot.WorldX) || !float.IsFinite(snapshot.WorldZ))
        {
            ResetDestination();
            return false;
        }

        worldXZ = new Vector2(snapshot.WorldX, snapshot.WorldZ);
        if (observedFlag is { } observed && observed.IsSameFlag(snapshot) && IsFinite(resolvedDestination))
            worldXYZ = resolvedDestination;
        else if (observedFlag is not { } previous || !previous.IsSameFlag(snapshot))
            ResetDestination();
        return true;
    }

    public static string BuildDetectedStatus(
        MapFlagSnapshot snapshot,
        string destinationStatus,
        Vector3? playerPosition,
        Vector3? resolvedDestination)
    {
        var distance = GetResolvedDistance(playerPosition, resolvedDestination);
        var distanceStatus = distance.HasValue
            ? string.Create(CultureInfo.InvariantCulture, $" Current full-3D distance: {distance.Value:0.00}y.")
            : string.Empty;
        if (snapshot.MapCoordinates is { } mapCoordinates)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"Detected map flag on map {snapshot.MapId} at map X/Y {mapCoordinates.X:0.0}, {mapCoordinates.Y:0.0}. {destinationStatus}{distanceStatus}");
        }

        return $"Detected map flag on map {snapshot.MapId}; map X/Y unavailable: {snapshot.MapCoordinateUnavailableReason}. {destinationStatus}{distanceStatus}";
    }

    public static float? GetResolvedDistance(Vector3? playerPosition, Vector3? resolvedDestination)
    {
        if (!IsFinite(playerPosition) || !IsFinite(resolvedDestination))
            return null;

        return Vector3.Distance(playerPosition!.Value, resolvedDestination!.Value);
    }

    internal static bool IsFinite(Vector3? value)
        => value.HasValue
           && float.IsFinite(value.Value.X)
           && float.IsFinite(value.Value.Y)
           && float.IsFinite(value.Value.Z);

    private void ResetDestination()
    {
        destinationResolved = false;
        resolvedDestination = null;
        destinationStatus = "Flag destination resolution pending.";
        nextRetryUtc = DateTime.MinValue;
    }
}
