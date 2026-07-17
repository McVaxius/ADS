using System.Numerics;
using System.Text.Json;

namespace ADS.Models;

public static class ShopPurchaseFailureCodes
{
    public const string InvalidRequest = "invalid-request";
    public const string Busy = "busy";
    public const string UnsupportedOffer = "unsupported-offer";
    public const string NoRoute = "no-route";
    public const string InsufficientCurrency = "insufficient-currency";
    public const string AmbiguousCurrency = "ambiguous-currency";
    public const string MissingDependency = "missing-dependency";
    public const string InventoryCapacity = "inventory-capacity";
    public const string UiMismatch = "ui-mismatch";
    public const string Timeout = "timeout";
    public const string Cancelled = "cancelled";
}

public readonly record struct ShopPurchaseRequest(uint ItemId, int Quantity)
{
    public const int MaximumQuantity = 9_999;

    public static bool TryCreate(uint itemId, int quantity, out ShopPurchaseRequest request, out string error)
    {
        request = default;
        if (itemId == 0)
        {
            error = "Shop item ID must be a positive decimal uint.";
            return false;
        }

        if (quantity is < 1 or > MaximumQuantity)
        {
            error = $"Shop quantity must be between 1 and {MaximumQuantity}.";
            return false;
        }

        request = new ShopPurchaseRequest(itemId, quantity);
        error = string.Empty;
        return true;
    }

    public static bool TryParseCommand(string? arguments, out ShopPurchaseRequest request, out string error)
    {
        request = default;
        var parts = (arguments ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 3 || !parts[0].Equals("shop", StringComparison.OrdinalIgnoreCase))
        {
            error = "Shop purchase must be: /ads shop <itemID> <quantity>.";
            return false;
        }

        if (!uint.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var itemId)
            || !int.TryParse(parts[2], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var quantity))
        {
            error = "Shop item ID and quantity must be decimal integers.";
            return false;
        }

        return TryCreate(itemId, quantity, out request, out error);
    }

    public static bool TryParseJson(JsonElement payload, out ShopPurchaseRequest request, out string error)
    {
        request = default;
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("itemId", out var itemElement)
            || itemElement.ValueKind != JsonValueKind.Number
            || !itemElement.TryGetUInt32(out var itemId)
            || !payload.TryGetProperty("quantity", out var quantityElement)
            || quantityElement.ValueKind != JsonValueKind.Number
            || !quantityElement.TryGetInt32(out var quantity))
        {
            error = "Shop payload must contain decimal itemId (uint) and quantity (int).";
            return false;
        }

        return TryCreate(itemId, quantity, out request, out error);
    }
}

public enum ShopOfferKind
{
    GilShop,
    SpecialShopItem,
    SpecialShopTomestone,
}

public enum ShopCurrencyKind
{
    Gil,
    Item,
    Tomestone,
}

public readonly record struct ShopCurrencyIdentity(ShopCurrencyKind Kind, uint ItemId);

public sealed record ShopCurrencyCost(
    ShopCurrencyKind Kind,
    uint ItemId,
    string Name,
    uint AmountPerTransaction)
{
    public ShopCurrencyIdentity Identity => new(Kind, ItemId);
}

internal enum ShopSheetKind
{
    Gil,
    Special,
}

internal enum ShopNpcEventKind
{
    Unsupported,
    GilShop,
    SpecialShop,
    TopicSelect,
    PreHandler,
}

internal enum ShopNpcLinkKind
{
    DirectShop,
    TopicSelectShop,
    DirectPreHandler,
    TopicSelectPreHandler,
}

internal enum ShopMenuPathStepKind
{
    ENpcData,
    TopicSelectShop,
}

internal enum ShopNpcPlacementSource
{
    Lgb,
    Level,
    OfflineCatalog,
}

internal sealed record ShopItemSheetRow(
    uint ItemId,
    string Name,
    uint StackSize,
    uint PriceMid,
    bool IsUnique);

internal sealed record GilShopSheetRow(
    uint ShopId,
    string ShopName,
    int RowIndex,
    uint ItemId,
    bool IsHq,
    IReadOnlyList<uint> RequiredQuestIds,
    bool HasUnknownGate);

