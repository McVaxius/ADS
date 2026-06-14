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
            entry =>
            {
                Assert.Equal(RuleSemanticsCatalog.UniversalFields.Length, entry.Fields.Count);
                Assert.False(string.IsNullOrWhiteSpace(entry.Goal));
                Assert.False(string.IsNullOrWhiteSpace(entry.Behavior));
                Assert.False(string.IsNullOrWhiteSpace(entry.CommonExample));
                Assert.NotEmpty(RuleSemanticsCatalog.GetRelevantEditorFieldLabels(entry));
            });
        Assert.Equal(
            RuleSemanticsCatalog.UniversalFields.OrderBy(x => x),
            RuleSemanticsCatalog.FieldGlossary.Keys.OrderBy(x => x));
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
        Assert.Equal(RuleFieldUse.Recommended, entry.Fields[nameof(ObjectPriorityRule.DutyEnglishName)]);
        Assert.Equal(RuleFieldUse.Ignored, entry.Fields[nameof(ObjectPriorityRule.WaitAfterInteractSeconds)]);
    }

    [Fact]
    public void CardinalRequiredFieldValidationReportsOnlyMissingRequiredValues()
    {
        var rule = new ObjectPriorityRule
        {
            Classification = nameof(InteractableClass.CardinalHoldNorth),
        };

        Assert.Equal(
            [
                nameof(ObjectPriorityRule.WorldCoordinates),
                nameof(ObjectPriorityRule.MaxDistance),
                nameof(ObjectPriorityRule.WaitAtDestinationSeconds),
            ],
            RuleSemanticsCatalog.GetMissingRequiredFields(rule));

        rule.WorldCoordinates = "123.4,-56.7";
        rule.MaxDistance = 3f;
        rule.WaitAtDestinationSeconds = 1.5f;

        Assert.Empty(RuleSemanticsCatalog.GetMissingRequiredFields(rule));
    }

    [Fact]
    public void DestinationRequiredFieldValidationPreservesIgnoredFields()
    {
        var rule = new ObjectPriorityRule
        {
            Classification = nameof(InteractableClass.MapXzDestination),
            ObjectName = "Ignored but preserved",
            ObjectWorldCoordinates = "1,2,3",
        };

        Assert.Equal(
            [nameof(ObjectPriorityRule.MapCoordinates)],
            RuleSemanticsCatalog.GetMissingRequiredFields(rule));
        Assert.Equal("Ignored but preserved", rule.ObjectName);
        Assert.Equal("1,2,3", rule.ObjectWorldCoordinates);

        rule.MapCoordinates = "11.3,10.4";

        Assert.Empty(RuleSemanticsCatalog.GetMissingRequiredFields(rule));
        Assert.Equal("Ignored but preserved", rule.ObjectName);
        Assert.Equal("1,2,3", rule.ObjectWorldCoordinates);
    }
}
