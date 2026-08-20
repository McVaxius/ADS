namespace ADS.Models;

public sealed class ShopListManifest
{
    public int Version { get; set; } = 1;
    public string ActivePresetName { get; set; } = ShopListPresetStoreDefaults.DefaultPresetName;
    public List<ShopListPreset> Presets { get; set; } = [];
}

public sealed class ShopListPreset
{
    public string Name { get; set; } = string.Empty;
    public List<ShopListItem> Items { get; set; } = [];
}

public sealed class ShopListItem
{
    public uint ItemId { get; set; }
    public int Quantity { get; set; }
}

internal static class ShopListPresetStoreDefaults
{
    public const string DefaultPresetName = "DEFAULT";
}

internal enum ShopListImportSource
{
    TeamCraft,
    CraftingAsAService,
    Artisan,
}

internal sealed record ShopListImportResult(
    bool Succeeded,
    IReadOnlyDictionary<uint, int> Items,
    int ParsedRows,
    int SkippedRows,
    IReadOnlyList<string> Warnings,
    string Message);

internal sealed record ShopPurchasePreviewResult(
    ShopPurchaseRequest Request,
    string ItemName,
    ShopPurchaseOfferStatus? SelectedOffer,
    IReadOnlyList<ShopPurchaseOfferStatus> AlternativeOffers,
    string? FailureCode,
    string Message,
    int SupportedOfferCount,
    int UnsupportedOfferCount,
    int NonDivisibleOfferCount,
    int UnresolvedRouteCount)
{
    public bool CanPurchase => SelectedOffer != null && string.IsNullOrEmpty(FailureCode);

    public bool HasVendorEvidence
        => SupportedOfferCount > 0
           || UnsupportedOfferCount > 0
           || NonDivisibleOfferCount > 0
           || UnresolvedRouteCount > 0;
}

internal readonly record struct ShopOfferIdentity(string ShopKind, uint ShopId)
{
    public static ShopOfferIdentity From(ShopPurchaseOfferStatus offer)
        => new(offer.ShopKind, offer.ShopId);
}

public sealed record ShopListRetainerLocation(
    ulong RetainerId,
    string RetainerName,
    string ContainerName,
    long Quantity,
    bool IsHq,
    string LastSeenUtc,
    string SnapshotQuality);

internal sealed record ShopListRetainerSnapshot(
    ulong OwnerContentId,
    IReadOnlyDictionary<uint, long> Quantities,
    IReadOnlyDictionary<uint, IReadOnlyList<ShopListRetainerLocation>> Locations,
    IReadOnlyList<string> Warnings,
    DateTime RefreshedAtUtc)
{
    public static ShopListRetainerSnapshot Empty { get; } = new(
        0,
        new Dictionary<uint, long>(),
        new Dictionary<uint, IReadOnlyList<ShopListRetainerLocation>>(),
        [],
        DateTime.MinValue);
}

public sealed record ShopListPreviewRow(
    uint ItemId,
    string ItemName,
    int DesiredQuantity,
    long LiveInventoryQuantity,
    long RetainerQuantity,
    int PurchaseQuantity,
    IReadOnlyList<ShopListRetainerLocation> RetainerLocations,
    ShopPurchaseOfferStatus? SelectedOffer,
    string? FailureCode,
    string StatusMessage);

internal sealed record ShopListBatchItem(
    uint ItemId,
    string ItemName,
    int DesiredQuantity,
    long RetainerQuantity);

public sealed record ShopListBatchStatusSnapshot(
    bool Running,
    bool Done,
    bool? Succeeded,
    int CompletedRows,
    int TotalRows,
    uint CurrentItemId,
    string CurrentItemName,
    string StatusMessage,
    string FailureMessage,
    DateTime? CompletedAtUtc);
