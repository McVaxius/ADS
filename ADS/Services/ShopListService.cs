using System.Globalization;
using System.Text.Json;
using ADS.Models;
using Dalamud.Plugin.Services;

namespace ADS.Services;

public sealed class ShopListService
{
    private readonly ShopListPresetStore presetStore;
    private readonly ShopListImportService importService;
    private readonly UtilityAutomationService utilityAutomation;
    private readonly Func<string, string> searchCurrentCharacterItemsJson;
    private readonly IPluginLog log;
    private ShopListRetainerSnapshot retainerSnapshot = ShopListRetainerSnapshot.Empty;

    public ShopListService(
        ShopListPresetStore presetStore,
        IDataManager dataManager,
        UtilityAutomationService utilityAutomation,
        Func<string, string> searchCurrentCharacterItemsJson,
        IPluginLog log)
    {
        this.presetStore = presetStore;
        this.utilityAutomation = utilityAutomation;
        this.searchCurrentCharacterItemsJson = searchCurrentCharacterItemsJson;
        this.log = log;
        importService = new ShopListImportService(dataManager, utilityAutomation.PreviewShopPurchase);
    }

    public ShopListPresetStore PresetStore => presetStore;
    public bool OwnershipAvailable { get; private set; }
    public string OwnershipStatus { get; private set; } = "Refresh XA Database retainer holdings before running this list.";
    public string ImportStatus { get; private set; } = string.Empty;
    internal ShopListImportResult? LastImportResult { get; private set; }
    public IReadOnlyList<string> OwnershipWarnings => retainerSnapshot.Warnings;
    public DateTime OwnershipRefreshedAtUtc => retainerSnapshot.RefreshedAtUtc;

    public bool SelectPreset(string name, out string error)
    {
        var succeeded = presetStore.Select(name, out error);
        if (succeeded)
            InvalidateOwnership("Active preset changed; refresh XA Database retainer holdings before running.");
        return succeeded;
    }

    public bool CreatePreset(string name, out string error)
    {
        var succeeded = presetStore.Create(name, out error);
        if (succeeded)
            InvalidateOwnership("New preset selected; refresh XA Database retainer holdings after adding items.");
        return succeeded;
    }

    public bool RenameActivePreset(string name, out string error)
        => presetStore.RenameActive(name, out error);

    public bool DeleteActivePreset(out string error)
    {
        var succeeded = presetStore.DeleteActive(out error);
        if (succeeded)
            InvalidateOwnership("Active preset changed; refresh XA Database retainer holdings before running.");
        return succeeded;
    }

    public bool SetItem(uint itemId, int quantity, out string error)
    {
        var succeeded = presetStore.SetItem(itemId, quantity, out error);
        if (succeeded)
            InvalidateOwnership("Shop-list quantities changed; refresh XA Database retainer holdings before running.");
        return succeeded;
    }

    public bool RemoveItem(uint itemId, out string error)
    {
        var succeeded = presetStore.RemoveItem(itemId, out error);
        if (succeeded)
            InvalidateOwnership("Shop-list contents changed; refresh XA Database retainer holdings before running.");
        return succeeded;
    }

    internal bool ImportClipboard(ShopListImportSource source, string clipboard, out string status)
    {
        var result = importService.Import(source, clipboard);
        LastImportResult = result;
        if (!result.Succeeded)
        {
            ImportStatus = result.Message;
            status = ImportStatus;
            return false;
        }

        if (!presetStore.ReplaceActiveItems(result.Items, out var saveError))
        {
            ImportStatus = $"{result.Message} The parsed result could not be saved: {saveError}";
            status = ImportStatus;
            return false;
        }

        InvalidateOwnership("Imported list saved; refresh XA Database retainer holdings before running.");
        ImportStatus = result.Message;
        status = ImportStatus;
        return true;
    }

    public bool RefreshOwnership(out string status)
    {
        var itemIds = presetStore.ActivePreset.Items
            .Select(item => item.ItemId)
            .Distinct()
            .Order()
            .ToArray();
        if (itemIds.Length == 0)
            return FailOwnership("The active shop-list preset has no items to query.", out status);

        var request = JsonSerializer.Serialize(new
        {
            version = 1,
            itemIds,
            sources = new[] { "retainers" },
            includeZeroQuantity = false,
        });

        try
        {
            var response = searchCurrentCharacterItemsJson(request);
            if (string.IsNullOrWhiteSpace(response))
                return FailOwnership("XA Database returned an empty ownership response.", out status);
            if (!TryParseRetainerResponse(response, itemIds.ToHashSet(), out var parsed, out var parseError))
                return FailOwnership($"XA Database retainer ownership is unavailable: {parseError}", out status);

            retainerSnapshot = parsed;
            OwnershipAvailable = true;
            OwnershipStatus = parsed.Warnings.Count == 0
                ? $"XA Database returned current-character retainer holdings for {itemIds.Length} item(s)."
                : $"XA Database returned current-character retainer holdings with {parsed.Warnings.Count} warning(s).";
            status = OwnershipStatus;
            return true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[ADS][ShopLists] XA Database retainer lookup failed.");
            return FailOwnership($"XA Database retainer ownership lookup failed: {ex.Message}", out status);
        }
    }

