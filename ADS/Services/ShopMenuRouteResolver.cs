using ADS.Models;

namespace ADS.Services;

internal readonly record struct ShopRuntimeMenuOption(
    uint HandlerId,
    int GlobalCallbackIndex,
    int LocalDiagnosticIndex);

internal static class ShopMenuRouteResolver
{
    public static bool TryResolveVisibleIndex(
        uint selectedNpcId,
        uint liveTargetNpcId,
        ShopMenuPathStep expected,
        ReadOnlySpan<ShopRuntimeMenuOption> visibleOptions,
        out int visibleIndex,
        out string diagnostic)
    {
        visibleIndex = -1;
        if (selectedNpcId == 0 || liveTargetNpcId != selectedNpcId)
        {
            diagnostic = $"Live event-handler target {liveTargetNpcId} does not match selected NPC {selectedNpcId}.";
            return false;
        }

        var matches = 0;
        ShopRuntimeMenuOption matchedOption = default;
        foreach (var option in visibleOptions)
        {
            if (option.HandlerId != expected.HandlerId)
                continue;
            matches++;
            matchedOption = option;
        }

        if (matches != 1)
        {
            diagnostic = matches == 0
                ? $"Handler {expected.HandlerId} is not present in the selected NPC's live menu."
                : $"Handler {expected.HandlerId} appears {matches} times in the live menu; ADS will not guess.";
            return false;
        }

        if (matchedOption.GlobalCallbackIndex < 0
            || matchedOption.GlobalCallbackIndex >= visibleOptions.Length)
        {
            diagnostic =
                $"Handler {expected.HandlerId} exposed invalid global callback index {matchedOption.GlobalCallbackIndex} "
                + $"for {visibleOptions.Length} live options; local index {matchedOption.LocalDiagnosticIndex} "
                + $"and sheet index {expected.Index} are diagnostic only.";
            return false;
        }

        visibleIndex = matchedOption.GlobalCallbackIndex;
        diagnostic =
            $"Resolved handler {expected.HandlerId} to unique global callback index {visibleIndex}; "
            + $"local index {matchedOption.LocalDiagnosticIndex} and sheet index {expected.Index} were diagnostic only.";
        return true;
    }
}
