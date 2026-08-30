using System.Globalization;
using System.Numerics;
using ADS.Models;
using ADS.Services;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace ADS.Windows;

public sealed class ShopListsWindow : PositionedWindow, IDisposable
{
    private readonly Plugin plugin;
    private readonly Dictionary<Guid, RowEdit> rowEdits = [];
    private IReadOnlyList<ShopListPreviewRow> previewRows = [];
    private IReadOnlyList<ShopCatalogSearchRow> catalogRows = [];
    private string previewState = string.Empty;
    private string newPresetName = string.Empty;
    private string renamePresetName = string.Empty;
    private int newItemId;
    private int newTriggerBelow = 1;
    private int newRefillToAtLeast = 1;
    private bool newRepeatable;
    private int newOwnershipScopeIndex = 1;
    private Guid settingsPresetId;
    private int presetModeIndex;
    private int currencyKindIndex;
    private int currencyItemId = 1;
    private int currencyThreshold;
    private string catalogQuery = string.Empty;
    private string status = string.Empty;
    private string testedPreviewState = string.Empty;
    private string testedDisposition = string.Empty;
    private string testedMessage = string.Empty;
    private DateTime? observedBatchCompletionUtc;

    public ShopListsWindow(Plugin plugin)
        : base("ADS Shop Lists###ADSShopLists")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(920f, 620f),
            MaximumSize = new Vector2(1800f, 1300f),
        };
        Size = new Vector2(1280f, 820f);
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        FinalizePendingWindowPlacement();
        RefreshPreviewIfChanged();
        RefreshAfterBatchCompletion();

        DrawBatchControls();
        ImGui.Separator();
        DrawPresetControls();
        ImGui.Separator();
        DrawImportControls();
        ImGui.Separator();
        DrawCatalogSearch();
        ImGui.Separator();
        DrawPreview();

        if (!string.IsNullOrWhiteSpace(status))
        {
            ImGui.Separator();
            ImGui.TextWrapped(status);
        }
    }

    private void DrawBatchControls()
    {
        var service = plugin.ShopListService;
        var batch = plugin.UtilityAutomationService.ShopListBatchStatus;
        ImGui.TextUnformatted("Preset test and purchase");
        ImGui.TextWrapped(batch.Running ? batch.StatusMessage : plugin.UtilityAutomationService.StatusMessage);
        if (batch.TotalRows > 0)
            ImGui.TextDisabled($"Completed rows: {batch.CompletedRows}/{batch.TotalRows} | operation: {batch.OperationId}");

        if (ImGui.Button("Test preset (preview only)"))
        {
            var test = service.PreviewActivePreset();
            ApplyPreviewRows(test.Rows);
            testedPreviewState = previewState;
            testedDisposition = test.Disposition;
            testedMessage = test.Message;
            status = $"Test {test.Disposition}: {test.Message} No travel or purchase was started.";
        }
        ImGui.SameLine();
        if (ImGui.Button("Refresh local preview"))
            RefreshPreview();

        var runBlocker = GetRunBlocker();
        ImGui.BeginDisabled(!string.IsNullOrEmpty(runBlocker));
        if (ImGui.Button("Run Shop List"))
        {
            plugin.StartShopListBatch(out status);
            RefreshPreview();
        }
        ImGui.EndDisabled();

        ImGui.SameLine();
        ImGui.BeginDisabled(!batch.Running);
        if (ImGui.Button("Cancel Shop List"))
            plugin.CancelUtility();
        ImGui.EndDisabled();

        if (!string.IsNullOrEmpty(runBlocker))
            ImGui.TextDisabled(runBlocker);

        ImGui.TextWrapped(service.OwnershipStatus);
        if (service.OwnershipRefreshedAtUtc != DateTime.MinValue)
        {
            ImGui.TextDisabled(
                $"Ownership response read: {service.OwnershipRefreshedAtUtc.ToString("u", CultureInfo.InvariantCulture)}");
        }
        DrawWarnings("XA Database warnings", service.OwnershipWarnings);
    }

    private void DrawPresetControls()
    {
        var service = plugin.ShopListService;
        var store = service.PresetStore;
        ImGui.TextUnformatted("Presets");

        var presetNames = store.Presets.Select(preset => preset.Name).ToArray();
        var presetIndex = Math.Max(0, Array.FindIndex(
            presetNames,
            name => string.Equals(name, store.ActivePresetName, StringComparison.OrdinalIgnoreCase)));
        if (ImGui.Combo("Active preset", ref presetIndex, presetNames, presetNames.Length))
        {
            SetStatus(service.SelectPreset(presetNames[presetIndex], out var error), error);
            settingsPresetId = Guid.Empty;
            RefreshPreview();
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("Copy preset ID"))
            ImGui.SetClipboardText(store.ActivePresetId.ToString("D"));
        ImGui.TextDisabled(store.ActivePresetId.ToString("D"));

        ImGui.SetNextItemWidth(240f);
        ImGui.InputText("New preset", ref newPresetName, 80);
        ImGui.SameLine();
        if (ImGui.Button("Create"))
        {
            var succeeded = service.CreatePreset(newPresetName, out var error);
            SetStatus(succeeded, error);
            if (succeeded)
            {
                newPresetName = string.Empty;
                renamePresetName = string.Empty;
                settingsPresetId = Guid.Empty;
                RefreshPreview();
            }
        }

        ImGui.SetNextItemWidth(240f);
        ImGui.InputText("Rename active", ref renamePresetName, 80);
        ImGui.SameLine();
        if (ImGui.Button("Rename"))
        {
            var succeeded = service.RenameActivePreset(renamePresetName, out var error);
            SetStatus(succeeded, error);
            if (succeeded)
            {
                renamePresetName = string.Empty;
                RefreshPreview();
            }
        }
        ImGui.SameLine();
        ImGui.BeginDisabled(string.Equals(store.ActivePresetName, ShopListPresetStore.DefaultPresetName, StringComparison.OrdinalIgnoreCase));
        if (ImGui.Button("Delete active"))
        {
            SetStatus(service.DeleteActivePreset(out var error), error);
            renamePresetName = string.Empty;
            settingsPresetId = Guid.Empty;
            RefreshPreview();
        }
        ImGui.EndDisabled();

        EnsurePresetSettings();
        var modes = new[] { "Targeted refill", "Spend until selected currency / capacity" };
        ImGui.SetNextItemWidth(275f);
        ImGui.Combo("Mode", ref presetModeIndex, modes, modes.Length);

        var currencyKinds = Enum.GetValues<ShopCurrencyKind>();
        var currencyNames = currencyKinds.Select(ShopOfferSelector.CurrencyKindName).ToArray();
        ImGui.SetNextItemWidth(210f);
        if (ImGui.Combo("Exact currency kind", ref currencyKindIndex, currencyNames, currencyNames.Length))
        {
            currencyItemId = currencyKinds[currencyKindIndex] switch
            {
                ShopCurrencyKind.Gil => 1,
                ShopCurrencyKind.FreeCompanyCredit => 0,
                _ => currencyItemId,
            };
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(120f);
        ImGui.InputInt("Currency item ID", ref currencyItemId);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(130f);
        ImGui.InputInt("Trigger at >=", ref currencyThreshold);
        ImGui.SameLine();
        if (ImGui.Button("Save preset settings"))
        {
            if (currencyItemId < 0 || currencyThreshold < 0)
            {
                status = "Currency item ID and trigger cannot be negative.";
            }
            else
            {
                var succeeded = service.ConfigureActivePreset(
                    (ShopListMode)Math.Clamp(presetModeIndex, 0, 1),
                    currencyKinds[Math.Clamp(currencyKindIndex, 0, currencyKinds.Length - 1)],
                    (uint)currencyItemId,
                    currencyThreshold,
                    out var error);
                SetStatus(succeeded, error);
                if (succeeded)
                    RefreshPreview();
            }
        }

        ImGui.TextUnformatted("Add row");
        ImGui.SetNextItemWidth(120f);
        ImGui.InputInt("Item ID", ref newItemId);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(95f);
        ImGui.InputInt("If owned <", ref newTriggerBelow);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(95f);
        ImGui.InputInt("Refill to >=", ref newRefillToAtLeast);
        ImGui.SameLine();
        ImGui.Checkbox("Repeatable", ref newRepeatable);
        ImGui.SameLine();
        var scopes = new[] { "Inventory only", "Inventory + XA Database retainers" };
        ImGui.SetNextItemWidth(255f);
        ImGui.Combo("Ownership", ref newOwnershipScopeIndex, scopes, scopes.Length);
        ImGui.SameLine();
        if (ImGui.Button("Add / update item"))
            AddOrUpdateItem((uint)Math.Max(0, newItemId));
    }

    private void DrawImportControls()
    {
        ImGui.TextUnformatted("Preset sharing (Base64)");
        if (ImGui.Button("Export active preset to clipboard"))
        {
            ImGui.SetClipboardText(plugin.ShopListService.ExportActivePresetBase64());
            status = "Copied the active Base64 preset. Stable preset and row IDs were preserved.";
        }
        ImGui.SameLine();
        if (ImGui.Button("Import Base64 preset from clipboard"))
        {
            var succeeded = plugin.ShopListService.ImportPresetBase64(ImGui.GetClipboardText() ?? string.Empty, out var error);
            SetStatus(succeeded, error);
            if (succeeded)
            {
                settingsPresetId = Guid.Empty;
                RefreshPreview();
            }
        }

        ImGui.TextUnformatted("Replace active rows from clipboard");
        if (ImGui.Button("Import TeamCraft"))
            ImportClipboard(ShopListImportSource.TeamCraft);
        ImGui.SameLine();
        if (ImGui.Button("Import Crafting as a Service"))
            ImportClipboard(ShopListImportSource.CraftingAsAService);
        ImGui.SameLine();
        if (ImGui.Button("Import Artisan"))
            ImportClipboard(ShopListImportSource.Artisan);

        if (!string.IsNullOrWhiteSpace(plugin.ShopListService.ImportStatus))
            ImGui.TextWrapped(plugin.ShopListService.ImportStatus);
        if (plugin.ShopListService.LastImportResult is { } importResult)
            DrawWarnings("Skipped / unresolved import rows", importResult.Warnings);
    }

    private void DrawCatalogSearch()
    {
        var active = plugin.ShopListService.PresetStore.ActivePreset;
        ImGui.TextUnformatted("Deterministic vendor catalog");
        ImGui.SetNextItemWidth(300f);
        ImGui.InputText("Item / vendor / NPC / territory / currency", ref catalogQuery, 120);
        ImGui.SameLine();
        if (ImGui.Button("Search exact preset currency"))
        {
            catalogRows = plugin.UtilityAutomationService.SearchShopCatalog(catalogQuery, active.Currency, 100).Rows;
            status = $"Catalog returned {catalogRows.Count} exact-currency offer(s).";
        }
        ImGui.SameLine();
        if (ImGui.Button("Discover currencies"))
        {
            catalogRows = plugin.UtilityAutomationService.SearchShopCatalog(catalogQuery, null, 100).Rows;
            status = $"Catalog returned {catalogRows.Count} offer(s); use a row's exact currency identity below.";
        }

        if (catalogRows.Count == 0)
        {
            ImGui.TextDisabled("Search results appear here. Catalog search and Test never travel or purchase.");
            return;
        }

        if (!ImGui.BeginTable(
                "ADSShopCatalogSearch",
                5,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp,
                new Vector2(-1f, 220f)))
        {
            return;
        }
        ImGui.TableSetupColumn("Currency", ImGuiTableColumnFlags.WidthStretch, 1.2f);
        ImGui.TableSetupColumn("Item / bundle", ImGuiTableColumnFlags.WidthStretch, 1.2f);
        ImGui.TableSetupColumn("Vendor / NPC", ImGuiTableColumnFlags.WidthStretch, 1.7f);
        ImGui.TableSetupColumn("Territory / XYZ", ImGuiTableColumnFlags.WidthStretch, 1.4f);
        ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed, 160f);
        ImGui.TableHeadersRow();

        foreach (var row in catalogRows)
        {
            ImGui.PushID(HashCode.Combine(row.ItemId, row.ShopId, row.ShopRow, row.NpcId, row.TerritoryId));
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextWrapped($"{row.CurrencyName}: {row.CurrencyCostPerTransaction}");
            ImGui.TextDisabled($"{row.CurrencyKind}:{row.CurrencyItemId}");
            ImGui.TableSetColumnIndex(1);
            ImGui.TextWrapped(row.ItemName);
            ImGui.TextDisabled($"{row.ItemId} | receive {row.ReceiveCount}");
            ImGui.TableSetColumnIndex(2);
            ImGui.TextWrapped(row.ShopName);
            ImGui.TextDisabled($"shop {row.ShopId} row {row.ShopRow}");
            ImGui.TextWrapped(row.NpcName);
            ImGui.TextDisabled(row.NpcId.ToString(CultureInfo.InvariantCulture));
            ImGui.TableSetColumnIndex(3);
            ImGui.TextWrapped(row.TerritoryName);
            ImGui.TextDisabled($"{row.TerritoryId} | {row.CopyableXyz}");
            ImGui.TableSetColumnIndex(4);
            if (ImGui.SmallButton("Use currency"))
                UseCatalogCurrency(row);
            if (ImGui.SmallButton("Add item"))
                AddOrUpdateItem(row.ItemId);
            if (ImGui.SmallButton("Copy XYZ"))
                ImGui.SetClipboardText(row.CopyableXyz);
            ImGui.PopID();
        }
        ImGui.EndTable();
    }

    private void DrawPreview()
    {
        ImGui.TextUnformatted("Preview");
        if (previewRows.Count == 0)
        {
            ImGui.TextDisabled("The active preset has no items.");
            return;
        }

        if (!ImGui.BeginTable(
                "ADSShopListPreview",
                8,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY
                | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp,
                new Vector2(-1f, -1f)))
        {
            return;
        }
        ImGui.TableSetupColumn("Item / row", ImGuiTableColumnFlags.WidthStretch, 1.1f);
        ImGui.TableSetupColumn("Rule", ImGuiTableColumnFlags.WidthStretch, 1.4f);
        ImGui.TableSetupColumn("Owned", ImGuiTableColumnFlags.WidthFixed, 95f);
        ImGui.TableSetupColumn("Retainer locations", ImGuiTableColumnFlags.WidthStretch, 1.6f);
        ImGui.TableSetupColumn("Would buy", ImGuiTableColumnFlags.WidthFixed, 70f);
        ImGui.TableSetupColumn("Selected vendor", ImGuiTableColumnFlags.WidthStretch, 1.4f);
        ImGui.TableSetupColumn("Outcome / status", ImGuiTableColumnFlags.WidthStretch, 1.6f);
        ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthFixed, 72f);
        ImGui.TableHeadersRow();

        foreach (var row in previewRows)
        {
            ImGui.PushID(row.RowId.GetHashCode());
            if (!rowEdits.TryGetValue(row.RowId, out var edit))
                edit = rowEdits[row.RowId] = RowEdit.From(row);

            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextWrapped(row.ItemName);
            ImGui.TextDisabled(row.ItemId.ToString(CultureInfo.InvariantCulture));
            if (ImGui.SmallButton("Copy row ID"))
                ImGui.SetClipboardText(row.RowId.ToString("D"));

            ImGui.TableSetColumnIndex(1);
            ImGui.SetNextItemWidth(70f);
            ImGui.InputInt("<##Trigger", ref edit.TriggerBelow);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(70f);
            ImGui.InputInt(">=##Refill", ref edit.RefillToAtLeast);
            ImGui.Checkbox("Repeatable", ref edit.Repeatable);
            var scopeNames = new[] { "Inventory", "Inventory + retainers" };
            ImGui.SetNextItemWidth(155f);
            ImGui.Combo("##Scope", ref edit.OwnershipScopeIndex, scopeNames, scopeNames.Length);
            if (ImGui.SmallButton("Save row"))
            {
                var succeeded = plugin.ShopListService.UpdateItem(
                    row.RowId,
                    edit.TriggerBelow,
                    edit.RefillToAtLeast,
                    edit.Repeatable,
                    (ShopListOwnershipScope)Math.Clamp(edit.OwnershipScopeIndex, 0, 1),
                    out var error);
                SetStatus(succeeded, error);
                if (succeeded)
                    RefreshPreview();
            }

            ImGui.TableSetColumnIndex(2);
            ImGui.TextUnformatted(row.OwnedQuantity.ToString("N0", CultureInfo.InvariantCulture));
            ImGui.TextDisabled($"Inv {row.LiveInventoryQuantity:N0}");

            ImGui.TableSetColumnIndex(3);
            DrawRetainerLocations(row);

            ImGui.TableSetColumnIndex(4);
            ImGui.TextUnformatted(row.PurchaseQuantity.ToString("N0", CultureInfo.InvariantCulture));

            ImGui.TableSetColumnIndex(5);
            if (row.SelectedOffer == null)
            {
                ImGui.TextDisabled("None needed / available");
            }
            else
            {
                ImGui.TextWrapped(row.SelectedOffer.ShopName);
                ImGui.TextDisabled($"{row.SelectedOffer.ShopKind} {row.SelectedOffer.ShopId}");
                ImGui.TextDisabled($"{row.SelectedOffer.NpcName} - {row.SelectedOffer.TerritoryName}");
            }

            ImGui.TableSetColumnIndex(6);
            ImGui.TextWrapped($"{row.Outcome}: {row.StatusMessage}");
            if (!string.IsNullOrWhiteSpace(row.FailureCode))
                ImGui.TextDisabled(row.FailureCode);

            ImGui.TableSetColumnIndex(7);
            if (ImGui.SmallButton("Remove"))
            {
                SetStatus(plugin.ShopListService.RemoveItem(row.RowId, out var error), error);
                RefreshPreview();
            }
            ImGui.PopID();
        }
        ImGui.EndTable();
    }

    private void EnsurePresetSettings()
    {
        var active = plugin.ShopListService.PresetStore.ActivePreset;
        if (settingsPresetId == active.PresetId)
            return;
        settingsPresetId = active.PresetId;
        presetModeIndex = (int)active.Mode;
        var kinds = Enum.GetValues<ShopCurrencyKind>();
        currencyKindIndex = Math.Max(0, Array.IndexOf(kinds, active.CurrencyKind));
        currencyItemId = unchecked((int)active.CurrencyItemId);
        currencyThreshold = active.CurrencyThreshold > int.MaxValue ? int.MaxValue : (int)active.CurrencyThreshold;
    }

    private void UseCatalogCurrency(ShopCatalogSearchRow row)
    {
        if (!ShopListService.TryParseCurrencyKind(row.CurrencyKind, out var kind))
        {
            status = "Catalog returned an unsupported currency kind; preset was not changed.";
            return;
        }
        var active = plugin.ShopListService.PresetStore.ActivePreset;
        var succeeded = plugin.ShopListService.ConfigureActivePreset(
            active.Mode,
            kind,
            row.CurrencyItemId,
            active.CurrencyThreshold,
            out var error);
        SetStatus(succeeded, error);
        if (succeeded)
        {
            settingsPresetId = Guid.Empty;
            RefreshPreview();
        }
    }

    private void AddOrUpdateItem(uint itemId)
    {
        if (itemId == 0)
        {
            status = "Item ID must be a positive decimal integer.";
            return;
        }
        var succeeded = plugin.ShopListService.SetItem(
            itemId,
            newTriggerBelow,
            newRefillToAtLeast,
            newRepeatable,
            (ShopListOwnershipScope)Math.Clamp(newOwnershipScopeIndex, 0, 1),
            out var error);
        SetStatus(succeeded, error);
        if (succeeded)
        {
            newItemId = 0;
            RefreshPreview();
        }
    }

    private static void DrawRetainerLocations(ShopListPreviewRow row)
    {
        ImGui.TextUnformatted(row.RetainerQuantity.ToString("N0", CultureInfo.InvariantCulture));
        foreach (var location in row.RetainerLocations)
        {
            var quality = location.IsHq ? " HQ" : string.Empty;
            ImGui.TextWrapped(
                $"{location.RetainerName}: {location.Quantity.ToString("N0", CultureInfo.InvariantCulture)}{quality} | "
                + $"{location.ContainerName} | {location.LastSeenUtc} | {location.SnapshotQuality}");
        }
    }

    private void ImportClipboard(ShopListImportSource source)
    {
        plugin.ShopListService.ImportClipboard(source, ImGui.GetClipboardText() ?? string.Empty, out status);
        RefreshPreview();
    }

    private string GetRunBlocker()
    {
        if (plugin.UtilityAutomationService.IsRunning)
            return $"Another ADS utility is active: {plugin.UtilityAutomationService.StatusMessage}";
        if (plugin.ShopListService.PresetStore.ActivePreset.Items.Count == 0)
            return "Add or import at least one item first.";
        if (!plugin.ShopListService.OwnershipAvailable)
            return "Run Test preset first so ownership and exact-currency offers are current.";
        if (!string.Equals(testedPreviewState, previewState, StringComparison.Ordinal))
            return "Run Test preset for the current saved preset before purchasing.";
        if (string.Equals(testedDisposition, "not-triggered", StringComparison.Ordinal))
            return string.IsNullOrWhiteSpace(testedMessage)
                ? "The selected currency trigger is not met."
                : testedMessage;
        if (string.Equals(testedDisposition, "fulfilled", StringComparison.Ordinal))
            return "The preset is already fulfilled; no purchase is needed.";
        if (!string.Equals(testedDisposition, "ready", StringComparison.Ordinal))
            return string.IsNullOrWhiteSpace(testedMessage)
                ? "The last Test did not produce a ready preset."
                : testedMessage;
        var blocked = previewRows.FirstOrDefault(row => row.Outcome == "failed");
        return blocked == null
            ? string.Empty
            : $"Resolve the preview error for {blocked.ItemName} before running: {blocked.StatusMessage}";
    }

    private void RefreshPreviewIfChanged()
    {
        var preset = plugin.ShopListService.PresetStore.ActivePreset;
        var state = $"{preset.PresetId:D}|{preset.Name}|{preset.Mode}|{preset.CurrencyKind}:{preset.CurrencyItemId}:{preset.CurrencyThreshold}|"
                    + string.Join(';', preset.Items.Select(item =>
                        $"{item.RowId:D}:{item.ItemId}:{item.TriggerBelow}:{item.RefillToAtLeast}:{item.Repeatable}:{item.OwnershipScope}"));
        if (!string.Equals(state, previewState, StringComparison.Ordinal))
            RefreshPreview();
    }

    private void RefreshAfterBatchCompletion()
    {
        var completionUtc = plugin.UtilityAutomationService.ShopListBatchStatus.CompletedAtUtc;
        if (completionUtc == observedBatchCompletionUtc)
            return;
        observedBatchCompletionUtc = completionUtc;
        if (completionUtc.HasValue)
        {
            testedPreviewState = string.Empty;
            RefreshPreview();
        }
    }

    private void RefreshPreview()
    {
        try
        {
            ApplyPreviewRows(plugin.ShopListService.BuildPreviewRows());
        }
        catch (Exception ex)
        {
            previewRows = [];
            status = $"Shop-list preview failed safely: {ex.Message}";
            Plugin.Log.Warning(ex, "[ADS][ShopLists] Preview failed.");
        }
    }

    private void ApplyPreviewRows(IReadOnlyList<ShopListPreviewRow> rows)
    {
        previewRows = rows;
        rowEdits.Clear();
        foreach (var row in previewRows)
            rowEdits[row.RowId] = RowEdit.From(row);

        var preset = plugin.ShopListService.PresetStore.ActivePreset;
        previewState = $"{preset.PresetId:D}|{preset.Name}|{preset.Mode}|{preset.CurrencyKind}:{preset.CurrencyItemId}:{preset.CurrencyThreshold}|"
                       + string.Join(';', preset.Items.Select(item =>
                           $"{item.RowId:D}:{item.ItemId}:{item.TriggerBelow}:{item.RefillToAtLeast}:{item.Repeatable}:{item.OwnershipScope}"));
    }

    private void SetStatus(bool succeeded, string error)
        => status = succeeded ? "Saved." : error;

    private static void DrawWarnings(string label, IReadOnlyList<string> warnings)
    {
        if (warnings.Count == 0 || !ImGui.TreeNode($"{label} ({warnings.Count})"))
            return;
        foreach (var warning in warnings)
            ImGui.BulletText(warning);
        ImGui.TreePop();
    }

    private sealed class RowEdit
    {
        public int TriggerBelow;
        public int RefillToAtLeast;
        public bool Repeatable;
        public int OwnershipScopeIndex;

        public static RowEdit From(ShopListPreviewRow row)
            => new()
            {
                TriggerBelow = row.TriggerBelow,
                RefillToAtLeast = row.RefillToAtLeast,
                Repeatable = row.Repeatable,
                OwnershipScopeIndex = row.OwnershipScope == "inventory-only" ? 0 : 1,
            };
    }
}