    public IReadOnlyList<ShopListPreviewRow> BuildPreviewRows()
    {
        var rows = new List<ShopListPreviewRow>();
        foreach (var item in presetStore.ActivePreset.Items)
        {
            var liveQuantity = utilityAutomation.GetLiveShopItemCount(item.ItemId);
            var retainerQuantity = retainerSnapshot.Quantities.GetValueOrDefault(item.ItemId);
            var needed = Math.Max(0L, item.Quantity - Math.Max(0L, liveQuantity));
            needed = Math.Max(0L, needed - retainerQuantity);
            var purchaseQuantity = needed > int.MaxValue ? int.MaxValue : (int)needed;
            var lookupQuantity = Math.Max(1, purchaseQuantity);
            var purchasePreview = utilityAutomation.PreviewShopPurchase(item.ItemId, lookupQuantity);
            var itemName = string.IsNullOrWhiteSpace(purchasePreview.ItemName)
                ? $"Item {item.ItemId.ToString(CultureInfo.InvariantCulture)}"
                : purchasePreview.ItemName;
            var message = purchaseQuantity == 0 && purchasePreview.CanPurchase
                ? "No purchase needed; live inventory and retainer holdings meet the desired total."
                : purchasePreview.Message;

            rows.Add(new ShopListPreviewRow(
                item.ItemId,
                itemName,
                item.Quantity,
                liveQuantity,
                retainerQuantity,
                purchaseQuantity,
                retainerSnapshot.Locations.GetValueOrDefault(item.ItemId) ?? [],
                purchasePreview.SelectedOffer,
                purchasePreview.FailureCode,
                message));
        }

        return rows;
    }

    internal bool TryStartBatch(out string status)
    {
        if (!RefreshOwnership(out status))
            return false;

        var items = new List<ShopListBatchItem>();
        foreach (var item in presetStore.ActivePreset.Items)
        {
            var preview = utilityAutomation.PreviewShopPurchase(item.ItemId, Math.Max(1, item.Quantity));
            items.Add(new ShopListBatchItem(
                item.ItemId,
                string.IsNullOrWhiteSpace(preview.ItemName) ? $"Item {item.ItemId}" : preview.ItemName,
                item.Quantity,
                retainerSnapshot.Quantities.GetValueOrDefault(item.ItemId)));
        }

        if (!utilityAutomation.StartShopListBatch(items))
        {
            status = utilityAutomation.StatusMessage;
            return false;
        }

        status = utilityAutomation.StatusMessage;
        return true;
    }

    private void InvalidateOwnership(string reason)
    {
        OwnershipAvailable = false;
        OwnershipStatus = reason;
    }

    private bool FailOwnership(string message, out string status)
    {
        OwnershipAvailable = false;
        OwnershipStatus = message;
        status = message;
        return false;
    }

