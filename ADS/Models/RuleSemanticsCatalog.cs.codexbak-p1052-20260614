namespace ADS.Models;

public enum RuleFieldUse
{
    Required,
    Optional,
    Ignored,
}

public sealed record RuleClassificationSemantics(
    string Value,
    string Label,
    string Behavior,
    IReadOnlyDictionary<string, RuleFieldUse> Fields);

public static class RuleSemanticsCatalog
{
    public static readonly string[] UniversalFields =
    [
        nameof(ObjectPriorityRule.Enabled),
        nameof(ObjectPriorityRule.TerritoryTypeId),
        nameof(ObjectPriorityRule.ContentFinderConditionId),
        nameof(ObjectPriorityRule.DutyEnglishName),
        nameof(ObjectPriorityRule.ObjectKind),
        nameof(ObjectPriorityRule.BaseId),
        nameof(ObjectPriorityRule.ObjectName),
        nameof(ObjectPriorityRule.NameMatchMode),
        nameof(ObjectPriorityRule.Classification),
        nameof(ObjectPriorityRule.DestinationType),
        nameof(ObjectPriorityRule.Layer),
        nameof(ObjectPriorityRule.MapCoordinates),
        nameof(ObjectPriorityRule.WorldCoordinates),
        nameof(ObjectPriorityRule.ObjectMapCoordinates),
        nameof(ObjectPriorityRule.ObjectWorldCoordinates),
        nameof(ObjectPriorityRule.ObjectMatchRadius),
        nameof(ObjectPriorityRule.Priority),
        nameof(ObjectPriorityRule.PriorityVerticalRadius),
        nameof(ObjectPriorityRule.MaxDistance),
        nameof(ObjectPriorityRule.WaitAtDestinationSeconds),
        nameof(ObjectPriorityRule.WaitAfterInteractSeconds),
        nameof(ObjectPriorityRule.Notes),
    ];

    public static IReadOnlyDictionary<string, string> FieldGlossary { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [nameof(ObjectPriorityRule.Enabled)] = "Enables this row.",
            [nameof(ObjectPriorityRule.TerritoryTypeId)] = "Optional territory scope; zero is wildcard.",
            [nameof(ObjectPriorityRule.ContentFinderConditionId)] = "Optional duty scope; zero is wildcard.",
            [nameof(ObjectPriorityRule.DutyEnglishName)] = "Optional English duty-name scope.",
            [nameof(ObjectPriorityRule.ObjectKind)] = "Optional live object-kind match.",
            [nameof(ObjectPriorityRule.BaseId)] = "Optional live object base-id match; zero is wildcard.",
            [nameof(ObjectPriorityRule.ObjectName)] = "Optional object-name match.",
            [nameof(ObjectPriorityRule.NameMatchMode)] = "Exact or Contains object-name matching.",
            [nameof(ObjectPriorityRule.Classification)] = "Required behavior classification.",
            [nameof(ObjectPriorityRule.DestinationType)] = "Legacy destination/layer field; avoid for new rows.",
            [nameof(ObjectPriorityRule.Layer)] = "Optional live map/sub-area scope.",
            [nameof(ObjectPriorityRule.MapCoordinates)] = "Manual destination map X,Z.",
            [nameof(ObjectPriorityRule.WorldCoordinates)] = "Manual destination world X,Y,Z; cardinal rules use X,Z or X,Y,Z with Y ignored.",
            [nameof(ObjectPriorityRule.ObjectMapCoordinates)] = "Optional ordinary-object positional selector in map X,Z.",
            [nameof(ObjectPriorityRule.ObjectWorldCoordinates)] = "Optional ordinary-object positional selector in world X,Y,Z.",
            [nameof(ObjectPriorityRule.ObjectMatchRadius)] = "Optional ordinary-object positional-match radius.",
            [nameof(ObjectPriorityRule.Priority)] = "Lower value wins after scope matching.",
            [nameof(ObjectPriorityRule.PriorityVerticalRadius)] = "Optional vertical-distance gate for ordinary/manual rules.",
            [nameof(ObjectPriorityRule.MaxDistance)] = "Optional distance gate; required positive activation X/Z radius for cardinal holds.",
            [nameof(ObjectPriorityRule.WaitAtDestinationSeconds)] = "Pre-interact wait; required positive movement duration for cardinal holds.",
            [nameof(ObjectPriorityRule.WaitAfterInteractSeconds)] = "Post-interact wait.",
            [nameof(ObjectPriorityRule.Notes)] = "Human documentation only.",
        };

