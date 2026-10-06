using AethertekUI;
using ADS.Localization;
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
    private int relicExampleIndex;
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
        windowMotion.DrawChrome();
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
            MaterialText.TextWrapped(Ui.Display(status));
        }
    }

    private void DrawBatchControls()
    {
        var service = plugin.ShopListService;
        var batch = plugin.UtilityAutomationService.ShopListBatchStatus;
        MaterialText.Text(Ui.T("Preset test and purchase"));
        MaterialText.TextWrapped(Ui.Display(batch.Running ? batch.StatusMessage : plugin.UtilityAutomationService.StatusMessage));
        if (batch.TotalRows > 0)
            MaterialText.TextDisabled(Ui.T("Completed rows: {0}/{1} | operation: {2}", batch.CompletedRows, batch.TotalRows, batch.OperationId));

        if (WindowLayout.Button(Ui.L("Test preset (preview only)")))
        {
            var test = service.PreviewActivePreset();
            ApplyPreviewRows(test.Rows);
            testedPreviewState = previewState;
            testedDisposition = test.Disposition;
            testedMessage = test.Message;
            status = $"Test {test.Disposition}: {test.Message} No travel or purchase was started.";
        }
        ImGui.SameLine();
        if (WindowLayout.Button(Ui.L("Refresh local preview")))
            RefreshPreview();

        var runBlocker = GetRunBlocker();
        ImGui.BeginDisabled(!string.IsNullOrEmpty(runBlocker));
        if (WindowLayout.Button(Ui.L("Run Shop List")))
        {
            plugin.StartShopListBatch(out status);
            RefreshPreview();
        }
        ImGui.EndDisabled();

        ImGui.SameLine();
        ImGui.BeginDisabled(!batch.Running);
        if (WindowLayout.Button(Ui.L("Cancel Shop List")))
            plugin.CancelUtility();
        ImGui.EndDisabled();

        if (!string.IsNullOrEmpty(runBlocker))
            MaterialText.TextDisabled(Ui.Display(runBlocker));

        if (service.IsStandaloneOrderComplete && WindowLayout.Button(Ui.L("Start new order")))
        {
            SetStatus(service.StartNewOrder(out var error), error);
            RefreshPreview();
        }
        MaterialText.TextWrapped(Ui.T("Targeted refill uses current ownership thresholds. Spend until currency/capacity keeps spending under its repeat rules. Fill order over multiple runs credits initial ownership once, then verified purchases; consuming or moving items keeps that progress."));

        MaterialText.TextWrapped(Ui.Display(service.OwnershipStatus));
        if (service.OwnershipRefreshedAtUtc != DateTime.MinValue)
        {
            MaterialText.TextDisabled(
                Ui.T("Ownership response read: {0}", service.OwnershipRefreshedAtUtc.ToString("u", CultureInfo.InvariantCulture)));
        }
        DrawWarnings("XA Database warnings", service.OwnershipWarnings);
    }

    private void DrawPresetControls()
    {
        var service = plugin.ShopListService;
        var store = service.PresetStore;
        MaterialText.Text(Ui.T("Presets"));
        MaterialText.TextWrapped(Ui.Display(store.LastStatus));
        ImGui.BeginDisabled(plugin.UtilityAutomationService.IsRunning);
        if (WindowLayout.Button(Ui.L("Reload saved lists")))
        {
            store.Reload();
            settingsPresetId = Guid.Empty;
            RefreshPreview();
        }
        ImGui.EndDisabled();

        var presetNames = store.Presets.Select(preset => preset.Name).ToArray();
        var presetIndex = Math.Max(0, Array.FindIndex(
            presetNames,
            name => string.Equals(name, store.ActivePresetName, StringComparison.OrdinalIgnoreCase)));
        if (WindowLayout.Combo(Ui.L("Active preset"), ref presetIndex, presetNames, presetNames.Length))
        {
            SetStatus(service.SelectPreset(presetNames[presetIndex], out var error), error);
            settingsPresetId = Guid.Empty;
            RefreshPreview();
        }
        ImGui.SameLine();
        if (WindowLayout.SmallButton(Ui.L("Copy preset ID")))
            ImGui.SetClipboardText(store.ActivePresetId.ToString("D"));
        MaterialText.TextDisabled(store.ActivePresetId.ToString("D"));

        var examples = ShopListExamples.RelicSteps;
        var exampleNames = examples.Select(example => example.Name).ToArray();
        ImGui.SetNextItemWidth(460f);
        WindowLayout.Combo(Ui.L("Relic step example"), ref relicExampleIndex,exampleNames.Select(Ui.Display).ToArray(), exampleNames.Length);
        ImGui.SameLine();
        if (WindowLayout.Button(Ui.L("Add example")))
        {
            var succeeded = service.AddExamplePreset(examples[relicExampleIndex], out var error);
            SetStatus(succeeded, error);
            if (succeeded)
            {
                renamePresetName = string.Empty;
                settingsPresetId = Guid.Empty;
                RefreshPreview();
                status = "Example added as an editable preset. Review targets, then Test preset before Run Shop List.";
            }
        }
        MaterialText.TextWrapped(Ui.Display(examples[relicExampleIndex].Description));
        MaterialText.TextWrapped(Ui.T("Examples use Poetics and inventory-only refill targets. Adjust targets for your unfinished step; Add example does not start purchases. Mysterious Map and its farming belong to Loot Goblin."));

        ImGui.SetNextItemWidth(240f);
        WindowLayout.InputText(Ui.L("New preset"), ref newPresetName, 80);
        ImGui.SameLine();
        if (WindowLayout.Button(Ui.L("Create")))
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
        WindowLayout.InputText(Ui.L("Rename active"), ref renamePresetName, 80);
        ImGui.SameLine();
        if (WindowLayout.Button(Ui.L("Rename")))
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
        if (WindowLayout.Button(Ui.L("Delete active")))
        {
            SetStatus(service.DeleteActivePreset(out var error), error);
            renamePresetName = string.Empty;
            settingsPresetId = Guid.Empty;
            RefreshPreview();
        }
        ImGui.EndDisabled();

        EnsurePresetSettings();
        var modes = new[] { "Targeted refill", "Spend until currency/capacity", "Fill order over multiple runs" };
        ImGui.SetNextItemWidth(275f);
        WindowLayout.Combo(Ui.L("Purchase type"), ref presetModeIndex,modes.Select(Ui.Display).ToArray(), modes.Length);

        var currencyKinds = Enum.GetValues<ShopCurrencyKind>();
        var currencyNames = currencyKinds.Select(ShopOfferSelector.CurrencyKindName).ToArray();
        ImGui.SetNextItemWidth(210f);
        if (WindowLayout.Combo(Ui.L("Exact currency kind"), ref currencyKindIndex,currencyNames.Select(Ui.Display).ToArray(), currencyNames.Length))
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
        WindowLayout.InputInt(Ui.L("Currency item ID"), ref currencyItemId);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(130f);
        WindowLayout.InputInt(Ui.L("Trigger at >="), ref currencyThreshold);
        ImGui.SameLine();
        if (WindowLayout.Button(Ui.L("Save preset settings")))
        {
            if (currencyItemId < 0 || currencyThreshold < 0)
            {
                status = "Currency item ID and trigger cannot be negative.";
            }
            else
            {
                var succeeded = service.ConfigureActivePreset(
                    (ShopListMode)Math.Clamp(presetModeIndex, 0, 2),
                    currencyKinds[Math.Clamp(currencyKindIndex, 0, currencyKinds.Length - 1)],
                    (uint)currencyItemId,
                    currencyThreshold,
                    out var error);
                SetStatus(succeeded, error);
                if (succeeded)
                    RefreshPreview();
            }
        }

        MaterialText.Text(Ui.T("Add row"));
        ImGui.SetNextItemWidth(120f);
        WindowLayout.InputInt(Ui.L("Item ID"), ref newItemId);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(95f);
        if (store.ActivePreset.Mode == ShopListMode.FillOrderOverMultipleRuns)
        {
            WindowLayout.InputInt(Ui.L("Target quantity"), ref newRefillToAtLeast);
            newTriggerBelow = newRefillToAtLeast;
            newRepeatable = false;
        }
        else
        {
            WindowLayout.InputInt(Ui.L("If owned <"), ref newTriggerBelow);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(95f);
            WindowLayout.InputInt(Ui.L("Refill to >="), ref newRefillToAtLeast);
            ImGui.SameLine();
            WindowLayout.Checkbox("Repeatable", ref newRepeatable);
        }
        ImGui.SameLine();
        var scopes = new[] { "Inventory only", "Inventory + XA Database retainers" };
        ImGui.SetNextItemWidth(255f);
        WindowLayout.Combo(Ui.L("Item ownership"), ref newOwnershipScopeIndex,scopes.Select(Ui.Display).ToArray(), scopes.Length);
        ImGui.SameLine();
        if (WindowLayout.Button(Ui.L("Add / update item")))
            AddOrUpdateItem((uint)Math.Max(0, newItemId));
    }

    private void DrawImportControls()
    {
        MaterialText.Text(Ui.T("Preset sharing (Base64)"));
        if (WindowLayout.Button(Ui.L("Export active preset to clipboard")))
        {
            ImGui.SetClipboardText(plugin.ShopListService.ExportActivePresetBase64());
            status = "Copied the active Base64 preset. Stable preset and row IDs were preserved.";
        }
        ImGui.SameLine();
        if (WindowLayout.Button(Ui.L("Import Base64 preset from clipboard")))
        {
            var succeeded = plugin.ShopListService.ImportPresetBase64(ImGui.GetClipboardText() ?? string.Empty, out var error);
            SetStatus(succeeded, error);
            if (succeeded)
            {
                settingsPresetId = Guid.Empty;
                RefreshPreview();
            }
        }

        MaterialText.Text(Ui.T("Replace active rows from clipboard"));
        if (WindowLayout.Button(Ui.L("Import TeamCraft")))
            ImportClipboard(ShopListImportSource.TeamCraft);
        ImGui.SameLine();
        if (WindowLayout.Button(Ui.L("Import Crafting as a Service")))
            ImportClipboard(ShopListImportSource.CraftingAsAService);
        ImGui.SameLine();
        if (WindowLayout.Button(Ui.L("Import Artisan")))
            ImportClipboard(ShopListImportSource.Artisan);

        if (!string.IsNullOrWhiteSpace(plugin.ShopListService.ImportStatus))
            MaterialText.TextWrapped(Ui.Display(plugin.ShopListService.ImportStatus));
        if (plugin.ShopListService.LastImportResult is { } importResult)
            DrawWarnings("Skipped / unresolved import rows", importResult.Warnings);
    }

    private void DrawCatalogSearch()
    {
        var active = plugin.ShopListService.PresetStore.ActivePreset;
        MaterialText.Text(Ui.T("Deterministic vendor catalog"));
        ImGui.SetNextItemWidth(300f);
        WindowLayout.InputText(Ui.L("Item / vendor / NPC / territory / currency"), ref catalogQuery, 120);
        ImGui.SameLine();
        if (WindowLayout.Button(Ui.L("Search exact preset currency")))
        {
            catalogRows = plugin.UtilityAutomationService.SearchShopCatalog(catalogQuery, active.Currency, 100).Rows;
            status = $"Catalog returned {catalogRows.Count} exact-currency offer(s).";
        }
        ImGui.SameLine();
        if (WindowLayout.Button(Ui.L("Discover currencies")))
        {
            catalogRows = plugin.UtilityAutomationService.SearchShopCatalog(catalogQuery, null, 100).Rows;
            status = $"Catalog returned {catalogRows.Count} offer(s); use a row's exact currency identity below.";
        }

        if (catalogRows.Count == 0)
        {
            MaterialText.TextDisabled(Ui.T("Search results appear here. Catalog search and Test never travel or purchase."));
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
        ImGui.TableSetupColumn(Ui.L("Currency"), ImGuiTableColumnFlags.WidthStretch, 1.2f);
        ImGui.TableSetupColumn(Ui.L("Item / bundle"), ImGuiTableColumnFlags.WidthStretch, 1.2f);
        ImGui.TableSetupColumn(Ui.L("Vendor / NPC"), ImGuiTableColumnFlags.WidthStretch, 1.7f);
        ImGui.TableSetupColumn(Ui.L("Territory / XYZ"), ImGuiTableColumnFlags.WidthStretch, 1.4f);
        ImGui.TableSetupColumn(Ui.L("Actions"), ImGuiTableColumnFlags.WidthFixed, 160f);
        WindowLayout.TableHeadersRow();

        foreach (var row in catalogRows)
        {
            ImGui.PushID(HashCode.Combine(row.ItemId, row.ShopId, row.ShopRow, row.NpcId, row.TerritoryId));
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            MaterialText.TextWrapped(Ui.T("{0}: {1}", Ui.ItemName(row.CurrencyItemId, row.CurrencyName), row.CurrencyCostPerTransaction));
            MaterialText.TextDisabled(Ui.T("{0}:{1}", row.CurrencyKind, row.CurrencyItemId));
            ImGui.TableSetColumnIndex(1);
            MaterialText.TextWrapped(Ui.ItemName(row.ItemId, row.ItemName));
            MaterialText.TextDisabled(Ui.T("{0} | receive {1}", row.ItemId, row.ReceiveCount));
            ImGui.TableSetColumnIndex(2);
            MaterialText.TextWrapped(Ui.ShopName(row.ShopKind, row.ShopId, row.ShopName));
            MaterialText.TextDisabled(Ui.T("shop {0} row {1}", row.ShopId, row.ShopRow));
            MaterialText.TextWrapped(Ui.NpcName(row.NpcId, row.NpcName));
            MaterialText.TextDisabled(row.NpcId.ToString(CultureInfo.InvariantCulture));
            ImGui.TableSetColumnIndex(3);
            MaterialText.TextWrapped(Ui.TerritoryName(row.TerritoryId, row.TerritoryName));
            MaterialText.TextDisabled(Ui.T("{0} | {1}", row.TerritoryId, row.CopyableXyz));
            ImGui.TableSetColumnIndex(4);
            if (WindowLayout.SmallButton(Ui.L("Use currency")))
                UseCatalogCurrency(row);
            if (WindowLayout.SmallButton(Ui.L("Add item")))
                AddOrUpdateItem(row.ItemId);
            if (WindowLayout.SmallButton(Ui.L("Copy XYZ")))
                ImGui.SetClipboardText(row.CopyableXyz);
            ImGui.PopID();
        }
        ImGui.EndTable();
    }

    private void DrawPreview()
    {
        MaterialText.Text(Ui.T("Preview"));
        if (previewRows.Count == 0)
        {
            MaterialText.TextDisabled(Ui.T("The active preset has no items."));
            return;
        }

        if (plugin.ShopListService.PresetStore.ActivePreset.Mode == ShopListMode.TargetedRefill)
            MaterialText.TextWrapped(Ui.T("Set both 'If owned <' and 'Refill to >=' to 130 to target 130 owned items. With single-item vendor bundles, owning 10 means buying 120. /ads shop <itemID> 130 requests 130 additional items."));

        if (!ImGui.BeginTable(
                "ADSShopListPreview",
                8,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY
                | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp,
                new Vector2(-1f, -1f)))
        {
            return;
        }
        ImGui.TableSetupColumn(Ui.L("Item / row"), ImGuiTableColumnFlags.WidthStretch, 1.1f);
        ImGui.TableSetupColumn(Ui.L("Rule"), ImGuiTableColumnFlags.WidthStretch, 1.4f);
        ImGui.TableSetupColumn(Ui.L("Owned"), ImGuiTableColumnFlags.WidthFixed, 95f);
        ImGui.TableSetupColumn(Ui.L("Retainer locations"), ImGuiTableColumnFlags.WidthStretch, 1.6f);
        ImGui.TableSetupColumn(Ui.L("Would buy"), ImGuiTableColumnFlags.WidthFixed, 70f);
        ImGui.TableSetupColumn(Ui.L("Selected vendor"), ImGuiTableColumnFlags.WidthStretch, 1.4f);
        ImGui.TableSetupColumn(Ui.L("Outcome / status"), ImGuiTableColumnFlags.WidthStretch, 1.6f);
        ImGui.TableSetupColumn(Ui.L("Action"), ImGuiTableColumnFlags.WidthFixed, 72f);
        WindowLayout.TableHeadersRow();

        foreach (var row in previewRows)
        {
            ImGui.PushID(row.RowId.GetHashCode());
            if (!rowEdits.TryGetValue(row.RowId, out var edit))
                edit = rowEdits[row.RowId] = RowEdit.From(row);

            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            MaterialText.TextWrapped(Ui.ItemName(row.ItemId, row.ItemName));
            MaterialText.TextDisabled(row.ItemId.ToString(CultureInfo.InvariantCulture));
            if (WindowLayout.SmallButton(Ui.L("Copy row ID")))
                ImGui.SetClipboardText(row.RowId.ToString("D"));

            ImGui.TableSetColumnIndex(1);
            ImGui.SetNextItemWidth(70f);
            if (plugin.ShopListService.PresetStore.ActivePreset.Mode == ShopListMode.FillOrderOverMultipleRuns)
            {
                WindowLayout.InputInt(Ui.L("Target"), ref edit.RefillToAtLeast);
                edit.TriggerBelow = edit.RefillToAtLeast;
                edit.Repeatable = false;
                var credited = plugin.ShopListService.PresetStore.GetOrderProgress(
                    plugin.UtilityAutomationService.ShopCharacterId, plugin.ShopListService.PresetStore.ActivePresetId);
                MaterialText.TextDisabled(Ui.T("Credited: {0} / {1}", credited.GetValueOrDefault(row.RowId), row.RefillToAtLeast));
            }
            else
            {
                WindowLayout.InputInt(Ui.L("If owned <##Trigger"), ref edit.TriggerBelow);
                ImGui.SetNextItemWidth(70f);
                WindowLayout.InputInt(Ui.L("Refill to >=##Refill"), ref edit.RefillToAtLeast);
                WindowLayout.Checkbox("Repeatable", ref edit.Repeatable);
            }
            var scopeNames = new[] { "Inventory", "Inventory + retainers" };
            ImGui.SetNextItemWidth(155f);
            WindowLayout.Combo("##Scope", ref edit.OwnershipScopeIndex,scopeNames.Select(Ui.Display).ToArray(), scopeNames.Length);
            if (WindowLayout.SmallButton(Ui.L("Save row")))
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
            MaterialText.Text(row.OwnedQuantity.ToString("N0", Ui.Culture));
            MaterialText.TextDisabled(Ui.T("Inv {0:N0}", row.LiveInventoryQuantity));

            ImGui.TableSetColumnIndex(3);
            DrawRetainerLocations(row);

            ImGui.TableSetColumnIndex(4);
            MaterialText.Text(row.PurchaseQuantity.ToString("N0", Ui.Culture));

            ImGui.TableSetColumnIndex(5);
            if (row.SelectedOffer == null)
            {
                MaterialText.TextDisabled(Ui.T("None needed / available"));
            }
            else
            {
                MaterialText.TextWrapped(Ui.ShopName(row.SelectedOffer.ShopKind, row.SelectedOffer.ShopId, row.SelectedOffer.ShopName));
                MaterialText.TextDisabled(Ui.T("{0} {1}", row.SelectedOffer.ShopKind, row.SelectedOffer.ShopId));
                MaterialText.TextDisabled(Ui.T("{0} - {1}", Ui.NpcName(row.SelectedOffer.NpcId, row.SelectedOffer.NpcName), Ui.TerritoryName(row.SelectedOffer.TerritoryId, row.SelectedOffer.TerritoryName)));
            }

            ImGui.TableSetColumnIndex(6);
            MaterialText.TextWrapped(Ui.T("{0}: {1}", Ui.Display(row.Outcome), Ui.Display(row.StatusMessage)));
            if (!string.IsNullOrWhiteSpace(row.FailureCode))
                MaterialText.TextDisabled(row.FailureCode);

            ImGui.TableSetColumnIndex(7);
            if (WindowLayout.SmallButton(Ui.L("Remove")))
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
        MaterialText.Text(row.RetainerQuantity.ToString("N0", Ui.Culture));
        foreach (var location in row.RetainerLocations)
        {
            var quality = location.IsHq ? " HQ" : string.Empty;
            MaterialText.TextWrapped(
                $"{location.RetainerName}: {location.Quantity.ToString("N0", Ui.Culture)}{quality} | "
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
        if (plugin.ShopListService.PresetStore.ActivePreset.Mode == ShopListMode.FillOrderOverMultipleRuns &&
            testedDisposition is "ready" or "partial" or "fulfilled")
            return plugin.ShopListService.IsStandaloneOrderComplete ? "Order complete. Use Start new order for another order." : string.Empty;
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
        if (warnings.Count == 0 || !WindowLayout.TreeNode(Ui.T("{0} ({1})", Ui.Display(label), warnings.Count) + $"###{label} ({warnings.Count})"))
            return;
        foreach (var warning in warnings)
            MaterialText.BulletText(Ui.Display(warning));
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
