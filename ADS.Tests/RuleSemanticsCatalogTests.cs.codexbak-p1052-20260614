using ADS.Models;
using ADS.Windows;

namespace ADS.Tests;

public sealed class RuleSemanticsCatalogTests
{
    [Fact]
    public void EveryClassificationHasExactlyOneGuideEntryAndFieldMatrix()
    {
        var expected = Enum.GetNames<InteractableClass>().Append(string.Empty).OrderBy(x => x).ToArray();
        var actual = RuleSemanticsCatalog.Classifications.Select(x => x.Value).OrderBy(x => x).ToArray();

        Assert.Equal(expected, actual);
        Assert.Equal(actual.Length, actual.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(
            RuleSemanticsCatalog.Classifications,
            entry => Assert.Equal(RuleSemanticsCatalog.UniversalFields.Length, entry.Fields.Count));
        Assert.Equal(
            RuleSemanticsCatalog.Classifications.Select(x => x.Value),
            ObjectRuleEditorWindow.ClassificationValues);
    }

    [Theory]
    [InlineData("CardinalHoldNorth")]
    [InlineData("CardinalHoldEast")]
    [InlineData("CardinalHoldSouth")]
    [InlineData("CardinalHoldWest")]
    public void CardinalMatrixUsesOnlyExistingFields(string classification)
    {
        var entry = Assert.Single(RuleSemanticsCatalog.Classifications, x => x.Value == classification);

        Assert.Equal(RuleFieldUse.Required, entry.Fields[nameof(ObjectPriorityRule.WorldCoordinates)]);
        Assert.Equal(RuleFieldUse.Required, entry.Fields[nameof(ObjectPriorityRule.MaxDistance)]);
        Assert.Equal(RuleFieldUse.Required, entry.Fields[nameof(ObjectPriorityRule.WaitAtDestinationSeconds)]);
        Assert.Equal(RuleFieldUse.Ignored, entry.Fields[nameof(ObjectPriorityRule.WaitAfterInteractSeconds)]);
    }
}
