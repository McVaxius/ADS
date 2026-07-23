using ADS.Models;

namespace ADS.Services;

internal readonly record struct ShopRuntimeMenuOption(uint HandlerId, int VisibleIndex);

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
        foreach (var option in visibleOptions)
        {
            if (option.HandlerId != expected.HandlerId || option.VisibleIndex < 0)
                continue;
            matches++;
            visibleIndex = option.VisibleIndex;
        }

        if (matches == 1)
        {
            diagnostic = $"Resolved handler {expected.HandlerId} to unique live menu index {visibleIndex}; sheet index {expected.Index} was diagnostic only.";
            return true;
        }

        visibleIndex = -1;
        diagnostic = matches == 0
            ? $"Handler {expected.HandlerId} is not present in the selected NPC's live menu."
            : $"Handler {expected.HandlerId} appears {matches} times in the live menu; ADS will not guess.";
        return false;
    }
}