internal sealed record SpecialShopCostSheetRow(
    uint ItemOrCurrencyId,
    uint Amount,
    ushort Collectability,
    byte CostType);

internal sealed record SpecialShopSheetRow(
    uint ShopId,
    string ShopName,
    int RowIndex,
    IReadOnlyList<(uint ItemId, uint Count, bool IsHq)> ReceiveItems,
    IReadOnlyList<SpecialShopCostSheetRow> Costs,
    byte UseCurrencyType,
    IReadOnlyList<uint> RequiredQuestIds,
    bool HasUnknownGate);

internal readonly record struct ShopNpcEventReference(ShopNpcEventKind Kind, uint RowId);

internal sealed record ShopNpcEventSheetRow(
    uint NpcId,
    string NpcName,
    IReadOnlyList<ShopNpcEventReference> Events);

internal sealed record ShopTopicSelectSheetRow(
    uint TopicSelectId,
    IReadOnlyList<ShopNpcEventReference> Shops);

internal sealed record ShopPreHandlerSheetRow(
    uint PreHandlerId,
    ShopNpcEventReference Target,
    uint UnlockQuestId,
    bool HasUnknownGate);

internal readonly record struct ShopMenuPathStep(
    ShopMenuPathStepKind Kind,
    int Index,
    uint HandlerId);

internal sealed record ShopNpcSheetLink(
    ShopSheetKind ShopKind,
    uint ShopId,
    uint NpcId,
    string NpcName,
    IReadOnlyList<ShopMenuPathStep> CallbackPath,
    ShopNpcLinkKind LinkKind,
    IReadOnlyList<uint> RequiredQuestIds,
    bool HasUnknownGate)
{
    public IReadOnlyList<int> MenuPath { get; } = CallbackPath.Select(step => step.Index).ToArray();

    public bool ViaTopicSelect
        => LinkKind is ShopNpcLinkKind.TopicSelectShop or ShopNpcLinkKind.TopicSelectPreHandler;
}

internal sealed record ShopNpcPlacementSheetRow(
    uint NpcId,
    uint TerritoryId,
    string TerritoryName,
    Vector3 Position,
    uint LevelId,
    ShopNpcPlacementSource Source = ShopNpcPlacementSource.Level,
    uint MapId = 0,
    string SourcePath = "",
    bool RequiresFloorResolution = false,
    bool ReplacesExisting = false);

internal sealed record ShopTerritorySheetRow(
    uint TerritoryId,
    string TerritoryName,
    uint MapId,
    string BackgroundPath);

internal readonly record struct ShopLgbInstanceObjectSheetRow(
    bool IsEventNpc,
    uint BaseId,
    Vector3 Position);

internal sealed record ShopNpcPlacementBuildResult(
    IReadOnlyList<ShopNpcPlacementSheetRow> Placements,
    int LgbCollectedCount,
    int LevelCollectedCount,
    int UnmappedLgbCount,
    int UnlinkedNpcCount,
    int NearDuplicateCount,
    int OfflineCatalogCount = 0,
    int ReplacementCount = 0);

internal sealed record ShopAetheryteSheetRow(
    uint AetheryteId,
    string Name,
    uint TerritoryId,
    Vector3 Position);

internal sealed record ShopAetheryteSourceRow(
    uint AetheryteId,
    string Name,
    uint TerritoryId,
    uint MapId,
    bool IsAetheryte);

internal sealed record ShopMapSheetRow(
    uint MapId,
    uint TerritoryId,
    ushort MapMarkerRange,
    ushort SizeFactor,
    short OffsetX,
    short OffsetY);

internal sealed record ShopMapMarkerSheetRow(
    uint MapMarkerRange,
    ushort SubrowId,
    byte DataType,
    uint DataKey,
    short X,
    short Y);

internal sealed record ShopAetheryteBuildResult(
    IReadOnlyList<ShopAetheryteSheetRow> Routes,
    int MappedCount,
    int RejectedCount,
    int MissingCount);

internal sealed record ShopTomestoneSheetRow(
    uint TomestoneId,
    uint ItemId,
    string Name);

