using ADS.Models;

namespace ADS.Services;

internal sealed class ShopConfirmationToken
{
    private static readonly TimeSpan MaximumAge = TimeSpan.FromSeconds(5);
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
        if (!normalized.Contains(ItemName, StringComparison.CurrentCultureIgnoreCase)
            || !normalized.Contains(Quantity.ToString(System.Globalization.CultureInfo.CurrentCulture), StringComparison.CurrentCulture))
            return false;
        foreach (var amount in expectedCosts.Values)
        {
            if (!normalized.Contains(amount.ToString(System.Globalization.CultureInfo.CurrentCulture), StringComparison.CurrentCulture))
                return false;
        }
        consumed = true;
        return true;
    }

    private bool CanConsume(DateTime observedAtUtc)
        => !consumed
            && observedAtUtc >= CreatedAtUtc
            && observedAtUtc - CreatedAtUtc <= MaximumAge;
}
