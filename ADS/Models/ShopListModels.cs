using System.Text.Json.Serialization;

namespace ADS.Models;

public enum ShopListMode
{
    TargetedRefill,
    SpendUntilCurrencyOrCapacity,
}

public enum ShopListOwnershipScope
{
    InventoryOnly,
    InventoryAndRetainers,
}

public sealed class ShopListManifest
{
    public int Version { get; set; } = 2;
    public Guid ActivePresetId { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ActivePresetName { get; set; }

    public List<ShopListPreset> Presets { get; set; } = [];
}

public sealed class ShopListPreset
{
    public Guid PresetId { get; set; }
    public string Name { get; set; } = string.Empty;
    public ShopListMode Mode { get; set; } = ShopListMode.TargetedRefill;
    public ShopCurrencyKind CurrencyKind { get; set; } = ShopCurrencyKind.Gil;
    public uint CurrencyItemId { get; set; } = 1;
    public long CurrencyThreshold { get; set; }
    public List<ShopListItem> Items { get; set; } = [];

    [JsonIgnore]
    public ShopCurrencyIdentity Currency => new(CurrencyKind, CurrencyItemId);
}

public sealed class ShopListItem
{
    public Guid RowId { get; set; }
    public uint ItemId { get; set; }
    public int TriggerBelow { get; set; }
    public int RefillToAtLeast { get; set; }
    public bool Repeatable { get; set; }
    public ShopListOwnershipScope OwnershipScope { get; set; } = ShopListOwnershipScope.InventoryAndRetainers;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Quantity { get; set; }
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
    Guid RowId,
    uint ItemId,
    string ItemName,
    int TriggerBelow,
    int RefillToAtLeast,
    bool Repeatable,
    string OwnershipScope,
    long LiveInventoryQuantity,
    long RetainerQuantity,
    long OwnedQuantity,
    int PurchaseQuantity,
    string Outcome,
    IReadOnlyList<ShopListRetainerLocation> RetainerLocations,
    ShopPurchaseOfferStatus? SelectedOffer,
    string? FailureCode,
    string StatusMessage);

internal sealed record ShopListBatchItem(
    Guid RowId,
    uint ItemId,
    string ItemName,
    int TriggerBelow,
    int RefillToAtLeast,
    bool Repeatable,
    ShopListOwnershipScope OwnershipScope,
    long RetainerQuantity);

internal sealed record ShopListBatchDefinition(
    string OperationId,
    Guid PresetId,
    ShopListMode Mode,
    ShopCurrencyIdentity Currency,
    long CurrencyThreshold,
    IReadOnlyList<Guid> AssociationCompletedRowIds,
    IReadOnlyList<Guid> InitiallyCompletedNonRepeatableRowIds,
    IReadOnlyList<ShopListBatchItem> Items);

public sealed record ShopListBatchRowStatus(
    Guid RowId,
    uint ItemId,
    string ItemName,
    bool Repeatable,
    string OwnershipScope,
    int TriggerBelow,
    int RefillToAtLeast,
    long OwnedQuantity,
    int RequestedQuantity,
    int PurchasedQuantity,
    string Outcome,
    string Message);

public sealed record ShopListBatchStatusSnapshot(
    int Version,
    string OperationId,
    Guid PresetId,
    bool Running,
    bool Done,
    bool? Succeeded,
    string Disposition,
    int CompletedRows,
    int TotalRows,
    uint CurrentItemId,
    string CurrentItemName,
    IReadOnlyList<Guid> CompletedNonRepeatableRowIds,
    IReadOnlyList<Guid> SkippedRowIds,
    string? FailureCode,
    string StatusMessage,
    string FailureMessage,
    IReadOnlyList<ShopListBatchRowStatus> Rows,
    DateTime? CompletedAtUtc);

public sealed record ShopListPresetSummary(
    Guid PresetId,
    string Name,
    string Mode,
    string CurrencyKind,
    uint CurrencyItemId,
    long CurrencyThreshold,
    int RowCount);

public sealed record ShopListPresetCatalogResponse(
    int Version,
    Guid ActivePresetId,
    IReadOnlyList<ShopListPresetSummary> Presets);

public sealed record ShopListPresetPreviewResponse(
    int Version,
    Guid PresetId,
    string Disposition,
    long CurrencyAvailable,
    IReadOnlyList<Guid> CompletedNonRepeatableRowIds,
    string Message,
    IReadOnlyList<ShopListPreviewRow> Rows);

public sealed record ShopListPresetStartResponse(
    int Version,
    bool Accepted,
    string OperationId,
    Guid PresetId,
    string Disposition,
    IReadOnlyList<Guid> CompletedNonRepeatableRowIds,
    string Message);

public sealed record ShopCatalogSearchRow(
    uint ItemId,
    string ItemName,
    uint ReceiveCount,
    string ShopKind,
    uint ShopId,
    string ShopName,
    int ShopRow,
    uint NpcId,
    string NpcName,
    uint TerritoryId,
    string TerritoryName,
    float X,
    float Y,
    float Z,
    string CopyableXyz,
    string CurrencyKind,
    uint CurrencyItemId,
    string CurrencyName,
    uint CurrencyCostPerTransaction);

public sealed record ShopCatalogSearchResponse(
    int Version,
    string Query,
    string CurrencyKind,
    uint CurrencyItemId,
    bool Truncated,
    IReadOnlyList<ShopCatalogSearchRow> Rows,
    string Message);