internal sealed record ShopCatalogSnapshot(
    IReadOnlyDictionary<uint, ShopItemSheetRow> Items,
    IReadOnlyList<GilShopSheetRow> GilShopRows,
    IReadOnlyList<SpecialShopSheetRow> SpecialShopRows,
    IReadOnlyList<ShopNpcSheetLink> NpcLinks,
    IReadOnlyList<ShopNpcPlacementSheetRow> NpcPlacements,
    IReadOnlyList<ShopAetheryteSheetRow> Aetherytes,
    IReadOnlyList<ShopTomestoneSheetRow> Tomestones);

internal sealed record ShopRouteCandidate(
    uint AetheryteId,
    string AetheryteName,
    Vector3 Position,
    float DistanceToNpc);

internal sealed record ShopOffer(
    ShopOfferKind Kind,
    uint ShopId,
    string ShopName,
    int SheetRowIndex,
    uint NpcId,
    string NpcName,
    uint TerritoryId,
    string TerritoryName,
    Vector3 NpcPosition,
    IReadOnlyList<ShopMenuPathStep> CallbackPath,
    ShopNpcLinkKind LinkKind,
    ShopNpcPlacementSource PlacementSource,
    uint PlacementMapId,
    uint PlacementLevelId,
    string PlacementSourcePath,
    uint ReceiveItemId,
    string ReceiveItemName,
    uint ReceiveCount,
    int TransactionsRequired,
    IReadOnlyList<ShopCurrencyCost> Currencies,
    IReadOnlyList<uint> RequiredQuestIds,
    bool HasUnknownGate,
    IReadOnlyList<ShopRouteCandidate> AetheryteRoutes,
    bool RequiresFloorResolution = false)
{
    public IReadOnlyList<int> MenuPath { get; } = CallbackPath.Select(step => step.Index).ToArray();

    public bool ViaTopicSelect
        => LinkKind is ShopNpcLinkKind.TopicSelectShop or ShopNpcLinkKind.TopicSelectPreHandler;
}

internal sealed record ShopCatalogResolution(
    uint ItemId,
    string ItemName,
    uint StackSize,
    bool IsUnique,
    int RequestedQuantity,
    IReadOnlyList<ShopOffer> Offers,
    int UnsupportedOfferCount,
    int NonDivisibleOfferCount,
    int UnresolvedRouteCount);

internal sealed record ResolvedShopRoute(
    uint TerritoryId,
    string TerritoryName,
    Vector3 NpcPosition,
    bool RequiresTeleport,
    uint AetheryteId,
    string AetheryteName,
    float RouteDistance,
    bool RequiresFloorResolution = false);

internal sealed record EvaluatedShopCurrency(
    ShopCurrencyCost Currency,
    long RequiredAmount,
    long AvailableAmount);

internal sealed record EvaluatedShopOffer(
    ShopOffer Offer,
    ResolvedShopRoute? Route,
    IReadOnlyList<EvaluatedShopCurrency> Currencies,
    bool GateSatisfied,
    bool Affordable,
    bool CapacitySatisfied,
    string? EvaluationFailureCode);

internal sealed record ShopOfferSelectionResult(
    EvaluatedShopOffer? Selected,
    IReadOnlyList<EvaluatedShopOffer> Alternatives,
    IReadOnlyList<EvaluatedShopOffer> IdenticalCostFallbacks,
    string? FailureCode,
    string Message);

public sealed record ShopPurchaseCurrencyStatus(
    string Kind,
    uint ItemId,
    string Name,
    long RequiredAmount,
    long AvailableAmount);

public sealed record ShopPurchaseOfferStatus(
    string ShopKind,
    uint ShopId,
    string ShopName,
    int Row,
    uint NpcId,
    string NpcName,
    uint TerritoryId,
    string TerritoryName,
    uint ReceiveCount,
    IReadOnlyList<ShopPurchaseCurrencyStatus> Currencies);

public sealed record ShopPurchaseStatusSnapshot(
    bool Running,
    bool Done,
    bool? Succeeded,
    string Phase,
    uint ItemId,
    string ItemName,
    int RequestedQuantity,
    int AcquiredQuantity,
    int RemainingQuantity,
    ShopPurchaseOfferStatus? SelectedOffer,
    IReadOnlyList<ShopPurchaseOfferStatus> AlternativeOffers,
    string? FailureCode,
    string StatusMessage,
    string SuccessMessage,
    string FailureMessage,
    string LastStartError,
    DateTime? CompletedAtUtc);