    private static bool TryParseRetainerResponse(
        string json,
        IReadOnlySet<uint> requestedItemIds,
        out ShopListRetainerSnapshot snapshot,
        out string error)
    {
        snapshot = ShopListRetainerSnapshot.Empty;
        error = string.Empty;

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return FailParse("response root is not an object", out error);
        if (!TryGetInt32(root, "version", out var version) || version != 1)
            return FailParse("response version is missing or unsupported", out error);
        if (!TryGetProperty(root, "ready", out var ready) || ready.ValueKind != JsonValueKind.True)
            return FailParse(ReadResponseError(root, "response is not ready"), out error);
        if (!TryGetProperty(root, "character", out var character)
            || character.ValueKind != JsonValueKind.Object
            || !TryGetUInt64(character, "contentId", out var ownerContentId)
            || ownerContentId == 0)
        {
            return FailParse("current-character Content ID is missing or invalid", out error);
        }
        if (!TryGetProperty(root, "rows", out var rows) || rows.ValueKind != JsonValueKind.Array)
            return FailParse("rows are missing or invalid", out error);
        if (!TryGetProperty(root, "warnings", out var warningsElement) || warningsElement.ValueKind != JsonValueKind.Array)
            return FailParse("warnings are missing or invalid", out error);

        var warnings = new List<string>();
        foreach (var warning in warningsElement.EnumerateArray())
        {
            if (warning.ValueKind != JsonValueKind.String)
                return FailParse("a warning entry is not a string", out error);
            var value = warning.GetString();
            if (!string.IsNullOrWhiteSpace(value))
                warnings.Add(value.Trim());
        }

        var quantities = new Dictionary<uint, long>();
        var locations = new Dictionary<uint, List<ShopListRetainerLocation>>();
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object)
                return FailParse("a retainer row is not an object", out error);
            if (!TryGetString(row, "source", out var source)
                || !source.Equals("retainer", StringComparison.OrdinalIgnoreCase))
            {
                return FailParse("a row did not identify the retainer source", out error);
            }
            if (!TryGetUInt64(row, "ownerContentId", out var rowOwner) || rowOwner != ownerContentId)
                return FailParse("a retainer row did not match the current character Content ID", out error);
            if (!TryGetUInt64(row, "retainerId", out var retainerId) || retainerId == 0)
                return FailParse("a retainer row has a missing or invalid retainer ID", out error);
            if (!TryGetString(row, "retainerName", out var retainerName) || string.IsNullOrWhiteSpace(retainerName))
                return FailParse("a retainer row has no retainer name", out error);
            if (!TryGetString(row, "containerName", out var containerName) || string.IsNullOrWhiteSpace(containerName))
                return FailParse("a retainer row has no container name", out error);
            if (!TryGetUInt32(row, "itemId", out var itemId) || !requestedItemIds.Contains(itemId))
                return FailParse("a retainer row contains an unrequested or invalid item ID", out error);
            if (!TryGetInt64(row, "quantity", out var quantity) || quantity <= 0)
                return FailParse("a retainer row has a non-positive or invalid quantity", out error);
            if (!TryGetBoolean(row, "isHq", out var isHq))
                return FailParse("a retainer row has a missing or invalid HQ flag", out error);
            if (!TryGetString(row, "lastSeenUtc", out var lastSeenUtc)
                || string.IsNullOrWhiteSpace(lastSeenUtc)
                || !DateTimeOffset.TryParse(
                    lastSeenUtc,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out _))
            {
                return FailParse("a retainer row has no last-seen timestamp", out error);
            }
            if (!TryGetString(row, "snapshotQuality", out var snapshotQuality) || string.IsNullOrWhiteSpace(snapshotQuality))
                return FailParse("a retainer row has no snapshot quality", out error);

            try
            {
                quantities[itemId] = checked(quantities.GetValueOrDefault(itemId) + quantity);
            }
            catch (OverflowException)
            {
                return FailParse($"retainer quantity overflowed for item {itemId}", out error);
            }

            if (!locations.TryGetValue(itemId, out var itemLocations))
            {
                itemLocations = [];
                locations[itemId] = itemLocations;
            }
            itemLocations.Add(new ShopListRetainerLocation(
                retainerId,
                retainerName.Trim(),
                containerName.Trim(),
                quantity,
                isHq,
                lastSeenUtc.Trim(),
                snapshotQuality.Trim()));
        }

        snapshot = new ShopListRetainerSnapshot(
            ownerContentId,
            quantities,
            locations.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<ShopListRetainerLocation>)pair.Value
                    .OrderBy(location => location.RetainerName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(location => location.ContainerName, StringComparer.OrdinalIgnoreCase)
                    .ToArray()),
            warnings,
            DateTime.UtcNow);
        return true;
    }

    private static string ReadResponseError(JsonElement root, string fallback)
    {
        if (TryGetString(root, "error", out var error) && !string.IsNullOrWhiteSpace(error))
            return error;
        if (TryGetString(root, "message", out var message) && !string.IsNullOrWhiteSpace(message))
            return message;
        return fallback;
    }

    private static bool FailParse(string message, out string error)
    {
        error = message;
        return false;
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (!TryGetProperty(element, name, out var property) || property.ValueKind != JsonValueKind.String)
            return false;
        value = property.GetString() ?? string.Empty;
        return true;
    }

    private static bool TryGetUInt32(JsonElement element, string name, out uint value)
    {
        value = 0;
        return TryGetProperty(element, name, out var property)
               && property.ValueKind == JsonValueKind.Number
               && property.TryGetUInt32(out value);
    }

    private static bool TryGetUInt64(JsonElement element, string name, out ulong value)
    {
        value = 0;
        return TryGetProperty(element, name, out var property)
               && property.ValueKind == JsonValueKind.Number
               && property.TryGetUInt64(out value);
    }

    private static bool TryGetInt32(JsonElement element, string name, out int value)
    {
        value = 0;
        return TryGetProperty(element, name, out var property)
               && property.ValueKind == JsonValueKind.Number
               && property.TryGetInt32(out value);
    }

    private static bool TryGetInt64(JsonElement element, string name, out long value)
    {
        value = 0;
        return TryGetProperty(element, name, out var property)
               && property.ValueKind == JsonValueKind.Number
               && property.TryGetInt64(out value);
    }

    private static bool TryGetBoolean(JsonElement element, string name, out bool value)
    {
        value = false;
        if (!TryGetProperty(element, name, out var property)
            || property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        value = property.ValueKind == JsonValueKind.True;
        return true;
    }
}