    public static IReadOnlyList<RuleClassificationSemantics> Classifications { get; } =
    [
        NoneEntry(),
        ObjectEntry(nameof(InteractableClass.Ignored), "Ignored", "Suppress matching object from planner truth.", waits: false),
        ObjectEntry(nameof(InteractableClass.Follow), "Follow", "Use matching live BattleNpc as movement anchor.", waits: false),
        ObjectEntry(nameof(InteractableClass.BossFight), "BossFight", "Prioritize matching live BattleNpc as boss target.", waits: false),
        ObjectEntry(nameof(InteractableClass.Required), "Required", "Treat matching object as required progression.", waits: true),
        ObjectEntry(nameof(InteractableClass.Optional), "Optional", "Treat matching object as optional progression.", waits: true),
        ObjectEntry(nameof(InteractableClass.Expendable), "Expendable", "Retry matching interactable until it disappears.", waits: true),
        ObjectEntry(nameof(InteractableClass.CombatFriendly), "CombatFriendly", "Direct-interact friendly BattleNpc/EventNpc.", waits: true),
        ObjectEntry(nameof(InteractableClass.TreasureCoffer), "TreasureCoffer", "Treat matching object as treasure loot.", waits: true),
        ObjectEntry(nameof(InteractableClass.TreasureDoor), "TreasureDoor", "Treat matching object as treasure passage.", waits: true),
        Destination(nameof(InteractableClass.MapXzDestination), "MapXzDestination", "Navigate to authored map X,Z destination.", nameof(ObjectPriorityRule.MapCoordinates)),
        Destination(nameof(InteractableClass.XYZ), "XYZ", "Navigate to precise world X,Y,Z destination.", nameof(ObjectPriorityRule.WorldCoordinates)),
        Destination(nameof(InteractableClass.MapXzForceMarch), "MapXzForceMarch", "Commit through incidental combat to authored map X,Z destination.", nameof(ObjectPriorityRule.MapCoordinates)),
        Destination(nameof(InteractableClass.XYZForceMarch), "XYZForceMarch", "Commit through incidental combat to precise world X,Y,Z destination.", nameof(ObjectPriorityRule.WorldCoordinates)),
        Cardinal(nameof(InteractableClass.CardinalHoldNorth), "CardinalHoldNorth", "Inside activation radius, stop vnav and move world-north for full hold duration."),
        Cardinal(nameof(InteractableClass.CardinalHoldEast), "CardinalHoldEast", "Inside activation radius, stop vnav and move world-east for full hold duration."),
        Cardinal(nameof(InteractableClass.CardinalHoldSouth), "CardinalHoldSouth", "Inside activation radius, stop vnav and move world-south for full hold duration."),
        Cardinal(nameof(InteractableClass.CardinalHoldWest), "CardinalHoldWest", "Inside activation radius, stop vnav and move world-west for full hold duration."),
    ];

    public static string[] ClassificationLabels => Classifications.Select(x => x.Label).ToArray();
    public static string[] ClassificationValues => Classifications.Select(x => x.Value).ToArray();

    public static RuleClassificationSemantics? Find(string value)
        => Classifications.FirstOrDefault(x => string.Equals(x.Value, value, StringComparison.OrdinalIgnoreCase));

    private static RuleClassificationSemantics NoneEntry()
        => new("", "(none)", "No behavior override.", Matrix(
            (nameof(ObjectPriorityRule.Enabled), RuleFieldUse.Optional),
            (nameof(ObjectPriorityRule.Classification), RuleFieldUse.Required),
            (nameof(ObjectPriorityRule.Notes), RuleFieldUse.Optional)));

    private static RuleClassificationSemantics ObjectEntry(string value, string label, string behavior, bool waits)
    {
        var fields = new List<(string Field, RuleFieldUse Use)>
        {
            (nameof(ObjectPriorityRule.Enabled), RuleFieldUse.Optional),
            (nameof(ObjectPriorityRule.TerritoryTypeId), RuleFieldUse.Optional),
            (nameof(ObjectPriorityRule.ContentFinderConditionId), RuleFieldUse.Optional),
            (nameof(ObjectPriorityRule.DutyEnglishName), RuleFieldUse.Optional),
            (nameof(ObjectPriorityRule.ObjectKind), RuleFieldUse.Optional),
            (nameof(ObjectPriorityRule.BaseId), RuleFieldUse.Optional),
            (nameof(ObjectPriorityRule.ObjectName), RuleFieldUse.Optional),
            (nameof(ObjectPriorityRule.NameMatchMode), RuleFieldUse.Optional),
            (nameof(ObjectPriorityRule.Classification), RuleFieldUse.Required),
            (nameof(ObjectPriorityRule.Layer), RuleFieldUse.Optional),
            (nameof(ObjectPriorityRule.ObjectMapCoordinates), RuleFieldUse.Optional),
            (nameof(ObjectPriorityRule.ObjectWorldCoordinates), RuleFieldUse.Optional),
            (nameof(ObjectPriorityRule.ObjectMatchRadius), RuleFieldUse.Optional),
            (nameof(ObjectPriorityRule.Priority), RuleFieldUse.Optional),
            (nameof(ObjectPriorityRule.PriorityVerticalRadius), RuleFieldUse.Optional),
            (nameof(ObjectPriorityRule.MaxDistance), RuleFieldUse.Optional),
            (nameof(ObjectPriorityRule.Notes), RuleFieldUse.Optional),
        };
        if (waits)
        {
            fields.Add((nameof(ObjectPriorityRule.WaitAtDestinationSeconds), RuleFieldUse.Optional));
            fields.Add((nameof(ObjectPriorityRule.WaitAfterInteractSeconds), RuleFieldUse.Optional));
        }

        return new RuleClassificationSemantics(value, label, behavior, Matrix(fields.ToArray()));
    }

