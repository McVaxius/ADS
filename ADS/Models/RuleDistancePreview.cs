using System.Globalization;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;

namespace ADS.Models;

internal enum RuleDistancePreviewKind
{
    MapXzDestination,
    XyzDestination,
    CardinalHold,
    OrdinaryObject,
}

internal readonly record struct RuleDistancePreviewObject(
    ObjectKind ObjectKind,
    uint BaseId,
    string Name,
    Vector3 Position,
    uint MapId);

internal sealed record RuleDistancePreview(
    RuleDistancePreviewKind Kind,
    string ExactDistanceLabel,
    float? ExactDistance,
    float? ConfiguredDistance,
    bool? PassesConfiguredDistance,
    int LiveMatchCount,
    float? LiveMatchNearestDistance,
    float? LiveMatchFarthestDistance,
    float? PlanningAidDistance);

internal static class RuleDistancePreviewResolver
{
    public static RuleDistancePreview? Resolve(
        ObjectPriorityRule rule,
        Vector3 playerPosition,
        Func<Vector2, float, Vector3?> resolveMapCoordinates,
        IEnumerable<RuleDistancePreviewObject>? liveObjects = null,
        Func<ObjectPriorityRule, RuleDistancePreviewObject, bool>? matchesOrdinaryObject = null)
    {
        if (!IsFinite(playerPosition))
            return null;

        if (IsCardinalHoldRule(rule))
            return ResolveCardinalHold(rule, playerPosition);

        if (IsMapXzDestinationRule(rule))
            return ResolveMapXzDestination(rule, playerPosition, resolveMapCoordinates);

        if (IsXyzDestinationRule(rule))
            return ResolveXyzDestination(rule, playerPosition);

        return ResolveOrdinaryObject(
            rule,
            playerPosition,
            resolveMapCoordinates,
            liveObjects ?? [],
            matchesOrdinaryObject ?? ((_, _) => false));
    }

    private static RuleDistancePreview? ResolveMapXzDestination(
        ObjectPriorityRule rule,
        Vector3 playerPosition,
        Func<Vector2, float, Vector3?> resolveMapCoordinates)
    {
        if (!TryParseMapCoordinates(rule.MapCoordinates, out var mapCoordinates))
            return null;

        var resolved = resolveMapCoordinates(mapCoordinates, playerPosition.Y);
        if (!resolved.HasValue || !IsFinite(resolved.Value))
            return null;

        var distance = Vector3.Distance(playerPosition, resolved.Value);
        return Build(
            RuleDistancePreviewKind.MapXzDestination,
            "Exact Rules Editor Dist (Vector3.Distance; Map XZ uses player Y)",
            distance,
            rule.MaxDistance);
    }

    private static RuleDistancePreview? ResolveXyzDestination(ObjectPriorityRule rule, Vector3 playerPosition)
    {
        if (!TryParseWorldCoordinates(rule.WorldCoordinates, out var worldCoordinates))
            return null;

        var distance = Vector3.Distance(playerPosition, worldCoordinates);
        return Build(
            RuleDistancePreviewKind.XyzDestination,
            "Exact Rules Editor Dist (Vector3.Distance)",
            distance,
            rule.MaxDistance);
    }

    private static RuleDistancePreview? ResolveCardinalHold(ObjectPriorityRule rule, Vector3 playerPosition)
    {
        if (!CardinalHoldPolicy.TryParseActivationPoint(rule.WorldCoordinates, out var activationPoint)
            || !IsFinite(activationPoint))
        {
            return null;
        }

        var distance = CardinalHoldPolicy.HorizontalDistance(playerPosition, activationPoint);
        return Build(
            RuleDistancePreviewKind.CardinalHold,
            "Exact Rules Editor Dist (XZ activation radius; Y ignored)",
            distance,
            rule.MaxDistance);
    }

