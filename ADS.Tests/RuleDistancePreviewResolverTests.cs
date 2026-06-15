using System.Numerics;
using ADS.Models;
using Dalamud.Game.ClientState.Objects.Enums;

namespace ADS.Tests;

public sealed class RuleDistancePreviewResolverTests
{
    [Theory]
    [InlineData("MapXzDestination")]
    [InlineData("MapXzForceMarch")]
    public void MapXzUsesResolvedWorldPointAtPlayerY(string classification)
    {
        var rule = new ObjectPriorityRule
        {
            Classification = classification,
            MapCoordinates = "3,4",
        };

        var preview = Resolve(rule, Vector3.Zero, (coordinates, playerY) => new Vector3(coordinates.X, playerY, coordinates.Y));

        Assert.NotNull(preview);
        Assert.Equal(RuleDistancePreviewKind.MapXzDestination, preview.Kind);
        Assert.Equal(5f, preview.ExactDistance);
    }

    [Theory]
    [InlineData("XYZ")]
    [InlineData("XYZForceMarch")]
    public void XyzUsesFullVector3Distance(string classification)
    {
        var rule = new ObjectPriorityRule
        {
            Classification = classification,
            WorldCoordinates = "1,2,2",
        };

        var preview = Resolve(rule, Vector3.Zero);

        Assert.NotNull(preview);
        Assert.Equal(RuleDistancePreviewKind.XyzDestination, preview.Kind);
        Assert.Equal(3f, preview.ExactDistance);
    }

    [Fact]
    public void CardinalHoldUsesHorizontalDistanceAndIgnoresY()
    {
        var rule = new ObjectPriorityRule
        {
            Classification = "CardinalHoldNorth",
            WorldCoordinates = "0,123,0",
            MaxDistance = 5f,
        };

        var preview = Resolve(rule, new Vector3(3, 999, 4));

        Assert.NotNull(preview);
        Assert.Equal(RuleDistancePreviewKind.CardinalHold, preview.Kind);
        Assert.Equal(5f, preview.ExactDistance);
        Assert.True(preview.PassesConfiguredDistance);
    }

    [Fact]
    public void OrdinaryRowUsesNearestMatchingLiveObjectAndSeparatePlanningAid()
    {
        var rule = new ObjectPriorityRule
        {
            ObjectName = "Target",
            ObjectWorldCoordinates = "10,0,0",
            MaxDistance = 6f,
        };
        RuleDistancePreviewObject[] objects =
        [
            Live("Ignored", new Vector3(1, 0, 0)),
            Live("Target", new Vector3(6, 0, 0)),
            Live("Target", new Vector3(3, 4, 0)),
        ];

        var preview = Resolve(rule, Vector3.Zero, liveObjects: objects, matches: (candidate, live) => live.Name == candidate.ObjectName);

        Assert.NotNull(preview);
        Assert.Equal(RuleDistancePreviewKind.OrdinaryObject, preview.Kind);
        Assert.Equal(5f, preview.ExactDistance);
        Assert.Equal(2, preview.LiveMatchCount);
        Assert.Equal(5f, preview.LiveMatchNearestDistance);
        Assert.Equal(6f, preview.LiveMatchFarthestDistance);
        Assert.Equal(10f, preview.PlanningAidDistance);
        Assert.True(preview.PassesConfiguredDistance);
    }

    [Fact]
    public void OrdinaryRowWithoutLiveMatchKeepsPlanningAidButNoExactGateDistance()
    {
        var rule = new ObjectPriorityRule
        {
            ObjectWorldCoordinates = "0,0,10",
            MaxDistance = 20f,
        };

        var preview = Resolve(rule, Vector3.Zero);

        Assert.NotNull(preview);
        Assert.Null(preview.ExactDistance);
        Assert.Equal(0, preview.LiveMatchCount);
        Assert.Equal(10f, preview.PlanningAidDistance);
        Assert.Null(preview.PassesConfiguredDistance);
    }

    [Theory]
    [InlineData(5f, true)]
    [InlineData(4.99f, false)]
    public void DistComparisonReportsPassOrFail(float configuredDistance, bool expectedPass)
    {
        var rule = new ObjectPriorityRule
        {
            Classification = "XYZ",
            WorldCoordinates = "3,0,4",
            MaxDistance = configuredDistance,
        };

        var preview = Resolve(rule, Vector3.Zero);

        Assert.NotNull(preview);
        Assert.Equal(expectedPass, preview.PassesConfiguredDistance);
    }

    [Fact]
    public void InvalidNonFiniteAndUnresolvableCoordinatesProduceNoPreview()
    {
        Assert.Null(Resolve(new ObjectPriorityRule(), Vector3.Zero));
        Assert.Null(Resolve(
            new ObjectPriorityRule { Classification = "MapXzDestination", MapCoordinates = "bad,4" },
            Vector3.Zero));
        Assert.Null(Resolve(
            new ObjectPriorityRule { Classification = "XYZ", WorldCoordinates = "1,NaN,2" },
            Vector3.Zero));
        Assert.Null(Resolve(
            new ObjectPriorityRule { Classification = "MapXzDestination", MapCoordinates = "1,2" },
            Vector3.Zero,
            (_, _) => null));
        Assert.Null(Resolve(
            new ObjectPriorityRule { ObjectMapCoordinates = "1,2" },
            Vector3.Zero,
            (_, _) => null));
    }

    private static RuleDistancePreview? Resolve(
        ObjectPriorityRule rule,
        Vector3 playerPosition,
        Func<Vector2, float, Vector3?>? mapResolver = null,
        IEnumerable<RuleDistancePreviewObject>? liveObjects = null,
        Func<ObjectPriorityRule, RuleDistancePreviewObject, bool>? matches = null)
        => RuleDistancePreviewResolver.Resolve(
            rule,
            playerPosition,
            mapResolver ?? ((coordinates, playerY) => new Vector3(coordinates.X, playerY, coordinates.Y)),
            liveObjects,
            matches);

    private static RuleDistancePreviewObject Live(string name, Vector3 position)
        => new(ObjectKind.EventObj, 1, name, position, 1);
}
