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
        expectedCosts = offer.Offer.Currencies.ToDictionary(
            currency => currency.Identity,
            currency => checked((long)currency.AmountPerTransaction * transactions));
    }

    public uint ItemId { get; }
    public string ItemName { get; }
    public int Quantity { get; }
    public DateTime CreatedAtUtc { get; }
    public bool IsConsumed => consumed;

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
        if (!ContainsExactText(normalized, ItemName)
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
