using ADS.Models;

namespace ADS.Services;

internal readonly record struct ShopRuntimeMenuOption(
    uint HandlerId,
    int GlobalDiagnosticIndex,
    int LocalDiagnosticIndex);

internal static class ShopMenuRouteResolver
{
    internal const uint LimsaSalvagerTopicId = 3276842;
    private const uint LimsaSalvagerLevel90ShopId = 1770756;
    internal static ReadOnlySpan<uint> LimsaSalvagerFeatherShops => [1769610, 1769988, 1769989, 1770475, LimsaSalvagerLevel90ShopId];
    internal static readonly ShopCurrencyIdentity SilverFeatherCurrency = new(ShopCurrencyKind.Item, 12995);

    internal static bool IsSilverFeatherExchange(ShopOffer offer) =>
        (offer.NpcId, offer.TerritoryId) is (1006005, 128) or (1006004, 133) or (1006006, 131)
        && offer.Kind == ShopOfferKind.SpecialShopItem && LimsaSalvagerFeatherShops.Contains(offer.ShopId)
        && offer.Currencies is [{ Identity: var currency, AmountPerTransaction: 5 }] && currency == SilverFeatherCurrency;

    public static bool IsLimsaSalvagerFeatherStep(uint npcId, ShopMenuPathStep step)
        => npcId == 1006005
            && step.Kind == ShopMenuPathStepKind.TopicSelectShop
            && LimsaSalvagerFeatherShops.Contains(step.HandlerId);

    internal static bool RequiresLimsaSalvagerExchangeOpen(ShopOffer offer)
        => offer.NpcId == 1006005 && offer.TerritoryId == 128
            && offer.ShopId == LimsaSalvagerLevel90ShopId && offer.Kind == ShopOfferKind.SpecialShopItem
            && offer.CallbackPath is
            [
                { Kind: ShopMenuPathStepKind.ENpcData, HandlerId: LimsaSalvagerTopicId },
                { Kind: ShopMenuPathStepKind.TopicSelectShop, HandlerId: LimsaSalvagerLevel90ShopId },
            ];

    internal static bool MatchesLimsaSalvagerOwner(
        (ulong Character, uint Territory, ulong Entity, uint BaseId) owner,
        (ulong Character, uint Territory, ulong Entity, uint BaseId) current)
        => owner.Character != 0 && owner.Character == current.Character
            && owner.Territory == 128 && owner.Territory == current.Territory
            && owner.Entity != 0 && owner.BaseId == 1006005
            && (current.Entity == 0 || current.Entity == owner.Entity && current.BaseId == owner.BaseId);

    public static bool TryResolveLimsaSalvagerTopicIndex(
        ShopMenuPathStep? completedParent,
        (ulong Character, uint Territory, ulong Entity, uint BaseId) owner,
        (ulong Character, uint Territory, ulong Entity, uint BaseId) current,
        string? shopLabel,
        ReadOnlySpan<string> tierLabels,
        ReadOnlySpan<string> visibleLabels,
        out int index)
    {
        index = -1;
        if (completedParent is not { Kind: ShopMenuPathStepKind.ENpcData, HandlerId: LimsaSalvagerTopicId }
            || !MatchesLimsaSalvagerOwner(owner, current)
            || string.IsNullOrWhiteSpace(shopLabel)
            || tierLabels.Length != LimsaSalvagerFeatherShops.Length)
            return false;

        // The whole live tier menu identifies the submenu after the game clears the NPC target.
        var matchedRows = new HashSet<int>();
        var selectedRow = -1;
        foreach (var label in tierLabels)
        {
            if (string.IsNullOrWhiteSpace(label))
                return false;

            var expected = label.Trim();
            var match = -1;
            for (var slot = 0; slot < visibleLabels.Length; slot++)
            {
                if (!string.Equals(visibleLabels[slot]?.Trim(), expected, StringComparison.Ordinal))
                    continue;
                if (match >= 0)
                    return false;
                match = slot;
            }
            if (match < 0 || !matchedRows.Add(match))
                return false;
            if (string.Equals(expected, shopLabel.Trim(), StringComparison.Ordinal))
                selectedRow = match;
        }

        index = selectedRow;
        return index >= 0;
    }

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
