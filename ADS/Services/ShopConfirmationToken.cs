using ADS.Models;

namespace ADS.Services;

internal static class ShopPurchaseTiming
{
    public static readonly TimeSpan ConfirmationAndVerificationTimeout = TimeSpan.FromSeconds(10);
}

internal sealed class ShopConfirmationToken
{
    private readonly IReadOnlyDictionary<ShopCurrencyIdentity, long> expectedCosts;
    private bool consumed;

    public ShopConfirmationToken(EvaluatedShopOffer offer, int transactions, DateTime createdAtUtc)
    {
        ItemId = offer.Offer.ReceiveItemId;
        ItemName = offer.Offer.ReceiveItemName;
        Quantity = checked((int)((long)offer.Offer.ReceiveCount * transactions));
        CreatedAtUtc = createdAtUtc;
        ShopContext = $"{offer.Offer.Kind} shopId={offer.Offer.ShopId} shopName='{offer.Offer.ShopName}'";
        DiagnosticCosts = string.Join(
            ", ",
            offer.Offer.Currencies.Select(currency =>
                $"{currency.Kind}:{currency.ItemId} '{currency.Name}'={checked((long)currency.AmountPerTransaction * transactions)}"));
        expectedCosts = offer.Offer.Currencies.ToDictionary(
            currency => currency.Identity,
            currency => checked((long)currency.AmountPerTransaction * transactions));
    }

    public uint ItemId { get; }
    public string ItemName { get; }
    public int Quantity { get; }
    public DateTime CreatedAtUtc { get; }
    public bool IsConsumed => consumed;
    public string ShopContext { get; }
    public string DiagnosticCosts { get; }
    public string DiagnosticDetails
        => $"{ShopContext}; itemId={ItemId} itemName='{ItemName}' quantity={Quantity}; totalCosts=[{DiagnosticCosts}]";

    public bool TryConsumeStructured(
        uint itemId,
        int quantity,
        IReadOnlyDictionary<ShopCurrencyIdentity, long> costs,
        DateTime observedAtUtc)
    {
        if (!CanConsume(observedAtUtc)
            || itemId != ItemId
            || quantity != Quantity
            || !expectedCosts.Keys.SetEquals(costs.Keys)
            || expectedCosts.Any(pair => !costs.TryGetValue(pair.Key, out var actual) || actual != pair.Value))
            return false;
        consumed = true;
        return true;
    }

    public bool TryConsumePrompt(string? prompt, DateTime observedAtUtc)
    {
        if (!CanConsume(observedAtUtc) || string.IsNullOrWhiteSpace(prompt))
            return false;
        var normalized = prompt.Trim();
        if (!ContainsItemName(normalized, ItemName)
            || !ContainsExactDisplayNumber(normalized, Quantity))
            return false;
        foreach (var amount in expectedCosts.Values)
        {
            if (!ContainsExactDisplayNumber(normalized, amount))
                return false;
        }
        consumed = true;
        return true;
    }

    private bool CanConsume(DateTime observedAtUtc)
        => !consumed
            && observedAtUtc >= CreatedAtUtc
            && observedAtUtc - CreatedAtUtc <= ShopPurchaseTiming.ConfirmationAndVerificationTimeout;

    private static bool ContainsExactDisplayNumber(string prompt, long value)
    {
        var display = value.ToString(System.Globalization.CultureInfo.CurrentCulture);
        var searchStart = 0;
        while (searchStart <= prompt.Length - display.Length)
        {
            var index = prompt.IndexOf(display, searchStart, StringComparison.CurrentCulture);
            if (index < 0)
                return false;
            var beforeIsDigit = index > 0 && char.IsDigit(prompt[index - 1]);
            var after = index + display.Length;
            var afterIsDigit = after < prompt.Length && char.IsDigit(prompt[after]);
            if (!beforeIsDigit && !afterIsDigit)
                return true;
            searchStart = index + display.Length;
        }

        return false;
    }

    /// <summary>
    /// Whole-word item-name match that also accepts the plural the shop confirmation actually uses.
    /// </summary>
    /// <remarks>
    /// The game pluralises the item name in the purchase prompt while the sheet name stays singular:
    ///   token 'Ragworm'    -> "Purchase 2 ragworms for 16 gil?"
    ///   token 'Plump Worm' -> "Purchase 2 plump worms for 16 gil?"
    /// ContainsExactText rejects those because the character after the match is a letter, so ADS
    /// refused to confirm its own purchase and failed with ui-mismatch. Krill hid the bug: its plural
    /// is identical to its singular.
    ///
    /// Only the item name is loosened, and only by an English plural suffix. Quantity and every
    /// currency amount are still matched exactly, so the prompt must still describe this exact
    /// transaction before ADS will dispatch Yes.
    /// </remarks>
    private static bool ContainsItemName(string prompt, string expected)
    {
        if (ContainsExactText(prompt, expected))
            return true;
        if (string.IsNullOrWhiteSpace(expected))
            return false;

        // "ragworm" -> "ragworms"; "anchovy" -> "anchovies"; "brass" -> "brasses".
        if (ContainsExactText(prompt, expected + "s"))
            return true;
        if (expected.Length > 1
            && expected.EndsWith("y", StringComparison.CurrentCultureIgnoreCase)
            && ContainsExactText(prompt, expected[..^1] + "ies"))
            return true;
        if (ContainsExactText(prompt, expected + "es"))
            return true;

        return false;
    }

    private static bool ContainsExactText(string prompt, string expected)
    {
        if (string.IsNullOrWhiteSpace(expected))
            return false;

        var searchStart = 0;
        while (searchStart <= prompt.Length - expected.Length)
        {
            var index = prompt.IndexOf(expected, searchStart, StringComparison.CurrentCultureIgnoreCase);
            if (index < 0)
                return false;
            var beforeIsWord = index > 0 && char.IsLetterOrDigit(prompt[index - 1]);
            var after = index + expected.Length;
            var afterIsWord = after < prompt.Length && char.IsLetterOrDigit(prompt[after]);
            if (!beforeIsWord && !afterIsWord)
                return true;
            searchStart = index + expected.Length;
        }

        return false;
    }
}
