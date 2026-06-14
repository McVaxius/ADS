using System.Numerics;
using ADS.Models;
using ADS.Services;

namespace ADS.Tests;

public sealed class CardinalHoldPolicyTests
{
    [Theory]
    [InlineData("123.4,-56.7", 123.4f, -56.7f)]
    [InlineData("123.4,999,-56.7", 123.4f, -56.7f)]
    public void ParsesWorldXzAndIgnoresOptionalY(string value, float expectedX, float expectedZ)
    {
        Assert.True(CardinalHoldPolicy.TryParseActivationPoint(value, out var point));
        Assert.Equal(expectedX, point.X, 3);
        Assert.Equal(expectedZ, point.Y, 3);
    }

    [Fact]
    public void RequiresCoordinatesPositiveRadiusAndPositiveDuration()
    {
        var rule = Rule("CardinalHoldNorth", "1,2", 3f, 1.5f);
        Assert.True(CardinalHoldPolicy.TryParse(rule, out _));

        rule.WorldCoordinates = "";
        Assert.False(CardinalHoldPolicy.TryParse(rule, out _));
        rule.WorldCoordinates = "1,2";
        rule.MaxDistance = 0;
        Assert.False(CardinalHoldPolicy.TryParse(rule, out _));
        rule.MaxDistance = 3;
        rule.WaitAtDestinationSeconds = 0;
        Assert.False(CardinalHoldPolicy.TryParse(rule, out _));
    }

    [Fact]
    public void RejectsMalformedIgnoredY()
        => Assert.False(CardinalHoldPolicy.TryParseActivationPoint("1,bad,2", out _));

    [Fact]
    public void UsesHorizontalRadiusAndLowestPriority()
    {
        var higher = Rule("CardinalHoldNorth", "0,0", 3, 1).WithPriority(100);
        var lower = Rule("CardinalHoldWest", "0,999,0", 3, 1).WithPriority(10);
        var selected = CardinalHoldPolicy.SelectActive(
            [higher, lower],
            new Vector3(2, 500, 0),
            _ => true,
            _ => false);

        Assert.NotNull(selected);
        Assert.Equal(CardinalDirection.West, selected.Value.Direction);
    }

    [Theory]
    [InlineData(CardinalDirection.South, 0f, RelativeMovementDirection.Forward)]
    [InlineData(CardinalDirection.North, 0f, RelativeMovementDirection.Back)]
    [InlineData(CardinalDirection.East, 0f, RelativeMovementDirection.Right)]
    [InlineData(CardinalDirection.West, 0f, RelativeMovementDirection.Left)]
    public void MapsWorldDirectionAgainstPlayerRotation(
        CardinalDirection direction,
        float rotation,
        RelativeMovementDirection expected)
        => Assert.Equal(expected, CardinalDirectionMapper.Resolve(direction, rotation));

    [Fact]
    public void InterruptedHoldRemainsAvailableAndConsumedHoldGhostsUntilRecovery()
    {
        var tracker = new CardinalHoldGhostTracker();
        const string key = "hold";

        Assert.False(tracker.IsGhosted(key));
        tracker.Consume(key);
        Assert.True(tracker.IsGhosted(key));
        Assert.Equal(0, tracker.ObserveStableDutyPosition(Vector3.Zero));
        Assert.True(tracker.IsGhosted(key));
        Assert.Equal(1, tracker.ObserveStableDutyPosition(new Vector3(40, 0, 0)));
        Assert.False(tracker.IsGhosted(key));
    }

    private static ObjectPriorityRule Rule(string classification, string coordinates, float radius, float duration)
        => new()
        {
            Classification = classification,
            WorldCoordinates = coordinates,
            MaxDistance = radius,
            WaitAtDestinationSeconds = duration,
        };
}

file static class RuleTestExtensions
{
    public static ObjectPriorityRule WithPriority(this ObjectPriorityRule rule, int priority)
    {
        rule.Priority = priority;
        return rule;
    }
}
