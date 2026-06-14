using System.Globalization;
using System.Numerics;
using ADS.Models;

namespace ADS.Services;

internal static class ObjectRuleSeedHelper
{
    private const float PinnedObjectRuleRadius = 6f;

    public static void ApplyCoordinates(
        ObjectPriorityRule rule,
        string classification,
        Vector3 worldPosition,
        bool pinOrdinaryRuleToObject,
        Vector2? mapCoordinates,
        out string coordinateNote)
    {
        if (IsXyzDestinationClassification(classification))
        {
            rule.WorldCoordinates = FormatWorldCoordinates(worldPosition);
            coordinateNote = "Seeded XYZ destination world coordinates.";
            return;
        }

        if (IsMapXzDestinationClassification(classification))
        {
            if (mapCoordinates.HasValue)
            {
                rule.MapCoordinates = FormatMapCoordinates(mapCoordinates.Value);
                coordinateNote = "Seeded Map XZ destination map coordinates.";
                return;
            }

            coordinateNote = "Map XZ destination selected, but current map coordinates were unavailable.";
            return;
        }

        if (!pinOrdinaryRuleToObject)
        {
            coordinateNote = "Object XYZ pin disabled.";
            return;
        }

        rule.ObjectWorldCoordinates = FormatWorldCoordinates(worldPosition);
        rule.ObjectMatchRadius = PinnedObjectRuleRadius;
        coordinateNote = "Pinned to object XYZ with 6y radius.";
    }

    public static bool IsMapXzDestinationClassification(string classification)
        => string.Equals(classification, nameof(InteractableClass.MapXzDestination), StringComparison.OrdinalIgnoreCase)
           || string.Equals(classification, nameof(InteractableClass.MapXzForceMarch), StringComparison.OrdinalIgnoreCase);

    private static bool IsXyzDestinationClassification(string classification)
        => string.Equals(classification, nameof(InteractableClass.XYZ), StringComparison.OrdinalIgnoreCase)
           || string.Equals(classification, nameof(InteractableClass.XYZForceMarch), StringComparison.OrdinalIgnoreCase);

    private static string FormatWorldCoordinates(Vector3 worldPosition)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{worldPosition.X:0.###},{worldPosition.Y:0.###},{worldPosition.Z:0.###}");

    private static string FormatMapCoordinates(Vector2 mapCoordinates)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{mapCoordinates.X:0.###},{mapCoordinates.Y:0.###}");
}
