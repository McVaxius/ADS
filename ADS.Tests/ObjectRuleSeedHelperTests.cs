using System.Numerics;
using ADS.Models;
using ADS.Services;

namespace ADS.Tests;

public sealed class ObjectRuleSeedHelperTests
{
    [Fact]
    public void XyzRuleSeedsWorldCoordinates()
    {
        var rule = new ObjectPriorityRule();

        ObjectRuleSeedHelper.ApplyCoordinates(
            rule,
            "XYZ",
            new Vector3(12f, 34f, 56f),
            pinOrdinaryRuleToObject: true,
            mapCoordinates: new Vector2(9f, 10f),
            out _);

        Assert.Equal("12,34,56", rule.WorldCoordinates);
        Assert.Empty(rule.MapCoordinates);
        Assert.Empty(rule.ObjectWorldCoordinates);
        Assert.Null(rule.ObjectMatchRadius);
    }

    [Fact]
    public void MapXzRuleSeedsMapCoordinates()
    {
        var rule = new ObjectPriorityRule();

        ObjectRuleSeedHelper.ApplyCoordinates(
            rule,
            "MapXzDestination",
            new Vector3(12f, 34f, 56f),
            pinOrdinaryRuleToObject: true,
            mapCoordinates: new Vector2(10.5f, 20.25f),
            out _);

        Assert.Equal("10.5,20.25", rule.MapCoordinates);
        Assert.Empty(rule.WorldCoordinates);
        Assert.Empty(rule.ObjectWorldCoordinates);
        Assert.Null(rule.ObjectMatchRadius);
    }

    [Fact]
    public void OrdinaryPinnedRuleSeedsObjectWorldCoordinatesAndRadius()
    {
        var rule = new ObjectPriorityRule();

        ObjectRuleSeedHelper.ApplyCoordinates(
            rule,
            "Required",
            new Vector3(12f, 34f, 56f),
            pinOrdinaryRuleToObject: true,
            mapCoordinates: null,
            out _);

        Assert.Equal("12,34,56", rule.ObjectWorldCoordinates);
        Assert.Equal(6f, rule.ObjectMatchRadius);
        Assert.Empty(rule.MapCoordinates);
        Assert.Empty(rule.WorldCoordinates);
    }

    [Fact]
    public void OrdinaryUnpinnedRuleLeavesCoordinatesBlank()
    {
        var rule = new ObjectPriorityRule();

        ObjectRuleSeedHelper.ApplyCoordinates(
            rule,
            "Required",
            new Vector3(12f, 34f, 56f),
            pinOrdinaryRuleToObject: false,
            mapCoordinates: new Vector2(10.5f, 20.25f),
            out _);

        Assert.Empty(rule.ObjectWorldCoordinates);
        Assert.Empty(rule.ObjectMapCoordinates);
        Assert.Empty(rule.MapCoordinates);
        Assert.Empty(rule.WorldCoordinates);
        Assert.Null(rule.ObjectMatchRadius);
    }
}