    private static RuleClassificationSemantics Destination(string value, string label, string behavior, string coordinateField)
        => new(value, label, behavior, Matrix(
            (nameof(ObjectPriorityRule.Enabled), RuleFieldUse.Optional),
            (nameof(ObjectPriorityRule.TerritoryTypeId), RuleFieldUse.Optional),
            (nameof(ObjectPriorityRule.ContentFinderConditionId), RuleFieldUse.Optional),
            (nameof(ObjectPriorityRule.DutyEnglishName), RuleFieldUse.Optional),
            (nameof(ObjectPriorityRule.Classification), RuleFieldUse.Required),
            (nameof(ObjectPriorityRule.Layer), RuleFieldUse.Optional),
            (coordinateField, RuleFieldUse.Required),
            (nameof(ObjectPriorityRule.Priority), RuleFieldUse.Optional),
            (nameof(ObjectPriorityRule.PriorityVerticalRadius), RuleFieldUse.Optional),
            (nameof(ObjectPriorityRule.MaxDistance), RuleFieldUse.Optional),
            (nameof(ObjectPriorityRule.Notes), RuleFieldUse.Optional)));

    private static RuleClassificationSemantics Cardinal(string value, string label, string behavior)
        => new(value, label, behavior, Matrix(
            (nameof(ObjectPriorityRule.WorldCoordinates), RuleFieldUse.Required),
            (nameof(ObjectPriorityRule.MaxDistance), RuleFieldUse.Required),
            (nameof(ObjectPriorityRule.WaitAtDestinationSeconds), RuleFieldUse.Required),
            (nameof(ObjectPriorityRule.Priority), RuleFieldUse.Optional)));

    private static IReadOnlyDictionary<string, RuleFieldUse> Matrix(params (string Field, RuleFieldUse Use)[] overrides)
    {
        var matrix = UniversalFields.ToDictionary(field => field, _ => RuleFieldUse.Ignored, StringComparer.Ordinal);
        foreach (var (field, use) in overrides)
            matrix[field] = use;

        if (overrides.Any(x => x.Field == nameof(ObjectPriorityRule.WorldCoordinates)
                               && x.Use == RuleFieldUse.Required)
            && overrides.Any(x => x.Field == nameof(ObjectPriorityRule.WaitAtDestinationSeconds)))
        {
            foreach (var field in UniversalFields)
                matrix[field] = RuleFieldUse.Ignored;
            matrix[nameof(ObjectPriorityRule.Enabled)] = RuleFieldUse.Optional;
            matrix[nameof(ObjectPriorityRule.TerritoryTypeId)] = RuleFieldUse.Optional;
            matrix[nameof(ObjectPriorityRule.ContentFinderConditionId)] = RuleFieldUse.Optional;
            matrix[nameof(ObjectPriorityRule.DutyEnglishName)] = RuleFieldUse.Optional;
            matrix[nameof(ObjectPriorityRule.Classification)] = RuleFieldUse.Required;
            matrix[nameof(ObjectPriorityRule.Layer)] = RuleFieldUse.Optional;
            matrix[nameof(ObjectPriorityRule.WorldCoordinates)] = RuleFieldUse.Required;
            matrix[nameof(ObjectPriorityRule.MaxDistance)] = RuleFieldUse.Required;
            matrix[nameof(ObjectPriorityRule.WaitAtDestinationSeconds)] = RuleFieldUse.Required;
            matrix[nameof(ObjectPriorityRule.Priority)] = RuleFieldUse.Optional;
            matrix[nameof(ObjectPriorityRule.Notes)] = RuleFieldUse.Optional;
        }

        return matrix;
    }
}
