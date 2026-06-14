namespace ADS.Models;

public enum RuleFieldUse
{
    Required,
    Recommended,
    Optional,
    Ignored,
}

public sealed record RuleClassificationSemantics(
    string Value,
    string Label,
    string Goal,
    string Behavior,
    string CommonExample,
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

    private static readonly IReadOnlyDictionary<string, string> EditorFieldLabels =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [nameof(ObjectPriorityRule.Enabled)] = "On",
            [nameof(ObjectPriorityRule.DutyEnglishName)] = "Duty",
            [nameof(ObjectPriorityRule.TerritoryTypeId)] = "Terr",
            [nameof(ObjectPriorityRule.ContentFinderConditionId)] = "CFC",
            [nameof(ObjectPriorityRule.ObjectKind)] = "Kind",
            [nameof(ObjectPriorityRule.ObjectName)] = "Name",
            [nameof(ObjectPriorityRule.NameMatchMode)] = "Match",
            [nameof(ObjectPriorityRule.Classification)] = "Class",
            [nameof(ObjectPriorityRule.Layer)] = "Layer",
            [nameof(ObjectPriorityRule.MapCoordinates)] = "Coords",
            [nameof(ObjectPriorityRule.WorldCoordinates)] = "Coords",
            [nameof(ObjectPriorityRule.ObjectMapCoordinates)] = "Coords",
            [nameof(ObjectPriorityRule.ObjectWorldCoordinates)] = "Coords",
            [nameof(ObjectPriorityRule.ObjectMatchRadius)] = "R",
            [nameof(ObjectPriorityRule.Priority)] = "Pri",
            [nameof(ObjectPriorityRule.PriorityVerticalRadius)] = "Y",
            [nameof(ObjectPriorityRule.MaxDistance)] = "Dist",
            [nameof(ObjectPriorityRule.WaitAtDestinationSeconds)] = "Wait-before",
            [nameof(ObjectPriorityRule.WaitAfterInteractSeconds)] = "Wait-after",
            [nameof(ObjectPriorityRule.Notes)] = "Notes",
        };

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
        ObjectEntry(nameof(InteractableClass.Ignored), "Ignored", "Hide an object ADS should not plan around.", "Suppress matching object from planner truth.", "Ignore one exact decorative or stale object.", waits: false),
        ObjectEntry(nameof(InteractableClass.Follow), "Follow", "Follow a moving friendly anchor.", "Use matching live BattleNpc as movement anchor.", "Follow Cid or another moving BattleNpc.", waits: false),
        ObjectEntry(nameof(InteractableClass.BossFight), "BossFight", "Commit combat targeting to a known boss.", "Prioritize matching live BattleNpc as boss target.", "Prefer a boss over nearby trash.", waits: false),
        ObjectEntry(nameof(InteractableClass.Required), "Required", "Make an interactable required progression.", "Treat matching object as required progression.", "Use a lift lever or required terminal.", waits: true),
        ObjectEntry(nameof(InteractableClass.Optional), "Optional", "Use an interactable only when stronger truth is absent.", "Treat matching object as optional progression.", "Use an optional shortcut.", waits: true),
        ObjectEntry(nameof(InteractableClass.Expendable), "Expendable", "Retry an interactable until it disappears.", "Retry matching interactable until it disappears.", "Consume firesand or another one-shot object.", waits: true),
        ObjectEntry(nameof(InteractableClass.CombatFriendly), "CombatFriendly", "Interact with a friendly target during combat.", "Direct-interact friendly BattleNpc/EventNpc.", "Talk to a Goblin Pathfinder during combat.", waits: true),
        ObjectEntry(nameof(InteractableClass.TreasureCoffer), "TreasureCoffer", "Treat a matched object as optional treasure.", "Treat matching object as treasure loot.", "Mark a special coffer as treasure.", waits: true),
        ObjectEntry(nameof(InteractableClass.TreasureDoor), "TreasureDoor", "Treat a matched object as a treasure passage.", "Treat matching object as treasure passage.", "Choose a treasure-dungeon door.", waits: true),
        Destination(nameof(InteractableClass.MapXzDestination), "MapXzDestination", "Stage at a map-space waypoint.", "Navigate to authored map X,Z destination.", "Route through a map waypoint such as 11.3,10.4.", nameof(ObjectPriorityRule.MapCoordinates)),
        Destination(nameof(InteractableClass.XYZ), "XYZ", "Stage at a precise world-space waypoint.", "Navigate to precise world X,Y,Z destination.", "Route to a precise point such as 154.1,101.9,-34.2.", nameof(ObjectPriorityRule.WorldCoordinates)),
        Destination(nameof(InteractableClass.MapXzForceMarch), "MapXzForceMarch", "Force progress through incidental combat in map space.", "Commit through incidental combat to authored map X,Z destination.", "Force through a blocked map waypoint.", nameof(ObjectPriorityRule.MapCoordinates)),
        Destination(nameof(InteractableClass.XYZForceMarch), "XYZForceMarch", "Force progress through incidental combat in world space.", "Commit through incidental combat to precise world X,Y,Z destination.", "Force through a precise blocked waypoint.", nameof(ObjectPriorityRule.WorldCoordinates)),
        Cardinal(nameof(InteractableClass.CardinalHoldNorth), "CardinalHoldNorth", "Hold direct movement north at a known point.", "Inside activation radius, stop vnav and move world-north for full hold duration."),
        Cardinal(nameof(InteractableClass.CardinalHoldEast), "CardinalHoldEast", "Hold direct movement east at a known point.", "Inside activation radius, stop vnav and move world-east for full hold duration."),
        Cardinal(nameof(InteractableClass.CardinalHoldSouth), "CardinalHoldSouth", "Hold direct movement south at a known point.", "Inside activation radius, stop vnav and move world-south for full hold duration."),
        Cardinal(nameof(InteractableClass.CardinalHoldWest), "CardinalHoldWest", "Hold direct movement west at a known point.", "Inside activation radius, stop vnav and move world-west for full hold duration."),
    ];

    public static string[] ClassificationLabels => Classifications.Select(x => x.Label).ToArray();
    public static string[] ClassificationValues => Classifications.Select(x => x.Value).ToArray();

    public static RuleClassificationSemantics? Find(string value)
        => Classifications.FirstOrDefault(x => string.Equals(x.Value, value, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<string> GetRelevantEditorFieldLabels(RuleClassificationSemantics semantics)
        => semantics.Fields
            .Where(x => x.Value != RuleFieldUse.Ignored && EditorFieldLabels.ContainsKey(x.Key))
            .Select(x => EditorFieldLabels[x.Key])
            .Distinct(StringComparer.Ordinal)
            .ToList();

    public static IReadOnlyList<string> GetMissingRequiredFields(ObjectPriorityRule rule)
    {
        var semantics = Find(rule.Classification ?? string.Empty);
        if (semantics == null)
            return [nameof(ObjectPriorityRule.Classification)];

        return semantics.Fields
            .Where(x => x.Value == RuleFieldUse.Required && !HasRequiredValue(rule, x.Key))
            .Select(x => x.Key)
            .ToList();
    }

    private static bool HasRequiredValue(ObjectPriorityRule rule, string field)
        => field switch
        {
            nameof(ObjectPriorityRule.Classification) => !string.IsNullOrWhiteSpace(rule.Classification),
            nameof(ObjectPriorityRule.MapCoordinates) => !string.IsNullOrWhiteSpace(rule.MapCoordinates),
            nameof(ObjectPriorityRule.WorldCoordinates) => !string.IsNullOrWhiteSpace(rule.WorldCoordinates),
            nameof(ObjectPriorityRule.MaxDistance) => rule.MaxDistance > 0,
            nameof(ObjectPriorityRule.WaitAtDestinationSeconds) => rule.WaitAtDestinationSeconds > 0,
            _ => true,
        };

    private static RuleClassificationSemantics NoneEntry()
        => new(
            "",
            "(none)",
            "Define only scope, matching, gates, or notes.",
            "No behavior override.",
            "Use while drafting, then choose a behavior class before saving DEFAULT.",
            Matrix(
                (nameof(ObjectPriorityRule.Enabled), RuleFieldUse.Optional),
                (nameof(ObjectPriorityRule.DutyEnglishName), RuleFieldUse.Recommended),
                (nameof(ObjectPriorityRule.TerritoryTypeId), RuleFieldUse.Optional),
                (nameof(ObjectPriorityRule.ContentFinderConditionId), RuleFieldUse.Optional),
                (nameof(ObjectPriorityRule.ObjectKind), RuleFieldUse.Recommended),
                (nameof(ObjectPriorityRule.BaseId), RuleFieldUse.Optional),
                (nameof(ObjectPriorityRule.ObjectName), RuleFieldUse.Recommended),
                (nameof(ObjectPriorityRule.NameMatchMode), RuleFieldUse.Recommended),
                (nameof(ObjectPriorityRule.Classification), RuleFieldUse.Required),
                (nameof(ObjectPriorityRule.Layer), RuleFieldUse.Optional),
                (nameof(ObjectPriorityRule.Priority), RuleFieldUse.Optional),
                (nameof(ObjectPriorityRule.Notes), RuleFieldUse.Optional)));

    private static RuleClassificationSemantics ObjectEntry(
        string value,
        string label,
        string goal,
        string behavior,
        string commonExample,
        bool waits)
    {
        var fields = new List<(string Field, RuleFieldUse Use)>
        {
            (nameof(ObjectPriorityRule.Enabled), RuleFieldUse.Optional),
            (nameof(ObjectPriorityRule.TerritoryTypeId), RuleFieldUse.Optional),
            (nameof(ObjectPriorityRule.ContentFinderConditionId), RuleFieldUse.Optional),
            (nameof(ObjectPriorityRule.DutyEnglishName), RuleFieldUse.Recommended),
            (nameof(ObjectPriorityRule.ObjectKind), RuleFieldUse.Recommended),
            (nameof(ObjectPriorityRule.BaseId), RuleFieldUse.Optional),
            (nameof(ObjectPriorityRule.ObjectName), RuleFieldUse.Recommended),
            (nameof(ObjectPriorityRule.NameMatchMode), RuleFieldUse.Recommended),
            (nameof(ObjectPriorityRule.Classification), RuleFieldUse.Required),
            (nameof(ObjectPriorityRule.Layer), RuleFieldUse.Optional),
            (nameof(ObjectPriorityRule.ObjectMapCoordinates), RuleFieldUse.Optional),
            (nameof(ObjectPriorityRule.ObjectWorldCoordinates), RuleFieldUse.Optional),
            (nameof(ObjectPriorityRule.ObjectMatchRadius), RuleFieldUse.Optional),
            (nameof(ObjectPriorityRule.Priority), RuleFieldUse.Recommended),
            (nameof(ObjectPriorityRule.PriorityVerticalRadius), RuleFieldUse.Optional),
            (nameof(ObjectPriorityRule.MaxDistance), RuleFieldUse.Optional),
            (nameof(ObjectPriorityRule.Notes), RuleFieldUse.Optional),
        };
        if (waits)
        {
            fields.Add((nameof(ObjectPriorityRule.WaitAtDestinationSeconds), RuleFieldUse.Optional));
            fields.Add((nameof(ObjectPriorityRule.WaitAfterInteractSeconds), RuleFieldUse.Optional));
        }

        return new RuleClassificationSemantics(value, label, goal, behavior, commonExample, Matrix(fields.ToArray()));
    }

    private static RuleClassificationSemantics Destination(
        string value,
        string label,
        string goal,
        string behavior,
        string commonExample,
        string coordinateField)
        => new(value, label, goal, behavior, commonExample, Matrix(
            (nameof(ObjectPriorityRule.Enabled), RuleFieldUse.Optional),
            (nameof(ObjectPriorityRule.TerritoryTypeId), RuleFieldUse.Optional),
            (nameof(ObjectPriorityRule.ContentFinderConditionId), RuleFieldUse.Optional),
            (nameof(ObjectPriorityRule.DutyEnglishName), RuleFieldUse.Recommended),
            (nameof(ObjectPriorityRule.Classification), RuleFieldUse.Required),
            (nameof(ObjectPriorityRule.Layer), RuleFieldUse.Optional),
            (coordinateField, RuleFieldUse.Required),
            (nameof(ObjectPriorityRule.Priority), RuleFieldUse.Recommended),
            (nameof(ObjectPriorityRule.PriorityVerticalRadius), RuleFieldUse.Optional),
            (nameof(ObjectPriorityRule.MaxDistance), RuleFieldUse.Optional),
            (nameof(ObjectPriorityRule.Notes), RuleFieldUse.Optional)));

    private static RuleClassificationSemantics Cardinal(string value, string label, string goal, string behavior)
        => new(value, label, goal, behavior, "At the activation point, hold the named world-cardinal direction.", Matrix(
            (nameof(ObjectPriorityRule.Enabled), RuleFieldUse.Optional),
            (nameof(ObjectPriorityRule.TerritoryTypeId), RuleFieldUse.Optional),
            (nameof(ObjectPriorityRule.ContentFinderConditionId), RuleFieldUse.Optional),
            (nameof(ObjectPriorityRule.DutyEnglishName), RuleFieldUse.Recommended),
            (nameof(ObjectPriorityRule.Classification), RuleFieldUse.Required),
            (nameof(ObjectPriorityRule.Layer), RuleFieldUse.Optional),
            (nameof(ObjectPriorityRule.WorldCoordinates), RuleFieldUse.Required),
            (nameof(ObjectPriorityRule.MaxDistance), RuleFieldUse.Required),
            (nameof(ObjectPriorityRule.WaitAtDestinationSeconds), RuleFieldUse.Required),
            (nameof(ObjectPriorityRule.Priority), RuleFieldUse.Recommended),
            (nameof(ObjectPriorityRule.Notes), RuleFieldUse.Optional)));

    private static IReadOnlyDictionary<string, RuleFieldUse> Matrix(params (string Field, RuleFieldUse Use)[] overrides)
    {
        var matrix = UniversalFields.ToDictionary(field => field, _ => RuleFieldUse.Ignored, StringComparer.Ordinal);
        foreach (var (field, use) in overrides)
            matrix[field] = use;

        return matrix;
    }
}
