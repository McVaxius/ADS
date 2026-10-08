using ADS.Models;

namespace ADS.Services;

internal readonly record struct ShopRuntimeMenuOption(
    uint HandlerId,
    int GlobalDiagnosticIndex,
    int LocalDiagnosticIndex);

internal static class ShopMenuRouteResolver
{
    public static bool TryResolveSelectorIndex(
        uint selectedNpcId,
        uint liveTargetNpcId,
        ShopMenuPathStep expected,
        ReadOnlySpan<ShopRuntimeMenuOption> selectorOptions,
        out int selectorIndex,
        out string diagnostic)
    {
        selectorIndex = -1;
        if (selectedNpcId == 0 || liveTargetNpcId != selectedNpcId)
        {
            diagnostic = $"Live event-handler target {liveTargetNpcId} does not match selected NPC {selectedNpcId}.";
            return false;
        }

        var matches = 0;
        ShopRuntimeMenuOption matchedOption = default;
        for (var index = 0; index < selectorOptions.Length; index++)
        {
            var option = selectorOptions[index];
            if (option.HandlerId != expected.HandlerId)
                continue;
            matches++;
            matchedOption = option;
            selectorIndex = index;
        }

        if (matches != 1)
        {
            selectorIndex = -1;
            diagnostic = matches == 0
                ? $"Handler {expected.HandlerId} is not present in the selected NPC's live menu."
                : $"Handler {expected.HandlerId} appears {matches} times in the live menu; ADS will not guess.";
            return false;
        }

        diagnostic =
            $"Resolved handler {expected.HandlerId} to unique selector option {selectorIndex}; "
            + $"global index {matchedOption.GlobalDiagnosticIndex}, local index {matchedOption.LocalDiagnosticIndex} "
            + $"and sheet index {expected.Index} are diagnostic only.";
        return true;
    }
}
