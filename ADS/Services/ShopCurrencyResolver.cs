using ADS.Models;

namespace ADS.Services;

internal static class ShopCurrencyResolver
{
    private static readonly IReadOnlyDictionary<uint, uint> VirtualCurrencyItems =
        new Dictionary<uint, uint>
        {
            [1] = 10309, [2] = 33913, [3] = 10311, [4] = 33914,
            [5] = 10307, [6] = 41784, [7] = 41785,
            [8] = 21072, [9] = 21073, [10] = 21074, [11] = 21075,
            [12] = 21076, [13] = 21077, [14] = 21078, [15] = 21079,
            [16] = 21080, [17] = 21081, [18] = 21172, [19] = 21173,
            [20] = 21935, [21] = 22525, [22] = 26533, [23] = 26807,
            [24] = 28063, [25] = 28186, [26] = 28187, [27] = 28188,
            [28] = 30341,
        };

    public static bool TryResolveSpecialShopCost(
        IReadOnlyDictionary<uint, ShopItemSheetRow> items,
        IReadOnlyDictionary<uint, ShopTomestoneSheetRow> tomestones,
        uint shopId,
        byte useCurrencyType,
        SpecialShopCostSheetRow cost,
        out ShopCurrencyCost resolved)
    {
        resolved = default!;
        if (cost.Amount == 0 || cost.ItemOrCurrencyId == 0 || cost.Collectability != 0)
            return false;

        if (cost.CostType == 0)
        {
            if (!items.TryGetValue(cost.ItemOrCurrencyId, out var item))
                return false;
            resolved = new ShopCurrencyCost(ShopCurrencyKind.Item, item.ItemId, item.Name, cost.Amount);
            return true;
        }

        if (cost.CostType is not (1 or 2 or 3))
            return false;

        if (!TryConvertCurrencyId(shopId, cost.ItemOrCurrencyId, useCurrencyType, tomestones, out var itemId, out var isTomestone))
            return false;
        var name = isTomestone
            ? tomestones.Values.FirstOrDefault(row => row.ItemId == itemId)?.Name
            : items.TryGetValue(itemId, out var currencyItem) ? currencyItem.Name : null;
        if (string.IsNullOrWhiteSpace(name))
            return false;

        resolved = new ShopCurrencyCost(
            ClassifyRuntimeSource(itemId, isTomestone),
            itemId,
            name,
            cost.Amount);
        return true;
    }

    internal static bool TryConvertCurrencyId(
        uint shopId,
        uint rawId,
        byte useCurrencyType,
        IReadOnlyDictionary<uint, ShopTomestoneSheetRow> tomestones,
        out uint itemId,
        out bool isTomestone)
    {
        itemId = 0;
        isTomestone = false;

        // These audited shops encode otherwise identical columns differently.
        if (shopId == 1_770_637)
            return TryVirtual(rawId, out itemId);

        if (shopId == 1_770_446
            || (shopId == 1_770_699 && rawId < 10)
            || (shopId == 1_770_803 && rawId < 10))
        {
            if (TryTomestone(rawId, tomestones, out itemId))
            {
                isTomestone = true;
                return true;
            }
            return TryVirtual(rawId, out itemId);
        }

        if (useCurrencyType == 16 && rawId != 25 && TryVirtual(rawId, out itemId))
            return true;
        if (useCurrencyType == 2 && rawId < 10)
        {
            isTomestone = TryTomestone(rawId, tomestones, out itemId);
            return isTomestone;
        }
        if ((useCurrencyType == 16 || useCurrencyType == 4) && rawId < 10)
        {
            if (TryTomestone(rawId, tomestones, out itemId))
            {
                isTomestone = true;
                return true;
            }
            return TryVirtual(rawId, out itemId);
        }

        // A non-virtual value is a real item ID only when it exists in Item.
        itemId = rawId;
        return itemId != 0;
    }

    private static bool TryTomestone(
        uint rawId,
        IReadOnlyDictionary<uint, ShopTomestoneSheetRow> tomestones,
        out uint itemId)
    {
        if (tomestones.TryGetValue(rawId, out var tomestone))
        {
            itemId = tomestone.ItemId;
            return true;
        }
        itemId = 0;
        return false;
    }

    private static bool TryVirtual(uint rawId, out uint itemId)
        => VirtualCurrencyItems.TryGetValue(rawId, out itemId);

    internal static ShopCurrencyKind ClassifyRuntimeSource(uint itemId, bool isTomestone)
        => isTomestone
            ? ShopCurrencyKind.Tomestone
            : itemId switch
            {
                20 or 21 or 22 => ShopCurrencyKind.CompanySeal,
                29 => ShopCurrencyKind.Mgp,
                25 => ShopCurrencyKind.WolfMark,
                27 => ShopCurrencyKind.AlliedSeal,
                _ => ShopCurrencyKind.CurrencyManager,
            };
}