    private static RuleDistancePreview? ResolveOrdinaryObject(
        ObjectPriorityRule rule,
        Vector3 playerPosition,
        Func<Vector2, float, Vector3?> resolveMapCoordinates,
        IEnumerable<RuleDistancePreviewObject> liveObjects,
        Func<ObjectPriorityRule, RuleDistancePreviewObject, bool> matchesOrdinaryObject)
    {
        Vector3 authoredPosition;
        if (!string.IsNullOrWhiteSpace(rule.ObjectWorldCoordinates))
        {
            if (!TryParseWorldCoordinates(rule.ObjectWorldCoordinates, out authoredPosition))
                return null;
        }
        else
        {
            if (!TryParseMapCoordinates(rule.ObjectMapCoordinates, out var mapCoordinates))
                return null;

            var resolved = resolveMapCoordinates(mapCoordinates, playerPosition.Y);
            if (!resolved.HasValue || !IsFinite(resolved.Value))
                return null;

            authoredPosition = resolved.Value;
        }

        var planningAidDistance = Vector3.Distance(playerPosition, authoredPosition);
        var distances = liveObjects
            .Where(x => IsFinite(x.Position))
            .Where(x => matchesOrdinaryObject(rule, x))
            .Select(x => Vector3.Distance(playerPosition, x.Position))
            .Where(float.IsFinite)
            .Order()
            .ToList();
        var exactDistance = distances.Count > 0 ? distances[0] : (float?)null;

        return new RuleDistancePreview(
            RuleDistancePreviewKind.OrdinaryObject,
            "Exact Rules Editor Dist gate (player to live object Vector3.Distance)",
            exactDistance,
            rule.MaxDistance,
            Compare(exactDistance, rule.MaxDistance),
            distances.Count,
            exactDistance,
            distances.Count > 0 ? distances[^1] : null,
            planningAidDistance);
    }

    private static RuleDistancePreview Build(
        RuleDistancePreviewKind kind,
        string exactDistanceLabel,
        float exactDistance,
        float? configuredDistance)
        => new(
            kind,
            exactDistanceLabel,
            exactDistance,
            configuredDistance,
            Compare(exactDistance, configuredDistance),
            0,
            null,
            null,
            null);

    private static bool? Compare(float? exactDistance, float? configuredDistance)
        => exactDistance.HasValue && configuredDistance.HasValue
            ? exactDistance.Value <= configuredDistance.Value
            : null;

    private static bool IsMapXzDestinationRule(ObjectPriorityRule rule)
        => string.Equals(rule.Classification, nameof(InteractableClass.MapXzDestination), StringComparison.OrdinalIgnoreCase)
           || string.Equals(rule.Classification, nameof(InteractableClass.MapXzForceMarch), StringComparison.OrdinalIgnoreCase)
           || string.Equals(rule.DestinationType, "MapXZ", StringComparison.OrdinalIgnoreCase);

    private static bool IsXyzDestinationRule(ObjectPriorityRule rule)
        => string.Equals(rule.Classification, nameof(InteractableClass.XYZ), StringComparison.OrdinalIgnoreCase)
           || string.Equals(rule.Classification, nameof(InteractableClass.XYZForceMarch), StringComparison.OrdinalIgnoreCase)
           || string.Equals(rule.DestinationType, "XYZ", StringComparison.OrdinalIgnoreCase);

    private static bool IsCardinalHoldRule(ObjectPriorityRule rule)
        => CardinalHoldPolicy.TryParseDirection(rule.Classification, out _);

    private static bool TryParseMapCoordinates(string value, out Vector2 coordinates)
    {
        coordinates = default;
        var parts = value.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2
            || !float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
        {
            return false;
        }

        coordinates = new Vector2(x, z);
        return IsFinite(coordinates);
    }

    private static bool TryParseWorldCoordinates(string value, out Vector3 coordinates)
    {
        coordinates = default;
        var parts = value.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 3
            || !float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
            || !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
        {
            return false;
        }

        coordinates = new Vector3(x, y, z);
        return IsFinite(coordinates);
    }

    private static bool IsFinite(Vector2 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y);

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
