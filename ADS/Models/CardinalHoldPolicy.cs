using System.Globalization;
using System.Numerics;

namespace ADS.Models;

public enum CardinalDirection
{
    North,
    East,
    South,
    West,
}

public readonly record struct CardinalHoldRule(
    ObjectPriorityRule Rule,
    CardinalDirection Direction,
    Vector2 ActivationPoint,
    float ActivationRadius,
    TimeSpan Duration,
    string Key);

public static class CardinalHoldPolicy
{
    public const float RecoveryRelocationDistance = 40f;

    public static bool TryParse(ObjectPriorityRule rule, out CardinalHoldRule hold)
    {
        hold = default;
        if (!TryParseDirection(rule.Classification, out var direction)
            || !TryParseActivationPoint(rule.WorldCoordinates, out var activationPoint)
            || rule.MaxDistance is not > 0f
            || rule.WaitAtDestinationSeconds <= 0f)
        {
            return false;
        }

        hold = new CardinalHoldRule(
            rule,
            direction,
            activationPoint,
            rule.MaxDistance.Value,
            TimeSpan.FromSeconds(rule.WaitAtDestinationSeconds),
            BuildKey(rule, direction, activationPoint));
        return true;
    }

    public static CardinalHoldRule? SelectActive(
        IEnumerable<ObjectPriorityRule> rules,
        Vector3 playerPosition,
        Func<ObjectPriorityRule, bool> scopeMatches,
        Func<string, bool> isGhosted)
        => rules
            .Where(rule => rule.Enabled && scopeMatches(rule))
            .Select(rule => TryParse(rule, out var hold) ? hold : (CardinalHoldRule?)null)
            .Where(hold => hold.HasValue)
            .Select(hold => hold!.Value)
            .Where(hold => !isGhosted(hold.Key))
            .Where(hold => HorizontalDistance(playerPosition, hold.ActivationPoint) <= hold.ActivationRadius)
            .OrderBy(hold => hold.Rule.Priority)
            .Select(hold => (CardinalHoldRule?)hold)
            .FirstOrDefault();

    public static bool TryParseDirection(string classification, out CardinalDirection direction)
    {
        direction = default;
        if (!Enum.TryParse<InteractableClass>(classification, true, out var parsed))
            return false;

        direction = parsed switch
        {
            InteractableClass.CardinalHoldNorth => CardinalDirection.North,
            InteractableClass.CardinalHoldEast => CardinalDirection.East,
            InteractableClass.CardinalHoldSouth => CardinalDirection.South,
            InteractableClass.CardinalHoldWest => CardinalDirection.West,
            _ => default,
        };
        return parsed is InteractableClass.CardinalHoldNorth
            or InteractableClass.CardinalHoldEast
            or InteractableClass.CardinalHoldSouth
            or InteractableClass.CardinalHoldWest;
    }

    public static bool TryParseActivationPoint(string value, out Vector2 point)
    {
        point = default;
        var parts = value.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length is not (2 or 3)
            || !float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x))
        {
            return false;
        }

        if (parts.Length == 3
            && !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            return false;
        }

        var zIndex = parts.Length == 2 ? 1 : 2;
        if (!float.TryParse(parts[zIndex], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
            return false;

        point = new Vector2(x, z);
        return true;
    }

    public static float HorizontalDistance(Vector3 position, Vector2 point)
    {
        var x = position.X - point.X;
        var z = position.Z - point.Y;
        return MathF.Sqrt((x * x) + (z * z));
    }

    private static string BuildKey(ObjectPriorityRule rule, CardinalDirection direction, Vector2 point)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{rule.ContentFinderConditionId}:{rule.TerritoryTypeId}:{rule.DutyEnglishName}:{rule.Layer}:{direction}:{point.X:0.###},{point.Y:0.###}:{rule.Priority}");
}

public sealed class CardinalHoldGhostTracker
{
    private readonly HashSet<string> ghosts = new(StringComparer.Ordinal);
    private Vector3? lastStablePosition;

    public int Count => ghosts.Count;

    public bool IsGhosted(string key) => ghosts.Contains(key);

    public void Consume(string key) => ghosts.Add(key);

    public int Reset()
    {
        var count = ghosts.Count;
        ghosts.Clear();
        lastStablePosition = null;
        return count;
    }

    public int ObserveStableDutyPosition(Vector3 position)
    {
        var resetCount = 0;
        if (lastStablePosition.HasValue
            && Vector3.Distance(lastStablePosition.Value, position) >= CardinalHoldPolicy.RecoveryRelocationDistance)
        {
            resetCount = ghosts.Count;
            ghosts.Clear();
        }

        lastStablePosition = position;
        return resetCount;
    }
}
