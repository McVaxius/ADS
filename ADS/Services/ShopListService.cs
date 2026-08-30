using System.Globalization;
using System.Text.Json;
using ADS.Models;
using Dalamud.Plugin.Services;

namespace ADS.Services;

public sealed class ShopListService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly ShopListPresetStore presetStore;
    private readonly ShopListImportService importService;
    private readonly UtilityAutomationService utilityAutomation;
    private readonly Func<string, string> searchCurrentCharacterItemsJson;
    private readonly IPluginLog log;
    private ShopListRetainerSnapshot retainerSnapshot = ShopListRetainerSnapshot.Empty;
    private Guid ownershipPresetId;

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

    public bool ConfigureActivePreset(
        ShopListMode mode,
        ShopCurrencyKind currencyKind,
        uint currencyItemId,
        long currencyThreshold,
        out string error)
    {
        var succeeded = presetStore.ConfigureActive(mode, currencyKind, currencyItemId, currencyThreshold, out error);
        if (succeeded)
            InvalidateOwnership("Preset currency settings changed; test the preset before running.");
        return succeeded;
    }

    public bool DeleteActivePreset(out string error)
    {
        var succeeded = presetStore.DeleteActive(out error);
        if (succeeded)
            InvalidateOwnership("Active preset changed; refresh XA Database retainer holdings before running.");
        return succeeded;
    }

    public bool SetItem(
        uint itemId,
        int triggerBelow,
        int refillToAtLeast,
        bool repeatable,
        ShopListOwnershipScope ownershipScope,
        out string error)
    {
        var succeeded = presetStore.SetItem(
            itemId,
            triggerBelow,
            refillToAtLeast,
            repeatable,
            ownershipScope,
            out error);
        if (succeeded)
            InvalidateOwnership("Shop-list row changed; test the preset before running.");
        return succeeded;
    }

    public bool UpdateItem(
        Guid rowId,
        int triggerBelow,
        int refillToAtLeast,
        bool repeatable,
        ShopListOwnershipScope ownershipScope,
        out string error)
    {
        var succeeded = presetStore.UpdateItem(
            rowId,
            triggerBelow,
            refillToAtLeast,
            repeatable,
            ownershipScope,
            out error);
        if (succeeded)
            InvalidateOwnership("Shop-list row changed; test the preset before running.");
        return succeeded;
    }

    public bool RemoveItem(Guid rowId, out string error)
    {
        var succeeded = presetStore.RemoveItem(rowId, out error);
        if (succeeded)
            InvalidateOwnership("Shop-list contents changed; refresh XA Database retainer holdings before running.");
        return succeeded;
    }

    public string ExportActivePresetBase64()
        => presetStore.ExportActiveBase64();

    public bool ImportPresetBase64(string encoded, out string error)
    {
        var succeeded = presetStore.ImportBase64(encoded, out error);
        if (succeeded)
            InvalidateOwnership("Imported preset selected; test it before running.");
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
        => RefreshOwnership(presetStore.ActivePreset, out status);

    private bool RefreshOwnership(ShopListPreset preset, out string status)
    {
        var itemIds = preset.Items
            .Where(item => item.OwnershipScope == ShopListOwnershipScope.InventoryAndRetainers)
            .Select(item => item.ItemId)
            .Distinct()
            .ToArray();
        if (itemIds.Length == 0)
        {
            retainerSnapshot = ShopListRetainerSnapshot.Empty;
            ownershipPresetId = preset.PresetId;
            OwnershipAvailable = true;
            OwnershipStatus = "This preset uses inventory-only ownership; XA Database was not queried.";
            status = OwnershipStatus;
            return true;
        }

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
            ownershipPresetId = preset.PresetId;
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
        => BuildPreviewRows(presetStore.ActivePreset, new HashSet<Guid>());

    private IReadOnlyList<ShopListPreviewRow> BuildPreviewRows(
        ShopListPreset preset,
        IReadOnlySet<Guid> associationCompletedRowIds)
    {
        var rows = new List<ShopListPreviewRow>();
        var spendPurchasePlanned = false;
        foreach (var item in preset.Items)
        {
            var liveQuantity = utilityAutomation.GetLiveShopItemCount(item.ItemId);
            var usesRetainers = item.OwnershipScope == ShopListOwnershipScope.InventoryAndRetainers;
            var retainerQuantity = usesRetainers && ownershipPresetId == preset.PresetId
                ? retainerSnapshot.Quantities.GetValueOrDefault(item.ItemId)
                : 0;
            var owned = Math.Max(0L, liveQuantity) + Math.Max(0L, retainerQuantity);
            var associationCompleted = !item.Repeatable && associationCompletedRowIds.Contains(item.RowId);
            ShopPurchasePreviewResult purchasePreview;
            var purchaseQuantity = 0;
            var outcome = "pending";
            string message;
            string? failureCode = null;

            if (associationCompleted)
            {
                purchasePreview = utilityAutomation.PreviewShopPurchase(item.ItemId, 1, preset.Currency);
                outcome = "association-completed";
                message = "This exact association already completed the non-repeatable row.";
            }
            else if (preset.Mode == ShopListMode.TargetedRefill && owned >= item.TriggerBelow)
            {
                purchasePreview = utilityAutomation.PreviewShopPurchase(item.ItemId, 1, preset.Currency);
                outcome = "already-satisfied";
                message = $"Owned quantity {owned} is not below trigger {item.TriggerBelow}.";
            }
            else if (preset.Mode == ShopListMode.SpendUntilCurrencyOrCapacity && spendPurchasePlanned)
            {
                purchasePreview = utilityAutomation.PreviewAnyShopPurchaseBundle(item.ItemId, preset.Currency);
                failureCode = purchasePreview.FailureCode;
                if (!purchasePreview.CanPurchase
                    && failureCode is not (ShopPurchaseFailureCodes.InsufficientCurrency
                        or ShopPurchaseFailureCodes.InventoryCapacity))
                {
                    outcome = "failed";
                    message = purchasePreview.Message;
                }
                else
                {
                    outcome = "deferred";
                    failureCode = null;
                    message = "Structurally valid; deferred until execution because an earlier stored-order row has first claim on currency and capacity.";
                }
            }
            else
            {
                purchasePreview = preset.Mode == ShopListMode.TargetedRefill
                    ? utilityAutomation.PreviewShopPurchaseAtLeast(
                        item.ItemId,
                        checked((int)(item.RefillToAtLeast - owned)),
                        preset.Currency)
                    : utilityAutomation.PreviewMaximumShopPurchase(item.ItemId, preset.Currency);
                purchaseQuantity = purchasePreview.CanPurchase ? purchasePreview.Request.Quantity : 0;
                failureCode = purchasePreview.FailureCode;
                if (purchasePreview.CanPurchase)
                {
                    outcome = "pending";
                    if (preset.Mode == ShopListMode.SpendUntilCurrencyOrCapacity)
                        spendPurchasePlanned = true;
                    message = preset.Mode == ShopListMode.TargetedRefill
                        ? $"Would buy {purchaseQuantity} to refill from {owned} to at least {item.RefillToAtLeast}; vendor bundles may exceed the target."
                        : $"First eligible stored-order row would buy up to {purchaseQuantity} whole-bundle item(s); later rows are evaluated only after its verified result.";
                }
                else if (preset.Mode == ShopListMode.SpendUntilCurrencyOrCapacity
                         && failureCode is ShopPurchaseFailureCodes.InsufficientCurrency
                             or ShopPurchaseFailureCodes.InventoryCapacity)
                {
                    outcome = "skipped";
                    message = purchasePreview.Message;
                }
                else
                {
                    outcome = "failed";
                    message = purchasePreview.Message;
                }
            }

            var itemName = string.IsNullOrWhiteSpace(purchasePreview.ItemName)
                ? $"Item {item.ItemId.ToString(CultureInfo.InvariantCulture)}"
                : purchasePreview.ItemName;

            rows.Add(new ShopListPreviewRow(
                item.RowId,
                item.ItemId,
                itemName,
                item.TriggerBelow,
                item.RefillToAtLeast,
                item.Repeatable,
                OwnershipScopeName(item.OwnershipScope),
                liveQuantity,
                retainerQuantity,
                owned,
                purchaseQuantity,
                outcome,
                usesRetainers && ownershipPresetId == preset.PresetId
                    ? retainerSnapshot.Locations.GetValueOrDefault(item.ItemId) ?? []
                    : [],
                purchaseQuantity > 0 ? purchasePreview.SelectedOffer : null,
                failureCode,
                message));
        }

        return rows;
    }

    internal bool TryStartBatch(out string status)
    {
        var request = new ShopListPresetStartRequest
        {
            Version = 1,
            OperationId = Guid.NewGuid().ToString("D"),
            PresetId = presetStore.ActivePresetId,
            CompletedRowIds = [],
        };
        var response = StartPreset(request);
        status = response.Message;
        return response.Accepted;
    }

    internal ShopListPresetPreviewResponse PreviewActivePreset()
    {
        var preset = presetStore.ActivePreset;
        if (!RefreshOwnership(preset, out var ownershipStatus))
        {
            return new ShopListPresetPreviewResponse(
                1,
                preset.PresetId,
                "error",
                -1,
                [],
                ownershipStatus,
                []);
        }

        return EvaluatePreset(preset, new HashSet<Guid>());
    }

    public string GetShopListPresetsJson()
        => JsonSerializer.Serialize(new ShopListPresetCatalogResponse(
            1,
            presetStore.ActivePresetId,
            presetStore.Presets.Select(preset => new ShopListPresetSummary(
                preset.PresetId,
                preset.Name,
                ModeName(preset.Mode),
                ShopOfferSelector.CurrencyKindName(preset.CurrencyKind),
                preset.CurrencyItemId,
                preset.CurrencyThreshold,
                preset.Items.Count)).ToArray()), JsonOptions);

    public string PreviewShopListPresetJson(string requestJson)
    {
        if (!TryParsePresetRequest(requestJson, requireOperationId: false, out var request, out var error))
            return SerializePreviewError(request?.PresetId ?? Guid.Empty, error);
        if (!TryGetPreset(request!.PresetId, out var preset))
            return SerializePreviewError(request.PresetId, $"Shop-list preset '{request.PresetId:D}' was not found.");
        if (!TryValidateCompletedRows(preset, request.CompletedRowIds, out var completedRows, out error))
            return SerializePreviewError(preset.PresetId, error);
        if (!RefreshOwnership(preset, out var ownershipStatus))
            return SerializePreviewError(preset.PresetId, ownershipStatus);

        var preview = EvaluatePreset(preset, completedRows);
        return JsonSerializer.Serialize(preview, JsonOptions);
    }

    public string StartShopListPresetJson(string requestJson)
    {
        if (!TryParsePresetRequest(requestJson, requireOperationId: true, out var request, out var error))
            return SerializeStart(new ShopListPresetStartResponse(
                1, false, request?.OperationId ?? string.Empty, request?.PresetId ?? Guid.Empty, "error", [], error));
        return SerializeStart(StartPreset(request!));
    }

    public string RejectShopListPresetStartJson(string requestJson, string message)
    {
        TryParsePresetRequest(requestJson, requireOperationId: true, out var request, out _);
        return SerializeStart(new ShopListPresetStartResponse(
            1,
            false,
            request?.OperationId ?? string.Empty,
            request?.PresetId ?? Guid.Empty,
            "error",
            [],
            message));
    }

    public string GetShopListPresetStatusJson(string operationId)
    {
        var normalized = operationId?.Trim() ?? string.Empty;
        var status = utilityAutomation.ShopListBatchStatus;
        if (!string.IsNullOrEmpty(normalized)
            && string.Equals(status.OperationId, normalized, StringComparison.Ordinal))
        {
            return JsonSerializer.Serialize(status, JsonOptions);
        }

        return JsonSerializer.Serialize(new ShopListBatchStatusSnapshot(
            1,
            normalized,
            Guid.Empty,
            false,
            false,
            null,
            "operation-not-found",
            0,
            0,
            0,
            string.Empty,
            [],
            [],
            "operation-not-found",
            "No shop-list preset operation with that exact operation ID is active or retained.",
            "No matching operation was found.",
            [],
            null), JsonOptions);
    }

    public bool CancelShopListPreset(string operationId)
        => utilityAutomation.CancelShopListPreset(operationId);

    public string SearchShopCatalogJson(string requestJson)
    {
        try
        {
            var request = JsonSerializer.Deserialize<ShopCatalogSearchRequest>(requestJson, JsonOptions)
                          ?? throw new InvalidDataException("Catalog search request was empty.");
            if (request.Version != 1)
                throw new InvalidDataException("Catalog search request version must be 1.");
            ShopCurrencyIdentity? currency = null;
            if (!string.IsNullOrWhiteSpace(request.CurrencyKind))
            {
                if (!TryParseCurrencyKind(request.CurrencyKind, out var currencyKind))
                    throw new InvalidDataException("Catalog search currencyKind is unsupported.");
                ValidateCurrency(currencyKind, request.CurrencyItemId);
                currency = new ShopCurrencyIdentity(currencyKind, request.CurrencyItemId);
            }
            else if (request.CurrencyItemId != 0)
            {
                throw new InvalidDataException("currencyItemId must be zero when currencyKind is omitted for discovery search.");
            }
            var response = utilityAutomation.SearchShopCatalog(
                request.Query,
                currency,
                request.Limit is < 1 or > 100 ? 50 : request.Limit);
            return JsonSerializer.Serialize(response, JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            return JsonSerializer.Serialize(new ShopCatalogSearchResponse(
                1, string.Empty, string.Empty, 0, false, [], $"Catalog search failed safely: {ex.Message}"), JsonOptions);
        }
    }

    private ShopListPresetStartResponse StartPreset(ShopListPresetStartRequest request)
    {
        if (!TryGetPreset(request.PresetId, out var preset))
            return StartError(request, $"Shop-list preset '{request.PresetId:D}' was not found.");
        if (!TryValidateCompletedRows(preset, request.CompletedRowIds, out var completedRows, out var error))
            return StartError(request, error);
        var retained = utilityAutomation.ShopListBatchStatus;
        if (!string.IsNullOrEmpty(retained.OperationId)
            && string.Equals(retained.OperationId, request.OperationId, StringComparison.Ordinal))
        {
            return StartError(request, "That operation ID has already been used; ADS will not replay it.");
        }
        if (utilityAutomation.IsRunning)
            return StartError(request, $"Another ADS utility is active: {utilityAutomation.StatusMessage}");
        if (!RefreshOwnership(preset, out var ownershipStatus))
            return StartError(request, ownershipStatus);

        var preview = EvaluatePreset(preset, completedRows);
        if (!string.Equals(preview.Disposition, "ready", StringComparison.Ordinal))
        {
            if (preview.Disposition is "fulfilled" or "not-triggered")
            {
                if (!utilityAutomation.RetainShopListPresetNoOp(
                    request.OperationId,
                    preset.PresetId,
                    preview.Disposition,
                    preview.Message,
                    preview.Rows,
                    preview.CompletedNonRepeatableRowIds))
                {
                    return StartError(request, "Another ADS utility became active before the no-op result could be retained.");
                }
            }
            return new ShopListPresetStartResponse(
                1,
                false,
                request.OperationId,
                preset.PresetId,
                preview.Disposition,
                preview.CompletedNonRepeatableRowIds,
                preview.Message);
        }

        var rowsById = preview.Rows.ToDictionary(row => row.RowId);
        var definition = new ShopListBatchDefinition(
            request.OperationId,
            preset.PresetId,
            preset.Mode,
            preset.Currency,
            preset.CurrencyThreshold,
            completedRows.ToArray(),
            preview.CompletedNonRepeatableRowIds,
            preset.Items.Select(item =>
            {
                var previewRow = rowsById[item.RowId];
                return new ShopListBatchItem(
                    item.RowId,
                    item.ItemId,
                    previewRow.ItemName,
                    item.TriggerBelow,
                    item.RefillToAtLeast,
                    item.Repeatable,
                    item.OwnershipScope,
                    item.OwnershipScope == ShopListOwnershipScope.InventoryAndRetainers
                        ? previewRow.RetainerQuantity
                        : 0);
            }).ToArray());

        if (!utilityAutomation.StartShopListBatch(definition))
            return StartError(request, utilityAutomation.StatusMessage);

        return new ShopListPresetStartResponse(
            1,
            true,
            request.OperationId,
            preset.PresetId,
            "started",
            preview.CompletedNonRepeatableRowIds,
            utilityAutomation.StatusMessage);
    }

    private ShopListPresetPreviewResponse EvaluatePreset(
        ShopListPreset preset,
        IReadOnlySet<Guid> completedRows)
    {
        if (preset.Items.Count == 0)
        {
            return new ShopListPresetPreviewResponse(
                1,
                preset.PresetId,
                "error",
                -1,
                [],
                "The exact preset has no rows; ADS will not treat an empty list as fulfilled.",
                []);
        }

        var rows = BuildPreviewRows(preset, completedRows);
        var completedNonRepeatableRowIds = rows
            .Where(row => !row.Repeatable
                          && row.Outcome is "association-completed" or "already-satisfied")
            .Select(row => row.RowId)
            .ToArray();
        var available = utilityAutomation.GetAvailableShopCurrency(preset.Currency);
        if (available < 0)
        {
            return new ShopListPresetPreviewResponse(
                1, preset.PresetId, "error", available,
                completedNonRepeatableRowIds,
                "The exact selected currency balance is unavailable; ADS will not guess.", rows);
        }

        if (available < preset.CurrencyThreshold)
        {
            return new ShopListPresetPreviewResponse(
                1,
                preset.PresetId,
                "not-triggered",
                available,
                completedNonRepeatableRowIds,
                $"Selected currency balance {available} has not reached trigger {preset.CurrencyThreshold}.",
                rows);
        }

        var failed = rows.FirstOrDefault(row => row.Outcome == "failed");
        if (failed != null)
        {
            return new ShopListPresetPreviewResponse(
                1,
                preset.PresetId,
                "error",
                available,
                completedNonRepeatableRowIds,
                $"{failed.ItemName}: {failed.StatusMessage}",
                rows);
        }

        var ready = rows.Any(row => row.Outcome == "pending" && row.PurchaseQuantity > 0);
        return new ShopListPresetPreviewResponse(
            1,
            preset.PresetId,
            ready ? "ready" : "fulfilled",
            available,
            completedNonRepeatableRowIds,
            ready
                ? "Preset test passed against current truth. Start re-evaluates rows in stored order after each verified purchase. No purchase was made."
                : "Preset is fulfilled or already at its requested currency/capacity limit; no purchase is needed.",
            rows);
    }

    private bool TryGetPreset(Guid presetId, out ShopListPreset preset)
    {
        preset = presetStore.Presets.FirstOrDefault(value => value.PresetId == presetId)!;
        return preset != null;
    }

    private static bool TryValidateCompletedRows(
        ShopListPreset preset,
        IReadOnlyList<Guid>? requested,
        out IReadOnlySet<Guid> completed,
        out string error)
    {
        var values = requested ?? [];
        if (values.Any(value => value == Guid.Empty) || values.Distinct().Count() != values.Count)
        {
            completed = new HashSet<Guid>();
            error = "completedRowIds must contain distinct, nonempty row IDs.";
            return false;
        }
        var known = preset.Items.Select(item => item.RowId).ToHashSet();
        var repeatable = preset.Items.Where(item => item.Repeatable).Select(item => item.RowId).ToHashSet();
        completed = values.Where(value => known.Contains(value) && !repeatable.Contains(value)).ToHashSet();
        error = string.Empty;
        return true;
    }

    private static bool TryParsePresetRequest(
        string json,
        bool requireOperationId,
        out ShopListPresetStartRequest? request,
        out string error)
    {
        request = null;
        try
        {
            request = JsonSerializer.Deserialize<ShopListPresetStartRequest>(json, JsonOptions);
            if (request == null)
                throw new InvalidDataException("Preset request was empty.");
            if (request.Version != 1)
                throw new InvalidDataException("Preset request version must be 1.");
            if (request.PresetId == Guid.Empty)
                throw new InvalidDataException("presetId must be a nonempty GUID.");
            request.OperationId = request.OperationId?.Trim() ?? string.Empty;
            if (requireOperationId && (request.OperationId.Length is < 1 or > 128))
                throw new InvalidDataException("operationId must contain 1 through 128 characters.");
            error = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            error = $"Preset request failed safely: {ex.Message}";
            return false;
        }
    }

    private static string SerializePreviewError(Guid presetId, string message)
        => JsonSerializer.Serialize(new ShopListPresetPreviewResponse(
            1, presetId, "error", -1, [], message, []), JsonOptions);

    private static string SerializeStart(ShopListPresetStartResponse response)
        => JsonSerializer.Serialize(response, JsonOptions);

    private static ShopListPresetStartResponse StartError(ShopListPresetStartRequest request, string message)
        => new(1, false, request.OperationId, request.PresetId, "error", [], message);

    internal static string ModeName(ShopListMode mode)
        => mode == ShopListMode.SpendUntilCurrencyOrCapacity
            ? "spend-until-currency-or-capacity"
            : "targeted-refill";

    internal static bool TryParseCurrencyKind(string? value, out ShopCurrencyKind kind)
    {
        foreach (var candidate in Enum.GetValues<ShopCurrencyKind>())
        {
            if (string.Equals(value?.Trim(), ShopOfferSelector.CurrencyKindName(candidate), StringComparison.OrdinalIgnoreCase)
                || string.Equals(value?.Trim(), candidate.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                kind = candidate;
                return true;
            }
        }

        kind = default;
        return false;
    }

    private static void ValidateCurrency(ShopCurrencyKind kind, uint itemId)
    {
        if (kind == ShopCurrencyKind.Gil && itemId != 1)
            throw new InvalidDataException("Gil requires currencyItemId 1.");
        if (kind == ShopCurrencyKind.FreeCompanyCredit && itemId != 0)
            throw new InvalidDataException("Free Company credits require currencyItemId 0.");
        if (kind is not (ShopCurrencyKind.Gil or ShopCurrencyKind.FreeCompanyCredit) && itemId == 0)
            throw new InvalidDataException("This currency kind requires a nonzero currencyItemId.");
    }

    private static string OwnershipScopeName(ShopListOwnershipScope scope)
        => scope == ShopListOwnershipScope.InventoryOnly
            ? "inventory-only"
            : "inventory-and-retainers";

    private sealed class ShopListPresetStartRequest
    {
        public int Version { get; set; }
        public string OperationId { get; set; } = string.Empty;
        public Guid PresetId { get; set; }
        public List<Guid>? CompletedRowIds { get; set; }
    }

    private sealed class ShopCatalogSearchRequest
    {
        public int Version { get; set; }
        public string Query { get; set; } = string.Empty;
        public string CurrencyKind { get; set; } = string.Empty;
        public uint CurrencyItemId { get; set; }
        public int Limit { get; set; } = 50;
    }

    private void InvalidateOwnership(string reason)
    {
        ownershipPresetId = Guid.Empty;
        retainerSnapshot = ShopListRetainerSnapshot.Empty;
        OwnershipAvailable = false;
        OwnershipStatus = reason;
    }

    private bool FailOwnership(string message, out string status)
    {
        ownershipPresetId = Guid.Empty;
        retainerSnapshot = ShopListRetainerSnapshot.Empty;
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
